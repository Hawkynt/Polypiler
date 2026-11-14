# O0501 — Remove helpers and private constants that nothing in the target syntax tree references

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The target passes `CSharpUnusedDeclarations` and `JavaScriptUnusedDeclarations` run between the backend's syntax tree and its printer ([#1336](https://github.com/Hawkynt/Polypiler/issues/1336)). Switched off with `--disable-opt O0501`. Removing dead functions and globals of the program stays [O0381](O0381-dead-function-elimination.md) and [O0383](O0383-global-dead-code-elimination.md).

## What it does

A backend declares a helper only when the code calls it, together with the helpers that one calls.
A pass before this one can make a declaration unused - [O0500](O0500-target-helper-inlining.md)
inlines every call of a helper, and a helper used only by that helper loses its last caller too. This
pass removes every helper and every private constant of the module that nothing in the file names any
more, again and again until every one left is named. The empty lines in front of a removed
declaration stay in front of what follows it.

## Example

On the example of [O0500](O0500-target-helper-inlining.md): after the call `$rotl(x, 8)` was
inlined, the declaration of `$rotl` is the only place that names it.

### Before

With `--disable-opt O0501` (O0500 on):

```javascript
function $rotl(value, count) {
  return ((value << count) | (value >>> (-count & 31))) >>> 0;
}

// F(x: number): number
function F(x) {
  return ((x << 8) | (x >>> (-8 & 31))) >>> 0;
}
```

### After

By default; `--explain-opt` prints `O0501 unused declaration removal: '$rotl'`:

```javascript
// F(x: number): number
function F(x) {
  return ((x << 8) | (x >>> (-8 & 31))) >>> 0;
}
```

## When it applies

- C#: a helper method of the module class, or a `private` field of it (a constant or a table), that
  no name anywhere in the file outside its own declaration refers to.
- JavaScript: a helper function or a variable at the level of the module - of the file, or of the
  function expression a script returns its exports from - that no identifier outside its own
  declaration refers to. Its initializer computes a value without an effect: a literal, a table, a
  `DataView` of a new buffer.

## When it must not fire

- A function of the program, public or private, and a struct: only helpers and constants are removed.
- A declaration whose name stands anywhere else, also as a local of another function that happens to
  share it: names are compared, not bindings, so a doubt keeps the declaration.
- Anything exported, and the `exports` assignments of a CommonJS module.

## Switching it off

`--disable-opt O0501`; on at `--opt default`, off at `--opt none`, where the file keeps every helper
the emitter declared.

## Interactions with other passes

Runs last of the target passes, so that it sees what every pass before it left unreferenced.

## How it is proven

`TargetPassTests` checks that a helper used only by an inlined helper and a constant used only in an
inlined body are removed and nothing else is, on both backends, and the acceptance scenario
`A helper made unused by inlining is removed` checks that no generated file of any fixture declares
a helper nothing references. With `--opt none` every golden output of
`tests/fixtures/backend/golden` is byte-identical, so the removal never runs where it is not asked for.
