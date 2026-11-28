using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The members of a closed tagged union (<see cref="IrType.Union"/>), in their canonical order: sorted
/// by the name of the type as the IR writes it, ordinally, so that <c>union&lt;str, i32&gt;</c> and
/// <c>union&lt;i32, str&gt;</c> are one type and a member index means the same in every module. Two
/// union types are equal when their members are. The members are kept as given otherwise, a member
/// given twice as well, for the verifier to report (<see cref="IrUnions.Problems"/>).
/// </summary>
public sealed class IrUnionType : IEquatable<IrUnionType> {

  /// <summary>A union of member types.</summary>
  /// <param name="members">The member types, in any order.</param>
  /// <exception cref="ArgumentNullException">The members or one of them is <see langword="null"/>.</exception>
  public IrUnionType(IReadOnlyList<IrType> members) {
    ArgumentNullException.ThrowIfNull(members);
    foreach (var member in members)
      ArgumentNullException.ThrowIfNull(member, nameof(members));

    this.Members = [.. members.OrderBy(member => member.ToString(), StringComparer.Ordinal)];
  }

  /// <summary>The member types in their canonical order; a member's index is its tag.</summary>
  public IReadOnlyList<IrType> Members { get; }

  /// <inheritdoc/>
  public bool Equals(IrUnionType? other) => other is not null && this.Members.SequenceEqual(other.Members);

  /// <inheritdoc/>
  public override bool Equals(object? obj) => this.Equals(obj as IrUnionType);

  /// <inheritdoc/>
  public override int GetHashCode() => this.Members.Aggregate(this.Members.Count, HashCode.Combine);

  /// <summary>The union as the IR writes it, for example <c>union&lt;i32, str&gt;</c>.</summary>
  public override string ToString() => $"union<{string.Join(", ", this.Members)}>";

}

/// <summary>A value of a closed tagged union made from one of its members.</summary>
/// <param name="Union">The union type, which is the type of the node.</param>
/// <param name="Member">The index of the member in the canonical order.</param>
/// <param name="Value">The value, of the member's type.</param>
public sealed record IrUnionOf(IrType Union, int Member, IrExpr Value) : IrExpr(Union);

/// <summary>The tag of a value of a closed tagged union: the index of the member it holds, as an <c>i32</c>.</summary>
/// <param name="Value">The value, of a union type, evaluated once.</param>
public sealed record IrUnionTagOf(IrExpr Value) : IrExpr(IrType.I32);

/// <summary>
/// The payload of a value of a closed tagged union that holds one member. It traps with
/// <see cref="IrTrap.InvalidCast"/> when the value holds another member.
/// </summary>
/// <param name="Value">The value, of a union type, evaluated once.</param>
/// <param name="Member">The index of the member.</param>
public sealed record IrUnionValue(IrExpr Value, int Member) : IrExpr(IrUnions.MemberType(Value.Type, Member) ?? IrType.Void) {

  /// <summary>
  /// Whether the frontend has tested the tag first, so that the value holds the member wherever this
  /// node is evaluated. A backend may then read the payload without the test; the meaning is the
  /// same, because the trap cannot happen.
  /// </summary>
  public bool IsTested { get; init; }

}

/// <summary>A value of a closed tagged union as it crosses the boundary of <see cref="IrInterpreter"/>: the member's index and the payload as a .NET value of the member's type.</summary>
/// <param name="Member">The index of the member.</param>
/// <param name="Value">The payload.</param>
public sealed record IrUnion(int Member, object? Value);

/// <summary>
/// The rules of the closed tagged unions (ADR 0300, level 5). A union type has two to four distinct
/// members, each a scalar (a number or <c>bool</c>), a string, a struct, an object reference or an
/// optional value; no variant, no union and no <c>void</c>. A value holds exactly one member:
/// <see cref="IrUnionOf"/> makes one, <see cref="IrUnionTagOf"/> reads which member it holds and
/// <see cref="IrUnionValue"/> its payload, which traps on another member. Two values of one union type
/// are equal (<c>==</c>) when they hold the same member with equal payloads.
/// </summary>
public static class IrUnions {

  /// <summary>The fewest members of a union.</summary>
  public const int MinMembers = 2;

  /// <summary>The most members of a union.</summary>
  public const int MaxMembers = 4;

  /// <summary>Whether a type may be a member of a union: a scalar, a string, a struct, an object reference or an optional value.</summary>
  /// <param name="type">The type.</param>
  public static bool IsMember(IrType type) {
    ArgumentNullException.ThrowIfNull(type);
    return type.IsNumeric || type == IrType.Bool || type.IsString || type.IsStruct || type.IsObject || type.IsOptional;
  }

  /// <summary>
  /// What is wrong with a union type, as the verifier reports it: <c>union of &lt;n&gt; members</c> outside
  /// two to four, and <c>union member &lt;t&gt; not allowed</c> for a member of another kind and for a
  /// member given twice; none for a valid union or a type that is no union.
  /// </summary>
  /// <param name="type">The type.</param>
  /// <returns>The reasons, in member order.</returns>
  public static IReadOnlyList<string> Problems(IrType type) {
    ArgumentNullException.ThrowIfNull(type);
    if (type.UnionType is not { } union)
      return [];

    var problems = new List<string>();
    if (union.Members.Count is < MinMembers or > MaxMembers)
      problems.Add($"union of {union.Members.Count} members");

    for (var index = 0; index < union.Members.Count; ++index) {
      var member = union.Members[index];
      if (!IsMember(member) || index > 0 && union.Members[index - 1] == member)
        problems.Add($"union member {member} not allowed");
    }

    return problems;
  }

  /// <summary>The type of a member of a union type, or <see langword="null"/> when the type is no union or has no such member.</summary>
  /// <param name="type">The union type.</param>
  /// <param name="member">The index of the member.</param>
  /// <returns>The member's type.</returns>
  public static IrType? MemberType(IrType type, int member) {
    ArgumentNullException.ThrowIfNull(type);
    return type.UnionType is { } union && member >= 0 && member < union.Members.Count ? union.Members[member] : null;
  }

  /// <summary>Whether a module uses a union anywhere: in a signature, a local, a global or an expression.</summary>
  /// <param name="module">The module.</param>
  /// <returns><see langword="true"/> when a backend must be able to write one.</returns>
  public static bool Uses(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return module.Globals.Any(global => global.Type.IsUnion)
      || module.Functions.Any(function => function.ReturnType.IsUnion || function.Parameters.Concat(function.Locals).Any(local => local.Type.IsUnion)
        || IrTree.Nodes(function.Body).Any(node => node.Type.IsUnion));
  }

}
