using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// Numbers and truth values to text and back, as .NET Core 3.0 and later does it with the invariant
/// culture: the constructs <see cref="CSharpNumberTextApi"/> lists. A conversion that leaves the
/// culture to the thread - <c>ToString()</c>, <c>"…" + x</c>, an interpolation, <c>Parse(s)</c> - is
/// imported as one with the invariant culture, and the module carries the caveat
/// <see cref="CSharpFrontend.InvariantCulture"/>; any other culture, a format outside the subset or
/// <c>NumberStyles</c> is refused by name.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>Whether the module converts a number by the culture of the thread, which the import fixes to the invariant culture.</summary>
  private bool _cultureImplied;

  /// <summary>Whether a type is one of the numbers of .NET the conversions take: the integers of 8 to 64 bits, <c>float</c> and <c>double</c>.</summary>
  private static bool IsNumber(ITypeSymbol? type) => type?.SpecialType is SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16
    or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
    or SpecialType.System_Single or SpecialType.System_Double;

  /// <summary>Whether a format writes hexadecimal digits, which no culture changes: <c>x</c> or <c>X</c> with its padding.</summary>
  private static bool IsHexadecimal(string? format) => format is { Length: > 0 } && format[0] is 'x' or 'X';

  /// <summary>An expression as the value of its own type, not of what the compiler converts it to - the object of a concatenation or of <c>string.Concat</c>.</summary>
  private IrExpr Own(ExpressionSyntax expression) => this.Natural(expression, this.Map(model.GetTypeInfo(expression).Type, expression));

  /// <summary>Whether an argument is the invariant culture: <c>CultureInfo.InvariantCulture</c> or <c>NumberFormatInfo.InvariantInfo</c>.</summary>
  private bool IsInvariant(ExpressionSyntax argument)
    => model.GetSymbolInfo(argument).Symbol is IPropertySymbol { Name: "InvariantCulture" or "InvariantInfo" } property
      && property.ContainingType.ToDisplayString() is "System.Globalization.CultureInfo" or "System.Globalization.NumberFormatInfo";

  /// <summary>The name of a member as a diagnostic gives it: <c>Double.ToString(String, IFormatProvider)</c>.</summary>
  private static string Member(IMethodSymbol method)
    => $"{method.ContainingType.Name}.{method.Name}({string.Join(", ", method.Parameters.Select(parameter => parameter.Type.Name))})";

  /// <summary>A number or a truth value as .NET writes it: the format of <see cref="Formatted"/>, a truth value as <c>True</c> or <c>False</c>.</summary>
  /// <param name="value">The value, of a numeric type or <c>bool</c>.</param>
  /// <param name="format">The format string, or <see langword="null"/> for none.</param>
  /// <param name="node">Where the conversion stands.</param>
  /// <param name="member">The member or construct a refusal names.</param>
  private IrExpr Text(IrExpr value, string? format, SyntaxNode node, string member) {
    if (value.Type == IrType.Bool)
      return format is null ? new IrIntrinsic(IrIntrinsicOp.FormatBoolDotNet, IrType.String, [value]) : throw new UnsupportedException(node, $"{member} of a bool with a format");

    return this.Formatted(value, format, node, member);
  }

  /// <summary>
  /// A number in a standard format of .NET: none, <c>G</c> and <c>R</c> the shortest digits that read
  /// back, <c>F&lt;n&gt;</c> the exact value to 0 to 15 digits (an integer of at most 32 bits through
  /// <c>double</c>, which holds it exactly), <c>D&lt;n&gt;</c> and <c>x&lt;n&gt;</c>/<c>X&lt;n&gt;</c> the
  /// decimal and hexadecimal digits of an integer. Any other format is refused.
  /// </summary>
  private IrExpr Formatted(IrExpr value, string? format, SyntaxNode node, string member) {
    var type = value.Type;
    var letter = string.IsNullOrEmpty(format) ? 'G' : char.ToUpperInvariant(format[0]);
    var count = format is { Length: > 1 } && int.TryParse(format[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null;
    var plain = format is null || format.Length == 1;
    UnsupportedException Refused() => new(node, $"{member} with the format \"{format}\"");
    switch (letter) {
      case 'G' when plain:
      case 'R' when plain && type.IsFloat:
        return new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [value]);
      case 'F' when (plain || count <= NumberTextOperations.MaxDotNetDigits) && (type.IsFloat || type.Bits <= 32):
        // "F" alone is "F2", the digits of the invariant culture.
        return NumberTextOperations.Fixed(IrIntrinsicOp.FixedDotNet, Convert(value, IrType.F64), count ?? 2);
      case 'D' when (plain || count <= NumberTextOperations.MaxPadding) && type.IsInteger:
        return NumberTextOperations.Radix(value, 10, count ?? 0, false, RadixMode.SignMagnitude);
      case 'X' when (plain || count <= NumberTextOperations.MaxPadding) && type.IsInteger:
        return NumberTextOperations.Radix(value, 16, count ?? 0, format![0] == 'X', RadixMode.TwoComplement);
      default:
        throw Refused();
    }
  }

  /// <summary>
  /// A call that converts between numbers and text, or <see langword="null"/> for a member of another
  /// kind: <c>ToString</c> of a number or a <c>bool</c>, <c>Parse</c> of a number, <c>Convert.ToString</c>
  /// and <c>string.Concat</c> of numbers among strings.
  /// </summary>
  private IrExpr? NumberTextCall(InvocationExpressionSyntax invocation, IMethodSymbol method, List<ExpressionSyntax> operands) {
    var owner = method.ContainingType;
    var member = Member(method);
    string? Format(ExpressionSyntax argument) => model.GetConstantValue(argument) is { HasValue: true, Value: string text }
      ? text
      : throw new UnsupportedException(argument, $"{member} with a format that is no constant");

    // The provider has to be the invariant culture; without one the culture of the thread is meant, which the import fixes.
    void Provider(int? index) {
      if (index is null)
        this._cultureImplied = true;
      else if (!this.IsInvariant(operands[index.Value]))
        throw new UnsupportedException(operands[index.Value], $"{member} with the provider {operands[index.Value]}, which is a culture other than the invariant one");
    }

    if (method.Name == "ToString" && !method.IsStatic && (IsNumber(owner) || owner.SpecialType == SpecialType.System_Boolean)
        && invocation.Expression is MemberAccessExpressionSyntax access) {
      var types = method.Parameters.Select(parameter => parameter.Type.SpecialType).ToList();
      var format = types.Count > 0 && types[0] == SpecialType.System_String ? Format(operands[0]) : null;
      var provider = types.Count > 0 && types[^1] != SpecialType.System_String ? types.Count - 1 : (int?)null;
      if (owner.SpecialType != SpecialType.System_Boolean && !IsHexadecimal(format))
        Provider(provider);

      return this.Text(this.Own(access.Expression), format, invocation, member);
    }

    if (method.Name is "Parse" or "TryParse" && method.IsStatic && IsNumber(owner)) {
      if (method.Name == "TryParse")
        throw new UnsupportedException(invocation, $"{member}, whose result is an out parameter (#216)");
      if (method.Parameters.Any(parameter => parameter.Type.Name == "NumberStyles"))
        throw new UnsupportedException(invocation, $"{member} with NumberStyles");
      if (method.Parameters[0].Type.SpecialType != SpecialType.System_String)
        throw new UnsupportedException(invocation, member);

      Provider(operands.Count == 2 ? 1 : null);
      return new IrIntrinsic(IrIntrinsicOp.ParseDotNet, this.Map(owner, invocation), [this.Value(operands[0])]);
    }

    if (owner.ToDisplayString() == "System.Convert" && method.Name == "ToString" && operands.Count > 0) {
      var from = method.Parameters[0].Type;
      var value = Convert(this.Own(operands[0]), this.Map(from, invocation));
      if (operands.Count == 2 && method.Parameters[1].Type.SpecialType == SpecialType.System_Int32 && from.SpecialType != SpecialType.System_Boolean) {
        // Convert.ToString(v, 2 | 8 | 16) writes the bits of the parameter's type, 10 a minus sign.
        return model.GetConstantValue(operands[1]) is { HasValue: true, Value: int radix and (2 or 8 or 10 or 16) }
          ? NumberTextOperations.Radix(value, radix, 0, false, radix == 10 ? RadixMode.SignMagnitude : RadixMode.TwoComplement)
          : throw new UnsupportedException(operands[1], $"{member} with a radix other than the constant 2, 8, 10 or 16");
      }

      if (IsNumber(from) || from.SpecialType == SpecialType.System_Boolean) {
        if (from.SpecialType != SpecialType.System_Boolean)
          Provider(operands.Count == 2 ? 1 : null);

        return this.Text(value, null, invocation, member);
      }
    }

    // string.Concat of strings, code units, numbers and truth values: each a piece of the concatenation.
    if (owner.SpecialType == SpecialType.System_String && method.Name == "Concat" && operands.Count >= 2
        && operands.Any(operand => IsNumber(model.GetTypeInfo(operand).Type) || model.GetTypeInfo(operand).Type?.SpecialType == SpecialType.System_Boolean))
      return Concatenated([.. operands.Select(operand => this.Piece(operand, model.GetTypeInfo(operand).Type))]);

    return null;
  }

}
