# O0006 — Inline direct calls under the cost model

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** `O0006Inlining` runs among the passes over the legalized module, between legalization and the backend ([#1353](https://github.com/Hawkynt/Polypiler/issues/1353)). It inlines the helpers and portable implementations legalization copied in from the [library](../architecture/BACKENDS.md#helpers-one-library-in-the-ir); the inlining of the program's own functions, on SSA with the cost model of PRD §4.8, stays [#159](https://github.com/Hawkynt/Polypiler/issues/159). Switched off with `--disable-opt O0006`.

## What it does

A helper of the library is written for every argument: `RotateLeftNarrow(value, count, bits)` masks, reduces
the count and handles a count of 0. At a call site most of its arguments are often constants. The pass
copies the body of a library function into the function that calls it - the arguments as variables
declared in their order, every return an assignment of a result variable, the call a read of it - so that
constant propagation ([O0003](O0003-sccp.md)), folding ([O0001](O0001-constant-folding.md)) and dead-code
removal ([O0005](O0005-dead-code-elimination.md)) can specialise the copy to its call. A function whose
every call was inlined is removed by O0005; its licence notice stays in the output (`IrModule.Inlined`).

## Example

`polypiler translate Ex.cs --from csharp --to csharp --profile net2.0` on

```csharp
public static ushort F(ushort x) => ushort.RotateLeft(x, 3);
```

### Before

With `--disable-opt O0006`:

```csharp
public static ushort F(ushort x) {
  unchecked {
    return (ushort)RotateLeftNarrow((uint)x, 3, 16);
  }
}

private static uint RotateLeftNarrow(uint value, int count, int bits) {
  unchecked {
    uint mask = (1u << bits) - 1u;
    value = value & mask;
    count = count & (bits - 1);
    return ((value << count) | (value >> (bits - count))) & mask;
  }
}
```

### After

By default; `--explain-opt` prints `O0006 inlining: 'F' statement 1` and the propagation, folding and
dead-code removal that follow:

```csharp
public static ushort F(ushort x) {
  unchecked {
    uint value = (uint)x;
    return (ushort)((value << 3) | (value >> 13));
  }
}
```

The same module for JavaScript at `es5` has no branch and no call left in `F`, and `F(0x8001)` returns
`0x000C` either way.

## When it applies

- The callee is a function legalization copied in from the libraries, other than the caller, and calls
  itself through no chain of calls.
- Its body has at most `O0006Inlining.Budget` statements and expression nodes - 64 - and no lambda.
- Its returns can be made the end of their branches: a return in a choice whose branch always leaves
  takes the statements after the choice into the other branch. A return in a loop, a switch or a try
  cannot be, and keeps the call.
- The call stands in a statement that evaluates it first: everything evaluated before it in the
  statement has no effect and cannot trap, it is not a branch of a choice nor the right side of `&&`,
  `||` or `??`, and the statement is no loop. A call in an argument of a call is taken first where the
  arguments before it may be moved after it.
- At most 256 calls are inlined into one function in one run.

## When it must not fire

- `x / 0 + H(x)`: the division traps before `H` runs; moving `H` in front would change which happens.
- `c ? H(x) : 0` and `H(x)` in the condition of a loop: the call runs only sometimes, or many times.
- A recursive helper, or one whose return stands in a loop: no single way out to assign the result at.
- A function of the program: the prototype inlines only what the libraries put in.
- A body over the budget - the portable `FusedMultiplyAddF`, for instance - stays one function.

## Switching it off

`--disable-opt O0006`; on at `--opt default`, off at `--opt none`. Without it the target pass
[O0500](O0500-target-helper-inlining.md) still inlines a helper whose body is one expression.

## Interactions with other passes

First of the passes over the legalized module, which run round after round until a round changes
nothing (at most 6): [O0003](O0003-sccp.md), [O0001](O0001-constant-folding.md),
[O0005](O0005-dead-code-elimination.md) and [O0023](O0023-loop-unrolling.md) clean up the copy. Only the
library functions and the functions something was inlined into are looked at by the others.

## How it is proven

`IrPassTests` inlines on hand-built IR and compares the interpreter before and after, with the budget
on and one over it, a recursive helper, a return in a loop, a call after an operand that traps, in a
choice and in a loop condition, a call in an argument and a call without a result. `HelperEquivalenceTests`
runs every integer and floating-point helper as legalized and after all passes against its intrinsic -
every value of an 8-bit operand, edges and random values of the wider ones - with the trap kind;
`HelperScenarioTests` checks the scenario above. The differential tests of every family run the
generated code in .NET and `node` against the interpreter.
