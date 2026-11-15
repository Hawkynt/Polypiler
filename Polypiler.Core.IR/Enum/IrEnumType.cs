using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>A named value of an enum.</summary>
/// <param name="Name">The name, unique among the members of its enum.</param>
/// <param name="Value">The value as the IR carries a value of the underlying type: wrapped to its width, a <c>u64</c> as its 64 bits.</param>
public sealed record IrEnumMember(string Name, long Value);

/// <summary>
/// An integer with names, as a C# <c>enum</c> and a JavaScript object frozen with
/// <c>Object.freeze</c> whose properties are distinct integers are. A value of the type is a value of
/// its underlying integer type, any of them, also one no member has: the type of an enum value has the
/// kind of the underlying type (<see cref="IrType.Enum"/>), so its width, its sign and its arithmetic are
/// those of that type; it differs from the underlying type only by its name, as a declared type does.
/// <para>
/// What .NET makes of the names is the enum's own operations (<see cref="EnumOperations"/>): the text of
/// a value, the parse of a text, the members in the order of their unsigned values.
/// </para>
/// </summary>
public sealed class IrEnumType : IrTypeDeclaration {

  /// <summary>Makes an enum.</summary>
  /// <param name="name">The name, unique among the declarations of the module.</param>
  /// <param name="underlying">The integer type of the values: one of the eight from <c>i8</c> to <c>u64</c>.</param>
  /// <param name="members">The members in declaration order; two may have the same value.</param>
  /// <param name="isFlags">Whether the values are combinations of flags, as <c>[Flags]</c> says, which changes the text of a value.</param>
  public IrEnumType(string name, IrType underlying, IReadOnlyList<IrEnumMember> members, bool isFlags = false) : base(name) {
    this.Underlying = underlying;
    this.Members = [.. members];
    this.IsFlags = isFlags;
    this.Define([]);
  }

  /// <summary>The integer type of the values.</summary>
  public IrType Underlying { get; }

  /// <summary>The members in declaration order.</summary>
  public IReadOnlyList<IrEnumMember> Members { get; }

  /// <summary>Whether the values are combinations of flags.</summary>
  public bool IsFlags { get; }

  /// <summary>Whether code outside the module may name the type: a C# enum declared <c>public</c>, an exported JavaScript object.</summary>
  public bool IsPublic { get; init; }

  /// <summary>The type of the values of the enum.</summary>
  public IrType Type => IrType.Enum(this);

  /// <summary>Whether two members have the same value, for which .NET does not say which name it prints.</summary>
  public bool HasRepeatedValues => this.Members.Select(member => member.Value).Distinct().Count() != this.Members.Count;

  /// <summary>
  /// The members in the order .NET lists them: by the value read as an unsigned number of the width of
  /// the underlying type, so that -1 of an <c>i32</c> comes after every value that is not negative; two
  /// members of the same value in declaration order.
  /// </summary>
  public IReadOnlyList<IrEnumMember> Ordered => [.. this.Members.OrderBy(member => this.Unsigned(member.Value))];

  /// <summary>A value as the unsigned number of the width of the underlying type its bits are.</summary>
  /// <param name="value">A value as the IR carries it.</param>
  /// <returns>The bits as an unsigned number.</returns>
  public ulong Unsigned(long value) => this.Underlying.Bits == 64 ? unchecked((ulong)value) : unchecked((ulong)value) & ((1UL << this.Underlying.Bits) - 1);

  /// <summary>The first member, in declaration order, with a value, or <see langword="null"/>.</summary>
  /// <param name="value">A value as the IR carries it.</param>
  /// <returns>The member.</returns>
  public IrEnumMember? MemberOf(long value) => this.Members.FirstOrDefault(member => member.Value == value);

  /// <summary>The member of a name, or <see langword="null"/>.</summary>
  /// <param name="name">The name.</param>
  /// <returns>The member.</returns>
  public IrEnumMember? Member(string name) => this.Members.FirstOrDefault(member => string.Equals(member.Name, name, StringComparison.Ordinal));

}
