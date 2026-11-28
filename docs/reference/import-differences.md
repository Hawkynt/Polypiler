# Import differences

Where an imported module could compute something other than the original does, before any backend
runs (#1367). Every such place either behaves like the original, is refused at import, or is
announced as a caveat in the notes of `translate` and in the manifest. Each case below says which,
and why.
Related: [repository README](../../README.md) · [integer and bit operations](integer-operations.md) ·
[collections](collections.md) · [string operations](string-operations.md) ·
[exceptions](exceptions.md) · [known runtime defects](known-runtime-defects.md).

## How it is checked

`ImportDifferentialTests` (`Polypiler.Tests.Acceptance`, `EndToEnd`) calls every public function of
every source under `tests/fixtures/prototype` whose values cross the boundary as numbers, truth
values, strings, arrays of them or structs, six times with fixed arguments: in .NET's build of the
C# fixture, in node on the JavaScript original, and in the IR interpreter on the imported module. The
calls of one source run in one instance of the module, as the original runs them: a fresh copy of the
JavaScript module, the C# class with its static fields initialised again. The outcomes must be equal.
A difference is accepted only when the test names it with the caveat the import announced for the
source, or with the section of this page that documents an oracle difference; a named difference that
no longer occurs fails as well. The backend differentials (`PythonFixtureTests`) compare against the
original runtime on every call and leave none out.

What the original runtime throws is compared by class: .NET's exception as the trap of the IR it maps
to, a view's range exception as `argument out of range` as in the tests of views, and an exception the
program throws as its class.

## JavaScript: an index outside an array

At an index that is not the integer of an element, JavaScript reads `undefined` and ignores a write
into a typed array; an `Array` grows. The IR has no `undefined` in a number, so the import decides by
where the value goes:

| Place | Decision | What the imported code does |
| --- | --- | --- |
| a read directly under `ToInt32` or `ToUint32` (`b[i] \| 0`, `b[i] << 8`, `b[i] >>> 0`) | behaves like the original | the element, or 0 outside: `ToInt32(undefined)` is 0 |
| a read directly under `ToNumber` (`a[i] + 1`, `a[i] * 0.5`) | behaves like the original | the element as a double, or a NaN outside |
| a read stored into a typed array (`out[0] = b[i]`) | behaves like the original | as the store converts: 0 into an integer array, a NaN into a float array |
| a store into a typed array (`b[i] = v`) | behaves like the original | made only inside the array; the array, the index and the value are evaluated first |
| an index that is a double, in the places above | behaves like the original | the element where the double is the integer of one, `undefined` at a fraction, a NaN or beyond 32 bits |
| a read returned or passed as it is, an operand of a sum under a coercion (`(s + b[i]) \| 0`), a comparison, a string element | announced: `js-oob-read` | traps with `index out of range`; JavaScript's `undefined` has no place in the IR's type |
| a store into an `Array` or a JSDoc `T[]` nothing decided (#1374), `a[i]++`, `a[i] op= v` | announced: `js-oob-read` | traps; the `Array` would grow |

Nothing is tested where the program proves the index inside the array: an index into a constant
table whose every value the inference bounds below the length of the table (a constant, `x & 255`
into a table of 256), and the variable of `for (let i = c; i < a.length;
i++)` with a non-negative integer `c` at `a[i]`, where nothing in the function but that update writes
`i` and nothing writes `a` (an array of the IR never changes its length). Such an access needs no
caveat either.

The test is the unsigned comparison of the index with the length, `(uint)a.Length > (uint)i`, which
is false for a negative index; the array and the index are evaluated once, in that order:

```js
/** @param {Uint8Array} b @param {int32} i */
export function low(b, i) { return b[i] | 0; }
```

```csharp
public static int low(byte[] b, int i) => ((uint)b.Length > (uint)i) ? b[i] : (byte)0;
```

## JavaScript: integer division by zero

`(a / b) | 0` divides doubles: by zero it is an infinity or a NaN, which the coercion makes 0, and
`-2147483648 / -1` is 2147483648, which `| 0` wraps.

| Construct | Decision | What the imported code does |
| --- | --- | --- |
| `(a / b) \| 0`, `(a % b) \| 0`, `>>> 0`, of 32-bit integers, divisor a constant other than 0 and -1 | behaves like the original | the division of the IR, which cannot trap there |
| the same with any other divisor | behaves like the original | 0 for a divisor of 0; for a signed divisor of -1 the negated dividend modulo 2^32 (`/`) or 0 (`%`); the division otherwise |
| the same where the dividend has an effect (a call, an assignment, `++`) and the divisor is no constant | announced: `js-div-zero` | traps: testing the divisor first would evaluate it before the dividend |
| `a % b` of two integers that are never negative, not under a coercion (#1375) | announced: `js-div-zero` | traps on 0; JavaScript's NaN has no place in the integer type that keeps the remainder exact |
| `BigInt` `/` and `%` | announced: `js-div-zero` | traps where JavaScript throws a `RangeError`, and the quotient that does not fit traps where `asIntN` wraps |
| a division of doubles | behaves like the original | IEEE 754 |

## Go: conversions to integers and fused multiply-add

The Go frontend parses and type-checks the source itself; where its verdict differs from Go's it
is a defect, which the Go layer looks for on the whole Go distribution. What it does not model it
refuses by name rather than accept unchecked.

The Go frontend ([Go frontend](../architecture/GO-FRONTEND.md)) announces two differences, each
where the source has the construct:

| Source | Announced | What the imported code does |
| --- | --- | --- |
| `int32(x)` of a `float32`/`float64` | `go-float-to-int` | truncates, and saturates outside the type with a NaN becoming 0, where Go leaves the result to the implementation |
| `x*y + z`, `z - x*y` of floating-point numbers | `go-fma-unfused` | rounds the product first, as Go does on amd64 and wherever the product is converted, where Go permits one fused rounding |

## Perl: what perl decides only while it runs

The Perl frontend ([Perl frontend](../architecture/PERL-FRONTEND.md)) parses the source itself;
where its verdict differs from perl's it is a defect, which the Perl layer looks for on the tests
and modules of the perl distribution. perl's grammar depends on what its BEGIN-time code defines:
a sub a module imports changes how a call of it parses. The frontend knows the exports of the
modules its table names and the subs of explicit import lists; where the parse of a bareword would
depend on a sub it cannot see, it refuses the file with `PLY1701` rather than guess.

The lowering makes perl's compile-time decisions itself, so the imported module no longer depends on
the perl that would have compiled it:

| Source | What the imported code does |
| --- | --- |
| several packages, classes or exports | in the order of the file and of their names, where perl's randomised hash order gave a different order on every run |
| `!1`, `1 == 1` and other folded comparisons | a boolean constant, where perl 5.42's own constants were refused |
| `$a->[0] += 1`, `$h{k} -= 1` (a compound assignment to an element) | stores the result, which the import of perl's op tree dropped |
| `$s .= 'x'` | appends, where the import of perl's op tree assigned |
| `do { STATEMENTS; VALUE }` with statements before the value | refused (`PLY1702`), where the import of perl's op tree dropped the statements |

## Classic Visual Basic: Variant

The classic Visual Basic frontend ([frontend](../architecture/VISUALBASIC-FRONTEND.md#variant),
[variants](variants.md)) gives a `Variant` local a fixed type only where every assignment has that type
and no read can see it `Empty`; every other `Variant` is the IR's dynamic type, whose operators are
the frontend's helpers. Where the result would depend on the kind a value has at run time, the
decision is made by the kinds the inference found:

| Place | Decision | What the imported code does |
| --- | --- | --- |
| integer arithmetic of a `Variant`, also of one given a fixed type | behaves like the original | promotes `Byte` → `Integer` → `Long` → `Double` (and `Single` → `Double`) where the typed operator would raise error 6 |
| `Empty`, `Null` in operators, comparisons, conditions and conversions | behaves like the original | VBA's rules ([MS-VBAL] 5.6.9, 5.5.1.2); `Null` where a value is needed is error 94 |
| the text of a `Single` or a `Double` | behaves like the original in the locale `--vb-locale` states (default `en-US`) | 15 or 7 significant digits of the exact value, a tie to the even digit, `1E+20`, `1E-05`, the locale's decimal separator; never the culture of the machine that runs the translation |
| a `String` read as a number: in arithmetic, a comparison with a number, a conversion, a condition, `IsNumeric` | behaves like the original in the stated locale | the locale's decimal and group separators, `E`/`D` exponents, `&H`/`&O`; error 13 for no number; currency symbols, parentheses and trailing signs, which `VarR8FromStr` also takes, are error 13 |
| the text of a `Date` | refused (`PLY2646`) | none: Visual Basic writes it in the Short Date format of the host; in a dynamic `Variant` from outside, error 13 when it runs |
| bitwise `And`/`Or`/`Xor`/`Eqv`/`Imp`/`Not` of a `Variant` | behaves like the original | the effective type of [MS-VBAL] 5.6.9.8 and its `Null` rules |
| a `Currency` in a dynamic `Variant` the host passes in | error 13 | a module meets `Currency` only through its own `CCur`, `@` literals and `Currency` variables, so a module without them carries no 64-bit integer |
| `Variant` parameters and results | typed per call signature where the calls prove one kind | the procedure as declared stays for the host (`Public`) and for calls of several kinds; the clones keep `Variant` promotion |

LibreOffice Basic, the oracle, has rules of its own for `Variant`s (ADR 2600: every arithmetic
result a `Double`, `Null & "a"` and the conversion of `Null` error 13, `Null And False` `Null`, every
bitwise result a `Long`), for the text of numbers (`1E+020`, all digits of an integer below 10^16, no
exponent for 1E-05, nine digits of a `Single`), for `Val` (`Val("1,5")` is 15), for `Currency` (four
decimals, `Currency` for a quotient and a product with a `Double`) and for dates (negative dates
linear, seconds truncated, no error 6 past 9999-12-31); the differential compares each fixture as
written with its evidence file and the round trip, which writes VBA's rules out, with the documented
values - the text of a number, which the round trip writes with the office's `CStr`, on the rows on
which the office agrees.

## Oracle differences

These calls differ because the original runtime does something its own specification does not say,
or something the IR deliberately does not imitate; the differential names each.

- **.NET's RyuJIT rotates a shift-xor also at count 0.** `RecognizeRotate.XorVariable(x, 0)` is `x ^ x
  = 0` by C#'s rules and `x` in .NET 8 and 10. The IR is right; evidence and reproduction are in
  [known runtime defects](known-runtime-defects.md#net-ryujit-rotates-a-shift-xor-also-at-count-0).
- **A default exception message names the namespace in .NET.** `Exceptions.Classes.DefaultMessage`:
  "Exception of type 'N.X' was thrown." in .NET, without the namespace in the IR, whose classes have
  none (see [exceptions](exceptions.md)).
- **`new string(c, n)` with a negative `n`.** .NET throws `ArgumentOutOfRangeException`; the IR's
  `StrRepeat`, which every frontend and backend shares, traps with `integer overflow`, as JavaScript's
  `repeat` throws a `RangeError` and Python's helper does. Both trap; the class differs
  (`Strings.Traps.Repeat`).
