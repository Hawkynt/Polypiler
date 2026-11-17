using System;
using System.Globalization;
using System.Numerics;

namespace Polypiler.Core.IR;

/// <summary>The order of the bytes of a multi-byte value in memory (#119).</summary>
public enum ByteOrder {

  /// <summary>The least significant byte first.</summary>
  Little,

  /// <summary>The most significant byte first.</summary>
  Big

}

/// <summary>
/// A value of the IR as the reference interpreter hands it across <see cref="IrInterpreter.Run"/> (#119): its type and
/// its bit pattern, independent of the host's integer and floating-point types. A scalar - a truth value (0 or 1), an
/// integer or the value of an enum (the pattern of its width, <c>0</c> to <c>2^w - 1</c>, a negative value in two's
/// complement), a floating-point number (its IEEE 754 encoding) or a big integer (the integer itself) - is all bits.
/// Any other value - an array, a string, a struct, an object - has no bit pattern of its own: it crosses as the host
/// value <see cref="IrInterpreter.Call(IrModule, string, object[])"/> takes and returns for its type, in
/// <see cref="Reference"/>, and its <see cref="Bits"/> are zero.
/// </summary>
public sealed record IrValue {

  private IrValue(IrType type, BigInteger bits, object? reference) {
    this.Type = type;
    this.Bits = bits;
    this.Reference = reference;
  }

  /// <summary>The type of the value.</summary>
  public IrType Type { get; }

  /// <summary>The bit pattern of a scalar; zero for any other value.</summary>
  public BigInteger Bits { get; }

  /// <summary>A value that is no scalar as the host carries it; <see langword="null"/> for a scalar, and for a null reference.</summary>
  public object? Reference { get; }

  /// <summary>Whether the value is a scalar, which <see cref="Bits"/> holds entirely.</summary>
  public bool IsScalar => IsScalarType(this.Type);

  /// <summary>
  /// The number a scalar stands for: an integer of a signed type (or of an enum over one) with its sign, of an
  /// unsigned type as it is; a truth value, a floating-point number and a big integer as their bits.
  /// </summary>
  public BigInteger Number => this.Type.Integral is { IsSigned: true } signed && this.Bits >= BigInteger.One << (signed.Bits - 1)
    ? this.Bits - (BigInteger.One << signed.Bits)
    : this.Bits;

  /// <summary>Whether values of a type are scalars: a truth value, an integer, the value of an enum, a floating-point number or a big integer.</summary>
  /// <param name="type">The type.</param>
  /// <returns><see langword="true"/> for the scalar types.</returns>
  public static bool IsScalarType(IrType type) {
    ArgumentNullException.ThrowIfNull(type);
    return type == IrType.Bool || type.IsBigInt || type.Integral.IsNumeric;
  }

  /// <summary>A scalar of a bit pattern.</summary>
  /// <param name="type">A scalar type.</param>
  /// <param name="bits">The pattern: 0 or 1 for a truth value, within the width of an integer or a floating-point type; any integer for a big integer.</param>
  /// <returns>The value.</returns>
  /// <exception cref="ArgumentException">The type is no scalar type.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The pattern does not fit the type.</exception>
  public static IrValue Of(IrType type, BigInteger bits) {
    if (!IsScalarType(type))
      throw new ArgumentException($"{type} is no scalar type: its values cross as references.", nameof(type));

    if (!type.IsBigInt && (bits.Sign < 0 || bits >= BigInteger.One << Width(type)))
      throw new ArgumentOutOfRangeException(nameof(bits), bits, $"not a bit pattern of {type}");

    return new(type, bits, null);
  }

  /// <summary>An integer of a type, reduced to its width in two's complement.</summary>
  /// <param name="type">An integer type, an enum or <see cref="IrType.BigInt"/>.</param>
  /// <param name="value">The number.</param>
  /// <returns>The value.</returns>
  /// <exception cref="ArgumentException">The type is no integer type.</exception>
  public static IrValue Integer(IrType type, BigInteger value) {
    ArgumentNullException.ThrowIfNull(type);
    if (type.IsBigInt)
      return new(type, value, null);

    if (!type.Integral.IsInteger)
      throw new ArgumentException($"{type} is no integer type.", nameof(type));

    return new(type, value & ((BigInteger.One << type.Integral.Bits) - 1), null);
  }

  /// <summary>A truth value.</summary>
  /// <param name="value">The value.</param>
  /// <returns>The value of <see cref="IrType.Bool"/>.</returns>
  public static IrValue Bool(bool value) => new(IrType.Bool, value ? BigInteger.One : BigInteger.Zero, null);

  /// <summary>A single-precision number, with its encoding kept exactly (also the payload of a NaN).</summary>
  /// <param name="value">The number.</param>
  /// <returns>The value of <see cref="IrType.F32"/>.</returns>
  public static IrValue F32(float value) => new(IrType.F32, BitConverter.SingleToUInt32Bits(value), null);

  /// <summary>A double-precision number, with its encoding kept exactly (also the payload of a NaN).</summary>
  /// <param name="value">The number.</param>
  /// <returns>The value of <see cref="IrType.F64"/>.</returns>
  public static IrValue F64(double value) => new(IrType.F64, BitConverter.DoubleToUInt64Bits(value), null);

  /// <summary>A value that is no scalar, as the host carries it.</summary>
  /// <param name="type">A type that is not a scalar type.</param>
  /// <param name="reference">The host value, as <see cref="IrInterpreter.Call(IrModule, string, object[])"/> takes it; <see langword="null"/> for a null reference.</param>
  /// <returns>The value.</returns>
  /// <exception cref="ArgumentException">The type is a scalar type.</exception>
  public static IrValue Referring(IrType type, object? reference) {
    ArgumentNullException.ThrowIfNull(type);
    if (IsScalarType(type))
      throw new ArgumentException($"{type} is a scalar type: its values are bit patterns.", nameof(type));

    return new(type, BigInteger.Zero, reference);
  }

  /// <summary>
  /// A value of a type from its host form, as <see cref="IrInterpreter.Call(IrModule, string, object[])"/> takes it:
  /// a scalar as <see cref="bool"/>, a .NET integer of any width, <see cref="char"/>, <see cref="float"/>,
  /// <see cref="double"/> or <see cref="BigInteger"/>; anything else as it is.
  /// </summary>
  /// <param name="type">The type.</param>
  /// <param name="host">The host value.</param>
  /// <returns>The value.</returns>
  /// <exception cref="ArgumentException">A scalar type with a host value that is no number of a kind it takes.</exception>
  public static IrValue FromHost(IrType type, object? host) {
    ArgumentNullException.ThrowIfNull(type);
    if (!IsScalarType(type))
      return Referring(type, host);

    var plain = type.Integral;
    return (plain.Kind, host) switch {
      (IrTypeKind.Bool, bool value) => Bool(value),
      (IrTypeKind.F32, float value) => new(type, BitConverter.SingleToUInt32Bits(value), null),
      (IrTypeKind.F64, double value) => new(type, BitConverter.DoubleToUInt64Bits(value), null),
      (IrTypeKind.BigInt, BigInteger value) => new(type, value, null),
      (_, sbyte or byte or short or ushort or char or int or uint or long or ulong) when plain.IsInteger => Integer(type, Whole(host)),
      _ => throw new ArgumentException($"{host?.GetType().Name ?? "null"} is no host value of {type}.", nameof(host))
    };
  }

  /// <summary>
  /// The host form of the value, as <see cref="IrInterpreter.Call(IrModule, string, object[])"/> returns it: an integer
  /// as the .NET type of its width and signedness, a floating-point number as <see cref="float"/> or <see cref="double"/>,
  /// a big integer as <see cref="BigInteger"/>; any other value as <see cref="Reference"/>.
  /// </summary>
  /// <returns>The host value.</returns>
  public object? ToHost() => this.Type.Integral.Kind switch {
    IrTypeKind.Bool => !this.Bits.IsZero,
    IrTypeKind.I8 => (sbyte)this.Number,
    IrTypeKind.U8 => (byte)this.Bits,
    IrTypeKind.I16 => (short)this.Number,
    IrTypeKind.U16 => (ushort)this.Bits,
    IrTypeKind.I32 => (int)this.Number,
    IrTypeKind.U32 => (uint)this.Bits,
    IrTypeKind.I64 => (long)this.Number,
    IrTypeKind.U64 => (ulong)this.Bits,
    IrTypeKind.F32 => BitConverter.UInt32BitsToSingle((uint)this.Bits),
    IrTypeKind.F64 => BitConverter.UInt64BitsToDouble((ulong)this.Bits),
    IrTypeKind.BigInt => this.Bits,
    _ => this.Reference
  };

  /// <summary>The bytes of a fixed-width scalar in memory, in a byte order.</summary>
  /// <param name="order">The byte order.</param>
  /// <returns>The bytes: one for a truth value, the size of the type for a number.</returns>
  /// <exception cref="InvalidOperationException">The value has no fixed width: a big integer or no scalar.</exception>
  public byte[] ToBytes(ByteOrder order) {
    if (!this.IsScalar || this.Type.IsBigInt)
      throw new InvalidOperationException($"A value of {this.Type} has no fixed width.");

    var bytes = new byte[Width(this.Type) == 1 ? 1 : Width(this.Type) / 8];
    var little = this.Bits.ToByteArray(isUnsigned: true, isBigEndian: false);
    Array.Copy(little, bytes, Math.Min(little.Length, bytes.Length));
    if (order == ByteOrder.Big)
      Array.Reverse(bytes);

    return bytes;
  }

  /// <summary>A fixed-width scalar from its bytes in memory, in a byte order.</summary>
  /// <param name="type">A scalar type of fixed width.</param>
  /// <param name="bytes">Exactly the bytes of one value: one for a truth value, the size of the type for a number.</param>
  /// <param name="order">The byte order.</param>
  /// <returns>The value.</returns>
  /// <exception cref="ArgumentException">The type has no fixed width, or the number of bytes is not its size.</exception>
  /// <exception cref="ArgumentOutOfRangeException">A truth value of a byte other than 0 and 1.</exception>
  public static IrValue FromBytes(IrType type, ReadOnlySpan<byte> bytes, ByteOrder order) {
    ArgumentNullException.ThrowIfNull(type);
    if (!IsScalarType(type) || type.IsBigInt)
      throw new ArgumentException($"A value of {type} has no fixed width.", nameof(type));

    var size = Width(type) == 1 ? 1 : Width(type) / 8;
    if (bytes.Length != size)
      throw new ArgumentException($"A value of {type} takes {size} bytes, not {bytes.Length}.", nameof(bytes));

    return Of(type, new BigInteger(bytes, isUnsigned: true, isBigEndian: order == ByteOrder.Big));
  }

  /// <summary>The value as the trace and the recorded values print it: a number in decimal, signed as its type is, a floating-point number round-trip, a reference by its type.</summary>
  /// <returns>The text.</returns>
  public override string ToString() => this.Type.Integral.Kind switch {
    IrTypeKind.Bool => this.Bits.IsZero ? "false" : "true",
    IrTypeKind.F32 => ((float)this.ToHost()!).ToString("R", CultureInfo.InvariantCulture),
    IrTypeKind.F64 => ((double)this.ToHost()!).ToString("R", CultureInfo.InvariantCulture),
    _ when this.IsScalar => this.Number.ToString(CultureInfo.InvariantCulture),
    _ => this.Reference is null ? $"null {this.Type}" : $"<{this.Type}>"
  };

  /// <summary>The width of the bit pattern of a fixed-width scalar type: 1 for a truth value.</summary>
  private static int Width(IrType type) => type.Integral.Kind switch {
    IrTypeKind.Bool => 1,
    IrTypeKind.F32 => 32,
    IrTypeKind.F64 => 64,
    _ => type.Integral.Bits
  };

  private static BigInteger Whole(object host) => host switch {
    ulong large => large,
    char unit => unit,
    _ => Convert.ToInt64(host, CultureInfo.InvariantCulture)
  };

}
