using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// The strings of C#: the constructs <see cref="CSharpStringApi"/> lists, each as the operations of
/// the IR it means. Every comparison and search is ordinal; a member that compares by culture is
/// refused with the advice to pass <c>StringComparison.Ordinal</c>.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The value of <c>StringComparison.Ordinal</c>.</summary>
  private const int Ordinal = (int)StringComparison.Ordinal;

  private static IrIntrinsic Str(IrIntrinsicOp op, IrType type, params IrExpr[] arguments) => new(op, type, arguments);

  /// <summary>A code unit as the string of it, a string as it is.</summary>
  private static IrExpr Text(IrExpr value) => value.Type.IsString ? value : Str(IrIntrinsicOp.StrFromCodeUnit, IrType.String, value);

  /// <summary>A constant the compiler folded to a string, or the refusal of the constant <c>null</c> of the type string.</summary>
  private static IrStrConst StringConstant(object? folded, SyntaxNode node)
    => folded as string is { } text ? new IrStrConst(text) : throw new UnsupportedException(node, "null of the type string");

  /// <summary>
  /// The parts of a concatenation, flattened: each operand of a <c>+</c> whose type is string, in
  /// order; a <c>char</c> becomes the string of it, any other type is refused - it would be a number
  /// converted to text.
  /// </summary>
  private void Pieces(ExpressionSyntax expression, List<IrExpr> pieces) {
    if (expression is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } sum && model.GetTypeInfo(sum).Type?.SpecialType == SpecialType.System_String
        && model.GetConstantValue(sum) is not { HasValue: true }) {
      this.Pieces(sum.Left, pieces);
      this.Pieces(sum.Right, pieces);
      return;
    }

    pieces.Add(this.Piece(expression, model.GetTypeInfo(expression).Type));
  }

  /// <summary>One part of a concatenation or an interpolation: a string, or a code unit as its string.</summary>
  private IrExpr Piece(ExpressionSyntax expression, ITypeSymbol? type) => type?.SpecialType switch {
    SpecialType.System_String => this.Natural(expression, IrType.String),
    SpecialType.System_Char => Text(this.Natural(expression, IrType.U16)),
    _ => throw new UnsupportedException(expression, $"string + {type?.ToDisplayString() ?? "null"}, {CSharpStringApi.NumberText}")
  };

  /// <summary>Strings joined, as one operation: two parts at least; one is itself.</summary>
  private static IrExpr Concatenated(List<IrExpr> pieces)
    => pieces.Count == 1 ? pieces[0] : Str(IrIntrinsicOp.StrConcat, IrType.String, [.. pieces]);

  /// <summary>An interpolated string: its texts and its holes, each of which has to be a string or a code unit, joined.</summary>
  private IrExpr Interpolated(InterpolatedStringExpressionSyntax interpolated) {
    var pieces = new List<IrExpr>();
    foreach (var content in interpolated.Contents)
      if (content is InterpolatedStringTextSyntax text)
        pieces.Add(new IrStrConst(text.TextToken.ValueText));
      else if (content is InterpolationSyntax { AlignmentClause: null, FormatClause: null } hole)
        pieces.Add(this.Piece(hole.Expression, model.GetTypeInfo(hole.Expression).Type));
      else
        throw new UnsupportedException(content, "an interpolation with an alignment or a format");

    return pieces.Count == 0 ? new IrStrConst("") : Concatenated(pieces);
  }

  /// <summary>
  /// A binary operator on strings, or <see langword="null"/> when the operator is none: <c>+</c>, which
  /// concatenates; <c>==</c> and <c>!=</c>, which compare by code unit; and a comparison of the result
  /// of <c>string.CompareOrdinal</c> against 0, which is all .NET documents of that result.
  /// </summary>
  private IrExpr? StringBinary(BinaryExpressionSyntax binary) {
    if (this.OrdinalComparison(binary) is { } ordinal)
      return ordinal;

    var left = model.GetTypeInfo(binary.Left).Type?.SpecialType;
    var right = model.GetTypeInfo(binary.Right).Type?.SpecialType;
    var isString = left == SpecialType.System_String || right == SpecialType.System_String;
    switch (binary.Kind()) {
      case SyntaxKind.AddExpression when model.GetTypeInfo(binary).Type?.SpecialType == SpecialType.System_String:
        var pieces = new List<IrExpr>();
        this.Pieces(binary, pieces);
        return Concatenated(pieces);
      case SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression when isString:
        var equal = Str(IrIntrinsicOp.StrEquals, IrType.Bool, this.Value(binary.Left), this.Value(binary.Right));
        return binary.IsKind(SyntaxKind.EqualsExpression) ? equal : new IrUnary(IrUnaryOp.LogicalNot, equal);
      default:
        return null;
    }
  }

  /// <summary>
  /// <c>string.CompareOrdinal(a, b)</c> or <c>string.Compare(a, b, StringComparison.Ordinal)</c> on one
  /// side of a comparison and the constant 0 on the other, as the comparison of the operation's sign
  /// with 0; <see langword="null"/> for any other comparison.
  /// </summary>
  private IrExpr? OrdinalComparison(BinaryExpressionSyntax binary) {
    IrBinaryOp? op = binary.Kind() switch {
      SyntaxKind.LessThanExpression => IrBinaryOp.Less,
      SyntaxKind.LessThanOrEqualExpression => IrBinaryOp.LessOrEqual,
      SyntaxKind.GreaterThanExpression => IrBinaryOp.Greater,
      SyntaxKind.GreaterThanOrEqualExpression => IrBinaryOp.GreaterOrEqual,
      SyntaxKind.EqualsExpression => IrBinaryOp.Equal,
      SyntaxKind.NotEqualsExpression => IrBinaryOp.NotEqual,
      _ => null
    };
    bool IsZero(ExpressionSyntax side) => model.GetConstantValue(side) is { HasValue: true, Value: int and 0 };
    if (op is null)
      return null;

    // With the 0 on the left the comparison is turned around: 0 < c is c > 0.
    var (call, flipped) = IsZero(binary.Right) ? (binary.Left, false) : IsZero(binary.Left) ? (binary.Right, true) : (null, false);
    if (call is not InvocationExpressionSyntax invocation || this.OrdinalOperands(invocation) is not { } operands)
      return null;

    var comparison = flipped ? op.Value switch {
      IrBinaryOp.Less => IrBinaryOp.Greater,
      IrBinaryOp.LessOrEqual => IrBinaryOp.GreaterOrEqual,
      IrBinaryOp.Greater => IrBinaryOp.Less,
      IrBinaryOp.GreaterOrEqual => IrBinaryOp.LessOrEqual,
      _ => op.Value
    } : op.Value;
    return new IrBinary(comparison, Str(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, this.Value(operands.Left), this.Value(operands.Right)), new IrConst(IrType.I32, 0));
  }

  /// <summary>The two strings of a call of <c>string.CompareOrdinal(a, b)</c> or <c>string.Compare(a, b, StringComparison.Ordinal)</c>, or <see langword="null"/> for any other call.</summary>
  private (ExpressionSyntax Left, ExpressionSyntax Right)? OrdinalOperands(InvocationExpressionSyntax invocation) {
    if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol { ContainingType.SpecialType: SpecialType.System_String, IsStatic: true } method)
      return null;

    var arguments = invocation.ArgumentList.Arguments;
    return (method.Name, arguments.Count) switch {
      ("CompareOrdinal", 2) => (arguments[0].Expression, arguments[1].Expression),
      ("Compare", 3) when this.IsOrdinal(arguments[2].Expression) => (arguments[0].Expression, arguments[1].Expression),
      _ => null
    };
  }

  /// <summary>Whether an expression is a string.</summary>
  private bool IsString(ExpressionSyntax expression) => model.GetTypeInfo(expression).Type?.SpecialType == SpecialType.System_String;

  /// <summary>Whether an argument is the constant <c>StringComparison.Ordinal</c>.</summary>
  private bool IsOrdinal(ExpressionSyntax argument) => model.GetConstantValue(argument) is { HasValue: true, Value: int and Ordinal };

  /// <summary>A member of a string or a code unit by its name and its parameter types, as a diagnostic names it: <c>StartsWith(string)</c>.</summary>
  private static string Signature(IMethodSymbol method)
    => $"{method.Name}({string.Join(", ", method.Parameters.Select(parameter => parameter.Type.ToDisplayString().Replace("?", "", StringComparison.Ordinal)))})";

  /// <summary>The refusal of a member that compares by culture.</summary>
  private static UnsupportedException ByCulture(SyntaxNode node, IMethodSymbol method)
    => new(node, $"string.{Signature(method)}, {CSharpStringApi.Culture}");

  /// <summary>
  /// A call of a member of <c>string</c> or <c>char</c>, as the operations of the IR it means;
  /// <see langword="null"/> for a member of another type. A member of the two that the table does not
  /// list is refused by name, one that compares by culture with the advice to pass
  /// <c>StringComparison.Ordinal</c>.
  /// </summary>
  private IrExpr? StringCall(InvocationExpressionSyntax invocation, IMethodSymbol method, List<ExpressionSyntax> operands) {
    var owner = method.ContainingType.SpecialType;
    if (owner is not (SpecialType.System_String or SpecialType.System_Char))
      return null;

    IrExpr Receiver() => invocation.Expression is MemberAccessExpressionSyntax access ? this.Value(access.Expression) : throw new UnsupportedException(invocation, Signature(method));
    IrExpr Operand(int index) => this.Value(operands[index]);
    var types = method.Parameters.Select(parameter => parameter.Type.SpecialType).ToList();
    var unit = types.Count > 0 && types[0] == SpecialType.System_Char;
    var comparison = types.Count > 0 && method.Parameters[^1].Type.Name == "StringComparison";
    if (comparison && !this.IsOrdinal(operands[^1]))
      throw ByCulture(invocation, method);

    // Of char only ToString is a string operation; every other member of it is refused by name as the library is.
    if (owner == SpecialType.System_Char)
      return method.Name == "ToString" && operands.Count == (method.IsStatic ? 1 : 0)
        ? Str(IrIntrinsicOp.StrFromCodeUnit, IrType.String, method.IsStatic ? Operand(0) : Receiver())
        : null;

    switch (method.Name, method.IsStatic, operands.Count) {
      case ("Equals", true, 2 or 3) when types[0] == SpecialType.System_String && types[1] == SpecialType.System_String:
        return Str(IrIntrinsicOp.StrEquals, IrType.Bool, Operand(0), Operand(1));
      case ("Equals", false, 1 or 2) when types[0] == SpecialType.System_String:
        return Str(IrIntrinsicOp.StrEquals, IrType.Bool, Receiver(), Operand(0));
      case ("CompareOrdinal", true, 2):
      case ("Compare", true, 3) when comparison:
        throw new UnsupportedException(invocation, $"the value of {method.Name} other than its sign, which .NET documents only by its sign");
      case ("Compare" or "CompareTo", _, _):
        throw ByCulture(invocation, method);
      // Concat and Join take their strings as separate parameters, a params array or a params span, whichever the compiler chose.
      case ("Concat", true, >= 2) when operands.All(this.IsString):
        return Str(IrIntrinsicOp.StrConcat, IrType.String, [.. operands.Select(this.Value)]);
      case ("Join", true, 2) when types[0] == SpecialType.System_String && model.GetTypeInfo(operands[1]).Type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_String }:
        return Str(IrIntrinsicOp.StrJoin, IrType.String, Operand(0), this.Read(operands[1]));
      case ("Join", true, >= 2) when types[0] == SpecialType.System_String && operands.Skip(1).All(this.IsString):
        return Str(IrIntrinsicOp.StrJoin, IrType.String, Operand(0), new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.String), [.. operands.Skip(1).Select(this.Value)]));
      case ("Substring", false, 1 or 2):
        return Str(IrIntrinsicOp.StrSubstring, IrType.String, [Receiver(), .. operands.Select(this.Value)]);
      case ("IndexOf", false, 1 or 2) when unit && types.Skip(1).All(type => type == SpecialType.System_Int32):
        return Str(IrIntrinsicOp.StrIndexOf, IrType.I32, [Receiver(), .. operands.Select(this.Value)]);
      case ("IndexOf", false, 2 or 3) when comparison && types[0] == SpecialType.System_String && types[1..^1].All(type => type == SpecialType.System_Int32):
        return Str(IrIntrinsicOp.StrIndexOf, IrType.I32, [Receiver(), .. operands.SkipLast(1).Select(this.Value)]);
      case ("LastIndexOf", false, 1) when unit:
      case ("LastIndexOf", false, 2) when comparison && types[0] == SpecialType.System_String:
        return Str(IrIntrinsicOp.StrLastIndexOf, IrType.I32, Receiver(), Operand(0));
      case ("Contains", false, 1 or 2) when unit || types[0] == SpecialType.System_String:
      case ("StartsWith", false, 1) when unit:
      case ("StartsWith", false, 2) when comparison:
      case ("EndsWith", false, 1) when unit:
      case ("EndsWith", false, 2) when comparison:
        var op = method.Name switch { "Contains" => IrIntrinsicOp.StrContains, "StartsWith" => IrIntrinsicOp.StrStartsWith, _ => IrIntrinsicOp.StrEndsWith };
        return Str(op, IrType.Bool, Receiver(), Operand(0));
      case ("IndexOf" or "LastIndexOf" or "StartsWith" or "EndsWith", false, _) when types[0] == SpecialType.System_String && !comparison:
        throw ByCulture(invocation, method);
      case ("Replace", false, 2):
      case ("Replace", false, 3) when comparison:
        return Str(IrIntrinsicOp.StrReplaceAll, IrType.String, Receiver(), Operand(0), Operand(1));
      case ("Split", false, _):
        return this.Split(invocation, method, Receiver, operands);
      case ("Trim" or "TrimStart" or "TrimEnd", false, 0):
        var trim = method.Name switch { "Trim" => IrIntrinsicOp.StrTrim, "TrimStart" => IrIntrinsicOp.StrTrimStart, _ => IrIntrinsicOp.StrTrimEnd };
        return Str(trim, IrType.String, Receiver(), StringOperations.Set(WhitespaceSet.DotNet));
      case ("PadLeft" or "PadRight", false, 1 or 2):
        var filler = operands.Count == 1 ? new IrStrConst(" ") : Text(Operand(1));
        return Str(method.Name == "PadLeft" ? IrIntrinsicOp.StrPadStart : IrIntrinsicOp.StrPadEnd, IrType.String, Receiver(), Operand(0), filler);
      case ("ToCharArray", false, 0):
        return Str(IrIntrinsicOp.StrToCodeUnits, IrType.Array(IrType.U16), Receiver());
      case ("ToUpper" or "ToLower" or "ToUpperInvariant" or "ToLowerInvariant", _, _):
        throw new UnsupportedException(invocation, $"string.{Signature(method)}, which is case mapping");
      default:
        throw new UnsupportedException(invocation, $"string.{Signature(method)}");
    }
  }

  /// <summary>
  /// A split at one code unit, or at a constant string that is not empty, without a count: .NET keeps
  /// the rest of the string in the last part of a split with a count, which no operation of the IR
  /// does, and splits at an empty string otherwise than ECMA-262.
  /// </summary>
  private IrExpr Split(InvocationExpressionSyntax invocation, IMethodSymbol method, Func<IrExpr> receiver, List<ExpressionSyntax> operands) {
    var first = method.Parameters.Length > 0 ? method.Parameters[0].Type : null;
    var unlimited = new IrConst(IrType.I32, -1);
    bool NoOptions(int from) => operands.Count == from || (operands.Count == from + 1 && method.Parameters[from].Type.Name == "StringSplitOptions"
      && model.GetConstantValue(operands[from]) is { HasValue: true, Value: int and 0 });
    switch (first) {
      case { SpecialType: SpecialType.System_Char } when NoOptions(1):
        return Str(IrIntrinsicOp.StrSplit, IrType.Array(IrType.String), receiver(), this.Value(operands[0]), unlimited);
      case IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Char } when method.Parameters[0].IsParams && operands.Count == 1
          && model.GetTypeInfo(operands[0]).Type?.SpecialType == SpecialType.System_Char:
        return Str(IrIntrinsicOp.StrSplit, IrType.Array(IrType.String), receiver(), this.Value(operands[0]), unlimited);
      case { SpecialType: SpecialType.System_String } when NoOptions(1) && model.GetConstantValue(operands[0]) is { HasValue: true, Value: string { Length: > 0 } }:
        return Str(IrIntrinsicOp.StrSplit, IrType.Array(IrType.String), receiver(), this.Value(operands[0]), unlimited);
      case IArrayTypeSymbol when method.Parameters.Length > 1 && method.Parameters[1].Type.SpecialType == SpecialType.System_Int32:
        throw new UnsupportedException(invocation, $"string.{Signature(method)}, whose last part keeps the rest of the string");
      default:
        throw new UnsupportedException(invocation, $"string.{Signature(method)}");
    }
  }

  /// <summary><c>new string(char, int)</c>, <c>new string(char[])</c> and <c>new string(char[], int, int)</c>.</summary>
  private IrExpr StringCreation(BaseObjectCreationExpressionSyntax creation) {
    var arguments = creation.ArgumentList?.Arguments.Select(argument => argument.Expression).ToList() ?? [];
    var types = arguments.Select(argument => model.GetTypeInfo(argument).Type).ToList();
    if (arguments.Count == 2 && types[0]?.SpecialType == SpecialType.System_Char)
      return Str(IrIntrinsicOp.StrRepeat, IrType.String, this.Value(arguments[0]), this.Value(arguments[1]));

    if (arguments.Count is 1 or 3 && types[0] is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Char })
      return Str(IrIntrinsicOp.StrFromCodeUnits, IrType.String, [this.Read(arguments[0]), .. arguments.Skip(1).Select(this.Value)]);

    throw new UnsupportedException(creation, $"new string({string.Join(", ", types.Select(type => type?.ToDisplayString()))})");
  }

  /// <summary>
  /// The selector of a <c>switch</c> on a string: the value read once, then the index of the first
  /// section, in source order, one of whose labels is equal to it, or -1. The switch then chooses by
  /// that index, which keeps every <c>break</c> and <c>continue</c> of the sections what it was.
  /// </summary>
  private IrExpr StringSelector(SwitchStatementSyntax choice, IrExpr value, List<IrStmt> before) {
    var text = this.Once(value, before);
    IrExpr selector = new IrConst(IrType.I32, -1);
    for (var index = choice.Sections.Count - 1; index >= 0; --index) {
      IrExpr? matches = null;
      foreach (var label in choice.Sections[index].Labels.OfType<CaseSwitchLabelSyntax>()) {
        var constant = model.GetConstantValue(label.Value);
        IrExpr test = Str(IrIntrinsicOp.StrEquals, IrType.Bool, text, StringConstant(constant.HasValue ? constant.Value : null, label.Value));
        matches = matches is null ? test : new IrBinary(IrBinaryOp.LogicalOr, matches, test);
      }

      if (matches is not null)
        selector = new IrConditional(matches, new IrConst(IrType.I32, index), selector);
    }

    var chosen = this.Local($"$t{this._locals.Count}", IrType.I32);
    before.Add(new IrDeclare(chosen, selector));
    return new IrLocalRef(chosen);
  }

}
