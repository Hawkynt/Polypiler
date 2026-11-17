using System;
using System.Collections.Generic;
using System.Numerics;

namespace Polypiler.Core.IR;

/// <summary>
/// The meaning of <see cref="IrIntegerOp"/> (#118): its type rules, which the verifier reports, and
/// its evaluation, which the interpreter calls. Values are carried as <see cref="IrConst"/> carries
/// them: a number within the range of its type, a <c>u64</c> as its 64 bits. The normative semantics
/// are those of the SMT-LIB 2.6 theory <c>FixedSizeBitVectors</c> (<c>bvadd</c>, <c>bvudiv</c>,
/// <c>bvsdiv</c>, <c>bvsrem</c>, <c>bvshl</c>, <c>bvlshr</c>, <c>bvashr</c>) where the contract leaves the
/// result defined, and the contract where it does not.
/// </summary>
public static class IrIntegerContracts {

  /// <summary><c>PLY0126</c>: a constant amount of a <c>[strict]</c> shift that is not below the width.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor StrictAmount = new("PLY0126", DiagnosticSeverity.Error, ExitCode.InternalError,
    "shift amount {0} is not below the width {1} of a [strict] shift", "ir.integer.shift-contract");

  /// <summary><c>PLY0127</c>: an integer operation without the contract its opcode requires.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MissingContract = new("PLY0127", DiagnosticSeverity.Error, ExitCode.InternalError,
    "'{0}' requires {1}", "ir.integer.contract");

  /// <summary>
  /// What is wrong with a node, apart from its operands, which the verifier checks as expressions of
  /// their own: the number of operands, their types, the contract the opcode requires.
  /// </summary>
  /// <param name="node">The node.</param>
  /// <returns>Each problem as the descriptor it is reported under and its message; empty for a well-formed node.</returns>
  public static IReadOnlyList<(DiagnosticDescriptor Descriptor, string Message)> Problems(IrIntegerOp node) {
    ArgumentNullException.ThrowIfNull(node);
    var problems = new List<(DiagnosticDescriptor, string)>();
    void Ill(string message) => problems.Add((IrVerifier.Descriptor, message));
    void Missing(string what) => problems.Add((MissingContract, $"'{node.Op}.{node.Type}' requires {what}"));

    var arity = node.Op == IrIntegerOpcode.Neg ? 1 : 2;
    if (node.Operands.Count != arity)
      Ill($"{node.Op} takes {arity} operands, not {node.Operands.Count}");

    if (node.Type == IrType.Bool)
      problems.Add((IrBooleans.BoolOperand, $"'{node.Op}' does not accept bool operands; convert with BoolToInt"));
    else if (!IsInteger(node.Type))
      Ill($"{node.Op} is applied to the type {node.Type}, which is no integer type");

    foreach (var operand in node.Operands)
      if (operand.Type != node.Type)
        Ill($"an operand of {node.Op}.{node.Type} has the type {operand.Type}");

    var contract = node.Contract;
    switch (Family(node.Op)) {
      case Kind.Arithmetic:
        if (contract.Overflow is null)
          Missing("an overflow contract: [wrap], [trap <label>] or [sat]");
        Extra(contract.Divisor is not null, "zero-divisor contract");
        Extra(contract.Shift is not null, "shift-amount contract");
        break;
      case Kind.Division:
        if (contract.Divisor is null)
          Missing("a zero-divisor contract: [trap <label>] or [divzero=zero]");
        if (Signed(node.Op) && contract.Overflow is null)
          Missing("an ovf contract: ovf=wrap or ovf=trap <label>");
        if (Signed(node.Op) && contract.Overflow is { Kind: IrOverflowKind.Saturate })
          Missing("an ovf contract of ovf=wrap or ovf=trap <label>, not ovf=sat");
        Extra(!Signed(node.Op) && contract.Overflow is not null, "ovf contract");
        Extra(contract.Shift is not null, "shift-amount contract");
        break;
      default:
        if (contract.Shift is null)
          Missing("a shift-amount contract: [mask], [unbounded], [trap <label>] or [strict]");
        Extra(contract.Overflow is not null, "overflow contract");
        Extra(contract.Divisor is not null, "zero-divisor contract");
        if (contract.Shift is { Kind: IrShiftKind.Strict } && node.Operands is [_, IrConst amount] && IsInteger(node.Type)
          && Amount(node.Type, amount.Value) is var count && count >= (ulong)node.Type.Bits)
          problems.Add((StrictAmount, $"shift amount {count} is not below the width {node.Type.Bits} of a [strict] shift"));
        break;
    }

    return problems;

    void Extra(bool present, string what) {
      if (present)
        Ill($"{node.Op} takes no {what}");
    }
  }

  /// <summary>Whether a type is one an <see cref="IrIntegerOp"/> computes on: an integer that is no enum.</summary>
  /// <param name="type">The type.</param>
  public static bool IsInteger(IrType type) => type.IsInteger && !type.IsEnum;

  /// <summary>Evaluates a node on the values of its operands, as the interpreter carries them.</summary>
  /// <param name="node">A node the verifier accepts.</param>
  /// <param name="operands">The values of its operands, in order.</param>
  /// <returns>The value of the result.</returns>
  /// <exception cref="IrTrapException">The contract raises a trap for the operands.</exception>
  /// <exception cref="IrUndefinedBehaviourException">An <c>[undefined]</c> contract meets operands outside it (#122).</exception>
  public static long Evaluate(IrIntegerOp node, IReadOnlyList<long> operands) {
    ArgumentNullException.ThrowIfNull(node);
    ArgumentNullException.ThrowIfNull(operands);
    var type = node.Type;
    var contract = node.Contract;
    var left = operands[0];
    switch (node.Op) {
      case IrIntegerOpcode.Neg:
        return Arithmetic(node, contract.Overflow!, -Number(type, left), -left);
      case IrIntegerOpcode.Add:
        return Arithmetic(node, contract.Overflow!, Number(type, left) + Number(type, operands[1]), left + operands[1]);
      case IrIntegerOpcode.Sub:
        return Arithmetic(node, contract.Overflow!, Number(type, left) - Number(type, operands[1]), left - operands[1]);
      case IrIntegerOpcode.Mul:
        return Arithmetic(node, contract.Overflow!, (BigInteger)Number(type, left) * (BigInteger)Number(type, operands[1]), unchecked(left * operands[1]));
      case IrIntegerOpcode.UDiv or IrIntegerOpcode.URem:
        return Unsigned(node, Pattern(type, left), Pattern(type, operands[1]));
      case IrIntegerOpcode.SDiv or IrIntegerOpcode.SRem:
        return SignedDivision(node, SignedOf(type, left), SignedOf(type, operands[1]));
      default:
        return Shift(node, left, Amount(type, operands[1]));
    }
  }

  /// <summary>The families of opcodes, which take the same contracts.</summary>
  private enum Kind { Arithmetic, Division, Shift }

  private static Kind Family(IrIntegerOpcode op) => op switch {
    IrIntegerOpcode.Add or IrIntegerOpcode.Sub or IrIntegerOpcode.Mul or IrIntegerOpcode.Neg => Kind.Arithmetic,
    IrIntegerOpcode.UDiv or IrIntegerOpcode.SDiv or IrIntegerOpcode.URem or IrIntegerOpcode.SRem => Kind.Division,
    _ => Kind.Shift
  };

  private static bool Signed(IrIntegerOpcode op) => op is IrIntegerOpcode.SDiv or IrIntegerOpcode.SRem;

  /// <summary>The result of an arithmetic operation under its overflow contract: the exact result, the wrapped one where it does not fit.</summary>
  private static long Arithmetic(IrIntegerOp node, IrOverflowContract contract, BigInteger exact, long wrapped) {
    var type = node.Type;
    var (minimum, maximum) = ((BigInteger)IrOperators.Minimum(type), (BigInteger)IrOperators.Maximum(type));
    if (exact >= minimum && exact <= maximum)
      return IrOperators.Wrap(type, wrapped);

    return contract.Kind switch {
      IrOverflowKind.Wrap => IrOperators.Wrap(type, wrapped),
      IrOverflowKind.Saturate => Carried(type, exact < minimum ? minimum : maximum),
      IrOverflowKind.Undefined => throw new IrUndefinedBehaviourException(node),
      _ => throw new IrTrapException(contract.Label)
    };
  }

  private static long Unsigned(IrIntegerOp node, ulong dividend, ulong divisor) {
    if (divisor == 0)
      return Zero(node);

    return Value(node.Type, node.Op == IrIntegerOpcode.UDiv ? dividend / divisor : dividend % divisor);
  }

  private static long SignedDivision(IrIntegerOp node, long dividend, long divisor) {
    if (divisor == 0)
      return Zero(node);

    var smallest = node.Type.Bits == 64 ? long.MinValue : -(1L << (node.Type.Bits - 1));
    if (dividend == smallest && divisor == -1) {
      var ovf = node.Contract.Overflow!;
      if (ovf.Kind == IrOverflowKind.Trap)
        throw new IrTrapException(ovf.Label);

      if (ovf.Kind == IrOverflowKind.Undefined)
        throw new IrUndefinedBehaviourException(node);

      // ovf=wrap: the quotient is the smallest value again, the remainder 0.
      return node.Op == IrIntegerOpcode.SDiv ? IrOperators.Wrap(node.Type, smallest) : 0;
    }

    return IrOperators.Wrap(node.Type, node.Op == IrIntegerOpcode.SDiv ? dividend / divisor : dividend % divisor);
  }

  /// <summary>A zero divisor under the zero-divisor contract: the trap, 0, or undefined behaviour.</summary>
  private static long Zero(IrIntegerOp node) {
    var divisor = node.Contract.Divisor!;
    return divisor.Kind switch {
      IrDivisorKind.Zero => 0,
      IrDivisorKind.Undefined => throw new IrUndefinedBehaviourException(node),
      _ => throw new IrTrapException(divisor.Label)
    };
  }

  private static long Shift(IrIntegerOp node, long value, ulong amount) {
    var type = node.Type;
    var width = (ulong)type.Bits;
    if (amount >= width) {
      var contract = node.Contract.Shift!;
      switch (contract.Kind) {
        case IrShiftKind.Mask:
          amount %= width;
          break;
        case IrShiftKind.Unbounded:
          return node.Op == IrIntegerOpcode.AShr && SignedOf(type, value) < 0 ? IrOperators.Wrap(type, -1) : 0;
        case IrShiftKind.Trap:
          throw new IrTrapException(contract.Label);
        case IrShiftKind.Undefined:
          throw new IrUndefinedBehaviourException(node);
        default:
          throw new IrTrapException(IrTrap.InvalidOperation);
      }
    }

    var count = (int)amount;
    return node.Op switch {
      IrIntegerOpcode.Shl => Value(type, Pattern(type, value) << count),
      IrIntegerOpcode.LShr => Value(type, Pattern(type, value) >> count),
      _ => IrOperators.Wrap(type, SignedOf(type, value) >> count)
    };
  }

  /// <summary>A number of the range of a type as the value is carried: a <c>u64</c> as its 64 bits.</summary>
  private static long Carried(IrType type, BigInteger number) => type.Kind == IrTypeKind.U64 ? unchecked((long)(ulong)number) : (long)number;

  private static BigInteger Number(IrType type, long value) => (BigInteger)IrOperators.Number(type, value);

  /// <summary>The bits of a carried value: its two's complement, cut to the width of its type.</summary>
  private static ulong Pattern(IrType type, long value) => type.Bits == 64 ? (ulong)value : (ulong)value & ((1UL << type.Bits) - 1);

  /// <summary>The value of a type that has the given bits, as it is carried.</summary>
  private static long Value(IrType type, ulong pattern) => IrOperators.Wrap(type, (long)pattern);

  /// <summary>The bits of a carried value read as a two's complement number of the width of its type.</summary>
  private static long SignedOf(IrType type, long value) {
    var shift = 64 - type.Bits;
    return (long)(Pattern(type, value) << shift) >> shift;
  }

  /// <summary>A shift amount: the bits of the value read as an unsigned number, so that -1 is 2^width - 1.</summary>
  private static ulong Amount(IrType type, long value) => Pattern(type, value);

}
