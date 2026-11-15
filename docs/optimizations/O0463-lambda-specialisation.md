# O0463 — Specialise functions for the lambdas passed to them

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** `O0463LambdaSpecialisation` is the first of the passes over the legalized module ([#1360](https://github.com/Hawkynt/Polypiler/issues/1360)), ahead of [O0006](O0006-inlining.md) in every round but the last. Switched off with `--disable-opt O0463`; off at `--opt none`. The general escape analysis ([#82](https://github.com/Hawkynt/Polypiler/issues/82)) and the effect summaries of the functions it lifts ([#1361](https://github.com/Hawkynt/Polypiler/issues/1361)) build on it.

## What it does

[Sequence fusion](../reference/sequence-operations.md) makes one loop of a LINQ chain or a JavaScript
`map`/`filter`/`reduce` chain, but the loop calls every lambda through a delegate parameter, and
inlining only reaches direct calls. The pass makes those calls direct, for every higher-order function
alike - the fused loops, the program's own functions taking a `Func<…>`, an `Action<…>` or a
JavaScript callback - without knowing anything about LINQ:

1. **What stays local.** A parameter of a function type stays local when every use of it is a call
   of it, or an argument to a parameter of another function that stays local in turn. It escapes when
   it is stored, returned, assigned, compared, captured by a lambda, passed anywhere else, when its
   function returns a lazy sequence or yields (the iterators of a stored or returned sequence), and
   when the chain of calls comes back to a function on it. Any doubt means it escapes.
2. **Lifting.** A function value that stays local - a lambda passed to such a parameter, a local
   that holds a lambda for good and is only called or passed on (a local function), a lambda called
   where it stands - becomes a function of the module whose parameters are the lambda's, followed by
   the variables it captures. A captured variable no lambda of the function writes is passed **by
   value**, read where the call evaluates its last argument: what the closure would read when it runs
   during the call. One a lambda writes goes into a **cell** - an array of one element the function,
   its lambdas and the lifted function share - when it is a number or a truth value the function
   declares and every lambda that captures it is lifted; otherwise the lambda stays. A method group
   needs no lifting: it is its function.
3. **Copies.** A call that passes such values gets a copy of its callee in which the parameters
   that received them are gone, every call of one is a direct call of its function with the captured
   values appended, and every call that passes one on is a call of a copy of that callee. Equal
   lambdas share one lifted function, and a callee with the same functions in the same parameters
   shares one copy. A function of the program the copies replaced at every call goes when it is
   private and no method.
4. **Cleaning up.** Inlining (O0006) copies the lifted function into the copy and the copy into the
   caller; [O0003](O0003-sccp.md), [O0001](O0001-constant-folding.md) and
   [O0005](O0005-dead-code-elimination.md) remove what is left. In the next round the pass makes a cell
   that nothing but its element reads any more a variable again.

The functions of a scope are classified by what they capture (`IrModule.ScopeFunctions`):
**static** when they capture nothing or only what nothing writes - those values became parameters -
and **capturing** when they write a captured variable. A capturing local function that is only
called stays a closure of its function. Where a function of a scope is not inlined, the backends
write it in the closest form their target has:

| Class | C# 8 and later (netstandard2.1, netcoreapp3.1, net8.0, net10.0) | C# 7.3 (net4.8, netstandard2.0) | C# 2 to 6 (net2.0, net3.5, net4.6) | JavaScript |
| --- | --- | --- | --- | --- |
| static, called only by its scope | `static` local function | local function, where no name of the two functions clashes | `private static` method | function of the module |
| static, called from elsewhere too (a copy) | `private static` method | `private static` method | `private static` method | function of the module |
| capturing, held in a local | local function | local function | lambda or anonymous method in a local | nested function declaration at the top of its function, else as before |

## Example

`polypiler translate Ex.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static int F(int[] a) => a.Select(x => x).Where(x => x > 3).Sum();
```

### Before

With `--disable-opt O0463`:

```csharp
public static int F(int[] a) => F_Sum(a, static x => x, static x => x > 3);

private static int F_Sum(int[] source, global::System.Func<int, int> select, global::System.Func<int, bool> where) {
  unchecked {
    int sum = 0;
    int i = 0;
    while (i < source.Length) {
      var x = source[i];
      i = i + 1;
      var y = select(x);
      if (where(y)) {
        sum = checked(sum + y);
      }
    }
    return sum;
  }
}
```

### After

By default; `--explain-opt` prints `O0463 lambda specialisation: 'F' statement 1`, then the inlining,
propagation and dead-code removal that follow:

```csharp
public static int F(int[] a) {
  unchecked {
    int sum = 0;
    int i = 0;
    while (i < a.Length) {
      var x = a[i];
      i = i + 1;
      if (x > 3) {
        sum = checked(sum + x);
      }
    }
    return sum;
  }
}
```

`F([1, 5, 7])` returns 12 and `F([int.MaxValue, 4])` throws `OverflowException`, either way. A lambda
that writes a captured counter, `a.Where(x => { n++; return x > 0; }).Sum()`, ends the same way: `n`
lives in a cell while the copy is a function of its own and is a variable again once it is inlined.

## When it applies

- The callee is a function of the program or one fusion made: not generic, no helper of the library,
  no method of a class, no iterator; its parameter stays local as above.
- The value is a lambda, a method group of a function of the module with the parameter's signature,
  or a local declared with a lambda, never assigned again, captured by no lambda, and only called or
  passed on to parameters that stay local.
- What the lambda captures is passed by value or lives in a cell as above; a lambda in a method of a
  class that captures its receiver stays.
- At most `O0463LambdaSpecialisation.CloneLimit` - 8 - copies of one function, and at most
  `O0463LambdaSpecialisation.GrowthLimit` - 4096 statements and nodes - of all copies together. A call
  over the budget keeps its function values; `--explain-opt` notes it:
  `O0463 lambda specialisation: 'G' passes its function values to 'Apply' as they are: 8 copies of 'Apply' are the most`.

## When it must not fire

- `static Func<int, int> keep; static int H(Func<int, int> f) { keep = f; return f(1); }`: the value is
  stored and may run after the call, when the variables it captures hold other values.
- A parameter returned, captured by a lambda, compared with `null` or passed to a function that does
  any of that: the same.
- A function that returns a lazy sequence or yields: its body runs when the sequence is enumerated,
  later. A sequence that is stored, passed or returned stays an iterator ([#1329](https://github.com/Hawkynt/Polypiler/issues/1329)).
- Recursion through the parameter, `Rec(n - 1, f)`: a copy would need a copy of itself.
- A variable a lambda writes that another lambda captures which stays a closure, or that is a
  parameter, a variable of a loop, a reference or a struct: a cell would split it from what the
  closure sees.

## Switching it off

`--disable-opt O0463`; on at `--opt default`, off at `--opt none`. Without it the lambdas stay
delegates as fusion made them.

## Interactions with other passes

First of the passes over the legalized module; like inlining it looks at every function, and it sits
out the last round, which only cleans up. What it makes - the lifted functions and the copies - is
looked at by the passes after it and inlined by [O0006](O0006-inlining.md) as the helpers are, and
[O0005](O0005-dead-code-elimination.md) removes the ones nothing calls any more. The vectorization of
the loops it leaves ([O0020](O0020-loop-vectorization.md), [#510](https://github.com/Hawkynt/Polypiler/issues/510))
and their parallel reduction ([O0027](O0027-parallel-reduction.md), [#959](https://github.com/Hawkynt/Polypiler/issues/959))
come later; a `checked` sum split across lanes or threads traps at another element, and needs a
proof that it cannot overflow or a wider accumulator with one check at the end.

## How it is proven

`LambdaSpecialisationTests` runs the rows of the test design on C# snippets, each compiled by Roslyn
as the oracle and run in the interpreter before and after the passes: the six cases of escape and a
comparison with `null` and an iterator, the captures by value and in cells, the budget at 1, at the
limit and one over, the growth on and one over, a checked overflow, a division by zero and an index
out of range inside a predicate with the calls before it counted, the switches, the local functions
that stay closures, and the optimised modules of the LINQ fixtures call by call against .NET and of
the JavaScript fixtures against the unoptimised ones. The acceptance scenarios of
`tests/features/optimize/opt-lambda-specialisation.feature` check the forms of the table per profile,
compile the output for every C# profile and run it in `node` at every level, and the LINQ acceptance
tests run every fixture call in every profile of both backends with the pass on.
