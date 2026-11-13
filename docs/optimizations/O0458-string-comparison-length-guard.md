# O0458 — Decide string inequality from the lengths before comparing code units

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0458`.

## What it does

Two strings of different lengths are unequal, and for `==` and `!=` that settles it without reading a code
unit. A three-way comparison cannot use that shortcut — it has to find the first difference to say which
string sorts first — yet a great deal of code tests equality through it (`strcmp(a, b) == 0`,
`a.CompareTo(b) == 0`, a `SELECT CASE` over strings). Routing equality tests to an equality operation,
and giving that operation the length guard and a block comparison where the target has none, makes the
common negative case two loads and a compare. PRD §4.8, §8 P4.

## Example

### Before

```
%c = StrCmp.i32[ordinal] %a, %b
%e = ICmp.eq.i32 %c, 0
```

### After

```
// step 1:  %e = StrEq %a, %b
// step 2, where the profile has no cheaper native equality:
  %la = StrLen.uptr[unit=codeunit] %a
  %lb = StrLen.uptr[unit=codeunit] %b
  %sl = ICmp.eq.uptr %la, %lb
  Br %sl, content, done
content:
  %eq = Memory.Equal.u8[read=full] (StrUnits.roslice<u8> %a), (StrUnits.roslice<u8> %b), %la
  Br done
done:
  %e  = Phi.bool [entry: false], [content: %eq]
```

## When it applies

**String model:** `string<enc>` of [#379](https://github.com/Hawkynt/Polypiler/issues/379) — an immutable sequence of code units with value semantics, no identity and no capacity; two references to one string cannot be told apart, and nothing can mutate a string another name still reads. Step 1: (1) the comparison is `StrCmp[ordinal]` on two non-optional strings of one type; its result is 0 iff the two
code-unit sequences are identical, which is the definition of `StrEq`. (2) Every user is `ICmp.eq` or
`ICmp.ne` of the result against the constant 0 (`StringFacts.ZeroTestUsers` of `OPT-STR-EMPTYCMP`); a
relational test, a stored result or any other user blocks the step. Step 2: (3) `StrEq` is code-unit
equality without normalization, so it holds iff the code-unit lengths are equal and the units agree
pairwise. (4) Lengths are taken in code units (stored, O(1)); never in scalars. (5) `Memory.Equal` runs
only on the equal-length path with that length; `[read=full]` is justified because both unit ranges
belong to live strings of exactly that length. The unit type follows the encoding (`u8` or `u16`).
(6) `Memory.Equal` with length 0 is `true` and reads nothing, so two empty strings are equal. (7)
[#155](https://github.com/Hawkynt/Polypiler/issues/155) gates step 2: it fires when the profile prices `StrEq` as a call or a sequence costlier than
two length loads, a compare and `Memory.Equal`; a profile with a native equality that already checks the
length keeps `StrEq`. Timing is not an observable of the IR.

## How it is proven

`structural+interpreter`: both steps are definitional rewrites of code-unit equality; the interpreter enumerates all pairs of strings up to three units over three unit values for the three forms. No solver, no timeout path.

## When it must not fire

- Ordering: `"b"` and `"abc"` differ in length, but which is smaller is decided by the first unit; a length guard on `<` would be wrong, and step 1 never touches a relational user.
- A word-wise scan used for ordering: little-endian 16-bit words order `"ba"` (0x6162) before `"ab"` (0x6261). That is why only equality is routed to `Memory.Equal`.
- Canonically equivalent text: `"é"` as U+00E9 (two UTF-8 units) and as `e` + U+0301 (three units) have different lengths; a normalizing comparison calls them equal, `StrEq` does not, and only `StrEq` is rewritten.
- A fixed-length field `"AB  "` compared with `"AB"` under padded comparison is equal although the lengths are 4 and 2.
- Comparing scalar counts instead of code-unit counts: correct but a full scan of both strings — the cost the guard was meant to avoid.
- `StrCmp` whose result is also returned: the caller may test its sign.

## Switching it off

`--disable-opt O0458`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

step 1 helps every target — `a == b` reads better than `compare(a, b) == 0` and every runtime's equality is at least as fast as its three-way comparison. Step 2 fires on targets without a length-checking native equality: C (`strcmp`), PowerBASIC 3.5-class compilers, tokenized BASIC, assembly and machine-code backends. C#, Java, JavaScript, Python, Rust and Go compare lengths first in their own equality; their profiles keep `StrEq`, and the generated source shows the plain operator.

## Interactions with other passes

runs in phase `optimize` after `OPT-STR-EMPTYCMP` and `OPT-STR-LITIDENTITY` (which remove the trivial cases) and after [#158](https://github.com/Hawkynt/Polypiler/issues/158) has merged repeated `StrLen`; the branch of step 2 is laid out by the control-flow passes; `Memory.Equal` is lowered per target by [#189](https://github.com/Hawkynt/Polypiler/issues/189)'s decomposition or a native primitive.
