using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Cli;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;

namespace Polypiler.Tests.Helpers.Support;

/// <summary>
/// One operation of the IR three times: as the intrinsic the interpreter evaluates, as the call of its
/// helper of the library that legalization makes of it, and as that call after the passes over the
/// legalized module. Each is run in the interpreter; an outcome is the value, or the class of the
/// exception the call ends with, which is the kind of the trap.
/// </summary>
internal static class HelperHarness {

  /// <summary>The libraries as the executable carries them.</summary>
  public static IrModule Library => PrecomputedImplementations.Module;

  /// <summary>A module whose public function <c>F</c> returns the node of an operation on its parameters.</summary>
  public static IrModule Module(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> arguments) {
    var parameters = arguments.Select((type, index) => new IrLocal("p" + index, type)).ToList();
    IrExpr node = new IrIntrinsic(op, result, [.. parameters.Select(parameter => (IrExpr)new IrLocalRef(parameter))]);
    var body = result == IrType.Void
      ? new IrBlock([new IrExprStmt(node), new IrReturn(null)])
      : new IrBlock([new IrReturn(node)]);
    return new IrModule("M", [new IrFunction("F", true, result, parameters, [], body)]);
  }

  /// <summary>The module legalized with the integer helpers of the library.</summary>
  public static IrModule Legalized(IrModule module) => LibraryLegalization.Lower(module, () => Library, IntegerLibrary.Call);

  /// <summary>The legalized module after the passes.</summary>
  public static IrModule Optimized(IrModule module, IReadOnlySet<string>? disabled = null)
    => IrPassPipeline.Default.Run(module, new OptimizerOptions { Disabled = disabled ?? new HashSet<string>(), Verify = true }).Module;

  /// <summary>The outcome of a call: the value as text, or the class of the exception it ends with.</summary>
  public static string Outcome(IrModule module, object[] arguments) {
    try {
      var value = IrInterpreter.Call(module, "F", [.. arguments.Select(Copy)]);
      return value switch {
        null => "void",
        byte[] bytes => string.Join(",", bytes),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!
      };
    } catch (IrException exception) {
      return "throws " + exception.Class.Name;
    }
  }

  /// <summary>An argument as a new object: an array is written by a store, which must not reach the next run.</summary>
  private static object Copy(object argument) => argument is Array array ? array.Clone() : argument;

  /// <summary>A value of a type as the interpreter takes it across its boundary.</summary>
  public static object Host(IrType type, long value) => type.Kind switch {
    IrTypeKind.I8 => (sbyte)value,
    IrTypeKind.U8 => (byte)value,
    IrTypeKind.I16 => (short)value,
    IrTypeKind.U16 => (ushort)value,
    IrTypeKind.I32 => (int)value,
    IrTypeKind.U32 => (uint)value,
    IrTypeKind.I64 => value,
    IrTypeKind.U64 => unchecked((ulong)value),
    IrTypeKind.Bool => value != 0,
    _ => throw new ArgumentOutOfRangeException(nameof(type), type, "no integer type")
  };

  /// <summary>
  /// The argument lists an operation is run on: for the first operand every value of an 8-bit type
  /// and the edges and random values of a wider one, for a count or a position small numbers around
  /// the width, for an array of bytes a few lengths; the other operands from the same pools, the
  /// lists drawn with a fixed seed.
  /// </summary>
  public static IEnumerable<object[]> Arguments(IrIntrinsicOp op, IReadOnlyList<IrType> types, int count, int seed) {
    var random = new Random(seed);
    var pools = types.Select((type, index) => Pool(op, type, index, random)).ToList();
    if (pools.Count == 1)
      return pools[0].Select(value => new[] { value });

    // The first operand runs through its pool; the others are drawn for each, and every edge meets every edge once.
    var lists = new List<object[]>();
    foreach (var first in pools[0])
      lists.Add([first, .. pools.Skip(1).Select(pool => pool[random.Next(pool.Count)])]);

    var edges = pools.Select(pool => pool.Take(6).ToList()).ToList();
    lists.AddRange(Product(edges));
    return lists.Take(count);
  }

  private static IEnumerable<object[]> Product(IReadOnlyList<List<object>> pools)
    => pools.Aggregate((IEnumerable<object[]>)[[]], (lists, pool) => lists.SelectMany(list => pool.Select(value => (object[])[.. list, value])));

  private static List<object> Pool(IrIntrinsicOp op, IrType type, int index, Random random) {
    if (type.IsArray)
      return [.. new[] { 0, 1, 2, 4, 8, 12 }.Select(length => (object)Enumerable.Range(0, length).Select(_ => (byte)random.Next(256)).ToArray())];

    if (type == IrType.Bool)
      return [false, true];

    // A count, a position, a start, a length or a width: small numbers about the widths, negative ones included.
    if (type == IrType.I32 && index > 0 && op is not (IrIntrinsicOp.Clamp or IrIntrinsicOp.MulHigh or IrIntrinsicOp.DivFloor or IrIntrinsicOp.ModFloor
        or IrIntrinsicOp.DivEuclid or IrIntrinsicOp.ModEuclid or IrIntrinsicOp.AddSaturate or IrIntrinsicOp.SubSaturate or IrIntrinsicOp.MulSaturate
        or IrIntrinsicOp.AddOverflows or IrIntrinsicOp.SubOverflows or IrIntrinsicOp.MulOverflows or IrIntrinsicOp.InsertBits or IrIntrinsicOp.Min or IrIntrinsicOp.Max
        or IrIntrinsicOp.AddCarry or IrIntrinsicOp.SubBorrow or IrIntrinsicOp.CarryOut or IrIntrinsicOp.BorrowOut or IrIntrinsicOp.BigMul))
      return [.. Enumerable.Range(-2, 70).Select(value => (object)value)];

    if (type == IrType.I32 && op is IrIntrinsicOp.LowMask)
      return [.. Enumerable.Range(-2, 70).Select(value => (object)value)];

    var bits = type.Bits;
    var minimum = (long)IrOperators.Minimum(type);
    var maximum = (long)(ulong)IrOperators.Maximum(type);
    var values = new List<long> { 0, 1, -1, minimum, maximum, 2, minimum + 1, maximum - 1, 3, 0x55, 0x80 };
    if (bits == 8)
      values.AddRange(Enumerable.Range(0, 256).Select(value => (long)value));
    else
      values.AddRange(Enumerable.Range(0, 256).Select(_ => random.NextInt64()));

    return [.. values.Select(value => Host(type, IrOperators.Wrap(type, value))).Distinct()];
  }

}
