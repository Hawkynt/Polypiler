# Views

A view is a window onto the elements of an array: the array, the first element and a number of them.
C# writes it as `Span<T>` and `ReadOnlySpan<T>`, JavaScript as the `subarray` of a typed array. The IR
has one family of views (`IrTypeKind.Span`, `IrTypeKind.ReadOnlySpan`, #1325), and every operation on
it has one stated trap. Both frontends import views, and both backends write them in the form each
profile has.
Related: [repository README](../../README.md) · [IR and analysis](../architecture/IR.md) ·
[integer and bit operations](integer-operations.md) · [value records](records.md).

`Memory<T>` and `ReadOnlyMemory<T>`, the views a class can keep in a field, come with classes (#1319).
Until then a view lives on the stack only.

## Semantics

- **Types.** `span<T>` and `readonly span<T>` of a number type `T`, from `i8` to `f64`. A view is a
  local, a parameter or a result. It is never stored in an array, a field or a constant: the verifier
  reports "a span is stored outside the stack". Nothing is written through a read-only view: the
  verifier reports "an element of a read-only span is written".
- **Bounds.** A view or slice outside its source traps with `ArgumentOutOfRange`, which is new in
  `IrTrap`, as do a negative start or length, `AsSpan(-1, 2)` included. An element outside a view
  traps with `IndexOutOfRange`, and a copy into a shorter view with `ArgumentOutOfRange`.
- **Copy.** `ViewCopy` behaves like memmove. Copying elements 0..7 of `0, 1, …, 9` to index 2 gives
  `0, 1, 0, 1, 2, 3, 4, 5, 6, 7`.
- **Equality.** `ViewEqual` compares elements as `IEquatable<T>.Equals` does, so `[NaN, 0.0]` equals
  `[NaN, -0.0]`. JavaScript's `===` would say false.
- **stackalloc.** `StackAlloc` gives zeros. A negative length traps with `IntegerOverflow`, as a new
  array's does.
- **Reinterpretation.** `Reinterpret` reads the bytes of a view as elements of another number type,
  lowest byte first on every host. The length becomes the number of whole elements: 7 bytes give one
  `uint`, 3 give none. In the IR and in C# from .NET Standard 2.1 on, the result shares the memory of
  its source. Below that, and in JavaScript, it is a copy taken when it is made. The C# frontend
  therefore accepts `MemoryMarshal.Cast`/`AsBytes` only into a read-only span. A copy behaves
  differently only when the source changes while the result is still read.
- **Endian access.** `LoadLittle`, `LoadBig`, `StoreLittle` and `StoreBig` take a view of bytes
  wherever they take a `u8[]`. On a view, a value outside it traps with `ArgumentOutOfRange`, as
  `BinaryPrimitives` on a span throws.

## Operations

| IR | Arguments | Trap |
| --- | --- | --- |
| `ViewOf` | `(source)`, `(source, start)`, `(source, start, length)` of an array or a view | `ArgumentOutOfRange` outside the source |
| `Slice` | `(view, start)`, `(view, start, length)` | `ArgumentOutOfRange` outside the view |
| `ViewLength` | `(view)` | — |
| element load and store | `IrArrayLoad`, `IrStore`, `IrStep` on a view | `IndexOutOfRange` |
| `StackAlloc` | `(length)` | `IntegerOverflow` when negative |
| `ViewCopy` | `(source, target)` | `ArgumentOutOfRange` for a shorter target, before anything is copied |
| `ViewTryCopy` | `(source, target)` | none: false, and nothing is copied |
| `ViewFill` | `(view, value)` | — |
| `ViewEqual` | `(a, b)` | — |
| `ViewToArray` | `(view)` | — |
| `Reinterpret` | `(view)` | — |

## C# frontend

`CSharpViewApi` is the table the lowering reads and the tests check:

| C# | IR |
| --- | --- |
| `Span<T>`, `ReadOnlySpan<T>` of `sbyte` … `double` | the view types (`char` waits for strings, #1314) |
| an array converted to a span, a span to a read-only span | `ViewOf` |
| `array.AsSpan()`, `AsSpan(start)`, `AsSpan(start, length)`, `new Span<T>(array[, start, length])` | `ViewOf` |
| `.Slice(a)`, `.Slice(a, n)`, `v[a..b]`, `v[..b]`, `v[a..]`, `v[^n..]`, `v[a..^n]` | `Slice` (an end from the end needs a view in a variable) |
| `array[a..b]` | `ViewToArray` of the slice: C# copies |
| `v[i]`, `v[^n]`, `.Length`, `.IsEmpty`, `foreach` | element load and store, `ViewLength` |
| `stackalloc T[n]` into a span | `StackAlloc` |
| `CopyTo`, `TryCopyTo`, `Fill`, `Clear`, `SequenceEqual`, `ToArray` | `ViewCopy`, `ViewTryCopy`, `ViewFill`, `ViewEqual`, `ViewToArray` |
| `MemoryMarshal.Cast<TFrom, TTo>`, `AsBytes` into a read-only span | `Reinterpret` |
| `MemoryMarshal.Read<T>`, `Write<T>` of a number of more than one byte; `BinaryPrimitives.Read…`/`Write…` on any span of bytes | `LoadLittle`, `StoreLittle`, … |

The frontend refuses each of these with `PLY1002`, naming it: `MemoryMarshal.GetReference`,
`CreateSpan` and `CreateReadOnlySpan`; a `ref` local; a span of anything but a number; `stackalloc`
with an initializer; a cast into a writable span; and any member of the span types the table does not
list, such as `MemoryExtensions.Reverse`. A `[SkipLocalsInit]` method needs unsafe code, which the
compiler refuses (`PLY1000`).

## JavaScript frontend

| JavaScript | IR |
| --- | --- |
| `typed.subarray(begin[, end])` | `Slice` or `ViewOf` at positions clamped as ECMA-262 §23.2.3.29 clamps them |
| `typed.slice(begin[, end])` | `ViewToArray` of that slice: a copy |
| `target.set(source[, offset])` | `ViewCopy` into the slice from `offset`, the `RangeError` of a source that does not fit |
| `typed.fill(value[, start[, end]])` | `ViewFill` of the clamped part |
| `.length`, element access of a view | `ViewLength`, element load and store |
| `new DataView(v.buffer, v.byteOffset, v.byteLength)` of a view | the endian loads and stores on the view |

A position is clamped by explicit arithmetic: `p < 0 ? Max(length + p, 0) : Min(p, length)`, and an
end before the begin leaves nothing. The exact slice of the IR then never traps where JavaScript does
not, so `subarray(13, 17)` of 16 bytes has length 3 in C# as well. A position that is no proven
integer is truncated as a double, and a NaN becomes 0. `subarray` and `slice` read their array and
positions more than once, so each has to be a variable, a constant or arithmetic of them.

The frontend refuses each of these with `PLY1102`: `new Uint32Array(b.buffer, …)`, which reads in the
host's byte order and needs an aligned offset; `b.buffer` used other than in the `DataView` form;
`new DataView(v.buffer)` of a view, which would see the whole buffer; and a view of a plain `Array`.
`for…of` over a view is not imported yet.

## C# backend

| IR | `net2.0` … `netstandard2.0` | `netstandard2.1` and later |
| --- | --- | --- |
| the view types | `global::System.ArraySegment<T>` | `Span<T>`, `ReadOnlySpan<T>` |
| `ViewOf`, `Slice` | `new ArraySegment<T>(a)`, helpers `ViewOf`, `ViewFrom`, `Slice`, `SliceFrom` throwing `ArgumentOutOfRangeException` | `MemoryExtensions.AsSpan(a, s, n)`, `v.Slice(s, n)` |
| `ViewLength` | `v.Count` | `v.Length` |
| element load, store, step | helpers `ViewGet`, `ViewSet<T>`, `v.Array[ViewIndex(v, i)]`, throwing `IndexOutOfRangeException` | the indexer |
| `StackAlloc` | `new ArraySegment<T>(new T[n])` | `stackalloc T[n]` for a constant length below 1024 bytes outside every loop, `new T[n]` otherwise |
| `ViewCopy`, `ViewTryCopy`, `ViewFill`, `ViewEqual`, `ViewToArray` | helpers (`Array.Copy` handles the overlap, `EqualityComparer<T>.Default` compares) | `CopyTo`, `TryCopyTo`, `Fill`, `MemoryExtensions.SequenceEqual`, `ToArray` |
| `Reinterpret` | helper `ViewCast<TFrom, TTo>`: a copy through `Buffer.BlockCopy` | `MemoryMarshal.Cast<TFrom, TTo>` |
| endian loads and stores on a view | helpers `LoadView`, `StoreView` | `BinaryPrimitives.Read…(v.Slice(i))`; a store through helper `StoreSpan`, which evaluates the value before it checks |

`Span<T>`, `MemoryExtensions`, `MemoryMarshal` and `BinaryPrimitives` are part of .NET Standard 2.1
and absent from its 2.0 (the reference assemblies, checked on 2026-11-14). Every C# profile from C# 8
on has `stackalloc` inside an expression. No form leaves `stackalloc` with a length it cannot prove
non-negative: .NET 10 ends the process on `stackalloc int[-1]` instead of throwing.

## JavaScript backend

| IR | Element type in a plain array (ES5 without `--typed-arrays`; 64-bit integers below ES2020) | Element type in a typed array |
| --- | --- | --- |
| the view types | an object `{ a, o, n }`: the array, the first element, the length | the `subarray` of the typed array |
| `ViewOf`, `Slice` | `$vobj`, `$vsub` | `$slice`, which checks the range first: `subarray` would clamp silently |
| element load and store | `$vget`, `$vset`, `$vix` | `$get`, `$set` |
| `StackAlloc` | `$vobj($new($len(n), 0))` | `new Int32Array($len(n))` and its kin |
| `ViewCopy`, `ViewTryCopy` | `$vcopyo`, `$vtrycopyo`: the direction chosen for an overlap | `$vcopy`, `$vtrycopy`: `target.set(source)`, which clones an overlapping source |
| `ViewFill` | `$vfillo` | `.fill`, or `$fill` |
| `ViewEqual` | `$vequalo`, also on `{ hi, lo }` | `$vequal`: `x === y \|\| (x !== x && y !== y)` |
| `ViewToArray` | `$vtoarrayo` | `$vtoarray` |
| `Reinterpret` | `$vcasto` between integers of up to 32 bits; anything else is `PLY1152` | `$vcast`, through a `DataView` with little-endian `get`/`set`: never `new Uint32Array(buffer, offset)` |
| endian loads and stores on a view | the byte helpers of the arrays at the place `$vat` checked | `$vdv(v, i, size).getUint32(0, little)`, `$vstore(…, "setUint32")` |

A trap is the `RangeError` of its message: `argument out of range`, `index out of range`,
`integer overflow`.
