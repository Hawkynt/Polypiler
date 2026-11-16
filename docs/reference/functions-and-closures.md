# Functions and closures

Function values in the IR: lambdas, anonymous methods, local functions and nested functions of the
source, functions and methods taken as values, and the calls of them - with closures that capture
*variables*, never their values. The IR states the semantics once (`IrLambda` in
`Polypiler.Core.IR/Functions`), the interpreter enforces them, and both backends reproduce them in
every target profile (#1326).
Related: [repository README](../../README.md) · [classes](classes.md) · [generics](generics.md) ·
[IR and analysis](../architecture/IR.md).

## The IR

- **Function types.** `IrTypeKind.Function` with an `IrFunctionType(Parameters, Result)` - the result
  may be `void`. Function types are structural: a C# `Func<int, int>` and a delegate type
  `delegate int Op(int a)` of the source are one type, `fn(i32) -> i32`. A function value is a
  reference and may be `null`; it is storable in locals, parameters, results, array elements and
  fields, never as a view.
- **Values.** `IrLambda(Type, Captures, Parameters, Locals, Body)` makes a closure; `IrFunctionRef`
  is a function of the module as a value (a method group of a static method, a top-level JavaScript
  function named as a value); `IrBoundMethod(Receiver, Method)` is a method of a class bound to its
  receiver, which is evaluated once, when the value is made, and traps there when it is `null`.
- **Calls.** `IrInvoke(Callee, Arguments)` evaluates the callee, then the arguments left to right,
  then checks the callee: a `null` function value traps with `IrTrap.NullReference` after the
  arguments, as C# (`Invoke` throws) and JavaScript (`TypeError` "f is not a function") both do.
- **Captures.** A closure captures variables, never values: a captured local is one storage location
  that the function declaring it and every closure capturing it share. A write after the closure was
  made is seen by it (`Late(2)` is 10), a write by it is seen by everybody (`Counter()` is 123,
  `Interleaved()` 21).
- **Variables of a pass.** A local declared in a block is a new variable each time its declaration
  runs - in a loop body once per pass, which is what a C# `foreach` variable (C# 5) and a variable
  declared in a JavaScript loop body are. `IrFor.PerIteration` lists the loop variables, declared
  before the loop, of which every pass has a variable of its own: before the condition is tested the
  first time and again after every pass before the step, each is replaced by a new variable that
  starts with the value the old one ended with - ECMA-262 §14.7.4.4 CreatePerIterationEnvironment,
  which a JavaScript `for (let …)` has. A C# `for` and a JavaScript `for (var …)` list none: every
  pass shares the variable (three closures of `i` in a three-pass loop give 9, not 3).
- **Local functions.** A local function is a local of a function type that holds one lambda for good
  (`IrClosures.Held`): declared with it, or declared without a value and assigned the lambda once in
  its block. A recursive one captures its own variable, which is therefore declared first.

## Nodes and verifier rules

| Node | Meaning | Verifier |
| --- | --- | --- |
| `IrLambda(Type, Captures, Parameters, Locals, Body)` | a new closure | the type is the signature of the parameters and the result; each capture is in scope where the lambda stands and none of its own; the body sees its captures, parameters and locals only and is checked as a function of its own; no `break` or `continue` leaves it |
| `IrFunctionRef(Type, Function)` | a function of the module as a value | the function exists, is no member of a class and has exactly the signature |
| `IrBoundMethod(Type, Receiver, Method)` | a method bound to its receiver | a method of a class of the module, no accessor; a receiver exactly for an instance method, of its class; the kind one the method can be called with; the signature after the receiver |
| `IrInvoke(Type, Callee, Arguments)` | a call of a function value | the callee has a function type; one argument per parameter, of its type; the result its result |
| `IrNull` of a function type, `IrReferenceEquals` | no function, `f == null` | as for references |
| `IrFor.PerIteration` | the variables of every pass | each in scope at the loop, each once |

A signature takes no view and returns none: a closure may outlive the stack frame a view lives in.

## C# frontend

| Construct | IR | Status |
| --- | --- | --- |
| lambda `x => …`, `(a, b) => { … }`, anonymous method `delegate (int x) { … }`, `static` lambda | `IrLambda` (a static one has no captures, which the compiler checked) | supported |
| local function, also recursive and `static`, called before it stands | a local declared where its block starts, assigned its `IrLambda` once every variable of the block it captures is declared - before any call C# admits | supported |
| method group of a static method of the class / of a method of a class of the file | `IrFunctionRef` / `IrBoundMethod` | supported |
| `System.Func<…>`, `System.Action<…>` of every arity, a non-generic delegate type of the file | the function type of the signature | supported |
| `f(x)`, `f.Invoke(x)`, a call of a local function | `IrInvoke` | supported |
| `null`, `f == null`, `f != null`, `f is null` | `IrNull`, `IrReferenceEquals` | supported |
| a generic method that takes a `Func<T, …>` | one instantiation per type argument, the function type closed ([generics](generics.md)) | supported |
| `f += g`, `f -= g`, `f + g` | - | `PLY1002`: "a multicast delegate" |
| `f == g` of two delegates | - | `PLY1002`: "delegate equality other than with null" |
| `ref`/`out`/`in` and `params` parameters of delegates and lambdas, `async` lambdas and local functions, generic local functions and generic delegate types, method groups of library methods | - | `PLY1002` naming it |

An expression tree (`Expression<Func<…>>`) does not compile against the core library the frontend
binds to (`PLY1000`); `Delegate`, `DynamicInvoke` and events are refused by their types and names.

## JavaScript frontend

| Construct | IR | Status |
| --- | --- | --- |
| arrow function (expression and block body), function expression | `IrLambda` | supported |
| function declaration inside a function | hoisted (ECMA-262 §10.2.11): a variable declared where its block starts, assigned its `IrLambda` once every variable of the block it captures is declared | supported |
| a closure returned from a function, a function value in a `const`/`let`, a parameter or an array | the function type the calls through it give it | supported |
| function values in an Array that grows (`fs.push(() => i)`, then `for (const f of fs) f()`) | a list of the function type (#1396) | supported |
| a function of the file named as a value | `IrFunctionRef` | supported |
| `f(x)`, `fs[0]()`, `compose(f, g)(x)` | `IrInvoke` | supported |
| `for (let …)`, `for (const …)` whose variables a closure captures / `for (var …)` | `IrFor` with `PerIteration` / one variable | supported |
| a nested function hiding a variable of the function around it; `this` in an arrow inside a method | the scoping of JavaScript | supported |
| `@param {function(byte[], int):T} f` with `@template T`, `function(int)`, `function():void` | the function type the calls through the values passed give it (#1408) | supported |
| a field holding a function value, `this._f = f; this._f(x)` - a field hides a method of its key | a field of a function type, `IrInvoke` of `IrFieldLoad` | supported |
| `new A(64, b => this.p(b))`: an arrow capturing `this` stored by another object | `IrLambda` capturing the receiver | supported |
| a call through a value that passes more arguments than the function has parameters | the lambda has a parameter for each, which it ignores | supported |
| `O.f` of an object of functions named as a value | `IrFunctionRef` | supported |
| `v => this.total += v`: a concise body assigning a variable or a field | the store, then the place read again as the result | supported |
| `return f()` of a call that returns nothing | the call, then `return` | supported |
| a JSDoc function type of a spelling that is no type; a stated function no call of the file passes | - | `PLY1101` |
| a stated function that a call passes something else to | - | `PLY1105` |
| a function of the file as a value that takes fewer parameters than its calls pass | - | `PLY1102` naming it |
| `arguments`, `this` in a function expression, `new f()`, `f.call`/`apply`/`bind` | - | `PLY1102` naming it |
| default and rest parameters of a nested function, a nested generator | - | `PLY1102` naming it |
| a variable that holds a function and a number | - | `PLY1105` |

The type of a function value is the set of functions it may be (`FunctionShape`): two places one
value reaches share it, the arguments of a call through it reach the parameters of every member, and
the members are inferred jointly - one signature, a parameter of a nested function a double unless
the program proves an integer (#1137).

A JSDoc function type says that a parameter is a function (#1408), as the Cipher framework's
`BlockAbsorber` states `@param {function(byte[]):void} processBlock`, keeps it in a field and calls it
back: the parameter takes the function values the calls of the file pass - of a method as well, whose
calls only the types find - and its type is the signature of their shape, as for a function value
without JSDoc. The stated parameter and result types are checked to be spellings JSDoc knows, a
`@template` name included, but do not change the signature: what the calls through the value pass and
what the functions return decide it, so a `T` of a template is the one result of every function the
parameter receives (two calls passing functions of two different results are `PLY1105`). The
positions a call passes are the signature's: an arrow of fewer parameters, `(held, pending) => …`
called with three arguments, gets a parameter for the third, which it ignores, as JavaScript does; a
function of the file of fewer parameters is refused as a value there.

## C# backend

| IR | net2.0 (C# 2) | net3.5 (C# 3) | net4.6 (C# 6) | net4.8, netstandard2.0 (C# 7.3) | netstandard2.1, netcoreapp3.1 (C# 8) | net8.0 (C# 12), net10.0 (C# 14) |
| --- | --- | --- | --- | --- | --- | --- |
| function type | `System.Action<T>` for one parameter and no result, else a delegate type the static class declares, named from the signature (`Int32ToInt32`, `ToVoid`), public where a public signature names it, generic over the type parameters of a definition written back | `Func`/`Action` up to four parameters, a declared delegate type above | `Func`/`Action` up to sixteen, a declared delegate type above | as net4.6 | as net4.6 | as net4.6 |
| `IrLambda` | anonymous method `delegate (int x) { … }` | lambda `x => …` | lambda | lambda | lambda | lambda; `static` without captures |
| local function | anonymous method in a local, `= null` first | lambda in a local, `= null` first when recursive | as net3.5 | local function | local function; `static` where it captures nothing but itself | as C# 8 |
| a function value no type is given to (called where it stands, a branch of a conditional, compared with null) | cast to its type | same | same | same | same | same |
| `foreach` element captured | the element is a local of the loop body, a variable of its own every pass in every version | same | same | same | same | same |
| `PerIteration`, the body never writes the variable | `for (; …; step) { int i_1 = i; … }` | same | same | same | same | same |
| `PerIteration`, the body writes it | `for (…) { int i_1 = i; try { … } finally { i = i_1; } }` | same | same | same | same | same |

The rows are capability rows: `anonymous method` (C# 2), `lambda expression` (C# 3), `foreach
iteration variable of its own per pass` (C# 5, recorded: the element is read into a body local), the
existing `local function` (C# 7) and `static local function` (C# 8), `static anonymous function`
(C# 9); `System.Action<T>` (net2.0), `System.Action`, `System.Func<T1…T4, TResult>` and
`System.Action<T1…T4>` (net3.5), `System.Func<T1…T16, TResult>` and `System.Action<T1…T16>` (net4.6,
the first listed profile at or after .NET Framework 4.0). A declared delegate type covers what no row
does, so `PLY1053` is unreachable for every construct of this page.

### Functions of a scope after O0463

At `--opt default` [O0463](../optimizations/O0463-lambda-specialisation.md) lifts a lambda or local
function whose value stays in its function and that writes nothing it captures into a function of the
module (`<function>_<local>`, or `<function>_Lambda` for one passed at a call) - what it reads becomes
parameters - and classifies one that writes a captured variable as capturing, which stays a closure.
What is not inlined afterwards is written as:

| Function of a scope | net2.0, net3.5, net4.6 | net4.8, netstandard2.0 (C# 7.3) | netstandard2.1, netcoreapp3.1, net8.0, net10.0 |
| --- | --- | --- | --- |
| static, called only by its scope | `private static` method | local function at the end of its scope, where no variable of the two shares a name; else `private static` method | `static` local function at the end of its scope |
| static, called by a bound copy too | `private static` method | same | same |
| capturing | the forms of the table above | same | same |

### What a function of a scope does

O0463 also records what each function of a scope does by its effect summary (`IrEffects`,
[#1361](https://github.com/Hawkynt/Polypiler/issues/1361)) - a lifted one that of its function, one
that stays a closure that of its body with what it captures as storage of its scope - and
`--explain-opt` names it: `O0463 lambda specialisation: 'H_lim' of 'H' is static, lifted, pure`,
`'inc' of 'W' is capturing, a closure, writes`. A call of a lifted function the summary finds pure or
read-only is merged with the same call after it ([O0464](../optimizations/O0464-call-merging.md)),
evaluated where its arguments are constants ([O0002](../optimizations/O0002-partial-evaluation.md))
and removed where nothing uses its value ([O0005](../optimizations/O0005-dead-code-elimination.md)); a
call through a function value is the worst case and stays. An anonymous method, `delegate (int x) {
return x * x; }` or `delegate { … }`, is the lambda it spells to every pass.

## JavaScript backend

| IR | es5 (with or without `--typed-arrays`) | es2015, es2017, es2020, es2022 |
| --- | --- | --- |
| `IrLambda` | `function (x) { … }`; `this` of a method as `var $self = this` | arrow function; an expression body for one `return` |
| local function | `var f = function f(…) { … };` | `const f = (…) => …;` |
| local function O0463 classified as capturing, declared at the top of its function | `function f(…) { … }`, a nested declaration | same |
| function of a scope O0463 lifted, capture-free | a function of the module | same |
| `IrFunctionRef` | the function's name | same |
| `IrBoundMethod` | `(function (o) { return function (a1) { return o.m(a1); }; })(receiver)` - never `bind` | `((o) => (a1) => o.m(a1))(receiver)` |
| `IrInvoke` | `f(x)`; a `null` callee throws the engine's `TypeError` after the arguments, which #1323 maps to the trap | same |
| a C# `for` variable captured (`PerIteration` empty) | `var i` | `let i` declared in front of the loop, never `for (let i …)` |
| `PerIteration` captured | the variables of a pass in an environment object made every pass, `$c1 = { i: i };`, which the closures of the pass take as the parameter of the function that makes them; copied back in a `finally` where the body writes them | `for (let i = 0; …)` where the declarations stand right in front of the loop and nothing after names them; else a `let` copy in the body |
| a local of a loop body captured | in the environment object of its pass | `let` in the body |

The rows are capability rows: `=>` (2015, replacement `function`), `let per-iteration binding`
(2015, replacement `environment object`); `PLY1152` is unreachable for every construct of this page.

## Known differences

- A method bound to a `null` receiver traps in the IR when the value is made; the JavaScript closure
  over the receiver throws only when it is called.
- A lambda of a C# 8 or later source may hide a variable of the function around it; the C# backend
  renames the inner one where a C# 7.3 target would refuse the shadowing.
- In ES5 the environments of the loops of a lambda body are variables of the function the lambda
  stands in, so a recursive call of such a lambda that runs a loop of itself again shares them.
- Expression trees, multicast delegates and events are not imported; they wait for the
  event/observer work of #749.
