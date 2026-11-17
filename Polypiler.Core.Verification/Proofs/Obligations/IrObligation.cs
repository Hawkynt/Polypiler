using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Verification.Proofs.Obligations;

/// <summary>What evaluating a fragment for one input gave: its bits, or why it has none.</summary>
/// <param name="Value">The bit pattern of the value; 0 when the fragment is invalid.</param>
/// <param name="Fault">Why the fragment is invalid for the input, or <see langword="null"/> when it is valid.</param>
internal readonly record struct ObligationValue(ulong Value, string? Fault) {

  /// <summary>Whether the fragment has a value for the input.</summary>
  public bool IsValid => this.Fault is null;

}

/// <summary>
/// An obligation as a module of the IR, which the reference interpreter evaluates: one function per
/// precondition and per side, each taking every input. A value of an obligation of <c>n</c> bits is
/// carried as the low <c>n</c> bits of a <c>u64</c>, a truth value as a <c>bool</c>; an operation
/// whose result depends on the sign reads its operands sign-extended to an <c>i64</c>. Where a
/// fragment is invalid - a strict shift by its width or more, a division by zero, a signed division
/// that traps on overflow - the function throws, and the throw is what makes the input invalid.
/// </summary>
internal sealed class IrObligation {

  /// <summary>The function of the before side.</summary>
  public const string BeforeFunction = "before";

  /// <summary>The function of the after side.</summary>
  public const string AfterFunction = "after";

  private static readonly IrType _carrier = IrType.U64;
  private static readonly IrType _signed = IrType.I64;

  private readonly IrModule _module;
  private readonly IReadOnlyList<ObligationVariable> _inputs;

  private IrObligation(IrModule module, IReadOnlyList<ObligationVariable> inputs, int preconditions) {
    this._module = module;
    this._inputs = inputs;
    this.PreconditionCount = preconditions;
  }

  /// <summary>How many preconditions the obligation has.</summary>
  public int PreconditionCount { get; }

  /// <summary>The module, for a reader who wants to see what is evaluated.</summary>
  public IrModule Module => this._module;

  /// <summary>The name of the function of a precondition.</summary>
  /// <param name="index">The 0-based index of the precondition.</param>
  /// <returns>The name.</returns>
  public static string PreconditionFunction(int index) => $"pre{index}";

  /// <summary>Builds the module of a program and checks it with the IR's verifier.</summary>
  /// <param name="program">The obligation at one width.</param>
  /// <returns>The module, ready for evaluation.</returns>
  /// <exception cref="ObligationUnsupportedException">A rotation of a width the IR has no integer type for.</exception>
  /// <exception cref="InvalidOperationException">The verifier rejects the module: an error of this lowering.</exception>
  public static IrObligation Lower(ObligationProgram program) {
    var functions = new List<IrFunction>();
    for (var index = 0; index < program.Preconditions.Count; ++index)
      functions.Add(new Lowering(program.Inputs).Function(PreconditionFunction(index), program.Preconditions[index]));

    functions.Add(new Lowering(program.Inputs).Function(BeforeFunction, program.Before));
    functions.Add(new Lowering(program.Inputs).Function(AfterFunction, program.After));
    var module = new IrModule("obligation", functions);
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      throw new InvalidOperationException($"The obligation lowers to IR the verifier rejects: {string.Join("; ", problems.Select(problem => problem.Message))}");

    return new(module, program.Inputs, program.Preconditions.Count);
  }

  /// <summary>The arguments of a call for input bit patterns, in the order of the inputs.</summary>
  /// <param name="values">The bit pattern of each input.</param>
  /// <returns>The arguments.</returns>
  public object[] Arguments(IReadOnlyList<ulong> values) {
    var arguments = new object[values.Count];
    for (var index = 0; index < arguments.Length; ++index)
      arguments[index] = this._inputs[index].Type == ObligationType.Bool ? values[index] != 0 : values[index];

    return arguments;
  }

  /// <summary>Evaluates one function of the module for arguments made by <see cref="Arguments"/>.</summary>
  /// <param name="function">The function.</param>
  /// <param name="arguments">The arguments.</param>
  /// <returns>The value, or the fault that makes the input invalid.</returns>
  public ObligationValue Evaluate(string function, object[] arguments) {
    try {
      return IrInterpreter.Call(this._module, function, arguments) switch {
        bool truth => new(truth ? 1UL : 0UL, null),
        var value => new((ulong)value!, null)
      };
    } catch (IrException exception) {
      return new(0, exception.Message);
    }
  }

  /// <summary>The statements of one function, built instruction by instruction.</summary>
  private sealed class Lowering {

    private readonly Dictionary<string, IrLocal> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ObligationType> _types = new(StringComparer.Ordinal);
    private readonly List<IrLocal> _parameters = [];
    private readonly List<IrLocal> _locals = [];
    private readonly List<IrStmt> _body = [];

    public Lowering(IReadOnlyList<ObligationVariable> inputs) {
      foreach (var input in inputs) {
        var local = new IrLocal(input.Name, Carrier(input.Type));
        this._parameters.Add(local);
        this._values[input.Name] = local;
        this._types[input.Name] = input.Type;
      }
    }

    public IrFunction Function(string name, ObligationFragment fragment) {
      foreach (var instruction in fragment.Instructions) {
        var value = this.Instruction(instruction);
        var local = new IrLocal("v_" + instruction.Target, Carrier(instruction.Result));
        this._locals.Add(local);
        this._body.Add(new IrDeclare(local, value));
        this._values[instruction.Target] = local;
        this._types[instruction.Target] = instruction.Result;
      }

      var result = this._values[fragment.Result];
      this._body.Add(new IrReturn(new IrLocalRef(result)));
      return new(name, true, result.Type, this._parameters, this._locals, new(this._body));
    }

    private static IrType Carrier(ObligationType type) => type == ObligationType.Bool ? IrType.Bool : _carrier;

    private static IrConst Unsigned(ulong value) => new(_carrier, unchecked((long)value));

    private static IrConst Word(int value) => new(IrType.I32, value);

    private IrExpr Operand(ObligationOperand operand) {
      if (operand is ObligationReference reference)
        return new IrLocalRef(this._values[reference.Name]);

      var bits = ((ObligationConstant)operand).Bits;
      return operand.Type == ObligationType.Bool ? new IrConst(IrType.Bool, (long)bits) : Unsigned(bits);
    }

    /// <summary>A carried value reduced to the bits of its type.</summary>
    private static IrExpr Masked(IrExpr value, ObligationType type)
      => type.Width >= 64 ? value : new IrBinary(IrBinaryOp.And, value, Unsigned(type.Mask));

    /// <summary>A carried value of a type, sign-extended to an <c>i64</c>.</summary>
    private static IrExpr Signed(IrExpr value, int width) {
      var converted = new IrConvert(_signed, value);
      if (width >= 64)
        return converted;

      var shift = Word(64 - width);
      return new IrBinary(IrBinaryOp.ShiftRight, new IrBinary(IrBinaryOp.ShiftLeft, converted, shift), shift);
    }

    /// <summary>An <c>i64</c> back as the carried bits of a type.</summary>
    private static IrExpr FromSigned(IrExpr value, ObligationType type) => Masked(new IrConvert(_carrier, value), type);

    /// <summary>A statement that makes the input invalid when a condition holds.</summary>
    private void FailWhen(IrExpr condition, string message) {
      var thrown = new IrNew(IrType.Object(IrExceptionClass.ArgumentOutOfRangeException), IrExceptionClass.Constructor(IrExceptionClass.ArgumentOutOfRangeException, 1), [new IrStrConst(message)]);
      this._body.Add(new IrIf(condition, new([new IrThrow(thrown)]), null));
    }

    private IrExpr Instruction(ObligationInstruction instruction) {
      var operands = instruction.Operands.Select(this.Operand).ToArray();
      var type = instruction.Type;
      var truth = type == ObligationType.Bool;
      IrExpr Binary(IrBinaryOp op) => new IrBinary(op, operands[0], operands[1]);
      IrExpr SignedOperand(int index) => Signed(operands[index], instruction.Operands[index].Type.Width);
      return instruction.Op switch {
        ObligationOp.Add => Masked(Binary(IrBinaryOp.Add), type),
        ObligationOp.Sub => Masked(Binary(IrBinaryOp.Subtract), type),
        ObligationOp.Mul => Masked(Binary(IrBinaryOp.Multiply), type),
        ObligationOp.Neg => Masked(new IrBinary(IrBinaryOp.Subtract, Unsigned(0), operands[0]), type),
        ObligationOp.And => Binary(truth ? IrBinaryOp.LogicalAnd : IrBinaryOp.And),
        ObligationOp.Or => Binary(truth ? IrBinaryOp.LogicalOr : IrBinaryOp.Or),
        ObligationOp.Xor => Binary(truth ? IrBinaryOp.NotEqual : IrBinaryOp.Xor),
        ObligationOp.Not => truth ? new IrUnary(IrUnaryOp.LogicalNot, operands[0]) : new IrBinary(IrBinaryOp.Xor, operands[0], Unsigned(type.Mask)),
        ObligationOp.Shl or ObligationOp.LShr or ObligationOp.AShr => this.Shift(instruction, operands),
        ObligationOp.RotateLeft or ObligationOp.RotateRight => Rotate(instruction, operands),
        ObligationOp.UDiv or ObligationOp.URem => this.UnsignedDivision(instruction, operands),
        ObligationOp.SDiv or ObligationOp.SRem => this.SignedDivision(instruction, operands),
        ObligationOp.Eq => Binary(IrBinaryOp.Equal),
        ObligationOp.Ne => Binary(IrBinaryOp.NotEqual),
        ObligationOp.ULt => Binary(IrBinaryOp.Less),
        ObligationOp.ULe => Binary(IrBinaryOp.LessOrEqual),
        ObligationOp.UGt => Binary(IrBinaryOp.Greater),
        ObligationOp.UGe => Binary(IrBinaryOp.GreaterOrEqual),
        ObligationOp.SLt => new IrBinary(IrBinaryOp.Less, SignedOperand(0), SignedOperand(1)),
        ObligationOp.SLe => new IrBinary(IrBinaryOp.LessOrEqual, SignedOperand(0), SignedOperand(1)),
        ObligationOp.SGt => new IrBinary(IrBinaryOp.Greater, SignedOperand(0), SignedOperand(1)),
        ObligationOp.SGe => new IrBinary(IrBinaryOp.GreaterOrEqual, SignedOperand(0), SignedOperand(1)),
        ObligationOp.Select => new IrConditional(operands[0], operands[1], operands[2]),
        ObligationOp.ZeroExtend => operands[0],
        ObligationOp.SignExtend => FromSigned(SignedOperand(0), type),
        _ => Masked(operands[0], type)
      };
    }

    /// <summary>
    /// A shift by its amount contract: strict throws at an amount of the width or more, mask takes the
    /// amount modulo the width, unbounded shifts every bit out - the sign bit in, for an arithmetic one.
    /// </summary>
    private IrExpr Shift(ObligationInstruction instruction, IrExpr[] operands) {
      var type = instruction.Type;
      var width = Unsigned((ulong)type.Width);
      var amount = operands[1];
      if (instruction.Shift == ShiftContract.Strict)
        this.FailWhen(new IrBinary(IrBinaryOp.GreaterOrEqual, amount, width), $"{instruction.Text}: shift amount of at least {type.Width}");
      else if (instruction.Shift == ShiftContract.Mask)
        amount = new IrBinary(IrBinaryOp.Remainder, amount, width);

      // An amount below the width - all that remains to be shifted by - fits an i32.
      var count = new IrConvert(IrType.I32, new IrBinary(IrBinaryOp.Remainder, amount, Unsigned(64)));
      var shifted = instruction.Op switch {
        ObligationOp.Shl => Masked(new IrBinary(IrBinaryOp.ShiftLeft, operands[0], count), type),
        ObligationOp.LShr => new IrBinary(IrBinaryOp.ShiftRightLogical, operands[0], count),
        _ => FromSigned(new IrBinary(IrBinaryOp.ShiftRight, Signed(operands[0], type.Width), count), type)
      };
      if (instruction.Shift != ShiftContract.Unbounded)
        return shifted;

      IrExpr outside = instruction.Op == ObligationOp.AShr
        ? FromSigned(new IrBinary(IrBinaryOp.ShiftRight, Signed(operands[0], type.Width), Word(63)), type)
        : Unsigned(0);
      return new IrConditional(new IrBinary(IrBinaryOp.GreaterOrEqual, amount, width), outside, shifted);
    }

    /// <summary>A rotation by the IR's own intrinsic, on the integer type of the width; the count is taken modulo the width.</summary>
    private static IrExpr Rotate(ObligationInstruction instruction, IrExpr[] operands) {
      var type = instruction.Type;
      var native = type.Width switch {
        8 => IrType.U8,
        16 => IrType.U16,
        32 => IrType.U32,
        64 => IrType.U64,
        _ => throw new ObligationUnsupportedException(instruction.Text, $"the IR has no {type.Width}-bit integer to rotate")
      };
      var count = new IrConvert(IrType.I32, new IrBinary(IrBinaryOp.Remainder, operands[1], Unsigned((ulong)type.Width)));
      var op = instruction.Op == ObligationOp.RotateLeft ? IrIntrinsicOp.RotateLeft : IrIntrinsicOp.RotateRight;
      return new IrConvert(_carrier, new IrIntrinsic(op, native, [new IrConvert(native, operands[0]), count]));
    }

    /// <summary>An unsigned quotient or remainder; the IR traps on a zero divisor.</summary>
    private IrExpr UnsignedDivision(ObligationInstruction instruction, IrExpr[] operands)
      => new IrBinary(instruction.Op == ObligationOp.UDiv ? IrBinaryOp.Divide : IrBinaryOp.Remainder, operands[0], operands[1]);

    /// <summary>
    /// A signed quotient or remainder truncated towards zero; the IR traps on a zero divisor. The
    /// smallest value divided by -1 wraps to itself (its remainder is 0), or makes the input invalid
    /// where the instruction says <c>ovf=trap</c>.
    /// </summary>
    private IrExpr SignedDivision(ObligationInstruction instruction, IrExpr[] operands) {
      var type = instruction.Type;
      var dividend = Signed(operands[0], type.Width);
      var divisor = Signed(operands[1], type.Width);
      var minusOne = new IrBinary(IrBinaryOp.Equal, divisor, new IrConst(_signed, -1));
      if (instruction.OverflowTraps) {
        var smallest = new IrBinary(IrBinaryOp.Equal, operands[0], Unsigned(1UL << (type.Width - 1)));
        this.FailWhen(new IrBinary(IrBinaryOp.LogicalAnd, smallest, minusOne), $"{instruction.Text}: the smallest value divided by -1");
      }

      // Dividing by -1 is a negation, which wraps; the IR's division would trap on the smallest value.
      return instruction.Op == ObligationOp.SDiv
        ? FromSigned(new IrConditional(minusOne, new IrBinary(IrBinaryOp.Subtract, new IrConst(_signed, 0), dividend), new IrBinary(IrBinaryOp.Divide, dividend, divisor)), type)
        : FromSigned(new IrConditional(minusOne, new IrConst(_signed, 0), new IrBinary(IrBinaryOp.Remainder, dividend, divisor)), type);
    }

  }

}
