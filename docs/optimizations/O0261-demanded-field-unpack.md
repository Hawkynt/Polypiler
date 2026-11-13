# O0261 — Unpack only the fields that are demanded after a foreign call

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#321](https://github.com/Hawkynt/Polypiler/issues/321). Once implemented it is switched off with `--disable-opt O0261`.

## What it does

PRD §29.3: "Unpack only demanded fields; invalidate cached fields when an external callee can mutate them."
After a call, a generated binding decodes every field the callee might have written, although the program
often reads one of them or none. Narrowing the copy-back to what is read later shortens the generated
`unpack` and removes conversions — as long as a field the callee changed and the program reads is never
served from a stale copy.

## Example

### Before

```
%r = AbiCall.i32[fn=@Bump] %p
%rec2 = AbiCopyBack.P5[layout=@p5.layout, fields=(type, value)] %buf, %rec
AbiRelease %buf
%v = ExtractField.u32 %rec2, value
```

### After

```
%r = AbiCall.i32[fn=@Bump] %p
%rec2 = AbiCopyBack.P5[layout=@p5.layout, fields=(value)] %buf, %rec
AbiRelease %buf
%v = ExtractField.u32 %rec2, value
```
When no field is demanded the `AbiCopyBack` is removed and users of `%rec2` are redirected to `%rec`.

## When it applies

1. Every user of the copy-back result is an `ExtractField` of a named field, an `AbiMaterialize` (which
   demands the fields of its `init` list), or another `AbiCopyBack` taking it as the base record. Any
   other user — a store, a call argument, a return, a phi — demands all fields.
2. A field is removed from the list only if it is not demanded.
3. A demanded field stays in the list whenever the callee may have written it; it is never replaced by
   its pre-call value. A demanded field the callee provably does not write is, by the copy-back's
   definition, already taken from the base record.
4. The buffer is not in the retained set (retained storage is read in place, not copied back).

## How it is proven

`dataflow-liveness` over record fields plus `differential` on scripted stubs. A user
the analysis does not understand demands all fields.

## When it must not fire

- The record is stored to memory after the call: dropping `type` from the list would store the
  pre-call `type` although the callee changed it.
- A field written by the callee and read afterwards (`value` after `iop_p5_bump`): forwarding the
  pre-call value would yield 0x12345678 instead of 0x12345679.
- The copy-back result feeds a phi whose other input is a full record.

## Switching it off

`--disable-opt O0261`; active at `--opt balanced`, `speed`, `size` and `memory`; off at
`--opt none`.

## Interactions with other passes

runs after [#327](https://github.com/Hawkynt/Polypiler/issues/327); uses the field lists [#345](https://github.com/Hawkynt/Polypiler/issues/345) produced; enables
dead-code elimination of decode instructions and shorter `unpack` templates in the generators.
