using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>The operand and result types of a floating-point operation, in terms of its format <c>F</c>.</summary>
public enum FloatShape {

  /// <summary><c>(F) → F</c>.</summary>
  Unary,

  /// <summary><c>(F, F) → F</c>.</summary>
  Binary,

  /// <summary><c>(F, F, F) → F</c>.</summary>
  Ternary,

  /// <summary><c>(F, i32) → F</c>.</summary>
  WithInteger,

  /// <summary><c>(F) → i32</c>.</summary>
  ToInteger,

  /// <summary><c>(F) → bool</c>.</summary>
  Predicate,

  /// <summary><c>(F, i32 digits, i32 mode) → F</c>, the mode a constant.</summary>
  WithDigits,

  /// <summary><c>(F) → I</c>, the integer of the width of the format: its bits.</summary>
  ToBits,

  /// <summary><c>(I) → F</c>: the number with the bits of the integer of its width.</summary>
  FromBits

}

/// <summary>What an operation promises about its result (ADR 0005).</summary>
public enum FloatContractKind {

  /// <summary>One result, the same bits on every target: correctly rounded, or a fixed sequence of such operations.</summary>
  Exact,

  /// <summary>Within a number of units in the last place of the exact result.</summary>
  Ulp

}

/// <summary>
/// The contract of an operation: <c>exact</c>, or <c>ulp(n)</c> with <c>n</c> the reference bound a
/// native implementation must keep to be used, and the distance of the portable implementation from
/// the correctly rounded result, which is 0 where it is correctly rounded.
/// </summary>
/// <param name="Kind">Exact or within a bound.</param>
/// <param name="Bound">The reference bound <c>n</c> in ULP; 0 for an exact operation.</param>
/// <param name="HelperDistance">The largest distance, in values of the format, of the portable implementation from the correctly rounded result.</param>
public sealed record FloatContract(FloatContractKind Kind, int Bound, int HelperDistance) {

  /// <summary>The contract of an exact operation.</summary>
  public static FloatContract Exact { get; } = new(FloatContractKind.Exact, 0, 0);

  /// <summary>The contract <c>ulp(n)</c> of an operation whose portable implementation is correctly rounded.</summary>
  /// <param name="bound">The reference bound.</param>
  /// <returns>The contract.</returns>
  public static FloatContract Ulp(int bound) => new(FloatContractKind.Ulp, bound, 0);

  /// <summary>The contract as ADR 0005 spells it: <c>exact</c> or <c>ulp(n)</c>.</summary>
  public override string ToString() => this.Kind == FloatContractKind.Exact ? "exact" : $"ulp({this.Bound})";

}

/// <summary>A result fixed for every target and every mode: the operands and the bits of the result.</summary>
/// <param name="Op">The operation.</param>
/// <param name="Format"><see cref="IrType.F32"/> or <see cref="IrType.F64"/>.</param>
/// <param name="Operands">The operands; a floating-point one as its bits, an integer one as its value.</param>
/// <param name="Result">The bits of the result, the value of an integer one, 1 or 0 for a truth value; any NaN matches a NaN.</param>
/// <param name="Note">Why the value is fixed, as the reference page states it.</param>
public sealed record FloatSpecialValue(IrIntrinsicOp Op, IrType Format, IReadOnlyList<long> Operands, long Result, string Note);

/// <summary>
/// One floating-point operation of the IR: its shape, its contract and its portable implementation
/// in both formats, which the interpreter evaluates and a backend emits where it has no native call.
/// </summary>
public sealed class FloatOperation {

  private FloatOperation(IrIntrinsicOp op, string family, FloatShape shape, FloatContract contract) {
    this.Op = op;
    this.Family = family;
    this.Shape = shape;
    this.Contract = contract;
  }

  /// <summary>The operation.</summary>
  public IrIntrinsicOp Op { get; }

  /// <summary>The family, as the reference page groups the operations.</summary>
  public string Family { get; }

  /// <summary>The operand and result types.</summary>
  public FloatShape Shape { get; }

  /// <summary>The contract.</summary>
  public FloatContract Contract { get; }

  /// <summary>
  /// The name of the portable implementation of <c>double</c> in <see cref="FloatOperations"/>, or
  /// <see langword="null"/> for an operation every target has as an operator or a call of the IR.
  /// </summary>
  public string? DoubleName { get; private init; }

  /// <summary>The name of the portable implementation of <c>float</c>, or <see langword="null"/>.</summary>
  public string? SingleName { get; private init; }

  /// <summary>Evaluates the operation on <c>double</c> operands as the interpreter carries them, or <see langword="null"/> where the interpreter has its own rule.</summary>
  public Func<object[], object>? Double { get; private init; }

  /// <summary>Evaluates the operation on <c>float</c> operands, or <see langword="null"/>.</summary>
  public Func<object[], object>? Single { get; private init; }

  /// <summary>Whether operands make the operation trap with an invalid operation before it computes anything.</summary>
  public Func<object[], bool>? Traps { get; private init; }

  /// <summary>The special values of the operation, compared exactly in every mode.</summary>
  public IReadOnlyList<FloatSpecialValue> SpecialValues { get; private init; } = [];

  /// <summary>An operation with a portable implementation in both formats.</summary>
  /// <param name="op">The operation.</param>
  /// <param name="family">The family.</param>
  /// <param name="shape">The operand and result types.</param>
  /// <param name="contract">The contract.</param>
  /// <param name="name">The name of the implementation of <c>double</c>; that of <c>float</c> appends <c>F</c>.</param>
  /// <param name="single">The implementation of <c>float</c>.</param>
  /// <param name="double">The implementation of <c>double</c>.</param>
  /// <param name="traps">When the operation traps, or <see langword="null"/> when it never does.</param>
  /// <returns>The operation.</returns>
  public static FloatOperation Of(
    IrIntrinsicOp op, string family, FloatShape shape, FloatContract contract, string name,
    Func<object[], object> single, Func<object[], object> @double, Func<object[], bool>? traps = null)
    => new(op, family, shape, contract) { DoubleName = name, SingleName = name + "F", Single = single, Double = @double, Traps = traps };

  /// <summary>An operation of the shape <see cref="FloatShape.Unary"/>.</summary>
  /// <param name="op">The operation.</param>
  /// <param name="family">The family.</param>
  /// <param name="contract">The contract.</param>
  /// <param name="name">The name of the implementation of <c>double</c>.</param>
  /// <param name="single">The implementation of <c>float</c>.</param>
  /// <param name="double">The implementation of <c>double</c>.</param>
  /// <returns>The operation.</returns>
  public static FloatOperation Unary(IrIntrinsicOp op, string family, FloatContract contract, string name, Func<float, float> single, Func<double, double> @double)
    => Of(op, family, FloatShape.Unary, contract, name, arguments => single((float)arguments[0]), arguments => @double((double)arguments[0]));

  /// <summary>An operation of the shape <see cref="FloatShape.Binary"/>.</summary>
  /// <param name="op">The operation.</param>
  /// <param name="family">The family.</param>
  /// <param name="contract">The contract.</param>
  /// <param name="name">The name of the implementation of <c>double</c>.</param>
  /// <param name="single">The implementation of <c>float</c>.</param>
  /// <param name="double">The implementation of <c>double</c>.</param>
  /// <returns>The operation.</returns>
  public static FloatOperation Binary(IrIntrinsicOp op, string family, FloatContract contract, string name, Func<float, float, float> single, Func<double, double, double> @double)
    => Of(op, family, FloatShape.Binary, contract, name,
      arguments => single((float)arguments[0], (float)arguments[1]), arguments => @double((double)arguments[0], (double)arguments[1]));

  /// <summary>An operation of the shape <see cref="FloatShape.Ternary"/>.</summary>
  /// <param name="op">The operation.</param>
  /// <param name="family">The family.</param>
  /// <param name="contract">The contract.</param>
  /// <param name="name">The name of the implementation of <c>double</c>.</param>
  /// <param name="single">The implementation of <c>float</c>.</param>
  /// <param name="double">The implementation of <c>double</c>.</param>
  /// <param name="traps">When the operation traps, or <see langword="null"/> when it never does.</param>
  /// <returns>The operation.</returns>
  public static FloatOperation Ternary(
    IrIntrinsicOp op, string family, FloatContract contract, string name, Func<float, float, float, float> single,
    Func<double, double, double, double> @double, Func<object[], bool>? traps = null)
    => Of(op, family, FloatShape.Ternary, contract, name,
      arguments => single((float)arguments[0], (float)arguments[1], (float)arguments[2]),
      arguments => @double((double)arguments[0], (double)arguments[1], (double)arguments[2]), traps);

  /// <summary>An operation of the shape <see cref="FloatShape.WithInteger"/>.</summary>
  /// <param name="op">The operation.</param>
  /// <param name="family">The family.</param>
  /// <param name="contract">The contract.</param>
  /// <param name="name">The name of the implementation of <c>double</c>.</param>
  /// <param name="single">The implementation of <c>float</c>.</param>
  /// <param name="double">The implementation of <c>double</c>.</param>
  /// <returns>The operation.</returns>
  public static FloatOperation WithInteger(IrIntrinsicOp op, string family, FloatContract contract, string name, Func<float, int, float> single, Func<double, int, double> @double)
    => Of(op, family, FloatShape.WithInteger, contract, name,
      arguments => single((float)arguments[0], (int)(long)arguments[1]), arguments => @double((double)arguments[0], (int)(long)arguments[1]));

  /// <summary>An operation of the shape <see cref="FloatShape.ToInteger"/>.</summary>
  /// <param name="op">The operation.</param>
  /// <param name="family">The family.</param>
  /// <param name="name">The name of the implementation of <c>double</c>.</param>
  /// <param name="single">The implementation of <c>float</c>.</param>
  /// <param name="double">The implementation of <c>double</c>.</param>
  /// <returns>The operation, which is exact.</returns>
  public static FloatOperation ToInteger(IrIntrinsicOp op, string family, string name, Func<float, int> single, Func<double, int> @double)
    => Of(op, family, FloatShape.ToInteger, FloatContract.Exact, name,
      arguments => (long)single((float)arguments[0]), arguments => (long)@double((double)arguments[0]));

  /// <summary>An operation of the shape <see cref="FloatShape.Predicate"/>.</summary>
  /// <param name="op">The operation.</param>
  /// <param name="family">The family.</param>
  /// <param name="name">The name of the implementation of <c>double</c>.</param>
  /// <param name="single">The implementation of <c>float</c>.</param>
  /// <param name="double">The implementation of <c>double</c>.</param>
  /// <returns>The operation, which is exact.</returns>
  public static FloatOperation Predicate(IrIntrinsicOp op, string family, string name, Func<float, bool> single, Func<double, bool> @double)
    => Of(op, family, FloatShape.Predicate, FloatContract.Exact, name, arguments => single((float)arguments[0]), arguments => @double((double)arguments[0]));

  /// <summary>An operation the IR had before this table: the interpreter and the backends keep their own rules for it.</summary>
  /// <param name="op">The operation.</param>
  /// <param name="family">The family.</param>
  /// <param name="shape">The operand and result types.</param>
  /// <param name="contract">The contract.</param>
  /// <returns>The operation.</returns>
  public static FloatOperation Existing(IrIntrinsicOp op, string family, FloatShape shape, FloatContract contract)
    => new(op, family, shape, contract);

  /// <summary>The operation with its special values.</summary>
  /// <param name="groups">The special values, in groups as one value in both formats gives them.</param>
  /// <returns>A copy that carries them.</returns>
  public FloatOperation With(params FloatSpecialValue[][] groups)
    => new(this.Op, this.Family, this.Shape, this.Contract) {
      DoubleName = this.DoubleName, SingleName = this.SingleName, Single = this.Single, Double = this.Double, Traps = this.Traps,
      SpecialValues = [.. this.SpecialValues, .. groups.SelectMany(group => group)]
    };

  /// <summary>Whether the operation applies to arguments of the given types with the given result type.</summary>
  /// <param name="result">The type of the node.</param>
  /// <param name="arguments">The types of the arguments.</param>
  /// <returns><see langword="true"/> when the types fit the shape.</returns>
  public bool Accepts(IrType result, IReadOnlyList<IrType> arguments) {
    if (this.Shape == FloatShape.FromBits)
      return arguments.Count == 1 && (arguments[0] == IrType.I64 ? result == IrType.F64 : arguments[0] == IrType.I32 && result == IrType.F32);

    if (arguments.Count == 0 || !arguments[0].IsFloat)
      return false;

    var format = arguments[0];
    bool Are(params IrType[] types) {
      if (types.Length != arguments.Count)
        return false;

      for (var index = 0; index < types.Length; ++index)
        if (arguments[index] != types[index])
          return false;

      return true;
    }

    return this.Shape switch {
      FloatShape.Unary => result == format && Are(format),
      FloatShape.Binary => result == format && Are(format, format),
      FloatShape.Ternary => result == format && Are(format, format, format),
      FloatShape.WithInteger => result == format && Are(format, IrType.I32),
      FloatShape.ToInteger => result == IrType.I32 && Are(format),
      FloatShape.Predicate => result == IrType.Bool && Are(format),
      FloatShape.ToBits => result == (format == IrType.F64 ? IrType.I64 : IrType.I32) && Are(format),
      _ => result == format && Are(format, IrType.I32, IrType.I32)
    };
  }

  /// <summary>The operation as the table names it.</summary>
  public override string ToString() => this.Op.ToString();

}
