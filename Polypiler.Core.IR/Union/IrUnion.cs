using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The members of a closed union type (<see cref="IrType.Union"/>), in their canonical order: by the
/// name the IR writes each member with, ordinal. Two union types are one when their members are, so
/// <c>union&lt;str, i32&gt;</c> and <c>union&lt;i32, str&gt;</c> are the same type, and a member is named
/// by its index in that order.
/// </summary>
public sealed class IrUnionType : IEquatable<IrUnionType> {

  /// <summary>The members of a union, in any order; the verifier, not this constructor, says whether they make a valid one.</summary>
  /// <param name="members">The member types.</param>
  /// <exception cref="ArgumentNullException"><paramref name="members"/> or one of them is <see langword="null"/>.</exception>
  public IrUnionType(IReadOnlyList<IrType> members) {
    ArgumentNullException.ThrowIfNull(members);
    foreach (var member in members)
      ArgumentNullException.ThrowIfNull(member, nameof(members));
    this.Members = [.. members.OrderBy(member => member.ToString(), StringComparer.Ordinal)];
  }

  /// <summary>The member types, in canonical order.</summary>
  public IReadOnlyList<IrType> Members { get; }

  /// <summary>The index of a member type, or -1 for a type that is none of them.</summary>
  /// <param name="member">The type.</param>
  /// <returns>The index.</returns>
  public int IndexOf(IrType member) {
    for (var index = 0; index < this.Members.Count; ++index)
      if (this.Members[index] == member)
        return index;
    return -1;
  }

  /// <inheritdoc/>
  public bool Equals(IrUnionType? other) => other is not null && this.Members.SequenceEqual(other.Members);

  /// <inheritdoc/>
  public override bool Equals(object? obj) => this.Equals(obj as IrUnionType);

  /// <inheritdoc/>
  public override int GetHashCode() => this.Members.Aggregate(this.Members.Count, HashCode.Combine);

  /// <summary>The union as the IR writes it, for example <c>union&lt;i32, str&gt;</c>.</summary>
  public override string ToString() => $"union<{string.Join(", ", this.Members)}>";

}

/// <summary>A value of a union type made from the value of one member.</summary>
/// <param name="Union">The union type.</param>
/// <param name="Member">The index of the member, in canonical order.</param>
/// <param name="Value">The value, of exactly the member's type.</param>
public sealed record IrUnionOf(IrType Union, int Member, IrExpr Value) : IrExpr(Union);

/// <summary>The tag of a value of a union type: the index of the member it holds, as an <c>i32</c>.</summary>
/// <param name="Value">The value, of a union type, evaluated once.</param>
public sealed record IrUnionTagOf(IrExpr Value) : IrExpr(IrType.I32);

/// <summary>
/// The value of a member of a value of a union type. It traps with <see cref="IrTrap.InvalidCast"/>
/// when the value holds another member.
/// </summary>
/// <param name="Value">The value, of a union type, evaluated once.</param>
/// <param name="Member">The index of the member.</param>
public sealed record IrUnionValue(IrExpr Value, int Member) : IrExpr(IrUnions.MemberType(Value.Type, Member)) {

  /// <summary>
  /// Whether the tag has been tested first, so that the value holds this member wherever the node is
  /// evaluated; the trap cannot happen and a backend may read the member without a test.
  /// </summary>
  public bool IsTested { get; init; }

}

/// <summary>A value of a union type as it crosses the boundary of <see cref="IrInterpreter"/>: the index of its member and the member's value as a .NET value of its type.</summary>
/// <param name="Member">The index of the member.</param>
/// <param name="Value">The value of the member.</param>
public sealed record IrUnion(int Member, object? Value);

/// <summary>
/// The rules of the closed union types (ADR 0300, level 5). A union has 2 to <see cref="MostMembers"/>
/// distinct members, each a truth value, a number, a string, a struct, an object reference or an
/// optional value; never the dynamic type, another union or <c>void</c>. <see cref="IrUnionOf"/> makes
/// a value of one member, <see cref="IrUnionTagOf"/> reads which member it holds and
/// <see cref="IrUnionValue"/> the member's value, which traps on another member. Nothing else operates
/// on a union. Unlike a variant's tag, a union's tag is the index of a member of its own type, never a
/// number of the COM <c>VARENUM</c>.
/// </summary>
public static class IrUnions {

  /// <summary>The most members a union has.</summary>
  public const int MostMembers = 4;

  /// <summary>The type of a member of a union type; <see cref="IrType.Void"/> for a type that is no union or an index outside it.</summary>
  /// <param name="union">The union type.</param>
  /// <param name="member">The index of the member.</param>
  /// <returns>The member's type.</returns>
  public static IrType MemberType(IrType union, int member) {
    ArgumentNullException.ThrowIfNull(union);
    return union.UnionType is { } members && member >= 0 && member < members.Members.Count ? members.Members[member] : IrType.Void;
  }

  /// <summary>Whether a type may be the member of a union: a truth value, a number, a string, a struct, an object reference or an optional value.</summary>
  /// <param name="type">The type.</param>
  /// <returns><see langword="true"/> for a member type.</returns>
  public static bool IsMember(IrType type) {
    ArgumentNullException.ThrowIfNull(type);
    return type == IrType.Bool || type.IsNumeric || type.IsString || type.IsStruct || type.IsObject || type.IsOptional;
  }

  /// <summary>What makes a type invalid as a union, as the verifier words it, or <see langword="null"/> for a valid union.</summary>
  /// <param name="union">The union type.</param>
  /// <returns>The reason.</returns>
  /// <exception cref="ArgumentException">The type is no union.</exception>
  public static string? Problem(IrType union) {
    ArgumentNullException.ThrowIfNull(union);
    var members = (union.UnionType ?? throw new ArgumentException($"The type {union} is no union.", nameof(union))).Members;
    if (members.Count is < 2 or > MostMembers)
      return $"union of {members.Count} members";
    if (members.FirstOrDefault(member => !IsMember(member)) is { } forbidden)
      return $"union member {forbidden} not allowed";
    var twice = members.GroupBy(member => member).FirstOrDefault(group => group.Count() > 1);
    return twice is null ? null : $"union member {twice.Key} not allowed twice";
  }

  /// <summary>Whether a module uses a union type anywhere: in a signature, a local, a global or an expression.</summary>
  /// <param name="module">The module.</param>
  /// <returns><see langword="true"/> when a backend must be able to write it.</returns>
  public static bool Uses(IrModule module) => Types(module).Count > 0;

  /// <summary>The union types a module uses - in a signature, a local, a global or an expression - each once, in the order they are first met.</summary>
  /// <param name="module">The module.</param>
  /// <returns>The types; empty when a backend need not write any.</returns>
  public static IReadOnlyList<IrType> Types(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return [.. module.Globals.Select(global => global.Type)
      .Concat(module.Functions.SelectMany(function => function.Parameters.Concat(function.Locals).Select(local => local.Type).Prepend(function.ReturnType)
        .Concat(IrTree.Nodes(function.Body).Select(node => node is IrUnionTagOf tag ? tag.Value.Type : node.Type))))
      .Where(type => type.IsUnion).Distinct()];
  }

}
