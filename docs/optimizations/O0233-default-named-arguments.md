# O0233 — Expand default and named arguments into positional calls

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#476](https://github.com/Hawkynt/Polypiler/issues/476). Once implemented it is switched off with `--disable-opt O0233`.

## What it does

`Scale(x, offset: 1)` relies on a default for `factor` and on a name instead of a position. C, PowerBASIC
3.5 and Java have neither. PRD §30.1 row "Named/default/optional parameters": omitted-argument behaviour and
the context in which a default is evaluated are the semantics to keep — a C# default is a call-site
constant, a Kotlin default is evaluated on every call and may read earlier parameters, a Python default is
evaluated once at definition.

## Example

### Before

```
func @Scale(%x: i32, %factor: i32 [default=2], %offset: i32 [default=0]) -> i32 defaultEval=constant { … }
%r = Call.Named.i32 @Scale(%v, offset: %o)
```

### After

```
%r = Call.i32 @Scale(%v, 2, %o)
```

## When it applies

(1) Every parameter receives exactly one value: by position, by name or by
default; a name that matches no parameter, a parameter bound twice and a required parameter left unbound
block the rewrite. (2) Supplied argument expressions keep their written order; when a named argument is
written before a positional slot it fills, its value is held in a temporary. (3) `constant` defaults are
inserted as literals; `per-call` thunks are called after all supplied arguments, in parameter order, each
receiving the final values of the earlier parameters; `once-at-definition` reads the cell. (4) A wrapper
passes its parameters unchanged and adds only defaults.

## How it is proven

`structural+interpreter`: the rewrite materializes a binding the call already determines;
validated by interpreting both forms on every fixture, including effectful argument expressions.

## When it must not fire

- Reordering argument evaluation into parameter order: `Scale(offset: Next(), x: Next())` with `Next()`
  returning 1 then 2 must bind offset = 1, x = 2 and yield 5; evaluating in parameter order yields 4.
- Evaluating a `per-call` default once and caching it: `fun stamp(t: Int = tick())` must call `tick()` on
  every call.
- Evaluating a `per-call` default at the call site with the caller's variables: `fun pad(n: Int, width:
  Int = n * 2)` reads the callee's `n`.
- Treating a `once-at-definition` list default as fresh per call: Python's shared default accumulates
  across calls.

## Switching it off

`--disable-opt O0233`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(default-parameters)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

runs after [#475](https://github.com/Hawkynt/Polypiler/issues/475) (the callee is known); its wrappers are named by the same
mangler; enables constant propagation of inserted defaults.
