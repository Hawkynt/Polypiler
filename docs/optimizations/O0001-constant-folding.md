# O0001 — Fold operations on constants and integer neutral/absorbing elements

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** `O0001ConstantFolding` runs among the passes over the legalized module ([#1353](https://github.com/Hawkynt/Polypiler/issues/1353)), over the library functions and every function a helper was inlined into. Folding everywhere, with floating-point results and the full table of identities, stays its target state. Switched off with `--disable-opt O0001`.

## What it does

An operation whose operands are all constants becomes its value - an integer or a truth value - as the
interpreter computes it; one that traps stays, so the trap happens where the program has it. A choice
on a constant becomes its branch, `true && c` becomes `c`, `false || c` becomes `c`. An operation with a
neutral operand becomes the other operand: `x + 0`, `x - 0`, `x | 0`, `x ^ 0`, shifts by 0, `x * 1` and
`x & ~0` in the width of the type; `x & 0` and `x * 0` become 0 where `x` may go unevaluated. A
conversion back to the type a value had before it was widened is the value; a mask that keeps every bit
a wrapping conversion keeps goes, and so does one that keeps every bit a widening from an unsigned type
can set.

## Example

`ushort.RotateLeft(x, 3)` for `net2.0` after inlining and propagation.

### Before

With `--disable-opt O0001`:

```csharp
uint mask = (1u << 16) - 1u;
uint value = (uint)x & mask;
int count = 3 & (16 - 1);
return (ushort)(((value << count) | (value >> (16 - count))) & mask);
```

### After

```csharp
uint value = (uint)x;
return (ushort)((value << 3) | (value >> 13));
```

## When it applies

- Every operand of a unary, binary, conversion or integer intrinsic node is a constant and the result is
  an integer or a truth value.
- The other forms above, matched node by node from the bottom up.

## When it must not fire

- `1 / 0`, `checked(int.MaxValue + 1)`: the operation traps; folding would remove the trap.
- `f() & 0`: the operand has an effect and must still run.
- A floating-point result: the prototype leaves rounding to the backend's own forms.

## Switching it off

`--disable-opt O0001`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

After [O0003](O0003-sccp.md), which puts the constants in, before [O0005](O0005-dead-code-elimination.md),
which removes the branches a folded condition decides.

## How it is proven

`IrPassTests` folds constants, keeps a division by zero, checks every neutral and absorbing operand, the
choices on constants, the widening undone and the masks a conversion makes redundant;
`HelperEquivalenceTests` checks every helper after all passes against its intrinsic, trap kind included.
