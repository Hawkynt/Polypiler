using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The typing rules of the views (#1325): which types the operations on a <see cref="IrTypeKind.Span"/> or
/// <see cref="IrTypeKind.ReadOnlySpan"/> take, and the endian loads and stores that read a view of bytes
/// where they read an array of them. <c>docs/reference/views.md</c> describes every operation.
/// </summary>
public static class IrViews {

  /// <summary>The operations on views, in the order of <see cref="IrIntrinsicOp"/>.</summary>
  public static IReadOnlyList<IrIntrinsicOp> Operations { get; } = [
    IrIntrinsicOp.ViewOf, IrIntrinsicOp.Slice, IrIntrinsicOp.ViewLength, IrIntrinsicOp.StackAlloc, IrIntrinsicOp.ViewCopy,
    IrIntrinsicOp.ViewTryCopy, IrIntrinsicOp.ViewFill, IrIntrinsicOp.ViewEqual, IrIntrinsicOp.ViewToArray, IrIntrinsicOp.Reinterpret
  ];

  /// <summary>Whether an operation is one of the views.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for an operation of <see cref="Operations"/>.</returns>
  public static bool IsViewOperation(IrIntrinsicOp op) => op is >= IrIntrinsicOp.ViewOf and <= IrIntrinsicOp.Reinterpret;

  /// <summary>Whether an endian load or store reads or writes bytes through a view rather than an array.</summary>
  /// <param name="intrinsic">The node.</param>
  /// <returns><see langword="true"/> for a load or store whose first argument is a view.</returns>
  public static bool IsEndianOnView(IrIntrinsic intrinsic)
    => intrinsic.Op is IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig or IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig
      && intrinsic.Arguments is [{ Type.IsView: true }, ..];

  /// <summary>
  /// The argument types an endian load or store is checked with when it works on a view of bytes: the
  /// view stands where the array of bytes does. A store needs a writable view. <see langword="null"/> for
  /// any other operation or argument.
  /// </summary>
  internal static IReadOnlyList<IrType>? AsBytes(IrIntrinsicOp op, IReadOnlyList<IrType> arguments) {
    if (op is not (IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig or IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig)
        || arguments is not [{ IsView: true, Element.Kind: IrTypeKind.U8 } view, ..])
      return null;

    var store = op is IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig;
    return store && view.Kind != IrTypeKind.Span ? null : [IrType.Array(IrType.U8), .. arguments.Skip(1)];
  }

  /// <summary>Whether an operation on views takes the types, or <see langword="null"/> when it is no operation on views.</summary>
  /// <param name="op">The operation.</param>
  /// <param name="result">The type of the node.</param>
  /// <param name="arguments">The types of the arguments, in order.</param>
  /// <returns>Whether the types fit; <see langword="null"/> for another operation.</returns>
  internal static bool? Accepts(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> arguments) {
    if (!IsViewOperation(op))
      return null;

    var first = arguments.Count > 0 ? arguments[0] : IrType.Void;
    bool Numbers(IrType type) => type.IsSequence && type.Element!.IsNumeric;
    bool View(IrType type) => type.IsView && type.Element!.IsNumeric;
    bool Writable(IrType type) => type.Kind == IrTypeKind.Span && type.Element!.IsNumeric;
    bool Words() => arguments.Skip(1).All(type => type.IsWord);
    bool Pair() => arguments.Count == 2 && View(first) && arguments[1].Element == first.Element;
    return op switch {
      IrIntrinsicOp.ViewOf => arguments.Count is >= 1 and <= 3 && Numbers(first) && Words() && View(result) && result.Element == first.Element
        && (result.Kind == IrTypeKind.ReadOnlySpan || first.IsWritableSequence),
      IrIntrinsicOp.Slice => arguments.Count is 2 or 3 && View(first) && Words() && result == first,
      IrIntrinsicOp.ViewLength => arguments.Count == 1 && View(first) && result == IrType.I32,
      IrIntrinsicOp.StackAlloc => arguments.Count == 1 && first.IsWord && Writable(result),
      IrIntrinsicOp.ViewCopy => Pair() && Writable(arguments[1]) && result == IrType.Void,
      IrIntrinsicOp.ViewTryCopy => Pair() && Writable(arguments[1]) && result == IrType.Bool,
      IrIntrinsicOp.ViewFill => arguments.Count == 2 && Writable(first) && arguments[1] == first.Element && result == IrType.Void,
      IrIntrinsicOp.ViewEqual => Pair() && View(arguments[1]) && result == IrType.Bool,
      IrIntrinsicOp.ViewToArray => arguments.Count == 1 && View(first) && result == IrType.Array(first.Element!),
      _ => arguments.Count == 1 && View(first) && View(result) && result.Kind == first.Kind
    };
  }

}
