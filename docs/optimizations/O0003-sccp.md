# O0003 — Sparse conditional constant propagation

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** `O0003ConstantPropagation` runs among the passes over the legalized module ([#1353](https://github.com/Hawkynt/Polypiler/issues/1353)), on the structured IR rather than on SSA, over the library functions and every function a helper was inlined into. The sparse, conditional form on SSA - where an edge that cannot be taken contributes nothing - is its full target state. Switched off with `--disable-opt O0003`.

## What it does

A read of a variable that holds a known constant on every way to it becomes the constant. The knowledge
flows forward through a function: a declaration or an assignment of a constant - or of an expression of
constants alone, whose value the interpreter computes - makes the variable known, any other value
unknown. After a choice, the variables both branches agree on stay known, a branch that never runs to
its end does not count; a variable a loop, a switch, a try or a using writes anywhere is unknown in it and
after it. Three clean-ups make what an inlined helper leaves one expression again:

- a variable declared as a copy of another, where neither is written again, is that other one;
- a declaration whose next mention of the variable is an assignment that reads it at most once takes
  the value, the declared one substituted where it is read;
- a variable read once in the whole function, in the statement right after its declaration, whose value
  has no effect and cannot trap, becomes that value there.

## Example

`ushort.RotateLeft(x, 3)` for `net2.0` after inlining ([O0006](O0006-inlining.md)).

### Before

With `--disable-opt O0003`:

```csharp
uint rotateLeftNarrow;
uint value = (uint)x;
int count = 3;
int bits = 16;
uint mask = (1u << bits) - 1u;
value = value & mask;
count = count & (bits - 1);
rotateLeftNarrow = ((value << count) | (value >> (bits - count))) & mask;
return (ushort)rotateLeftNarrow;
```

### After

```csharp
uint value = (uint)x;
return (ushort)((value << 3) | (value >> 13));
```

(the masks are folded by [O0001](O0001-constant-folding.md), the unread variables removed by
[O0005](O0005-dead-code-elimination.md)).

## When it applies

- The variable is a local or a parameter of the function, and no lambda names it.
- What it holds is a constant on every way to the read, as the rules above find it.
- The variable a step changes (`i++`) is its place, not a read, and is not replaced.

## When it must not fire

- A variable a lambda reads or writes: the lambda may run between any two statements.
- A variable a loop writes, in the loop: its value differs between passes.
- The forward substitution of a value that may trap (`a / b`, an array element) or has an effect: it
  would move the trap or the effect.

## Switching it off

`--disable-opt O0003`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

After [O0006](O0006-inlining.md), before [O0001](O0001-constant-folding.md); the pipeline repeats them
until nothing changes.

## How it is proven

`IrPassTests` checks straight code, the meet after a choice, a loop that writes the variable, the copy and
the forward substitution and a variable a lambda names, each against the interpreter;
`HelperEquivalenceTests` checks every helper after all passes against its intrinsic.
