# O0023 — Unroll counted loops under the cost model

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** `O0023Unrolling` runs among the passes over the legalized module ([#1353](https://github.com/Hawkynt/Polypiler/issues/1353)), over the library functions and every function a helper was inlined into, and unrolls a short counted loop fully. Partial unrolling by a factor, and the cost model of PRD §4.8, stay its target state. Switched off with `--disable-opt O0023`.

## What it does

A loop that counts a variable from a constant by a constant step while it compares with a constant -
`for (int i = c; i < n; i += k)` as the frontends make it, a `while` whose body ends with the step - and
runs at most `O0023Unrolling.Limit` times (8) becomes that many copies of its body, each a block of its
own whose variables are new ones, the step after each. A loop that does not run at all goes. Constant
propagation ([O0003](O0003-sccp.md)) then gives every copy its index, folding and dead-code removal clean
up.

## Example

`BinaryPrimitives.ReadUInt64BigEndian(a)` for `net2.0`, after the helper `Load64` was inlined.

### Before

With `--disable-opt O0023`:

```csharp
ulong value = 0;
{
  int offset = 0;
  while (offset < 8) {
    value = (value << 8) | (ulong)a[offset];
    offset = offset + 1;
  }
}
return value;
```

### After

```csharp
ulong value = 0;
value = (ulong)a[0];
value = (value << 8) | (ulong)a[1];
value = (value << 8) | (ulong)a[2];
value = (value << 8) | (ulong)a[3];
value = (value << 8) | (ulong)a[4];
value = (value << 8) | (ulong)a[5];
value = (value << 8) | (ulong)a[6];
value = (value << 8) | (ulong)a[7];
return value;
```

## When it applies

- The statement before the loop declares or assigns the counter a constant; the condition compares the
  counter with a constant (`<`, `<=`, `>`, `>=`, `!=`); every statement of the step adds or subtracts a
  constant or steps the counter, as its type wraps (a checked step that overflows keeps the loop).
- Nothing in the body writes the counter, breaks or continues, and no lambda names a variable of it.
- The loop runs 0 to 8 times, and all copies together hold at most `O0023Unrolling.Budget` statements and
  expression nodes (512).

## When it must not fire

- A bound that is no constant, or a counter that may hold any value at the loop.
- Nine passes or more: the copies would cost more than the loop.
- A `break`, a `continue`, or a counter the body writes: the number of passes is not the counted one.
- A loop whose iterations bind a variable each (`PerIteration`): a lambda may hold one of them.

## Switching it off

`--disable-opt O0023`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Last of the passes over the legalized module; the next round propagates, folds and removes in the copies.

## How it is proven

`IrPassTests` unrolls trip counts 0, 1, 8 and one from 5 to 3, keeps 9 and a bound that is no constant
and a loop that breaks, counts down and by a step, each against the interpreter; `HelperEquivalenceTests`
runs the endian loads and stores, whose loops it unrolls, against their intrinsics.
