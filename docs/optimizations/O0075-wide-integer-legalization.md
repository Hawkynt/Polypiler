# O0075 — Legalize integers wider than the target's native width into limbs

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** `WideIntegerLibrary` (`Polypiler.Core.Legalize`) legalizes the 64-bit
> integers of a JavaScript profile below ECMAScript 2020, which has no `BigInt`, into two 32-bit limbs
> ([#1358](https://github.com/Hawkynt/Polypiler/issues/1358)). Wider integers, other limb widths and other
> targets stay [#263](https://github.com/Hawkynt/Polypiler/issues/263). It is a legalization and cannot be
> switched off.

## What it does

JavaScript's bitwise integers are 32 bits wide, 16-bit DOS code has 16-bit registers, a 6502 has eight, and
even 64-bit targets meet 128-bit products and `UInt4096`. PRD §21.3 models this as `Integer.Wide(bits)`;
§8 P2 needs it at once ("PB integer precision", Cipher's 64-bit hashes translated to JavaScript and back).
A wide operation must be decomposed into limbs with explicit carries — and only integers get carries: PRD
§4.7 insists that `UInt4096`, `BitVector4096` and `Vector4096<byte>` "cannot share arithmetic semantics by
fiat".

The prototype splits `i64` and `u64` into two `u32` limbs. The IR has two operations for them:
`HighWord` takes the high limb out of an `i64`, `JoinWords` makes an `i64` of a high and a low limb; the low
limb is the conversion to `u32`. Every other operation on 64 bits is a helper of the library
(`Polypiler.Core.IR/Helpers/Operations/Limbs.cs`) written with those and with `uint` and `int` alone, and
legalization replaces the operation by the call of its helper, which it copies into the module:

| Operation | Helper |
| --- | --- |
| `+`, `-`, unary `-`, `~`, `&`, `\|`, `^` | `Add64`, `Subtract64`, `Negate64`, `Not64`, `And64`, `Or64`, `Xor64` |
| `<<`, `>>`, `>>>` (count modulo 64) | `ShiftLeft64`, `ShiftRightArithmetic64`, `ShiftRightLogical64` |
| `==`, `!=`, the orders, generic equality and order | `Equal64`, `CompareSigned64`, `CompareUnsigned64` |
| `*` | `Multiply64`: four partial products, the upper half of the low one by `MulHighUnsigned32` |
| `/`, `%` | `DivideSigned64`, `RemainderSigned64`, `DivideUnsigned64`, `RemainderUnsigned64`: bit by bit |
| checked `+`, `-`, `*`, unary `-` | `AddChecked*64`, `SubtractChecked*64`, `MultiplyChecked*64` |
| checked conversion from 64 bits | `CheckRangeSigned64`, `CheckRangeUnsigned64`, then the low limb |
| from a narrower integer | `Widen64` for a signed one, `JoinWords(0, x)` for an unsigned one |
| to and from `double` and `float` | `ToDouble64`, `ToSingle64` (rounded once), `FromDouble64`, `FromDoubleChecked64` and their unsigned forms |
| a step of an array element | `StepElement64`, `StepElementChecked64` and their unsigned forms |
| `HasFlag` of an enum of 64 bits | `HasFlag64` |

A step of a variable, or of an element of a view held in a variable, whose value is used is the call of a
lambda that stores the helper's result, as the IR has no assignment that is an expression.

What the profile writes itself is the representation: a 64-bit integer is the object `{ hi, lo }` of its
limbs, `HighWord` is `.hi`, `JoinWords` the object literal, the low limb `.lo`, a change of signedness
changes nothing. A switch over a 64-bit value compares the text of the limbs, a record compares the two
limbs of a field, a keyed collection keys by their text.

The helpers are then optimised like the program: inlining (O0006), propagation (O0003), folding (O0001)
and dead-code removal (O0005) specialise them to their calls, so that an operand whose limb is a constant
costs nothing.

## Example

`polypiler translate Ex.cs --from csharp --to javascript --profile es5` on

```csharp
public static long F(long x) => x + 0x100000000L;
```

### Before

With `--disable-opt O0006`, the call of the library's add:

```javascript
function F(x) {
  return Add64(x, { hi: 1, lo: 0 });
}

function Add64(left, right) {
  var low = (left.lo + right.lo) >>> 0;
  return { hi: (((left.hi + right.hi) >>> 0) + ((low < left.lo) ? 1 : 0)) >>> 0, lo: low };
}
```

### After

By default: the add is inlined and specialised to the constant high limb; no 64-bit helper is called, and
`F({ hi: 0, lo: 1 })` is `{ hi: 1, lo: 1 }`, 4294967297:

```javascript
function F(x) {
  var low = x.lo;
  var add64 = { hi: (((x.hi + 1) >>> 0) + ((low < x.lo) ? 1 : 0)) >>> 0, lo: low };
  return add64;
}
```

## When it applies

- The profile has no `BigInt` (ECMAScript 5 to 2019) and the capability table replaces it with the pairs of
  halves. A table without that replacement has no 64-bit integer at all, which the backend reports.
- The node is an operation on `i64` or `u64`, or on an enum of 64 bits: a binary or unary operator, a
  conversion from or to a 64-bit integer other than the low limb and the change of signedness, a step, a
  generic equality or order, `HasFlag`.
- Inside a helper of the library the operation the helper computes is the helper's own form and is kept;
  every other operation on 64 bits in it is legalized as in the program, so the library's helpers on 64
  bits - `RotateLeft64`, `PopCount64`, the float library - run on the limbs too.

## How it is proven

`WideIntegerLegalizationTests`: every operator, conversion and step, on the edges 0, ±1, 2^31, 2^32,
2^63 − 1 and −2^63 against each other and on random pairs (seed 1358), gives in the interpreter the value or
the trap of the operation, legalized and after the passes. `HelperSourceTests`: every helper, compiled by
the C# compiler, computes what its import computes. `WideIntegerScenarioTests` (acceptance): `node` agrees
with the interpreter on every call at ECMAScript 5, with the passes and without the inlining. The proof
obligations of the target state stay [#263](https://github.com/Hawkynt/Polypiler/issues/263).

## When it must not fire

- Carry by signed comparison: `0x00000000_FFFFFFFF + 1` — the low limb wraps to 0 and `0 < 0xFFFFFFFF` must be evaluated unsigned; signed it is false (−1) and the carry is lost. `Add64` compares the `uint` limbs.
- Limb shift by `n`: `x << 32` implemented as `hi = lo << 32` on a target whose shifts mask the amount gives `hi = lo << 0`. The shift helpers take a count of 32 or more as a whole-limb move and a count of 0 as the value.
- Low × low overflow: `0xFFFFFFFF × 0xFFFFFFFF` needs 64 bits (`0xFFFFFFFE_00000001`); a 32-bit multiply keeps only `0x00000001`. `Multiply64` adds the upper half of that product.
- Signed comparison of `−1` (`0xFFFFFFFF_FFFFFFFF`) and `0`: comparing the top limb unsigned says −1 is larger. `CompareSigned64` compares the high limbs as `int`.
- The smallest value divided by −1, and its remainder: both trap, as the IR's division does, after the check of the divisor.
- A profile with `BigInt`: the operations are the level's own, nothing is legalized.

## Switching it off

It cannot be: without the limbs a profile without `BigInt` has no form for a 64-bit operation, so the
legalization runs at every `--opt` level including `none`, and `--disable-opt O0075` is refused as a
number the pipeline does not have. Switching off the inlining (`--disable-opt O0006`) keeps the calls of
the helpers.

## Interactions with other passes

Runs with the rest of legalization, before the passes over the legalized module, which specialise the
helpers it copied in (O0006, O0003, O0001, O0005, O0023); a helper whose every call was inlined is removed
and its licence notice kept. The traps of the 32-bit arithmetic are helpers of the library in the same way
(`TrapLibrary`), and the checked operations of 64 bits a profile with `BigInt` writes are those helpers too.
