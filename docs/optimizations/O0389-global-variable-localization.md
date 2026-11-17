# O0389 — Turn a global that only one non-reentrant function touches into a local of that function

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** `O0389GlobalLocalization` runs among the passes over the legalized module ([#1158](https://github.com/Hawkynt/Polypiler/issues/1158)) and looks at every function of the program. Switched off with `--disable-opt O0389`.

## What it does

Old BASIC and C sources declare scratch variables at module level although one procedure is their only user.
As a global the variable is state every pass must treat as shared: constant propagation does not follow it,
its stores cannot be removed. As a variable of that function it is an ordinary local. The catch is that a
global keeps its value between calls and is shared by nested activations of the function, while a local is
neither - so "only one function uses it" is not enough. PRD §4.8, §17.1.

The pass declares the variable where the function first writes the global, turns every later read and
write into one of the variable, and drops the global's declaration, which nothing names any more and
whose initializer, static by the rules of `IrGlobal`, has no effect.

The idea and its two conditions beyond a single user - the store that precedes every load, and
non-reentrancy - come from PB-Compiler's
[O0278 global variable localization](https://github.com/Hawkynt/PB-Compiler/blob/0d73acbea185a2597357d53f7ce9acb44758ba76/docs/optimizations/O0278-global-variable-localization.md)
(`LocalizeGlobals`, LGPL-3.0-or-later), carried over from its SSA form to the structured tree IR (ADR 0009):
an entry-block store becomes a top-level assignment, an `alloca` a declared variable.

## Example

### Before

```
static int temp = 0;

int Work() {
  temp = 0;
  for (int i = 0; i < 100; i++) temp = temp + i;
  return temp;
}
```

### After

```
int Work() {
  int temp = 0;
  for (int i = 0; i < 100; i++) temp = temp + i;
  return temp;
}
```

`--explain-opt` prints `O0389 applied: global @temp localized into @Work [proof: structural+alias-facts]`.
`Work()` returns 4950 on every call either way.

## When it applies

- The global is of a scalar type - a number, a truth value, an enumeration, a pointer or a function
  value - and no static field of a class, which code outside the module may reach.
- Exactly one function names it, and only by reads, assignments and steps: no lambda names it, no
  `AddressOf` takes its address, no initializer of another global reads it.
- The first statement of the function's body that names the global at all is, at the top level of the
  body, an assignment of it whose value does not read it. Every way through the function then writes it
  before it reads it, so no activation reads what an earlier call left.
- The function cannot be entered again while it runs: it calls itself through no chain of direct calls,
  no `IrFunctionRef` names it, and nothing it reaches - its own code and that of every function it calls
  or constructs - runs code the module cannot name: a call through a function value, a method call, a
  native call, a call of a name the module has no function of, a `using`, a `foreach` or anything else
  whose effect summary ([#1361](https://github.com/Hawkynt/Polypiler/issues/1361)) is the worst case
  (`ModuleOwnership.UnknownCodeReachedFrom`).
- The function is neither async nor an iterator, whose activations interleave.
- The function may be public: its signature does not change.

## When it must not fire

- Recursion: `f(n) { g = n; if (n > 0) f(n - 1); return g; }` returns 0 for `f(3)` with the global - the
  innermost call overwrote it - and 3 with a local (`@f is recursive`).
- A counter: `calls = calls + 1` reads before it writes; the value of the previous call is the point
  (`load before the first store`).
- A store that is first only on one path, inside an `if` or a block of its own (`no store in the entry block`).
- A callback: the function calls a function value that may call it again (`may be re-entered through Invoke in @F`).
- The address given away, or a lambda that may run after the function returned (`address escapes at ...`).
- Two functions use the global (`used by 2 functions`), an array global (`not scalar`), a static field
  (`external`), an iterator or async function (`@F is resumable`), a function a reference names
  (`@F is address-taken`), a global another global's initializer reads (`read by the initializer of @h`).

Each refusal is in the opt-report as `O0389 not applied: global @<g>: <reason>`.

## Targets

Every target: the variable is a local in generated source, the idiomatic form in every target language,
and constant propagation and dead-store removal see it from then on.

## Switching it off

`--disable-opt O0389`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Fourth of the passes over the legalized module, after [O0006](O0006-inlining.md) and
[O0380](O0380-interprocedural-constant-propagation.md) and before [O0003](O0003-sccp.md), which propagates
the new variable's values.

## How it is proven

`structural+alias-facts`: with the first write before every read, each activation reads only values it
wrote itself; with re-entry excluded, no two activations are live at once, so one variable per activation
and one shared global are indistinguishable. `O0389GlobalLocalizationTests` runs every scenario and row of
the test design of #1158 on hand-built IR, each fixture function called twice in a row in one
interpreter instance and the recursive one to a depth of 3; `PassPipelineFixtureTests` runs the whole
pipeline over every fixture.

## What remains

- The issue left dropping the declaration to a separate pass (#1154); that pass does not exist, so this
  one drops the declaration it emptied, which no code names and whose initializer has no effect.
- The cost question (#155) - static storage cheaper on a profile without a stack frame - is not asked.
- Volatile, device and overlaid regions do not exist in the IR yet; external native callees count as
  unknown code without a contract that excludes calling back.
- The `.pir` fixtures, the feature file `opt-ipa-localglobal.feature`, `spec/rewrites/O0389.yaml` and the
  differential run over 2 000 generated modules (#119) are not written yet.
