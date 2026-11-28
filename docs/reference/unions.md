# Closed tagged unions

A value proven to be one of a few types, such as a result that is a number or a string, has a type
better than `variant`: a closed tagged union (#1582, ADR 0300 decision 2, level 5). The set of members
is fixed, so every backend can write its own sum type (a C# tagged struct, a Rust `enum`, a TypeScript
`A | B`) instead of `object`. Memory overlays in the style of a C `union` are another thing (#375). For a
target without a sum type, O0236 (#471) lowers the union.
Related: [repository README](../../README.md) · [ADR 0300, static types first](../adr/0300-static-types-first.md) · [variants](variants.md) ·
[optional values](optional-values.md) · [IR and analysis](../architecture/IR.md).

## The model

- **Type.** `IrType.Union(members)`, written `union<i32, str>`, `IrTypeKind.Union`, with the members in
  `IrType.UnionType` (`IrUnionType`).
  - **Canonical order.** The members are sorted by their names as the IR writes them, ordinally. So
    `union<str, i32>` and `union<i32, str>` are one type, and a member index means the same in every
    module.
  - **Equality.** Two union types are equal when their members are.
  - **Where a union may appear.** A union is a local, a parameter, a result or a global.
- **Members.**
  - **Count.** A union has two to four distinct members (`IrUnions.MinMembers`, `IrUnions.MaxMembers`).
  - **Allowed kinds.** A member is a scalar (a number or `bool`), a string, a struct, an object
    reference or an optional value.
  - **Refused.** A `variant`, a union, `void`, any other kind, and a member given twice.
  - **Diagnostics.** The verifier reports `PLY0100` with the reason `union of <n> members` or
    `union member <t> not allowed`, wherever the type is declared.
- **Operations.**
  - **`IrUnionOf(union, member, value)`** makes a value. `value` is exactly of the member's type.
  - **`IrUnionTagOf(value)`** is the member index the value holds, as an `i32`.
  - **`IrUnionValue(value, member)`** is the payload.
    - It traps with `InvalidCast` when the value holds another member.
    - `IsTested` says the frontend tested the tag first, so a backend may read the payload without the
      test.
  - **`==` and `!=`** compare two values of one union type. They are equal when both hold the same
    member and the payloads are equal as `==` compares that member's type:
    - a float as IEEE 754 does, so a NaN equals nothing;
    - a string by its code units;
    - an object by its identity;
    - a struct field by field;
    - an optional when both are none or both hold equal values.
  - **Nothing else.** No other operator, conversion or constant takes a union.
- **Interpreter boundary.** A union value crosses as `IrUnion(member, value)`, the payload as the
  member's .NET value.
- **Module format.** It appends the type kind `Union`, followed by its members, and the node tags
  `UnionOf`, `UnionTagOf` and `UnionValue` after the last existing tag. Modules written before this
  read unchanged.

## Backends

No backend has a lowering yet (#1596, #1598–#1604). Each refuses a module that uses a union
(`IrUnions.Uses`) with its own diagnostic instead of writing it wrongly. The JavaScript backend reports
`PLY1152` "a closed tagged union has no JavaScript form yet"; the others refuse it where they refuse
any type they do not know.
