# O0011 — Remove stores that are overwritten or never read

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#504](https://github.com/Hawkynt/Polypiler/issues/504). Once implemented it is switched off with `--disable-opt O0011`.

## What it does

Initialise-then-assign is everywhere: `Dim x = 0` followed by `x = f()`, zero-filled buffers that are fully
written next, struct fields set twice by inlined constructors. Each dead store is a memory write the target
performs for nothing. It is also the optimization PRD §17.5 warns about most: a store into unknown memory,
to MMIO or under an exception handler is *not* dead. PRD §4.8 lists DCE; dead stores are its memory half.

## Example

### Before

```
Store.u32 %p, 0
%v = Call.u32 @pure_compute(%x)        // no effects
Store.u32 %p, %v
```

### After

```
%v = Call.u32 @pure_compute(%x)
Store.u32 %p, %v
```

## When it applies

`S1` (address `A1`, size `n1`) is dead iff (1) its effect is exactly `Memory.Write` to a classified,
non-volatile program-storage region ([#121](https://github.com/Hawkynt/Polypiler/issues/121)); (2) on every path from `S1` to a function exit or to
the killing store there is no instruction that may read any byte of `[A1, A1+n1)`: loads that may alias
([#82](https://github.com/Hawkynt/Polypiler/issues/82) answers `MayAlias` or `Unknown`), calls whose effect set ([#114](https://github.com/Hawkynt/Polypiler/issues/114)) contains a read of
the region or `Memory.Unknown`, and — because handlers and callers can read memory — any instruction that
may raise ([#115](https://github.com/Hawkynt/Polypiler/issues/115)) while the location is visible to a handler or outlives the function; (3) either a
store `S2` with `MustAlias` address and size ≥ `n1` post-dominates `S1` within the function, or the location
is a non-escaping local allocation dead at every exit. Atomic and synchronized stores are never dead.

## How it is proven

`alias-facts+effect-facts`: no execution can observe the value written by `S1`. No solver, no timeout path.

## When it must not fire

- `Store %p, 0; %t = Load %q; Store %p, 1` with `%q` possibly equal to `%p`: the load observes 0.
- `Store.u8[volatile] 0xD020, 0; Store.u8[volatile] 0xD020, 1` — two writes to the C64 border colour register are two visible events.
- `Store %g, 1; Call.void @may_throw(); Store %g, 2` with `%g` global: if the call throws, the caller sees 1.
- A store through a pointer of unknown provenance (it may be the stack slot of a return address or MMIO) — PRD §17.5.
- `Store.u32 %p, 0; Store.u8 %p, 1`: the second store overwrites one of four bytes; the first is not dead.

## Switching it off

`--disable-opt O0011`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#503](https://github.com/Hawkynt/Polypiler/issues/503), [#160](https://github.com/Hawkynt/Polypiler/issues/160) and alias analysis; enables [#156](https://github.com/Hawkynt/Polypiler/issues/156) (the stored values) and [#500](https://github.com/Hawkynt/Polypiler/issues/500).
