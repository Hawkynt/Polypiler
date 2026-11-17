using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// The typing rules of the byte-ordered loads and stores through a pointer to bytes (#113):
/// <see cref="IrIntrinsicOp.PointerLoadLittle"/>, <see cref="IrIntrinsicOp.PointerLoadBig"/>,
/// <see cref="IrIntrinsicOp.PointerStoreLittle"/> and <see cref="IrIntrinsicOp.PointerStoreBig"/>. They read and
/// write a number of any width that is a whole number of bytes - every integer type and, through their bit
/// patterns, <c>f32</c> and <c>f64</c>; any other number of bytes as an unsigned <c>bigint</c> - in the stated
/// order, wherever the pointer points: no alignment is required, and no host order leaks in.
/// </summary>
public static class IrOrderedAccess {

  private static readonly IrType _bytePointer = IrType.Pointer(IrType.U8);

  /// <summary>Whether an operation is one of the four ordered loads and stores.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for an ordered load or store.</returns>
  public static bool IsOrdered(IrIntrinsicOp op) => op is >= IrIntrinsicOp.PointerLoadLittle and <= IrIntrinsicOp.PointerStoreBig;

  /// <summary>Whether an ordered operation stores; otherwise it loads.</summary>
  /// <param name="op">An ordered operation.</param>
  /// <returns><see langword="true"/> for a store.</returns>
  public static bool IsStore(IrIntrinsicOp op) => op is IrIntrinsicOp.PointerStoreLittle or IrIntrinsicOp.PointerStoreBig;

  /// <summary>The byte order of an ordered operation.</summary>
  /// <param name="op">An ordered operation.</param>
  /// <returns>The order.</returns>
  public static IrByteOrder OrderOf(IrIntrinsicOp op) => op is IrIntrinsicOp.PointerLoadLittle or IrIntrinsicOp.PointerStoreLittle ? IrByteOrder.Little : IrByteOrder.Big;

  /// <summary>The load of an order.</summary>
  /// <param name="order">The order.</param>
  /// <returns><see cref="IrIntrinsicOp.PointerLoadLittle"/> or <see cref="IrIntrinsicOp.PointerLoadBig"/>.</returns>
  public static IrIntrinsicOp Load(IrByteOrder order) => order == IrByteOrder.Little ? IrIntrinsicOp.PointerLoadLittle : IrIntrinsicOp.PointerLoadBig;

  /// <summary>The store of an order.</summary>
  /// <param name="order">The order.</param>
  /// <returns><see cref="IrIntrinsicOp.PointerStoreLittle"/> or <see cref="IrIntrinsicOp.PointerStoreBig"/>.</returns>
  public static IrIntrinsicOp Store(IrByteOrder order) => order == IrByteOrder.Little ? IrIntrinsicOp.PointerStoreLittle : IrIntrinsicOp.PointerStoreBig;

  /// <summary>Whether a type is read and written whole by an ordered operation: an integer that is no enum, <c>f32</c> or <c>f64</c>.</summary>
  /// <param name="type">The type.</param>
  /// <returns><see langword="true"/> for a type of a fixed number of bytes an ordered operation takes.</returns>
  public static bool IsOrderedType(IrType type) => type.IsNumeric && !type.IsEnum;

  /// <summary>Whether an ordered operation takes the types; <see langword="null"/> for another operation.</summary>
  /// <param name="op">The operation.</param>
  /// <param name="result">The type of the node.</param>
  /// <param name="arguments">The types of the arguments, in order.</param>
  /// <returns>Whether the types fit; <see langword="null"/> when the operation is not an ordered one.</returns>
  internal static bool? Accepts(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> arguments) {
    if (!IsOrdered(op))
      return null;

    if (arguments.Count == 0 || arguments[0] != _bytePointer)
      return false;

    return IsStore(op)
      ? result == IrType.Void && (arguments is [_, var value] ? IsOrderedType(value) : arguments is [_, { IsWord: true }, { IsBigInt: true }])
      : arguments is [_] ? IsOrderedType(result) : arguments is [_, { IsWord: true }] && result.IsBigInt;
  }

}
