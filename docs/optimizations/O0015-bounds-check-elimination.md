# O0015 — Remove bounds checks proven redundant and attach a reusable bounds proof

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#502](https://github.com/Hawkynt/Polypiler/issues/502). Once implemented it is switched off with `--disable-opt O0015`.

## What it does

Safe source languages check every index; C targets and assembly check none. Translating C# to C must not
lose the checks that can fail, and translating C to C# must not gain a check per byte where the loop bound
already proves safety. PRD §6.3 makes this the first proof-driven optimization scenario: the optimized IR
"carries a reusable bounds proof", the backend "may emit unchecked pointer/array accesses where legal", and
an unknown call that may shorten the collection invalidates the facts.

## Example

### Before

```
// guard already executed: %g = ICmp.ule.u32 %n, Length(%dst); Br %g, loop, fail      (and %n = Length(%src))
loop:
  %i = Phi.u32 [pre: 0], [loop: %i2]
  %v = Load.u8[bounds] %src, %i
  Store.u8[bounds] %dst, %i, %v
  %i2 = Add.u32[wrap] %i, 1
  %c = ICmp.ult.u32 %i2, %n
  Br %c, loop, exit
```

### After

```
loop:
  %v = Load.u8[inbounds #1] %src, %i        // #1: 0 <= %i < %n = Length(%src)
  Store.u8[inbounds #2] %dst, %i, %v        // #2: 0 <= %i < %n <= Length(%dst)
```

## When it applies

At the access, [#88](https://github.com/Hawkynt/Polypiler/issues/88) must prove `0 ≤ index` and `index < Length(array)` for the index *value as
used* — after any wrapping arithmetic — where `Length(array)` is the length at the access. Length stability:
between the point where the length relation was established and the access there is no instruction that may
change the array's length or replace the array: fixed-length arrays are always stable; resizable collections
are invalidated by any call or store whose effect set ([#114](https://github.com/Hawkynt/Polypiler/issues/114)) may write the collection header,
including unknown calls when the collection has escaped ([#82](https://github.com/Hawkynt/Polypiler/issues/82)). Index arithmetic must be shown not
to wrap: `i + 1 < n` proves `i + 1` in range only if `i + 1` did not overflow. Signed indices need the lower
bound explicitly. An answer of `Unknown` keeps the check.

## How it is proven

`range-facts+smt-bitvector`: each `BoundsProof` lists the facts used (with their defining instructions) and is discharged by [#71](https://github.com/Hawkynt/Polypiler/issues/71) with a 5 s budget; on timeout the check stays (`not applied: proof timeout`).

## When it must not fire

- `for (i = 0; i <= n; i++) a[i]` with `n = Length(a)`: the last iteration is out of range.
- `a[i + 1]` inside `for (i = 0; i < n; i++)`: `i + 1` reaches `n`.
- A call to an unknown function inside the loop may shorten a list that has escaped (PRD §6.3).
- `u32` index arithmetic `a[i * 4]` with `i` up to 2^30: `i * 4` wraps to 0 — in range by accident for some values, out of range for others; the fact must be about the wrapped value.
- A signed index proven `< n` but possibly negative.
- `n ≤ Length(src)` proven, but the access is to `dst`.

## Switching it off

`--disable-opt O0015`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#508](https://github.com/Hawkynt/Polypiler/issues/508) (hoisted lengths), [#160](https://github.com/Hawkynt/Polypiler/issues/160) (which calls can resize) and range analysis; enables [#510](https://github.com/Hawkynt/Polypiler/issues/510), [#511](https://github.com/Hawkynt/Polypiler/issues/511) and [#513](https://github.com/Hawkynt/Polypiler/issues/513), which require trap-free loop bodies, and unchecked emission in the backends.
