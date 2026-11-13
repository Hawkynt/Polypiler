# O0340 — Place a non-escaping heap allocation of bounded size in the frame

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0340`.

## What it does

A scratch buffer that never leaves its function does not need the heap: in the frame it costs no
allocator call, no free and no fragmentation, and the return reclaims it. [#500](https://github.com/Hawkynt/Polypiler/issues/500) turns such
buffers into SSA values only when every index is a literal, and names stack promotion as not planned;
buffers indexed by a loop variable stay on the heap. PB-Compiler promotes bounded temporaries into the
frame (upstream O0287, pass `StringStackPromotion`, for temporaries of up to 64 bytes). PRD §4.8
(allocation objective).

## Example

### Before

```
%t = Alloc.ptr<u8>[heap, zero] 16
...                                   // loads and stores of %t[%i], no escape
Release %t
Ret.u32 %sum
```

### After

```
%t = Alloc.ptr<u8>[stack, zero] 16
...
Ret.u32 %sum
```

## When it applies

(1) `EscapeOf(site)` is `NoEscape` or `ArgEscape` ([#82](https://github.com/Hawkynt/Polypiler/issues/82)): the address is not returned, not stored into memory that outlives the activation, and passed only to callees whose summary does not capture it ([#87](https://github.com/Hawkynt/Polypiler/issues/87)); `GlobalEscape` and `Unknown` block. (2) The count is a literal `n` and `n × size`, added to the bytes already promoted in the function, does not exceed `MaxFrameBytes`. (3) The `Alloc` executes at most once per activation: its block is in no natural loop. (4) The function is not part of a call-graph cycle — a frame of a recursive function is multiplied by the recursion depth. (5) the allocation cannot fail observably — its count is a literal within the profile's allocation limit, or the profile declares allocation failure unobservable (the rule of [#500](https://github.com/Hawkynt/Polypiler/issues/500)): a failing heap allocation traps `OutOfMemory`, a failing frame traps `StackOverflow`, and a handler can tell them apart ([#115](https://github.com/Hawkynt/Polypiler/issues/115)). (6) [#348](https://github.com/Hawkynt/Polypiler/issues/348) does not report the handle `Shared` or `Moved` at any point: a handle whose ownership is transferred would be released by someone else. (7) Lifetime, alignment (`align=a`) and `[zero]` attributes carry over unchanged ([#121](https://github.com/Hawkynt/Polypiler/issues/121)); the region's extent and therefore every bounds trap stay the same. (8) `ICostModel.IsAvailable("alloc.stack")` is true and [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the frame (`Cost.Allocations` 1 → 0).

## How it is proven

`alias-facts+structural`: a region that is unreachable once its function returns is indistinguishable in the frame and on the heap, provided neither allocation can fail observably. No solver, no timeout path.

## When it must not fire

- `return buf;` or `global = buf;`: the frame is gone when the caller dereferences the pointer.
- A buffer allocated in a loop body and never released: on the heap that is a leak of `trip × n` bytes, in the frame it is a stack overflow after a few thousand iterations.
- A recursive descent parser promoting 64 bytes per level: recursion depth 1 000 needs 64 000 bytes of stack on a target that has 4 096.
- `Alloc[heap] 16` under a handler for `OutOfMemory` on a target where the heap can be exhausted: the promoted version raises `StackOverflow` instead, which the handler does not catch.
- A buffer passed to `Call @keep(%t)` whose summary is missing: the callee may store the pointer.

## Targets and cost gate

Pays off on targets with an explicit heap and a frame that can hold arrays: C, C++, Rust, Delphi, FreeBASIC, PowerBASIC, and machine-code backends. Their profiles list the form `alloc.stack`. Profiles of garbage-collected source targets whose language cannot express a frame array (JavaScript, Python, Java, Kotlin, Perl, PHP, Ruby) do not list it, and neither does a 6502 profile with its 256-byte hardware stack; there the pass does nothing. C# lists it only for element types `stackalloc` admits. No target compiler performs this on a `malloc`/`new` it cannot see through, so the rewrite does not duplicate work done later. The form name `alloc.stack` and the `forms` list of `cost.yaml` that carries it are defined by `OPT-MEM-FORMS`.

## Switching it off

`--disable-opt O0340`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `specialize` after [#500](https://github.com/Hawkynt/Polypiler/issues/500), [#503](https://github.com/Hawkynt/Polypiler/issues/503) and `OPT-MEM-LOOPTEMP` (which moves loop temporaries to a preheader where they become candidates); enables [#504](https://github.com/Hawkynt/Polypiler/issues/504) at function exit and removes helper calls from [#260](https://github.com/Hawkynt/Polypiler/issues/260)' closure.
