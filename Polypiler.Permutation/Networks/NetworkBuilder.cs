using System;
using System.Collections.Generic;
using System.Numerics;

namespace Polypiler.Permutation.Networks;

/// <summary>
/// Builds a network step by step. Identical steps are shared (hash-consing): asking for a step the builder
/// has already made gives its value again. Steps that change nothing are not emitted: an <c>And</c> with an
/// all-ones constant, an <c>Or</c> or <c>Xor</c> with zero, a shift by zero, a width change to the same width.
/// </summary>
public sealed class NetworkBuilder {

  private readonly List<NetworkStep> _steps = [];
  private readonly Dictionary<NetworkStep, int> _shared = [];

  /// <summary>Starts a network with an input of a width.</summary>
  /// <param name="inputWidth">The width of the input, value 0.</param>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputWidth"/> is not positive.</exception>
  public NetworkBuilder(int inputWidth) {
    ArgumentOutOfRangeException.ThrowIfLessThan(inputWidth, 1);
    this.InputWidth = inputWidth;
  }

  /// <summary>The width of the input.</summary>
  public int InputWidth { get; }

  /// <summary>The input, value 0.</summary>
  public static int Input => 0;

  /// <summary>The steps so far.</summary>
  public IReadOnlyList<NetworkStep> Steps => this._steps;

  /// <summary>The width of a value made so far.</summary>
  /// <param name="value">The value.</param>
  /// <returns>Its width.</returns>
  public int WidthOf(int value) => value == 0 ? this.InputWidth : this._steps[value - 1].Width;

  /// <summary>Adds a step, or gives the value of the identical step made before.</summary>
  /// <param name="step">The step.</param>
  /// <returns>Its value.</returns>
  public int Emit(NetworkStep step) {
    ArgumentNullException.ThrowIfNull(step);
    if (this._shared.TryGetValue(step, out var known))
      return known;

    this._steps.Add(step);
    return this._shared[step] = this._steps.Count;
  }

  /// <summary>A constant of a width.</summary>
  public int Const(int width, BigInteger bits) => this.Emit(new(NetworkOp.Const, width, [], new NetworkImmediate.Bits(bits)));

  /// <summary>The and of two values.</summary>
  public int And(int left, int right) => this.Emit(new(NetworkOp.And, this.WidthOf(left), [left, right], null));

  /// <summary>The and of a value and a mask; the value itself for a mask of all ones. Named apart from <see cref="And(int, int)"/>, which an <c>int</c> literal would otherwise pick.</summary>
  public int AndConstant(int value, BigInteger mask)
    => mask == Networks.Steps.Mask(this.WidthOf(value)) ? value : this.Emit(new(NetworkOp.And, this.WidthOf(value), [value], new NetworkImmediate.Bits(mask)));

  /// <summary>The or of two values.</summary>
  public int Or(int left, int right) => this.Emit(new(NetworkOp.Or, this.WidthOf(left), [left, right], null));

  /// <summary>The or of a value and a constant; the value itself for zero.</summary>
  public int OrConstant(int value, BigInteger bits) => bits.IsZero ? value : this.Emit(new(NetworkOp.Or, this.WidthOf(value), [value], new NetworkImmediate.Bits(bits)));

  /// <summary>The exclusive or of two values.</summary>
  public int Xor(int left, int right) => this.Emit(new(NetworkOp.Xor, this.WidthOf(left), [left, right], null));

  /// <summary>The exclusive or of a value and a constant; the value itself for zero.</summary>
  public int XorConstant(int value, BigInteger bits) => bits.IsZero ? value : this.Emit(new(NetworkOp.Xor, this.WidthOf(value), [value], new NetworkImmediate.Bits(bits)));

  /// <summary>Every bit of a value inverted.</summary>
  public int Not(int value) => this.Emit(new(NetworkOp.Not, this.WidthOf(value), [value], null));

  /// <summary>A value widened with zeros; the value itself at its own width.</summary>
  public int ZeroExtend(int value, int width) => width == this.WidthOf(value) ? value : this.Emit(new(NetworkOp.ZeroExtend, width, [value], null));

  /// <summary>A value cut to a narrower width; the value itself at its own width.</summary>
  public int Truncate(int value, int width) => width == this.WidthOf(value) ? value : this.Emit(new(NetworkOp.Truncate, width, [value], null));

  /// <summary>A value shifted towards its top; the value itself for zero.</summary>
  public int ShiftLeft(int value, int amount) => amount == 0 ? value : this.Emit(new(NetworkOp.ShiftLeft, this.WidthOf(value), [value], new NetworkImmediate.Amount(amount)));

  /// <summary>A value shifted towards its bottom; the value itself for zero.</summary>
  public int ShiftRightLogical(int value, int amount) => amount == 0 ? value : this.Emit(new(NetworkOp.ShiftRightLogical, this.WidthOf(value), [value], new NetworkImmediate.Amount(amount)));

  /// <summary>The network that computes a value, with every step made so far; <see cref="NetworkVerifier"/> reports those it does not use.</summary>
  /// <param name="result">The value.</param>
  /// <returns>The network, its output as wide as the value.</returns>
  public BitOperationNetwork Build(int result) => new(this.InputWidth, this.WidthOf(result), this._steps, result);

}
