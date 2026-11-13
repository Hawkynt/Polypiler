# O0004 — Global value numbering and common-subexpression elimination

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#158](https://github.com/Hawkynt/Polypiler/issues/158). Once implemented it is switched off with `--disable-opt O0004`.

## What it does

Translated code repeats itself: `data[i]` read twice in one expression, `(a + b)` on both sides of a
comparison, the same mask rebuilt in each round of a cipher. Each repetition costs an operation on every
target and hides the pattern recognizers look for. Value numbering gives equal computations one name. PRD §4.8
(GVN/CSE), §8 P1.

## Example

### Before

```
%a = Add.u32[wrap] %x, %y
%b = Add.u32[wrap] %y, %x
%r = Mul.u32[wrap] %a, %b
```

### After

```
%a = Add.u32[wrap] %x, %y
%r = Mul.u32[wrap] %a, %a
```

## When it applies

Instruction `J` is replaced by instruction `I` iff (1) `I` dominates `J`; (2) both have the same operation,
result type, overflow contract, shift contract and rounding mode; (3) their operands have equal value numbers,
position by position, or swapped for the commutative set; (4) the operation is pure — no effect other than
`Trap` — or is a non-volatile `Load` with the same memory-state token; (5) for floating-point `Add`/`Mul`
the swapped match is used only when the profile's NaN policy is not `payload-observable`. Calls are numbered
only when their effect set is empty. Nothing is numbered across types: `Add.u32` and `Add.i32` are different
values even with equal bits.

## How it is proven

`structural+effect-facts`, with `exhaustive-8bit` for the commutativity table. Equal operation on equal operands under the same memory state yields the equal result by definition; no solver, no timeout path.

## When it must not fire

- `%p = Load.u32 %a; Store.u32 %b, 7; %q = Load.u32 %a` with `%b` possibly equal to `%a`: the tokens differ, `%q` is not `%p`.
- Two volatile or MMIO loads of one address are two observable reads (PRD §17.5).
- `%a = Call.u32 @next()` twice, where `@next` has a `Memory.Write` effect: two calls.
- The same `SDiv.i32[checked] %x, %y` in the two arms of an `if`: neither dominates the other; merging would need hoisting, which adds a possible trap to a path.
- `Add.f64 %x, %y` vs `Add.f64 %y, %x` with both operands NaN and payloads observable: x86 SSE returns the first operand's payload.
- `Add.i32[wrap] %x, %y` vs `Add.i32[checked] %x, %y`: the second traps on overflow, the first does not.

## Switching it off

`--disable-opt O0004`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` after [#164](https://github.com/Hawkynt/Polypiler/issues/164); enables [#156](https://github.com/Hawkynt/Polypiler/issues/156) (the operands of removed copies), [#508](https://github.com/Hawkynt/Polypiler/issues/508) and recognition rules that require one shared operand (see the rotate rule under [#276](https://github.com/Hawkynt/Polypiler/issues/276)).
