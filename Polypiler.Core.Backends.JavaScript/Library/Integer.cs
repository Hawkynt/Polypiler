using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptLibrary {

  /// <summary>
  /// The integer and bit operations (<see cref="IntegerLibrary"/>). ECMAScript has none of the rotates,
  /// counts but <c>Math.clz32</c>, reorderings, powers of two, roundings of a quotient, clamps, bit
  /// fields, masks, saturating or overflow-testing arithmetic and endian accesses; what a level has -
  /// <c>Math.clz32</c>, <c>Math.sign</c>, <c>Math.min</c> and <c>Math.max</c> of numbers, exact
  /// arithmetic on numbers and on <c>BigInt</c>s for the narrow products and the carries - the writer
  /// writes itself.
  /// </summary>
  private static IEnumerable<(IrIntrinsicOp, Row)> IntegerRows => [
    .. new[] {
      IrIntrinsicOp.RotateLeft, IrIntrinsicOp.RotateRight, IrIntrinsicOp.PopCount, IrIntrinsicOp.TrailingZeroCount, IrIntrinsicOp.Parity,
      IrIntrinsicOp.ByteSwap, IrIntrinsicOp.BitReverse, IrIntrinsicOp.IsPow2, IrIntrinsicOp.RoundUpPow2,
      IrIntrinsicOp.DivFloor, IrIntrinsicOp.ModFloor, IrIntrinsicOp.DivEuclid, IrIntrinsicOp.ModEuclid, IrIntrinsicOp.Clamp,
      IrIntrinsicOp.AddSaturate, IrIntrinsicOp.SubSaturate, IrIntrinsicOp.MulSaturate,
      IrIntrinsicOp.AddOverflows, IrIntrinsicOp.SubOverflows, IrIntrinsicOp.MulOverflows,
      IrIntrinsicOp.ExtractBits, IrIntrinsicOp.InsertBits, IrIntrinsicOp.TestBit, IrIntrinsicOp.SetBit, IrIntrinsicOp.ClearBit, IrIntrinsicOp.ToggleBit,
      IrIntrinsicOp.LowMask, IrIntrinsicOp.IsolateLowestSet, IrIntrinsicOp.ClearLowestSet, IrIntrinsicOp.MaskToLowestSet,
      IrIntrinsicOp.SignExtend, IrIntrinsicOp.ZeroExtend,
      IrIntrinsicOp.ParallelBitExtract, IrIntrinsicOp.ParallelBitDeposit, IrIntrinsicOp.ByteShuffle, IrIntrinsicOp.CrcUpdate
    }.Select(op => (op, Always)),
    (IrIntrinsicOp.LeadingZeroCount, new Row((node, context) => IsInteger(node) && (Bits(node) == 64 || context.Replaces("Math.clz32")), IntegerLibrary.Call)),
    (IrIntrinsicOp.Log2, new Row((node, context) => IsInteger(node) && (Bits(node) == 64 || context.Replaces("Math.clz32")), IntegerLibrary.Call)),
    (IrIntrinsicOp.BigMul, new Row((node, context) => IsInteger(node) && !context.BigInt, IntegerLibrary.Call)),
    (IrIntrinsicOp.MulHigh, new Row((node, context) => IsInteger(node) && Bits(node) == 32 || (IsInteger(node) && Bits(node) == 64 && !context.BigInt), IntegerLibrary.Call)),
    (IrIntrinsicOp.Min, new Row((node, _) => IsInteger(node) && Bits(node) == 64, IntegerLibrary.Call)),
    (IrIntrinsicOp.Max, new Row((node, _) => IsInteger(node) && Bits(node) == 64, IntegerLibrary.Call)),
    (IrIntrinsicOp.Abs, new Row((node, _) => IsInteger(node) && node.Arguments[0].Type.IsSigned, IntegerLibrary.Call)),
    (IrIntrinsicOp.AbsWrap, new Row((node, _) => IsInteger(node) && node.Arguments[0].Type.IsSigned && Bits(node) == 64, IntegerLibrary.Call)),
    (IrIntrinsicOp.Sign, new Row((node, context) => IsInteger(node) && (Bits(node) == 64 || context.Replaces("Math.sign")), IntegerLibrary.Call)),
    .. new[] { IrIntrinsicOp.AddCarry, IrIntrinsicOp.CarryOut, IrIntrinsicOp.SubBorrow, IrIntrinsicOp.BorrowOut }
      .Select(op => (op, new Row((node, context) => IsInteger(node) && Bits(node) == 64 && !context.BigInt, IntegerLibrary.Call))),
    .. new[] { IrIntrinsicOp.LoadLittle, IrIntrinsicOp.LoadBig, IrIntrinsicOp.StoreLittle, IrIntrinsicOp.StoreBig }
      .Select(op => (op, new Row((node, _) => node.Arguments[0].Type.IsArray, Endian)))
  ];

  private static Row Always { get; } = new((node, _) => IsInteger(node), IntegerLibrary.Call);

  /// <summary>Whether a node is one of an integer operation: one whose operand that chooses the helper is an integer.</summary>
  private static bool IsInteger(IrIntrinsic node) => IntegerLibrary.Operand(node).IsInteger;

  private static int Bits(IrIntrinsic node) => IntegerLibrary.Operand(node).Bits;

  /// <summary>
  /// An endian access: of an integer its helper, of a floating-point number the access of the integer
  /// of its width and the bit cast of that, which the profile writes as it writes every bit cast.
  /// </summary>
  private static IrExpr? Endian(IrIntrinsic node, LibraryCalls calls) {
    var type = IntegerLibrary.Operand(node);
    if (!type.IsFloat)
      return IntegerLibrary.Call(node, calls);

    var integer = type == IrType.F32 ? IrType.I32 : IrType.I64;
    return node.Op is IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig
      ? new IrIntrinsic(IrIntrinsicOp.BitsFloat, node.Type, [calls.Legalize(node with { Type = integer })])
      : calls.Legalize(node with { Arguments = [node.Arguments[0], node.Arguments[1], new IrIntrinsic(IrIntrinsicOp.FloatBits, integer, [node.Arguments[2]])] });
  }

}
