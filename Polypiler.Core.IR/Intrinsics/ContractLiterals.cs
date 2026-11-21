using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Polypiler.Core.IR.Intrinsics;

/// <summary>
/// The literals of the reference vectors of a contract as values: a hexadecimal string is the bit
/// pattern of the type, a decimal integer its value, a list an array of the element type or, for a
/// string, its code units.
/// </summary>
public static class ContractLiterals {

  /// <summary>
  /// A literal as a value of a type, as <see cref="IrInterpreter.Call(IrModule, string, object[])"/>
  /// takes and gives it.
  /// </summary>
  /// <param name="type">The type: a scalar, a string, or an array of numbers.</param>
  /// <param name="literal">The literal.</param>
  /// <returns>The value.</returns>
  /// <exception cref="ArgumentException">The literal is no value of the type, or the type is of no other kind.</exception>
  public static object Value(IrType type, JsonNode? literal) {
    ArgumentNullException.ThrowIfNull(type);
    if (type.IsString)
      return literal is JsonArray units ? new string([.. units.Select(unit => (char)(ushort)Number(IrType.U16, unit))]) : throw Refused(type, literal);
    if (type.IsArray && type.Element!.IsNumeric) {
      if (literal is not JsonArray elements)
        throw Refused(type, literal);
      var array = Array.CreateInstance(HostType(type.Element), elements.Count);
      for (var index = 0; index < elements.Count; ++index)
        array.SetValue(Scalar(type.Element, elements[index]), index);
      return array;
    }

    return Scalar(type, literal);
  }

  private static object Scalar(IrType type, JsonNode? literal) {
    if (type == IrType.Bool) {
      var truth = Number(IrType.U8, literal);
      return truth.IsZero ? false : truth.IsOne ? true : throw Refused(type, literal);
    }
    if (!type.IsNumeric)
      throw Refused(type, literal);

    var number = Number(type, literal);
    if (type == IrType.F32)
      return Hexadecimal(literal) ? BitConverter.Int32BitsToSingle((int)(uint)number) : (float)number;
    if (type == IrType.F64)
      return Hexadecimal(literal) ? BitConverter.Int64BitsToDouble((long)(ulong)number) : (double)number;

    var value = (long)(ulong)(number & ulong.MaxValue);
    return type.Kind switch {
      IrTypeKind.I8 => (sbyte)value,
      IrTypeKind.U8 => (byte)value,
      IrTypeKind.I16 => (short)value,
      IrTypeKind.U16 => (ushort)value,
      IrTypeKind.I32 => (int)value,
      IrTypeKind.U32 => (uint)value,
      IrTypeKind.I64 => value,
      _ => (ulong)value
    };
  }

  /// <summary>The number a scalar literal writes: the pattern of a hexadecimal string, the value of a decimal integer.</summary>
  private static BigInteger Number(IrType type, JsonNode? literal) {
    if (literal is JsonValue value && value.TryGetValue<string>(out var text) && Hexadecimal(literal))
      return BigInteger.Parse("0" + text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    if (literal is JsonValue number && BigInteger.TryParse(number.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
      return parsed;
    throw Refused(type, literal);
  }

  private static bool Hexadecimal(JsonNode? literal)
    => literal is JsonValue value && value.TryGetValue<string>(out var text) && text.StartsWith("0x", StringComparison.Ordinal) && text.Length > 2;

  private static Type HostType(IrType element) => element.Kind switch {
    IrTypeKind.I8 => typeof(sbyte),
    IrTypeKind.U8 => typeof(byte),
    IrTypeKind.I16 => typeof(short),
    IrTypeKind.U16 => typeof(ushort),
    IrTypeKind.I32 => typeof(int),
    IrTypeKind.U32 => typeof(uint),
    IrTypeKind.I64 => typeof(long),
    IrTypeKind.U64 => typeof(ulong),
    IrTypeKind.F32 => typeof(float),
    _ => typeof(double)
  };

  private static ArgumentException Refused(IrType type, JsonNode? literal)
    => new($"The literal {literal?.ToJsonString() ?? "null"} is no value of {type}.", nameof(literal));

}
