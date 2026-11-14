using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>One integer operation on one signature, as one function <see cref="IntegerCases.Module"/> declares.</summary>
/// <param name="Function">The name of the function: the operation and the type that tells its signatures apart.</param>
/// <param name="Op">The operation.</param>
/// <param name="Result">The type of the node.</param>
/// <param name="Types">The types of the arguments.</param>
internal sealed record IntegerCase(string Function, IrIntrinsicOp Op, IrType Result, IrType[] Types) {

  /// <summary>The case as a test names it.</summary>
  public override string ToString() => this.Function;

}

/// <summary>
/// The operations of the integer issue and every signature each is defined on, written down from
/// the issue rather than taken from <see cref="IntegerOperations"/>: a width the implementation left
/// out is then a difference, not a smaller test. Per case the calls: every combination of the
/// boundaries of each argument, and random tuples from one seed.
/// </summary>
internal static class IntegerCases {

  /// <summary>The seed of the random tuples.</summary>
  public const int Seed = 2026;

  public static readonly IrType[] Integers = [IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64];

  public static readonly IrType[] Unsigned = [IrType.U8, IrType.U16, IrType.U32, IrType.U64];

  public static readonly IrType[] Multibyte = [IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64];

  private static readonly IrType _bytes = IrType.Array(IrType.U8);

  /// <summary>The bytes the boundary loads and stores work on: every value of a byte's top and bottom bit.</summary>
  private static readonly byte[] _sample = [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0xFE, 0x80, 0x7F];

  /// <summary>Every operation with every signature it has.</summary>
  public static IReadOnlyList<IntegerCase> All { get; } = Build();

  private static List<IntegerCase> Build() {
    var cases = new List<IntegerCase>();
    void Each(IrIntrinsicOp op, IEnumerable<IrType> types, Func<IrType, IrType> result, Func<IrType, IrType[]> arguments)
      => cases.AddRange(types.Select(type => new IntegerCase($"{op}_{type}", op, result(type), arguments(type))));

    IrType Same(IrType type) => type;
    foreach (var op in new[] { IrIntrinsicOp.RotateLeft, IrIntrinsicOp.RotateRight })
      Each(op, Integers, Same, type => [type, IrType.I32]);

    foreach (var op in new[] { IrIntrinsicOp.PopCount, IrIntrinsicOp.LeadingZeroCount, IrIntrinsicOp.TrailingZeroCount, IrIntrinsicOp.Log2, IrIntrinsicOp.Parity })
      Each(op, Integers, _ => IrType.I32, type => [type]);

    foreach (var op in new[] { IrIntrinsicOp.ByteSwap, IrIntrinsicOp.BitReverse, IrIntrinsicOp.RoundUpPow2, IrIntrinsicOp.Abs, IrIntrinsicOp.AbsWrap,
               IrIntrinsicOp.IsolateLowestSet, IrIntrinsicOp.ClearLowestSet, IrIntrinsicOp.MaskToLowestSet })
      Each(op, Integers, Same, type => [type]);

    Each(IrIntrinsicOp.IsPow2, Integers, _ => IrType.Bool, type => [type]);
    Each(IrIntrinsicOp.BigMul, [IrType.I32, IrType.U32], type => type.IsSigned ? IrType.I64 : IrType.U64, type => [type, type]);
    foreach (var op in new[] { IrIntrinsicOp.MulHigh, IrIntrinsicOp.DivFloor, IrIntrinsicOp.ModFloor, IrIntrinsicOp.DivEuclid, IrIntrinsicOp.ModEuclid,
               IrIntrinsicOp.Min, IrIntrinsicOp.Max, IrIntrinsicOp.AddSaturate, IrIntrinsicOp.SubSaturate, IrIntrinsicOp.MulSaturate })
      Each(op, Integers, Same, type => [type, type]);

    Each(IrIntrinsicOp.Clamp, Integers, Same, type => [type, type, type]);
    Each(IrIntrinsicOp.Sign, Integers, _ => IrType.I32, type => [type]);
    foreach (var op in new[] { IrIntrinsicOp.AddCarry, IrIntrinsicOp.SubBorrow })
      Each(op, Unsigned, Same, type => [type, type, IrType.Bool]);

    foreach (var op in new[] { IrIntrinsicOp.CarryOut, IrIntrinsicOp.BorrowOut })
      Each(op, Unsigned, _ => IrType.Bool, type => [type, type, IrType.Bool]);

    foreach (var op in new[] { IrIntrinsicOp.AddOverflows, IrIntrinsicOp.SubOverflows, IrIntrinsicOp.MulOverflows })
      Each(op, Integers, _ => IrType.Bool, type => [type, type]);

    Each(IrIntrinsicOp.ExtractBits, Integers, Same, type => [type, IrType.I32, IrType.I32]);
    Each(IrIntrinsicOp.InsertBits, Integers, Same, type => [type, type, IrType.I32, IrType.I32]);
    Each(IrIntrinsicOp.TestBit, Integers, _ => IrType.Bool, type => [type, IrType.I32]);
    foreach (var op in new[] { IrIntrinsicOp.SetBit, IrIntrinsicOp.ClearBit, IrIntrinsicOp.ToggleBit, IrIntrinsicOp.SignExtend, IrIntrinsicOp.ZeroExtend })
      Each(op, Integers, Same, type => [type, IrType.I32]);

    Each(IrIntrinsicOp.LowMask, Integers, Same, _ => [IrType.I32]);
    foreach (var op in new[] { IrIntrinsicOp.LoadLittle, IrIntrinsicOp.LoadBig })
      Each(op, Multibyte, Same, _ => [_bytes, IrType.I32]);

    foreach (var op in new[] { IrIntrinsicOp.StoreLittle, IrIntrinsicOp.StoreBig })
      Each(op, Multibyte, _ => IrType.Void, type => [_bytes, IrType.I32, type]);

    return cases;
  }

  /// <summary>One module with one public function per case, which applies the operation to its parameters and returns the result.</summary>
  public static IrModule Module(IEnumerable<IntegerCase> cases, string name = "Integers")
    => new(name, [.. cases.Select(Function)]);

  /// <summary>The function of a case.</summary>
  public static IrFunction Function(IntegerCase @case) {
    var parameters = @case.Types.Select((type, index) => new IrLocal(((char)('a' + index)).ToString(), type)).ToList();
    var node = new IrIntrinsic(@case.Op, @case.Result, [.. parameters.Select(parameter => (IrExpr)new IrLocalRef(parameter))]);
    IrStmt[] body = @case.Result == IrType.Void ? [new IrExprStmt(node), new IrReturn(null)] : [new IrReturn(node)];
    return new IrFunction(@case.Function, true, @case.Result, parameters, [], new IrBlock(body));
  }

  /// <summary>The width that bounds the counts and positions of a case: that of its first integer argument, or of its result.</summary>
  private static int WidthOf(IntegerCase @case) => @case.Op == IrIntrinsicOp.LowMask ? @case.Result.Bits : @case.Types.First(type => !type.IsArray).Bits;

  /// <summary>
  /// The calls of a case: every combination of the boundaries of its arguments, then
  /// <paramref name="random"/> tuples drawn from the generator.
  /// </summary>
  public static IReadOnlyList<PrototypeCall> Calls(IntegerCase @case, Random generator, int random = 1000) {
    var returns = @case.Result == IrType.Void ? null : HostType(@case.Result);
    var calls = new List<PrototypeCall>();
    if (@case.Types[0].IsArray) {
      var size = (@case.Types.Length == 2 ? @case.Result : @case.Types[2]).Bits / 8;
      foreach (var bytes in new[] { _sample, [], _sample[..size] })
        foreach (var index in Indexes(bytes.Length, size))
          if (@case.Types.Length == 2)
            calls.Add(new PrototypeCall(@case.Function, [bytes, index]) { Returns = returns });
          else
            calls.AddRange(Edges(@case.Types[2]).Select(value => new PrototypeCall(@case.Function, [bytes, index, value]) { Returns = returns }));
    } else {
      IEnumerable<object[]> tuples = [[]];
      for (var index = 0; index < @case.Types.Length; ++index) {
        var boundaries = Boundaries(@case, index);
        tuples = tuples.SelectMany(tuple => boundaries.Select(value => (object[])[.. tuple, value])).ToList();
      }

      calls.AddRange(tuples.Select(tuple => new PrototypeCall(@case.Function, tuple) { Returns = returns }));
    }

    for (var count = 0; count < random; ++count)
      calls.Add(new PrototypeCall(@case.Function, [.. Enumerable.Range(0, @case.Types.Length).Select(index => Random(@case, index, generator))]) { Returns = returns });

    return calls;
  }

  /// <summary>The outcome the bit-by-bit reference gives for a call of a case.</summary>
  public static string Expected(IntegerCase @case, PrototypeCall call) => IntegerReference.Outcome(@case.Op, @case.Result, @case.Types, call.Arguments);

  /// <summary>The indexes of a load or store of a size: -1, 0, 1, the last that fits, the first that does not, the length and the extremes.</summary>
  private static int[] Indexes(int length, int size)
    => [.. new[] { -1, 0, 1, length - size, length - size + 1, length, int.MinValue, int.MaxValue }.Distinct()];

  /// <summary>The boundaries of one argument of a case.</summary>
  private static object[] Boundaries(IntegerCase @case, int index) {
    var type = @case.Types[index];
    if (type == IrType.Bool)
      return [false, true];

    var counted = type == IrType.I32 && (index > 0 || @case.Op == IrIntrinsicOp.LowMask) && !IsValue(@case, index);
    if (!counted)
      return Edges(type);

    var width = WidthOf(@case);
    int[] counts = @case.Op is IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight
      ? [-width - 1, -1, 0, 1, width - 1, width, width + 1, 2 * width + 3, int.MinValue, int.MaxValue]
      : [-1, 0, 1, width - 1, width, width + 1, int.MinValue, int.MaxValue];
    return [.. counts.Distinct().Cast<object>()];
  }

  /// <summary>Whether an <c>i32</c> argument is a value the operation computes with rather than a count or a position.</summary>
  private static bool IsValue(IntegerCase @case, int index)
    => @case.Op is IrIntrinsicOp.MulHigh or IrIntrinsicOp.DivFloor or IrIntrinsicOp.ModFloor or IrIntrinsicOp.DivEuclid or IrIntrinsicOp.ModEuclid
      or IrIntrinsicOp.Min or IrIntrinsicOp.Max or IrIntrinsicOp.Clamp or IrIntrinsicOp.AddSaturate or IrIntrinsicOp.SubSaturate or IrIntrinsicOp.MulSaturate
      or IrIntrinsicOp.AddOverflows or IrIntrinsicOp.SubOverflows or IrIntrinsicOp.MulOverflows or IrIntrinsicOp.BigMul
      || (@case.Op == IrIntrinsicOp.InsertBits && index == 1);

  /// <summary>
  /// The edges of an integer type: 0, 1, 2, 3, all ones, all ones but the lowest bit, only the top
  /// bit and its neighbours (which include the smallest and the largest value), and the alternating
  /// patterns.
  /// </summary>
  public static object[] Edges(IrType type) {
    var mask = type.Bits == 64 ? ulong.MaxValue : (1UL << type.Bits) - 1;
    var top = 1UL << (type.Bits - 1);
    ulong[] patterns = [0, 1, 2, 3, mask, mask - 1, top, top - 1, top + 1, 0x5555555555555555UL & mask, 0xAAAAAAAAAAAAAAAAUL & mask];
    return [.. patterns.Distinct().Select(pattern => FromPattern(type, pattern))];
  }

  /// <summary>A random argument: a random bit pattern or a small number for a value, a count about the width, a byte array with an index about its length.</summary>
  private static object Random(IntegerCase @case, int index, Random generator) {
    var type = @case.Types[index];
    if (type == IrType.Bool)
      return generator.Next(2) == 1;

    if (type.IsArray) {
      var bytes = new byte[generator.Next(0, 13)];
      generator.NextBytes(bytes);
      return bytes;
    }

    if (@case.Types[0].IsArray && index == 1)
      return generator.Next(-2, 15);

    var width = WidthOf(@case);
    if (type == IrType.I32 && !@case.Types[0].IsArray && (index > 0 || @case.Op == IrIntrinsicOp.LowMask) && !IsValue(@case, index))
      return generator.Next(-2 * width - 2, 2 * width + 3);

    var pattern = (ulong)generator.NextInt64(long.MinValue, long.MaxValue);
    // One value in four is small, so that divisions, fields and powers meet small numbers too.
    return FromPattern(type, generator.Next(4) == 0 ? pattern % 9 - (type.IsSigned ? 4UL : 0UL) : pattern);
  }

  /// <summary>The host value of a type with the given low bits.</summary>
  public static object FromPattern(IrType type, ulong pattern) => type.Kind switch {
    IrTypeKind.I8 => (sbyte)pattern,
    IrTypeKind.U8 => (byte)pattern,
    IrTypeKind.I16 => (short)pattern,
    IrTypeKind.U16 => (ushort)pattern,
    IrTypeKind.I32 => (int)pattern,
    IrTypeKind.U32 => (uint)pattern,
    IrTypeKind.I64 => (long)pattern,
    _ => pattern
  };

  /// <summary>The .NET type a value of an IR type crosses the boundary as.</summary>
  public static Type HostType(IrType type) => type.Kind switch {
    IrTypeKind.Bool => typeof(bool),
    IrTypeKind.I8 => typeof(sbyte),
    IrTypeKind.U8 => typeof(byte),
    IrTypeKind.I16 => typeof(short),
    IrTypeKind.U16 => typeof(ushort),
    IrTypeKind.I32 => typeof(int),
    IrTypeKind.U32 => typeof(uint),
    IrTypeKind.I64 => typeof(long),
    _ => typeof(ulong)
  };

}
