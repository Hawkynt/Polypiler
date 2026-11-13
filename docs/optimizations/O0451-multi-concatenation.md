# O0451 — Build a concatenation tree of three or more strings with one allocation sized by the summed lengths

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0451`.

## What it does

`a + b + c + d` is three binary concatenations: three results are allocated and the growing prefix is copied
again at every node — quadratic copying for linear output. Summing the lengths first, allocating once and
copying each operand once is what a careful programmer writes by hand with a builder. C#, Java and
JavaScript compilers do this for their own `+`; C, Rust, Go, Python, PowerBASIC and every assembly target
do not. PRD §4.8, §8 P4.

## Example

### Before

```
%t1 = StrConcat %a, %b
%t2 = StrConcat %t1, %c
%s  = StrConcat %t2, %d
```

### After

```
%s  = StrConcatN %a, %b, %c, %d               // one result of StrLen a + b + c + d code units
```

## When it applies

**String model:** `string<enc>` of [#379](https://github.com/Hawkynt/Polypiler/issues/379) — an immutable sequence of code units with value semantics, no identity and no capacity; two references to one string cannot be told apart, and nothing can mutate a string another name still reads. (1) The root is a `StrConcat`; an operand is descended into iff it is a `StrConcat` (or `StrConcatN`) with
exactly one user. An inner node with another user stays an opaque leaf — its value is needed anyway, and
re-copying its parts would cost more. (2) At least three leaves result; all have the root's type.
(3) Leaf order is the in-order traversal of the tree; concatenation of immutable values is associative,
so the value is the same. (4) Failure behaviour: the tree allocates each intermediate and may fail with
`OutOfMemory` ([#115](https://github.com/Hawkynt/Polypiler/issues/115)) at any node; the flat form allocates once, at the root. The rewrite needs
either a target profile that declares allocation failure unobservable — the condition
[#503](https://github.com/Hawkynt/Polypiler/issues/503) and [#500](https://github.com/Hawkynt/Polypiler/issues/500) use — or that no instruction with an observable effect lies
between the first inner node and the root ([#114](https://github.com/Hawkynt/Polypiler/issues/114)), so that a failure only moves between pure
instructions. (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the flat form: `Allocations` and copied units fall from
`n − 1` allocations and a quadratic copy to one allocation and a linear copy; a profile whose native
concatenation is already n-ary or lazily joined prices `StrConcat` chains at the same cost and keeps
them.

## How it is proven

`structural+effect-facts`: associativity of code-unit concatenation on immutable values; the only observable difference, the position of an allocation failure, is excluded by (4). No solver, no timeout path.

## When it must not fire

- `t = a + b; Call @log(x); s = t + c` with memory exhausted at the first concatenation: the source fails before the log entry, the flat form after it.
- A shared intermediate — `t = a + b; u = t + c; v = t + d`: flattening both users copies `a` and `b` twice and still needs `t` if anything else reads it.
- Operands that are views of a buffer which is modified between the inner concatenation and the root: the tree captured the old bytes at the inner node, the flat form reads the new ones. (This is the Polypiler form of PB-Compiler's warning that `f$() & g$() & h$()` staged through a shared result buffer reads `"hhh"`.)
- Reordering leaves 'for alignment': `a + b + c` is not `a + c + b`.
- Summing lengths in a narrower type than `uptr`: two 40 000-unit operands overflow a 16-bit sum and the buffer is too small.

## Switching it off

`--disable-opt O0451`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

targets whose concatenation allocates per operation and whose compiler does not join chains: C, Rust, Go, Python, PowerBASIC, and all assembly and machine-code backends (through a runtime helper). C#, Java and JavaScript already compile `a + b + c + d` into one n-ary concatenation or a rope; their profiles price the chain no higher than the flat form, the rule does not fire, and the generated source keeps the plain operator chain.

## Interactions with other passes

runs in phase `optimize` after `OPT-STR-CONCATFOLD` and `OPT-STR-EMPTYOPERAND` and after `OPT-STR-BUILDER` (a loop-carried chain must be seen by the builder rule first — the interaction PB-Compiler found the hard way); the legalizer picks the target form or the `pairwise` alternative.
