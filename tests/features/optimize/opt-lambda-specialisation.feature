@EndToEnd @Layer.core
Feature: Lambdas that stay local are bound and optimised away

  A function value that never leaves the function it is made in is lifted into a function of the
  module, and the function it is passed to is copied with it bound (O0463, #1360); inlining and the
  passes after it then make one loop of a LINQ chain or a callback, with no delegate. A function of a
  scope that is not inlined is written in the closest form its target has. The sources stand in a
  class with the usings System and System.Linq.

  Scenario: An identity Select and a predicate disappear into one loop
    Given the C# source "public static int F(int[] a) => a.Select(x => x).Where(x => x > 3).Sum();"
    When it is translated with "--from csharp --to csharp --profile net10.0"
    Then the output contains no delegate type, no lambda and no call inside the loop
    And the loop body compares the element with 3 and adds it with overflow checking
    And F([1, 5, 7]) returns 12, and F([int.MaxValue, 4]) throws OverflowException
    When it is translated with "--disable-opt O0463"
    Then the output passes the lambdas as delegates as before

  Scenario: The same pipeline in JavaScript at ES5 and ES2022
    Given the C# source "public static int F(int[] a) => a.Select(x => x).Where(x => x > 3).Sum();"
    When the method is translated with "--to javascript" at "es5" and at "es2022"
    Then the output contains no function expression inside F
    And F([1, 5, 7]) returns 12 in node

  Scenario: A user higher-order function is specialised
    Given "static int Apply(int[] a, Func<int,int> f) { int s = 0; foreach (var x in a) s += f(x); return s; }"
    And "public static int G(int[] a, int k) => Apply(a, x => x * k);"
    When it is translated
    Then G contains a loop multiplying by k and no call of Apply
    And G([1, 2, 3], 2) returns 12

  Scenario: An escaping lambda is left alone
    Given "static Func<int,int> keep; static int H(Func<int,int> f) { keep = f; return f(1); }"
    When "H(x => x + 1)" is translated
    Then H is not specialised and the lambda is passed as a delegate
    And R() returns 2

  Scenario: A written capture keeps its order
    Given "public static int C(int[] a) { int n = 0; var s = a.Where(x => { n++; return x > 0; }).Sum(); return s * 100 + n; }"
    When it is translated to every profile of both backends
    Then C([1, -2, 3]) returns 403 everywhere

  Scenario: A capture-free local function called from several places takes the closest native form
    Given "public static int H(int a, int b) { Func<int, int> mix = x => { int y = x * x + 3 * x; y = y ^ (y >> 2); y = y * 7 + 1; y = y - (x << 1); return y - x; }; return mix(a) + mix(b) + mix(a + b); }"
    When it is translated to every profile of both backends
    Then mix is a static local function from C# 8, a local function in C# 7.3, a private static method below that and a function of the module in JavaScript
    And H(3, 4) returns what .NET returns everywhere

  Scenario: A local function that writes what it captures stays capturing
    Given "public static int W() { int n = 0; Action inc = () => { n++; }; inc(); inc(); return n; }"
    When it is translated to every profile of both backends
    Then inc stays a closure: a local function from C# 7.3, a lambda in a local below that and a nested function declaration in JavaScript
    And W() returns 2 everywhere
