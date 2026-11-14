using System;
using System.Collections.Generic;

namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>
  /// A view as the interpreter carries it: the array it views, where it starts in that array's memory,
  /// counted in bytes, and how many elements of which type it has. A view of the array's own element
  /// type that starts on an element reads the array directly; any other view - one that
  /// <see cref="IrIntrinsicOp.Reinterpret"/> made - reads and writes the bytes of the array's elements,
  /// the lowest byte of each element first, as the IR fixes it.
  /// </summary>
  private sealed class View(Array backing, IrType backingElement, long byteOffset, int length, IrType element) {

    public int Length { get; } = length;

    public IrType Element { get; } = element;

    private int Size => this.Element.Size;

    private bool Direct => this.Element == backingElement && byteOffset % this.Size == 0;

    /// <summary>The whole of an array as a view.</summary>
    public static View Of(Array array, IrType element) => new(array, element, 0, array.Length, element);

    /// <summary>The part of the view from an element on, which the caller has checked lies in it.</summary>
    public View Part(long start, long count) => new(backing, backingElement, byteOffset + (start * this.Size), (int)count, this.Element);

    /// <summary>The bytes of the view read as elements of another type: as many whole ones as they hold.</summary>
    public View As(IrType type) => new(backing, backingElement, byteOffset, (int)((long)this.Length * this.Size / type.Size), type);

    public object Get(int index) {
      if (this.Direct)
        return Internal(backing.GetValue((byteOffset / this.Size) + index)!);

      var pattern = 0UL;
      var first = byteOffset + ((long)index * this.Size);
      for (var offset = this.Size - 1; offset >= 0; --offset)
        pattern = (pattern << 8) | this.Byte(first + offset);

      return FromBits(this.Element, pattern);
    }

    public void Set(int index, object value) {
      if (this.Direct) {
        backing.SetValue(External(backing.GetType().GetElementType()!, value), (byteOffset / this.Size) + index);
        return;
      }

      var pattern = Bits(this.Element, value);
      var first = byteOffset + ((long)index * this.Size);
      for (var offset = 0; offset < this.Size; ++offset)
        this.Byte(first + offset, (byte)(pattern >> (8 * offset)));
    }

    /// <summary>A byte of the memory of the array, by its position from the start of the array.</summary>
    private byte Byte(long position) {
      var size = backingElement.Size;
      var bits = Bits(backingElement, Internal(backing.GetValue(position / size)!));
      return (byte)(bits >> (int)(8 * (position % size)));
    }

    private void Byte(long position, byte value) {
      var size = backingElement.Size;
      var index = position / size;
      var shift = (int)(8 * (position % size));
      var bits = Bits(backingElement, Internal(backing.GetValue(index)!));
      bits = (bits & ~(0xFFUL << shift)) | ((ulong)value << shift);
      backing.SetValue(External(backing.GetType().GetElementType()!, FromBits(backingElement, bits)), index);
    }

    /// <summary>The bits of a carried number of a type.</summary>
    private static ulong Bits(IrType type, object value) => value switch {
      float single => (ulong)(uint)BitConverter.SingleToInt32Bits(single),
      double number => (ulong)BitConverter.DoubleToInt64Bits(number),
      _ => (ulong)(long)value & (type.Bits == 64 ? ulong.MaxValue : (1UL << type.Bits) - 1)
    };

    /// <summary>The carried number of a type that has the bits.</summary>
    private static object FromBits(IrType type, ulong pattern) => type.Kind switch {
      // The arms are of different types; without the first cast the compiler would make them all doubles.
      IrTypeKind.F32 => (object)BitConverter.Int32BitsToSingle((int)(uint)pattern),
      IrTypeKind.F64 => BitConverter.Int64BitsToDouble((long)pattern),
      _ => IrOperators.Wrap(type, (long)pattern)
    };

  }

  /// <summary>The elements of a view as a new array of the host's type for them.</summary>
  private static Array ToArray(View view) {
    var array = Array.CreateInstance(HostType(view.Element), view.Length);
    for (var index = 0; index < view.Length; ++index)
      array.SetValue(External(array.GetType().GetElementType()!, view.Get(index)), index);

    return array;
  }

  /// <summary>The value an array or a view is read through: a view of the whole array, or the view itself.</summary>
  private static View AsView(object value, IrType type) => value as View ?? View.Of((Array)value, type.Element!);

  /// <summary>The part of a source from an element on, or the trap when it does not lie in the source.</summary>
  private static View Part(View source, long start, long count) {
    if (start < 0 || count < 0 || start + count > source.Length)
      throw new IrTrapException(IrTrap.ArgumentOutOfRange);

    return source.Part(start, count);
  }

  /// <summary>The part of a source that <c>(source, start)</c> or <c>(source, start, length)</c> names; without a length it runs to the end.</summary>
  private static View Part(View source, object[] arguments) {
    var start = (long)arguments[1];
    return Part(source, start, arguments.Length == 3 ? (long)arguments[2] : source.Length - start);
  }

  /// <summary>An index of an element of a view, or the bounds trap.</summary>
  private static int Checked(View view, long index) {
    if (index < 0 || index >= view.Length)
      throw new IrTrapException(IrTrap.IndexOutOfRange);

    return (int)index;
  }

  /// <summary>Two elements of one type are equal as <c>Equals</c> of .NET compares them: a NaN equals a NaN, and the zeros are equal.</summary>
  private static bool SameElement(object left, object right) => left.Equals(right);

  /// <summary>
  /// An operation of the views on the values of its arguments: <see cref="IrViews.Operations"/>, and an
  /// endian load or store on a view of bytes, which is checked against the view - a value that does not
  /// lie in it traps with an argument out of range, as <c>BinaryPrimitives</c> on a span throws - and
  /// then reads or writes its bytes as the operation on an array does.
  /// </summary>
  private static object? Views(IrIntrinsic intrinsic, object[] arguments) {
    var types = intrinsic.Arguments;
    switch (intrinsic.Op) {
      case IrIntrinsicOp.ViewOf:
        var whole = AsView(arguments[0], types[0].Type);
        return arguments.Length == 1 ? whole : Part(whole, arguments);
      case IrIntrinsicOp.Slice:
        return Part((View)arguments[0], arguments);
      case IrIntrinsicOp.ViewLength:
        return (long)((View)arguments[0]).Length;
      case IrIntrinsicOp.StackAlloc:
        var count = (long)arguments[0];
        if (count < 0)
          throw new IrTrapException(IrTrap.IntegerOverflow);

        return View.Of(Array.CreateInstance(HostType(intrinsic.Type.Element!), count), intrinsic.Type.Element!);
      case IrIntrinsicOp.ViewCopy or IrIntrinsicOp.ViewTryCopy:
        var source = (View)arguments[0];
        var target = (View)arguments[1];
        if (source.Length > target.Length)
          return intrinsic.Op == IrIntrinsicOp.ViewTryCopy ? false : throw new IrTrapException(IrTrap.ArgumentOutOfRange);

        var buffer = new object[source.Length];
        for (var index = 0; index < buffer.Length; ++index)
          buffer[index] = source.Get(index);

        for (var index = 0; index < buffer.Length; ++index)
          target.Set(index, buffer[index]);

        return intrinsic.Op == IrIntrinsicOp.ViewTryCopy ? true : null;
      case IrIntrinsicOp.ViewFill:
        var filled = (View)arguments[0];
        for (var index = 0; index < filled.Length; ++index)
          filled.Set(index, arguments[1]);

        return null;
      case IrIntrinsicOp.ViewEqual:
        var left = (View)arguments[0];
        var right = (View)arguments[1];
        if (left.Length != right.Length)
          return false;

        for (var index = 0; index < left.Length; ++index)
          if (!SameElement(left.Get(index), right.Get(index)))
            return false;

        return true;
      case IrIntrinsicOp.ViewToArray:
        return ToArray((View)arguments[0]);
      case IrIntrinsicOp.Reinterpret:
        return ((View)arguments[0]).As(intrinsic.Type.Element!);
    }

    // An endian load or store on a view of bytes.
    var bytes = (View)arguments[0];
    var at = (long)arguments[1];
    var store = intrinsic.Op is IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig;
    var type = store ? types[2].Type : intrinsic.Type;
    if (at < 0 || at + type.Size > bytes.Length)
      throw new IrTrapException(IrTrap.ArgumentOutOfRange);

    var little = intrinsic.Op is IrIntrinsicOp.LoadLittle or IrIntrinsicOp.StoreLittle;
    var copy = new byte[type.Size];
    if (!store) {
      for (var index = 0; index < copy.Length; ++index)
        copy[index] = (byte)(long)bytes.Get((int)at + index);

      return IntegerOperations.Load(type, copy, 0, little);
    }

    IntegerOperations.Store(type, copy, 0, arguments[2], little);
    for (var index = 0; index < copy.Length; ++index)
      bytes.Set((int)at + index, (long)copy[index]);

    return null;
  }

  /// <summary>Whether the interpreter evaluates a node as an operation of the views.</summary>
  private static bool IsView(IrIntrinsic intrinsic) => IrViews.IsViewOperation(intrinsic.Op) || IrViews.IsEndianOnView(intrinsic);

  /// <summary>An element of an array or of a view, read after its index is checked.</summary>
  private static object LoadElement(object sequence, long index)
    => sequence is View view ? view.Get(Checked(view, index)) : Internal(((Array)sequence).GetValue(CheckedIndex((Array)sequence, index))!);

  /// <summary>An element of an array or of a view, written after its index is checked.</summary>
  private static void StoreElement(object sequence, long index, object value) {
    if (sequence is View view) {
      view.Set(Checked(view, index), value);
      return;
    }

    var array = (Array)sequence;
    array.SetValue(External(array.GetType().GetElementType()!, value), CheckedIndex(array, index));
  }

  private static int CheckedIndex(Array array, long index) {
    if (index < 0 || index >= array.Length)
      throw new IrTrapException(IrTrap.IndexOutOfRange);

    return (int)index;
  }

  /// <summary>The views a host value of a list crosses as; only an array is taken, as a view of the whole of it.</summary>
  private static object? AcceptedView(IrType type, object? value)
    => value is Array array && TypeOf(array.GetType()) is { } host && host.Element == type.Element ? View.Of(array, type.Element!) : null;

}
