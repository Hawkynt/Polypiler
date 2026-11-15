using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The typing rules of the bounded pointers (#1331): which types the operations on a
/// <see cref="IrTypeKind.Pointer"/> take. A pointer is a region - an array, a view, the memory of a
/// <c>stackalloc</c> or of one local - and a byte offset in it; <c>docs/reference/pointers.md</c> describes
/// every operation and what it traps with.
/// </summary>
public static class IrPointers {

  /// <summary>The operations on pointers, in the order of <see cref="IrIntrinsicOp"/>.</summary>
  public static IReadOnlyList<IrIntrinsicOp> Operations { get; } = [
    IrIntrinsicOp.Pin, IrIntrinsicOp.AddressOf, IrIntrinsicOp.PointerAdd, IrIntrinsicOp.PointerDifference,
    IrIntrinsicOp.PointerEqual, IrIntrinsicOp.PointerNotEqual, IrIntrinsicOp.PointerLess, IrIntrinsicOp.PointerLessOrEqual,
    IrIntrinsicOp.PointerGreater, IrIntrinsicOp.PointerGreaterOrEqual, IrIntrinsicOp.PointerLoad, IrIntrinsicOp.PointerStore,
    IrIntrinsicOp.PointerCast
  ];

  /// <summary>Whether an operation is one of the pointers.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for an operation of <see cref="Operations"/>.</returns>
  public static bool IsPointerOperation(IrIntrinsicOp op) => op is >= IrIntrinsicOp.Pin and <= IrIntrinsicOp.PointerCast;

  /// <summary>Whether a node works on pointers: an operation of <see cref="Operations"/>, or a <c>stackalloc</c> into a pointer.</summary>
  /// <param name="intrinsic">The node.</param>
  /// <returns><see langword="true"/> for a node a backend writes as pointer code.</returns>
  public static bool IsPointerNode(IrIntrinsic intrinsic)
    => IsPointerOperation(intrinsic.Op) || (intrinsic.Op == IrIntrinsicOp.StackAlloc && intrinsic.Type.IsPointer);

  /// <summary>Whether an operation compares two pointers.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for the six comparisons.</returns>
  public static bool IsComparison(IrIntrinsicOp op) => op is >= IrIntrinsicOp.PointerEqual and <= IrIntrinsicOp.PointerGreaterOrEqual;

  /// <summary>Whether a comparison orders two pointers, which traps for pointers into two regions.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c> and <c>&gt;=</c>.</returns>
  public static bool Orders(IrIntrinsicOp op) => op is >= IrIntrinsicOp.PointerLess and <= IrIntrinsicOp.PointerGreaterOrEqual;

  /// <summary>Whether a type is a pointer to a number, through which values are read and written and which arithmetic moves.</summary>
  private static bool Typed(IrType type) => type.IsPointer && type.Element!.IsNumeric;

  /// <summary>
  /// Whether an operation on pointers, or a <c>stackalloc</c> into a pointer, takes the types; <see langword="null"/> when
  /// it is neither. Nothing is read, written or counted through a <c>void*</c>.
  /// </summary>
  /// <param name="op">The operation.</param>
  /// <param name="result">The type of the node.</param>
  /// <param name="arguments">The types of the arguments, in order.</param>
  /// <returns>Whether the types fit; <see langword="null"/> for another operation.</returns>
  internal static bool? Accepts(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> arguments) {
    if (op == IrIntrinsicOp.StackAlloc && result.IsPointer)
      return arguments is [{ IsWord: true }] && Typed(result);

    if (!IsPointerOperation(op))
      return null;

    var first = arguments.Count > 0 ? arguments[0] : IrType.Void;
    bool Pair() => arguments.Count == 2 && first.IsPointer && arguments[1] == first;
    return op switch {
      IrIntrinsicOp.Pin => arguments.Count is 1 or 2 && (first.IsArray || first.IsView) && first.Element!.IsNumeric
        && arguments.Skip(1).All(type => type.IsWord) && result == IrType.Pointer(first.Element),
      IrIntrinsicOp.AddressOf => arguments is [{ IsNumeric: true } local] && result == IrType.Pointer(local),
      IrIntrinsicOp.PointerAdd => arguments is [_, { IsWord: true }] && Typed(first) && result == first,
      IrIntrinsicOp.PointerDifference => Pair() && Typed(first) && result == IrType.I64,
      IrIntrinsicOp.PointerLoad => arguments.Count == 1 && Typed(first) && result == first.Element,
      IrIntrinsicOp.PointerStore => arguments.Count == 2 && Typed(first) && arguments[1] == first.Element && result == IrType.Void,
      IrIntrinsicOp.PointerCast => arguments.Count == 1 && first.IsPointer && result.IsPointer && result.IsStorable,
      _ => Pair() && result == IrType.Bool
    };
  }

}
