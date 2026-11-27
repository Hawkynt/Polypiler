using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using Polypiler.Core.IR;

namespace Polypiler.Permutation.Networks;

/// <summary>
/// The meaning of one operation of a network: how a step of it evaluates, what makes a step of it
/// well-formed, how it lowers to the IR and which primitive of a cost table it costs. A primitive issue
/// registers the semantics of the operations it adds in <see cref="NetworkOpRegistry"/>.
/// </summary>
public interface INetworkOpSemantics {

  /// <summary>The operation.</summary>
  NetworkOp Op { get; }

  /// <summary>The primitive of a cost table a step costs, such as <c>and</c> or <c>shift</c>; <see langword="null"/> for a step that costs nothing.</summary>
  string? Primitive { get; }

  /// <summary>The value of a step, reduced modulo 2^width.</summary>
  /// <param name="step">The step.</param>
  /// <param name="operands">The values of its operands, each reduced to its own width.</param>
  /// <returns>The value.</returns>
  BigInteger Evaluate(NetworkStep step, IReadOnlyList<BigInteger> operands);

  /// <summary>What is wrong with a step whose operands are defined: operand count and widths (N2), the immediate (N3).</summary>
  /// <param name="step">The step.</param>
  /// <param name="network">The network, for the widths of the operands.</param>
  /// <returns>The problems, each a sentence fragment such as <c>amount 8 is outside 1..7</c>; none for a well-formed step.</returns>
  IEnumerable<string> Check(NetworkStep step, BitOperationNetwork network);

  /// <summary>
  /// The step as an expression of the tree IR over the expressions of its operands. Every value is held at
  /// its carrier (<see cref="NetworkLowering.CarrierOf"/>) with the bits above its width zero.
  /// </summary>
  /// <param name="step">The step.</param>
  /// <param name="operands">The expressions of its operands.</param>
  /// <returns>The expression, at the carrier of the step's width.</returns>
  IrExpr Lower(NetworkStep step, IReadOnlyList<IrExpr> operands);

}

/// <summary>The semantics of the operations of networks; <see cref="Default"/> has the core operations and the logical shifts.</summary>
public sealed class NetworkOpRegistry {

  private readonly Dictionary<NetworkOp, INetworkOpSemantics> _semantics = [];

  /// <summary>A registry with the core operations and the logical shifts.</summary>
  public static NetworkOpRegistry Default { get; } = Core();

  /// <summary>A new registry with the core operations and the logical shifts, for a caller that registers more.</summary>
  /// <returns>The registry.</returns>
  public static NetworkOpRegistry Core() => new NetworkOpRegistry()
    .Register(new ConstSemantics())
    .Register(new BitwiseSemantics(NetworkOp.And)).Register(new BitwiseSemantics(NetworkOp.Or)).Register(new BitwiseSemantics(NetworkOp.Xor))
    .Register(new NotSemantics())
    .Register(new WidthSemantics(NetworkOp.ZeroExtend)).Register(new WidthSemantics(NetworkOp.Truncate))
    .Register(new ShiftSemantics(NetworkOp.ShiftLeft)).Register(new ShiftSemantics(NetworkOp.ShiftRightLogical));

  /// <summary>Registers the semantics of an operation.</summary>
  /// <param name="semantics">The semantics.</param>
  /// <returns>The registry, for chaining.</returns>
  /// <exception cref="InvalidOperationException">The operation has semantics already.</exception>
  public NetworkOpRegistry Register(INetworkOpSemantics semantics) {
    ArgumentNullException.ThrowIfNull(semantics);
    if (!this._semantics.TryAdd(semantics.Op, semantics))
      throw new InvalidOperationException($"The operation {semantics.Op} has semantics already.");

    return this;
  }

  /// <summary>The semantics of an operation, if it has any.</summary>
  /// <param name="op">The operation.</param>
  /// <param name="semantics">The semantics.</param>
  /// <returns><see langword="true"/> when the operation is registered.</returns>
  public bool TryGet(NetworkOp op, [NotNullWhen(true)] out INetworkOpSemantics? semantics) => this._semantics.TryGetValue(op, out semantics);

}

/// <summary>The checks and helpers the core semantics share.</summary>
internal static class Steps {

  public static BigInteger Mask(int width) => (BigInteger.One << width) - 1;

  public static string Operands(NetworkStep step, int count)
    => step.Operands.Count == count ? "" : $"takes {count} operand{(count == 1 ? "" : "s")}, not {step.Operands.Count}";

  public static IEnumerable<string> SameWidth(NetworkStep step, BitOperationNetwork network) {
    foreach (var operand in step.Operands)
      if (network.WidthOf(operand) != step.Width)
        yield return $"operand v{operand.ToString(CultureInfo.InvariantCulture)} has {network.WidthOf(operand).ToString(CultureInfo.InvariantCulture)} bits, not {step.Width.ToString(CultureInfo.InvariantCulture)}";
  }

  public static string? Fits(BigInteger bits, int width)
    => bits.Sign < 0 || bits > Mask(width) ? $"constant {NetworkText.Hex(bits)} does not fit {width.ToString(CultureInfo.InvariantCulture)} bits" : null;

  public static string Kind(NetworkStep step, string wanted) => $"needs {wanted}, not {(step.Immediate is null ? "none" : step.Immediate.GetType().Name.ToLowerInvariant())}";

}

/// <summary><see cref="NetworkOp.Const"/>: a bit pattern of the step's width.</summary>
internal sealed class ConstSemantics : INetworkOpSemantics {
  public NetworkOp Op => NetworkOp.Const;
  public string? Primitive => null;
  public BigInteger Evaluate(NetworkStep step, IReadOnlyList<BigInteger> operands) => ((NetworkImmediate.Bits)step.Immediate!).Value & Steps.Mask(step.Width);

  public IEnumerable<string> Check(NetworkStep step, BitOperationNetwork network) {
    if (Steps.Operands(step, 0) is { Length: > 0 } count)
      yield return count;
    if (step.Immediate is not NetworkImmediate.Bits bits)
      yield return Steps.Kind(step, "a bit pattern");
    else if (Steps.Fits(bits.Value, step.Width) is { } problem)
      yield return problem;
  }

  public IrExpr Lower(NetworkStep step, IReadOnlyList<IrExpr> operands)
    => NetworkLowering.Constant(NetworkLowering.CarrierOf(step.Width), ((NetworkImmediate.Bits)step.Immediate!).Value);
}

/// <summary><see cref="NetworkOp.And"/>, <see cref="NetworkOp.Or"/>, <see cref="NetworkOp.Xor"/>: of two values, or of a value and a constant.</summary>
internal sealed class BitwiseSemantics(NetworkOp op) : INetworkOpSemantics {
  public NetworkOp Op => op;
  public string? Primitive => op.ToString().ToLowerInvariant();

  public BigInteger Evaluate(NetworkStep step, IReadOnlyList<BigInteger> operands) {
    var (left, right) = (operands[0], operands.Count > 1 ? operands[1] : ((NetworkImmediate.Bits)step.Immediate!).Value);
    return (op switch { NetworkOp.And => left & right, NetworkOp.Or => left | right, _ => left ^ right }) & Steps.Mask(step.Width);
  }

  public IEnumerable<string> Check(NetworkStep step, BitOperationNetwork network) {
    switch (step.Operands.Count, step.Immediate) {
      case (2, null):
        break;
      case (1, NetworkImmediate.Bits bits):
        if (Steps.Fits(bits.Value, step.Width) is { } problem)
          yield return problem;
        break;
      case (1, _):
        yield return Steps.Kind(step, "a second operand or a bit pattern");
        break;
      default:
        yield return $"takes two operands, or one and a bit pattern, not {step.Operands.Count.ToString(CultureInfo.InvariantCulture)}";
        yield break;
    }

    foreach (var problem in Steps.SameWidth(step, network))
      yield return problem;
  }

  public IrExpr Lower(NetworkStep step, IReadOnlyList<IrExpr> operands) {
    var carrier = NetworkLowering.CarrierOf(step.Width);
    var right = operands.Count > 1 ? operands[1] : NetworkLowering.Constant(carrier, ((NetworkImmediate.Bits)step.Immediate!).Value);
    var binary = op switch { NetworkOp.And => IrBinaryOp.And, NetworkOp.Or => IrBinaryOp.Or, _ => IrBinaryOp.Xor };
    // Two values with zeros above their width combine into one with zeros there too.
    return new IrBinary(binary, operands[0], right);
  }
}

/// <summary><see cref="NetworkOp.Not"/>: every bit of a value inverted.</summary>
internal sealed class NotSemantics : INetworkOpSemantics {
  public NetworkOp Op => NetworkOp.Not;
  public string? Primitive => "not";
  public BigInteger Evaluate(NetworkStep step, IReadOnlyList<BigInteger> operands) => operands[0] ^ Steps.Mask(step.Width);

  public IEnumerable<string> Check(NetworkStep step, BitOperationNetwork network) {
    if (Steps.Operands(step, 1) is { Length: > 0 } count) {
      yield return count;
      yield break;
    }

    if (step.Immediate is not null)
      yield return Steps.Kind(step, "no immediate");
    foreach (var problem in Steps.SameWidth(step, network))
      yield return problem;
  }

  public IrExpr Lower(NetworkStep step, IReadOnlyList<IrExpr> operands)
    => NetworkLowering.Masked(new IrUnary(IrUnaryOp.BitNot, operands[0]), step.Width);
}

/// <summary><see cref="NetworkOp.ZeroExtend"/> and <see cref="NetworkOp.Truncate"/>: a value at another width.</summary>
internal sealed class WidthSemantics(NetworkOp op) : INetworkOpSemantics {
  public NetworkOp Op => op;
  public string? Primitive => null;
  public BigInteger Evaluate(NetworkStep step, IReadOnlyList<BigInteger> operands) => operands[0] & Steps.Mask(step.Width);

  public IEnumerable<string> Check(NetworkStep step, BitOperationNetwork network) {
    if (Steps.Operands(step, 1) is { Length: > 0 } count) {
      yield return count;
      yield break;
    }

    if (step.Immediate is not null)
      yield return Steps.Kind(step, "no immediate");
    var from = network.WidthOf(step.Operands[0]);
    if (op == NetworkOp.ZeroExtend ? step.Width <= from : step.Width >= from)
      yield return $"{(op == NetworkOp.ZeroExtend ? "widens" : "narrows")} nothing: {from.ToString(CultureInfo.InvariantCulture)} to {step.Width.ToString(CultureInfo.InvariantCulture)} bits";
  }

  public IrExpr Lower(NetworkStep step, IReadOnlyList<IrExpr> operands) {
    var carrier = NetworkLowering.CarrierOf(step.Width);
    var value = operands[0].Type == carrier ? operands[0] : new IrConvert(carrier, operands[0]);
    return op == NetworkOp.ZeroExtend ? value : NetworkLowering.Masked(value, step.Width);
  }
}

/// <summary><see cref="NetworkOp.ShiftLeft"/> and <see cref="NetworkOp.ShiftRightLogical"/> by a constant amount in 1..width-1.</summary>
internal sealed class ShiftSemantics(NetworkOp op) : INetworkOpSemantics {
  public NetworkOp Op => op;
  public string? Primitive => "shift";

  public BigInteger Evaluate(NetworkStep step, IReadOnlyList<BigInteger> operands) {
    var amount = ((NetworkImmediate.Amount)step.Immediate!).Value;
    return (op == NetworkOp.ShiftLeft ? operands[0] << amount : operands[0] >> amount) & Steps.Mask(step.Width);
  }

  public IEnumerable<string> Check(NetworkStep step, BitOperationNetwork network) {
    if (Steps.Operands(step, 1) is { Length: > 0 } count) {
      yield return count;
      yield break;
    }

    if (step.Immediate is not NetworkImmediate.Amount amount)
      yield return Steps.Kind(step, "an amount");
    else if (amount.Value < 1 || amount.Value > step.Width - 1)
      yield return $"amount {amount.Value.ToString(CultureInfo.InvariantCulture)} is outside 1..{(step.Width - 1).ToString(CultureInfo.InvariantCulture)}";
    foreach (var problem in Steps.SameWidth(step, network))
      yield return problem;
  }

  public IrExpr Lower(NetworkStep step, IReadOnlyList<IrExpr> operands) {
    var amount = new IrConst(IrType.I32, ((NetworkImmediate.Amount)step.Immediate!).Value);
    // The carrier is unsigned, so its right shift is logical; a left shift may carry bits past the width.
    return op == NetworkOp.ShiftLeft
      ? NetworkLowering.Masked(new IrBinary(IrBinaryOp.ShiftLeft, operands[0], amount), step.Width)
      : new IrBinary(IrBinaryOp.ShiftRight, operands[0], amount);
  }
}
