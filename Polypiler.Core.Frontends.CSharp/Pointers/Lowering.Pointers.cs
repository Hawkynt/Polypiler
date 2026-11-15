using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// The unsafe subset of C# as bounded pointers (#1331): <c>unsafe</c> methods, classes and blocks, pointers to the
/// number types and <c>void*</c>, <c>fixed</c> over an array, an element of one or a span, <c>&amp;</c> of a
/// local, <c>*p</c>, <c>p[i]</c>, the arithmetic and comparisons of pointers, casts between pointer types and
/// <c>stackalloc</c> into a pointer. Whatever reads a machine address - a cast between a pointer and an integer -
/// and whatever would let a pointer outlive its region is refused by name; <c>docs/reference/pointers.md</c>.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>
  /// The IR type of a C# pointer type: a pointer to a number type other than <c>char</c>, or <c>void*</c>, held
  /// in a local or a parameter of a method that is not public. <see langword="null"/> for any other type.
  /// </summary>
  private IrType? PointerType(ITypeSymbol? type, SyntaxNode node) {
    if (type is IFunctionPointerTypeSymbol)
      throw new UnsupportedException(node, "delegate*");

    if (type is not IPointerTypeSymbol pointer)
      return null;

    var spelled = pointer.ToDisplayString();
    var pointed = pointer.PointedAtType;
    if (pointed.SpecialType is SpecialType.System_Char or SpecialType.System_Boolean)
      throw new UnsupportedException(node, spelled);

    if (pointed.TypeKind == TypeKind.Struct && pointed.SpecialType == SpecialType.None)
      throw new UnsupportedException(node, $"a pointer to a struct, {spelled}");

    var element = pointed.SpecialType == SpecialType.System_Void ? IrType.Void : pointed is IPointerTypeSymbol ? null : this.Map(pointed, node);
    if (element is null || (!element.IsNumeric && element != IrType.Void) || element.IsEnum)
      throw new UnsupportedException(node, spelled);

    foreach (var place in node.AncestorsAndSelf())
      switch (place) {
        case FieldDeclarationSyntax or PropertyDeclarationSyntax:
          throw new UnsupportedException(node, $"the pointer {spelled} stored in a field");
        case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or DelegateDeclarationSyntax:
          throw new UnsupportedException(node, $"the pointer {spelled} in a lambda or a local function");
        case MethodDeclarationSyntax method when method.ReturnType == node:
          throw new UnsupportedException(node, $"the pointer {spelled} returned");
        case ParameterSyntax { Parent.Parent: MethodDeclarationSyntax method } when method.Modifiers.Any(SyntaxKind.PublicKeyword):
          throw new UnsupportedException(node, $"the pointer {spelled} as a parameter of a public method");
      }

    return IrType.Pointer(element);
  }

  /// <summary>Whether an expression has a pointer type of C#.</summary>
  private bool IsPointer(ExpressionSyntax expression) => model.GetTypeInfo(expression).Type is IPointerTypeSymbol;

  /// <summary>
  /// An expression of the pointers: <c>*p</c>, <c>&amp;x</c>, <c>p[i]</c>, the arithmetic and the comparisons of
  /// pointers, a cast that has a pointer on either side and <c>null</c> as a pointer. <see langword="null"/> for
  /// any other expression.
  /// </summary>
  private IrExpr? PointerNatural(ExpressionSyntax expression, IrType type) {
    switch (expression) {
      case LiteralExpressionSyntax or DefaultExpressionSyntax when type.IsPointer:
        return new IrNull(type);
      case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PointerIndirectionExpression } indirection:
        return Load(this.Value(indirection.Operand));
      case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression } address:
        return this.AddressOf(address);
      case ElementAccessExpressionSyntax { ArgumentList.Arguments: [var argument] } element when this.IsPointer(element.Expression):
        return Load(Moved(this.Value(element.Expression), this.Count(argument.Expression)));
      case PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax when type.IsPointer:
        throw new UnsupportedException(expression, "a step of a pointer whose value is used");
      case ConditionalExpressionSyntax when type.IsPointer:
        throw new UnsupportedException(expression, "a conditional expression of pointers");
      case CastExpressionSyntax cast when type.IsPointer || this.IsPointer(cast.Expression):
        return this.PointerCast(cast, type);
      case BinaryExpressionSyntax binary when this.IsPointer(binary.Left) || this.IsPointer(binary.Right):
        return this.PointerBinary(binary);
      default:
        return null;
    }
  }

  private static IrIntrinsic Load(IrExpr pointer) => new(IrIntrinsicOp.PointerLoad, pointer.Type.Element!, [pointer]);

  private static IrIntrinsic Moved(IrExpr pointer, IrExpr count) => new(IrIntrinsicOp.PointerAdd, pointer.Type, [pointer, count]);

  private static IrExpr Negated(IrExpr count) => count is IrConst constant ? new IrConst(IrType.I32, IrOperators.Wrap(IrType.I32, -constant.Value)) : new IrUnary(IrUnaryOp.Negate, count);

  /// <summary>
  /// A count of elements a pointer moves by: an integer of C#, wrapped to 32 bits, as a pointer stays in a
  /// region an index of 32 bits reaches.
  /// </summary>
  private IrExpr Count(ExpressionSyntax expression) {
    var count = this.Value(expression);
    if (!count.Type.IsInteger)
      throw new UnsupportedException(expression, $"a pointer moved by {count.Type}");

    return count.Type == IrType.I32 ? count : Convert(count, IrType.I32);
  }

  /// <summary><c>&amp;x</c> of a local of a number type, which then lives in a region of one element.</summary>
  private IrExpr AddressOf(PrefixUnaryExpressionSyntax address) {
    if (address.Operand is not IdentifierNameSyntax || this.Value(address.Operand) is not IrLocalRef { Type.IsNumeric: true } local)
      throw new UnsupportedException(address, $"the address of {address.Operand}");

    return new IrIntrinsic(IrIntrinsicOp.AddressOf, IrType.Pointer(local.Type), [local]);
  }

  /// <summary>A cast between two pointer types; a cast between a pointer and an integer reads or makes a machine address and is refused.</summary>
  private IrExpr PointerCast(CastExpressionSyntax cast, IrType type) {
    var value = this.Value(cast.Expression);
    if (!type.IsPointer || !value.Type.IsPointer)
      throw new UnsupportedException(cast, $"a cast from {model.GetTypeInfo(cast.Expression).Type?.ToDisplayString()} to {model.GetTypeInfo(cast.Type).Type?.ToDisplayString()}");

    return ToPointer(value, type);
  }

  /// <summary>A pointer as one of another pointer type: itself, or its cast.</summary>
  private static IrExpr ToPointer(IrExpr value, IrType type)
    => value.Type == type ? value : new IrIntrinsic(IrIntrinsicOp.PointerCast, type, [value]);

  /// <summary><c>p + n</c>, <c>n + p</c>, <c>p - n</c>, <c>p - q</c> and the comparisons of two pointers.</summary>
  private IrExpr PointerBinary(BinaryExpressionSyntax binary) {
    var op = binary.Kind() switch {
      SyntaxKind.EqualsExpression => IrIntrinsicOp.PointerEqual,
      SyntaxKind.NotEqualsExpression => IrIntrinsicOp.PointerNotEqual,
      SyntaxKind.LessThanExpression => IrIntrinsicOp.PointerLess,
      SyntaxKind.LessThanOrEqualExpression => IrIntrinsicOp.PointerLessOrEqual,
      SyntaxKind.GreaterThanExpression => IrIntrinsicOp.PointerGreater,
      SyntaxKind.GreaterThanOrEqualExpression => IrIntrinsicOp.PointerGreaterOrEqual,
      SyntaxKind.AddExpression => IrIntrinsicOp.PointerAdd,
      SyntaxKind.SubtractExpression when this.IsPointer(binary.Right) => IrIntrinsicOp.PointerDifference,
      SyntaxKind.SubtractExpression => IrIntrinsicOp.PointerAdd,
      _ => throw new UnsupportedException(binary, $"{binary.OperatorToken.Text} of pointers")
    };
    if (op != IrIntrinsicOp.PointerAdd) {
      var left = this.Value(binary.Left);
      var right = this.Value(binary.Right);
      return op == IrIntrinsicOp.PointerDifference
        ? new IrIntrinsic(op, IrType.I64, [left, right])
        : new IrIntrinsic(op, IrType.Bool, [left, ToPointer(right, left.Type)]);
    }

    // n + p evaluates n first; the pointer is a variable or arithmetic of one, which a count cannot change.
    if (!this.IsPointer(binary.Left)) {
      var first = this.Count(binary.Left);
      return Moved(this.Value(binary.Right), first);
    }

    var pointer = this.Value(binary.Left);
    var count = this.Count(binary.Right);
    return Moved(pointer, binary.IsKind(SyntaxKind.SubtractExpression) ? Negated(count) : count);
  }

  /// <summary>
  /// What an assignment writes through a pointer: <c>*p</c> or <c>p[i]</c>, the pointer evaluated once when it
  /// is read as well. <see langword="null"/> for any other target.
  /// </summary>
  private (System.Func<IrExpr> Read, System.Func<IrExpr, IrStmt> Write)? PointerTarget(ExpressionSyntax target, List<IrStmt> into, bool alsoRead) {
    IrExpr pointer;
    switch (target) {
      case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PointerIndirectionExpression } indirection:
        pointer = this.Value(indirection.Operand);
        break;
      case ElementAccessExpressionSyntax { ArgumentList.Arguments: [var argument] } element when this.IsPointer(element.Expression):
        pointer = Moved(this.Value(element.Expression), this.Count(argument.Expression));
        break;
      default:
        return null;
    }

    if (alsoRead)
      pointer = this.Once(pointer, into);

    return (() => Load(pointer), value => new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.PointerStore, IrType.Void, [pointer, value])));
  }

  /// <summary><c>p += n</c> and <c>p -= n</c> of a pointer variable; <see langword="false"/> for any other assignment.</summary>
  private bool PointerAssignment(AssignmentExpressionSyntax assignment, List<IrStmt> into) {
    if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) || !this.IsPointer(assignment.Left))
      return false;

    if (assignment.Kind() is not (SyntaxKind.AddAssignmentExpression or SyntaxKind.SubtractAssignmentExpression))
      throw new UnsupportedException(assignment, $"{assignment.OperatorToken.Text} of a pointer");

    var target = this.Target(assignment.Left, into, true);
    var count = this.Count(assignment.Right);
    into.Add(target.Write(Moved(target.Read(), assignment.IsKind(SyntaxKind.SubtractAssignmentExpression) ? Negated(count) : count)));
    return true;
  }

  /// <summary><c>p++</c> and <c>p--</c> of a pointer variable as a statement; <see langword="false"/> for any other step.</summary>
  private bool PointerStep(ExpressionSyntax operand, bool increment, List<IrStmt> into) {
    if (!this.IsPointer(operand))
      return false;

    var target = this.Target(operand, into, true);
    into.Add(target.Write(Moved(target.Read(), new IrConst(IrType.I32, increment ? 1 : -1))));
    return true;
  }

  /// <summary>An <c>unsafe</c> block, whose statements are lowered as any others, and <c>fixed</c>; <see langword="false"/> for any other statement.</summary>
  private bool PointerStatement(StatementSyntax statement, List<IrStmt> into) {
    switch (statement) {
      case UnsafeStatementSyntax block:
        into.Add(this.Block(block.Block));
        return true;
      case FixedStatementSyntax pinned:
        into.Add(this.Fixed(pinned));
        return true;
      default:
        return false;
    }
  }

  /// <summary>
  /// <c>fixed (T* p = source, …) body</c>: a block that declares each pointer with its pin and holds the body, so
  /// that no pointer outlives it. The source is an array, a span, or <c>&amp;</c> of an element of either.
  /// </summary>
  private IrBlock Fixed(FixedStatementSyntax pinned) {
    var statements = new List<IrStmt>();
    foreach (var variable in pinned.Declaration.Variables) {
      var symbol = (ILocalSymbol)model.GetDeclaredSymbol(variable)!;
      var local = this.Local(symbol.Name, this.Map(symbol.Type, pinned.Declaration.Type));
      var source = variable.Initializer!.Value;
      IrExpr pin = source switch {
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression, Operand: ElementAccessExpressionSyntax { ArgumentList.Arguments: [var index] } element }
          when this.Pinnable(element.Expression) is { } sequence => this.Pin(sequence, this.Index(index.Expression)),
        _ when this.Pinnable(source) is { } sequence => this.Pin(sequence, null),
        _ => throw new UnsupportedException(source, $"fixed of {model.GetTypeInfo(source).Type?.ToDisplayString() ?? source.Kind().ToString()}")
      };
      this._variables[symbol] = local;
      if (pin.Type == local.Type) {
        statements.Add(new IrDeclare(local, pin));
      } else {
        // fixed (void* p = a): the pin of the elements' own type, then the pointer converted.
        var held = this.Local($"$t{this._localBase + this._locals.Count}", pin.Type);
        statements.Add(new IrDeclare(held, pin));
        statements.Add(new IrDeclare(local, ToPointer(new IrLocalRef(held), local.Type)));
      }
    }

    statements.AddRange(this.Body(pinned.Statement).Statements);
    return new IrBlock(statements);
  }

  /// <summary>An array or a span of numbers a <c>fixed</c> may pin, lowered; <see langword="null"/> for anything else.</summary>
  private IrExpr? Pinnable(ExpressionSyntax source) {
    var type = model.GetTypeInfo(source).Type;
    if (type is not IArrayTypeSymbol { Rank: 1 } && (type is not INamedTypeSymbol named || ViewKind(named) is not (IrTypeKind.Span or IrTypeKind.ReadOnlySpan)))
      return null;

    var value = this.Read(source);
    return value.Type.Element!.IsNumeric ? value : null;
  }

  private IrIntrinsic Pin(IrExpr source, IrExpr? index)
    => new(IrIntrinsicOp.Pin, IrType.Pointer(source.Type.Element!), index is null ? [source] : [source, index]);

  /// <summary>
  /// A <c>[SkipLocalsInit]</c> leaves what <c>stackalloc</c> gives unspecified, which the IR fixes as zeros: it is
  /// refused on the method, its class and the module.
  /// </summary>
  private void RefuseSkipLocalsInit(MethodDeclarationSyntax method) {
    var attributes = method.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().SelectMany(member => member.AttributeLists)
      .Concat(method.SyntaxTree.GetCompilationUnitRoot().AttributeLists)
      .SelectMany(list => list.Attributes);
    if (attributes.FirstOrDefault(attribute => attribute.Name.ToString().Split('.')[^1] is "SkipLocalsInit" or "SkipLocalsInitAttribute") is { } skipped)
      throw new UnsupportedException(skipped, "[SkipLocalsInit]");
  }

  /// <summary>A pointer kept after the <c>fixed</c> block or the local it points into, which would dangle, is refused.</summary>
  private static void RefuseDangling(IrFunction function, SyntaxNode node) {
    if (IrPointers.Problems(function).FirstOrDefault() is { } problem)
      throw new UnsupportedException(node, problem);
  }

}
