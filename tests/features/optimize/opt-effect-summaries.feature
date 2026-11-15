@EndToEnd @Layer.core
Feature: Functions confined to a scope are classified and optimised by their effects

  Every function has an effect summary (#1361): what it reads and writes outside its own variables,
  whether it allocates, may trap and may run forever. A pure call with constant arguments is evaluated
  at compile time (O0002), identical pure and read-only calls are merged (O0464), and a call of a
  function that writes is never merged, evaluated or removed. An anonymous method is the lambda it
  spells. The sources stand in a class with the using System.

  Scenario: An anonymous delegate translates like a lambda
    Given "public static int F(int[] a) { Func<int,int> sq = delegate (int x) { return x * x; }; int s = 0; foreach (var v in a) s += sq(v); return s; }"
    When it is translated to every profile of both backends
    Then F([1, 2, 3]) returns 14 everywhere
    And for net2.0 the output writes an anonymous method, and for net10.0 no delegate remains

  Scenario: A pure call with constant arguments is folded
    Given "static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2); public static int G() => Fib(10);"
    When it is translated with the default optimisation level
    Then G returns the constant 55 with no call
    And "Fib(40)" (over the step budget) stays a call

  Scenario: Identical read-only calls are merged
    Given "public static int H(int[] a, int k) { Func<int> lim = () => k * 2; int s = 0; for (int i = 0; i < a.Length; i++) if (a[i] < lim() && a[i] > -lim()) s++; return s; }"
    When it is translated
    Then each loop pass computes lim once
    And H([1, 5, -3], 2) returns 2

  Scenario: A writing lambda is never merged or deleted
    Given "public static int W() { int n = 0; Func<int> inc = () => ++n; inc(); inc(); return n; }"
    When it is translated to every profile
    Then W() returns 2 and both calls remain
