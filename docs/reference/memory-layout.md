# Memory layout

How a value of the IR lies in bytes when it lies in memory: the layout of a record under the data model
of a target. Everything here is library code of `Polypiler.Core.IR` plus nodes the interpreter runs
and `IrLayoutLowering` resolves for one target, so a backend that writes pointers needs nothing of its
own.
Related: [repository README](../../README.md) · [IR and analysis](../architecture/IR.md) ·
[value records](records.md) · [pointers](pointers.md) · [integer and bit operations](integer-operations.md).

## Layout profiles

A `LayoutProfile` is the data model of a target as far as bytes are concerned: the size of a pointer,
the largest alignment of a number, and the byte order of memory. A number of `n` bytes is aligned to
`min(n, MaxAlignment)`.

| Profile | Pointer | Largest alignment | Order | Source |
| --- | --- | --- | --- | --- |
| `lp64-sysv` | 8 | 8 | little | System V AMD64 ABI §3.1.2 |
| `ilp32-sysv` | 4 | 4 | little | System V i386 ABI: `double` and `long long` aligned to 4 in a struct |
| `dos16` | 2 | 2 | little | 16-bit DOS compilers, word alignment |

A profile of one's own is `new LayoutProfile(name, pointerSize, maxAlignment, byteOrder)` or a standard
one `with { ByteOrder = IrByteOrder.Big }`.

## Record layout

`RecordLayout.Compute(record, profile)` gives the offset of every field, the size, the alignment and
every range of padding - the tail included - so that no consumer infers it. The rule is the record's
`IrStructLayout`:

| Rule | `IrStructLayout` | Placement |
| --- | --- | --- |
| natural | none, or `Sequential` without packing | each field at the next multiple of its alignment; size rounded up to the largest alignment |
| packed(n) | `Sequential` with `Pack = n` | as natural, every alignment capped at `n` |
| explicit | `Explicit` with one offset per field | each field at its offset; aligned to `Pack`, or 1; size the stated `Size`, or the end of the last field |

A stated `Size` of a sequential layout enlarges the record. For `H { a: u8, b: u32, c: u16 }`:

| Rule | Profile | Offsets | Size | Alignment | Padding |
| --- | --- | --- | --- | --- | --- |
| natural | `lp64-sysv` | 0, 4, 8 | 12 | 4 | 1+3, 10+2 |
| packed(1) | `lp64-sysv` | 0, 1, 5 | 7 | 1 | none |
| packed(2) | `lp64-sysv` | 0, 2, 6 | 8 | 2 | 1+1 |
| natural | `dos16` | 0, 2, 6 | 8 | 2 | 1+1 |

`RecordLayout.Problems` names what keeps a record from having a layout: a field without bytes (a string,
an array, an object), overlapping explicit fields (an overlay is #375), a stated size short of the
fields, a packing that is no power of two, a record that holds itself. `Compute` throws for them.
`IrStructType.LayoutObservable` marks a record whose bytes are seen from outside - a file, a stream, an
ABI boundary, an overlay, an address that reaches unknown code; it keeps the declared layout exactly and
does not change what `Compute` gives. A frontend sets it; an analysis may only set it.

## Nodes

| Node | Meaning | Verifier |
| --- | --- | --- |
| `IrLayoutOf(Size, T)` | `SizeOf`: the size of `T` in bytes, an `i64` | `T` is a truth value, a number, an enum, a pointer or a record of the module |
| `IrLayoutOf(Alignment, T)` | `AlignOf` | as for the size |
| `IrLayoutOf(Offset, R, f)` | `OffsetOf`: the offset of field `f` of record `R` | `f` is one of `R`'s own |
| `IrFieldAddress(p, R, f)` | `FieldAddr`: `p` moved by the offset of `f` | `p` is a `u8*`; `f` is one of `R`'s own; the result is `T*` for a number of the profile's order, `u8*` otherwise |

A query of a record whose rule is packed(1) or explicit, the size of a number and the alignment of a
byte have the same answer under every profile and are answered at once. Every other query waits for the
profile: `IrInterpreter.Call(module, function, arguments, profile)` answers it, and
`IrLayoutLowering.Lower(module, profile)` replaces each query by its value and each field address by
`PointerAdd` on the `u8*` and a `PointerCast`. Without a profile the interpreter refuses it. The rule
`ir.verifier.records.layout` is registered in the standard registry. A field may state its byte order
(`IrField.ByteOrder`); its address is then a `u8*`.
