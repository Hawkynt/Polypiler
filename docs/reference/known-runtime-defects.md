# Known runtime defects

Defects of a target's runtime that change what correct generated code computes, with the evidence,
a minimal reproduction for an upstream report and what the backend writes around them. Each one is a
row of the backend's capability table, so it applies per target profile and a test shows the defect
and the form that avoids it.
Related: [repository README](../../README.md) · [frontends, backends and targets](../architecture/TARGETS.md) ·
[integer and bit operations](integer-operations.md).

## .NET: RyuJIT rotates a shift-xor also at count 0

**Row:** `RyuJIT rotation of (x << y) ^ (x >> (w - y)), also at y = 0` (`CSharpRuntimeDefect.ShiftXorRotation`),
every C# profile. **Issue:** [#1310](https://github.com/Hawkynt/Polypiler/issues/1310).
**Upstream:** not reported yet; the reproduction below is what a report to dotnet/runtime needs.

C# takes a shift count modulo the width of the shifted value (ECMA-334, "Shift operators"). So at
`n = 0` the expression `(x << n) ^ (x >> (32 - n))` shifts by 0 twice and is `x ^ x = 0`. The JIT
compiles it to `rol` by `n`, which gives `x`.

### Minimal reproduction

```csharp
using System;
using System.Runtime.CompilerServices;

static class Program {
  [MethodImpl(MethodImplOptions.NoInlining)]
  static uint F(uint x, int n) => (x << n) ^ (x >> (32 - n));

  static void Main() => Console.WriteLine($"{F(0x12345678, 0):X8}");
}
```

Expected `00000000`; prints `12345678`. The same with `ulong` and `64 - n` prints the value too. The
JIT's disassembly (`DOTNET_JitDisasm=F`) is `mov ecx, esi; mov eax, edi; rol eax, cl; ret`.

Measured on linux-x64 with .NET SDK 10.0.112, on the runtimes 10.0.12 and 8.0.31; the build in
Release and in Debug; run in every tier (tier 0, tiered with and without PGO,
`DOTNET_TieredCompilation=0`, `DOTNET_JITMinOpts=1`). The cause is `fgRecognizeAndMorphBitwiseRotation`
in `src/coreclr/jit/morph.cpp` (release/10.0): it accepts `GT_XOR` as the root of a rotation as well as
`GT_OR` (`fgOperIsBitwiseRotationRoot`), and for a count that is not a constant it does not require the
count to be nonzero modulo the width. An `|` is a rotation at count 0 too, since `x | x = x`; an `^` is
not.

### What is affected

`^` with a variable count is computed wrongly at every count that is 0 modulo the width; everything
else in this table is computed as C# defines it. A row holds for .NET 8 and .NET 10 alike.

| Expression (`w` = 32 or 64) | Tier 0, Debug, MinOpts | Tiered, full optimization |
| --- | --- | --- |
| `(x << n) ^ (x >> (w - n))`, also `(x >> n) ^ (x << (w - n))`, either order, `-n + w` | wrong | wrong |
| the same on `int`/`long` with `>>>` or through `(uint)`/`(ulong)` | wrong | wrong |
| `(x << (n & w-1)) ^ (x >> ((w - n) & w-1))` | right | wrong |
| `int r = w - n; … (x << n) ^ (x >> r)` | right | wrong |
| `int k = n; … (x << k) ^ (x >> (w - n))` | right | wrong |
| `(x << n) \| (x >> (w - n))`, `(x << n) + (x >> (w - n))` | right | right |
| `^` with constant counts, `(x << 0) ^ (x >> w)` included | right | right |
| `^` on `byte`/`ushort` after the promotion to `int` (`8 - n`, `16 - n`) | right | right |
| `(x << n) ^ (x >> -n)`, `(x << n) ^ (x >> ((n ^ w-1) + 1))` | right | right |
| `(x << n) ^ (x >> (r ^ w))` with `r = w - n` | right | right |

### What the backend writes

In an exclusive or of a left shift and a logical right shift (a same-width conversion between them
included), with the row in the table:

- a count `w - y`, `-y + w` or `w + -y` of a `y` that is not a constant - masked or not - is written
  `-y`: `(x << n) ^ (x >> -n)`;
- a count that is a local the function sets to such a complement anywhere is written with its width
  bit flipped: `(x << n) ^ (x >> (r ^ 32))`.

C# takes the count modulo the width, so `w - y` and `-y` are the same count, and so are `r` and `r ^ w`.
The JIT does not rotate either form, and either costs what the plain expression costs where the JIT does
not rotate it, which is the only correct code for it; that is why the row covers every profile,
including the .NET Framework ones whose runtime was not measured. Everything else - `|`, `+`, constant
counts, narrow values, a count not of this shape - is written as before. A copy of a count that is
not a complement (`k = n` above) needs nothing: the complement beside it is already written `-n`.

### How it is checked

`ShiftXorRotationTests` (unit) compiles every shape of the table with the generated text, runs it in
the first tier and with `MethodImplOptions.AggressiveOptimization` for the counts 0 to `w + 1` and
around the edges of `int`, and compares it with the interpreter: no mismatch. The same shapes written
without the row differ, every direct form at count 0 and only at counts that are 0 modulo the width -
once a runtime no longer does this, that test fails and this page and the row are due for a revision.
The acceptance scenarios in `tests/features/prototype/jit-shift-xor.feature` run the real executable.
`RecognizeRotate.XorVariable` is compared with the build of the generated C#, because the C# compiler's
build of the fixture itself carries the defect.
