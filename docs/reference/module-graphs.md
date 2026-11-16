# Module graphs

A JavaScript file that requires other files is imported together with them: `polypiler translate`
reads every file the entry requires, links them into one script and imports that, so that the
generated module holds the entry and what it uses of the files below it. The link lives in
`Polypiler.Core.Frontends.JavaScript/Modules` (`ModuleGraph`, `Reachability`); the CLI reads the
files next to the entry. Related: [repository README](../../README.md) ·
[module state](module-state.md) · [classes](classes.md).

## Shapes

A file is a module of one of three shapes; any other file - an ES module, a script - is imported
alone, as before.

| Shape | Form | The module | Its requirements | Its exports |
| --- | --- | --- | --- | --- |
| UMD factory | `(function (root, factory) { … factory(require('a'), require('b')) … }(global, function (A, B) { … }))` | the body of the factory | the `require` calls of the wrapper's call of `factory` whose every argument is one, by position of the factory's parameters | the factory's `return { x, y }`, or `return X` |
| Wrapper | `(function (global) { … module.exports = X; })(this)` | the body of the wrapper | its `const B = require('…')` | what it assigns `module.exports` |
| CommonJS | a file whose top level requires another or assigns `module.exports` | the file | its `const B = require('…')` | what it assigns `module.exports` |

A specifier is relative (`./`, `../`) and names the file as it is, with `.js`, or its `index.js`. A
file required twice is one module; the files a file requires come before it.

## What the link folds away

What only finds out where the code runs has no meaning for a library and is blanked: a test of
`typeof module`, `typeof define`, `define.amd`, `typeof window`, `self`, `global`, `globalThis`,
`exports` or `require` (and the exports it assigns are recorded), an assignment to
`module.exports` or to a member of the wrapper's global object, a directive, and a test whether a
required module is there (`if (!B) throw …`), which the link proves it is. Blanking keeps every line
and every column, so a diagnostic points at the line of the file it is about.

## Names

The modules share one scope. `B.x` and a destructured `const { x } = B` are the declaration of `x`
in the module `B` is; a module whose export is one object - `module.exports = OpCodes` - is reached
through that object, under its own name. Refused by name: a name two modules declare, a destructured
export under another name, a module used as a value, an export that is no name of its module, a
member the module does not export, a required file that is destructured, cannot be read or is no
module, a cycle of required files, and a factory its wrapper does not call with what it requires.

## What is imported

Everything the entry file declares or does, and what every file does besides declaring, is reached;
so is everything a reached declaration names, a class with all its members, and a function of an
object literal held by a `const` - a namespace such as `OpCodes` - that reached code takes from it
(the whole namespace where it is used as a value). The rest of the required files is left out, so a
construct the subset does not take in an unused helper never refuses the entry. Names are matched
without regard to scopes, which only ever keeps more.

A function a required file declares - of its own or of an object it exports - is a helper of the
translation (#1387): in the original it is local to its factory or its wrapper, and nothing but the
graph calls it, so its parameters take what the calls of the graph pass, as those of a function the
file does not export do. What the entry file declares stays as visible as a file imported alone; the
classes of every file keep their contracts, since their methods are called through the objects the
entry exports.
