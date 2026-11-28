# Closed unions

The closed tagged union of the IR (#1582): a value that is one of two to four proven types, level 5
of the lattice of [ADR 0300](../adr/0300-static-types-first.md). Where a [variant](variants.md) may
hold anything, a union holds one of the members its type lists, so every backend can write it as its
own sum type. A memory overlay - C's `union` - is another thing (#375).
Related: [repository README](../../README.md) · [variants](variants.md) · [optional values](optional-values.md) ·
[IR and analysis](../architecture/IR.md).

## The model

- **Type.** `IrType.Union(members)`, written `union<i32, str>`, `IrTypeKind.Union`. Its members are
  kept in a canonical order - by the name the IR writes each with, ordinal - so `union<str, i32>` and
  `union<i32, str>` are one type and a member is named by its index in that order
  (`IrUnionType.Members`, `IrUnionType.IndexOf`).
- **Members.** 2 to `IrUnions.MostMembers` = 4, each distinct, each a truth value, a number, a string,
  a struct, an object reference or an optional value. The verifier refuses a union of fewer or more
  members (`PLY0100` … `union of 1 members`), a variant, another union, `void`, an array or a
  collection as a member (`union member variant not allowed`) and a member listed twice.
- **Operations.**
  - `IrUnionOf(union, member, value)` makes a value of the member with that index; `value` is of
    exactly the member's type.
  - `IrUnionTagOf(value)` is the index of the member the value holds, as an `i32`.
  - `IrUnionValue(value, member)` is the member's value, and traps with `IrTrap.InvalidCast` when the
    value holds another member. `IsTested` marks one whose tag has been tested first; a backend may
    leave the test out. A tested member and the other two nodes never trap (`IrShape.IsTrapFreeNode`).
- **No operators.** No arithmetic, comparison or conversion takes a union; code tests the tag and works
  on members. A union's tag is the index of a member, never a COM `VARENUM` number.
- **Boundary.** A value crosses the interpreter's boundary as an `IrUnion(member, value)`, the value a
  .NET value of the member's type; one of another member index or value type is refused.
- **Module format.** The type is its kind and its members; the three nodes are tags of their own after
  the last older one, so every older layout reads as before.

## In the backends

No backend writes a union yet: each refuses it with its type diagnostic rather than a placeholder. The
lowerings are #1596 (C#), #1598 (JavaScript), #1599 (Python), #1600 (Rust), #1601 (Go), #1602 (JVM),
#1603 (C++) and #1604 (Visual Basic); #1589 infers the unions a program proves.
