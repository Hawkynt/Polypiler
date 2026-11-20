using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR.Intrinsics;

/// <summary>One application of an intrinsic to values: the types of node and operands, and the values as the interpreter carries them.</summary>
/// <param name="Id">The intrinsic.</param>
/// <param name="Type">The type of the node.</param>
/// <param name="ArgumentTypes">The type of each operand.</param>
/// <param name="Arguments">
/// The value of each operand as <see cref="IrInterpreter.Call(IrModule, string, object[])"/> takes it: a
/// value of the .NET type of its IR type (<see cref="uint"/> for a <c>u32</c>, <see cref="float"/> for an
/// <c>f32</c>), an array as an array of that type, a string as a <see cref="string"/>.
/// </param>
public sealed record IntrinsicCall(IntrinsicId Id, IrType Type, IReadOnlyList<IrType> ArgumentTypes, IReadOnlyList<object?> Arguments);

/// <summary>What an evaluation gives: the values of the results, or the trap the operation raised on its operands.</summary>
/// <param name="Values">One value per result; empty for a trap.</param>
/// <param name="Trap">The trap, or <see langword="null"/>.</param>
public sealed record EvaluationResult(IReadOnlyList<object?> Values, IrTrap? Trap) {

  /// <summary>The results of an evaluation that did not trap.</summary>
  /// <param name="values">One value per result.</param>
  /// <returns>The result.</returns>
  public static EvaluationResult Of(params IReadOnlyList<object?> values) => new(values, null);

  /// <summary>An evaluation that trapped.</summary>
  /// <param name="trap">The trap.</param>
  /// <returns>The result.</returns>
  public static EvaluationResult Trapped(IrTrap trap) => new([], trap);

}

/// <summary>
/// The meaning of an intrinsic in code: the one implementation of its semantics the interpreter and
/// constant folding use. It is written independently of the decomposition, so that each checks the other.
/// </summary>
public interface IIntrinsicEvaluator {

  /// <summary>Applies the intrinsic to values.</summary>
  /// <param name="call">The application.</param>
  /// <returns>The results, or the trap.</returns>
  EvaluationResult Evaluate(IntrinsicCall call);

}

/// <summary>
/// The evaluator of an operation of the IR: the interpreter's own meaning of the operation - for the
/// integer operations that of <see cref="IntegerOperations"/>, for the floating-point ones that of the
/// float table -, applied to the values of a call.
/// </summary>
/// <param name="op">The operation.</param>
public sealed class InterpretedEvaluator(IrIntrinsicOp op) : IIntrinsicEvaluator {

  /// <summary>The operation.</summary>
  public IrIntrinsicOp Op { get; } = op;

  /// <inheritdoc />
  /// <remarks>
  /// A scalar operand is a constant of the node - several operations take a mode, a digit count or a
  /// set as a constant only -; an array or a string is a parameter. A node of no value that writes an
  /// array gives that array, as its contract's result describes it.
  /// </remarks>
  public EvaluationResult Evaluate(IntrinsicCall call) {
    ArgumentNullException.ThrowIfNull(call);
    var function = Function(this.Op, call, out var passed);
    try {
      var result = IrInterpreter.Call(new IrModule("Intrinsic", [function]), function.Name, [.. passed.Select(index => call.Arguments[index]!)]);
      return EvaluationResult.Of(call.Type == IrType.Void ? call.Arguments.First(argument => argument is Array) : result);
    } catch (IrTrapException trap) {
      return EvaluationResult.Trapped(trap.Trap);
    }
  }

  /// <summary>
  /// The function that applies an operation to the values of a call: its scalar operands constants, the
  /// others parameters, whose positions among the operands <paramref name="passed"/> lists.
  /// </summary>
  /// <param name="op">The operation.</param>
  /// <param name="call">The call.</param>
  /// <param name="passed">The positions of the operands that are parameters, in order.</param>
  /// <returns>The function <c>Evaluate</c>.</returns>
  public static IrFunction Function(IrIntrinsicOp op, IntrinsicCall call, out IReadOnlyList<int> passed) {
    ArgumentNullException.ThrowIfNull(call);
    var parameters = new List<IrLocal>();
    var positions = new List<int>();
    var operands = new List<IrExpr>();
    for (var index = 0; index < call.ArgumentTypes.Count; ++index) {
      var type = call.ArgumentTypes[index];
      if (Constant(type, call.Arguments[index]) is { } constant) {
        operands.Add(constant);
        continue;
      }

      var parameter = new IrLocal("a" + index, type);
      parameters.Add(parameter);
      positions.Add(index);
      operands.Add(new IrLocalRef(parameter));
    }

    passed = positions;
    var node = new IrIntrinsic(op, call.Type, operands);
    return new IrFunction("Evaluate", true, call.Type, parameters, [],
      new IrBlock([call.Type == IrType.Void ? new IrExprStmt(node) : new IrReturn(node)]));
  }

  /// <summary>A scalar value as a constant of its type, or <see langword="null"/> for a value of another kind.</summary>
  private static IrConst? Constant(IrType type, object? value) => value switch {
    bool truth when type == IrType.Bool => new(type, truth ? 1 : 0),
    float single when type == IrType.F32 => new(type, BitConverter.SingleToInt32Bits(single)),
    double number when type == IrType.F64 => new(type, BitConverter.DoubleToInt64Bits(number)),
    ulong wide when type.IsInteger => new(type, (long)wide),
    sbyte or byte or short or ushort or int or uint or long when type.IsInteger => new(type, System.Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
    _ => null
  };

}

/// <summary>What a decomposition builds with besides the operations of the IR: calls of the functions of Polypiler's libraries.</summary>
public interface IIntrinsicLibrary {

  /// <summary>A call of a function of the libraries, as an expression of a type, its arguments and result converted where their numeric types differ.</summary>
  /// <param name="implementation">The name of the function.</param>
  /// <param name="type">The type of the expression the call stands for.</param>
  /// <param name="arguments">The arguments.</param>
  /// <returns>The call.</returns>
  IrExpr Call(string implementation, IrType type, params IReadOnlyList<IrExpr> arguments);

  /// <summary>An expression the decomposition built, its new nodes legalized as the module is.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The legalized expression.</returns>
  IrExpr Legalize(IrExpr expression);

}

/// <summary>
/// The fallback of an intrinsic: an expression of the type of its node that computes the same value,
/// made of operations of the IR, of intrinsics its contract's <c>lowersTo</c> lists, and of calls of the
/// libraries.
/// </summary>
public interface IIntrinsicDecomposition {

  /// <summary>The replacement of a node whose operands are already expanded.</summary>
  /// <param name="node">The node.</param>
  /// <param name="library">Makes calls of the libraries.</param>
  /// <returns>
  /// The replacement, which evaluates the operands once and in their order; <see langword="null"/> to
  /// keep the node, for a type the decomposition has nothing better for.
  /// </returns>
  IrExpr? Expand(IrIntrinsic node, IIntrinsicLibrary library);

}
