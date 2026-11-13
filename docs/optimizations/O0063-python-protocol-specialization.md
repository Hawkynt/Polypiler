# O0063 — Resolve Python protocols, attributes, slices and iterators statically where proven

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#780](https://github.com/Hawkynt/Polypiler/issues/780). Once implemented it is switched off with `--disable-opt O0063`.

## What it does

In Python `a[i]`, `len(a)`, `a + b`, `for x in a` and `obj.attr` are all method calls decided at run time:
`__getitem__`, `__len__`, `__add__`, `__iter__`/`__next__`, the descriptor protocol and `__getattr__`. For
the built-in types algorithm code actually uses — `list`, `bytes`, `bytearray`, `range`, `tuple` — the
target of each call is known once the type is proven, and their exact semantics (negative indices, slice
clamping, slices as copies, iteration over a list that grows) must be reproduced. PRD §4.6 (Python:
protocols, dynamic attributes, descriptors, slices, iterators), §8 P3 ("type/shape … specialization; typed comprehensions and sequences"), §3.3 Tier B: the Python-as-source pairs need this proven-only resolution in P3. Guarded specialization stays in P4 with [#778](https://github.com/Hawkynt/Polypiler/issues/778).

## Example

### Before

```
// fact: %a : HomogeneousSequence(i64, growable, dense) [Proven] (a Python list of int)
%n  = Dyn.Call len, %a
%x  = Dyn.GetIndex %a, -1
%s  = Dyn.GetSlice %a, 1, 10, None
```

### After

```
%n  = Length %a
%i  = Sub.i64[wrap] %n, 1
%x  = Load.i64[bounds] %a, %i                  // IndexError when the list is empty
%hi = Min.i64 10, %n
%lo = Min.i64 1, %n
%s  = Sequence.CopyRange %a, %lo, %hi          // a new list
```

## When it applies

(1) The receiver's fact from [#253](https://github.com/Hawkynt/Polypiler/issues/253) is an exact built-in type (not a subclass: `type(x) is list`),
`Proven`, or `Contextual` with its contract in force; a `Guarded` fact is treated as `Dynamic` here. (2) Index: `i < 0` →
`i + len`; then `0 ≤ i < len` or `IndexError`. (3) Slice with step 1: `lo = clamp(i < 0 ? i + len : i, 0,
len)`, likewise `hi`; empty when `lo ≥ hi`; other steps follow the reference algorithm
(`slice.indices`); step 0 raises `ValueError`. A list slice is a copy: later writes to either side are not
visible in the other ([#121](https://github.com/Hawkynt/Polypiler/issues/121) `CopyRange`). (4) List iteration is by index against the *current*
length; appending during iteration extends the loop; the rewrite is an index loop with the length re-read
unless [#114](https://github.com/Hawkynt/Polypiler/issues/114) proves the body cannot change it. (5) Closed-shape classes: every attribute store is
`self.<name> = …` inside `__init__`, the class and its bases define none of `__getattr__`,
`__getattribute__`, `__setattr__`, `__delattr__`, and no `setattr`, `delattr`, `vars`, `__dict__` access
on the class or its instances exists in the module; instances do not escape to unknown code. (6) A
`property` (data descriptor on the class) takes precedence over the instance attribute and is called on
each access; its effects stay in place and count. (7) Binary operators on a proven class call `__add__`;
if it returns `NotImplemented` the reflected method of the right operand is tried, else `TypeError`.

## How it is proven

`effect-facts+range-facts`: with the receiver's exact type known, the data-model lookup has a single statically known target; the built-in algorithms are transcribed from the language reference and validated against `python`. No solver, no timeout path.

## When it must not fire

- `a[-1]` on `[10, 20, 30, 40]` is 40, not an error; `a[4]` raises `IndexError`; `a[1:10]` is `[20, 30, 40]` without error.
- `b = a[:]; b[0] = 99`: `a[0]` is unchanged — a slice of a list is a copy, not a view.
- `for x in a: if x == 10: a.append(50)` visits 10, 20, 30, 40, 50; a loop over a length captured at the start stops at 40.
- A class with `__getattr__` that logs: `obj.missing` runs user code; a field load does not.
- A `property` whose getter increments a counter must be called once per access, not cached.
- `class L(list)` overriding `__getitem__`: the receiver is a `list` subclass, not an exact `list`.

## Switching it off

`--disable-opt O0063`; `Kind = Required`, so it runs at every level including `--opt none`. With it disabled these operations stay dynamic and a typed target reports PLY0500 or PLY0506.

## Interactions with other passes

runs in phase `specialize` after [#253](https://github.com/Hawkynt/Polypiler/issues/253) and [#494](https://github.com/Hawkynt/Polypiler/issues/494); enables [#502](https://github.com/Hawkynt/Polypiler/issues/502), [#503](https://github.com/Hawkynt/Polypiler/issues/503) (slice copies that are only read), [#507](https://github.com/Hawkynt/Polypiler/issues/507) and [#510](https://github.com/Hawkynt/Polypiler/issues/510) on Python sources.
