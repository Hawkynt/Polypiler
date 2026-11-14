using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Float.Support;

/// <summary>
/// A function of the reference vectors and the portable implementation that computes it: the name of
/// the vector files, the name of the method of <see cref="FloatOperations"/> for <c>double</c> (that of
/// <c>float</c> appends <c>F</c>), and how far from the correctly rounded result it may be.
/// </summary>
/// <param name="Vectors">The name of the vector files.</param>
/// <param name="Method">The name of the implementation of <c>double</c>.</param>
/// <param name="Distance">The largest comparison distance from the correctly rounded result; 0 for a correctly rounded implementation.</param>
/// <param name="Redefined">
/// The operands where the operation is defined otherwise than the real function, which the vectors hold:
/// <c>LogBase</c> follows .NET for the bases 1, 0 and +∞. The special-value table fixes those results.
/// </param>
internal sealed record FloatFunction(string Vectors, string Method, int Distance = 0, Func<double[], bool>? Redefined = null) {

  /// <summary>The function as a test name shows it.</summary>
  public override string ToString() => this.Method;

}

/// <summary>
/// The portable implementations twice: as the C# compiler built <see cref="FloatOperations"/> into
/// the IR assembly, and as the C# frontend imports its embedded source into IR, which the interpreter
/// runs and the backends emit. The two have to agree bit for bit.
/// </summary>
internal static class FloatImplementations {

  private static readonly Lazy<IrModule> _module = new(Import);

  /// <summary>Every function the vector files hold, with its implementation.</summary>
  public static IReadOnlyList<FloatFunction> Functions { get; } = [
    new("sin", "Sin"), new("cos", "Cos"), new("tan", "Tan"),
    new("asin", "Asin"), new("acos", "Acos"), new("atan", "Atan"), new("atan2", "Atan2"),
    new("sinh", "Sinh"), new("cosh", "Cosh"), new("tanh", "Tanh"),
    new("asinh", "Asinh"), new("acosh", "Acosh"), new("atanh", "Atanh"),
    new("exp", "Exp"), new("exp2", "Exp2"), new("exp10", "Exp10"), new("expm1", "ExpM1"),
    new("log", "Log"), new("log2", "Log2"), new("log10", "Log10"), new("log1p", "Log1P"), new("logbase", "LogBase", 2, operands => operands[1] == 1 || (operands[1] == 0 || double.IsPositiveInfinity(operands[1])) && operands[0] != 1),
    new("pow", "Pow"), new("cbrt", "Cbrt"), new("hypot", "Hypot"), new("rsqrt", "ReciprocalSqrt"), new("rootn", "RootN"),
    new("remainder", "IeeeRemainder"), new("fma", "FusedMultiplyAdd")
  ];

  /// <summary>The functions whose implementation exists, in both formats.</summary>
  public static IEnumerable<FloatFunction> Implemented => Functions.Where(function => Method(function.Method) is not null && Method(function.Method + "F") is not null);

  /// <summary>The functions and formats whose implementation exists, as test cases.</summary>
  public static IEnumerable<TestCaseData> ImplementedCases
    => Implemented.SelectMany(function => new[] { IrType.F32, IrType.F64 }.Select(format
      => new TestCaseData(function, format).SetArgDisplayNames(function.Method, format.ToString())));

  /// <summary>The source of <see cref="FloatOperations"/> imported into IR; fails with the diagnostics when the frontend refuses it.</summary>
  public static IrModule Module => _module.Value;

  /// <summary>A public static method of <see cref="FloatOperations"/>, or <see langword="null"/>.</summary>
  public static MethodInfo? Method(string name) => typeof(FloatOperations).GetMethod(name, BindingFlags.Public | BindingFlags.Static);

  /// <summary>The name of the implementation of a function in a format.</summary>
  public static string NameIn(string method, IrType format) => format == IrType.F32 ? method + "F" : method;

  /// <summary>
  /// The arguments of an implementation for the operands of a vector: each floating-point operand as a
  /// number of the format, an integer operand as an <see cref="int"/>.
  /// </summary>
  public static object[] Arguments(MethodInfo method, IrType format, long[] operands)
    => method.GetParameters().Select((parameter, index) => parameter.ParameterType == typeof(int)
      ? (object)(int)operands[index]
      : format == IrType.F32 ? (object)(float)FloatVectors.Value(format, operands[index]) : FloatVectors.Value(format, operands[index])).ToArray();

  /// <summary>The bits of a result as the vectors carry them.</summary>
  public static long Bits(IrType format, object? result) => result switch {
    float single => BitConverter.SingleToInt32Bits(single) & 0xffffffffL,
    double number => BitConverter.DoubleToInt64Bits(number),
    bool truth => truth ? 1 : 0,
    int integer => integer,
    _ => throw new ArgumentException($"no result of {format}", nameof(result))
  };

  /// <summary>Calls the compiled implementation.</summary>
  public static long Compiled(string method, IrType format, long[] operands) {
    var target = Method(NameIn(method, format))!;
    return Bits(format, target.Invoke(null, Arguments(target, format, operands)));
  }

  /// <summary>Calls the imported implementation in the interpreter.</summary>
  public static long Interpreted(string method, IrType format, long[] operands) {
    var target = Method(NameIn(method, format))!;
    return Bits(format, IrInterpreter.Call(Module, target.Name, Arguments(target, format, operands)));
  }

  private static IrModule Import() {
    var result = CSharpFrontend.Import(FloatOperationSources.All.Select(file => new CSharpSource(file.Name, file.Text)).ToList());
    if (result.Module is null)
      Assert.Fail(string.Join(Environment.NewLine, result.Diagnostics.Take(20)));

    return result.Module!;
  }

}
