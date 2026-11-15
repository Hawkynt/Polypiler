using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// Nullable value types and <c>null</c> references: <c>T?</c>, <c>HasValue</c>, <c>Value</c>,
/// <c>GetValueOrDefault</c>, <c>??</c>, <c>??=</c>, <c>?.</c> and <c>?[]</c>, and the lifted
/// operators, which are written out as ECMA-334 §12.4.8 defines them: an arithmetic, bitwise or shift
/// operator gives none when an operand is none, <c>==</c> and <c>!=</c> take two nones as equal, and
/// <c>&lt;</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&gt;=</c> are false when an operand is none. Both operands
/// are evaluated, left first, before any of that is decided. A reference annotation (<c>string?</c>,
/// <c>int[]?</c>) and the null-forgiving <c>!</c> change nothing.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The temporaries an operand evaluated once is assigned to, declared at the start of the function being lowered.</summary>
  private readonly List<IrLocal> _bound = [];

  /// <summary>The IR type of <c>Nullable&lt;T&gt;</c>, or <see langword="null"/> for any other type.</summary>
  private IrType? OptionalType(ITypeSymbol? type, SyntaxNode node) {
    if (type is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
      return null;

    // A nullable value type holds a number, a truth value or a struct, which is what the IR's optional type holds.
    return IrType.Optional(this.Map(nullable.TypeArguments[0], node));
  }

  /// <summary>Whether a type is <c>Nullable&lt;T&gt;</c>.</summary>
  private static bool IsNullableValue(ITypeSymbol? type) => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

  /// <summary>The declarations of the temporaries, which go in front of the body - behind the call of a chained constructor, which has to stay first.</summary>
  private void DeclareBound(List<IrStmt> statements) {
    var at = statements is [IrExprStmt { Expression: IrCall }, ..] ? 1 : 0;
    statements.InsertRange(at, this._bound.Select(local => (IrStmt)new IrDeclare(local, null)));
    this._bound.Clear();
  }

  /// <summary>
  /// An operand that is read more than once, evaluated once: as it stands when reading it twice is
  /// the same - a variable, a constant, none -, otherwise assigned to a temporary where it is
  /// evaluated first and read from it after that.
  /// </summary>
  private (IrExpr First, IrExpr Again) Bound(IrExpr value) {
    if (value is IrLocalRef or IrConst or IrNone or IrNull or IrConstRef or IrSome { Value: IrConst or IrLocalRef })
      return (value, value);

    var temporary = this.Local($"$t{this._locals.Count}", value.Type);
    this._bound.Add(temporary);
    return (new IrBind(temporary, value), new IrLocalRef(temporary));
  }

  /// <summary>The value an optional value is known to hold where it is read: the operand of a lifted operation after its test.</summary>
  private static IrExpr Held(IrExpr value) => value is IrSome some ? some.Value : new IrUnwrap(value) { IsTested = true };

  /// <summary>Whether the value is none: the test of an optional value, or of a reference for <c>null</c>.</summary>
  private static IrExpr Absent(IrExpr value) => value.Type.IsOptional ? new IrIsNone(value) : new IrIsNull(value);

  private static IrExpr Not(IrExpr value) => value is IrUnary { Op: IrUnaryOp.LogicalNot } negated ? negated.Operand : new IrUnary(IrUnaryOp.LogicalNot, value);

  private static IrConst Truth(bool value) => new(IrType.Bool, value ? 1 : 0);

  /// <summary>
  /// A value converted to a type where one of the two is optional: a value made optional, an optional
  /// value made another (none stays none), and the value of an optional one - which traps when it is none,
  /// as the explicit conversion of C# throws.
  /// </summary>
  private IrExpr Converted(IrExpr value, IrType type, bool trap = false) {
    var from = value.Type;
    if (from == type || !(from.IsOptional || type.IsOptional))
      return Convert(value, type, trap);

    if (!type.IsOptional)
      return Convert(new IrUnwrap(value), type, trap);

    if (!from.IsOptional)
      return new IrSome(Convert(value, type.Underlying!, trap));

    var (first, again) = this.Bound(value);
    return new IrConditional(new IrIsNone(first), new IrNone(type), new IrSome(Convert(Held(again), type.Underlying!, trap)));
  }

  /// <summary>
  /// An expression of nullable values or of <c>null</c> as its natural value, or <see langword="null"/>
  /// for any other.
  /// </summary>
  private IrExpr? OptionalNatural(ExpressionSyntax expression, IrType type) {
    switch (expression) {
      case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } forgiven:
        return this.Natural(forgiven.Operand, type);
      case MemberAccessExpressionSyntax { Name.Identifier.Text: "HasValue" or "Value" } member when IsNullableValue(model.GetTypeInfo(member.Expression).Type):
        var held = this.Value(member.Expression);
        return member.Name.Identifier.Text == "Value" ? new IrUnwrap(held) : Not(new IrIsNone(held));
      case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GetValueOrDefault" } member } invocation
          when IsNullableValue(model.GetTypeInfo(member.Expression).Type):
        var optional = this.Value(member.Expression);
        var fallback = invocation.ArgumentList.Arguments is [var given] ? this.Value(given.Expression) : Zero(optional.Type.Underlying!, invocation);
        return new IrCoalesce(optional, fallback);
      case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } coalesce:
        return this.Coalesce(coalesce, type);
      case ConditionalAccessExpressionSyntax access:
        return this.ConditionalAccess(access, type);
      case AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceAssignmentExpression } assignment:
        return this.CoalesceAssignmentValue(assignment, type);
      case BinaryExpressionSyntax binary when this.IsLifted(binary.Left, binary.Right):
        return this.LiftedBinary(binary);
      case PrefixUnaryExpressionSyntax unary when IsNullableValue(model.GetTypeInfo(unary.Operand).ConvertedType)
          && unary.Kind() is SyntaxKind.UnaryMinusExpression or SyntaxKind.UnaryPlusExpression or SyntaxKind.BitwiseNotExpression or SyntaxKind.LogicalNotExpression:
        return this.LiftedUnary(unary);
      case IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax constant } pattern when IsNull(constant.Expression) && this.IsNullTested(pattern.Expression):
        return Absent(this.Value(pattern.Expression));
      case IsPatternExpressionSyntax { Pattern: UnaryPatternSyntax { Pattern: ConstantPatternSyntax constant } } pattern when IsNull(constant.Expression) && this.IsNullTested(pattern.Expression):
        return Not(Absent(this.Value(pattern.Expression)));
      case IsPatternExpressionSyntax { Pattern: DeclarationPatternSyntax or RecursivePatternSyntax or TypePatternSyntax } pattern
          when model.GetTypeInfo(pattern.Expression).Type is not { TypeKind: TypeKind.Class or TypeKind.Interface }:
        throw new UnsupportedException(pattern, "a type pattern such as 'is int i'");
      default:
        return null;
    }
  }

  /// <summary>Whether <c>is null</c> tests a value that is nullable and no object, which the classes test themselves: a nullable value or an array.</summary>
  private bool IsNullTested(ExpressionSyntax expression) => model.GetTypeInfo(expression).Type is { } type && (IsNullableValue(type) || type is IArrayTypeSymbol);

  /// <summary>Whether a binary operator on the operands is one the compiler lifted: an operand is a nullable value, or an array compared with null.</summary>
  private bool IsLifted(ExpressionSyntax left, ExpressionSyntax right) {
    var (first, second) = (model.GetTypeInfo(left), model.GetTypeInfo(right));
    return IsNullableValue(first.ConvertedType) || IsNullableValue(second.ConvertedType)
      || (first.Type is IArrayTypeSymbol && IsNull(right)) || (second.Type is IArrayTypeSymbol && IsNull(left));
  }

  /// <summary>
  /// <c>a ?? b</c>: the value of <c>a</c> when it holds one, otherwise <c>b</c>, which is evaluated
  /// only then. Of a reference: the reference unless it is <c>null</c>.
  /// </summary>
  private IrExpr Coalesce(BinaryExpressionSyntax coalesce, IrType type) {
    var value = this.Read(coalesce.Left);
    if (value.Type.IsOptional) {
      var fallback = this.Value(coalesce.Right);
      return new IrCoalesce(value, type.IsOptional ? this.Converted(fallback, type) : fallback.Type.IsOptional ? new IrUnwrap(fallback) : Convert(fallback, type));
    }

    if (!value.Type.IsNullable)
      throw new UnsupportedException(coalesce, $"?? on the type {value.Type}");

    var (first, again) = this.Bound(value);
    return new IrConditional(new IrIsNull(first), this.Value(coalesce.Right), again);
  }

  /// <summary>
  /// <c>r?.m</c> and <c>r?[i]</c> of an array or an object: none - or <c>null</c> for a reference - when
  /// <c>r</c> is <c>null</c>, otherwise the member of <c>r</c>; <c>r</c> is evaluated once.
  /// </summary>
  private IrExpr ConditionalAccess(ConditionalAccessExpressionSyntax access, IrType type) {
    var receiver = this.Read(access.Expression);
    if (!receiver.Type.IsNullable)
      throw new UnsupportedException(access, $"?. on the type {receiver.Type}");

    var (first, again) = this.Bound(receiver);
    IrExpr member = access.WhenNotNull switch {
      MemberBindingExpressionSyntax { Name.Identifier.Text: "Length" } when receiver.Type.IsArray => new IrArrayLength(again),
      ElementBindingExpressionSyntax { ArgumentList.Arguments: [var index] } when receiver.Type.IsArray => new IrArrayLoad(again, this.Index(index.Expression)),
      MemberBindingExpressionSyntax binding when receiver.Type.IsObject && model.GetSymbolInfo(binding).Symbol is { } symbol && this._fields.TryGetValue(symbol, out var field)
        => new IrFieldLoad(again, field),
      _ => throw new UnsupportedException(access.WhenNotNull, "a null-conditional access of anything but a length, an element or a field")
    };
    var absent = type.IsOptional ? (IrExpr)new IrNone(type) : type.IsNullable ? new IrNull(type) : throw new UnsupportedException(access, $"?. of the type {type}");
    return new IrConditional(new IrIsNull(first), absent, type.IsOptional && !member.Type.IsOptional ? new IrSome(Convert(member, type.Underlying!)) : member);
  }

  /// <summary><c>x ??= v</c> as a statement: <c>v</c> is assigned only when <c>x</c> is none or <c>null</c>; the target is evaluated once.</summary>
  private void CoalesceAssignment(AssignmentExpressionSyntax assignment, List<IrStmt> into) {
    var target = this.Target(assignment.Left, into, true);
    var current = target.Read();
    into.Add(new IrIf(Absent(current), new IrBlock([target.Write(this.Converted(this.Value(assignment.Right), current.Type))]), null));
  }

  /// <summary><c>x ??= v</c> as a value, of a variable: the value of <c>x</c> when it holds one, otherwise <c>v</c>, assigned to it first.</summary>
  private IrExpr CoalesceAssignmentValue(AssignmentExpressionSyntax assignment, IrType type) {
    if (this.Value(assignment.Left) is not IrLocalRef { Local: var local } variable)
      throw new UnsupportedException(assignment, "??= of anything but a variable as a value");

    var assigned = new IrBind(local, this.Converted(this.Value(assignment.Right), local.Type));
    if (!local.Type.IsOptional)
      return new IrConditional(new IrIsNull(variable), assigned, variable);

    return type.IsOptional ? new IrCoalesce(variable, assigned) : new IrCoalesce(variable, new IrUnwrap(assigned) { IsTested = true });
  }

  /// <summary>A lifted unary operator: none for none, otherwise the operator on the value.</summary>
  private IrExpr LiftedUnary(PrefixUnaryExpressionSyntax unary) {
    var operand = this.Value(unary.Operand);
    var (first, again) = this.Bound(operand);
    IrExpr value = Held(again);
    if (!unary.IsKind(SyntaxKind.UnaryPlusExpression)) {
      var op = unary.Kind() switch {
        SyntaxKind.UnaryMinusExpression => value.Type.IsInteger && this.IsChecked(unary) ? IrUnaryOp.NegateChecked : IrUnaryOp.Negate,
        SyntaxKind.BitwiseNotExpression => IrUnaryOp.BitNot,
        _ => IrUnaryOp.LogicalNot
      };
      value = new IrUnary(op, value);
    }

    return new IrConditional(new IrIsNone(first), new IrNone(operand.Type), new IrSome(value));
  }

  /// <summary>
  /// A lifted binary operator, both operands evaluated first, left before right; the test that one
  /// is none follows. An operand that is not optional - a reference compared with <c>null</c>, or a
  /// constant the compiler made optional - is never none.
  /// </summary>
  private IrExpr LiftedBinary(BinaryExpressionSyntax binary) {
    var kind = binary.Kind();
    var equality = kind is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression;
    if (equality && (IsNull(binary.Left) || IsNull(binary.Right))) {
      // x == null: whether x holds nothing, x read as its own type rather than the object a reference is compared as.
      var tested = this.Read(IsNull(binary.Left) ? binary.Right : binary.Left);
      var absent = Absent(tested);
      return kind == SyntaxKind.EqualsExpression ? absent : Not(absent);
    }

    var left = this.Value(binary.Left);
    var right = this.Value(binary.Right);

    if (kind is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression or SyntaxKind.CoalesceExpression
        || ((kind is SyntaxKind.BitwiseAndExpression or SyntaxKind.BitwiseOrExpression or SyntaxKind.ExclusiveOrExpression) && (left.Type.Underlying ?? left.Type) == IrType.Bool))
      throw new UnsupportedException(binary, $"{kind} on {left.Type} and {right.Type}");

    var op = kind switch {
      SyntaxKind.AddExpression => IrBinaryOp.Add,
      SyntaxKind.SubtractExpression => IrBinaryOp.Subtract,
      SyntaxKind.MultiplyExpression => IrBinaryOp.Multiply,
      SyntaxKind.DivideExpression => IrBinaryOp.Divide,
      SyntaxKind.ModuloExpression => IrBinaryOp.Remainder,
      SyntaxKind.BitwiseAndExpression => IrBinaryOp.And,
      SyntaxKind.BitwiseOrExpression => IrBinaryOp.Or,
      SyntaxKind.ExclusiveOrExpression => IrBinaryOp.Xor,
      SyntaxKind.LeftShiftExpression => IrBinaryOp.ShiftLeft,
      SyntaxKind.RightShiftExpression => IrBinaryOp.ShiftRight,
      SyntaxKind.UnsignedRightShiftExpression => IrBinaryOp.ShiftRightLogical,
      SyntaxKind.EqualsExpression => IrBinaryOp.Equal,
      SyntaxKind.NotEqualsExpression => IrBinaryOp.NotEqual,
      SyntaxKind.LessThanExpression => IrBinaryOp.Less,
      SyntaxKind.LessThanOrEqualExpression => IrBinaryOp.LessOrEqual,
      SyntaxKind.GreaterThanExpression => IrBinaryOp.Greater,
      _ => IrBinaryOp.GreaterOrEqual
    };
    // A shift count is an int? the compiler made of an int; the IR shifts by an i32.
    if (op is IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical && right is IrSome { Value: var count })
      right = count;

    var (leftFirst, leftAgain) = this.Bound(left);
    var (rightFirst, rightAgain) = this.Bound(right);
    var leftOptional = left.Type.IsOptional && left is not IrSome;
    var rightOptional = right.Type.IsOptional && right is not IrSome;
    var value = Binary(op, leftOptional || left is IrSome ? Held(leftAgain) : leftAgain, rightOptional || right is IrSome ? Held(rightAgain) : rightAgain, binary, this.IsChecked(binary));

    if (equality && leftOptional && rightOptional) {
      // Two nones are equal; none and a value are not.
      var equal = kind == SyntaxKind.EqualsExpression;
      return new IrConditional(new IrIsNone(leftFirst), equal ? new IrIsNone(rightFirst) : Not(new IrIsNone(rightFirst)),
        new IrConditional(new IrIsNone(rightFirst), Truth(!equal), value));
    }

    // The operands are evaluated in order, the left one first, before the test; one that is never
    // none needs no test, and one without an effect may be evaluated after the other.
    IrExpr? test = (leftOptional, rightOptional) switch {
      (true, true) when leftFirst is IrBind && rightFirst is IrBind
        => new IrConditional(new IrIsNone(leftFirst), new IrBinary(IrBinaryOp.LogicalOr, new IrIsNone(rightFirst), Truth(true)), new IrIsNone(rightFirst)),
      (true, true) when rightFirst is IrBind => new IrBinary(IrBinaryOp.LogicalOr, new IrIsNone(rightFirst), new IrIsNone(leftFirst)),
      (true, true) => new IrBinary(IrBinaryOp.LogicalOr, new IrIsNone(leftFirst), new IrIsNone(rightFirst)),
      (true, false) when rightFirst is IrBind => new IrConditional(new IrIsNone(leftFirst), IsAnything(rightFirst), Not(IsAnything(rightFirst))),
      (true, false) => new IrIsNone(leftFirst),
      (false, true) when leftFirst is IrBind => new IrBinary(IrBinaryOp.LogicalAnd, IsAnything(leftFirst), new IrIsNone(rightFirst)),
      (false, true) => new IrIsNone(rightFirst),
      _ => null
    };
    if (test is null)
      return equality || op is IrBinaryOp.Less or IrBinaryOp.LessOrEqual or IrBinaryOp.Greater or IrBinaryOp.GreaterOrEqual ? value : new IrSome(value);

    if (equality)
      return new IrConditional(test, Truth(kind == SyntaxKind.NotEqualsExpression), value);

    if (value.Type == IrType.Bool)
      return new IrConditional(test, Truth(false), value);

    return new IrConditional(test, new IrNone(IrType.Optional(value.Type)), new IrSome(value));
  }

  /// <summary>A test of an optional value that is always true and evaluates it: that it holds none or not.</summary>
  private static IrExpr IsAnything(IrExpr value) => new IrBinary(IrBinaryOp.LogicalOr, new IrIsNone(value), Truth(true));

}
