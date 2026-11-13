# O0010 — Elide aggregate and buffer copies whose source stays unchanged

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#503](https://github.com/Hawkynt/Polypiler/issues/503). Once implemented it is switched off with `--disable-opt O0010`.

## What it does

Value-semantic languages copy: a C# struct passed by value, a PowerBASIC UDT assigned to a temporary, a
JavaScript `slice()` taken only to iterate. When neither the copy nor the original is modified while both
are alive, the copy is pure cost — 64 KiB of it per call on some fixtures. PRD §4.8 (copy elision).

## Example

### Before

```
%t = Alloc.u8 16
Memory.Copy %t, %src, 16
%x = Load.u8 %t, 3
%y = Load.u8 %t, 7
```

### After

```
%x = Load.u8 %src, 3
%y = Load.u8 %src, 7
```

## When it applies

For `Memory.Copy dst, src, n` with constant or symbolic `n`: (1) `dst` is an allocation that does not
escape ([#82](https://github.com/Hawkynt/Polypiler/issues/82) escape fact) and whose only writes are this copy; (2) every read of `dst` lies within
`[0, n)` and is dominated by the copy; (3) on every path from the copy to each such read there is no write
that may alias `src[0, n)` — unknown calls and `Memory.Unknown` writes count as such writes
([#114](https://github.com/Hawkynt/Polypiler/issues/114)); (4) `src` is live (not freed, not out of scope) at every redirected read; (5) the copy is
a plain bitwise copy of [#121](https://github.com/Hawkynt/Polypiler/issues/121)/[#110](https://github.com/Hawkynt/Polypiler/issues/110), not a call. For forwarding, additionally `t` has no
reader other than the second copy and `a` is unmodified between the two copies. Identity of `dst` must be
unobservable: no pointer comparison or address-to-integer conversion on it.

## How it is proven

`alias-facts+effect-facts`: under (1)–(4) `dst[i] = src[i]` holds at every redirected read. No solver, no timeout path.

## When it must not fire

- `t = copy(a); a[3] = 9; return t[3]` — the copy exists precisely to keep the old value; eliding it returns 9.
- `f(a, a)` where the callee copies its first parameter and writes through the second: the two parameters alias.
- An unknown call between copy and read may write `src` (PRD §17.5).
- `src` is a stack buffer of a callee frame or is freed before the last read of the copy.
- The copy's address is compared with the source's address (`t == a`): after elision the comparison changes.

## Switching it off

`--disable-opt O0010`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#160](https://github.com/Hawkynt/Polypiler/issues/160) and alias analysis; enables [#500](https://github.com/Hawkynt/Polypiler/issues/500), [#504](https://github.com/Hawkynt/Polypiler/issues/504) and [#507](https://github.com/Hawkynt/Polypiler/issues/507).
