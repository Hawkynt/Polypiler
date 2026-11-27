using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Polypiler.Permutation.Networks;

/// <summary>
/// The operations of a network. The core ones are defined here; a primitive issue adds the operations it
/// needs, with their semantics in <see cref="NetworkOpRegistry"/>. The two logical shifts are here as well,
/// as the smallest routing the composite assembly and its tests need.
/// </summary>
public enum NetworkOp {

  /// <summary>A constant: <see cref="NetworkImmediate.Bits"/>, no operand.</summary>
  Const,

  /// <summary>The bitwise and of two values, or of a value and a <see cref="NetworkImmediate.Bits"/> mask.</summary>
  And,

  /// <summary>The bitwise or of two values, or of a value and a <see cref="NetworkImmediate.Bits"/> constant.</summary>
  Or,

  /// <summary>The bitwise exclusive or of two values, or of a value and a <see cref="NetworkImmediate.Bits"/> constant.</summary>
  Xor,

  /// <summary>Every bit of a value inverted.</summary>
  Not,

  /// <summary>A value widened to the step's width with zeros above it.</summary>
  ZeroExtend,

  /// <summary>A value cut to the step's narrower width.</summary>
  Truncate,

  /// <summary>A value shifted towards its top by a constant <see cref="NetworkImmediate.Amount"/>, zeros shifted in.</summary>
  ShiftLeft,

  /// <summary>A value shifted towards its bottom by a constant <see cref="NetworkImmediate.Amount"/>, zeros shifted in.</summary>
  ShiftRightLogical

}

/// <summary>The constant part of a step.</summary>
public abstract record NetworkImmediate {

  private protected NetworkImmediate() { }

  /// <summary>A bit pattern, written in hexadecimal.</summary>
  /// <param name="Value">The bits, not negative.</param>
  public sealed record Bits(BigInteger Value) : NetworkImmediate {

    /// <inheritdoc/>
    public override string ToString() => NetworkText.Hex(this.Value);

  }

  /// <summary>An amount, such as a shift distance or a target width, written in decimal.</summary>
  /// <param name="Value">The amount.</param>
  public sealed record Amount(int Value) : NetworkImmediate {

    /// <inheritdoc/>
    public override string ToString() => this.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

  }

  /// <summary>A list of bytes, written <c>[b0,b1,…]</c>.</summary>
  /// <param name="Values">The bytes.</param>
  public sealed record Bytes(IReadOnlyList<byte> Values) : NetworkImmediate {

    /// <inheritdoc/>
    public bool Equals(Bytes? other) => other is not null && this.Values.SequenceEqual(other.Values);

    /// <inheritdoc/>
    public override int GetHashCode() => this.Values.Aggregate(17, (hash, value) => HashCode.Combine(hash, value));

    /// <inheritdoc/>
    public override string ToString() => "[" + string.Join(",", this.Values) + "]";

  }

  /// <summary>A lookup table of bit patterns.</summary>
  /// <param name="Values">The entries.</param>
  public sealed record Table(IReadOnlyList<BigInteger> Values) : NetworkImmediate {

    /// <inheritdoc/>
    public bool Equals(Table? other) => other is not null && this.Values.SequenceEqual(other.Values);

    /// <inheritdoc/>
    public override int GetHashCode() => this.Values.Aggregate(17, (hash, value) => HashCode.Combine(hash, value));

    /// <inheritdoc/>
    public override string ToString() => "{" + string.Join(",", this.Values.Select(NetworkText.Hex)) + "}";

  }

}

/// <summary>
/// One step of a network: an operation at a width over earlier values, with its constant part. Steps with
/// the same operation, width, operands and immediate are equal, which is how a builder shares them.
/// </summary>
/// <param name="Op">The operation.</param>
/// <param name="Width">The width of the value the step defines, in bits.</param>
/// <param name="Operands">The values it reads: 0 for the input, <c>k</c> for the value of step <c>k</c>.</param>
/// <param name="Immediate">Its constant part, if the operation has one.</param>
public sealed record NetworkStep(NetworkOp Op, int Width, IReadOnlyList<int> Operands, NetworkImmediate? Immediate) {

  /// <inheritdoc/>
  public bool Equals(NetworkStep? other) => other is not null && this.Op == other.Op && this.Width == other.Width
    && this.Operands.SequenceEqual(other.Operands) && Equals(this.Immediate, other.Immediate);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Op, this.Width, this.Operands.Aggregate(17, HashCode.Combine), this.Immediate);

}

/// <summary>
/// A candidate network: a straight-line program of bit operations from one input to one result. Value 0
/// is the input, of <see cref="InputWidth"/> bits; step <c>k</c> (1-based) defines value <c>k</c>; the
/// network computes value <see cref="Result"/>, of <see cref="OutputWidth"/> bits. A network is a value:
/// it never changes, and its evaluation is pure. Ill-formed networks can be built - the parser and the
/// tests need them - and <see cref="NetworkVerifier"/> says what is wrong with one.
/// </summary>
public sealed class BitOperationNetwork {

  /// <summary>Creates a network.</summary>
  /// <param name="inputWidth">The width of the input, value 0.</param>
  /// <param name="outputWidth">The width of the result.</param>
  /// <param name="steps">The steps, value 1 first.</param>
  /// <param name="result">The value the network computes.</param>
  /// <exception cref="ArgumentNullException"><paramref name="steps"/> is <see langword="null"/>.</exception>
  public BitOperationNetwork(int inputWidth, int outputWidth, IReadOnlyList<NetworkStep> steps, int result) {
    ArgumentNullException.ThrowIfNull(steps);
    this.InputWidth = inputWidth;
    this.OutputWidth = outputWidth;
    this.Steps = [.. steps];
    this.Result = result;
  }

  /// <summary>The width of the input.</summary>
  public int InputWidth { get; }

  /// <summary>The width of the result.</summary>
  public int OutputWidth { get; }

  /// <summary>The steps, value 1 first.</summary>
  public IReadOnlyList<NetworkStep> Steps { get; }

  /// <summary>The value the network computes.</summary>
  public int Result { get; }

  /// <summary>The width of a value: the input's for 0, a step's for the value it defines.</summary>
  /// <param name="value">The value.</param>
  /// <returns>The width.</returns>
  /// <exception cref="ArgumentOutOfRangeException">No such value.</exception>
  public int WidthOf(int value) {
    ArgumentOutOfRangeException.ThrowIfNegative(value);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(value, this.Steps.Count);
    return value == 0 ? this.InputWidth : this.Steps[value - 1].Width;
  }

  /// <inheritdoc/>
  public override string ToString() => NetworkText.Print(this);

}
