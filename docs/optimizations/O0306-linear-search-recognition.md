# O0306 — Recognize early-exit linear search loops as Memory.Find

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0306`.

## What it does

`for i: if a[i] = k then found = i: exit for` is how every language without a library search looks for a
value. [#189](https://github.com/Hawkynt/Polypiler/issues/189) defines `Memory.Find` — the smallest index holding a value, or the count — and
backends map it to `memchr`, `Array.IndexOf`, `REPNE SCAS` or PowerBASIC's `ARRAY SCAN`, but no rule produces
it yet ([#561](https://github.com/Hawkynt/Polypiler/issues/561) covers the two-buffer comparisons only). PB-Compiler's idiom catalog lists the
linear search next to the sort (`OPT-LOOP-SORT`); the operation is also the starting point of
`OPT-LOOP-BINSEARCH` and `OPT-LOOP-KEYSWITCH`. PRD §4.5, §19.3.

## Example

### Before

```
head:
  %i = Phi.u32 [pre: 0], [next: %i2]
  %c = ICmp.ult.u32 %i, %n
  Br %c, test, exit
test:
  %x  = Load.u16[bounds] (ElemAddr.ptr<u16>[bounds] %a, %i)
  %eq = ICmp.eq.u16 %x, %k
  Br %eq, exit, next
next:
  %i2 = Add.u32[wrap] %i, 1
  Br head
exit:
  %f = Phi.u32 [head: %i], [test: %i]
```

### After

```
%f = Memory.Find.u16[read=prefix] %a, %k, %n
```

## When it applies

(1) The loop has one counter `AddRec(0, 1)` ([#350](https://github.com/Hawkynt/Polypiler/issues/350)) of an unsigned type, the bound test
`i < n` in the header before the load, and exactly two exits: the bound test and the equality test.
(2) Each iteration performs exactly one load, of element `i` of a loop-invariant array, and compares it for
equality with a loop-invariant value of the element type; the loop has no other effect ([#114](https://github.com/Hawkynt/Polypiler/issues/114)).
(3) The storage is ordinary, non-volatile memory ([#82](https://github.com/Hawkynt/Polypiler/issues/82) classification). (4) Values used after the
loop are only the counter (index form: its exit value on both exits) or one phi that is `i` on the match
edge and a loop-invariant value `F` on the bound edge (break form). (5) The element accesses are `[bounds]`
→ `[read=prefix]` reproduces the trap at the first inaccessible element; they are `[inbounds]`/`[unchecked]`
or `n ≤ Length(a)` is proven ([#94](https://github.com/Hawkynt/Polypiler/issues/94)) → `[read=full]`.

## How it is proven

`structural+effect-facts`: the loop is the definition of `Memory.Find` (smallest `i < n` with `a[i] = k`, else `n`) with the prefix read contract. No solver, no timeout path.

## When it must not fire

- `i <= n` as the bound test: the loop reads `a[n]`; `Memory.Find` over `n` elements does not, over `n + 1` it would return `n + 1` for a miss.
- `do { … } while (i < n)`: element 0 is read even when `n = 0`.
- `f64` elements and key `NaN`: the loop never matches; a bitwise search would.
- A body that also counts comparisons in a global: one more effect per iteration.
- A loop over a volatile receive buffer waiting for a byte to appear: each read is an effect and the data changes.

## Targets

Every target. Source targets get their library search (`Array.IndexOf`, `memchr`, `indexOf`,
`ARRAY SCAN`), which reads better than the loop; x86 backends can use `REPNE SCAS`; targets with neither get
the decomposition of [#189](https://github.com/Hawkynt/Polypiler/issues/189), which is the loop again. Choosing among those is the backend's and
[#155](https://github.com/Hawkynt/Polypiler/issues/155)'s decision at legalization — the recognition itself never makes code worse and is therefore not
cost-gated.

## Switching it off

`--disable-opt O0306`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `recognize` after loop canonicalization; enables `OPT-LOOP-BINSEARCH` and `OPT-LOOP-KEYSWITCH` (searches of constant tables) and the backends' library-call selection.
