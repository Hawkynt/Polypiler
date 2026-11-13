# O0014 — Remove allocations that never escape and replace small ones by scalars

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#500](https://github.com/Hawkynt/Polypiler/issues/500). Once implemented it is switched off with `--disable-opt O0014`.

## What it does

Tuples returned as two-element arrays, a 4-byte scratch buffer for an endian conversion, a `[a, b]` pair
built only to be destructured: dynamic languages allocate for things a typed target keeps in registers.
Removing the allocation removes a heap or runtime-helper dependency — on BASIC and 8-bit targets the
difference between having a string/array heap and not. PRD §4.8 (redundant allocation removal).

## Example

### Before

```
%t = Alloc.u8[zeroed] 4
Store.u8 %t, 0, %b0
Store.u8 %t, 1, %b1
%x = Load.u8 %t, 0
%y = Load.u8 %t, 1
%z = Load.u8 %t, 3
```

### After

```
%x = %b0
%y = %b1
%z = 0
```

## When it applies

(1) The allocation does not escape: it is not returned, stored into memory that outlives it, passed to a call
that may retain or inspect it, or converted to an integer ([#82](https://github.com/Hawkynt/Polypiler/issues/82) escape fact; `Unknown` = escapes).
(2) Its element count is a literal `n` with `0 ≤ n ≤ 64` for scalar replacement; for plain removal the count
must be proven non-negative and within the profile's allocation limit ([#94](https://github.com/Hawkynt/Polypiler/issues/94)), or the profile must
declare allocation failure unobservable — otherwise the size check stays as an explicit trap check.
(3) Every access is a `Load`/`Store` of the element type ([#121](https://github.com/Hawkynt/Polypiler/issues/121), fields per [#110](https://github.com/Hawkynt/Polypiler/issues/110)) with a
literal index in `[0, n)`; a variable index, a reinterpreting access, `Memory.Copy` or an out-of-range
literal blocks scalar replacement. (4) A load not dominated by a store of that element needs the `[zeroed]`
contract. (5) Identity is unobservable (no pointer comparison). (6) [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts the replacement —
on register-starved profiles 64 live scalars may cost more than a buffer.

## How it is proven

`alias-facts+structural`: a private memory cell with only direct, typed, in-range accesses is equivalent to an SSA variable (the mem2reg argument). No solver, no timeout path.

## When it must not fire

- The temporary is returned or stored into a global: the caller reads it later.
- `t[i]` with variable `i`: which scalar it names is unknown; `i = 4` on a 4-element array must still trap.
- A literal index 4 on a 4-element array: the source traps (or the profile defines the behaviour); it must not become an invented fifth variable.
- `Alloc.u8 %n` with `n = −1` or `n = 2^31`: the source fails; deleting the allocation deletes the failure.
- An uninitialized (not `[zeroed]`) read: replacing it by 0 invents a value.

## Switching it off

`--disable-opt O0014`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#159](https://github.com/Hawkynt/Polypiler/issues/159), [#503](https://github.com/Hawkynt/Polypiler/issues/503), [#504](https://github.com/Hawkynt/Polypiler/issues/504) and [#507](https://github.com/Hawkynt/Polypiler/issues/507); enables [#158](https://github.com/Hawkynt/Polypiler/issues/158), [#163](https://github.com/Hawkynt/Polypiler/issues/163) and recognition rules on the now-visible values (endian pack/unpack).
