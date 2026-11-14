using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

internal sealed partial class Lowering {

  /// <summary>
  /// A statement whose call yields two results: <c>Math.DivRem</c> and <c>Math.BigMul</c> of 64 bits
  /// with their second result in an out argument, and the pair of <c>DivRem</c> deconstructed. Both
  /// operands are evaluated once, both results are computed - which traps, if anything does, before
  /// either is stored - and then stored, the out argument or second element first.
  /// </summary>
  /// <returns><see langword="false"/> for a statement that is none of these.</returns>
  private bool Paired(ExpressionSyntax statement, List<IrStmt> into) {
    switch (statement) {
      case InvocationExpressionSyntax call when this.OutForm(call) is { } method:
        this.Pair(method, call, null, this.Out(call), into);
        return true;
      case AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Right: InvocationExpressionSyntax call } assignment:
        if (this.OutForm(call) is { } outForm) {
          this.Pair(outForm, call, this.Target(assignment.Left), this.Out(call), into);
          return true;
        }

        if (this.PairForm(call) is { } pairForm && Elements(assignment.Left) is [var first, var second]) {
          this.Pair(pairForm, call, this.Target(first), this.Target(second), into);
          return true;
        }

        return false;
      default:
        return false;
    }
  }

  /// <summary>A declaration of one local whose initializer is a call with an out argument.</summary>
  private bool Paired(VariableDeclarationSyntax declaration, List<IrStmt> into) {
    if (declaration.Variables is not [{ Initializer.Value: InvocationExpressionSyntax call } variable] || this.OutForm(call) is not { } method)
      return false;

    this.Pair(method, call, this.Declared(model.GetDeclaredSymbol(variable)!, variable), this.Out(call), into);
    return true;
  }

  /// <summary>The method of a call of <c>Math.DivRem</c> or <c>Math.BigMul</c> whose last argument is out, or <see langword="null"/>.</summary>
  private IMethodSymbol? OutForm(InvocationExpressionSyntax call)
    => model.GetSymbolInfo(call).Symbol is IMethodSymbol { Parameters: [_, _, { RefKind: RefKind.Out }] } method
      && CSharpIntegerApi.Find(method.ContainingType.ToDisplayString(), method.Name) is { Ir: "Divide, Remainder" } or { Member: "BigMul" }
        ? method : null;

  /// <summary>The method of a call of a <c>DivRem</c> that returns the pair, or <see langword="null"/>.</summary>
  private IMethodSymbol? PairForm(InvocationExpressionSyntax call)
    => model.GetSymbolInfo(call).Symbol is IMethodSymbol { Parameters.Length: 2 } method
      && CSharpIntegerApi.Find(method.ContainingType.ToDisplayString(), method.Name) is { Ir: "Divide, Remainder" }
        ? method : null;

  /// <summary>The two elements a pair is deconstructed into: expressions of a tuple, or the designations of <c>var (q, r)</c>; nothing for another target.</summary>
  private static SyntaxNode[] Elements(ExpressionSyntax target) => target switch {
    TupleExpressionSyntax tuple => [.. tuple.Arguments.Select(argument => argument.Expression)],
    DeclarationExpressionSyntax { Designation: ParenthesizedVariableDesignationSyntax pair } => [.. pair.Variables],
    _ => []
  };

  /// <summary>Where the out argument of a call goes.</summary>
  private Func<IrExpr, IrStmt>? Out(InvocationExpressionSyntax call) => this.Target(call.ArgumentList.Arguments[2].Expression);

  /// <summary>
  /// What stores one result: a local declared there (<c>var r</c>, <c>int r</c>), a local that is
  /// assigned, or nothing for a discard.
  /// </summary>
  private Func<IrExpr, IrStmt>? Target(SyntaxNode target) {
    switch (target) {
      case DiscardDesignationSyntax or DeclarationExpressionSyntax { Designation: DiscardDesignationSyntax }:
      case IdentifierNameSyntax when model.GetSymbolInfo(target).Symbol is IDiscardSymbol:
        return null;
      case SingleVariableDesignationSyntax single:
        return this.Declared(model.GetDeclaredSymbol(single)!, single);
      case DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax single }:
        return this.Declared(model.GetDeclaredSymbol(single)!, single);
      case IdentifierNameSyntax identifier when this.Value(identifier) is IrLocalRef variable:
        return value => new IrAssign(variable.Local, value);
      default:
        throw new UnsupportedException(target, $"a result stored in {target.Kind()}, which is no local");
    }
  }

  /// <summary>A local declared by the statement, which the result initializes.</summary>
  private Func<IrExpr, IrStmt> Declared(ISymbol symbol, SyntaxNode node) {
    var local = this.Local(symbol.Name, this.Map(((ILocalSymbol)symbol).Type, node));
    this._variables[symbol] = local;
    return value => new IrDeclare(local, value);
  }

  /// <summary>Both results of the call, computed into temporaries, then stored: the second first, as the call stores its out argument before it returns.</summary>
  private void Pair(IMethodSymbol method, InvocationExpressionSyntax call, Func<IrExpr, IrStmt>? first, Func<IrExpr, IrStmt>? second, List<IrStmt> into) {
    var arguments = call.ArgumentList.Arguments;
    var left = this.Once(this.Value(arguments[0].Expression), into);
    var right = this.Once(this.Value(arguments[1].Expression), into);
    var type = left.Type;
    (IrExpr First, IrExpr Second) results;
    if (method.Name == "BigMul") {
      results = (new IrIntrinsic(IrIntrinsicOp.MulHigh, type, [left, right]), new IrBinary(IrBinaryOp.Multiply, left, right));
    } else {
      // A narrow type divides as an int, which holds every quotient and remainder of it, and converts back.
      var wide = type.Bits < 32 ? IrType.I32 : type;
      IrExpr Divided(IrBinaryOp op) => Convert(new IrBinary(op, Convert(left, wide), Convert(right, wide)), type);
      results = (Divided(IrBinaryOp.Divide), Divided(IrBinaryOp.Remainder));
    }

    var computed = this.Temporary(results.First, into);
    var other = this.Temporary(results.Second, into);
    if (second is not null)
      into.Add(second(other));

    if (first is not null)
      into.Add(first(computed));
  }

  /// <summary>A temporary that holds a value, so that it is computed exactly here.</summary>
  private IrExpr Temporary(IrExpr value, List<IrStmt> into) {
    var temporary = this.Local($"$t{this._locals.Count}", value.Type);
    into.Add(new IrDeclare(temporary, value));
    return new IrLocalRef(temporary);
  }

}
