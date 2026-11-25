using System;
using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Jvm.Bytecode;

internal sealed record JvmType(IrType IrType, int SlotWidth);

internal sealed record JvmMethodDescriptor(IReadOnlyList<JvmType> Parameters, JvmType ReturnType);

internal static class JvmDescriptor {

  private static readonly Dictionary<string, IrClassType> _exceptions = new(StringComparer.Ordinal) {
    ["java/lang/Throwable"] = IrExceptionClass.Exception,
    ["java/lang/Exception"] = IrExceptionClass.Exception,
    ["java/lang/RuntimeException"] = IrExceptionClass.Exception,
    ["java/lang/ArithmeticException"] = IrExceptionClass.ArithmeticException,
    ["java/lang/IndexOutOfBoundsException"] = IrExceptionClass.IndexOutOfRangeException,
    ["java/lang/ArrayIndexOutOfBoundsException"] = IrExceptionClass.IndexOutOfRangeException,
    ["java/lang/StringIndexOutOfBoundsException"] = IrExceptionClass.IndexOutOfRangeException,
    ["java/lang/NullPointerException"] = IrExceptionClass.NullReferenceException,
    ["java/lang/ClassCastException"] = IrExceptionClass.InvalidCastException,
    ["java/lang/IllegalStateException"] = IrExceptionClass.InvalidOperationException,
    ["java/lang/UnsupportedOperationException"] = IrExceptionClass.InvalidOperationException,
    ["java/lang/IllegalArgumentException"] = IrExceptionClass.ArgumentException,
    ["java/lang/NumberFormatException"] = IrExceptionClass.FormatException,
    ["java/lang/AssertionError"] = IrExceptionClass.AssertionException
  };

  private static readonly Dictionary<IrTypeKind, string> _typeTags = new() {
    [IrTypeKind.Bool] = "z",
    [IrTypeKind.I8] = "i8",
    [IrTypeKind.U8] = "u8",
    [IrTypeKind.I16] = "i16",
    [IrTypeKind.U16] = "u16",
    [IrTypeKind.I32] = "i32",
    [IrTypeKind.I64] = "i64",
    [IrTypeKind.F32] = "f32",
    [IrTypeKind.F64] = "f64",
    [IrTypeKind.String] = "str"
  };

  private static readonly Dictionary<char, JvmType> _primitives = new() {
    ['Z'] = new(IrType.Bool, 1),
    ['B'] = new(IrType.I8, 1),
    ['C'] = new(IrType.U16, 1),
    ['S'] = new(IrType.I16, 1),
    ['I'] = new(IrType.I32, 1),
    ['J'] = new(IrType.I64, 2),
    ['F'] = new(IrType.F32, 1),
    ['D'] = new(IrType.F64, 2)
  };

  public static IrClassType? MapExceptionClass(string? internalName)
    => internalName is null ? IrExceptionClass.Exception : _exceptions.GetValueOrDefault(internalName);

  public static JvmType? TryParseField(string descriptor) {
    var index = 0;
    var parsed = TryParseType(descriptor, ref index, allowVoid: false);
    return parsed is not null && index == descriptor.Length ? parsed : null;
  }

  public static JvmMethodDescriptor? TryParseMethod(string descriptor) {
    if (descriptor.Length < 3 || descriptor[0] != '(')
      return null;

    var index = 1;
    var parameters = new List<JvmType>();
    while (index < descriptor.Length && descriptor[index] != ')') {
      var param = TryParseType(descriptor, ref index, allowVoid: false);
      if (param is null)
        return null;

      parameters.Add(param);
    }

    if (index >= descriptor.Length || descriptor[index] != ')')
      return null;

    ++index;
    var returnType = TryParseType(descriptor, ref index, allowVoid: true);
    return returnType is not null && index == descriptor.Length ? new(parameters, returnType) : null;
  }

  public static IrType ComputationalType(IrType type)
    => type.Kind is IrTypeKind.Bool or IrTypeKind.I8 or IrTypeKind.U8 or IrTypeKind.I16 or IrTypeKind.U16 ? IrType.I32 : type;

  public static bool IsCategory2(IrType type) => type == IrType.I64 || type == IrType.F64;

  public static string TypeTag(IrType type)
    => type.IsArray ? "arr_" + TypeTag(type.Element!)
      : type.IsObject ? "obj_" + type.ClassType!.Name
      : _typeTags[type.Kind];

  private static JvmType? TryParseType(string descriptor, ref int index, bool allowVoid) {
    if (index >= descriptor.Length)
      return null;

    var ch = descriptor[index++];
    if (ch == 'V')
      return allowVoid ? new(IrType.Void, 0) : null;

    if (_primitives.TryGetValue(ch, out var primitive))
      return primitive;

    if (ch == '[') {
      if (index >= descriptor.Length)
        return null;

      if (descriptor[index] == 'Z') {
        ++index;
        return new(IrType.Array(IrType.U8), 1);
      }

      var element = TryParseType(descriptor, ref index, allowVoid: false);
      if (element is null || element.IrType.IsArray || element.IrType.IsObject)
        return null;

      return new(IrType.Array(element.IrType), 1);
    }

    if (ch == 'L') {
      var semi = descriptor.IndexOf(';', index);
      if (semi < 0)
        return null;

      var className = descriptor[index..semi];
      index = semi + 1;
      if (className == "java/lang/String")
        return new(IrType.String, 1);

      if (MapExceptionClass(className) is { } exceptionClass)
        return new(IrType.Object(exceptionClass), 1);
    }

    return null;
  }

}
