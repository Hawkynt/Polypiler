# Memory layout

How a value of the IR lies in bytes when it lies in memory: the layout of a record under the data model
of a target, the order of the bytes of a number, and the position of an element of an array of one or
more dimensions. Everything here is library code of `Polypiler.Core.IR` plus nodes the interpreter runs
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
| `PointerLoadLittle/Big.T(p)` | the number whose bytes lie at `p` in the stated order | `p` is a `u8*`; `T` an integer that is no enum, `f32` or `f64` |
| `PointerLoadLittle/Big.bigint(p, n)` | the unsigned number of the `n` bytes at `p`: 24 bits, 128 bits, 4096 bits | `n` is an `i32` |
| `PointerStoreLittle/Big(p, v)`, `(p, n, v)` | the inverse; a `bigint` writes its lowest `n` bytes in two's complement | as for the loads |
| `IrElementAddress(p, shape, i…, checked)` | `ElemAddr`: the element at the indices | below |

A query of a record whose rule is packed(1) or explicit, the size of a number and the alignment of a
byte have the same answer under every profile and are answered at once. Every other query waits for the
profile: `IrInterpreter.Call(module, function, arguments, profile)` answers it, and
`IrLayoutLowering.Lower(module, profile)` replaces each query by its value and each field address by
`PointerAdd` on the `u8*` and a `PointerCast`. Without a profile the interpreter refuses it. The rules
`ir.verifier.records.layout` and `ir.verifier.records.field-byte-order` are registered in the standard
registry.

## Byte order

An ordered load or store reads and writes its bytes one by one, so no host order leaks in; all bytes are
checked against the region before one is read or written, and no alignment is required. A field with
`IrField.ByteOrder` set is addressed as bytes and read with the ordered load of its order; the rule
`ir.verifier.records.field-byte-order` refuses another order, another type, and a cast of its address.

A plain load or store through a pointer that reinterprets bytes - a `u32*` cast from a `u8*`, a byte of an
`u32[]` - uses the byte order of the profile: the interpreter lays memory out little-endian without a
profile, and in the profile's order under one. This is the only place where a host order exists.

`IrLayoutLowering` expands an ordered load or store of a number into byte loads or stores, shifts, ors
and `BitsFloat`/`FloatBits` where its address and value are locals, constants or pointer arithmetic on
them; a store reads its last byte first so that it traps before it writes. A `bigint` access, or one
whose address is computed otherwise, stays as it is, and a backend that meets it refuses it as an
operation it has no form for.

## Arrays in memory

`IrArrayShape` describes an array of rank 1 to 8: per dimension a lower bound and an extent, and the
storage order. The element at `(i_0, …, i_{r-1})` lies at the linear position
`Σ (i_d − lower_d) · stride_d`; with `row` order the last dimension has the stride 1, with `column` the
first. `IrArrayShape.Fixed(n)` is `array<T, n>`. In a 3 × 4 array the element at (1, 2) is at 6 in row
order and at 7 in column order; in a 2 × 3 × 4 array (1, 0, 2) is at 14 and 13.

`IrElementAddress` takes a pointer to the first element, the shape and one integer index per dimension.
A checked address traps with `IndexOutOfRange` when an index lies outside its own dimension - also when
the linear position would exist - before anything is read; an unchecked one is formed without a check.
Every index is extended by its own type: a `u8` of 255 is 255, an `i64` of 2^40 is never truncated.
The verifier refuses the wrong number of indices, an index that is no integer, and a constant index
outside its dimension, checked or not. `IrLayoutLowering` writes an unchecked address as `PointerAdd` of
the linear position, converted to `i32` with a trap on overflow; a checked one stays, for the backends to
write with their own bounds checks.
