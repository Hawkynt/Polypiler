# ADR 0007 — Native calls in JavaScript through a user-supplied binding

Area: foundation

| | |
| --- | --- |
| Status | accepted |
| Index | [decision records](README.md) |
| Date | 2026-11-15 |
| Issue | [#1332](https://github.com/Hawkynt/Polypiler/issues/1332) |
| Requirements | [PRD §29.1](../prd/08-interop-and-language-semantics.md#s29-1), [§29.3](../prd/08-interop-and-language-semantics.md#s29-3) |

## Question

C# calls native code through P/Invoke (`[DllImport]`, `[LibraryImport]`). ECMA-262 has no foreign-function interface
at any level, and Node.js has none built in. What does a native call become in JavaScript?

## Options

| Option | What the generated JavaScript does | Consequences |
| --- | --- | --- |
| A. Refuse | `PLY1152`, exit 3, nothing written | Honest and cheap; a class with one native call cannot be translated at all. |
| B. Node-API addon | a C file and a `binding.gyp` beside the module, which `require`s the built addon | A C compiler, `node-gyp` and Python on every machine, per OS and architecture; Node only; the output is no longer one dependency-free file. |
| C. WebAssembly | the library compiled to WebAssembly | Only for libraries whose source compiles to wasm, which is then another library; every caller becomes asynchronous. |
| D. User-supplied binding | each foreign function is called through a JavaScript module the user names | Works at every level and module format; the output stays dependency-free; the user supplies the platform meaning. |

## Decision

**D, with A as its default.**

1. `polypiler translate … --to javascript --bind <library>=<module or global>` names the binding of a library, once
   per library; the option repeats. The library is the name the source wrote, verbatim (`libc.so.6`, `msvcrt`).
2. An ES module imports the binding (`import * as $libc_so_6 from "./libc-bindings.mjs"`), CommonJS requires it, a
   script (`iife`) reads it from the global the option names (`--bind libc.so.6=globalThis.LibC`).
3. Every foreign function is a function of the module under its C# name, exported where the source made it public,
   that calls the function of its symbol in the binding with the value contract of
   [foreign calls](../reference/foreign-calls.md#the-binding-contract).
4. A foreign function whose library has no `--bind` is refused with `PLY1152` "'strlen' (libc.so.6) is a native
   function, which JavaScript profile 'es2022' cannot call", exit 3, nothing written.
5. A malformed `--bind`, a library bound twice and a binding of a library the module calls nothing of are a command
   line not understood, `PLY0003`, exit 2.
6. Nothing proves that the binding is the native function. The manifest of the translation records every foreign
   function called through a binding as the caveat `unverified-external`, and every library under
   `nativeDependencies`.

B and C stay possible as later issues that produce a binding module of this contract; nothing done here is thrown
away.

## Evidence

The acceptance tests run the generated JavaScript of `tests/fixtures/prototype/interop/LibC.Linux.cs` (or
`LibC.Windows.cs`) in `node` with `tests/fixtures/prototype/interop/libc-bindings.mjs`, at ES5 as CommonJS and at
ES2022 as an ES module, and compare with the .NET run of the same fixture against the real C library of the
operating system: `StrLen("hello")` is 5, `Abs(-7)` is 7, `Div(7, 2)` is `{ Quot: 3, Rem: 1 }`.

## Consequences

- Generated JavaScript never calls a missing function at run time: either the binding is named, or the translation
  is refused.
- A binding written for every level converts what it receives with `typeof` (a 64-bit integer is a `BigInt` from
  ES2020 and its halves `{ hi, lo }` below) and may return a `Number` for any integer.
- Callbacks, varargs, dynamic loading and COM stay out of scope (#640, #638, #642, #641).
