# O0062 — Specialize Python arbitrary-precision int to fixed-width integers or keep it exact

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** `PyIntSpecialization` (`Polypiler.Core.Optimize/Specialize`) narrows
> the IR's arbitrary-precision integer `bigint` to `i32`, `u32`, `i64` or `u64` where masks and ranges prove
> it, and keeps it exact everywhere else ([#494](https://github.com/Hawkynt/Polypiler/issues/494)). It is
> required: `translate` runs it at every level, `--opt none` included, unless `--disable-opt O0062` switches
> it off. Its range facts are its own, intra-procedural ones; the facts of
> [#253](https://github.com/Hawkynt/Polypiler/issues/253) and [#94](https://github.com/Hawkynt/Polypiler/issues/94)
> and run-time range guards on parameters ([#778](https://github.com/Hawkynt/Polypiler/issues/778)) stay open.

## What it does

A Python `int` never overflows. Algorithm code relies on that in two opposite ways: hashes emulate 32- and
64-bit registers by masking (`(h * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF`), and number-theoretic code needs
hundreds of bits. The Python frontend imports every `int` as `bigint`, which is exact at every magnitude
(PRD §6.2: "arbitrary precision is preserved or translation fails explicitly; no silent truncation
occurs"); this pass gives the first kind its native arithmetic back:

- **Range facts.** A variable of a function is held in the narrowest of `i32`, `u32`, `i64` and `u64` that
  holds every value assigned to it: constants, a mask `e & m` (which lies in `[0, m]` for an `m` that is not
  negative, whatever `e` is), arithmetic of bounded values, the counter of a loop whose test bounds it. The
  ranges are joined until nothing grows. A result is narrowed the same way when every `return` of its
  function bounds it, and each call widens it back. A parameter stays `bigint`: no contract bounds its callers.
- **The exact-range form.** An expression whose result and operands lie in one fixed-width type is computed
  in it: no operation can wrap there.
- **The mask form.** `e & m` with a mask below 2^64 computes `e` modulo 2^32 (a mask of up to 32 bits) or
  2^64 when `e` is built from `+ - *`, `& | ^`, negation, the complement and `<<` by a constant: their low
  bits depend only on the low bits of their operands. A leaf of any range - also a parameter - enters as
  its low bits. Negative intermediates are fine: `&` works on the infinite two's complement, which the
  wrapped value equals.

What is neither stays `bigint`.

## Example

### Before

```python
def splitmix64(seed: int) -> int:
    z = (seed + 0x9E3779B97F4A7C15) & 0xFFFFFFFFFFFFFFFF
    z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & 0xFFFFFFFFFFFFFFFF
    z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & 0xFFFFFFFFFFFFFFFF
    return z ^ (z >> 31)
```

imports as `bigint` arithmetic throughout.

### After

```csharp
public static ulong splitmix64(global::System.Numerics.BigInteger seed) {
  unchecked {
    ulong z = 0;
    z = (ulong)(seed & ulong.MaxValue) + 11400714819323198485UL;
    z = (z ^ (z >> 30)) * 13787848793156543929UL;
    z = (z ^ (z >> 27)) * 10723151780598845931UL;
    return z ^ (z >> 31);
  }
}
```

`z` is proven in `[0, 2^64 - 1]` and held in `u64`; the masked multiplications are wrapping `u64`
multiplications; the parameter enters once as its low 64 bits. FNV-1a 32 becomes `Math.imul(h, 16777619) >>> 0`
in JavaScript.

## When it applies

- `e & m`, `m` a constant in `[1, 2^64 - 1]`: always - the low bits of `e` are computed in `u32` or `u64` as
  far as `e` is low-bit closed, and what is not (a right shift, a division, a remainder, a call) is computed
  exactly, in a fixed width where its own range proves that, and cut.
- `e % 2^k` and `e // 2^k`: the frontend writes them as `e & (2^k - 1)` and `e >> k`, which are exact floor
  semantics for every sign, so they take part.
- A variable, an expression or a result whose proven range fits 64 bits.

## How it is proven

`range-facts`: exact-range containment of interval arithmetic over `BigInteger` bounds, and the low-bit
closure of the listed operations (the lemma of demanded-bits narrowing). Validated against CPython 3.14:
FNV-1a 64 and 32, SplitMix64, xorshift64 and xorshift32 written the idiomatic way
(`tests/fixtures/optimize/o0062/hashes.py`) agree with CPython on the original source in the interpreter,
the emitted Python and the C# compiler's build, on arguments from 0 to beyond 2^70 and negative; node agrees
with the interpreter on the emitted JavaScript. No solver, no timeout path.

## When it must not fire

- `2 ** n` for an unbounded `n` stays exact: `2 ** 64` is 18446744073709551616, not 0.
- `(a // b) & 0xFFFFFFFF`: the low 32 bits of a quotient depend on the high bits of the dividend - the
  division stays exact (`O0062 not applied: masked expression contains a call of 'PyFloorDivideInt'`);
  `(2**40 // 2) & 0xFFFFFFFF` is 0.
- `(a >> 3) & 0xFF` for an unbounded `a`: the shift pulls in bits above the mask and stays exact
  (`... contains >>`).
- `x << n` under a mask with a count that may reach the width: not narrowed (`shift count may reach
  width`); `(1 << 40) & 0xFFFFFFFF` is 0.
- A value used unmasked as well as masked (`p = a * b; return (p & 0xFFFFFFFF) + p`): `p` stays exact.
- `-7 // 2` is -4 and `-7 % 2` is 1: the floor operations are the frontend's, which the pass keeps.
- The result of a method, of a function used as a value and of a function with a lambda in it stays
  exact: only a direct call is widened back. A variable a closure captures or a walrus (`:=`) assigns
  stays exact too: the closure and the binding name the variable itself.

## Switching it off

`--disable-opt O0062`. Every `int` then stays `bigint`; a target without an arbitrary-precision integer
refuses it with its own diagnostic - C# `net2.0` and `net3.5` with PLY1053, JavaScript below ES2020 with
PLY1152 - rather than with PLY0505 of the plan. `--explain-opt` writes each decision as a note:
`O0062 applied: & 0xFFFFFFFF -> u32 [wrap] (mask) [proof: range-facts]`, `O0062 applied: 'h' held in u64,
its values proven in [0, 18446744073709551615] [proof: range-facts]`, `O0062 applied: 'n' kept as
arbitrary-precision integer, ...`, `O0062 not applied: <reason>`.

## Interactions with other passes

Runs first, before the rules of the optimizer, on the module as the frontend made it; it touches only
`bigint`, so modules of the C# and JavaScript frontends pass unchanged. What it narrows every later integer
pass sees as ordinary fixed-width arithmetic.
