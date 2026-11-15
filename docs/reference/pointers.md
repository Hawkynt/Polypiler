# Pointers

A pointer of the IR is a *bounded* pointer: a region of memory and a byte offset in it. Pre-`Span` C# walks
memory with `byte*`, `fixed` and `*(uint*)(p + i)`; a pointer that stays inside the array it was taken from is
exactly a typed array and an index, which JavaScript has. The IR has such pointers (`IrTypeKind.Pointer`, #1331),
the C# frontend imports the unsafe subset of C#, the C# backend keeps it unsafe code in every profile and the
JavaScript backend lowers it to a region and an offset in every profile. Whatever means a machine address is refused.
Related: [repository README](../../README.md) · [views](views.md) · [exceptions](exceptions.md) ·
[IR and analysis](../architecture/IR.md).

## Semantics

- **Type.** `T*` over a number type, from `i8` to `f64`, or `void*` (`IrType.Pointer`). A pointer is a local or a
  parameter, never a result, a field, a global, a constant, an element of an array or a variable of a lambda: the
  verifier reports "a pointer is stored outside the stack".
- **Region.** The array a pin names (the whole of it, also for a pin of one element), the view a pin names (only the
  view), the memory of a `stackalloc`, or the one element a local whose address is taken lives in from then on.
  Forming a pointer is never checked - one past the end, or before the start, as C does. A load or a store checks
  that all its bytes lie in the region and traps with `IndexOutOfRange` otherwise; through the null pointer it
  traps with `NullReference`.
- **Arithmetic** scales by the size of the element: `p + 1` on `i32*` moves 4 bytes; `q - p` is the number of
  elements between them as an `i64` (5 for the elements 2 and 7 of one array, -5 the other way round). Nothing is
  counted, read or written through `void*`.
- **Reinterpretation.** `(U*)p` reads and writes the same bytes as another type, the lowest byte first on every
  host, with no alignment: `*(uint*)(p + 1)` of the bytes 0 to 15 is 67305985.
- **Null.** `fixed` over `null` or an empty array or view gives the null pointer (`EmptyIsNull()` is true in .NET
  too); `fixed` of `&a[i]` traps with `NullReference` for a null array and `IndexOutOfRange` for an element that is
  not there.
- **Comparison** of pointers into two regions: equality is false; an ordering and a difference trap with
  `InvalidOperation` - C# compares the addresses, whose order is no value any target reproduces.
- **stackalloc** into a pointer gives zeros and is valid until the function returns; a negative length traps with
  `IntegerOverflow`.
- **Lifetime.** A pointer never outlives the block of its pin or of the local it points into: a pointer variable of
  an outer block is assigned no pointer into an inner one ("a pointer outlives the block of its region"). The
  pinned variable itself is never assigned, as C# makes a `fixed` variable read-only.
- **Undefined in C#.** Out-of-bounds access is undefined behaviour in C#; the IR defines it as a trap (a
  refinement), JavaScript reproduces the trap, and the C# backend writes raw pointers without a check. The
  differential tests compare .NET only on calls in bounds.

## Operations

| IR | Arguments | Result | Trap |
| --- | --- | --- | --- |
| `Pin` | `(array or view)`, `(array or view, i)`; only the initializer of a declaration | `T*` | `NullReference`, `IndexOutOfRange` for `(…, i)` |
| `AddressOf` | `(x)` of a local of a number type no lambda captures | `T*` | — |
| `StackAlloc` | `(n)`; only the initializer of a declaration | `T*` | `IntegerOverflow` when negative |
| `PointerAdd` | `(T*, i32)` | `T*` | — |
| `PointerDifference` | `(T*, T*)` | `i64` | `InvalidOperation` across regions |
| `PointerEqual`, `PointerNotEqual` | `(T*, T*)` | `bool` | — |
| `PointerLess`, `PointerLessOrEqual`, `PointerGreater`, `PointerGreaterOrEqual` | `(T*, T*)` | `bool` | `InvalidOperation` across regions |
| `PointerLoad` | `(T*)` | `T` | `NullReference`, `IndexOutOfRange` |
| `PointerStore` | `(T*, T)`, the value evaluated first | — | `NullReference`, `IndexOutOfRange` |
| `PointerCast` | `(T*)` | `U*` | — |
| `IrNull` of `T*` | — | the null pointer | — |

`sizeof` of a number type is the constant C# folds it to. The traps are thrown as their classes of
[exceptions](exceptions.md): `IndexOutOfRangeException`, `NullReferenceException` and `ArithmeticException`.

## C# frontend

`CSharpPointerApi` is the construct table the tests check against the fixtures (`tests/fixtures/prototype/pointers`):

| C# | IR |
| --- | --- |
| `unsafe` classes, methods, fields and blocks | the context of the pointers |
| `byte*` … `double*`, `void*` | the pointer types |
| `fixed (T* p = a)`, `fixed (T* p = &a[i])`, `fixed (T* p = span)`, several declarators, `fixed (void* v = a)` | a block that declares `p` with `Pin` and holds the body |
| `&x` of a local or a parameter | `AddressOf` |
| `*p`, `p[i]` as a value and as the target of `=`, `+=`, … | `PointerLoad`, `PointerStore` of `PointerAdd` |
| `p + n`, `n + p`, `p - n`, `p++`, `p--`, `p += n`, `p -= n` as statements | `PointerAdd` |
| `p - q` | `PointerDifference` |
| `==`, `!=`, `<`, `<=`, `>`, `>=`, `p == null` | the comparisons; the compiler's `void*` conversion of both sides is left out |
| `(U*)p`, the conversion to `void*` | `PointerCast` |
| `sizeof(long)` | the constant 8 |
| `T* p = stackalloc T[n]` | `StackAlloc` into a pointer |

Refused with `PLY1002`, naming the construct: a cast between a pointer and an integer, `(byte*)0x1000`, `(long)p`,
`(ulong)p` - the value is a machine address, which no target reproduces; `nint`/`IntPtr` (and so their arithmetic
with pointers); `delegate*`; `char*`, `bool*` and a pointer to a struct; a pointer in a field, an array, a lambda
or a local function, returned, or as a parameter of a public method; a pointer kept after its `fixed` block or the
local it points into; a step of a pointer whose value is used (`*p++`); a conditional of pointers;
`[SkipLocalsInit]`, which would leave `stackalloc` unspecified; `Unsafe`, `GCHandle` and `Marshal` members as
members of the library. `->` and `fixed` buffers wait for layouts (#1332), `fixed (char* p = s)` for strings as
memory (#123).

## C# backend

Unsafe code exists since C# 1 in every framework, so every profile from `net2.0` to `net10.0` writes one form:

| IR | C# |
| --- | --- |
| the pointer types | `T*`, `void*` |
| `Pin` of an array, an element | `fixed (T* p = a)`, `fixed (T* p = &a[i])` around the rest of the block |
| `Pin` of a view | `fixed (T* p = span)` from `netstandard2.1` on; below, `fixed (T* pPinned = segment.Array) { T* p = segment.Count == 0 ? null : pPinned + segment.Offset; … }` |
| `StackAlloc` into a pointer | `T* p = stackalloc T[n];` for a constant length of fewer than 1024 bytes outside every loop; `fixed (T* p = new T[n])` around the rest of the block otherwise |
| `AddressOf`, `PointerLoad`, `PointerStore`, `PointerAdd`, `PointerDifference`, comparisons, `PointerCast` | `&x`, `*p` or `p[i]`, `*p = v`, `p + n`, `p - q`, `==` …, `(U*)p`; to `void*` without a cast |

A pinned variable the IR assigns is a copy of the `fixed` one, which C# makes read-only. Every class of a module
with pointers is declared `unsafe`, and the header says that the project compiling the file must set
`AllowUnsafeBlocks`. No bounds check is written. Two rows of the capability table, `CSharpFeature.UnsafeCode` and
`CSharpFeature.StackAllocPointer` from C# 1, let a profile without unsafe code refuse a module with pointers with
`PLY1053` and one without `stackalloc` into a pointer pin an array instead; no profile of today lacks either.

## JavaScript backend

JavaScript has no pointers (capability row `*p, a pointer`, never written, replaced by "view and index" at every level). A pointer is a
region and a byte offset:

| IR | every profile |
| --- | --- |
| a pointer in its function | two variables, `p` (the region) and `p$o` (the offset in bytes) |
| a pointer passed to a function | the array `[region, offset]`, which the function takes apart first |
| region | `{ a, o, n, s, k }`: the array - a typed array, or a plain one where the profile keeps the type so (ES5 without `--typed-arrays`; 64-bit integers below ES2020, as halves) - the first element, the number of elements, their size and their kind as a `DataView` names it; the null pointer is `null` |
| `Pin` | `$pin`, `$pinat` of an array or a typed view; `$pinv`, `$pinatv` of a view over a plain array |
| `AddressOf(x)` | `x` lives in `[x]`, or in a one-element typed array, from its declaration on |
| `StackAlloc` | the region of a new array of zeros, `$len` checking the length |
| `PointerLoad`, `PointerStore` | `$pget`, `$pset`: checked; the element directly where the kind is the region's and the offset aligned, the bytes otherwise |
| `PointerLoad` through a cast, every region typed (ES2020, ES2022) | `$pdv(p, p$o + at, 4).getUint32(0, true)`: a `DataView`, little-endian, unaligned - never `new Uint32Array(buffer, offset)` |
| reinterpretation in a plain array | the bytes of the elements, lowest first (`$bits32`, `$bits64`, `$float32`, `$float64` for floating-point elements) |
| `PointerDifference`, comparisons | `$pcmp`, `$peq`: the byte in the array's memory, across two regions an `ArithmeticError` |

A trap is the error of its class: `IndexOutOfRangeError` ("index out of range"), a `TypeError` ("null reference") -
JavaScript's own class of a null reference - for the null pointer, `ArithmeticError` ("invalid operation") for an
ordering across regions. No profile refuses a pointer operation (`PLY1152`).

## Known differences

- `stackalloc` into a pointer of a length that is no small constant, or inside a loop, is a pinned array in C#; of
  the length 0 that array's pointer is `null`, where the IR and JavaScript give a pointer to no elements. Only a
  comparison with `null` sees the difference.
- A pin of an element of a view keeps the pointer in the view in the IR and in JavaScript; in C# it reaches the
  whole array, which only an access out of bounds - undefined in C# - can tell.
