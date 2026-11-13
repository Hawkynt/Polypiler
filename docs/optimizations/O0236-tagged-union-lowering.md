# O0236 — Lower tagged unions and payload-carrying enums to tag-plus-payload records with checked accessors

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#471](https://github.com/Hawkynt/Polypiler/issues/471). Once implemented it is switched off with `--disable-opt O0236`.

## What it does

[#200](https://github.com/Hawkynt/Polypiler/issues/200) covers enumerations that are integers — plain, open and flag sets — and leaves "enumerations
with payloads (sum types, tagged unions)" to this area. A Rust `enum Shape { Circle { r }, Rect { w, h } }`
has no counterpart in C, PowerBASIC or C# without a tag field and a rule for which payload is valid. PRD
§30.1 row "Tagged unions, ADTs, enums, flags": variant tags, exhaustiveness and underlying representation
are the semantics; the fallback is tagged UDTs with generated accessors.

## Example

### Before

```
shape Shape [kind=union] { variant Circle { r: i32 }; variant Rect { w: i32, h: i32 }; variant Empty }
%s = Union.New.Shape Rect(w: 3, h: 4)
%w = Union.Get.i32[trap InvalidCast] %s, Rect.w
```

### After

```
type Shape = record { tag: u8, Circle_r: i32, Rect_w: i32, Rect_h: i32 }      ; alternative side-by-side
%s0 = RecordNew.Shape 1, 0, 3, 4
%t  = ExtractField.u8 %s0, tag
%ok = ICmp.eq.u8 %t, 1
Assert %ok [trap InvalidCast]
%w  = ExtractField.i32 %s0, Rect_w
```

## When it applies

(1) `Union.New` writes the tag of its variant and that variant's fields;
fields of other variants are zero in `side-by-side` and unspecified but never read in `overlay`. (2) Every
`Union.Get` either carries a trap label, and then the tag test precedes the read, or is `[unchecked]` and
dominated by a `Switch` or comparison on `Union.Tag` of the same value that selects its variant. (3) A
`Switch` over `Union.Tag` without a default lists every variant ([#200](https://github.com/Hawkynt/Polypiler/issues/200)'s closed-enumeration rule).
(4) The `overlay` alternative is chosen only when the target states `Union.Overlay`; the cost model
decides between the two when both apply.

## How it is proven

`structural+interpreter`: validated by interpreting the union form and the lowered form
on every fixture, with each variant constructed and each accessor applied to each variant.

## When it must not fire

- Reading `Rect.w` from a `Circle` without the tag test: in `overlay` it returns the circle's `r`; the
  source traps or does not compile.
- Assigning tags by variant name order instead of declaration order: a source `as u8` cast of the tag
  changes value.
- Dropping the tag for a union with a single payload variant plus `Empty`: `Empty` and a payload of all
  zeros become indistinguishable.
- An `[unchecked]` accessor hoisted above the `Switch` that justified it.

## Switching it off

`--disable-opt O0236`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(tagged-union)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

runs before [#477](https://github.com/Hawkynt/Polypiler/issues/477)'s decision trees are simplified; `overlay` hands the storage
to the union strategy the target documents under [#259](https://github.com/Hawkynt/Polypiler/issues/259).
