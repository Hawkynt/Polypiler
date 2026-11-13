# ADR 0002 — Numeric contracts: integer overflow, shift, division and rounding

Area: foundation

**Status:** accepted · **Date:** 2026-11-13 · **Issue:** [#21](https://github.com/Hawkynt/Polypiler/issues/21) (`ADR-NUMERIC`) · **Index:** [decision records](README.md)

**Decides:** [PRD §4.3](../prd/02-core-architecture.md#s4-3) "checked/wrapping/saturating arithmetic", [§4.4](../prd/02-core-architecture.md#s4-4) "never fabricated proof", §7.2 "silent fallback to wrong arithmetic", §8 P0 "numerical overflow/rounding contracts" and §15 "IR integer signedness policy", "float strict/accurate/fast policy".

Issue names in `code font` (`IR-INT`, `INT-DIVMOD`, …) are the backlog identifiers of the issues that consume this decision: `IR-INT` [#118](https://github.com/Hawkynt/Polypiler/issues/118), `IR-OVERFLOW` [#122](https://github.com/Hawkynt/Polypiler/issues/122), `IR-FLOAT` [#116](https://github.com/Hawkynt/Polypiler/issues/116), `IR-FLOATCONV` [#117](https://github.com/Hawkynt/Polypiler/issues/117), `IR-SERIAL` [#126](https://github.com/Hawkynt/Polypiler/issues/126), `INT-DIVMOD` [#99](https://github.com/Hawkynt/Polypiler/issues/99) and every `FE-*-SCALARS` and `BE-*-SCALARS` issue.

## Question

How does PolyIR state, on each arithmetic instruction, what happens on overflow, on an out-of-range
shift amount, on division by zero and `MIN / -1`, on the sign of a remainder, and on a
float-to-integer or narrowing conversion — as separate opcodes, as a mode annotation on one opcode,
or as flags that an analysis may strengthen?

## Decision

**A mandatory contract annotation on a typed opcode, taken from a closed set** (option 1 below).
`Add.i32[trap OverflowException]` is one instruction: opcode, type, contract.

1. **One opcode per mathematical operation, one contract per edge.** An *edge* is the part of the
   input domain where the mathematical result does not fit the result type or does not exist:
   overflow, a shift amount ≥ the width, a zero divisor, `MIN / -1`, a float that is NaN, infinite or
   out of the integer's range. A contract says what the instruction does on its edge and nothing
   else: two instructions that differ only in a contract agree on every input outside the edge.
2. **What changes the result off the edge, or the shape of the result, is not a contract.** It is a
   *rounding mode* (which representable result is chosen when the exact one is not representable:
   the five IEEE modes for floats, `trunc | floor | euclid` for integer division) or a different
   opcode (`SDiv`/`UDiv`, `AShr`/`LShr`, `AddOverflow` returning value and flag, `AddCarry`,
   `DivMod` returning quotient and remainder).
3. **Contracts are mandatory in the model.** A frontend, a pass or `IrBuilder` that omits one fails
   verification. Only the text parser completes abbreviated listings, from the alias table below; the
   printer always writes the contract.
4. **Contracts come from the source language, not from analysis.** A frontend chooses the contract
   its language profile prescribes. Analyses publish facts through the query facade (PRD §4.4) and
   never annotate instructions. A pass may exchange one contract for another only with a proof that
   the edge is unreachable (a range fact turning `[trap Overflow]` into `[wrap]`, or `[mask]` into
   `[strict]`), under the pass contract of [IR.md](../architecture/IR.md#pass-contract-and-invariants).
   No pass introduces `undefined`; a timeout or `Unknown` exchanges nothing.
5. **Signedness lives in the type and, where the operation itself differs, in the opcode.** `u<N>`
   and `i<N>` are distinct types with identical storage. `trap` and `sat` judge overflow against the
   type's range. Operations whose bit result depends on signedness are separate opcodes (`SDiv`/`UDiv`,
   `SRem`/`URem`, `AShr`/`LShr`, the `ICmp` predicates, `SignExtend`/`ZeroExtend`); they ignore the
   type's signedness. A shift amount is always read as an unsigned number.
6. **Floating point is strict.** Every rounding operation carries exactly one rounding mode; there are
   no fast-math, reassociation or "accurate" flags in the IR. Contraction is the module attribute
   `fp-contract` of IR-FLOAT; tolerance is a verification policy (ADR 0005), never an IR property.

## The closed contract sets

### Integer overflow — `Add`, `Sub`, `Mul`, `Neg`

| Contract | On overflow | Source that needs it |
| --- | --- | --- |
| `wrap` | result modulo 2^N | C# `unchecked`, C unsigned, Rust release, Kotlin, Java, Go, PowerBASIC |
| `trap <label>` | the trap `<label>`; no result | C# `checked`, Rust debug |
| `sat` | the type's minimum or maximum | Rust `saturating_*`, PRD §4.3 |
| `undefined` | no defined behaviour from this instruction on | C and C++ signed arithmetic |

`undefined` is legal only in a module whose source profile declares `undefinedOverflow`; the reference
interpreter answers `UndefinedBehaviourReached` and never a value (IR-OVERFLOW). It is a statement
the *source language* makes. It differs from LLVM's `nsw`/`nuw` in both respects that matter here: no
analysis may add it, and it yields no poison value that flows on.

The wrapped value together with the overflow flag is not a contract but the opcodes `AddOverflow`,
`SubOverflow`, `MulOverflow` (IR-OVERFLOW); carry in and out are `AddCarry`/`SubBorrow` (INT-CARRY).

### Narrowing — `Truncate`

`wrap` (the low N bits; printed bare, `Truncate.u8 %x`), `trap <label>`, `sat` (relative to the source
type's signedness). There is no `undefined`: no language in the table makes integer narrowing
undefined (C: implementation-defined, modular on every supported compiler).

### Shift amount — `Shl`, `LShr`, `AShr`

For a width N and the amount n read as unsigned:

| Contract | When n ≥ N | Source that needs it |
| --- | --- | --- |
| `mask` | shifts by n mod N | C#, JavaScript, Kotlin, Java, Rust release and `wrapping_shl` |
| `unbounded` | every bit is shifted out: 0 for `Shl` and `LShr`, the sign fill for `AShr` | Go, Perl, Rust `unbounded_shl` |
| `trap <label>` | the trap `<label>` | Rust debug |
| `undefined` | no defined behaviour (profile-gated as above) | C, C++ |
| `strict` | cannot happen: the producer asserts n < N | constant amounts in every language |

`strict` is a proof obligation, not a source behaviour. A constant amount ≥ N is a verifier error;
a variable amount must be covered by a range fact before a backend may emit the native shift, and
the reference interpreter stops with the trap `InvalidOperation` if it is not. It is what a frontend
emits for `x << 8`, and what `mask` becomes once the amount is proven in range. `unbounded` is new
with this ADR (the work order's "saturate-to-zero", renamed because `AShr` fills with the sign).

### Division edges — `SDiv`, `SRem`, `UDiv`, `URem`, `DivMod`

| Edge | Contract | Result | Source that needs it |
| --- | --- | --- | --- |
| divisor 0 | `trap <label>` | the trap | C#, Rust, Kotlin, Java, Go, Python, Perl, PowerBASIC |
| | `divzero=zero` | 0, for quotient and remainder alike | JavaScript `(a / b) \| 0`, `(a % b) \| 0` |
| | `divzero=undefined` | no defined behaviour | C, C++ |
| `MIN / -1`, `MIN % -1` (signed only) | `ovf=wrap` | quotient `MIN`, remainder 0 | Kotlin, Java, Go, JavaScript `\| 0`, Rust `wrapping_div` |
| | `ovf=trap <label>` | the trap | C#, Rust `/` and `%` in every build mode |
| | `ovf=undefined` | no defined behaviour | C, C++ |

Both edges apply to the remainder as much as to the quotient: `SRem` carries `ovf` exactly as `SDiv`
does. The unsigned opcodes have no `ovf`.

### Division rounding — the sign of the remainder

The remainder is `r = a − q·b`; its sign follows from how the quotient is rounded, so there is one
rounding attribute and no separate remainder-sign contract.

| Rounding | Quotient | Remainder | `-7, 3` | Where |
| --- | --- | --- | --- | --- |
| `trunc` | toward zero | sign of the dividend | −2, −1 | `SDiv`/`SRem`/`UDiv`/`URem` (fixed, not written); `DivMod[trunc]` |
| `floor` | toward −∞ | sign of the divisor | −3, 2 | `DivMod[floor]` — Python `//` `%`, Perl `%`, Kotlin `floorDiv`/`mod` |
| `euclid` | such that 0 ≤ r < \|b\| | never negative | −3, 2 | `DivMod[euclid]` — Rust `div_euclid`/`rem_euclid` |

The work order's `dividend | divisor | euclidean` are these three under the names INT-DIVMOD already
uses. Ceiling and round-to-nearest division have no source in the table and are not in the set.

### Float → integer — `FloatToInt`

A rounding mode (below) and a range policy. Rounding happens once, from the exact value; the policy
applies to the rounded integer, to NaN and to ±∞.

| Range policy | Out of range | NaN | Source that needs it |
| --- | --- | --- | --- |
| `wrap` | modulo 2^N | 0 (also ±∞) | ECMAScript `ToInt32`/`ToUint32` |
| `sat` with `nan=0` | the type's minimum or maximum | 0 | C# on .NET 9 and later, Rust `as`, Kotlin, Java |
| `sat` with `nan=trap <label>` | minimum or maximum | the trap | none in the table; kept as IR-FLOATCONV specifies it |
| `trap <label>` | the trap | the same trap, or `nan=trap <other>` | C# `checked`, WebAssembly `trunc`, Python (`OverflowError` for ±∞, `ValueError` for NaN) |
| `sentinel <value>` | the value | the value | x86 `CVTTSD2SI`/`FISTP` "integer indefinite", PowerBASIC on DOS |
| `undefined` | no defined behaviour | no defined behaviour | C, C++ |

### Rounding modes

The five rounding-direction attributes of IEEE 754-2019 §4.3, and no others:

| Attribute | IEEE 754-2019 | Meaning |
| --- | --- | --- |
| `rne` | §4.3.1 roundTiesToEven | nearest, ties to the even significand |
| `rna` | §4.3.1 roundTiesToAway | nearest, ties away from zero |
| `rtz` | §4.3.2 roundTowardZero | truncation |
| `rup` | §4.3.2 roundTowardPositive | toward +∞ |
| `rdn` | §4.3.2 roundTowardNegative | toward −∞ |

| Carries a rounding mode | Carries none |
| --- | --- |
| `FAdd`, `FSub`, `FMul`, `FDiv`, `FRem` (exact in every mode; carried for uniformity) | `FNeg`, `FAbs`, `FCopySign`, `FCmp`, `FClass` — bit operations and predicates |
| `IntToFloat` — inexact whenever the integer has more significant bits than the format | `FloatBits`, `BitsToFloat` — reinterpretation |
| `FloatToInt` — a fraction must go somewhere; `rtz` for every C-family cast, `rne` for PowerBASIC stores | `Truncate`, `ZeroExtend`, `SignExtend`, `BitCast` and all integer arithmetic |
| `FloatConvert` — narrowing rounds; widening is exact but carries the mode so that one opcode serves both | |
| `Fma`, `Sqrt` and the other correctly rounded intrinsics (INT-FMA, INT-ROOTS) | |

The mode is static, part of the instruction. A source that changes the rounding direction at run time
(`fesetround`) is outside the first verified subsets and is diagnosed by its frontend. Rounding
*functions* that are not IEEE directions — JavaScript's `Math.round` (2.5 → 3, −2.5 → −2: ties toward
+∞) — are intrinsics or recipes of INT-ROUND, not a sixth mode.

Float arithmetic has one more, optional, contract: `trap <label>` on `FDiv`/`FRem` and friends for
profiles in which division by zero or an invalid operation raises instead of producing ±∞ or NaN
(Python, PowerBASIC), as IR-FLOAT specifies.

## Notation for IR dumps

Contracts go in the one attribute list after the type, in this order; the printer always writes all of
them:

```
%r = Add.i32[wrap] %a, %b                              Add Sub Mul Neg: overflow contract
%r = Mul.i32[trap OverflowException] %a, %b
%r = Add.u8[sat] %a, %b
%r = Add.i32[undefined] %a, %b
%s = Shl.u32[mask] %x, %n                              Shl LShr AShr: amount contract
%s = LShr.u64[unbounded] %x, %n
%s = Shl.i32[strict] %x, 8
%q = SDiv.i32[trap DivideByZero, ovf=wrap] %a, %b      SDiv SRem: zero-divisor contract, ovf
%m = SRem.i32[trap DivideByZeroException, ovf=trap OverflowException] %a, %b
%q = UDiv.u32[divzero=zero] %a, %b                     UDiv URem: zero-divisor contract
%q, %r = DivMod.i64[floor, trap ZeroDivisionError, ovf=wrap] %a, %b
%t = Truncate.u8 %x                                    Truncate: bare, [trap <label>] or [sat]
%i = FloatToInt.i32[rtz, wrap] %d                      FloatToInt: rounding, range policy
%i = FloatToInt.i32[rtz, sat, nan=0] %d
%i = FloatToInt.i32[rne, sentinel -2147483648] %d
%f = IntToFloat.f32[rne] %i                            rounding only
%r = FDiv.f64[rne, trap ZeroDivisionError] %a, %b      rounding, optional trap
```

A trap label is a canonical trap kind (`Overflow`, `DivideByZero`, `InvalidOperation`) or the source
language's own exception type declared in the module's `traps { }` table, as IR-EXCEPT rules.

Input aliases — accepted by the text parser only, never produced by the printer:

| Written | Read as |
| --- | --- |
| `Add`/`Sub`/`Mul`/`Neg` without a contract | `[wrap]` |
| `Shl`/`LShr`/`AShr` without a contract | `[strict]` |
| `SDiv`/`SRem`/`UDiv`/`URem` without a zero-divisor contract | `trap DivideByZero` |
| `SDiv`/`SRem`/signed `DivMod` without `ovf` | `ovf=wrap` |
| `SDiv.i32[wrap]`, `SRem.i32[wrap]` | `[trap DivideByZero, ovf=wrap]` |
| `[checked]` | `[trap Overflow]` on arithmetic and narrowing; `[trap DivideByZero, ovf=trap Overflow]` on `SDiv`/`SRem`; `[trap DivideByZero]` on `UDiv`/`URem` |
| `[trap]` on arithmetic, narrowing and `FloatToInt` | `[trap Overflow]` |
| `[saturate]` · `[modular]` · `round-even` · `nan→0` | `[sat]` · `[wrap]` · `rne` · `nan=0` |
| `FloatToInt` without a rounding mode | `rtz` |
| `FloatToInt` with a rounding mode only | `sentinel <MIN of the destination>` |
| `FAdd`/`FSub`/`FMul`/`FDiv`/`FRem` without a rounding mode | `[rne]` |
| `Add.f64`, `Sub.f64`, `Mul.f64`, `Div.f64`, `Rem.f64`, `Neg.f64` | `FAdd` … `FNeg` |

## Options considered

1. **Mode annotation on a typed opcode — chosen.** Every language cell below maps to one member of
   a set; a rewrite that holds for every contract is written once against the opcode; a rewrite that
   needs a particular contract states it as a precondition on one attribute.
2. **Distinct opcodes per behaviour** (`AddWrap`, `AddChecked`, `AddSat`). Rejected: the sets above
   would turn 3 shift opcodes into 15, 4 arithmetic opcodes into 16 and 4 division opcodes into 24,
   and every contract-independent rewrite (`x + 0`, commutativity, constant folding off the edge)
   would be written per opcode or against an opcode class — which is option 1 with the contract
   moved into the name. Kept exactly where rule 2 demands an opcode.
3. **Wrapping opcodes plus guard instructions.** Rejected as the *model*: the source contract
   survives only while guard and operation stay adjacent, every backend must re-associate them to
   emit `checked(a + b)` or `a.checked_add(b)`, and an overflow guard needs the result recomputed in
   a wider type. Guards remain the right *recipe* where a language tests an operand in a way no
   contract names (a negative shift amount, below).
4. **Poison/undefined flags an analysis may strengthen** (`nsw`, `nuw`, `exact`). Rejected: no
   language in the table needs a deferred poison value, and a flag set by an analysis is a
   fabricated proof once the analysis is wrong (PRD §4.4). What C requires is covered by the
   source-stated, profile-gated `undefined` contract.

### Rotate recognition under each option

The 32-bit rotate of
`Hawkynt/Hawkynt.github.io@b3a8a8e92cbca83475986391c3cac56f0b185d29:Cipher/OpCodes.js` is
`positions &= 31; return ((value << positions) | (value >>> (32 - positions))) >>> 0;`.

| Option | The idiom in IR | Rotate recognition |
| --- | --- | --- |
| 1 | `%n = And.u32 %p, 31` · `%a = Shl.u32[mask] %x, %n` · `%s = Sub.u32[wrap] 32, %n` · `%b = LShr.u32[mask] %x, %s` · `%r = Or.u32 %a, %b` | one pattern `Or(Shl x n, LShr x (N − n))` with a precondition on the attribute: `mask` for every n (at n = 0 the right shift by 32 is a shift by 0, and `x \| x` is x), `unbounded` with the fact n ≤ N (at n = 0 the right shift yields 0, and `x \| 0` is x), `strict` with the fact 1 ≤ n ≤ N − 1 |
| 2 | the same five instructions with `ShlMask`, `LShrMask` | one pattern per opcode pair — `ShlMask`/`LShrMask`, `ShlUnbounded`/`LShrUnbounded`, `ShlStrict`/`LShrStrict`, and the mixed pairs a legalizer produces |
| 3 | `Shl x (And n 31)` · `LShr x (And (Sub 32 n) 31)`, the masks as separate instructions | the pattern must look through the `And` nodes for JavaScript, through a `Select(n ≥ 32, 0, …)` for Go and Perl, and through a compare-and-trap for Rust: one shape per source language |
| 4 | LLVM's shape: `shl`/`lshr` that are poison at n ≥ N, plus explicit `and` | as option 3, and the n = 0 case is poison unless the `and` is found |

The PowerBASIC fixture
`Hawkynt/PB-Compiler@0d73acbea185a2597357d53f7ce9acb44758ba76:docs/decompilation/features/08-shift-rotate-bitwise.bas`
shifts at the left operand's width with constant amounts and has rotate *operators*:

| Source | Option 1 | Measured |
| --- | --- | --- |
| `n << 3` (`n AS INTEGER`) | `Shl.i16[strict] %n, 3` | 8 |
| `&H8000 >> 4` | `AShr.i16[strict] -32768, 4` | −2048 |
| `&H8000 >>> 4` | `LShr.i16[strict] -32768, 4` | 2048 |
| `&H1 <<> 1`, `&H1 <>> 1` | `RotateLeft.i16 1, 1`, `RotateRight.i16 1, 1` (INT-ROTATE; nothing to recognize) | 2, −32768 |

Under option 2 the same lines need `ShlStrict`, `AShrStrict`, `LShrStrict`; under option 3 they are
bare shifts and the fact that the amount was a checked constant is gone.

## Evidence: language × operator

Each cell gives the prescribed behaviour, the normative source and the contract. *Measured* values
are in [`evidence/0002/outputs.txt`](evidence/0002/outputs.txt), produced by
[`evidence/0002/run.sh`](evidence/0002/run.sh) from the probes beside it on 2026-11-13 with
dotnet-sdk 10.0.112, node 22.23.3, gcc 16.2.1, clang 23.1.3, rustc 1.99.0, kotlinc-jvm 2.2.20 on
OpenJDK 27, go 1.27.2, Python 3.13.1, perl 5.42.2 and `pbc` of `Hawkynt/PB-Compiler` (x64 back end),
all on x86-64 Linux. Values use `i32` unless the language has another native width.

Sources: **C#** — ECMA-334 (7th ed.) §12.8.20 checked/unchecked, §12.10 arithmetic operators,
§12.11 shift operators, §10.3.2 explicit numeric conversions. **JS** — ECMA-262 §6.1.6.1
(`Number::add`, `multiply`, `divide`, `remainder`, `leftShift`, `signedRightShift`,
`unsignedRightShift`), §7.1.6 `ToInt32`, §7.1.7 `ToUint32`. **C** — ISO/IEC 9899:2018 §6.2.5 ¶9,
§6.3.1.4 ¶1, §6.5 ¶5, §6.5.5 ¶5–6, §6.5.7 ¶3–5. **Rust** — The Rust Reference, "Arithmetic and
Logical Binary Operators", "Overflow" and "Numeric cast"; `core` documentation of the `wrapping_*`,
`saturating_*`, `unbounded_*` and `*_euclid` methods. **Kotlin** — Kotlin language specification,
"Built-in types and their semantics"; `kotlin.Int` API (`shl`, `toInt`, `mod`, `floorDiv`); on the JVM the Java
Language Specification §15.17.2, §15.17.3, §15.19, §5.1.3. **Python** — Language Reference §3.2
(`numbers.Integral`), §6.7, §6.8; Library Reference, `int`. **Perl** — `perlop` ("Shift Operators",
"Multiplicative Operators", "Integer Arithmetic"), `perlnumber`. **Go** — The Go Programming Language
Specification, "Arithmetic operators" ("Integer operators", "Integer overflow") and "Conversions".
**PowerBASIC** — no standard exists; the PowerBASIC 3.5 Reference Guide (entries `\`, `MOD`,
`SHIFT`, `CLNG`, run-time error 11) and, as the backlog's PowerBASIC oracle, `pb-compiler`.
**6502** — MOS Technology, *MCS6500 Microcomputer Family Programming Manual* (1976), chapter 2,
"ADC — Add Memory to Accumulator with Carry" and "SBC — Subtract Memory from Accumulator with
Borrow".

### `+ - *` overflow

| Language | Prescribed | Measured (`MAX + 1`, `65536 * 65536`) | Contract |
| --- | --- | --- | --- |
| C# `unchecked` (§12.8.20) | high-order bits discarded | −2147483648, 0 | `wrap` |
| C# `checked` | `System.OverflowException` | `OverflowException` | `trap OverflowException` |
| JavaScript Number (§6.1.6.1.7) | binary64 arithmetic, no integer overflow; `\| 0` is `ToInt32` | 2147483648; with `\| 0` −2147483648 | `FAdd.f64[rne]`, then `FloatToInt.i32[rtz, wrap]` |
| PowerBASIC `INTEGER`/`LONG` | wraps | −32768 (16 bit), −2147483648, 0 | `wrap` |
| C unsigned (§6.2.5 ¶9) | reduced modulo 2^N | 0, 0 | `wrap` |
| C signed (§6.5 ¶5) | undefined | sanitizer: "signed integer overflow"; unsanitized −2147483648 | `undefined` |
| Rust, debug (Reference, "Overflow") | panic | "attempt to add with overflow" | `trap Overflow` |
| Rust, release | two's complement wrap | −2147483648, 0 | `wrap` |
| Rust `saturating_add` / `overflowing_add` | clamp / value and flag | 2147483647 / (−2147483648, true) | `sat` / `AddOverflow` |
| Kotlin `Int`, `UInt` | wraps (JLS §15.18.2, §15.17.1) | −2147483648, 0 | `wrap` |
| Python `int` (§3.2) | unlimited range, no overflow | 2147483648, 4294967296 | none: the type is the big integer of IR-BIGINT |
| Perl (`perlop`, "Integer Arithmetic") | integer while the result fits `IV` or `UV`, otherwise a double | `IV_MAX + 1` = 9223372036854775808 (exact), `IV_MAX * 2` = 18446744073709551614 (exact), `UV_MAX + 1` = 1.84467440737096e+19 | recipe on `AddOverflow` at `i64` and `u64`, see below |
| Perl, `use integer` | native signed wrap | −9223372036854775808, −2 | `wrap` |
| 6502 `ADC`/`SBC`, binary mode | A + M + C modulo 256, carry out in C, signed overflow in V | not run | `AddCarry.u8` (INT-CARRY); V is its signed-overflow flag |

### `<<` `>>` with an amount ≥ the width, and a negative amount

| Language | Prescribed | Measured (`1 << 32`, `1 << 33`, `-8 >> 33`, `1 << -1`) | Contract |
| --- | --- | --- | --- |
| C# (§12.11) | amount `& 0x1F` (`& 0x3F` for 64 bit) | 1, 2, −4, −2147483648 | `mask` |
| JavaScript (§6.1.6.1.9–11) | amount modulo 32 | 1, 2, −4, −2147483648 | `mask` |
| Kotlin `shl`/`shr`/`ushr` (JLS §15.19) | five (six) low bits of the amount | 1, 2, −4, −2147483648 | `mask` |
| C (§6.5.7 ¶3) | undefined when negative or ≥ width | sanitizer: "shift exponent 32 is too large", "shift exponent -1 is negative" | `undefined` |
| Rust, debug | panic | "attempt to shift left with overflow" (all four) | `trap Overflow` |
| Rust, release; `wrapping_shl` | amount masked | 1, 2, −4, −2147483648 | `mask` |
| Rust `unbounded_shl`/`unbounded_shr` | all bits shifted out | 0, 0, −1 | `unbounded` |
| Go ("Integer operators") | no upper limit, "as if shifted n times by 1"; a negative amount panics | 0, 0, −1, panic "negative shift amount" | `unbounded`; the negative test is a guard |
| Python (§6.8) | on unlimited integers; a negative amount raises `ValueError` | 4294967296, 8589934592, −1, `ValueError` | `unbounded` on the big integer; the negative test is a guard |
| Perl (`perlop`, "Shift Operators") | ≥ width gives 0 (−1 for a negative value under `use integer`); a negative amount shifts the other way | `1 << 64` = 0, `8 >> 64` = 0, `use integer; -8 >> 64` = −1, `1 << -1` = 0, `8 >> -1` = 16 | `unbounded` at `u64` (`i64` under `use integer`); the direction is a `Select` |
| PowerBASIC `SHIFT`, `<<` | x64 back end: all bits shifted out; DOS build: amount masked to five bits | x64: 0, 0, −1 (`>>`), 1 (`SHIFT LEFT 1, -1`) | `unbounded` (x64); `And` with 31, then `unbounded` (DOS) — see open points |
| 6502 `ASL`/`LSR`/`ROL`/`ROR` | one position per instruction | not run | `strict` with the constant 1 |

### `/` and `%` by zero, `MIN / -1`, `MIN % -1`

| Language | Prescribed | Measured (`7 / 0`, `7 % 0`, `MIN / -1`, `MIN % -1`) | Contract |
| --- | --- | --- | --- |
| C# (§12.10.3, §12.10.4) | `DivideByZeroException`; `MIN / -1` overflows — `ArithmeticException` when `checked`, implementation-defined otherwise; `%` throws exactly when `/` does | `DivideByZeroException` ×2, `OverflowException` ×2 (in `unchecked`) | `trap DivideByZeroException, ovf=trap OverflowException` |
| JavaScript Number (§6.1.6.1.5–6) | ±∞ and NaN; `ToInt32` of both is 0 | `Infinity`, `NaN`; with `\| 0`: 0, 0, −2147483648, 0 | float: `FDiv.f64[rne]`; narrowed to `i32`: `divzero=zero, ovf=wrap` |
| JavaScript BigInt | `RangeError` | `RangeError` | `trap RangeError` |
| PowerBASIC `\`, `MOD` | run-time error 11 | "Error 11" ×2, −2147483648, 0 | `trap DivideByZero, ovf=wrap` |
| C (§6.5.5 ¶5–6) | undefined; if `a / b` is not representable both `a / b` and `a % b` are undefined | sanitizer: "division by zero", "division of -2147483648 by -1 cannot be represented"; unsanitized: `SIGFPE` ×4 | `divzero=undefined, ovf=undefined` |
| Rust (Reference, "Overflow") | panic in every build mode | "attempt to divide by zero", "attempt to divide with overflow", "attempt to calculate the remainder with overflow" — debug and release | `trap DivideByZero, ovf=trap Overflow` |
| Rust `wrapping_div`/`wrapping_rem` | wrap | −2147483648, 0 | `trap DivideByZero, ovf=wrap` |
| Kotlin (JLS §15.17.2–3) | `ArithmeticException`; `MIN / -1` is `MIN`, no exception | `ArithmeticException` ×2, −2147483648, 0 | `trap ArithmeticException, ovf=wrap` |
| Go ("Integer operators") | run-time panic; `MIN / -1` is `MIN` | "integer divide by zero" ×2, −2147483648, 0 | `trap DivideByZero, ovf=wrap` |
| Python (§6.7) | `ZeroDivisionError`; no overflow | `ZeroDivisionError` ×2, 2147483648, 0 | `trap ZeroDivisionError` on the big integer |
| Perl (`perlop`) | dies | "Illegal division by zero", "Illegal modulus zero" | `trap DivideByZero`; `/` itself is `FDiv.f64`, `%` is `DivMod[floor]` |

### Remainder sign — `-7 % 3` and `7 % -3`

| Language | Prescribed | Measured | Rounding |
| --- | --- | --- | --- |
| C# (§12.10.4), JavaScript (§6.1.6.1.6), C (§6.5.5 ¶6), Rust, Kotlin (JLS §15.17.3), Go, PowerBASIC `MOD` | quotient truncated; sign of the dividend | −1, 1 | `trunc` — `SRem` |
| Python `%`, `//` (§6.7) | floored; sign of the divisor | 2, −2; `-7 // 3` = −3 | `floor` — `DivMod[floor]` |
| Perl `%` (`perlop`, "Multiplicative Operators") | sign of the right operand | 2, −2 | `floor` — `DivMod[floor]` |
| Perl `%` under `use integer` | the C compiler's `%` | −1, 1 | `trunc` — `SRem` |
| Rust `rem_euclid`, `div_euclid` | 0 ≤ r < \|b\| | 2, 1; `div_euclid(-7, 3)` = −3 | `euclid` — `DivMod[euclid]` |
| Kotlin `mod`, `floorDiv`; Java `floorMod` | floored | 2; −3 | `floor` — `DivMod[floor]` |

### Float → integer, out of range and NaN

| Language | Prescribed | Measured (`3e9`, `-3e9`, NaN, +∞ to `i32`) | Contract |
| --- | --- | --- | --- |
| C# `unchecked` cast (§10.3.2) | unspecified value; saturating on .NET 9 and later | 2147483647, −2147483648, 0, 2147483647 | `rtz, sat, nan=0` — pinned by the profile's runtime version |
| C# `checked` cast | `OverflowException` | `OverflowException` (also for NaN) | `rtz, trap OverflowException` |
| C# `(byte)300.0` | via `int` | 44 | `FloatToInt.i32[rtz, sat, nan=0]`, then `Truncate.u8` |
| JavaScript `\| 0`, `>>> 0` (§7.1.6, §7.1.7) | truncate, modulo 2^32; NaN and ±∞ give 0 | −1294967296, 1294967296, 0, 0 | `rtz, wrap` |
| PowerBASIC store to `LONG` | rounds to nearest even (2.5 → 2, 3.5 → 4) | x64 back end: `3E9` → −1294967296 | `rne, wrap` (x64); `rne, sentinel MIN` (DOS, per FE-PB-SCALARS) — see open points |
| C (§6.3.1.4 ¶1) | undefined | sanitizer: "3e+09 is outside the range of representable values of type 'int'" (all four); unsanitized −2147483648 ×4 | `rtz, undefined` |
| Rust `as` (Reference, "Numeric cast") | saturates; NaN is 0 | 2147483647, −2147483648, 0, 2147483647; `300.0 as u8` = 255 | `rtz, sat, nan=0` |
| Kotlin `toInt()` (JLS §5.1.3) | saturates; NaN is 0 | 2147483647, −2147483648, 0, 2147483647 | `rtz, sat, nan=0` |
| Go ("Conversions") | implementation-dependent when the value does not fit | −2147483648 ×3 on amd64 | pinned per target by the profile; `rtz, sentinel MIN` as measured on amd64 |
| Python `int(x)` | exact, unlimited; `ValueError` for NaN, `OverflowError` for ±∞ | 3000000000, −3000000000, `ValueError`, `OverflowError` | `rtz, trap OverflowError, nan=trap ValueError` to the big integer |
| Perl `int(x)` | truncates and stays a number: no integer conversion | 3000000000, −3000000000, `int(1e30)` = 1e+30, NaN, Inf | the float rounding intrinsic of INT-ROUND, not `FloatToInt` |

### Cells that are one contract plus a recipe

No cell is inexpressible. These need ordinary IR around the contract, owned by the issue named:

| Cell | Recipe | Owner |
| --- | --- | --- |
| Go, Python: negative shift amount | compare the amount as signed, `Trap` with the language's label, then the `unbounded` shift | FE-GO-SCALARS, FE-PY-SCALARS |
| Perl: negative shift amount | `Select` between the shift and the opposite shift by the negated amount | FE-PL-SCALARS |
| Perl: `+ - *` leaving the integer range | in outline: `AddOverflow.i64`; on overflow `AddOverflow.u64` when the exact result is non-negative; on that overflow `FAdd.f64[rne]` of the converted operands | IR-OVERFLOW (`perl` profile row) |
| Perl: number to integer for `\| & ^ ~ << >>` | negative operands through `FloatToInt.i64[rtz, sat, nan=0]` and `BitCast.u64`, others through `FloatToInt.u64[rtz, sat, nan=0]` (measured: `1e30 \| 0` = 18446744073709551615, `-1.5 \| 0` = 18446744073709551615, NaN → 0) | FE-PL-SCALARS |
| C#: float to an integer narrower than 32 bits | `FloatToInt` to `i32`, then `Truncate` | FE-CS-SCALARS |
| JavaScript: integer arithmetic | only as a recognition over `FloatToInt[rtz, wrap]` of a float operation, when the float result is exact: `(0x7fffffff * 0x7fffffff) \| 0` is 0 but `Math.imul` gives 1 | FE-JS-SCALARS, de-dynamicization |
| 6502 `ADC`/`SBC` in decimal mode | binary-coded-decimal correction of the `AddCarry` result | ISA-6502-SEM-ADC |

## Consequences

Where the consuming issues disagreed with each other or with the measurements, this ADR decides:

| Issue | Change |
| --- | --- |
| IR-INT | The shift set becomes `mask`, `unbounded`, `trap <label>`, `strict` (plus `undefined` from IR-OVERFLOW); the amount is read as unsigned. |
| IR-INT | `SRem` carries `ovf` like `SDiv`. IR-INT fixed "`SRem MIN, -1` is 0" while IR-OVERFLOW expands `SRem[checked]` to `ovf=trap Overflow`; C# and Rust trap (measured) and C leaves it undefined, so IR-OVERFLOW's reading stands. |
| IR-INT | `divzero=zero` yields 0 for `SRem`/`URem` too; the printer writes `ovf=` on every signed division; a bare `wrap` on `SDiv`/`SRem` (used by BE-C-SCALARS, BE-CS-SCALARS, BE-RS-SCALARS, BE-PB-SCALARS) is registered as an input alias. |
| INT-DIVMOD | Zero-divisor and overflow attributes take the core spelling and a label: `trap <label>`, `divzero=zero`, `divzero=undefined`, `ovf=wrap`, `ovf=trap <label>`, `ovf=undefined` (it has `zero=trap\|zero` and a label-less `ovf=trap`). `divzero=zero` returns (0, 0), not (0, a): the only source with a defined result is ECMAScript, where both are 0, and one attribute must not mean two things on `SRem` and on `DivMod`. |
| IR-OVERFLOW | The `perl` recipe `promote-to-float` is wrong between `IV_MAX` and `UV_MAX`: Perl stays exact in an unsigned integer there (measured above), so `IV_MAX + 1` is the `u64` 9223372036854775808, not the `f64` 0x43E0000000000000. The recipe gains the `u64` step. |
| IR-OVERFLOW | The profile table states shifts and division too: `rust` debug `Shl[trap Overflow]`, release `[mask]`, `/` and `%` `ovf=trap Overflow` in both; `csharp` `ovf=trap OverflowException` in both contexts on the pinned runtime. |
| IR-FLOATCONV | `nan=trap <label>` may accompany `trap <label>` (Python raises two different exceptions); `RangePolicy` lists `Undefined`, which its scope already names. |
| IR-FLOAT | `FNeg`, `FAbs`, `FCopySign` carry no rounding attribute (the scope sentence reads as if they did). |
| IR-SERIAL, `backlog/README.md` | The alias table above is the complete list; the "IR notation" paragraph names `unbounded` beside `mask` and `strict`. |
| FE-RS-SCALARS, FE-GO-SCALARS | May use `Shl[trap Overflow]` and `[unbounded]` in place of assert-plus-`[strict]` and compare-and-select. Both older forms remain valid IR. |

The contract sets are recorded in `spec/ir/` and `spec/types/` when SPEC-IR
([#53](https://github.com/Hawkynt/Polypiler/issues/53)) and SPEC-TYPES
([#61](https://github.com/Hawkynt/Polypiler/issues/61)) create those directories.

Backends inherit a plain rule: emit the target's native operator only when its behaviour on the edge
equals the contract, or a fact excludes the edge; otherwise legalize or report the missing capability.
A set grows only by a superseding ADR that names the language cell requiring the new member.

## Open points

- **PowerBASIC was measured on the x64 back end of `pbc` only**; no DOS emulator is installed here.
  FE-PB-SCALARS records the DOS build as masking shift amounts to five bits and storing −2147483648
  for an out-of-range real, where the x64 back end shifts everything out and wraps. Each behaviour
  is in the sets above; which one the `powerbasic` profile takes is FE-PB-SCALARS's to settle
  against the DOS oracle.
- **The 6502 row is cited, not run**: the work order lists no 6502 toolchain, and the instruction
  semantics are verified by ISA-6502-ORACLE.
- **Kotlin** was compiled with a `kotlinc` 2.2.20 downloaded for this run; it is not pinned in
  `tooling/toolchains.lock` yet (INFRA-TOOLCHAINS).
