# O0259 — Recover slice parameters and adapter functions from pointer-plus-length interop shapes

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#949](https://github.com/Hawkynt/Polypiler/issues/949). Once implemented it is switched off with `--disable-opt O0259`.

## What it does

PRD §33.1 O4 names "interop adapters" among the shapes to recognize in reverse. The commonest adapter shape
is the C buffer convention: a pointer and a separate length, plus thin wrapper functions that only repackage
arguments for a callee. Left as it is, C# output takes `byte*` and `nuint`, Rust takes raw pointers.
Recovered as one bounded slice, the same function can be exposed as `ReadOnlySpan<byte>` or `&[u8]`, and the
adaptation layer of [#769](https://github.com/Hawkynt/Polypiler/issues/769) can regenerate each target's idiomatic overloads.

## Example

### Before

```
func @sum(%p: ptr<u8>, %n: uptr) -> u32 {
entry: Br head
head:  %i = Phi.uptr [entry: 0], [body: %j]
       %a = Phi.u32 [entry: 0], [body: %b]
       Br (ICmp.ult.uptr %i, %n), body, done
body:  %v = Load.u8[unchecked] (PtrAdd.ptr<u8>[unchecked] %p, %i)
       %b = Add.u32[wrap] %a, (ZeroExtend.u32 %v)
       %j = Add.uptr[wrap] %i, 1
       Br head
done:  Ret.u32 %a }
```

### After

```
func @sum(%s: slice<u8>) -> u32 {
entry: %n = SliceLen.uptr %s
       Br head
head:  %i = Phi.uptr [entry: 0], [body: %j]
       %a = Phi.u32 [entry: 0], [body: %b]
       Br (ICmp.ult.uptr %i, %n), body, done
body:  %v = Load.u8[bounds] (SliceElem.ptr<u8> %s, %i)      ; check provably redundant: 0 <= i < n
       %b = Add.u32[wrap] %a, (ZeroExtend.u32 %v)
       %j = Add.uptr[wrap] %i, 1
       Br head
done:  Ret.u32 %a }
```

## When it applies

(1) Range facts prove every index used with p lies in [0, n) at its access
([#94](https://github.com/Hawkynt/Polypiler/issues/94), loop-inductive where needed). (2) p does not escape and is not used in pointer comparisons
or differences other than with pointers derived from itself ([#82](https://github.com/Hawkynt/Polypiler/issues/82)). (3) n is not written and is
the same value at entry and at every access. (4) At every call site in the module, provenance facts show
the pointer argument addresses a region of at least n elements from that position ([#93](https://github.com/Hawkynt/Polypiler/issues/93)); a
site without the fact blocks the recovery of that function. (5) An adapter has no effect of its own and
passes every converted value to exactly one call.

## How it is proven

`validated-rule+differential` with the listed fact obligations (reported as
`structural+interpreter` plus the fact classes): each candidate is re-lowered with the pointer-plus-length
adapter of [#769](https://github.com/Hawkynt/Polypiler/issues/769) and the result is compared with the original fragment on [#119](https://github.com/Hawkynt/Polypiler/issues/119) over
the rule's input generator; any difference, any `Unknown` fact or a budget expiry leaves the IR unchanged.

## When it must not fire

- A function that reads `p[n]` (a sentinel one past the length): the index is outside [0, n); a slice
  access would trap where the original read a valid byte.
- A caller passing `(buf + 4, len)` where `len` is the length of the whole buffer: the pair does not
  describe a region; the original over-reads, a slice would be constructed out of bounds.
- A function that stores `p` in a global for later use: the slice's lifetime ends with the call.
- A wrapper that logs before forwarding: it has an effect of its own and is not an adapter.

## Switching it off

`--disable-opt O0259`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt
none`; never required — with it disabled the low-level form is translated as it is.

## Interactions with other passes

runs in phase `recover` after range and provenance facts are computed and after loop
recovery; enables bounds-check elimination proofs and the API-shape generation of [#769](https://github.com/Hawkynt/Polypiler/issues/769).
