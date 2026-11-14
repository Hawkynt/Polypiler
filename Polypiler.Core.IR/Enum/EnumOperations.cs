using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The meaning of the operations of the IR on enums, which are .NET's: JavaScript has no names for the
/// values of a frozen object, so the rules of <c>System.Enum</c> are the rules here, read from its API
/// reference and checked against .NET 10 (docs/reference/enums.md). A value that is no member keeps its
/// number; the text of a <c>[Flags]</c> value is the names of the flags it consists of, found greedily
/// from the largest value down and written in ascending order, as <c>Enum.ToString</c> of dotnet/runtime
/// (MIT) finds them; the members are listed by their unsigned values.
/// </summary>
public static class EnumOperations {

  /// <summary>The operations on enums.</summary>
  public static IReadOnlyCollection<IrIntrinsicOp> Operations { get; } = [
    IrIntrinsicOp.EnumToString, IrIntrinsicOp.EnumIsDefined, IrIntrinsicOp.EnumParse, IrIntrinsicOp.EnumHasFlag, IrIntrinsicOp.EnumNames, IrIntrinsicOp.EnumValues
  ];

  private static readonly HashSet<IrIntrinsicOp> _operations = [.. Operations];

  /// <summary>Whether an operation is one on enums.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for the members of <see cref="Operations"/>.</returns>
  public static bool IsEnum(IrIntrinsicOp op) => _operations.Contains(op);

  /// <summary>Whether an operation on enums takes the types, or <see langword="null"/> for an operation that is none.</summary>
  internal static bool? Accepts(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> arguments) {
    if (!IsEnum(op))
      return null;

    var first = arguments.Count > 0 ? arguments[0] : IrType.Void;
    var one = arguments.Count == 1 && first.IsEnum;
    return op switch {
      IrIntrinsicOp.EnumToString => one && result == IrType.String,
      IrIntrinsicOp.EnumIsDefined => one && result == IrType.Bool,
      IrIntrinsicOp.EnumParse => result.IsEnum && arguments.Count == 2 && first == IrType.String && arguments[1] == IrType.Bool,
      IrIntrinsicOp.EnumHasFlag => result == IrType.Bool && arguments.Count == 2 && first.IsEnum && arguments[1] == first,
      IrIntrinsicOp.EnumNames => one && result == IrType.Array(IrType.String),
      _ => one && result == IrType.Array(first)
    };
  }

  /// <summary>
  /// What is wrong with a well-typed node beyond its types, or <see langword="null"/>: the name of a
  /// value, the order of the names and that of the values are not defined by .NET for an enum whose
  /// members repeat a value.
  /// </summary>
  /// <param name="intrinsic">The node.</param>
  /// <returns>The problem, or <see langword="null"/>.</returns>
  public static string? Problem(IrIntrinsic intrinsic)
    => intrinsic.Op is IrIntrinsicOp.EnumToString or IrIntrinsicOp.EnumNames or IrIntrinsicOp.EnumValues && intrinsic.Arguments.Count == 1
      && intrinsic.Arguments[0].Type.EnumType is { HasRepeatedValues: true } repeated
      ? $"{intrinsic.Op} of {repeated.Name}, whose members repeat a value, for which .NET defines no name and no order"
      : null;

  /// <summary>The enum a node of an operation on enums is about.</summary>
  /// <param name="intrinsic">A well-typed node of one of <see cref="Operations"/>.</param>
  /// <returns>The enum.</returns>
  public static IrEnumType Of(IrIntrinsic intrinsic)
    => (intrinsic.Op == IrIntrinsicOp.EnumParse ? intrinsic.Type : intrinsic.Arguments[0].Type).EnumType!;

  /// <summary>A node of <see cref="IrIntrinsicOp.EnumNames"/> or <see cref="IrIntrinsicOp.EnumValues"/>, whose argument only names the enum.</summary>
  /// <param name="op">The operation.</param>
  /// <param name="type">The enum.</param>
  /// <returns>The node.</returns>
  public static IrIntrinsic Listing(IrIntrinsicOp op, IrEnumType type)
    => new(op, op == IrIntrinsicOp.EnumNames ? IrType.Array(IrType.String) : IrType.Array(type.Type), [new IrConst(type.Type, 0)]);

  /// <summary>
  /// .NET's <c>Enum.ToString()</c>: the name of the member with the value; for <c>[Flags]</c> the name
  /// of the zero member for 0 ("0" without one), the name of a member with exactly the value, or else
  /// the names of the members taken greedily from the largest value down while each one's bits are all
  /// set, joined by <c>", "</c> in ascending order of value, when together they cover every set bit;
  /// otherwise the decimal digits of the value in the underlying type.
  /// </summary>
  /// <param name="type">The enum.</param>
  /// <param name="value">The value as the IR carries it.</param>
  /// <returns>The text.</returns>
  public static string Format(IrEnumType type, long value) {
    var digits = IrOperators.Number(type.Underlying, value).ToString(CultureInfo.InvariantCulture);
    var bits = type.Unsigned(value);
    var ordered = type.Ordered;
    if (ordered.FirstOrDefault(member => type.Unsigned(member.Value) == bits) is { } named)
      return named.Name;

    if (!type.IsFlags || bits == 0)
      return digits;

    var rest = bits;
    var found = new List<string>();
    for (var index = ordered.Count - 1; index >= 0 && rest != 0; --index) {
      var flag = type.Unsigned(ordered[index].Value);
      if (flag != 0 && (rest & flag) == flag) {
        rest &= ~flag;
        found.Add(ordered[index].Name);
      }
    }

    return rest != 0 ? digits : string.Join(", ", Enumerable.Reverse(found));
  }

  /// <summary>.NET's <c>Enum.IsDefined</c>: whether a member has exactly the value; a combination of flags is none.</summary>
  /// <param name="type">The enum.</param>
  /// <param name="value">The value as the IR carries it.</param>
  /// <returns><see langword="true"/> when a member has the value.</returns>
  public static bool IsDefined(IrEnumType type, long value) => type.MemberOf(value) is not null;

  /// <summary>
  /// .NET's <c>Enum.Parse(Type, string, bool)</c> from .NET Core 3.0 on: the text without the white
  /// space of <c>char.IsWhiteSpace</c> at its start; one that begins with a digit, <c>-</c> or <c>+</c>
  /// is the number <c>Parse</c> of the underlying type reads (a number outside it traps
  /// <see cref="IrTrap.IntegerOverflow"/>); otherwise, and when that is no number, names separated by
  /// commas, each without white space at both ends, every one the name of a member, ordinal or ignoring
  /// case as <c>OrdinalIgnoreCase</c> does - the first member in .NET's order whose name matches - and
  /// the result the bits of all of them. Any enum takes a list, also one without <c>[Flags]</c>. An
  /// empty text, an empty name and an unknown one trap <see cref="IrTrap.InvalidFormat"/>.
  /// </summary>
  /// <param name="type">The enum.</param>
  /// <param name="text">The text.</param>
  /// <param name="ignoreCase">Whether names are matched ignoring case.</param>
  /// <returns>The value as the IR carries it.</returns>
  /// <exception cref="IrTrapException">The text names no value, or its number does not fit the underlying type.</exception>
  public static long Parse(IrEnumType type, string text, bool ignoreCase) {
    var trimmed = StringOperations.Trim(text, WhitespaceSet.DotNet, true, false);
    if (trimmed.Length == 0)
      throw new IrTrapException(IrTrap.InvalidFormat);

    if (char.IsAsciiDigit(trimmed[0]) || trimmed[0] is '-' or '+') {
      try {
        return (long)NumberTextOperations.ParseDotNet(trimmed, type.Underlying);
      } catch (IrTrapException malformed) when (malformed.Trap == IrTrap.InvalidFormat) {
        // No number: a name may still begin so, which no language of the IR allows, but .NET looks.
      }
    }

    var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    var ordered = type.Ordered;
    var bits = 0UL;
    foreach (var part in trimmed.Split(',')) {
      var name = StringOperations.Trim(part, WhitespaceSet.DotNet, true, true);
      var member = ordered.FirstOrDefault(candidate => string.Equals(candidate.Name, name, comparison))
        ?? throw new IrTrapException(IrTrap.InvalidFormat);
      bits |= type.Unsigned(member.Value);
    }

    return IrOperators.Wrap(type.Underlying, unchecked((long)bits));
  }

  /// <summary>.NET's <c>Enum.GetNames</c>: the names in the order of the unsigned values.</summary>
  /// <param name="type">The enum.</param>
  /// <returns>The names.</returns>
  public static string[] Names(IrEnumType type) => [.. type.Ordered.Select(member => member.Name)];

  /// <summary>.NET's <c>Enum.GetValues</c>: the values in the order of their unsigned values.</summary>
  /// <param name="type">The enum.</param>
  /// <returns>The values as the IR carries them.</returns>
  public static long[] Values(IrEnumType type) => [.. type.Ordered.Select(member => member.Value)];

  /// <summary>Evaluates a node of a well-typed module but <see cref="IrIntrinsicOp.EnumValues"/>, whose array the interpreter builds, on the values of its arguments.</summary>
  internal static object Evaluate(IrIntrinsic intrinsic, object[] arguments) {
    var type = Of(intrinsic);
    return intrinsic.Op switch {
      IrIntrinsicOp.EnumToString => Format(type, (long)arguments[0]),
      IrIntrinsicOp.EnumIsDefined => IsDefined(type, (long)arguments[0]),
      IrIntrinsicOp.EnumParse => Parse(type, (string)arguments[0], (bool)arguments[1]),
      IrIntrinsicOp.EnumHasFlag => ((long)arguments[0] & (long)arguments[1]) == (long)arguments[1],
      _ => Names(type)
    };
  }

}
