using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Float.Support;

/// <summary>Modules of one function that applies one floating-point intrinsic to its parameters.</summary>
internal static class FloatIntrinsics {

  private static readonly Dictionary<(IrIntrinsicOp, IrType, int), IrModule> _modules = [];

  /// <summary>The operand types of an operation of a shape in a format.</summary>
  public static IrType[] Operands(FloatShape shape, IrType format) => shape switch {
    FloatShape.Binary => [format, format],
    FloatShape.Ternary => [format, format, format],
    FloatShape.WithInteger => [format, IrType.I32],
    FloatShape.WithDigits => [format, IrType.I32],
    FloatShape.FromBits => [format == IrType.F64 ? IrType.I64 : IrType.I32],
    _ => [format]
  };

  /// <summary>The result type of an operation of a shape in a format.</summary>
  public static IrType Result(FloatShape shape, IrType format) => shape switch {
    FloatShape.ToInteger => IrType.I32,
    FloatShape.Predicate => IrType.Bool,
    FloatShape.ToBits => format == IrType.F64 ? IrType.I64 : IrType.I32,
    _ => format
  };

  /// <summary>
  /// The module of <c>F</c>, which applies the operation to its parameters; a rounding to digits gets
  /// its mode as the constant it has to be.
  /// </summary>
  public static IrModule Module(FloatOperation operation, IrType format, int mode = 0) {
    lock (_modules) {
      if (_modules.TryGetValue((operation.Op, format, mode), out var cached))
        return cached;

      var parameters = Operands(operation.Shape, format).Select((type, index) => new IrLocal("p" + index, type)).ToList();
      var arguments = parameters.Select(parameter => (IrExpr)new IrLocalRef(parameter)).ToList();
      if (operation.Shape == FloatShape.WithDigits)
        arguments.Add(new IrConst(IrType.I32, mode));

      var body = new IrReturn(new IrIntrinsic(operation.Op, Result(operation.Shape, format), arguments));
      var module = new IrModule("M", [new IrFunction("F", true, Result(operation.Shape, format), parameters, [], new IrBlock([body]))]);
      _modules[(operation.Op, format, mode)] = module;
      return module;
    }
  }

  /// <summary>Evaluates the operation in the interpreter on numbers given as doubles.</summary>
  public static object? Evaluate(FloatOperation operation, IrType format, double[] operands, int mode = 0) {
    var types = Operands(operation.Shape, format);
    var arguments = operands.Select((operand, index) => types[index] == IrType.F32 ? (object)(float)operand
      : types[index] == IrType.F64 ? operand
      : types[index] == IrType.I64 ? (long)operand
      : (object)(int)operand).ToArray();
    return IrInterpreter.Call(Module(operation, format, mode), "F", arguments);
  }

}
