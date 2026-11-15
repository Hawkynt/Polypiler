using System;

namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>
  /// A bounded pointer as the interpreter carries it (#1331): the region it points into - a view of the array,
  /// the span, the <c>stackalloc</c> or the local it was taken of, <see langword="null"/> for the null pointer
  /// - and a byte offset from the start of the region. Forming any pointer is allowed; a load or a store
  /// checks that all its bytes lie in the region.
  /// </summary>
  private sealed record PointerValue(View? Region, long Offset) {

    /// <summary>The null pointer, which <c>fixed</c> of an empty array gives as well.</summary>
    public static PointerValue Null { get; } = new(null, 0);

    /// <summary>The array the region lies in; <see langword="null"/> for a pointer based on the null pointer.</summary>
    public Array? Backing => this.Region?.Backing;

    /// <summary>Where the pointer is in the memory of its array, in bytes.</summary>
    public long Position => (this.Region?.Start ?? 0) + this.Offset;

  }

  private sealed partial class Machine {

    /// <summary>An operation on pointers, or a <c>stackalloc</c> into a pointer; <c>docs/reference/pointers.md</c>.</summary>
    private object? Pointer(IrIntrinsic intrinsic, Frame frame) {
      if (intrinsic.Op == IrIntrinsicOp.AddressOf) {
        var local = ((IrLocalRef)intrinsic.Arguments[0]).Local;
        return new PointerValue(View.Of(frame.Locals[local].Box(local.Type), local.Type), 0);
      }

      var arguments = Array.ConvertAll([.. intrinsic.Arguments], argument => this.Evaluate(argument, frame));
      var element = intrinsic.Type.Element;
      switch (intrinsic.Op) {
        case IrIntrinsicOp.StackAlloc:
          var count = (long)arguments[0];
          if (count < 0)
            throw new IrTrapException(IrTrap.IntegerOverflow);

          return new PointerValue(View.Of(Array.CreateInstance(HostType(element!), count), element!), 0);
        case IrIntrinsicOp.Pin:
          return Pinned(intrinsic, arguments);
        case IrIntrinsicOp.PointerAdd:
          var moved = AsPointer(arguments[0]);
          return moved with { Offset = moved.Offset + ((long)arguments[1] * element!.Size) };
        case IrIntrinsicOp.PointerCast:
          return AsPointer(arguments[0]);
        case IrIntrinsicOp.PointerLoad:
          return Reached(AsPointer(arguments[0]), intrinsic.Type).Get(0);
        case IrIntrinsicOp.PointerStore:
          Reached(AsPointer(arguments[0]), intrinsic.Arguments[1].Type).Set(0, arguments[1]);
          return null;
        case IrIntrinsicOp.PointerDifference:
          return Distance(AsPointer(arguments[0]), AsPointer(arguments[1])) / intrinsic.Arguments[0].Type.Element!.Size;
        default:
          return Compared(intrinsic.Op, AsPointer(arguments[0]), AsPointer(arguments[1]));
      }
    }

  }

  /// <summary>A pointer as an operation receives it: the null value of a pointer type is the null pointer.</summary>
  private static PointerValue AsPointer(object value) => value as PointerValue ?? PointerValue.Null;

  /// <summary>
  /// The pointer a pin gives: to the first element of an array or a view, the null pointer for <c>null</c> or no
  /// elements; to element <c>i</c>, which has to exist, of a source that has to be no <c>null</c>.
  /// </summary>
  private static PointerValue Pinned(IrIntrinsic pin, object[] arguments) {
    var type = pin.Arguments[0].Type;
    if (arguments[0] is NullReference)
      return arguments.Length == 1 ? PointerValue.Null : throw new IrTrapException(IrTrap.NullReference);

    var region = AsView(arguments[0], type);
    if (arguments.Length == 1)
      return region.Length == 0 ? PointerValue.Null : new PointerValue(region, 0);

    var index = (long)arguments[1];
    if (index < 0 || index >= region.Length)
      throw new IrTrapException(IrTrap.IndexOutOfRange);

    return new PointerValue(region, index * type.Element!.Size);
  }

  /// <summary>The one value a load or a store reaches, or the trap: the null pointer, or bytes not wholly in the region.</summary>
  private static View Reached(PointerValue pointer, IrType type) {
    if (pointer.Region is not { } region)
      throw new IrTrapException(IrTrap.NullReference);

    if (pointer.Offset < 0 || pointer.Offset > region.Bytes - type.Size)
      throw new IrTrapException(IrTrap.IndexOutOfRange);

    return region.At(pointer.Offset, type);
  }

  /// <summary>Whether two pointers lie in one region: the same array, or both based on the null pointer.</summary>
  private static bool Together(PointerValue left, PointerValue right) => ReferenceEquals(left.Backing, right.Backing);

  /// <summary>The bytes from one pointer to another, which have to lie in one region.</summary>
  private static long Distance(PointerValue left, PointerValue right)
    => Together(left, right) ? left.Position - right.Position : throw new IrTrapException(IrTrap.InvalidOperation);

  /// <summary>A comparison of two pointers: equality is false across regions, an ordering traps.</summary>
  private static bool Compared(IrIntrinsicOp op, PointerValue left, PointerValue right) {
    if (!IrPointers.Orders(op)) {
      var same = Together(left, right) && left.Position == right.Position;
      return op == IrIntrinsicOp.PointerEqual ? same : !same;
    }

    var difference = Distance(left, right);
    return op switch {
      IrIntrinsicOp.PointerLess => difference < 0,
      IrIntrinsicOp.PointerLessOrEqual => difference <= 0,
      IrIntrinsicOp.PointerGreater => difference > 0,
      _ => difference >= 0
    };
  }

}
