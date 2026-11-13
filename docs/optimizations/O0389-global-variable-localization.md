# O0389 — Turn a global that only one non-reentrant function touches into a local of that function

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0389`.

## What it does

Old BASIC and C sources declare scratch variables at module level although one procedure is their only user.
As a global the variable is memory every pass must treat as shared: it cannot become an SSA value, its stores
cannot be removed, its range is unknown after every call. As a local of that procedure it is an ordinary
value. The catch is that a global keeps its value between calls and is shared by nested activations of the
function, while a local is neither — so "only one function uses it" is not enough. PRD §4.8 (redundant
allocation removal), §17.1.

## Example

### Before

```
global @temp: i32[1] = [0]

func @Work() -> i32 {
entry:
  Store.i32 @temp, 0, 0
  Br head
head:
  ...                                       // loop adding %i to @temp for i in 0..99
exit:
  %r = Load.i32 @temp, 0
  Ret.i32 %r
}
```

### After

```
global @temp: i32[1] = [0]                  // unreferenced; removed by O0383

func @Work() -> i32 {
entry:
  %temp = Alloc.ptr<i32>[stack] 1
  Store.i32 %temp, 0, 0
  Br head
  ...
exit:
  %r = Load.i32 %temp, 0
  Ret.i32 %r
}
```

## When it applies

(1) The global is scalar, `static`, not volatile, not a device region, not overlaid, and occurs in no
other global's initializer. (2) Every occurrence is the address operand of a `Load` or `Store` of the element
type in one function `F`; the address is not passed, stored, compared or returned ([#82](https://github.com/Hawkynt/Polypiler/issues/82)). (3) A
`Store` to it stands in `F`'s entry block with no `Load` of it before it in that block; it then dominates
every load, so neither the initializer nor a value left by an earlier call can be read. (4) `F` is
not re-entered while active: `F` is in no cycle of the call graph of [#87](https://github.com/Hawkynt/Polypiler/issues/87); no `FuncRef`
names `F`; and no function reachable from `F` contains a `CallIndirect` with an unresolved target set or a
call to an external function without a contract that excludes calling back. (5) `F` is not `resumable`. The
conversion does not change `F`'s signature, so `F` may be exported. A stack slot is not expected to cost more
than static storage; on profiles where it does (no stack frame), [#155](https://github.com/Hawkynt/Polypiler/issues/155) refuses, the original listed
first.

## How it is proven

`structural+alias-facts`: with the entry store dominating every load, each activation reads only values it wrote itself; with re-entry excluded, no two activations are live at once, so one slot per activation and one shared slot are indistinguishable. No solver, no timeout path.

## When it must not fire

- Recursion: `f(n) { g = n; if (n > 0) f(n - 1); return g; }` returns 0 for `f(3)` with a global `g` — the innermost call overwrote it — and 3 with a local.
- A counter: `calls = calls + 1` loads before it stores; the value of the previous call is the point.
- A store that is first only on one path: the other path reads the previous call's value.
- A callback: `F` calls an external function that calls `F` again through a stored address.
- The address given to a callee "for a moment": the callee may keep it.

## Targets

every target: the variable becomes a candidate for a register or an SSA value in machine code and a
local variable in generated source, which is also the idiomatic form in every target language. Sources
translated from BASIC dialects with liberal `SHARED` declarations benefit most.

## Switching it off

`--disable-opt O0389`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

module pass in phase `optimize` after `OPT-IPA-DEADFUNC` and [#159](https://github.com/Hawkynt/Polypiler/issues/159) (which can reduce the users of a global to one function) and before [#500](https://github.com/Hawkynt/Polypiler/issues/500), [#504](https://github.com/Hawkynt/Polypiler/issues/504) and [#508](https://github.com/Hawkynt/Polypiler/issues/508); enables `OPT-IPA-DEADGLOBAL` and, for cells holding code references, `OPT-IPA-DEVIRT`.
