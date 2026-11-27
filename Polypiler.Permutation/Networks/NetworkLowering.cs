using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Permutation.Model;

namespace Polypiler.Permutation.Networks;

/// <summary>A network lowered to the tree IR: one local per step, declared in order, and the result.</summary>
/// <param name="Locals">The locals of the steps, <c>v1</c> first.</param>
/// <param name="Statements">Their declarations.</param>
/// <param name="Result">The result, of type <c>u&lt;OutputWidth&gt;</c>.</param>
public sealed record LoweredNetwork(IReadOnlyList<IrLocal> Locals, IReadOnlyList<IrStmt> Statements, IrExpr Result);

/// <summary>
/// Lowers a network to the tree IR. A network's widths must be widths of the IR's integer types - 8, 16,
/// 32 or 64 bits; any other is refused with <c>PLY0812</c>. The tree IR computes at 32 or 64 bits, so every
/// value is held at its carrier - <c>u32</c> up to 32 bits, <c>u64</c> above - with the bits above its width
/// zero: the input is zero-extended to its carrier, a step that can set bits above its width (<c>Not</c>,
/// <c>ShiftLeft</c>, <c>Truncate</c>) is masked, and the result is converted to <c>u&lt;OutputWidth&gt;</c>.
/// Every shift amount is a constant in 1..width-1, which the tree IR's shifts take as they are.
/// </summary>
public static class NetworkLowering {

  private static readonly int[] _widths = [8, 16, 32, 64];

  /// <summary>The carrier of a width: <c>u32</c> up to 32 bits, <c>u64</c> above.</summary>
  /// <param name="width">The width.</param>
  /// <returns>The carrier type.</returns>
  public static IrType CarrierOf(int width) => width <= 32 ? IrType.U32 : IrType.U64;

  /// <summary>The unsigned integer type of a width the IR has: <c>u8</c>, <c>u16</c>, <c>u32</c>, <c>u64</c>.</summary>
  /// <param name="width">The width.</param>
  /// <returns>The type.</returns>
  public static IrType TypeOf(int width) => width switch { 8 => IrType.U8, 16 => IrType.U16, 32 => IrType.U32, _ => IrType.U64 };

  /// <summary>A constant of a carrier.</summary>
  public static IrConst Constant(IrType carrier, BigInteger bits) => new(carrier, unchecked((long)(ulong)(bits & ulong.MaxValue)));

  /// <summary>A carrier value with the bits above a width cleared; the value itself at the carrier's full width.</summary>
  public static IrExpr Masked(IrExpr value, int width)
    => width == value.Type.Bits ? value : new IrBinary(IrBinaryOp.And, value, Constant(value.Type, Steps.Mask(width)));

  /// <summary>Lowers a network over an input expression of type <c>u&lt;InputWidth&gt;</c>.</summary>
  /// <param name="network">The network, well-formed.</param>
  /// <param name="input">The input.</param>
  /// <param name="registry">The semantics of its operations; <see cref="NetworkOpRegistry.Default"/> when <see langword="null"/>.</param>
  /// <returns>The locals, their declarations and the result.</returns>
  /// <exception cref="PermutationModelException">PLY0812: a width the IR has no integer type of; PLY0810: an operation without semantics.</exception>
  public static LoweredNetwork Lower(BitOperationNetwork network, IrExpr input, NetworkOpRegistry? registry = null) {
    ArgumentNullException.ThrowIfNull(network);
    ArgumentNullException.ThrowIfNull(input);
    registry ??= NetworkOpRegistry.Default;
    Require("input", network.InputWidth);
    Require("output", network.OutputWidth);

    var values = new List<IrExpr> { input.Type == CarrierOf(network.InputWidth) ? input : new IrConvert(CarrierOf(network.InputWidth), input) };
    var locals = new List<IrLocal>();
    var statements = new List<IrStmt>();
    foreach (var (step, number) in network.Steps.Select((step, index) => (step, index + 1))) {
      Require(step.Op.ToString(), step.Width);
      if (!registry.TryGet(step.Op, out var semantics))
        throw new PermutationModelException(Diagnostic.Create(NetworkDiagnostics.IllFormed, null, number, step.Op, "is not registered"));

      var local = new IrLocal("v" + number.ToString(CultureInfo.InvariantCulture), CarrierOf(step.Width));
      locals.Add(local);
      statements.Add(new IrDeclare(local, semantics.Lower(step, [.. step.Operands.Select(operand => values[operand])])));
      values.Add(new IrLocalRef(local));
    }

    var result = values[network.Result];
    var type = TypeOf(network.OutputWidth);
    return new LoweredNetwork(locals, statements, result.Type == type ? result : new IrConvert(type, result));
  }

  /// <summary>A function of the network: one parameter <c>x</c> of type <c>u&lt;InputWidth&gt;</c>, returning the result.</summary>
  /// <param name="network">The network.</param>
  /// <param name="name">The function's name.</param>
  /// <param name="registry">The semantics of its operations.</param>
  /// <returns>The function.</returns>
  public static IrFunction ToFunction(BitOperationNetwork network, string name, NetworkOpRegistry? registry = null) {
    ArgumentNullException.ThrowIfNull(network);
    Require("input", network.InputWidth);
    var input = new IrLocal("x", TypeOf(network.InputWidth));
    var lowered = Lower(network, new IrLocalRef(input), registry);
    return new IrFunction(name, true, TypeOf(network.OutputWidth), [input], lowered.Locals, new IrBlock([.. lowered.Statements, new IrReturn(lowered.Result)]));
  }

  private static void Require(string what, int width) {
    if (!_widths.Contains(width))
      throw new PermutationModelException(Diagnostic.Create(NetworkDiagnostics.UnsupportedWidth, null, what, width));
  }

}
