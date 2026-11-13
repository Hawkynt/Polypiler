# O0260 — Fuse marshalling across adjacent foreign calls into one packed buffer

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#327](https://github.com/Hawkynt/Polypiler/issues/327). Once implemented it is switched off with `--disable-opt O0260`.

## What it does

PRD §29.3: "Across multiple compatible foreign calls, analyze effects and escape to preserve packed buffers
without redundant pack/unpack … Do not fuse across an observable mutation, retained pointer, error check,
callback or synchronization boundary." PRD §31, scenario "One packed buffer survives adjacent native
calls": "it may pack once and reuse the stable buffer across both calls". Without this rule a Perl or
Python port packs and unpacks the same record around every call of a sequence.

## Example

### Before

```
%b1 = AbiMaterialize[layout=@p5.layout, init=(type, value)] %rec
%p1 = AbiBorrow.ptr<u8>[scope=call] %b1
%r1 = AbiCall.i32[fn=@Peek] %p1
AbiRelease %b1
%b2 = AbiMaterialize[layout=@p5.layout, init=(type, value)] %rec
%p2 = AbiBorrow.ptr<u8>[scope=call] %b2
%r2 = AbiCall.i32[fn=@Bump] %p2
%rec2 = AbiCopyBack.P5[layout=@p5.layout, fields=(value)] %b2, %rec
AbiRelease %b2
```

### After

```
%b1 = AbiMaterialize[layout=@p5.layout, init=(type, value)] %rec
%p1 = AbiBorrow.ptr<u8>[scope=call] %b1
%r1 = AbiCall.i32[fn=@Peek] %p1
%r2 = AbiCall.i32[fn=@Bump] %p1
%rec2 = AbiCopyBack.P5[layout=@p5.layout, fields=(value)] %b1, %rec
AbiRelease %b1
```
- **Legality preconditions** — two boundary sequences S1 (first) and S2 (second) fuse iff all hold:
1. Both materialize the same layout (`StructuralId`) with the same target representation, both borrows
   have `scope=call`, and S1 and S2 are in the same basic block with S1 first.
2. The callee of S1 has a declared contract, does not list the parameter in `retains`, is not
   `Reentrant`, takes no callback, and writes only named fields (`writes(p: f…)` or nothing) — a
   whole-pointee or conservative write could change padding bytes.
3. Between S1's `AbiCall` and S2's `AbiMaterialize` there is no instruction with an effect other than
   reads and writes of the function's own non-escaping values: no other call, no `Synchronize`, no
   trap-capable instruction whose trap would observe the buffer's lifetime, no branch.
4. For every field f, the value v2 that S2 would encode is classified: *current* if f is not written
   by S1's callee and v2 is the same SSA value S1 encoded, or if v2 is the field f of S1's
   `AbiCopyBack` result; otherwise *changed*. Changed fields are stored into the kept buffer with the
   field's setter directly before S2's call. If S1's callee writes f and S1 has no copy-back of f and
   v2 is the pre-call value, f is *changed* (the stale value must overwrite the callee's write, as the
   unfused program did).
5. S1's `AbiCopyBack`, if any, stays where it is and reads the kept buffer before any store of
   step 4.
Chains of more than two sequences fuse pairwise left to right.

## How it is proven

`effect-facts` (preconditions 2 and 3 from [#73](https://github.com/Hawkynt/Polypiler/issues/73) contracts and
[#82](https://github.com/Hawkynt/Polypiler/issues/82)) plus `differential` translation validation on scripted stubs. Any missing fact leaves
the IR unchanged with the reason in the opt-report.

## When it must not fire

- S1's callee retains the pointer: after fusion the buffer outlives S1 as before, but an unfused
  program gave the callee a *different* buffer for S2 — a retained pointer would now alias S2's data.
- S1's callee writes `value`, the program ignores the write and passes the old `value` again: naive
  reuse would hand S2 the callee-modified value (0x12345679 instead of 0x12345678).
- A third foreign call between S1 and S2 that may write through a retained alias.
- An error check between the calls (`if %r1 < 0 goto fail`): S2 is control-dependent on S1's result.

## Switching it off

`--disable-opt O0260`; active at `--opt balanced`, `speed`, `size` and `memory`; off at
`--opt none`.

## Interactions with other passes

runs after `iop.plan` in phase `legalize`; before [#321](https://github.com/Hawkynt/Polypiler/issues/321) (which then narrows
the remaining copy-backs); enables buffer reuse in every generator without generator changes.
