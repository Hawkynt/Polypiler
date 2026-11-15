# O0002 — Evaluate pure calls with constant arguments at compile time

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** `O0002CallEvaluation` runs among the passes over the legalized module ([#1361](https://github.com/Hawkynt/Polypiler/issues/1361)), after [O0001](O0001-constant-folding.md), and looks at every function. It evaluates calls with an integer or truth-value result; an array result as a read-only table, floating point by the exact-folding rules of [#154](https://github.com/Hawkynt/Polypiler/issues/154) and a memory budget stay with [#162](https://github.com/Hawkynt/Polypiler/issues/162), its target state. Switched off with `--disable-opt O0002`.

## What it does

Algorithms carry code that only computes constants: `factorial(12)`, a key schedule for a fixed key,
a small recursive helper called with a literal. Evaluated at compile time they become data - which
matters most on targets where the loop is slow - and the call, its frame and its recursion are gone.
PRD §4.8 ("constant/partial evaluation"), §8 P1.

A call is replaced by its value when

1. it is a direct call of a function of the module whose effect summary (`IrEffects`, #1361) finds it
   **pure**: it reads nothing that can change - no global, no field, no array element; the tables of
   the module are constant -, writes nothing and makes nothing, through every function it calls;
2. every argument is a constant integer or truth value, and so is the result;
3. the interpreter evaluates it within `O0002CallEvaluation.StepBudget` - 10000 steps, one per
   statement executed and one per call, and calls nested no deeper than 128 - in a module of the
   function and its callees alone;
4. no function it reaches uses an operation of floating point (`Math.Sqrt`, `double.IsNaN`), whose
   result the host may compute otherwise than a target;
5. its value is used: a call that stands as a statement is dead-code removal's
   ([O0005](O0005-dead-code-elimination.md)).

A call that traps stays where it is, so the trap happens at run time where the program has it. A call
over the budget stays, and `--explain-opt` notes it once.

## Example

`polypiler translate Ex.cs --from csharp --to csharp --explain-opt` on

```csharp
static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);
public static int G() => Fib(10);
public static int G40() => Fib(40);
```

### Before

With `--disable-opt O0002`:

```csharp
public static int G() => Fib(10);

public static int G40() => Fib(40);
```

### After

```csharp
public static int G() => 55;

public static int G40() => Fib(40);
```

`--explain-opt` prints `O0002 compile-time evaluation: 'G' statement 1` and, for the call over the
budget, `O0002 compile-time evaluation: 'G40' keeps its call of 'Fib': its evaluation needs more than
10000 steps or nests calls deeper than 128`. `Fib` is recursive, so its summary says it may not end;
the budget, not the summary, bounds the evaluation.

## When it must not fire

- A callee that reads a global: `static int g = 4; static int R(int x) => x + g;` - the value at
  compile time is not the value at run time.
- `static int D(int x) => 10 / x;` called as `D(0)`: the evaluation traps; the trap must happen at run
  time, so the call stays.
- `Fib(40)` and `while (true) {}` in a pure function: the evaluation stops at the budget, and the
  call stays in the program.
- A callee that makes an array or an object: its identity is not a constant.
- An async function, which runs as it is awaited, and a native function: both are the worst case.
- A callee using `Math.Sqrt` or `double.IsNaN`: the host's result may differ from the target's.

## Switching it off

`--disable-opt O0002`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

It runs after [O0003](O0003-sccp.md) and [O0001](O0001-constant-folding.md), which make the arguments
constants, and before [O0464](O0464-call-merging.md), which then has no pure call with constant
arguments left to merge; the value it leaves is folded further in the next round. A function it
changed is looked at by the cleanup passes after it, like one something was inlined into.

## How it is proven

`interpreter+effect-facts`: the result is the callee's own semantics executed on the given arguments;
purity comes from the effect summary. `EffectPassTests` checks `Fib(10)` against .NET and `Fib(40)`
over the budget, the budget one step short of a call's need, on it and one over, a call that traps, the
calls it must keep - one reading a global, one with a variable argument, one whose value nothing uses,
three of floating point - every integer type and truth values across the interpreter's boundary, and
the interpreter's budget itself: its count of steps, its depth and that no catch of the program stops
it.
