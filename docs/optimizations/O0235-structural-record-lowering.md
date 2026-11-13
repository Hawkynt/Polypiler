# O0235 — Lower tuples, anonymous and structural records to named records with generated equality

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#478](https://github.com/Hawkynt/Polypiler/issues/478). Once implemented it is switched off with `--disable-opt O0235`.

## What it does

A C# tuple, an anonymous type and a record have no declaration a C or PowerBASIC compiler could use, and
their `==` compares members, not addresses. PRD §30.1 row "Anonymous/structural types, records, tuples":
structural versus nominal identity, equality, layout and escape are the semantics; the fallback is
synthesized named records or flattened fields. §32 lists "anonymous-type equality" among the dispatch/object
tests.

## Example

### Before

```
%t = TupleMake.tuple<i32, f64> %a, %x
%u = TupleMake.tuple<i32, f64> %b, %y
%e = Record.Equals.bool[floatEq=ieee] %t, %u
```

### After

```
type Tuple_i32_f64 = record { item0: i32, item1: f64 }
func @Tuple_i32_f64_Equals_ieee(%p: Tuple_i32_f64, %q: Tuple_i32_f64) -> bool {
entry: %c0 = ICmp.eq.i32 (ExtractField.i32 %p, item0), (ExtractField.i32 %q, item0)
       Br %c0, f1, no
f1:    %c1 = FCmp.oeq.f64 (ExtractField.f64 %p, item1), (ExtractField.f64 %q, item1)
       Ret.bool %c1
no:    Ret.bool false }
%t = RecordNew.Tuple_i32_f64 %a, %x
%u = RecordNew.Tuple_i32_f64 %b, %y
%e = Call.bool @Tuple_i32_f64_Equals_ieee(%t, %u)
```

## When it applies

(1) The shape has `equality=structural`; `equality=custom` calls the declared
`equals` member instead and `equality=reference` is left to [#470](https://github.com/Hawkynt/Polypiler/issues/470). (2) Member comparison per
type: integers, booleans and enumerations by value; floats by the `floatEq` policy — `ieee`: `FCmp.oeq`;
`nan-equal`: `oeq` or both NaN; `total`: equal bit patterns after mapping every NaN to one canonical NaN;
nested structural members by their own generated function; references by `Object.Same` unless the member's
shape is itself structural. (3) A reference-storage record is converted to a value record only if no
instruction in the module observes its identity and it is not passed to code outside the module. (4) Two
structural keys that are equal map to one record; different keys never share one.

## How it is proven

`structural+interpreter` plus an exhaustive check of the three float policies over the 5
× 5 pairs of {1.0, NaN, +0, −0, a second NaN payload}; validated by interpreting both forms on every
fixture.

## When it must not fire

- Comparing with `memcmp` over the record bytes: padding bytes differ, +0 and −0 differ under `ieee`, and
  two NaN payloads differ under `nan-equal`.
- Using `FCmp.oeq` for a C# record: `new P(double.NaN) == new P(double.NaN)` is `True` in C#, `oeq` says
  false.
- Using `nan-equal` for a C# tuple: `(double.NaN, 1) == (double.NaN, 1)` is `False`.
- Turning a record class into a value while `ReferenceEquals(a, b)` is used: two equal records would
  become "the same".

## Switching it off

`--disable-opt O0235`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(structural-record)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

runs after [#472](https://github.com/Hawkynt/Polypiler/issues/472); the value records it produces are candidates for copy
elision and allocation removal.
