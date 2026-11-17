using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Polypiler.Core.IR;

/// <summary>
/// The conversions of the IR that carry a rounding mode, and for a conversion to an integer a range
/// policy, on the node (#117): <see cref="IrIntrinsicOp.IntToFloat"/>, <see cref="IrIntrinsicOp.FloatConvert"/>
/// and <see cref="IrIntrinsicOp.FloatToInt"/>. The contract is a list of constant arguments after the
/// value (<see cref="FloatToIntContract"/>). Rounding happens once, in the stated mode, from the exact
/// value; the range policy applies to the rounded integer. <see cref="IrConvert"/> stays the
/// conversion of the common contracts - nearest even to floating point, truncation with a trap or with
/// saturation to an integer - and legalization makes a node of this class that states one of those
/// that conversion, and every other the call of its helper.
/// </summary>
public static class IeeeConversions {

  private static readonly HashSet<IrIntrinsicOp> _all = [IrIntrinsicOp.IntToFloat, IrIntrinsicOp.FloatConvert, IrIntrinsicOp.FloatToInt];

  /// <summary>The operations of this class.</summary>
  public static IReadOnlyCollection<IrIntrinsicOp> Operations => _all;

  /// <summary>Whether an operation is one of this class.</summary>
  /// <param name="op">The operation.</param>
  public static bool Is(IrIntrinsicOp op) => _all.Contains(op);

  /// <summary>An integer converted to a floating-point type, rounded once in a mode.</summary>
  /// <param name="type"><see cref="IrType.F32"/> or <see cref="IrType.F64"/>.</param>
  /// <param name="value">The integer; its type gives its signedness.</param>
  /// <param name="mode">The rounding mode.</param>
  /// <returns>The node.</returns>
  public static IrIntrinsic IntToFloat(IrType type, IrExpr value, RoundingMode mode) {
    ArgumentNullException.ThrowIfNull(value);
    return new(IrIntrinsicOp.IntToFloat, type, [value, Int((int)mode)]);
  }

  /// <summary>A number converted to another floating-point format, rounded once in a mode.</summary>
  /// <param name="type"><see cref="IrType.F32"/> or <see cref="IrType.F64"/>.</param>
  /// <param name="value">The number.</param>
  /// <param name="mode">The rounding mode.</param>
  /// <returns>The node.</returns>
  public static IrIntrinsic FloatConvert(IrType type, IrExpr value, RoundingMode mode) {
    ArgumentNullException.ThrowIfNull(value);
    return new(IrIntrinsicOp.FloatConvert, type, [value, Int((int)mode)]);
  }

  /// <summary>Whether an operation of this class takes the types, or <see langword="null"/> for an operation of another.</summary>
  internal static bool? Accepts(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> arguments) {
    if (!Is(op))
      return null;

    bool Float(IrType type) => IeeeFormats.Default.Of(type) is not null;
    return op switch {
      IrIntrinsicOp.IntToFloat => Float(result) && arguments is [{ IsInteger: true }, var mode] && mode == IrType.I32,
      IrIntrinsicOp.FloatConvert => Float(result) && arguments is [var value, var mode] && Float(value) && mode == IrType.I32,
      _ => result.IsInteger && arguments.Count == 6 && Float(arguments[0]) && arguments[1] == IrType.I32 && arguments[2] == IrType.I32
        && arguments[3] == result && arguments[4] == IrType.I32 && arguments[5] == IrType.I32
    };
  }

  /// <summary>What is wrong with a node of this class beyond its types: every part of the contract has to be a constant that names one.</summary>
  /// <param name="intrinsic">The node.</param>
  /// <returns>The problem, or <see langword="null"/>.</returns>
  public static string? Problem(IrIntrinsic intrinsic) {
    ArgumentNullException.ThrowIfNull(intrinsic);
    if (!Is(intrinsic.Op) || intrinsic.Arguments.Count < 2)
      return null;

    if (intrinsic.Arguments[1] is not IrConst { Value: var mode })
      return $"'{intrinsic.Op}' requires a rounding mode that is a constant";
    if (mode is < 0 or > 4)
      return $"unknown rounding mode {mode} of {intrinsic.Op}";
    if (intrinsic.Op != IrIntrinsicOp.FloatToInt || intrinsic.Arguments.Count != 6)
      return null;

    var arguments = intrinsic.Arguments;
    if (arguments[2] is not IrConst { Value: >= 0 and <= 3 })
      return "the range policy of FloatToInt is no constant trap (0), sat (1), wrap (2) or sentinel (3)";
    if (arguments[3] is not IrConst)
      return "the sentinel of FloatToInt is no constant";
    if (arguments[4] is not IrConst { Value: var trap } || !FloatToIntContract.Traps.Contains((IrTrap)trap))
      return "the trap of FloatToInt is no constant arithmetic trap";
    if (arguments[5] is not IrConst { Value: var nan } || (nan != -1 && !FloatToIntContract.Traps.Contains((IrTrap)nan)))
      return "the trap of a NaN of FloatToInt is no constant arithmetic trap, nor -1";

    return null;
  }

  /// <summary>Whether the interpreter is to call this class for a node.</summary>
  internal static bool Evaluates(IrIntrinsic intrinsic) => Is(intrinsic.Op);

  /// <summary>Evaluates a node of a well-typed module on the values of its arguments, as the interpreter carries them.</summary>
  /// <param name="intrinsic">The node.</param>
  /// <param name="arguments">The value, then the constants of the contract as <see cref="long"/>s.</param>
  /// <returns>The result as the interpreter carries it.</returns>
  /// <exception cref="IrTrapException">The contract traps for the value.</exception>
  public static object Evaluate(IrIntrinsic intrinsic, object[] arguments) {
    ArgumentNullException.ThrowIfNull(intrinsic);
    ArgumentNullException.ThrowIfNull(arguments);
    var source = intrinsic.Arguments[0].Type;
    var mode = (RoundingMode)(long)arguments[1];
    switch (intrinsic.Op) {
      case IrIntrinsicOp.IntToFloat:
        var integer = (long)arguments[0];
        var exact = source == IrType.U64 ? (BigInteger)(ulong)integer : integer;
        return IeeeFormats.ValueOf(intrinsic.Type, IeeeFormats.Default.Of(intrinsic.Type)!.FromInteger(exact, mode));
      case IrIntrinsicOp.FloatConvert:
        if (source == intrinsic.Type)
          return arguments[0];

        var target = IeeeFormats.Default.Of(intrinsic.Type)!;
        return IeeeFormats.ValueOf(intrinsic.Type, target.Convert(IeeeFormats.BitsOf(arguments[0]), IeeeFormats.Default.Of(source)!, mode));
      default:
        var contract = FloatToIntContract.Of(intrinsic);
        return ToInteger(intrinsic.Type, IeeeFormats.Default.Of(source)!, IeeeFormats.BitsOf(arguments[0]), contract);
    }
  }

  /// <summary>A number of a format as a value of an integer type under a contract, as the interpreter carries it.</summary>
  /// <param name="type">The integer type.</param>
  /// <param name="format">The format of the number.</param>
  /// <param name="bits">The bits of the number.</param>
  /// <param name="contract">The contract.</param>
  /// <returns>The value.</returns>
  /// <exception cref="IrTrapException">The contract traps for the number.</exception>
  public static long ToInteger(IrType type, IeeeFormat format, ulong bits, FloatToIntContract contract) {
    ArgumentNullException.ThrowIfNull(format);
    ArgumentNullException.ThrowIfNull(contract);
    if (format.IsNaN(bits)) {
      if (contract.NanTrap is { } raised)
        throw new IrTrapException(raised);

      return contract.Policy switch {
        RangePolicy.Trap => throw new IrTrapException(contract.Trap),
        RangePolicy.Sentinel => contract.Sentinel,
        _ => 0
      };
    }

    var minimum = (BigInteger)IrOperators.Minimum(type);
    var maximum = (BigInteger)IrOperators.Maximum(type);
    var rounded = format.ToInteger(bits, contract.Rounding);
    if (rounded is { } value && value >= minimum && value <= maximum)
      return Carried(type, value);

    return contract.Policy switch {
      RangePolicy.Trap => throw new IrTrapException(contract.Trap),
      RangePolicy.Saturate => Carried(type, format.IsNegative(bits) ? minimum : maximum),
      // ECMA-262 ToInt32: the infinities give 0; a finite value is taken modulo 2^N in the signedness of the type.
      RangePolicy.Wrap => rounded is { } finite ? IrOperators.Wrap(type, (long)(ulong)(finite & ulong.MaxValue)) : 0,
      _ => contract.Sentinel
    };
  }

  /// <summary>An integer within the range of a type as an <see cref="IrConst"/> carries it: a <c>u64</c> as its bits.</summary>
  private static long Carried(IrType type, BigInteger value) => type == IrType.U64 ? (long)(ulong)value : (long)value;

  private static IrConst Int(int value) => new(IrType.I32, value);

}
