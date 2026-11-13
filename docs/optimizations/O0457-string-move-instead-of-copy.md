# O0457 — Transfer the buffer of an owned code-unit sequence instead of copying it when the source is not used again

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0457`.

## What it does

In languages whose strings are mutable values — PowerBASIC, PHP, Perl, C++ — an assignment `b = a` copies the
buffer, because afterwards both names may be modified independently. When `a` is never used again the copy
is pure waste: `b` can take over `a`'s buffer. [#503](https://github.com/Hawkynt/Polypiler/issues/503) removes copies whose destination is only
read; this is the other case, where the destination goes on to be modified and the *source* is what dies.
It is also the half of PB-Compiler's result-buffer forwarding (O0295) that carries over: a call result
that is copied into a variable and then dropped. PRD §4.8, §8 P4.

## Example

### Before

```
%a = Call.list<u8> @read_line() [owned]
%b = ListCopy.list<u8> %a                     // b = a
ListAppend %b, 0x21                           // b is modified afterwards
Release %a                                    // a is never used again
```

### After

```
%a = Call.list<u8> @read_line() [owned]
%b = Move.list<u8> %a
ListAppend %b, 0x21
```

## When it applies

**String model:** an owned, mutable code-unit sequence — `list<unit>` of [#202](https://github.com/Hawkynt/Polypiler/issues/202), the representation of PowerBASIC, PHP, Perl and C++ strings — with a length, a capacity and a version that a program can read, and, under `[semantics=reference]`, an identity a second handle can observe. Immutable `string<enc>` values of [#379](https://github.com/Hawkynt/Polypiler/issues/379) are never copied in the IR and need no such rule. (1) `%a` is `Owned` at the copy ([#348](https://github.com/Hawkynt/Polypiler/issues/348)) — not `Borrowed`, `Shared`, `Moved` or `Unknown` — and its
storage is unique: it comes from `ListNew`, `ListCopy`, `Collect` or an `[owned]` call result and no
second handle was created. (2) On **every** path from the copy, the next use of `%a` is its `Release` or
there is none; one path with another use — including a later iteration of an enclosing loop when `%a` is
defined outside it — blocks the rule. (3) `%a` does not escape before the copy: `NoEscape`, or
`ArgEscape` to callees that did not capture it ([#82](https://github.com/Hawkynt/Polypiler/issues/82)). (4) No view of `%a` (`ListAsSlice`,
`ListAt` pointer, live `SeqOf`) is used after the copy: it would silently become a view of `%b`.
(5) Nothing reads `ListCapacity %b` or `ListVersion %b`: a copy and a moved buffer differ in both, and
[#202](https://github.com/Hawkynt/Polypiler/issues/202) makes them readable. (6) Under `[semantics=reference]` no identity comparison involves `%b`
and a handle derived from `%a` (there can be none after (1)–(3)). (7) The copy's allocation disappears;
a profile where allocation failure is observable must permit that refinement, as in
[#503](https://github.com/Hawkynt/Polypiler/issues/503). (8) [#155](https://github.com/Hawkynt/Polypiler/issues/155) always prefers the move where the target can express it; a profile
that has no move for the type keeps the copy.

## How it is proven

`alias-facts+effect-facts+structural`: with the source owned, unique, unescaped and dead, the only holder of the buffer after the rewrite is `%b`, whose contents equal the copy's; capacity and version are unobserved by (5). No solver, no timeout path.

## When it must not fire

- `b = a; print(a)`: the source is read after the assignment; after a move it is dead (PLY0166).
- A diamond in which one arm releases `a` and the other appends to it first: one path still uses the source.
- `a` was stored in a global before the copy: the global's handle now sees every change made to `b`.
- `v = ListAsSlice a; b = copy(a); b.append(x); use(v)`: the view was of `a`; after a move it shows `b`'s storage, which the append may have reallocated.
- A loop `for …: b = copy(a); modify(b)` with `a` defined before the loop: the second iteration copies from a moved-out source.
- `c = a` under reference semantics, then `b = clone(a)`: moving `a`'s buffer into `b` makes `c` and `b` the same list.
- `ListCapacity b` printed after the assignment: a fresh copy reports the copy's capacity, a moved buffer the source's.

## Switching it off

`--disable-opt O0457`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

every target with owned sequences: Rust (`b = a` instead of `a.clone()`), C++ (`std::move`), C (pointer hand-over instead of `malloc` and `memcpy`), Swift, assembly and machine-code backends; with reference lists, Java, C# and Python drop the explicit clone. Languages whose assignment always copies and that have no move spelling (PowerBASIC source, PHP) cannot express the result: their profile lists no move for lists, the rule does not fire, and the generated source keeps the plain assignment.

## Interactions with other passes

runs in phase `optimize` after [#159](https://github.com/Hawkynt/Polypiler/issues/159) and [#503](https://github.com/Hawkynt/Polypiler/issues/503) (which handles read-only destinations first) and before [#500](https://github.com/Hawkynt/Polypiler/issues/500); `OPT-STR-RETSLOT` defers owned-handle results to this rule; the borrow check of [#376](https://github.com/Hawkynt/Polypiler/issues/376) runs on the result like on any IR.
