# Integer and bit operations

The intrinsic operations of the IR that hashes, ciphers, checksums and codecs are built from. Each is
defined on every integer type it lists, with the meaning `IntegerOperations` in `Polypiler.Core.IR`
gives it; the interpreter calls that class, and both backends reproduce it in every target profile.
Related: [repository README](../../README.md) · [IR and analysis](../architecture/IR.md).

## Conventions

- **Widths.** `all` is `i8`, `u8`, `i16`, `u16`, `i32`, `u32`, `i64`, `u64`; `unsigned` is the four
  unsigned types; `multibyte` is the types of 16, 32 and 64 bits.
- **Bit pattern.** A signed value is counted, rotated, swapped and masked as its two's complement.
- **Counts and positions** are `i32`. A rotate takes its count modulo the width, a negative one
  included; every other count, position, start or length outside its range traps.
- **Traps** are the four the IR has: *invalid operation* for a field, position, mask width or clamp
  bound outside its range; *index out of range* for an endian access, before anything is written;
  *divide by zero* and *integer overflow* where the division operator of the IR has them. No
  operation adds a trap kind.

## The operations

| Family | Operation | Arguments → result | Widths | Definition and edge cases |
| --- | --- | --- | --- | --- |
| Rotate | `RotateLeft`, `RotateRight` | `(T, i32) → T` | all | count modulo the width, negative counts included |
| Counts | `PopCount` | `T → i32` | all | set bits of the pattern |
| | `LeadingZeroCount`, `TrailingZeroCount` | `T → i32` | all | the width for 0 |
| | `Log2` | `T → i32` | all | position of the highest set bit of the pattern; 0 for 0 |
| | `Parity` | `T → i32` | all | 1 for an odd number of set bits, else 0 |
| Reordering | `ByteSwap` | `T → T` | all | an 8-bit value is itself |
| | `BitReverse` | `T → T` | all | the lowest bit becomes the highest |
| Powers of two | `IsPow2` | `T → bool` | all | false for 0 and for every negative value |
| | `RoundUpPow2` | `T → T` | all | smallest power of two not below the pattern, modulo 2^width: 0 for 0, 0 where it is 2^width, the smallest value of a signed type for 2^(width-1) |
| Wide multiply | `BigMul` | `(i32, i32) → i64`, `(u32, u32) → u64` | — | exact |
| | `MulHigh` | `(T, T) → T` | all | upper half of the product in twice the width, signed or unsigned as `T` |
| Division | `DivFloor`, `ModFloor` | `(T, T) → T` | all | quotient rounded down; remainder with the sign of the divisor |
| | `DivEuclid`, `ModEuclid` | `(T, T) → T` | all | remainder from 0 to the magnitude of the divisor |
| | — | | | all four trap on a zero divisor and on the smallest value divided by -1, like `Divide` and `Remainder` |
| Bounds | `Min`, `Max` | `(T, T) → T` | all | |
| | `Clamp` | `(T, T, T) → T` | all | low above high traps (invalid operation) |
| | `Abs` | `T → T` | all | traps on the smallest signed value; an unsigned value is itself |
| | `AbsWrap` | `T → T` | all | the smallest signed value is itself |
| | `Sign` | `T → i32` | all | -1, 0, 1; 0 or 1 for an unsigned value |
| Saturating | `AddSaturate`, `SubSaturate`, `MulSaturate` | `(T, T) → T` | all | the exact result clamped to the range of `T` |
| Carry | `AddCarry`, `SubBorrow` | `(T, T, bool) → T` | unsigned | low word of `a + b + carry`, `a - b - borrow` |
| | `CarryOut`, `BorrowOut` | `(T, T, bool) → bool` | unsigned | whether that sum reaches 2^width, that difference is negative |
| Overflow test | `AddOverflows`, `SubOverflows`, `MulOverflows` | `(T, T) → bool` | all | whether the exact result is outside `T`; never traps |
| Bit fields | `ExtractBits` | `(T, start, length) → T` | all | the field at the bottom, zeros above; length 0 gives 0; a field outside the width traps |
| | `InsertBits` | `(T, field, start, length) → T` | all | the field replaced by the lowest bits of `field` |
| | `TestBit` | `(T, position) → bool` | all | a position outside 0 to width-1 traps |
| | `SetBit`, `ClearBit`, `ToggleBit` | `(T, position) → T` | all | as `TestBit` |
| Masks | `LowMask` | `count → T` | all | the type of the node fixes the width; `LowMask(width)` is all ones; outside 0 to the width traps |
| | `IsolateLowestSet` | `T → T` | all | `x & -x` |
| | `ClearLowestSet` | `T → T` | all | `x & (x - 1)` |
| | `MaskToLowestSet` | `T → T` | all | `x ^ (x - 1)`; all ones for 0 |
| | `SignExtend`, `ZeroExtend` | `(T, bits) → T` | all | the lowest `bits` bits extended by their top bit or by zeros; `bits` outside 1 to the width traps |
| Endian access | `LoadLittle`, `LoadBig` | `(u8[], index) → T` | multibyte | a byte outside the array traps |
| | `StoreLittle`, `StoreBig` | `(u8[], index, T) → void` | multibyte | traps before anything is written |

Truncating division and its remainder are the `Divide` and `Remainder` operators of the IR.

## Forms per profile

### C#

One row of `CSharpCapabilities` per operation and width. Where the base library has the operation,
the row names its first profile and calls it from there on; before it, and for every operation no
framework has, the class gets a private C# 2 helper on the 64 bits of the pattern, only when it calls
it. A form that C# writes with its own operators exists in every profile.

| Operation | Library form | First profile | Elsewhere |
| --- | --- | --- | --- |
| `RotateLeft`/`Right` `u32`, `u64` (and `i32`, `i64` on the pattern) | `BitOperations.RotateLeft` | netcoreapp3.1 | helper |
| `RotateLeft`/`Right` of 8 and 16 bits | `byte.RotateLeft`, `short.RotateLeft`, … | net8.0 | helper |
| Counts of every width, `Parity` | `BitOperations.PopCount`/`LeadingZeroCount`/`TrailingZeroCount`/`Log2` on the pattern | netcoreapp3.1 | helper |
| `ByteSwap` | `BinaryPrimitives.ReverseEndianness` | netstandard2.1 | helper |
| `IsPow2`, `RoundUpPow2` | `BitOperations.IsPow2`, `RoundUpToPowerOf2` | net8.0 | helper |
| `BigMul` `i32` / `u32` | `Math.BigMul` | net2.0 / net10.0 | — / helper |
| `MulHigh` up to 32 bits | the product in the next wider type, shifted | net2.0 | — |
| `MulHigh` of 64 bits | `Math.BigMul(a, b, out _)` | net8.0 | helper (four partial products) |
| `Min`, `Max`, `Abs` and `Sign` of a signed type | `Math.Min`, `Math.Max`, `Math.Abs`, `Math.Sign` | net2.0 | — |
| `Abs`, `AbsWrap`, `Sign` of an unsigned type | the value, a comparison | net2.0 | — |
| `AddCarry`, `SubBorrow`; `CarryOut`, `BorrowOut` up to 32 bits | 64-bit arithmetic | net2.0 | — |
| unsigned overflow tests up to 32 bits, `SubOverflows` of `u64` | 64-bit arithmetic, a comparison | net2.0 | — |
| `LoadLittle`, `LoadBig` | `BinaryPrimitives.Read…LittleEndian`/`BigEndian` on a `ReadOnlySpan<byte>` | netstandard2.1 | helper |
| `BitReverse`, the floor and Euclidean divisions, `Clamp`, signed `AbsWrap`, saturation, the 64-bit carries, the remaining overflow tests, bit fields, masks, stores | — | — | helper in every profile |

`Math.Clamp` is not used: it throws `ArgumentException` for crossed bounds, where the IR traps with
an invalid operation (`ArithmeticException` in generated C#). A store is a helper in every profile:
the span of `BinaryPrimitives.Write…` would check the range before the value is evaluated, where the
IR evaluates every argument first.

### JavaScript

An integer of up to 32 bits is a number; a helper told the width returns the bits of the result and
the call cuts them to the type. A 64-bit integer is a `BigInt` from ES2020 on and a pair `{ hi, lo }`
below; every 64-bit helper has a body for each. The library forms:

| Operation | Form | Profiles |
| --- | --- | --- |
| `LeadingZeroCount`, `Log2` of every width up to 32 bits | `Math.clz32` on the pattern (helper `$clz32` below ES2015) | all |
| `AbsWrap` up to 32 bits | `Math.abs` cut to the type | all |
| `Min`, `Max` up to 32 bits | `Math.min`, `Math.max` | all |
| `Sign` up to 32 bits | `Math.sign` (helper `$sign` below ES2015) | all |
| `MulHigh` up to 16 bits | `Math.floor` of the exact product over 2^width | all |
| carries, saturation and overflow tests up to 32 bits | the exact arithmetic of numbers | all |
| every 64-bit operation | the arithmetic of `BigInt`, cut with `BigInt.asIntN`/`asUintN` | es2020, es2022 |
| every 64-bit operation | a helper on the pair of halves, built on the software arithmetic of the backend | es5, es2015, es2017 |

Everything else is a helper (`$rotln`, `$bswap`, `$brev`, `$rup2`, `$mulhi`, `$divf`, `$clamp`,
`$sat`, `$outside`, `$extract`, `$insert`, `$lowmask`, `$blsi`, `$sext`, `$load`, `$store`, … and
their 64-bit forms), declared only when a module calls it.

## How it is tested

- A bit-by-bit reference in the test project - a ripple-carry adder, a shift-and-add multiplier, a
  restoring divider, comparisons from the top bit down - that shares no code with `IntegerOperations`.
- The 362 signatures, listed from this page rather than from the implementation: every boundary
  combination of their arguments and 1000 random tuples each (seed 2026), 467 274 calls.
- The interpreter, all nine C# profiles (built for their own reference assemblies, run without and
  with overflow checking) and seven JavaScript profiles in `node` (ES5 plain and with typed arrays,
  ES5 as a script, ES2015, ES2017, ES2020, ES2022): 0 mismatches against the reference.
- Every row of the C# table compiles in its first profile and not in the one before.
