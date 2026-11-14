using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize;

/// <summary>
/// One bit of an integer as the exclusive or of atom bits and a constant, the atom bits as a set of
/// keys <c>atom * 64 + bit</c>.
/// </summary>
/// <param name="Keys">The atom bits, sorted.</param>
/// <param name="One">The constant.</param>
internal sealed record XorBit(IReadOnlyList<int> Keys, bool One) {

  public static XorBit Zero { get; } = new([], false);

  public static XorBit Set { get; } = new([], true);

  public bool IsConstant => this.Keys.Count == 0;

  public XorBit Xor(XorBit other) => new([.. this.Keys.Except(other.Keys).Concat(other.Keys.Except(this.Keys)).Order()], this.One ^ other.One);

  public bool SameAs(XorBit other) => this.One == other.One && this.Keys.SequenceEqual(other.Keys);

}

/// <summary>
/// Computes each bit of an integer expression as an exclusive or of atom bits: exact for and, or,
/// exclusive or and shifts by constants, for sums without carries, and for conversions between
/// integers. A node whose bits are not all of that form is an atom; an expression with an effect
/// cannot be one, and the evaluation then fails.
/// </summary>
internal sealed class XorEvaluator {

  private readonly RuleContext _context;
  private readonly List<IrExpr> _atoms = [];
  private readonly Dictionary<IrExpr, int> _index = new(IrShape.Comparer);
  private bool _refused;

  private XorEvaluator(RuleContext context) => this._context = context;

  /// <summary>The bits of an integer expression, lowest first, and the atoms; <see langword="null"/> when it has an effect.</summary>
  public static (XorBit[] Bits, IReadOnlyList<IrExpr> Atoms)? Evaluate(IrExpr root, RuleContext context) {
    if (!root.Type.IsInteger)
      return null;

    var evaluator = new XorEvaluator(context);
    var bits = evaluator.Eval(root);
    return evaluator._refused ? null : (bits, evaluator._atoms);
  }

  private XorBit[] Atom(IrExpr expression) {
    if (!IrShape.IsSideEffectFree(expression))
      this._refused = true;

    if (!this._index.TryGetValue(expression, out var index)) {
      index = this._atoms.Count;
      this._atoms.Add(expression);
      this._index.Add(expression, index);
    }

    return [.. Enumerable.Range(0, expression.Type.Bits).Select(bit => new XorBit([index * 64 + bit], false))];
  }

  private XorBit[] Eval(IrExpr expression) {
    var bits = this.Structure(expression);
    return bits.Any(bit => bit is null) ? this.Atom(expression) : bits!.Select(bit => bit!).ToArray();
  }

  private static XorBit?[] Constant(int width, ulong value)
    => [.. Enumerable.Range(0, width).Select(bit => ((value >> bit) & 1) != 0 ? XorBit.Set : XorBit.Zero)];

  private XorBit?[] Structure(IrExpr expression) {
    var width = expression.Type.Bits;
    switch (expression) {
      case IrConst constant:
        return Constant(width, (ulong)constant.Value);
      case IrLocalRef reference when this._context.Definition(reference.Local) is { } definition:
        return this.Eval(definition);
      case IrConvert { Contract: IrConversion.Wrap } conversion when conversion.Operand.Type.IsInteger: {
        var inner = this.Eval(conversion.Operand);
        var fill = conversion.Operand.Type.IsSigned ? inner[^1] : XorBit.Zero;
        return [.. Enumerable.Range(0, width).Select(bit => bit < inner.Length ? inner[bit] : fill)];
      }
      case IrIntrinsic { Op: IrIntrinsicOp.ToInt32 or IrIntrinsicOp.ToUint32, Arguments: [IrConvert { Operand.Type: { IsInteger: true, Bits: <= 32 } } exact] } intrinsic:
        return this.Structure(new IrConvert(intrinsic.Type, exact.Operand));
      case IrIntrinsic { Op: IrIntrinsicOp.ToUint32 } unsigned:
        return this.Structure(new IrConvert(IrType.U32, Images.Call(IrIntrinsicOp.ToInt32, IrType.I32, unsigned.Arguments[0])));
      case IrUnary { Op: IrUnaryOp.BitNot } unary:
        return [.. this.Eval(unary.Operand).Select(bit => bit.Xor(XorBit.Set))];
      case IrBinary { Op: IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical, Right: IrConst count } shift: {
        var inner = this.Eval(shift.Left);
        var by = (int)(count.Value & (width - 1));
        var fill = shift.Op == IrBinaryOp.ShiftRight && shift.Type.IsSigned ? inner[^1] : XorBit.Zero;
        return shift.Op == IrBinaryOp.ShiftLeft
          ? [.. Enumerable.Range(0, width).Select(bit => bit >= by ? inner[bit - by] : XorBit.Zero)]
          : [.. Enumerable.Range(0, width).Select(bit => bit + by < width ? inner[bit + by] : fill)];
      }
      case IrBinary { Op: IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor or IrBinaryOp.Add } binary when binary.Type.IsInteger: {
        var left = this.Eval(binary.Left);
        var right = this.Eval(binary.Right);
        return [.. left.Zip(right, (a, b) => Combine(binary.Op, a, b))];
      }
      default:
        return this.Atom(expression);
    }
  }

  private static XorBit? Combine(IrBinaryOp op, XorBit a, XorBit b) => op switch {
    IrBinaryOp.Xor => a.Xor(b),
    IrBinaryOp.And when a.IsConstant => a.One ? b : XorBit.Zero,
    IrBinaryOp.And when b.IsConstant => b.One ? a : XorBit.Zero,
    IrBinaryOp.And => a.SameAs(b) ? a : null,
    IrBinaryOp.Or when a.IsConstant => a.One ? XorBit.Set : b,
    IrBinaryOp.Or when b.IsConstant => b.One ? XorBit.Set : a,
    IrBinaryOp.Or => a.SameAs(b) ? a : null,
    _ => a.SameAs(XorBit.Zero) ? b : b.SameAs(XorBit.Zero) ? a : null
  };

}
