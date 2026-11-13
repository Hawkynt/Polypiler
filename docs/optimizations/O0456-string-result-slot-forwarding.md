# O0456 — Let the caller pass the destination of a fixed-capacity string result so the callee writes it in place

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0456`.

## What it does

A function that returns a fixed-capacity string — a C struct with a character array, a Pascal short string,
a PowerBASIC fixed-length string, a Rust `[u8; 40]` — builds it in a local buffer, returns the buffer by
value, and the caller stores it into its destination: the same forty bytes are written three times. If the
caller hands over the destination, the callee writes them once. PB-Compiler's O0295 found that for its
handle-based dynamic strings the hand-over is a move and a hidden destination would be a loss; that half is
`OPT-STR-MOVE`. What remains language-agnostic is the classic result-slot forwarding for results that
*are* the buffer. PRD §4.8, §8 P4.

## Example

### Before

```
func @pad(%s: roslice<u8>) -> array<u8, 8>
entry:
  %tmp = Alloc.ptr<u8>[stack] 8
  Fill.u8 %tmp, 0x20, 8
  %k   = UMin.uptr (SliceLen.uptr %s), 8
  Copy.u8[bounds] %tmp, %s, %k
  %r   = Load.array<u8, 8> %tmp
  Ret.array<u8, 8> %r

caller:
  %v = Call.array<u8, 8> @pad(%src)
  Store.array<u8, 8> %dst, %v
```

### After

```
func @pad.into(%ret: ptr<array<u8, 8>> [borrowed, mutable, exclusive, scoped], %s: roslice<u8>) -> void
entry:
  Fill.u8 %ret, 0x20, 8
  %k   = UMin.uptr (SliceLen.uptr %s), 8
  Copy.u8[bounds] %ret, %s, %k
  Ret.void

caller:
  Call.void @pad.into(%dst, %src)
```

## When it applies

**String model:** a fixed-capacity code-unit array held by value ([#111](https://github.com/Hawkynt/Polypiler/issues/111), [#110](https://github.com/Hawkynt/Polypiler/issues/110)); the caller's
destination is ordinary memory that the program can read at any time, alias through another pointer and
observe half-written. Callee side: (1) the function is internal and all its callers are known; (2) on
every return path the returned value is the complete contents of the same local buffer, which does not
escape ([#82](https://github.com/Hawkynt/Polypiler/issues/82)) and is not read after the `Ret`. Call-site side: (3) the result's only use is one
store of the whole value into `%dst`, with no instruction between call and store. (4) `%dst` is `NoAlias`
with every region the callee may read or write according to its summary — its pointer arguments and
every global in its mod/ref set ([#87](https://github.com/Hawkynt/Polypiler/issues/87), [#160](https://github.com/Hawkynt/Polypiler/issues/160)); `MayAlias` blocks.
(5) The callee cannot leave abnormally after its first write to the buffer: it has no trap, throw or
non-returning call that the first write does not post-dominate — or `%dst` is dead on every path that
follows such an exit ([#115](https://github.com/Hawkynt/Polypiler/issues/115)). (6) `%dst` is not `volatile`, is writable for the whole call, and
no other thread can read it during the call (`NoEscape`/`ArgEscape`, or a single-threaded module).
(7) The loan of `%dst` to the callee is `exclusive` and `scoped` and passes the borrow check of
[#376](https://github.com/Hawkynt/Polypiler/issues/376) at the call. (8) [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts the clone's size against the two removed copies
per qualifying call.

## How it is proven

`alias-facts+effect-facts+structural`: under (4)–(6) nothing can read `%dst` between the callee's first write and its return, so writing the result there early is indistinguishable from writing it after the return; under (2) the buffer and the result are the same bytes. No solver, no timeout path.

## When it must not fire

- `s = Reverse(s)` with the destination also passed as the source: on `"AB"` the callee writes `ret[0] = s[1]` and then reads `s[0]`, which is already `'B'` — the result is `"BB"`, not `"BA"`.
- A callee that fills the buffer with spaces and then traps on a bounds check, inside a caller that catches the trap and prints the destination: the source prints the old contents, the forwarded form eight spaces.
- A callee that reads the global `g` while the call site is `g = f()`: the same aliasing, through a global.
- Two return paths returning different local buffers: neither can be the slot for both.
- A callee that stores the address of its buffer in a global: after forwarding, that global points into the caller's variable.
- A destination that is a memory-mapped display buffer: the intermediate fill becomes visible on screen.
- A result of type `string<utf8>` or `list<u8>`: there is no buffer to forward — passing a destination pointer adds an aliasing surface and removes no copy.

## Switching it off

`--disable-opt O0456`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

targets where an aggregate result is copied: C and C++ output for compilers without guaranteed copy elision of such returns, Pascal and BASIC dialects with fixed-length strings, assembly and machine-code backends. Rust, modern C++ and LLVM-based toolchains perform the return-slot optimization themselves; their profiles price the by-value return at zero copies, the rule does not fire, and the generated source keeps the natural value-returning signature instead of an out-parameter.

## Interactions with other passes

module pass in phase `optimize` after [#159](https://github.com/Hawkynt/Polypiler/issues/159) and [#160](https://github.com/Hawkynt/Polypiler/issues/160) (summaries must be current); followed by [#503](https://github.com/Hawkynt/Polypiler/issues/503) and [#504](https://github.com/Hawkynt/Polypiler/issues/504) inside the clone and by [#156](https://github.com/Hawkynt/Polypiler/issues/156) on the unused original; `OPT-STR-MOVE` covers the owned-handle results this rule refuses.
