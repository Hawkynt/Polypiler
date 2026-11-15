using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The rows of the capability table for the conversions between numbers and text. The rules of .NET
/// are those of .NET Core 3.0 with the invariant culture, which a profile from netcoreapp3.1 on calls
/// itself: .NET Framework writes 15 significant digits for <c>ToString()</c>, has known defects of
/// <c>"R"</c>, rounds <c>"F"</c> away from zero after 15 digits and does not round <c>Parse</c>
/// correctly, and a .NET Standard library may run on it, so the profiles before get the helpers of
/// <see cref="CSharpNumberTextHelpers"/>. The rules of ECMA-262 are helpers in every profile; an
/// integer is written alike by both languages. No conversion depends on the culture of the thread.
/// </summary>
internal static class CSharpNumberTextRows {

  private const string Invariant = "global::System.Globalization.CultureInfo.InvariantCulture";
  private const string Floating = "global::System.Globalization.NumberStyles.Float | global::System.Globalization.NumberStyles.AllowThousands";

  private static readonly IrTypeKind[] _double = [IrTypeKind.F64];
  private static readonly IrTypeKind[] _single = [IrTypeKind.F32];
  private static readonly IrTypeKind[] _text = [IrTypeKind.String];
  private static readonly IrTypeKind[] _bool = [IrTypeKind.Bool];

  private static readonly IrTypeKind[] _integers = [
    IrTypeKind.I8, IrTypeKind.U8, IrTypeKind.I16, IrTypeKind.U16, IrTypeKind.I32, IrTypeKind.U32, IrTypeKind.I64, IrTypeKind.U64
  ];

  /// <summary>The C# type of each integer type, as <c>Parse</c> is called on it.</summary>
  private static readonly (IrTypeKind Kind, string Name, string Type)[] _parsed = [
    (IrTypeKind.I8, "SByte", "sbyte"), (IrTypeKind.U8, "Byte", "byte"), (IrTypeKind.I16, "Int16", "short"), (IrTypeKind.U16, "UInt16", "ushort"),
    (IrTypeKind.I32, "Int32", "int"), (IrTypeKind.U32, "UInt32", "uint"), (IrTypeKind.I64, "Int64", "long"), (IrTypeKind.U64, "UInt64", "ulong")
  ];

  /// <summary>The paddings of a hexadecimal number the framework's <c>"x&lt;n&gt;"</c> writes; any other is the helper.</summary>
  private static readonly int[] _paddings = [0, 2, 4, 8, 16];

  /// <summary>Every row of the conversions.</summary>
  public static IEnumerable<CSharpCapability> All => [
    Row("Double.ToString(IFormatProvider)", IrIntrinsicOp.FormatDotNet, _double, $"{{0}}.ToString({Invariant})", "{h}({0}, 0, 0)", CSharpNumberTextHelpers.NumberText),
    Row("Single.ToString(IFormatProvider)", IrIntrinsicOp.FormatDotNet, _single, $"{{0}}.ToString({Invariant})", "{h}({0}, 1, 0)", CSharpNumberTextHelpers.NumberText),
    Written("Int32.ToString(IFormatProvider) and the other integers", IrIntrinsicOp.FormatDotNet, _integers, $"{{0}}.ToString({Invariant})"),
    Helped("Number::toString of ECMA-262", IrIntrinsicOp.FormatEcma, _double, "{h}({0}, 2, 0)", CSharpNumberTextHelpers.NumberText),
    Written("Number::toString of ECMA-262 of an integer", IrIntrinsicOp.FormatEcma, _integers, $"{{0}}.ToString({Invariant})"),
    Written("toString of a boolean of ECMA-262", IrIntrinsicOp.FormatBoolEcma, _bool, "({0} ? \"true\" : \"false\")"),
    Written("Boolean.ToString()", IrIntrinsicOp.FormatBoolDotNet, _bool, "({0} ? \"True\" : \"False\")"),
    .. _paddings.SelectMany(padding => new[] { false, true }.Select(upper => Hexadecimal(padding, upper))),
    Radix("two's complement of 8 bits", RadixMode.TwoComplement, [IrTypeKind.I8, IrTypeKind.U8], "(long)(byte){0}, false"),
    Radix("two's complement of 16 bits", RadixMode.TwoComplement, [IrTypeKind.I16, IrTypeKind.U16], "(long)(ushort){0}, false"),
    Radix("two's complement of 32 bits", RadixMode.TwoComplement, [IrTypeKind.I32, IrTypeKind.U32], "(long)(uint){0}, false"),
    Radix("two's complement of 64 bits", RadixMode.TwoComplement, [IrTypeKind.I64, IrTypeKind.U64], "(long){0}, false"),
    Radix("sign and magnitude", RadixMode.SignMagnitude, [.. _integers.Where(kind => kind != IrTypeKind.U64)], "(long){0}, true"),
    Radix("sign and magnitude of 64 bits unsigned", RadixMode.SignMagnitude, [IrTypeKind.U64], "(long){0}, false"),
    .. Enumerable.Range(0, NumberTextOperations.MaxDotNetDigits + 1).Select(Fixed),
    Helped("toFixed of ECMA-262", IrIntrinsicOp.FixedEcma, _double, "{h}({0}, 3, {1})", CSharpNumberTextHelpers.NumberText),
    Helped("StringToNumber of ECMA-262", IrIntrinsicOp.ParseNumberEcma, _text, "{h}({0}, 2, 0)", CSharpNumberTextHelpers.ParseText),
    Helped("parseFloat of ECMA-262", IrIntrinsicOp.ParseFloatEcma, _text, "{h}({0}, 3, 0)", CSharpNumberTextHelpers.ParseText),
    Helped("parseInt of ECMA-262", IrIntrinsicOp.ParseIntEcma, _text, "{h}({0}, 4, 0)", CSharpNumberTextHelpers.ParseText).When(intrinsic => intrinsic.Arguments.Count == 1),
    Helped("parseInt of ECMA-262 with a radix", IrIntrinsicOp.ParseIntEcma, _text, "{h}({0}, 4, {1})", CSharpNumberTextHelpers.ParseText),
    .. _parsed.Select(parsed => Written($"{parsed.Name}.Parse(String, NumberStyles, IFormatProvider)", IrIntrinsicOp.ParseDotNet, _text,
      $"{parsed.Type}.Parse({{0}}, global::System.Globalization.NumberStyles.Integer, {Invariant})").When(Yields(parsed.Kind))),
    Row("Double.Parse(String, NumberStyles, IFormatProvider)", IrIntrinsicOp.ParseDotNet, _text, $"double.Parse({{0}}, {Floating}, {Invariant})", "{h}({0}, 0, 0)", CSharpNumberTextHelpers.ParseText)
      .When(Yields(IrTypeKind.F64)),
    Row("Single.Parse(String, NumberStyles, IFormatProvider)", IrIntrinsicOp.ParseDotNet, _text, $"float.Parse({{0}}, {Floating}, {Invariant})", "(float){h}({0}, 1, 0)", CSharpNumberTextHelpers.ParseText)
      .When(Yields(IrTypeKind.F32))
  ];

  /// <summary>Whether a parse yields a type.</summary>
  private static Func<IrIntrinsic, bool> Yields(IrTypeKind kind) => intrinsic => intrinsic.Type.Kind == kind;

  /// <summary>The hexadecimal two's complement the framework writes with <c>"x&lt;n&gt;"</c> or <c>"X&lt;n&gt;"</c> in every profile.</summary>
  private static CSharpCapability Hexadecimal(int padding, bool upper) {
    var format = (upper ? "X" : "x") + (padding == 0 ? "" : padding.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return Written($"Int32.ToString(\"{format}\") and the other integers", IrIntrinsicOp.FormatRadix, _integers, $"{{0}}.ToString(\"{format}\")")
      .When(intrinsic => NumberTextOperations.RadixOf(intrinsic) is (16, var minimum, var capital, RadixMode.TwoComplement) && capital == upper && (minimum == padding || (padding == 0 && minimum == 1)));
  }

  /// <summary>A radix in a mode, by the helper: the value as the <c>long</c> it is in that mode, and whether it is signed.</summary>
  private static CSharpCapability Radix(string name, RadixMode mode, IrTypeKind[] operands, string value)
    => Helped($"digits of a radix, {name}", IrIntrinsicOp.FormatRadix, operands, $"{{h}}({value}, {{1}}, {{2}}, {{3}})", CSharpNumberTextHelpers.FormatRadix)
      .When(intrinsic => NumberTextOperations.RadixOf(intrinsic)?.Mode == mode);

  /// <summary><c>"F&lt;n&gt;"</c> for one number of digits, which the format string spells.</summary>
  private static CSharpCapability Fixed(int digits)
    => Row($"Double.ToString(\"F{digits}\", IFormatProvider)", IrIntrinsicOp.FixedDotNet, _double, $"{{0}}.ToString(\"F{digits}\", {Invariant})", "{h}({0}, 4, {1})", CSharpNumberTextHelpers.NumberText)
      .When(intrinsic => NumberTextOperations.DigitsOf(intrinsic) == digits);

  /// <summary>A row that C# writes with a member every profile has.</summary>
  private static CSharpCapability Written(string name, IrIntrinsicOp op, IrTypeKind[] operands, string template)
    => CSharpCapability.OfOperation(name, CSharpProfile.Net20, op, operands, new CSharpForm(null, Template: template));

  /// <summary>A row with the member of .NET Core 3.0 from netcoreapp3.1 on and the helper before.</summary>
  private static CSharpCapability Row(string name, IrIntrinsicOp op, IrTypeKind[] operands, string native, string replacement, CSharpHelper helper)
    => CSharpCapability.OfOperation(name, CSharpProfile.NetCoreApp31, op, operands, new CSharpForm(null, Template: native), new CSharpForm(null, Template: replacement), helper);

  /// <summary>A row that is a helper in every profile.</summary>
  private static CSharpCapability Helped(string name, IrIntrinsicOp op, IrTypeKind[] operands, string template, CSharpHelper helper)
    => CSharpCapability.OfOperation(name, null, op, operands, null, new CSharpForm(null, Template: template), helper);

}
