# ADR 0005 — Floating-point tolerance policy

Area: foundation

| | |
| --- | --- |
| Status | accepted |
| Index | [decision records](README.md) |
| Date | 2026-11-13 |
| Issue | [#18](https://github.com/Hawkynt/Polypiler/issues/18) |
| Requirements | [PRD §1.4](../prd/01-product-and-targets.md#s1-4), [§7.2](../prd/03-behavior-and-testing.md#s7-2), §11.2 and §15 in [part 4](../prd/04-roadmap-and-delivery.md) |

## Question

When a floating-point result of generated code is compared with its oracle, which operations must be
bit-exact, which may differ, by what measure and by how much — and how is that declared per fixture
and logged in the manifest so that a difference is never ignored wholesale?

## Decision

1. **Exact by default, tolerance per operation.** An output is compared under a tolerance only when
   it is the direct result of an operation whose contract is a tolerance. Everything else is compared
   bit for bit. A fixture cannot grant itself a tolerance for a computation that contains no such
   operation.
2. **Two measures, never mixed.** *Reference error* is the distance of a result from the exact real
   value, in ULP. *Comparison distance* is the number of representable values between two results.
   An `accuracy` contract is a reference error; a comparison bound is a distance, and it follows from
   the contracts of both sides by a formula.
3. **Four comparison modes** — `exact`, `ulp`, `relative`, `absolute` — and three special-value rules
   — `bitwise`, `signed-zero` (default), `class`.
4. **Three profiles** — `strict`, `accurate` (default), `fast` (reserved).
5. **A native math function gets the slack its measured error justifies and no more.** A runtime
   function without a measurement has no slack; one whose error exceeds 4 ULP is not accepted.
6. **Every tolerance is named and logged.** The manifest records the profile, the operations that
   earned a tolerance, the bound applied, and how many results passed without being identical.

The per-function *reference bounds* are not set here. They stay with the operations
([#650](https://github.com/Hawkynt/Polypiler/issues/650) and the intrinsic issues).

## Definitions

A format has precision `p` and minimum exponent `emin`: `f32` has `p = 24`, `emin = −126`; `f64` has
`p = 53`, `emin = −1022`.

**ULP.** For a real `x ≠ 0` with `2^e ≤ |x| < 2^(e+1)`, `ulp(x) = 2^(max(e, emin) − p + 1)`: the
spacing of the format's values in the binade that contains `x`, and the spacing of subnormals below
the smallest normal. `ulp(0)` is the smallest subnormal. This is the definition of Muller, "On the
definition of ulp(x)" (INRIA RR-5504, 2005), also used by Gladman et al. (see [Published accuracy](#published-accuracy)).

**Reference error** of a computed value `c` against the exact real result `x`:
`err(c, x) = |c − x| / ulp(x)`, a real number. With the exact result given as an adjacent pair
`lo ≤ x ≤ hi` and its position `frac` between them, `err = |(c − lo) / (hi − lo) − frac|` — the
formula of the reference-vector harness, unchanged. Two consequences are part of the definition:

- *At a binade boundary* the unit is the spacing at the exact result. For `x` just above `2^k`, the
  value just below `2^k` is half a unit away, not one.
- *Across zero* the unit is the smallest subnormal on both sides, so the measure is continuous.

An exact result beyond the largest finite value, a NaN and an exact zero have no reference error;
they are special values (below).

**Comparison distance** of two finite values `a`, `b` of one format. Let `ord(v)` be the magnitude
bits of `v` read as an integer, negated when the sign bit is set. Then `d(a, b) = |ord(a) − ord(b)|`,
an integer: the number of steps from one representable value to the next that separate `a` and `b`.

- *Across zero* both zeros have `ord = 0`, so `d(−min, +min) = 2` and `d(−0, +0) = 0`. The sign of a
  zero is not a distance; it is checked by the special-value rule.
- *At a binade boundary* a step is a step, whatever its size: `d(pred(2^k), 2^k) = 1`.
- It needs no floating-point arithmetic and costs the same as a bit comparison (measured below).

**From contracts to a bound.** If `err(a, x) ≤ m` and `err(b, x) ≤ n` for integers `m, n ≥ 1`, then

```
d(a, b) ≤ m + n + max(m, n)
```

Inside a binade the two values are at most `m + n` steps apart. The third term covers a result at a
power of two: a value `m` units below `2^k` is `2m` steps below it, because the spacing halves
there. `1 − 2^−52` and `1 + 2^−52` are both within 1 ULP of the exact result 1 and are 3 steps
apart. The bound is sound and attained: `docs/adr/evidence/0005/lemma.py` checks it exhaustively in
a 4-bit format for `m, n ≤ 4`. A comparison under it never fails for two implementations that keep
their contracts.

## Comparison modes

| Mode | Passes when | Bound |
| --- | --- | --- |
| `exact` | the two bit patterns are identical, subject to the special-value rule | none |
| `ulp` | `d(candidate, reference) ≤ bound` | positive integer |
| `relative` | `\|candidate − reference\| ≤ bound · \|reference\|` | positive real |
| `absolute` | `\|candidate − reference\| ≤ bound` | positive real |

`relative` and `absolute` are evaluated in exact rational arithmetic on the two values, so the
comparison itself cannot round in favour of a pass; `reference` is the oracle's value.

In every mode other than `exact`, both outputs are sequences of one declared format with equal
length, and the special-value rule is applied first.

## Special values

| Rule | NaN | Zero |
| --- | --- | --- |
| `bitwise` | sign, quiet bit and payload compared | `+0` and `−0` differ |
| `signed-zero` (default) | any NaN equals any NaN | `+0` and `−0` differ |
| `class` | any NaN equals any NaN | `+0` equals `−0` |

- **NaN is compared by class unless a fixture asks for more.** IEEE 754-2019 does not specify the
  sign or the payload of a NaN that an invalid operation produces (§6.2, §6.3), and the runtimes use
  that freedom: the same
  `log(−1)` returned `fff8000000000000`, `7ff4000000000000` and `7ff8000000000001` in the
  measurement below. `bitwise` is for fixtures whose subject is the encoding (NaN boxing, payload
  propagation under the IR's `nan(propagate)` contract, codec round trips).
- **The sign of zero is always compared**, in `ulp` mode as well. IEEE 754 §6.3 fixes it for every
  arithmetic operation and the intrinsic issues fix it for every function, so a wrong sign is a
  wrong result, not a rounding effect. A zero and a non-zero within the bound still match: that is
  underflow. `class` is for a source language that does not keep the sign — Perl returned `+0` for
  `−0.0 × 1` — and must be declared by the fixture.
- **An infinity matches only the same infinity**, in every mode. A NaN never matches a number. A
  finite result within the bound of the overflow threshold is therefore not a usable vector, and
  vector generators leave such inputs out.
- **`−0.0 == 0.0`** is a comparison *operation*; its result is a boolean and is compared exactly.
- A source runtime that raises an error where IEEE 754 returns a value (Python `math.log(0)`, Perl
  `1 / 0`) has produced a trap outcome, which is compared by trap class and is outside this policy.
- The special-value rule applies to elements whose image type is `f32` or `f64`. Images without a
  declared type are bytes and are compared as such.

## Per-operation table

| Operations | Contract | Compared | Basis |
| --- | --- | --- | --- |
| `FAdd`, `FSub`, `FMul`, `FDiv`, `FRem`, `Sqrt`, `Fma`, the rounding family, conversions between formats and to and from integers | exact | `exact` | IEEE 754-2019 §5.4.1, §5.3.1, §5.4.2 require the correctly rounded result |
| `FNeg`, `FAbs`, `FCopySign`, `FCmp`, `FClass`, minimum and maximum | exact | `exact` | bit operations and predicates |
| `Square`, `Cube`, `ReciprocalSqrt` | exact | `exact` | defined as fixed sequences of the operations above |
| Every operation with `accuracy=ulp(n)` — `Sin`, `Cos`, `Tan`, `Exp`, `Log`, `Pow`, `Cbrt`, `Hypot`, the inverse and hyperbolic families, … | tolerance | `ulp`, bound from the formula | IEEE 754-2019 §9.2 only recommends correct rounding; no measured runtime provides it for all of them |
| The same with `accuracy=cr` | exact | `exact` | the contract is the correctly rounded value |
| The same with `accuracy=library(<name>)` | exact against that library, otherwise not comparable | `exact` | the contract is "whatever the named library returns" |
| Exact results of a tolerance operation listed as such by its issue (`Exp(±0) = 1`, `Cbrt(27) = 3`) | exact | `exact` | vector class `exact` |

"Exact" means exact in the format the IR states. The x87 stack computing `f64` arithmetic with a
64-bit significand is a different computation — in the measurement it differed from the correctly
rounded `f64` result in 1 to 3 of 10 000 additions, multiplications, divisions and square roots — and
is modelled by the extended type, not excused by a tolerance.

### Accuracy contracts

The spellings of [#650](https://github.com/Hawkynt/Polypiler/issues/650) are adopted unchanged:

- `ulp(n)` — reference error at most `n`, `n` a positive integer; the default, with `n` the reference
  bound stated by the operation's issue.
- `cr` — correctly rounded: reference error at most ½ with ties to even; bit-exact.
- `library(<name>)` — identical to the named external library on the source platform.

## Profiles

A translation runs under one profile; the manifest records it.

| Profile | Tolerance operations | Arithmetic | Comparison |
| --- | --- | --- | --- |
| `strict` | default contract is `cr`; `ulp(n)` is refused | as written | `exact` everywhere |
| `accurate` (default) | default contract is `ulp(n)` | as written | `exact`, and `ulp` for direct results of tolerance operations |
| `fast` | as `accurate` | value-changing licences (reassociation, contraction beyond the source) | bounds declared by the user |

- `strict` is adopted. It needs a correctly rounded implementation per function; until one is
  registered, and for an explicit `ulp(n)`, the operation fails with the existing "accuracy contract
  not available" diagnostic (`PLY0262`). The implementation is a follow-up.
- `fast` is a reserved name. The IR evaluates operations as written, [ADR 0002](0002-numeric-contracts.md)
  keeps it free of fast-math flags and PRD §4.1 forbids unproven reassociation, so no licence exists and the profile cannot be selected yet. A later decision that
  introduces a licence must keep two rules fixed here: every bound under `fast` is declared by the
  user per output, and a `fast` result is never reported as exact.

How a profile is selected on the command line belongs to the command-line decision.

## From the IR to the tolerance of a fixture

The verification pipeline derives the comparison from the IR the frontend produced, before
optimization:

1. **No tolerance operation → `exact`.** A tolerance declared for such a fixture is an error, not a
   setting.
2. **An output that is the direct result of a tolerance operation → `ulp`** with the bound of that
   operation's tolerance document. *Direct* means the value reaches the output through moves only:
   copies, loads and stores, selection, `FNeg` and `FAbs`. When one export's outputs come from several
   operations, the largest of their bounds applies and the manifest lists every operation.
3. **An output computed from a tolerance result by further arithmetic** has no derivable bound: the
   error is amplified by an amount that depends on the fixture. The fixture declares `ulp`, `relative`
   or `absolute` with a bound and a written reason. A declared bound is marked as declared wherever it
   is recorded.

The bound of an operation's tolerance document is

```
bound = n + N + max(n, N)
```

with `n` from the operation's `accuracy=ulp(n)` and `N` the native slack of the function (next
section). Two cases need no document: evaluator against generated helper is `exact` (the intrinsic
issues require bit identity), and an implementation against exact reference vectors is measured as
reference error `≤ n`, not as a distance.

## Native math functions

A native function is the math library call of a runtime: the oracle side when the source program
calls it, the candidate side when a backend emits it.

- **Slack.** `N(function)` is the largest reference error of that function over the admitted pinned
  runtimes, rounded up to an integer. The error of a runtime is the larger of what the measurement
  script finds and what is published for the pinned library version.
- **No measurement, no slack.** A runtime that is not in the table cannot be compared under a
  tolerance for that function.
- **Cap.** A runtime whose error for a function exceeds **4 ULP** is not admitted for it. 4 is the
  smallest integer that admits the seven measured runtimes at their largest known errors (glibc
  `cbrt`, 3.67), the one exception being a published defect (glibc 2.44 `sinh`). The translation then uses the generated helper on that runtime, and a
  source program calling the function there has no tolerance oracle.
- **Backends.** A backend may emit a native call for an `ulp(n)` operation only where that
  runtime's error for the function, rounded up, is at most `n`. Otherwise it emits the helper.

Slack justified today for `f64`, and the bounds that follow from the reference bounds the intrinsic
issues state now:

| Function | Largest error, seven runtimes | Source | `N` | `n` | Bound |
| --- | ---: | --- | ---: | ---: | ---: |
| `sin` | 0.770 | measured, node | 1 | 1 | 3 |
| `cos` | 0.784 | measured, node | 1 | 1 | 3 |
| `tan` | 0.777 | measured, node | 1 | 1 | 3 |
| `exp` | 0.837 | measured, node | 1 | 1 | 3 |
| `log` | 0.756 | measured, node | 1 | 1 | 3 |
| `log2` | 0.750 | measured, node | 1 | 1 | 3 |
| `log10` | 1.62 | published, glibc 2.44; 1.616 measured, node | 2 | 1 | 5 |
| `pow` | 0.896 | measured, node | 1 | 1 | 3 |
| `atan2` | 1.195 | measured, node and jdk | 2 | 1 | 5 |
| `sinh` | 1.444 | measured, node; glibc 2.44 not admitted | 2 | 2 | 6 |
| `cosh` | 1.799 | measured, node and jdk | 2 | 2 | 6 |
| `tanh` | 1.821 | measured, node | 2 | 2 | 6 |
| `cbrt` | 3.67 | published, glibc 2.44, reproduced here | 4 | 1 | 9 |
| `hypot` | 1.696 | measured, node | 2 | 1 | 5 |

Not admitted: Go's `math` for every function above except `tanh`, `cbrt` and `hypot`; the x87
instructions `FSIN`, `FCOS`, `FPTAN`; glibc 2.44 `sinh`. Each has a follow-up below.

For `f32` nothing was measured. A runtime that only has `f64` functions returns a rounded `f64`
result, which is within 1 `f32` ULP whenever the `f64` error is below 2^28 ULP; runtimes with native
`f32` functions need the same measurement before an `f32` tolerance document is written.

## Declaring and logging the policy

**Tolerance document** `spec/verification/tolerances/<name>.yaml`, ID
`VERIFICATION.tolerance.<name>` — one per operation: `operation`, `mode`, `bound`, `specialValues`.
This ID is the *policy name* other documents refer to.

**Vector file**, property `exactness`:

```json
{ "mode": "exact" }
{ "mode": "ulp", "bound": 3, "format": "f64", "policy": "VERIFICATION.tolerance.sin" }
{ "mode": "relative", "bound": 1e-12, "format": "f64", "specialValues": "class",
  "reason": "sum of 64 Sin results; error grows with the number of terms" }
```

`specialValues` defaults to `signed-zero`. A mode other than `exact` carries either `policy` (a
derived bound) or `reason` (a declared bound), never neither.

**Corpus manifest**, fixture property `tolerance`: `exact`, a policy name, or `declared` when the
bound is declared in the fixture's vector file.

**Output manifest**, `verification.exactness`:

```json
"exactness": {
  "profile": "accurate",
  "mode": "tolerance",
  "specialValues": "signed-zero",
  "operations": [
    { "operation": "INTRINSICS.Sin", "accuracy": "ulp(1)", "policy": "VERIFICATION.tolerance.sin",
      "mode": "ulp", "bound": 3 }
  ],
  "declared": [],
  "observed": { "identical": 4090, "withinTolerance": 6, "maxDistance": 1, "worstVector": "sin-0173" }
}
```

| Member | Content |
| --- | --- |
| `profile` | `strict`, `accurate` or `fast` |
| `mode` | `exact` when every output was compared exactly, otherwise `tolerance` |
| `specialValues` | the rule applied |
| `operations` | every tolerance operation whose result reaches an output directly, with its contract, policy, mode and bound; `[]` under `exact` |
| `declared` | every declared bound: `{ output, mode, bound, reason }`; `[]` when none |
| `observed.identical` | compared elements whose bit patterns were identical |
| `observed.withinTolerance` | elements that passed without being identical |
| `observed.maxDistance` | the largest comparison distance of a passing element, in every mode |
| `observed.worstVector` | the vector that produced it; absent when `maxDistance` is 0 |

`observed` is what keeps a tolerance from hiding a difference: a fixture that passes with
`withinTolerance: 0` was bit-exact, and one that uses its whole bound says so. A failure reports both
values, the distance and the bound (`PLY0052`); the comparison report keeps both results of every
element that was not identical (PRD §11.2).

## Evidence

Measured on one development host, not on the CI hosts; see [Limits](#limits-of-the-evidence).

```
bash docs/adr/evidence/0005/measure.sh <work directory>
```

builds one runner per runtime, evaluates one input file on each and prints the tables. Inputs: for
each function 10 000 values from SplitMix64 seeded with 20260101 — half in the function's core range,
half log-uniform over its whole domain — plus the special values ±0, ±∞, NaN, the smallest and
largest subnormal, the smallest normal, the largest finite value and ±1 (all pairs for two operands,
all triples of a shorter list for `fma`): 202 552 evaluations per runtime. The exact results come
from GNU MPFR at 400 bits (4 400 bits for arithmetic, where the result is then exact), rounded to
`f64` in integer arithmetic. mpmath is not installed on the host and was not used.

| Key | Version | Math library, as the outputs show |
| --- | --- | --- |
| `gcc` | 16.2.1 | glibc 2.44 |
| `rust` | 1.99.0 | bit-identical to glibc 2.44 except `cbrt` |
| `python` | 3.13.1 | bit-identical to glibc 2.44 except `hypot` |
| `perl` | 5.42.2 | bit-identical to glibc 2.44 except where Perl computes on integers |
| `dotnet-sdk` | 10.0.112 | bit-identical to glibc 2.44 except `hypot` |
| `node` | 22.23.3 | V8's own |
| `jdk` | OpenJDK 27 | `Math`; `StrictMath` measured separately and different for ten functions |
| `go` | 1.27.2 | Go's own |
| x87 | `gcc -DX87`, inline `FSIN`, `FCOS`, `FPTAN`, `FPATAN`, `FYL2X`, `FSQRT`, extended-precision arithmetic | — |

Host: Linux 7.2.6 x86-64, Intel Core i9-11950H, MPFR 4.2.2. Full tables, versions and the SHA-256 of
every output file are in [`evidence/0005/results/`](evidence/0005/results/report.md).

### Reference error, maximum in ULP

| Function | dotnet-sdk | node | gcc | rust | jdk | python | perl | `StrictMath` | go | x87 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `sin` | 0.509 | 0.770 | 0.509 | 0.509 | 0.506 | 0.509 | 0.509 | 0.770 | 2.3·10³ | 1.1·10¹⁴ |
| `cos` | 0.511 | 0.784 | 0.511 | 0.511 | 0.505 | 0.511 | 0.511 | 0.784 | 6.0·10² | 2.9·10¹⁴ |
| `tan` | 0.523 | 0.777 | 0.523 | 0.523 | 0.521 | 0.523 | 0.523 | 0.777 | 6.5·10² | 2.0·10¹⁴ |
| `exp` | 0.503 | 0.837 | 0.503 | 0.503 | 0.509 | 0.503 | 0.503 | 0.837 | +∞ for a finite result | — |
| `log` | 0.509 | 0.756 | 0.509 | 0.509 | 0.500 | 0.509 | 0.509 | 0.756 | 3.1·10¹⁴ | 0.500 |
| `log2` | 0.518 | 0.750 | 0.518 | 0.518 | — | 0.518 | 0.518 | — | 2.3·10⁴ | 0.500 |
| `log10` | 1.526 | 1.616 | 1.526 | 1.526 | 0.500 | 1.526 | 1.526 | 1.616 | 2.7·10¹⁴ | 0.500 |
| `pow` | 0.503 | 0.896 | 0.503 | 0.503 | 0.502 | 0.503 | 0.503 | 0.813 | 3.0·10³ | — |
| `atan2` | 0.503 | 1.195 | 0.503 | 0.503 | 1.195 | 0.503 | 0.503 | 1.195 | wrong sign of π | 0.500 |
| `sinh` | 0.500 | 1.444 | 0.500 | 0.500 | 0.500 | 0.500 | 0.500 | 1.444 | ∓∞ for a finite result | — |
| `cosh` | 0.500 | 1.799 | 0.500 | 0.500 | 1.799 | 0.500 | 0.500 | 1.799 | +∞ for a finite result | — |
| `tanh` | 0.500 | 1.821 | 0.500 | 0.500 | 0.503 | 0.500 | 0.500 | 1.821 | 1.135 | — |
| `cbrt` | 2.925 | 0.658 | 2.925 | 0.500 | 0.509 | 2.925 | 2.925 | 0.661 | 0.663 | — |
| `hypot` | 0.885 | 1.696 | 0.524 | 0.524 | 0.817 | 0.500 | 0.524 | 0.817 | 1.696 | — |
| `+ − × ÷`, `sqrt` | 0.500 | 0.500 | 0.500 | 0.500 | 0.500 | 0.500 | 0.500 | 0.500 | 0.500 | 0.500 |
| `fma` | 0.500 | — | 0.500 | 0.500 | 0.500 | 0.500 | 0.500 | 0.500 | 0.500 | — |

— : the runtime has no such function. The x87 trigonometric instructions were measured for
`|x| < 2^63` only; beyond that they return the operand unchanged.

- **The basic operations are exact everywhere.** `+ − × ÷`, `sqrt` and `fma`: 0 results of more
  than 50 000 per runtime differ from the correctly rounded value on the seven runtimes and Go. Under x87 extended
  precision 1, 1, 2, 3 and 3 results (`+ − × ÷ √`) differ, by one step each.
- **No runtime rounds the transcendental functions correctly.** glibc, the most accurate, misrounds
  up to 44 of 10 000 results per function, 1 450 for `log10` and 5 515 for `cbrt`; only its `sinh`,
  `cosh` and `tanh` were correctly rounded throughout the sample.
- **The seven runtimes stay under 3 ULP** in this sample, and under 4 including the published worst
  case.
- **Go is an outlier.** `math.Exp(709.5)` returns `+Inf` (exact 1.35·10³⁰⁸); `math.Log` of the smallest
  subnormal returns −709.09 (exact −744.44); `math.Atan2(-1e-300, -1e300)` returns +π (exact −π);
  `math.Log2(1.0000125)` is 23 113 ULP off; `Sin`, `Cos`, `Tan` lose up to 2 287 ULP for large
  arguments.
- **The x87 instructions are an outlier** for trigonometry — Gladman et al. exclude them as "known
  to have bad accuracy" — and within 0.500 ULP for logarithms and `atan2`, where 1 to 4 results of
  10 000 are one step off because of the second rounding.

### Comparison distance between runtimes, maximum in doubles

| Function | among the seven | with `StrictMath`, go, x87 |
| --- | ---: | ---: |
| `sin`, `cos`, `tan`, `exp`, `log`, `log2`, `pow`, `atan2`, `sinh` | 1 | 3 034 to 9.2·10¹⁸ |
| `log10`, `cosh`, `tanh`, `hypot` | 2 | 2 (`tanh`, `hypot`); above 10¹⁴ otherwise |
| `cbrt` | 3 | 3 |
| `+ − × ÷`, `sqrt` | 0 | 1 (x87) |
| `fma` | 0 | 0 |

Every observed distance among the seven is within the bound the formula gives.

### Special values

| Runtime | raised an error | wrong sign of zero | NaN instead of a number | wrong exact value |
| --- | ---: | ---: | ---: | ---: |
| gcc, dotnet-sdk | 0 | 0 | 0 | 5 |
| rust, go | 0 | 0 | 0 | 0 |
| node, jdk | 0 | 0 | 5 | 0 |
| python | 229 | 0 | 0 | 5 |
| perl | 42 | 14 | 0 | 5 |

- *Wrong exact value*: glibc `cbrt` of the smallest subnormal is one step below the exact 2^−358.
- *NaN instead of a number*: `pow(1, NaN)`, `pow(1, ±∞)`, `pow(−1, ±∞)` — the ECMAScript and Java
  definitions, which the intrinsic `Pow[ecmascript]` already models.
- *Wrong sign of zero*: Perl computes `−0.0 + −0.0`, `−0.0 × 1` and similar on integers and returns
  `+0`.
- *Errors*: Python raises for domain and range errors, Perl for division by zero and `log` of a
  non-positive number.

NaN encodings for one operation each:

| Operation | gcc, rust, jdk, dotnet-sdk | node | go |
| --- | --- | --- | --- |
| `∞ − ∞`, `0 × ∞` | `fff8000000000000` | `fff8000000000000` | `fff8000000000000` |
| `NaN + 1` | `7ff8000000000000` | `7ff8000000000000` | `7ff8000000000000` |
| `log(−1)` | `fff8000000000000` | `7ff4000000000000` | `7ff8000000000001` |
| `hypot(NaN, 1)` | `7ff8000000000000` (dotnet-sdk: `fff8000000000000`) | `7ff8000000000000` | `7ff8000000000001` |

### False passes on wrong kernels

Two kernels without any tolerance operation, 10 000 vectors each
(`evidence/0005/falsepass.py`). A: 64 `f32` values summed in an `f32` accumulator; the wrong
translation accumulates in `f64`. B: `(a + b) + −a` in `f64`, including `a = 1e16, b = 1`; the wrong
translation reassociates to `(a + −a) + b`. The last column is the chance that a fixture of 8 vectors
passes the wrong kernel.

| Comparison | A: vectors accepted | A: 8-vector fixture | B: vectors accepted | B: 8-vector fixture |
| --- | ---: | ---: | ---: | ---: |
| `exact` (options 1 and 3) | 1 948 | 0.0002 % | 66 | 0 % |
| `ulp` 1 (option 2) | 5 115 | 0.47 % | 66 | 0 % |
| `ulp` 4 (option 2) | 8 407 | 25 % | 185 | 0 % |
| `relative` 1e-6 (option 2) | 9 443 | 63 % | 4 668 | 0.23 % |
| `absolute` 1e-6 (option 2) | 9 200 | 51 % | 4 956 | 0.36 % |
| interval around the exact real result (option 4) | 10 000 | 100 % | 10 000 | 100 % |

The interval oracle also rejects the *correct* kernel in 6 244 (A) and 9 934 (B) vectors: it checks
closeness to mathematics, and the source semantics here are the rounding errors.

### Cost

Per compared `f64` element over 10 000 000 elements (`cost.c`, `gcc -O1`): `exact` 1.36 ns, `ulp`
1.41 ns, `relative` 1.58 ns, `absolute` 1.54 ns. The modes do not differ in cost. An interval oracle
pays for the reference: MPFR needed 1.09 s for the 202 552 evaluations against 0.07 s for glibc.

### Published accuracy

- IEEE 754-2019 §5.4.1 requires correct rounding of `+ − × ÷`, square root and `fma`; §9.2 only
  recommends it for the other functions.
- ECMA-262 §21.3.2 calls the `Math` functions implementation-approximated and recommends, without
  requiring, the algorithms of fdlibm.
- `java.lang.Math` (Java SE 21 API): `sin`, `cos`, `tan`, `exp`, `log`, `log10`, `cbrt`, `pow`,
  `hypot` "within 1 ulp of the exact result", `atan2` within 2 ulps, `sinh`, `cosh`, `tanh` within
  2.5 ulps; `Math` results are "not defined to return the bit-for-bit same results", `StrictMath`
  results are.
- The glibc manual, "Errors in Math Functions": "Ideally the error for all functions is always less
  than 0.5ulps in round-to-nearest mode"; its test suite only flags errors above 9 ULP.
- Gladman, Innocente, Mather, Ozaki, Zimmermann, "Accuracy of Mathematical Functions in Single,
  Double, Double Extended, and Quadruple Precision" (August 2026), Table 3, largest known `f64`
  errors in ULP. glibc 2.44: `sin` 0.516, `cos` 0.516, `tan` 0.619, `exp` 0.511, `log` 0.520, `log2`
  0.548, `log10` 1.62, `pow` 0.523, `atan2` 0.524, `cosh` 0.500, `tanh` 0.500, `hypot` 0.792,
  `cbrt` 3.67, `sinh` 1.94·10¹⁹ ("a bug in the integration from the CORE-MATH code in GNU libc
  2.44"). FreeBSD 15.1, whose library the reference implementations are converted from: `tan` 1.02,
  `atan2` 1.55, `hypot` 1.21, `tanh` 2.22, `pow` 636. Microsoft's library, which the Windows CI host
  uses: `exp` 1.50, `tan` 1.32, `cbrt` 1.86, `hypot` 1.22.
  Their `cbrt` and `log10` inputs (`0x1.7a337e1ba1ec2p-257`, `0x1.de02157073b31p-1`) reproduce on
  this host at 3.67 and 1.62 ULP; their `sinh` input (`0x1.2b705c15abeb9p+5`) does not — glibc
  returns the correctly rounded value here.

The published numbers exceed the sampled ones (`cbrt` 3.67 against 2.925). A sample of 10 000 is a
lower bound, which is why the slack is the larger of the two.

## Options considered

1. **Exact by default, tolerance per intrinsic — chosen.** It is the only option under which neither
   wrong kernel passes, because neither contains an operation that may differ.
2. **Tolerance per fixture only — rejected.** A bound loose enough for one `sin` accepted the
   wrong accumulation in 25 % to 63 % of 8-vector fixtures.
3. **Three named profiles — adopted as names** on top of option 1: `strict` and `accurate` differ in
   the default contract, not in the comparison rule.
4. **Interval oracle — rejected as the comparison rule.** It accepts both wrong kernels, rejects the
   correct ones, and costs an arbitrary-precision evaluation per result. It remains what the
   reference vectors are: the measure of an *implementation* against exact values.

## Changes to consuming issues

Applied in each issue's own pull request.

| Issue | Change | Why |
| --- | --- | --- |
| [#67](https://github.com/Hawkynt/Polypiler/issues/67) oracle framework | vector-file `exactness`: `"bit-exact"` → `{ "mode": "exact" }`; `value` → `bound`; add `specialValues`, `policy`, `reason` | one spelling; it disagreed with #62 |
| #67 | in `ulp` mode `−0` no longer matches `+0` unless `specialValues` is `class` | it disagreed with #650, where a wrong sign of zero is a failure |
| #67 | the NaN-by-class rule also applies in `exact` mode to typed `f32`/`f64` elements | measured NaN encodings |
| [#62](https://github.com/Hawkynt/Polypiler/issues/62) verification schema | `specialValues` gains `signed-zero`, which becomes the default | `bitwise` and `class` tied the NaN rule to the zero rule |
| [#66](https://github.com/Hawkynt/Polypiler/issues/66) manifest | `verification.exactness` is the object above, with its sub-schema | the property had no shape |
| [#50](https://github.com/Hawkynt/Polypiler/issues/50) catalogue schema | `exactness` value `bit-exact` → `exact` | one spelling |
| [#36](https://github.com/Hawkynt/Polypiler/issues/36) corpus | `tolerance` accepts `declared` beside `exact` and a policy name | declared bounds have no tolerance document |
| [#650](https://github.com/Hawkynt/Polypiler/issues/650) reference vectors | none to the vocabulary; the harness formula is the reference error as defined here | — |
| [#655](https://github.com/Hawkynt/Polypiler/issues/655), [#649](https://github.com/Hawkynt/Polypiler/issues/649), [#653](https://github.com/Hawkynt/Polypiler/issues/653), [#656](https://github.com/Hawkynt/Polypiler/issues/656), [#652](https://github.com/Hawkynt/Polypiler/issues/652) | table column "Tolerance" → "Reference bound"; the tolerance documents carry the bound of the formula, not the reference bound | #654 already separates the two numbers |

## Contradictions with stated reference bounds

The reference implementations do not exist yet, so nothing was measured against them. The published
errors of the library they are to be converted from (FreeBSD msun) exceed five stated bounds. The
numbers in the issues are not overridden; each needs a decision in its own issue — another source,
or a larger `n`.

| Issue | Stated | Published for FreeBSD 15.1 |
| --- | --- | ---: |
| #649 `Pow` | ≤ 1 ULP | 636 |
| #656 `Atan2` | ≤ 1 ULP | 1.55 |
| #654 `Hypot` | ≤ 1 ULP | 1.21 |
| #655 `Tan` | ≤ 1 ULP | 1.02 |
| #652 `Tanh` | ≤ 2 ULP | 2.22 |

## Follow-ups

Drafted here; none is filed yet.

1. **Correctly rounded helpers for `strict`.** Register `cr` implementations of the FloatMath and
   Trig operations, converted from CORE-MATH (MIT); until then `strict` rejects them with `PLY0262`.
2. **Go: native `math` functions exceed the cap.** `Exp`, `Sinh`, `Cosh` (infinite for finite
   results), `Log`, `Log10` (subnormal arguments), `Log2`, `Pow`, `Atan2` (sign), `Sin`, `Cos`, `Tan`.
   The Go backend emits helpers for them; a Go source calling them has no tolerance oracle.
3. **x87 instruction functions exceed the cap.** `FSIN`, `FCOS`, `FPTAN` for PowerBASIC and other
   x87 code generators; `pb-compiler` itself was not run and needs the measurement.
4. **glibc 2.44 `sinh`.** Confirm or refute the published error on the pinned CI image; `sinh` on
   glibc stays unadmitted until then.
5. **Measurement on the CI hosts.** Run `measure.sh` against `tooling/toolchains.lock` on the Linux
   and Windows runners; add the Windows C runtime, `f32`, the functions not sampled here (`asin`,
   `acos`, `atan`, `expm1`, `log1p`, `exp2`, `exp10`, the area functions) and the toolchain keys
   `php`, `ruby`, `kotlinc`, `fpc`, `fbc`. The tolerance documents cite that run.
6. **Reference bounds against msun** — the five rows above.

## Limits of the evidence

- One host, x86-64 Linux. The work order asks for the CI hosts; glibc dispatches by CPU and the
  Windows runner uses another C library, so the table is valid for this host only. Follow-up 5.
- `f64` only, 14 functions, 10 000 samples each: a lower bound on every error.
- `pb-compiler` was not run; x87 was measured with the instructions it would emit.
- The versions are those installed here, not those of `tooling/toolchains.lock`, which does not exist
  yet.
