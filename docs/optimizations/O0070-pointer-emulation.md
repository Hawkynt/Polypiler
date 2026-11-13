# O0070 — Emulate pointers on pointerless targets with indices, byte buffers and fat pointers

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#496](https://github.com/Hawkynt/Polypiler/issues/496). Once implemented it is switched off with `--disable-opt O0070`.

## What it does

C, C++, Rust and PowerBASIC algorithms walk buffers with pointers; JavaScript, Python, Perl, Java and
classic BASIC have none. PRD §4.7: "Pointerless targets use owned/shared byte buffers, typed arrays or
`(allocationHandle, offset)` fat pointers. Preserve aliasing, pointer identity, byte-vs-element offsets and
lifetime." §6.4 makes it an acceptance scenario (a native byte pointer becomes a Perl buffer-offset view
that preserves overlapping writes, byte order and bounds). Because ranges and provenance are tracked, most
pointers turn out to be an index into one known array — the cheap case — and only the rest need a fat
pointer.

## Example

### Before

```
// uint8_t *p = buf + 4;  *p++ = 1;  p[1] = 2;        %buf: allocation of 8 x u8
%p  = PtrAdd.ptr<u8> %buf, 4
Store.u8 %p, 1
%p2 = PtrAdd.ptr<u8> %p, 1
%q  = PtrAdd.ptr<u8> %p2, 1
Store.u8 %q, 2
```

### After

```
// representation element-index over %buf.array
%p  = 4
Store.u8[bounds] %buf.array, %p, 1
%p2 = Add.u32[wrap] %p, 1
%q  = Add.u32[wrap] %p2, 1
Store.u8[bounds] %buf.array, %q, 2
```

## When it applies

Provenance comes from [#93](https://github.com/Hawkynt/Polypiler/issues/93): every pointer value has a set of possible allocations.
`element-index` requires a singleton set for the whole class, accesses only at the allocation's element
type, and offsets that are multiples of the element size ([#94](https://github.com/Hawkynt/Polypiler/issues/94) congruence); pointer arithmetic in
elements maps to integer arithmetic, in bytes to division by the element size (exact by the congruence).
`byte-buffer` requires a singleton set; the allocation's storage becomes bytes for **all** its users, so
aliasing through differently typed pointers stays visible; multi-byte accesses use the source profile's
byte order via [#258](https://github.com/Hawkynt/Polypiler/issues/258). `fat` is used when the set has several members or is unknown but bounded to
allocations of the module; `handle` is a reference to the allocation's backing array (targets with
references) or an index into a module allocation table (targets without). All pointers into one allocation
share one backing store — an emulated pointer never owns a copy. Equality: same handle and same offset.
Ordering and difference: only when both operands are proven to point into the same allocation
([#82](https://github.com/Hawkynt/Polypiler/issues/82)); otherwise the source behaviour is undefined or implementation-defined and the rule
reports PLY0541. Refused with a diagnostic: provenance `Unknown` or including non-module memory
(PLY0540); a pointer passed to or received from foreign code on a pointerless target (PLY0542); lifetime:
a pointer to a local that outlives the local's frame keeps the local's storage alive as a boxed array
([#123](https://github.com/Hawkynt/Polypiler/issues/123) lifetime fact) or, if the fact is `Unknown`, is refused (PLY0540). Null dereference traps
as in the source profile.

## How it is proven

`alias-facts+range-facts`: with provenance a singleton (or a finite set) and offsets proven element-aligned, a pointer is isomorphic to (allocation, offset); memory is modelled per allocation, so loads and stores commute with the translation. No solver, no timeout path.

## When it must not fire

- Copy-per-pointer: representing `p` and `q = p + 1` as two slices that each own a copy — a write through `p` is no longer seen through `q`.
- `memmove(buf + 1, buf, 4)` on `[1, 2, 3, 4, 5]` must give `[1, 1, 2, 3, 4]`; a forward element-by-element copy gives `[1, 1, 1, 1, 1]`.
- `*(uint32_t *)(bytes + 2) = 0x11223344` on a little-endian source writes `44 33 22 11` at offsets 2–5; an element-index representation over `u32` cannot express the unaligned offset.
- `p < q` with `p` and `q` in different allocations: comparing only the offsets gives an answer the source never promised.
- A pointer computed from an integer (`(char *)0xB8000`): no allocation identity exists.
- Byte offset vs element offset: `(uint8_t *)w + 1` on a `u32` array is byte 1, not element 1.

## Switching it off

`--disable-opt O0070`; `Kind = Required`: the rule runs at every `--opt` level including `none`. With it disabled the legalizer has no alternative for these operations on targets that need it and reports PLY0530.

## Interactions with other passes

runs in phase `legalize` under the driver of [#259](https://github.com/Hawkynt/Polypiler/issues/259), before [#258](https://github.com/Hawkynt/Polypiler/issues/258) and [#263](https://github.com/Hawkynt/Polypiler/issues/263) legalize what it produces; [#502](https://github.com/Hawkynt/Polypiler/issues/502) proofs are carried over to the emitted `[bounds]` accesses and remove their checks.
