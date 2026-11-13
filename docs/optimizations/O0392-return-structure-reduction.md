# O0392 — Return only the record fields callers read and drop the computations of the rest

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0392`.

## What it does

A function returning a statistics record or a `(quotient, remainder)` pair computes every field for every
caller, although most callers read one. The unread fields are built, stored into the result and copied back —
and the code that computes them stays alive, because from inside the callee the result is observed. Only the
set of all callers knows which fields matter. Narrowing the return type to the fields somebody reads lets
ordinary dead-code elimination remove the rest of the work. PRD §4.8 (DCE, copy elision).

## Example

### Before

```
type Stats = record { total: i32, count: i32, worst: i32 }

func @Analyze(%a: ptr<i32>, %n: u32) -> Stats {
  ...                                       // computes %total, %count, %worst
  %s = RecordNew.Stats %total, %count, %worst
  Ret.Stats %s
}
// every caller:  %s = Call.Stats @Analyze(%data, %n);  %t = ExtractField.i32 %s, total
```

### After

```
func @Analyze(%a: ptr<i32>, %n: u32) -> i32 {
  ...                                       // %count and %worst are dead unless they have effects
  Ret.i32 %total
}
// every caller:  %t = Call.i32 @Analyze(%data, %n)
```

## When it applies

(1) The callee is owned (`ModuleOwnership` of `OPT-IPA-CONSTARG`) and returns a record or tuple by
value. (2) At every call site every use of the result is an `ExtractField` or projection; a result that is
stored, passed on, returned by another function, compared, or used in a `Phi` or `Select` counts as reading
all fields at that site. (3) The live set — the union over all call sites of the fields read — is a proper
subset of the fields. (4) Every `Ret` of the callee is rewritten to return the live fields, taken from the
returned value with `ExtractField`; a `Ret` of a direct self-call's unchanged result returns the reduced
result of that call. (5) Call sites are rewritten in the same step (`OPT-IPA-DEADPARAM`'s
`SignatureRewriter.ChangeReturnType`). (6) No instruction of the callee is removed by this pass. The reduced
result is never larger than the record, so [#155](https://github.com/Hawkynt/Polypiler/issues/155) is not expected to refuse; the original is listed
first.

## How it is proven

`structural`: a field no call site extracts cannot influence any caller; returning the remaining fields delivers the same values to every extract. The callee's instructions are untouched, so effects and traps are too. No solver, no timeout path.

## When it must not fire

- One caller stores the whole record to memory another function reads: every field is observed.
- A dropped field computed by `Call.i32 @log_worst(...)`: the call must still run; only its result is unused.
- A dropped field computed by a division that can trap: the division stays unless [#156](https://github.com/Hawkynt/Polypiler/issues/156) has a fact that it cannot trap.
- An address-taken function: an indirect caller reads `count`.
- Union taken per call site instead of over all sites: one site reads `total`, another `count`; returning only `total` breaks the second.

## Targets

every target: fewer values to return and the computations behind them gone. In generated source an
internal helper returns `int` instead of a record of which callers used one member; only internal functions
change.

## Switching it off

`--disable-opt O0392`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

module pass, first in phase `optimize` before [#159](https://github.com/Hawkynt/Polypiler/issues/159) — once a callee is inlined into some callers the closed set of calls is gone; triggers the per-function [#156](https://github.com/Hawkynt/Polypiler/issues/156) and [#164](https://github.com/Hawkynt/Polypiler/issues/164) of the same iteration; enables `OPT-IPA-ICF` and [#500](https://github.com/Hawkynt/Polypiler/issues/500) of callers' result records.
