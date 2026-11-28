# Variants

The dynamic type of the IR: a value that carries its own tag, as Visual Basic's `Variant` and COM's
`VARIANT` do (#1556). The IR states only how a value is made, how its tag is read and how its payload
is taken out; what a language computes with its dynamic values - Visual Basic's promotion of `Integer`
arithmetic to `Long`, its `Null` - is the frontend's code written over those three operations, so
every backend that has them computes what the source language computes.
Related: [repository README](../../README.md) · [ADR 0300, static types first](../adr/0300-static-types-first.md) · [closed unions](unions.md) · [optional values](optional-values.md) ·
[exceptions](exceptions.md) · [the classic Visual Basic frontend](../architecture/VISUALBASIC-FRONTEND.md) ·
[IR and analysis](../architecture/IR.md).

## The model

- **Type.** `IrType.Variant`, written `variant`, `IrTypeKind.Variant`. A variant is a local, a
  parameter, a result, a global or an element of an array (`variant[]`, #1567), whose new elements are
  `Empty` and which `ArrayCopy` copies as values. No struct field, record field, optional value or
  collection holds one, and there is no constant of it; the verifier refuses each.
- **Tags.** `IrVariantTag`, numbered as the COM `VARENUM` (copied exactly, so Visual Basic's `VarType`
  is the tag itself):

  | Tag | Number | Payload |
  | --- | --- | --- |
  | `Empty` | 0 | none: a value not assigned yet |
  | `Null` | 1 | none: a value known to be missing |
  | `I16` | 2 | `i16` |
  | `I32` | 3 | `i32` |
  | `F32` | 4 | `f32` |
  | `F64` | 5 | `f64` |
  | `Currency` | 6 | `i64`, ten thousandths |
  | `Date` | 7 | `f64`, days since 1899-12-30 |
  | `String` | 8 | `str` |
  | `Bool` | 11 | `bool` |
  | `U8` | 17 | `u8` |
  | `I64` | 20 | `i64` |

  `IrVariants.PayloadType(tag)` gives the payload type, `IrVariants.TagOf(type)` the tag a value of a
  type is boxed with (never `Currency` or `Date`, whose payload types are shared), `IrVariants.Uses(module)`
  whether a module needs a backend that writes the type.
- **Operations.**
  - `IrVariantOf(tag, value)` makes a value: `value` is of the tag's payload type, and absent exactly
    for `Empty` and `Null`.
  - `IrVariantTagOf(value)` is the tag as an `i32`.
  - `IrVariantValue(value, tag)` is the payload of a value of that tag, and traps with
    `IrTrap.InvalidCast` on any other tag, as a .NET cast of an `object` to a value type it does not
    hold throws. `IsTested` marks one the frontend has tested the tag of first: the meaning is the
    same, a backend may leave the test out.
- **No operators.** No arithmetic, comparison, conversion or equality takes a variant; a frontend
  tests the tag and works on payloads. A tested payload and the other two nodes never trap
  (`IrShape.IsTrapFreeNode`), which the effect summaries see.
- **Boundary.** A value crosses the interpreter's boundary as an `IrVariant(tag, value)`, the payload
  a .NET value of its type (`(short)5`, `"a"`), `IrVariant.Empty` and `IrVariant.Null` without one,
  an array of them as an `IrVariant[]`; one of another shape is refused as an argument.
- **Module format.** The type is its kind; the three nodes are tags of their own after the last
  older one, so every older layout reads as before.

## In the backends

| Backend | A variant | Tag | Payload of a tag not tested |
| --- | --- | --- | --- |
| Visual Basic | `Variant`: `Empty`, `Null`, the payload through its conversion function (`CInt(x)`, `CLng(x)`, `CDate(x)`, `CCur(CDec(x) / 10000)`); arrays `Variant()` | `VarType(v)` | a test that raises error 13, then the conversion function (`CLngLng(CDec(v) * 10000)` of a currency) |
| C# | `object`: `null` for `Empty`, `System.DBNull.Value` for `Null`, a currency the `decimal` of its amount, a date the nested `VariantDate` of its days, every other payload boxed as its own type; arrays `object[]` | the helper `VariantTag`, by the type of the box | the cast, through `VariantPayload`, which turns `Empty` and `Null` into the `InvalidCastException` |
| JavaScript | a pair `[tag, payload]` (`[0]`, `[1]` without one); arrays of pairs | `v[0]` | `$variant(v, tag)`, which throws the `InvalidCastError` |
| Python | a tuple `(tag, payload)` (`(0,)`, `(1,)`); lists of tuples | `v[0]` | `_ply_variant(v, tag)`, which raises the `InvalidCastError` |

JavaScript and Python keep the tag because their own values cannot tell an `i16` from an `i32` or an
`f64`, which the source language can. C# boxes a date in a class of its own rather than as a `DateTime`,
whose `FromOADate` rounds to the millisecond. JavaScript refuses a payload of 64 bits, a currency too;
Visual Basic writes a currency only where the profile has `LongLong` (vba7-64). Rust, Go, C++, the JVM,
MSIL, Perl, Batch and VBScript have no form of the type yet and refuse it with their type diagnostic.
