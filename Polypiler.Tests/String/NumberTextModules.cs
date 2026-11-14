using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.String;

/// <summary>
/// A hand-built module of the conversions between numbers and text - one public function per
/// operation and per form a backend tells apart - and the values every function is called with: the
/// boundaries of the test design, numbers of the reference vectors, and the classes of texts every
/// parser is given.
/// </summary>
internal static class NumberTextModules {

  private static readonly IrType _str = IrType.String;

  private static IrIntrinsic Op(IrIntrinsicOp op, IrType type, params IrExpr[] arguments) => new(op, type, arguments);

  /// <summary>Every function: its name, its parameter type and its body from the parameter.</summary>
  private static readonly (string Name, IrType Parameter, Func<IrExpr, IrExpr> Body)[] _functions = [
    ("FormatEcma", IrType.F64, x => Op(IrIntrinsicOp.FormatEcma, _str, x)),
    ("FormatEcmaInt", IrType.I32, x => Op(IrIntrinsicOp.FormatEcma, _str, x)),
    ("FormatEcmaUInt", IrType.U32, x => Op(IrIntrinsicOp.FormatEcma, _str, x)),
    ("FormatEcmaLong", IrType.I64, x => Op(IrIntrinsicOp.FormatEcma, _str, x)),
    ("FormatEcmaULong", IrType.U64, x => Op(IrIntrinsicOp.FormatEcma, _str, x)),
    ("FormatNet", IrType.F64, x => Op(IrIntrinsicOp.FormatDotNet, _str, x)),
    ("FormatNetSingle", IrType.F32, x => Op(IrIntrinsicOp.FormatDotNet, _str, x)),
    ("FormatNetSByte", IrType.I8, x => Op(IrIntrinsicOp.FormatDotNet, _str, x)),
    ("FormatNetInt", IrType.I32, x => Op(IrIntrinsicOp.FormatDotNet, _str, x)),
    ("FormatNetLong", IrType.I64, x => Op(IrIntrinsicOp.FormatDotNet, _str, x)),
    ("FormatNetULong", IrType.U64, x => Op(IrIntrinsicOp.FormatDotNet, _str, x)),
    ("BoolEcma", IrType.Bool, x => Op(IrIntrinsicOp.FormatBoolEcma, _str, x)),
    ("BoolNet", IrType.Bool, x => Op(IrIntrinsicOp.FormatBoolDotNet, _str, x)),
    ("HexWord", IrType.I32, x => NumberTextOperations.Radix(x, 16, 8, false, RadixMode.TwoComplement)),
    ("HexUpper", IrType.I32, x => NumberTextOperations.Radix(x, 16, 0, true, RadixMode.TwoComplement)),
    ("HexOdd", IrType.I32, x => NumberTextOperations.Radix(x, 16, 3, false, RadixMode.TwoComplement)),
    ("HexByte", IrType.U8, x => NumberTextOperations.Radix(x, 16, 2, false, RadixMode.TwoComplement)),
    ("HexShort", IrType.I16, x => NumberTextOperations.Radix(x, 16, 1, false, RadixMode.TwoComplement)),
    ("HexLong", IrType.I64, x => NumberTextOperations.Radix(x, 16, 16, true, RadixMode.TwoComplement)),
    ("HexULong", IrType.U64, x => NumberTextOperations.Radix(x, 16, 0, false, RadixMode.TwoComplement)),
    ("BinaryInt", IrType.I32, x => NumberTextOperations.Radix(x, 2, 0, false, RadixMode.TwoComplement)),
    ("BinarySByte", IrType.I8, x => NumberTextOperations.Radix(x, 2, 0, false, RadixMode.TwoComplement)),
    ("OctalLong", IrType.I64, x => NumberTextOperations.Radix(x, 8, 0, false, RadixMode.TwoComplement)),
    ("SignHex", IrType.I32, x => NumberTextOperations.Radix(x, 16, 0, false, RadixMode.SignMagnitude)),
    ("SignHexPadded", IrType.I32, x => NumberTextOperations.Radix(x, 16, 4, true, RadixMode.SignMagnitude)),
    ("SignBinaryLong", IrType.I64, x => NumberTextOperations.Radix(x, 2, 0, false, RadixMode.SignMagnitude)),
    ("SignBase36", IrType.I32, x => NumberTextOperations.Radix(x, 36, 0, false, RadixMode.SignMagnitude)),
    ("SignULong", IrType.U64, x => NumberTextOperations.Radix(x, 16, 0, false, RadixMode.SignMagnitude)),
    ("SignUInt", IrType.U32, x => NumberTextOperations.Radix(x, 7, 0, false, RadixMode.SignMagnitude)),
    ("Decimal5", IrType.I32, x => NumberTextOperations.Radix(x, 10, 5, false, RadixMode.SignMagnitude)),
    ("FixedEcma0", IrType.F64, x => NumberTextOperations.Fixed(IrIntrinsicOp.FixedEcma, x, 0)),
    ("FixedEcma2", IrType.F64, x => NumberTextOperations.Fixed(IrIntrinsicOp.FixedEcma, x, 2)),
    ("FixedEcma100", IrType.F64, x => NumberTextOperations.Fixed(IrIntrinsicOp.FixedEcma, x, 100)),
    ("FixedNet0", IrType.F64, x => NumberTextOperations.Fixed(IrIntrinsicOp.FixedDotNet, x, 0)),
    ("FixedNet2", IrType.F64, x => NumberTextOperations.Fixed(IrIntrinsicOp.FixedDotNet, x, 2)),
    ("FixedNet15", IrType.F64, x => NumberTextOperations.Fixed(IrIntrinsicOp.FixedDotNet, x, 15)),
    ("ParseNumber", _str, x => Op(IrIntrinsicOp.ParseNumberEcma, IrType.F64, x)),
    ("ParseFloat", _str, x => Op(IrIntrinsicOp.ParseFloatEcma, IrType.F64, x)),
    ("ParseInt", _str, x => Op(IrIntrinsicOp.ParseIntEcma, IrType.F64, x)),
    ("ParseInt16", _str, x => Op(IrIntrinsicOp.ParseIntEcma, IrType.F64, x, new IrConst(IrType.I32, 16))),
    ("ParseInt36", _str, x => Op(IrIntrinsicOp.ParseIntEcma, IrType.F64, x, new IrConst(IrType.I32, 36))),
    ("ParseInt7", _str, x => Op(IrIntrinsicOp.ParseIntEcma, IrType.F64, x, new IrConst(IrType.I32, 7))),
    ("ParseNetDouble", _str, x => Op(IrIntrinsicOp.ParseDotNet, IrType.F64, x)),
    ("ParseNetSingle", _str, x => Op(IrIntrinsicOp.ParseDotNet, IrType.F32, x)),
    ("ParseNetSByte", _str, x => Op(IrIntrinsicOp.ParseDotNet, IrType.I8, x)),
    ("ParseNetByte", _str, x => Op(IrIntrinsicOp.ParseDotNet, IrType.U8, x)),
    ("ParseNetShort", _str, x => Op(IrIntrinsicOp.ParseDotNet, IrType.I16, x)),
    ("ParseNetUShort", _str, x => Op(IrIntrinsicOp.ParseDotNet, IrType.U16, x)),
    ("ParseNetInt", _str, x => Op(IrIntrinsicOp.ParseDotNet, IrType.I32, x)),
    ("ParseNetUInt", _str, x => Op(IrIntrinsicOp.ParseDotNet, IrType.U32, x)),
    ("ParseNetLong", _str, x => Op(IrIntrinsicOp.ParseDotNet, IrType.I64, x)),
    ("ParseNetULong", _str, x => Op(IrIntrinsicOp.ParseDotNet, IrType.U64, x))
  ];

  /// <summary>The names of the functions.</summary>
  public static IEnumerable<string> Functions => _functions.Select(function => function.Name);

  /// <summary>The parameter type of a function.</summary>
  public static IrType ParameterOf(string name) => _functions.Single(function => function.Name == name).Parameter;

  /// <summary>One public function per operation and form, each returning what the operation computes from its parameter.</summary>
  public static IrModule Operations { get; } = new("Conversions", [
    .. _functions.Select(function => {
      var parameter = new IrLocal("x", function.Parameter);
      var body = function.Body(new IrLocalRef(parameter));
      return new IrFunction(function.Name, true, body.Type, [parameter], [], new IrBlock([new IrReturn(body)]));
    })
  ]);

  /// <summary>The path of the reference vectors of the shortest digits.</summary>
  public static string VectorsPath { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "vectors", "numtext", "shortest.txt");

  /// <summary>The reference vectors: every double with what .NET and ECMA-262 write, every float with what .NET writes.</summary>
  public static IEnumerable<(string Kind, ulong Bits, string DotNet, string? Ecma)> Vectors()
    => File.ReadLines(VectorsPath).Where(line => !line.StartsWith('#')).Select(line => line.Split(' ')).Select(parts =>
      (parts[0], ulong.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture), parts[2], parts.Length > 3 ? parts[3] : null));

  /// <summary>The doubles of the layout thresholds and of the test design's boundaries, both signs.</summary>
  public static IReadOnlyList<double> Doubles { get; } = [
    .. new[] {
      0.0, 1e-7, 1e-6, 1.5e-6, 1e20, 1e21, 1e-5, 1e-4, 1e16, 1e17, 5e-324, double.MaxValue, double.NaN, double.PositiveInfinity,
      0.5, 1.5, 2.5, 0.125, 1.005, 0.045, 999.995, 123.456, 0.1, 0.2, 0.1 + 0.2, 9007199254740993, 123456789012345680000.0, 1e300,
      2.2250738585072014E-308, 4.9406564584124654E-324 * 3, 1e22, 0.000001234, 100, 7
    }.SelectMany(value => new[] { value, -value }),
    .. Vectors().Where(vector => vector.Kind == "f64").Take(150).Select(vector => BitConverter.UInt64BitsToDouble(vector.Bits))
  ];

  /// <summary>The floats of the layout thresholds, both signs, and floats of the reference vectors.</summary>
  public static IReadOnlyList<float> Singles { get; } = [
    .. new[] { 0f, 0.1f, 16777217f, 1e10f, 1e8f, 1e9f, 1e-5f, 1e-4f, float.MaxValue, float.Epsilon, 3.4e38f, 170739.125f, float.NaN, float.PositiveInfinity }
      .SelectMany(value => new[] { value, -value }),
    .. Vectors().Where(vector => vector.Kind == "f32").Take(100).Select(vector => BitConverter.UInt32BitsToSingle((uint)vector.Bits))
  ];

  /// <summary>The integers of the boundaries of every width.</summary>
  public static IReadOnlyList<long> Integers { get; } = [
    0, 1, -1, 7, -7, 255, -128, 127, 32767, -32768, 65535, int.MaxValue, int.MinValue, uint.MaxValue, long.MaxValue, long.MinValue, 123456789, -987654321
  ];

  /// <summary>The classes of texts of the test design: every grammar's accepting and refusing forms.</summary>
  public static IReadOnlyList<string> Texts { get; } = [
    "", " ", "0", "-0", "+5", "12", " 12 ", "\t12\n", ".5", "5.", "1e3", "1E+3", "1.5e-3", "1e", "1e+", "e5", "12px", "0x10", "0X1a", "0x", "0o17", "0b101",
    "0b2", "1,000", "1,000.5", ",1", "1_000", "Infinity", "-Infinity", "infinity", "+Infinity", "INFINITY", "Infinityx", "∞", "NaN", "nan", "-nan", " NaN ",
    "٣", " 1 ", "﻿1", "\u00851", "1\u0000", "1e400", "-1e-400", "1e-320", "2147483647", "2147483648", "-2147483648", "-2147483649",
    "4294967295", "4294967296", "9223372036854775807", "9223372036854775808", "-9223372036854775808", "18446744073709551615", "18446744073709551616",
    "99999999999x", "-1", "255", "256", "-129", "65535", "00000000000000000000000002147483647", "123456789012345678901234567890", "zz", "-zz", "7a",
    "3.4028235e38", "3.4028236e38", "1.401298464324817e-45", "0.1", "9007199254740993", "4.9406564584124654e-324", "1.7976931348623159e308"
  ];

  /// <summary>Every call of every function with the values of its parameter type.</summary>
  public static IEnumerable<PrototypeCall> Calls() {
    foreach (var name in Functions) {
      var parameter = ParameterOf(name);
      IEnumerable<object> values = parameter.Kind switch {
        IrTypeKind.F64 => Doubles.Cast<object>(),
        IrTypeKind.F32 => Singles.Cast<object>(),
        IrTypeKind.Bool => [true, false],
        IrTypeKind.String => Texts,
        IrTypeKind.I8 => Integers.Select(value => (object)(sbyte)value).Distinct(),
        IrTypeKind.U8 => Integers.Select(value => (object)(byte)value).Distinct(),
        IrTypeKind.I16 => Integers.Select(value => (object)(short)value).Distinct(),
        IrTypeKind.I32 => Integers.Select(value => (object)(int)value).Distinct(),
        IrTypeKind.U32 => Integers.Select(value => (object)(uint)value).Distinct(),
        IrTypeKind.I64 => Integers.Cast<object>(),
        _ => Integers.Select(value => (object)(ulong)value)
      };
      foreach (var value in values)
        yield return new PrototypeCall(name, [value]);
    }
  }

}
