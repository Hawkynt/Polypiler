# O0500 — Inline single-expression helpers into their call sites in the target syntax tree

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The target passes `CSharpHelperInlining` and `JavaScriptHelperInlining` run between the backend's syntax tree and its printer ([#1336](https://github.com/Hawkynt/Polypiler/issues/1336)). Switched off with `--disable-opt O0500`. The IR-level inlining of calls between functions of the program stays [O0006](O0006-inlining.md).

## What it does

A backend calls a helper for what the target lacks or what needs more than an operator: `$rotl` for a
32-bit rotation below the level that has no rotate, `RotateLeft` in C# before .NET Core 3.1. Many of
them are one returned expression. The pass replaces a call of such a helper by its body with the
arguments put in for the parameters, so that the generated code reads like the hand-written form and
pays no call. It works on the target syntax tree after the backend built it (PRD §4.9,
[backends](../architecture/BACKENDS.md)); the printer adds the parentheses the substituted expressions
need. Together with [O0501](O0501-target-unused-declaration-removal.md), which runs after it, a helper
whose every call was inlined is gone from the file.

## Example

`polypiler translate Snippet.cs --from csharp --to javascript --profile es5` on

```csharp
public static uint F(uint x) => System.Numerics.BitOperations.RotateLeft(x, 8);
```

### Before

With `--disable-opt O0500`:

```javascript
function $rotl(value, count) {
  return ((value << count) | (value >>> (-count & 31))) >>> 0;
}

// F(x: number): number
function F(x) {
  return $rotl(x, 8);
}
```

### After

By default; `--explain-opt` prints `O0500 helper inlining: '$rotl' into 'F'` and
`O0501 unused declaration removal: '$rotl'`:

```javascript
// F(x: number): number
function F(x) {
  return ((x << 8) | (x >>> (-8 & 31))) >>> 0;
}
```

`F(0x12345678)` returns 878082066 either way. For C# at `net2.0` the call `RotateLeft(x, 8)` becomes
`(uint)((x << 8) | (x >> (32 - (8 & 31))))`.

## When it applies

- The callee is a helper the backend declared (not a function of the program), its body is one
  `return` of an expression, and it is neither recursive nor generic, nor `void`, nor has a parameter
  passed by reference.
- The profile's cost rule says the call is not cheaper: the body has at most `Budget(profile)` nodes,
  parentheses not counted - 16 in every profile of both backends today. A call costs an engine or JIT
  that does not inline it itself about what a dozen operators cost.
- Every argument is a variable or a literal - which may be read any number of times or not at all -
  or an expression without an effect that the body reads once: in JavaScript operators, members,
  elements and conditionals of variables and literals; in C# only operations that cannot trap in a
  checked context (bitwise operators and comparisons).
- In C#, each argument whose type differs from its parameter's is cast to it, which is the implicit
  conversion the call made, and the result is cast to the return type unless the body is that cast.
  The helpers compute the same in a checked and an unchecked context, so the body may stand anywhere.

## When it must not fire

- A multi-statement helper (`$ck`, `$udm64`, `PopCount` before .NET Core 3.1) or a recursive one.
- A body that assigns or steps anything, or names `this`, `arguments` or a function expression.
- An argument with an effect (`$rotl(f(), 8)`, `$rotl(i++, 8)`) or one read twice by the body that is
  not a variable or literal (`value` of `$rotl` is read twice: `$rotl(a + b, 8)` stays a call).
- A body that names a variable the calling function declares, which the substitution would capture.
- In C#: arguments that are all constants, and any constant argument of a body that divides, which
  would make the compiler evaluate the expression and refuse an overflow or a zero divisor it was
  meant to meet at run time; an argument of a type the call converts implicitly to no other.

## Switching it off

`--disable-opt O0500`; on at `--opt default`, off at `--opt none`. The IR rules and the target passes
share one namespace of numbers.

## Interactions with other passes

Runs first of the target passes, after the IR optimizer, legalization and the building of the tree;
[O0501](O0501-target-unused-declaration-removal.md) removes what it leaves unreferenced.

## How it is proven

`TargetPassTests` checks every class of argument (constant, local, parameter, expression with an
effect, an unused argument of an effect), every refusal and the cost rule on both backends; the
acceptance scenario `A single-expression helper is inlined` runs the example above in `node`. Every
fixture of `tests/fixtures/prototype` is written by every profile of both backends with the passes
on, parsed back by Roslyn and Acornima at the profile's level and compared with the tree it was
printed from; the differential tests run the generated code against the interpreter and the C#
compiler's build of the original.
