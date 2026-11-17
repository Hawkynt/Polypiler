# O0380 — Replace a parameter that receives the same constant at every call site by that constant in the callee

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** `O0380ArgumentConstantPropagation` runs among the passes over the legalized module ([#1151](https://github.com/Hawkynt/Polypiler/issues/1151)) and looks at every function of the program. Switched off with `--disable-opt O0380`.

## What it does

A helper called as `Draw(1, x)` from every call site tests `mode == 1` on every call and carries the code for
all other modes. Constant propagation ([O0003](O0003-sccp.md)) stops at the function boundary, and
inlining ([O0006](O0006-inlining.md)) exposes the literal only where it copies the body. This pass puts
the literal itself into the callee: every read of the parameter becomes the literal, so that folding
([O0001](O0001-constant-folding.md)) and dead-code removal ([O0005](O0005-dead-code-elimination.md))
remove the modes no call uses. The signature and every call stay as they are. PRD §4.8.

The idea, and the visibility test it rests on, come from PB-Compiler's
[O0018 interprocedural constant propagation](https://github.com/Hawkynt/PB-Compiler/blob/0d73acbea185a2597357d53f7ce9acb44758ba76/docs/optimizations/O0018-interprocedural-constant-propagation.md)
(`IpConstantProp.PropagateArguments` and `IrCallGraph.IsFullyVisible`, LGPL-3.0-or-later), carried over
from its SSA form to the structured tree IR (ADR 0009).

## Example

### Before

```
private int Draw(int mode, int x) {      // no public, no reference to it
  if (mode == 1) return x;
  return -x;
}
public int Main(int a) => Draw(1, 10) + Draw(1, 20);
```

### After

```
private int Draw(int mode, int x) {      // signature unchanged
  if (1 == 1) return x;                  // O0001 folds it, O0005 removes the other arm
  return -x;
}
```

`--explain-opt` prints `O0380 applied: @Draw: parameter %mode = 1 at all 2 call sites [proof: structural]`.

## When it applies

- The callee is owned (`Polypiler.Core.Optimize.Interprocedural.ModuleOwnership`): the module sees every
  call of it. It is not public, not the initializer of the module, not a constructor, method or accessor
  of a class or an interface, not an instantiation of a generic definition, and no `IrFunctionRef` names
  it - in a function, in the body of a lambda or in the initializer of a global.
- It has at least one call site; the calls are counted in every function, in the bodies of lambdas and
  in the initializers of globals, on the module as it is when the pass runs - never a census cached from
  an earlier pass.
- At every call site the argument for the parameter is a literal - an `IrConst`, an `IrNull` or an
  `IrFunctionRef` - and all are equal: a constant by its type and its bits, so `0.0` and `-0.0` differ and
  two NaNs are equal only with the same payload, a null by its type, a reference by the function it names.
  A call of the function by itself that passes the parameter on at its own position is not counted; one
  from a lambda in the function is, since the lambda's variable may be another.
- The parameter keeps the value it arrived with: no assignment, step, binding or declaration writes it,
  no lambda writes it and its address (`AddressOf`) is never taken.
- What changes: every read of the parameter outside the bodies of lambdas. A read in a lambda stays a
  read of the captured variable, which holds the same literal.
- The pass repeats until nothing changes, so a literal passed on (`A(1)` calls `B(p)`, which calls `C(q)`)
  reaches every function of the chain in one run.

## When it must not fire

- A public function called with 1 from inside the module: an outside caller passes 2 (`exported`).
- A function whose reference is stored in a table: the indirect call passes whatever the table's user
  chooses (`address taken`).
- Call sites passing `0.0` and `-0.0`: equal under `==`, different under `1 / x` (`arguments differ`).
- A call site whose argument is a variable (`argument at @f is not a literal`).
- A third call site added by inlining a caller: the census is taken anew on every run.
- Mutual recursion where `f(1)` passes its parameter to `g`, which calls `f(2)` (`arguments differ`).
- A parameter the function assigns (`written in the function`).

The opt-report names the reason for every parameter some call passes a literal to:
`O0380 not applied: @<f>: %<p>: <reason>`, with `exported`, `address taken`, `module initializer`,
`member of a type`, `generic instantiation`, `no call site`, `written in the function`,
`argument at <site> is not a literal` and `arguments differ (<a> at <site>, <b> at <site>)`.

## Targets

Every target: the folded branches disappear from generated source alike. The signature is unchanged, so
no target sees an interface change.

## Switching it off

`--disable-opt O0380`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Third of the passes over the legalized module, after [O0463](O0463-lambda-specialisation.md) and
[O0006](O0006-inlining.md) and before [O0389](O0389-global-variable-localization.md),
[O0003](O0003-sccp.md), [O0001](O0001-constant-folding.md) and [O0005](O0005-dead-code-elimination.md),
which fold what it puts in and remove the arms no call reaches.

## How it is proven

`structural`: every execution of the callee starts at one of the enumerated call sites, and each passes
the literal; the substitution replaces a value by an equal one. `O0380ArgumentConstantPropagationTests`
runs every scenario and row of the test design of #1151 on hand-built IR and compares the interpreter
before and after on boundary and random inputs (seed 380); `ModuleOwnershipTests` classifies every
combination of export, member, generic, module initializer and a reference in code, in a lambda and in
a global's initializer; `PassPipelineFixtureTests` runs the whole pipeline over every fixture.

## What remains

- The cost question (#155) - a literal a target materializes at higher cost than a parameter read - is
  not asked; every literal is taken.
- `test` functions do not exist in the IR yet, so there is no `test function` reason.
- The `.pir` fixtures, the feature file `opt-ipa-constarg.feature`, `spec/rewrites/O0380.yaml` and the
  differential run over 2 000 generated modules (#119) are not written yet.
- Constants flowing out through the return value (#1160) and removing the now unused parameter (#1155).
