using System;
using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Msil;

/// <summary>
/// Recognizes and lowers supported .NET BCL static/instance calls into Polypiler IR intrinsics
/// and expressions.
/// </summary>
internal static class BclIntrinsics {

  private static readonly Dictionary<(string Name, int Arity), IrIntrinsicOp> _mathOps = new() {
    [("Abs", 1)] = IrIntrinsicOp.Abs,
    [("Min", 2)] = IrIntrinsicOp.Min,
    [("Max", 2)] = IrIntrinsicOp.Max,
    [("Sqrt", 1)] = IrIntrinsicOp.Sqrt,
    [("Floor", 1)] = IrIntrinsicOp.Floor,
    [("Ceiling", 1)] = IrIntrinsicOp.Ceiling,
    [("Truncate", 1)] = IrIntrinsicOp.Truncate,
    [("Round", 1)] = IrIntrinsicOp.RoundEven,
    [("CopySign", 2)] = IrIntrinsicOp.CopySign
  };

  private static readonly Dictionary<string, (IrIntrinsicOp Op, bool ReturnsI32)> _bitOps = new(StringComparer.Ordinal) {
    ["RotateLeft"] = (IrIntrinsicOp.RotateLeft, false),
    ["RotateRight"] = (IrIntrinsicOp.RotateRight, false),
    ["LeadingZeroCount"] = (IrIntrinsicOp.LeadingZeroCount, true),
    ["TrailingZeroCount"] = (IrIntrinsicOp.TrailingZeroCount, true),
    ["PopCount"] = (IrIntrinsicOp.PopCount, true)
  };

  private static readonly Dictionary<string, IrIntrinsicOp> _floatPredicates = new(StringComparer.Ordinal) {
    ["IsNaN"] = IrIntrinsicOp.IsNaN,
    ["IsInfinity"] = IrIntrinsicOp.IsInfinity,
    ["IsFinite"] = IrIntrinsicOp.IsFinite,
    ["IsNormal"] = IrIntrinsicOp.IsNormal,
    ["IsSubnormal"] = IrIntrinsicOp.IsSubnormal
  };

  /// <summary>
  /// Attempts to lower a call to a recognized BCL method. Returns <see langword="null"/> when the
  /// target is not a recognized intrinsic (so the caller can report <see cref="MsilFrontend.UnsupportedCall"/>).
  /// </summary>
  public static IrExpr? TryLower(
    string declaringType,
    string methodName,
    IrType returnType,
    IReadOnlyList<IrExpr> arguments) => declaringType switch {
      "System.Math" or "System.MathF" => LowerMath(methodName, returnType, arguments),
      "System.Numerics.BitOperations" => LowerBitOperations(methodName, arguments),
      "System.Buffers.Binary.BinaryPrimitives" => LowerBinaryPrimitives(methodName, returnType, arguments),
      "System.Array" => LowerArray(methodName, arguments),
      "System.Buffer" => LowerBuffer(methodName, arguments),
      "System.Double" or "System.Single" => LowerFloatClassification(methodName, arguments),
      "System.String" => LowerString(methodName, arguments),
      "System.Span`1" or "System.ReadOnlySpan`1" => LowerSpan(methodName, returnType, arguments),
      "System.MemoryExtensions" => LowerMemoryExtensions(methodName, returnType, arguments),
      _ => LowerExceptionMember(declaringType, methodName, arguments)
    };

  private static IrExpr? LowerMath(string name, IrType returnType, IReadOnlyList<IrExpr> args) {
    if (_mathOps.TryGetValue((name, args.Count), out var op))
      return new IrIntrinsic(op, returnType, args);

    if (name == "Round" && args.Count == 2 && args[1] is IrConst { Value: 0 or 1 } mode)
      return new IrIntrinsic(mode.Value == 0 ? IrIntrinsicOp.RoundEven : IrIntrinsicOp.RoundAway, returnType, [args[0]]);

    return null;
  }

  private static IrExpr? LowerBitOperations(string name, IReadOnlyList<IrExpr> args) {
    if (!_bitOps.TryGetValue(name, out var entry))
      return null;

    return new IrIntrinsic(entry.Op, entry.ReturnsI32 ? IrType.I32 : args[0].Type, args);
  }

  private static IrExpr? LowerBinaryPrimitives(string name, IrType returnType, IReadOnlyList<IrExpr> args) {
    var little = name.EndsWith("LittleEndian", StringComparison.Ordinal);
    var big = !little && name.EndsWith("BigEndian", StringComparison.Ordinal);
    if (!little && !big)
      return null;

    if (name.StartsWith("Read", StringComparison.Ordinal))
      return new IrIntrinsic(little ? IrIntrinsicOp.LoadLittle : IrIntrinsicOp.LoadBig, returnType, [args[0], new IrConst(IrType.I32, 0)]);

    return new IrIntrinsic(little ? IrIntrinsicOp.StoreLittle : IrIntrinsicOp.StoreBig, IrType.Void, [args[0], new IrConst(IrType.I32, 0), args[1]]);
  }

  private static IrExpr? LowerArray(string name, IReadOnlyList<IrExpr> args) => (name, args.Count) switch {
    ("Copy", 3) => new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [args[0], new IrConst(IrType.I32, 0), args[1], new IrConst(IrType.I32, 0), args[2]]),
    ("Copy", 5) => new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, args),
    ("Clear", 1) => new IrIntrinsic(IrIntrinsicOp.ArrayClear, IrType.Void, args),
    ("Clear", 3) => ClearRange(args),
    ("Fill", 2) => new IrIntrinsic(IrIntrinsicOp.ArrayFill, IrType.Void, args),
    _ => null
  };

  private static IrExpr ClearRange(IReadOnlyList<IrExpr> args) {
    var elem = args[0].Type.Element!;
    var view = new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(elem), args);
    var zero = elem.IsFloat ? new IrConst(elem, IrOperators.Bits(elem, 0.0)) : new IrConst(elem, 0);
    return new IrIntrinsic(IrIntrinsicOp.ViewFill, IrType.Void, [view, zero]);
  }

  private static IrExpr? LowerBuffer(string name, IReadOnlyList<IrExpr> args) {
    if (name != "BlockCopy")
      return null;

    var elemSize = args[0].Type.Element!.Size;
    if (elemSize == 1)
      return new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, args);

    var sizeConst = new IrConst(IrType.I32, elemSize);
    return new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [
      args[0],
      new IrBinary(IrBinaryOp.Divide, args[1], sizeConst),
      args[2],
      new IrBinary(IrBinaryOp.Divide, args[3], sizeConst),
      new IrBinary(IrBinaryOp.Divide, args[4], sizeConst)
    ]);
  }

  private static IrExpr? LowerFloatClassification(string name, IReadOnlyList<IrExpr> args)
    => _floatPredicates.TryGetValue(name, out var op) ? new IrIntrinsic(op, IrType.Bool, args) : null;

  private static IrExpr? LowerString(string name, IReadOnlyList<IrExpr> args) => name switch {
    "get_Length" => new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, args),
    "get_Chars" => new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, args),
    "Concat" => new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, args),
    "op_Equality" => new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, args),
    "op_Inequality" => new IrUnary(IrUnaryOp.LogicalNot, new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, args)),
    _ => null
  };

  private static IrExpr? LowerSpan(string name, IrType returnType, IReadOnlyList<IrExpr> args) => name switch {
    "op_Implicit" => new IrIntrinsic(IrIntrinsicOp.ViewOf, returnType, args),
    "Slice" => new IrIntrinsic(IrIntrinsicOp.Slice, args[0].Type, args),
    "get_Length" => new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, args),
    _ => null
  };

  private static IrExpr? LowerMemoryExtensions(string name, IrType returnType, IReadOnlyList<IrExpr> args)
    => name == "AsSpan" ? new IrIntrinsic(IrIntrinsicOp.ViewOf, returnType, args) : null;

  private static IrExpr? LowerExceptionMember(string declaringType, string name, IReadOnlyList<IrExpr> args)
    => name == "get_Message" && MsilType.TryResolveExceptionClass(declaringType) is not null
      ? new IrFieldLoad(args[0], IrExceptionClass.Message)
      : null;

}
