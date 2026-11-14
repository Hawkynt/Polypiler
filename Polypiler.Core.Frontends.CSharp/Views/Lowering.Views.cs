using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// <c>Span&lt;T&gt;</c> and <c>ReadOnlySpan&lt;T&gt;</c> of a number type (#1325): the views of the IR, made
/// of arrays, sliced, indexed, copied, filled, compared and reinterpreted, as <see cref="CSharpViewApi"/>
/// lists the members. A member of these types the table does not list is refused by name.
/// </summary>
internal sealed partial class Lowering {

  private const string Marshal = "System.Runtime.InteropServices.MemoryMarshal";

  /// <summary>The view type of <c>Span&lt;T&gt;</c> or <c>ReadOnlySpan&lt;T&gt;</c> of a number type other than <c>char</c>; <see langword="null"/> for any other type.</summary>
  private IrType? ViewType(ITypeSymbol? type, SyntaxNode node) {
    if (type is not INamedTypeSymbol { TypeArguments: [var argument] } named || ViewKind(named) is not { } kind)
      return null;

    if (argument.SpecialType == SpecialType.System_Char)
      throw new UnsupportedException(node, named.ToDisplayString());

    var element = this.Map(argument, node);
    if (!element.IsNumeric)
      throw new UnsupportedException(node, named.ToDisplayString());

    return kind == IrTypeKind.Span ? IrType.Span(element) : IrType.ReadOnlySpan(element);
  }

  /// <summary>The kind of view a constructed <c>Span&lt;T&gt;</c> or <c>ReadOnlySpan&lt;T&gt;</c> is.</summary>
  private static IrTypeKind? ViewKind(INamedTypeSymbol type) => type.ConstructedFrom.ToDisplayString() switch {
    "System.Span<T>" => IrTypeKind.Span,
    "System.ReadOnlySpan<T>" => IrTypeKind.ReadOnlySpan,
    _ => null
  };

  /// <summary>
  /// A value converted to a view the compiler converts it to: an array to either view, a writable view to
  /// a read-only one. The value itself when it already has the type.
  /// </summary>
  private static IrExpr ToView(IrExpr value, IrType view)
    => value.Type == view ? value : new IrIntrinsic(IrIntrinsicOp.ViewOf, view, [value]);

  /// <summary>An expression as a view of its elements: a view, or an array as a writable view of the whole of it.</summary>
  private IrExpr AsView(ExpressionSyntax expression) {
    var value = this.Value(expression);
    return value.Type.IsArray ? ToView(value, IrType.Span(value.Type.Element!)) : value;
  }

  /// <summary><c>stackalloc T[n]</c> made a view: a run of zeros the length of <c>n</c>. An initializer is refused.</summary>
  private IrExpr StackAlloc(StackAllocArrayCreationExpressionSyntax creation, ITypeSymbol? converted) {
    if (creation.Initializer is not null || creation.Type is not ArrayTypeSyntax { RankSpecifiers: [{ Sizes: [var size] }] } || size is OmittedArraySizeExpressionSyntax)
      throw new UnsupportedException(creation, "stackalloc with an initializer");

    var view = this.ViewType(converted, creation) ?? throw new UnsupportedException(creation, "stackalloc into a pointer");
    return new IrIntrinsic(IrIntrinsicOp.StackAlloc, IrType.Span(view.Element!), [this.Index(size)]);
  }

  /// <summary>
  /// <c>v[a..b]</c>, <c>v[..b]</c>, <c>v[a..]</c> and the ends counted from the end, <c>v[^n..]</c>: the slice
  /// of a view, or the copy of the part of an array that a range of an array is. An end counted from
  /// the end reads the length, so the view has to be a variable.
  /// </summary>
  private IrExpr Range(ElementAccessExpressionSyntax access, RangeExpressionSyntax range, IrType type) {
    var source = this.Read(access.Expression);
    var view = source.Type.IsArray ? ToView(source, IrType.ReadOnlySpan(source.Type.Element!)) : source;
    var start = range.LeftOperand is null ? new IrConst(IrType.I32, 0) : this.Bound(range.LeftOperand, view);
    IrExpr slice = range.RightOperand is null
      ? new IrIntrinsic(IrIntrinsicOp.Slice, view.Type, [view, start])
      : new IrIntrinsic(IrIntrinsicOp.Slice, view.Type, [view, start, new IrBinary(IrBinaryOp.Subtract, this.Bound(range.RightOperand, view), start)]);
    if (range.LeftOperand is not null && range.RightOperand is not null && !IsSimple(start))
      throw new UnsupportedException(range, "a range whose start is more than a variable or a constant");

    return type.IsArray ? new IrIntrinsic(IrIntrinsicOp.ViewToArray, type, [slice]) : slice;
  }

  /// <summary>One end of a range: an index, or with <c>^</c> one counted from the end of a view that is a variable.</summary>
  private IrExpr Bound(ExpressionSyntax bound, IrExpr view) {
    // The compiler converts the end to a System.Index, the innermost expression in parentheses; the integer before that conversion is meant.
    while (bound is ParenthesizedExpressionSyntax parenthesized)
      bound = parenthesized.Expression;

    if (bound is not PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.IndexExpression } fromEnd) {
      var index = this.Read(bound);
      return index.Type.IsWord ? index : throw new UnsupportedException(bound, $"an end of a range of the type {index.Type}");
    }

    if (!IsSimple(view))
      throw new UnsupportedException(bound, "an index from the end of a view that is not a variable");

    return new IrBinary(IrBinaryOp.Subtract, new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [view]), this.Index(fromEnd.Operand));
  }

  /// <summary>Whether evaluating an expression twice is the same as evaluating it once: a variable, a constant, or a view of one.</summary>
  private static bool IsSimple(IrExpr expression)
    => expression is IrLocalRef or IrConst || (expression is IrIntrinsic { Op: IrIntrinsicOp.ViewOf, Arguments: [var whole] } && IsSimple(whole));

  /// <summary><c>v[^n]</c>: the element counted from the end of a view or an array that is a variable.</summary>
  private IrExpr FromEnd(ElementAccessExpressionSyntax access, PrefixUnaryExpressionSyntax fromEnd) {
    var source = this.Read(access.Expression);
    if (!IsSimple(source))
      throw new UnsupportedException(fromEnd, "an index from the end of an expression that is not a variable");

    IrExpr length = source.Type.IsArray ? new IrArrayLength(source) : new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [source]);
    return new IrArrayLoad(source, new IrBinary(IrBinaryOp.Subtract, length, this.Index(fromEnd.Operand)));
  }

  /// <summary>
  /// <c>new Span&lt;T&gt;(array)</c> and <c>new Span&lt;T&gt;(array, start, length)</c>; the span of one element
  /// a reference is taken to, <c>new Span&lt;T&gt;(ref x)</c>, is refused.
  /// </summary>
  private IrExpr ViewCreation(BaseObjectCreationExpressionSyntax creation, IrType type) {
    var arguments = creation.ArgumentList!.Arguments.Select(argument => argument.Expression).ToList();
    if (model.GetTypeInfo(arguments[0]).Type is not IArrayTypeSymbol)
      throw new UnsupportedException(creation, $"new {type} of a reference");

    return new IrIntrinsic(IrIntrinsicOp.ViewOf, type, [this.Written(arguments[0]), .. arguments.Skip(1).Select(this.Index)]);
  }

  /// <summary>
  /// A call of a member of <c>Span&lt;T&gt;</c>, <c>ReadOnlySpan&lt;T&gt;</c>, <c>MemoryExtensions</c> or
  /// <c>MemoryMarshal</c>, as the operation on views it means; <see langword="null"/> for a method of any
  /// other type. A member of these types that <see cref="CSharpViewApi"/> does not list is refused by name.
  /// </summary>
  private IrExpr? ViewCall(InvocationExpressionSyntax invocation, IMethodSymbol method, IrType type) {
    var declaring = method.ContainingType.OriginalDefinition.ToDisplayString();
    if (declaring is not ("System.Span<T>" or "System.ReadOnlySpan<T>" or "System.MemoryExtensions" or Marshal))
      return null;

    var name = $"{method.ContainingType.Name}.{method.Name}";
    var arguments = invocation.ArgumentList.Arguments.Select(argument => argument.Expression).ToList();
    var receiver = invocation.Expression is MemberAccessExpressionSyntax access ? access.Expression : null;
    if (method.ReducedFrom is not null || (!method.IsStatic && receiver is not null))
      arguments.Insert(0, receiver!);

    if (CSharpViewApi.Find(declaring, method.Name, arguments.Count) is null)
      throw new UnsupportedException(invocation, name);

    IrIntrinsic Node(IrIntrinsicOp op, IrType result, params IrExpr[] operands) => new(op, result, operands);
    switch (method.Name) {
      case "AsSpan":
        if (model.GetTypeInfo(arguments[0]).Type is not IArrayTypeSymbol || method.Parameters.Skip(method.ReducedFrom is null ? 1 : 0).Any(parameter => parameter.Type.SpecialType != SpecialType.System_Int32))
          throw new UnsupportedException(invocation, $"{name} of anything but an array, at an int");

        return Node(IrIntrinsicOp.ViewOf, type, [this.Written(arguments[0]), .. arguments.Skip(1).Select(this.Index)]);
      case "Slice":
        return Node(IrIntrinsicOp.Slice, type, [this.Value(arguments[0]), .. arguments.Skip(1).Select(this.Index)]);
      case "CopyTo":
        return Node(IrIntrinsicOp.ViewCopy, IrType.Void, this.AsView(arguments[0]), this.AsView(arguments[1]));
      case "TryCopyTo":
        return Node(IrIntrinsicOp.ViewTryCopy, IrType.Bool, this.AsView(arguments[0]), this.AsView(arguments[1]));
      case "Fill":
        var filled = this.Value(arguments[0]);
        return Node(IrIntrinsicOp.ViewFill, IrType.Void, filled, Convert(this.Value(arguments[1]), filled.Type.Element!));
      case "Clear":
        var cleared = this.Value(arguments[0]);
        return Node(IrIntrinsicOp.ViewFill, IrType.Void, cleared, Zero(cleared.Type.Element!));
      case "SequenceEqual":
        return Node(IrIntrinsicOp.ViewEqual, IrType.Bool, this.AsView(arguments[0]), this.AsView(arguments[1]));
      case "ToArray":
        return Node(IrIntrinsicOp.ViewToArray, type, this.Value(arguments[0]));
      case "Cast" or "AsBytes":
        return this.Reinterpreted(invocation, name, arguments[0]);
      case "Read":
        return this.Marshalled(invocation, name, type, IrIntrinsicOp.LoadLittle, [this.AsView(arguments[0]), new IrConst(IrType.I32, 0)]);
      default:
        var value = this.Value(arguments[1]);
        if (!Pure(value))
          throw new UnsupportedException(arguments[1], $"{name} with a value that can trap or has an effect");

        return this.Marshalled(invocation, name, value.Type, IrIntrinsicOp.StoreLittle, [this.AsView(arguments[0]), new IrConst(IrType.I32, 0), value]);
    }
  }

  /// <summary>
  /// <c>MemoryMarshal.Cast</c> or <c>AsBytes</c> whose result is read only: a read-only view of the bytes
  /// read as the other type. A result kept in a writable span is refused: where a profile has no spans
  /// the reinterpretation is a copy (docs/reference/views.md), through which nothing could be written back.
  /// </summary>
  private IrIntrinsic Reinterpreted(InvocationExpressionSyntax invocation, string name, ExpressionSyntax source) {
    if (this.ViewType(model.GetTypeInfo(invocation).ConvertedType, invocation) is not { Kind: IrTypeKind.ReadOnlySpan } result)
      throw new UnsupportedException(invocation, $"{name} into a writable span");

    var view = this.AsView(source);
    return new IrIntrinsic(IrIntrinsicOp.Reinterpret, result, [ToView(view, IrType.ReadOnlySpan(view.Type.Element!))]);
  }

  /// <summary><c>MemoryMarshal.Read&lt;T&gt;</c> or <c>Write&lt;T&gt;</c> of a number type of more than one byte, the lowest byte first.</summary>
  private IrIntrinsic Marshalled(InvocationExpressionSyntax invocation, string name, IrType value, IrIntrinsicOp op, IrExpr[] arguments) {
    var result = op == IrIntrinsicOp.LoadLittle ? value : IrType.Void;
    var node = new IrIntrinsic(op, result, arguments);
    return value.Size > 1 && IrOperators.Accepts(op, result, [.. arguments.Select(argument => argument.Type)])
      ? node
      : throw new UnsupportedException(invocation, $"{name} of {value}");
  }

  /// <summary>The constant zero of a number type, which <c>Clear</c> stores.</summary>
  private static IrConst Zero(IrType type) => new(type, 0);

  /// <summary>
  /// A <c>ref</c> expression, which the subset does not have: refused naming the member of
  /// <c>MemoryMarshal</c> it takes a reference from when it calls one.
  /// </summary>
  private UnsupportedException Reference(RefExpressionSyntax reference)
    => reference.Expression is InvocationExpressionSyntax call && model.GetSymbolInfo(call).Symbol is IMethodSymbol method
       && method.ContainingType.ToDisplayString() == Marshal
      ? new UnsupportedException(reference, $"MemoryMarshal.{method.Name}")
      : new UnsupportedException(reference, "a ref expression");

}
