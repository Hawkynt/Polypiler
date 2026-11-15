using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize.Passes;

/// <summary>
/// O0001, constant folding (prototype): an operation on constants becomes its value, as the
/// interpreter computes it - an integer or a truth value; one that traps stays, so the trap happens
/// where it did. A choice on a constant becomes its branch, and an operation with a neutral operand
/// (<c>x + 0</c>, <c>x | 0</c>, <c>x &lt;&lt; 0</c>, <c>x &amp; ~0</c>, <c>x * 1</c>) becomes the other one;
/// <c>x &amp; 0</c> and <c>x * 0</c> become 0 where <c>x</c> may go unevaluated. A conversion back to the
/// type a value had before it was widened is the value, and a mask that keeps every bit a wrapping
/// conversion keeps goes.
/// </summary>
public sealed class O0001ConstantFolding : IIrPass {

  /// <inheritdoc/>
  public string Id => "O0001";

  /// <inheritdoc/>
  public string Name => "constant folding";

  /// <inheritdoc/>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(scope);
    ArgumentNullException.ThrowIfNull(applied);
    var changed = false;
    var functions = module.Functions.Select(function => {
      if (!scope(function))
        return function;

      var position = new List<int>();
      var folded = Block(function.Body, position, function.Name, applied);
      if (ReferenceEquals(folded, function.Body))
        return function;

      changed = true;
      return function with { Body = folded };
    }).ToList();
    return changed ? module with { Functions = functions } : module;
  }

  private static IrBlock Block(IrBlock block, List<int> position, string function, Action<string, string> applied) {
    var statements = new List<IrStmt>();
    var changed = false;
    for (var index = 0; index < block.Statements.Count; ++index) {
      position.Add(index + 1);
      var statement = block.Statements[index];
      var folds = 0;
      var mapped = IrPassSupport.Map(statement, expression => {
        var result = IrShape.Map(expression, node => Fold(node) is { } value ? Count(value) : null);
        return result;

        IrExpr Count(IrExpr value) {
          ++folds;
          return value;
        }
      }, inner => Block(inner, position, function, applied));
      if (folds > 0)
        applied(function, string.Join('.', position));

      changed |= !ReferenceEquals(mapped, statement) && (folds > 0 || !Same(mapped, statement));
      statements.Add(mapped);
      position.RemoveAt(position.Count - 1);
    }

    return changed ? new IrBlock(statements) : block;
  }

  /// <summary>Whether a rebuilt statement is the old one: only its blocks may have been rebuilt, which they report themselves.</summary>
  private static bool Same(IrStmt mapped, IrStmt statement)
    => IrTree.Blocks(mapped).Zip(IrTree.Blocks(statement)).All(pair => ReferenceEquals(pair.First, pair.Second))
      && IrTree.Expressions(mapped).Zip(IrTree.Expressions(statement)).All(pair => ReferenceEquals(pair.First, pair.Second));

  /// <summary>What a node, its operands folded already, folds to.</summary>
  /// <param name="node">The node.</param>
  /// <returns>The folded node, or <see langword="null"/> to keep it.</returns>
  public static IrExpr? Fold(IrExpr node) {
    switch (node) {
      case IrConditional { Condition: IrConst condition } choice:
        return condition.Value != 0 ? choice.WhenTrue : choice.WhenFalse;
      case IrBinary { Op: IrBinaryOp.LogicalAnd, Left: IrConst left } and:
        return left.Value != 0 ? and.Right : left;
      case IrBinary { Op: IrBinaryOp.LogicalOr, Left: IrConst left } or:
        return left.Value != 0 ? left : or.Right;
      case IrBinary { Op: IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr, Right: IrConst right } logical when IrPassSupport.IsPure(logical.Left):
        // x && true is x, x || false is x; x && false is false and x || true is true once x need not run.
        return (right.Value != 0) == (logical.Op == IrBinaryOp.LogicalAnd) ? logical.Left : right;
      case IrConvert conversion when conversion.Operand.Type == conversion.Type:
        return conversion.Operand;
      case IrConvert { Contract: IrConversion.Wrap, Operand: IrConvert { Contract: IrConversion.Wrap, Operand: var inner } widened } narrowed
        when inner.Type == narrowed.Type && inner.Type.IsInteger && widened.Type.IsInteger && widened.Type.Bits >= inner.Type.Bits:
        return inner;
      case IrConvert { Contract: IrConversion.Wrap, Operand: IrBinary { Op: IrBinaryOp.And } mask } conversion
        when conversion.Type.IsInteger && mask.Type.IsInteger && Keeps(mask, conversion.Type) is { } kept:
        return conversion with { Operand = kept };
    }

    if (Operands(node) is { } operands && operands.All(operand => operand is IrConst) && node is IrUnary or IrBinary or IrConvert or IrIntrinsic)
      return IrPassSupport.Evaluate(node);

    return node is IrBinary binary ? Neutral(binary) : null;
  }

  private static IReadOnlyList<IrExpr>? Operands(IrExpr node) => node switch {
    IrIntrinsic { Type.IsArray: true } => null,
    IrIntrinsic { Arguments.Count: 0 } => null,
    _ => IrTree.Operands(node)
  };

  /// <summary>The operand of a mask that keeps at least every bit a wrapping conversion to a type keeps, or <see langword="null"/>.</summary>
  private static IrExpr? Keeps(IrBinary mask, IrType type) {
    var ones = Ones(type.Bits);
    if (mask.Right is IrConst right && (unchecked((ulong)right.Value) & ones) == ones)
      return mask.Left;

    return mask.Left is IrConst left && (unchecked((ulong)left.Value) & ones) == ones ? mask.Right : null;
  }

  /// <summary>An operation with a neutral or an absorbing operand, simplified; <see langword="null"/> for any other.</summary>
  private static IrExpr? Neutral(IrBinary binary) {
    if (!binary.Type.IsInteger || !binary.Left.Type.IsInteger)
      return null;

    var (left, right) = (binary.Left as IrConst, binary.Right as IrConst);
    var all = Ones(binary.Type.Bits);
    switch (binary.Op) {
      case IrBinaryOp.Add or IrBinaryOp.Or or IrBinaryOp.Xor when Is(right, 0):
      case IrBinaryOp.Subtract or IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical when Is(right, 0):
      case IrBinaryOp.Multiply when Is(right, 1):
        return binary.Left;
      case IrBinaryOp.Add or IrBinaryOp.Or or IrBinaryOp.Xor when Is(left, 0):
      case IrBinaryOp.Multiply when Is(left, 1):
        return binary.Right;
      case IrBinaryOp.And when right is not null && (unchecked((ulong)right.Value) & all) == all:
        return binary.Left;
      case IrBinaryOp.And when left is not null && (unchecked((ulong)left.Value) & all) == all:
        return binary.Right;
      case IrBinaryOp.And when right is not null && Extended(binary.Left) is { } bits && (unchecked((ulong)right.Value) & Ones(bits)) == Ones(bits):
        return binary.Left;
      case IrBinaryOp.And when left is not null && Extended(binary.Right) is { } bits && (unchecked((ulong)left.Value) & Ones(bits)) == Ones(bits):
        return binary.Right;
      case IrBinaryOp.And or IrBinaryOp.Multiply when Is(right, 0) && IrPassSupport.IsPure(binary.Left):
        return right;
      case IrBinaryOp.And or IrBinaryOp.Multiply when Is(left, 0) && IrPassSupport.IsPure(binary.Right):
        return left;
      default:
        return null;
    }
  }

  /// <summary>The width of the unsigned value a wrapping conversion widened without setting a bit above it; <see langword="null"/> for any other operand.</summary>
  private static int? Extended(IrExpr operand)
    => operand is IrConvert { Contract: IrConversion.Wrap, Operand.Type: { IsInteger: true, IsSigned: false } from } conversion && from.Bits < conversion.Type.Bits ? from.Bits : null;

  private static bool Is(IrConst? constant, long value) => constant is not null && constant.Value == value;

  private static ulong Ones(int bits) => bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;

}
