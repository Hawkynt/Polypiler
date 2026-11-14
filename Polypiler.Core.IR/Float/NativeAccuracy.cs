using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>How a translation treats the floating-point functions whose contract is <c>ulp(n)</c> (ADR 0005).</summary>
public enum FloatMode {

  /// <summary>The portable implementation everywhere: every target gives the same bits.</summary>
  Strict,

  /// <summary>The native call of the target where its measured error admits it, the portable implementation elsewhere.</summary>
  Accurate

}

/// <summary>A measured or published reference error of a native function of a runtime, in ULP of <c>f64</c>.</summary>
/// <param name="Runtime">The runtime, by its toolchain key: <c>dotnet-sdk</c> or <c>node</c>.</param>
/// <param name="Op">The operation the native function computes.</param>
/// <param name="Error">The largest reference error in ULP.</param>
/// <param name="Source">Where the number comes from.</param>
public sealed record NativeError(string Runtime, IrIntrinsicOp Op, double Error, string Source);

/// <summary>
/// The native slack of ADR 0005: what is known of the error of each runtime's native function, and
/// what follows for a backend. A native call is admitted for an <c>ulp(n)</c> operation only where
/// the runtime's error, rounded up, is at most <c>n</c> and at most 4; a runtime without a number has
/// no slack. The <c>f64</c> numbers are the ADR's table: the larger of the measurement on the
/// development host and the published error of the library the runtime uses. A <c>f32</c> operation
/// may use the <c>f64</c> native rounded to <c>f32</c>: below 2^28 ULP of <c>f64</c> error the rounded
/// result is within 1 ULP of <c>f32</c>.
/// </summary>
public static class NativeAccuracy {

  /// <summary>The runtime of the C# backend's generated code.</summary>
  public const string DotNet = "dotnet-sdk";

  /// <summary>The runtime of the JavaScript backend's generated code.</summary>
  public const string Node = "node";

  private const string Measured = "ADR 0005, measured";
  private const string Glibc = "ADR 0005, published for glibc 2.44 (Gladman et al., table 3), which .NET calls on Linux";
  private const string Microsoft = "ADR 0005, published for Microsoft's C runtime (Gladman et al., table 3), which .NET calls on Windows";

  /// <summary>Every number, one per runtime, operation and source.</summary>
  public static IReadOnlyList<NativeError> Errors { get; } = [
    new(DotNet, IrIntrinsicOp.Sin, 0.509, Measured), new(DotNet, IrIntrinsicOp.Sin, 0.516, Glibc),
    new(DotNet, IrIntrinsicOp.Cos, 0.511, Measured), new(DotNet, IrIntrinsicOp.Cos, 0.516, Glibc),
    new(DotNet, IrIntrinsicOp.Tan, 0.523, Measured), new(DotNet, IrIntrinsicOp.Tan, 0.619, Glibc), new(DotNet, IrIntrinsicOp.Tan, 1.32, Microsoft),
    new(DotNet, IrIntrinsicOp.Exp, 0.503, Measured), new(DotNet, IrIntrinsicOp.Exp, 0.511, Glibc), new(DotNet, IrIntrinsicOp.Exp, 1.50, Microsoft),
    new(DotNet, IrIntrinsicOp.Log, 0.509, Measured), new(DotNet, IrIntrinsicOp.Log, 0.520, Glibc),
    new(DotNet, IrIntrinsicOp.Log2, 0.518, Measured), new(DotNet, IrIntrinsicOp.Log2, 0.548, Glibc),
    new(DotNet, IrIntrinsicOp.Log10, 1.526, Measured), new(DotNet, IrIntrinsicOp.Log10, 1.62, Glibc),
    new(DotNet, IrIntrinsicOp.Pow, 0.503, Measured), new(DotNet, IrIntrinsicOp.Pow, 0.523, Glibc),
    new(DotNet, IrIntrinsicOp.Atan2, 0.503, Measured), new(DotNet, IrIntrinsicOp.Atan2, 0.524, Glibc),
    new(DotNet, IrIntrinsicOp.Sinh, 0.500, Measured), new(DotNet, IrIntrinsicOp.Sinh, 1.94e19, Glibc),
    new(DotNet, IrIntrinsicOp.Cosh, 0.500, Measured), new(DotNet, IrIntrinsicOp.Cosh, 0.500, Glibc),
    new(DotNet, IrIntrinsicOp.Tanh, 0.500, Measured), new(DotNet, IrIntrinsicOp.Tanh, 0.500, Glibc),
    new(DotNet, IrIntrinsicOp.Cbrt, 2.925, Measured), new(DotNet, IrIntrinsicOp.Cbrt, 3.67, Glibc), new(DotNet, IrIntrinsicOp.Cbrt, 1.86, Microsoft),
    new(DotNet, IrIntrinsicOp.Hypot, 0.885, Measured + "; double.Hypot is managed code, the same on every host"),
    new(Node, IrIntrinsicOp.Sin, 0.770, Measured), new(Node, IrIntrinsicOp.Cos, 0.784, Measured), new(Node, IrIntrinsicOp.Tan, 0.777, Measured),
    new(Node, IrIntrinsicOp.Exp, 0.837, Measured), new(Node, IrIntrinsicOp.Log, 0.756, Measured), new(Node, IrIntrinsicOp.Log2, 0.750, Measured),
    new(Node, IrIntrinsicOp.Log10, 1.616, Measured), new(Node, IrIntrinsicOp.PowEcma, 0.896, Measured + " (Math.pow, ECMA-262's exponentiation)"), new(Node, IrIntrinsicOp.Atan2, 1.195, Measured),
    new(Node, IrIntrinsicOp.Sinh, 1.444, Measured), new(Node, IrIntrinsicOp.Cosh, 1.799, Measured), new(Node, IrIntrinsicOp.Tanh, 1.821, Measured),
    new(Node, IrIntrinsicOp.Cbrt, 0.658, Measured), new(Node, IrIntrinsicOp.Hypot, 1.696, Measured)
  ];

  /// <summary>The largest error ADR 0005 accepts of any runtime.</summary>
  public const int Cap = 4;

  /// <summary>The slack <c>N</c> of a runtime's native function of <c>f64</c>: its largest known error rounded up; <see langword="null"/> without a number.</summary>
  /// <param name="runtime">The runtime.</param>
  /// <param name="op">The operation.</param>
  /// <returns>The slack.</returns>
  public static double? Error(string runtime, IrIntrinsicOp op) {
    var known = Errors.Where(error => error.Runtime == runtime && error.Op == op).Select(error => error.Error).ToList();
    return known.Count == 0 ? null : known.Max();
  }

  /// <summary>
  /// The slack of a native function in a format, or <see langword="null"/> where it is not admitted:
  /// no number, an error above the cap, or (for <c>f64</c>) above the operation's reference bound. A
  /// <c>f32</c> operation computed by the <c>f64</c> native and rounded has the slack 1.
  /// </summary>
  /// <param name="runtime">The runtime.</param>
  /// <param name="op">The operation.</param>
  /// <param name="format"><see cref="IrType.F32"/> or <see cref="IrType.F64"/>.</param>
  /// <returns>The slack <c>N</c>, or <see langword="null"/>.</returns>
  public static int? Slack(string runtime, IrIntrinsicOp op, IrType format) {
    if (Error(runtime, op) is not { } error || FloatOperationTable.Of(op) is not { Contract.Kind: FloatContractKind.Ulp } operation)
      return null;

    if (format == IrType.F32)
      return error < Math.ScaleB(1, 28) ? 1 : null;

    var slack = (int)Math.Ceiling(error);
    return slack <= Cap && slack <= operation.Contract.Bound ? slack : null;
  }

  /// <summary>The comparison bound of ADR 0005 between a native result and the reference: <c>n + N + max(n, N)</c>.</summary>
  /// <param name="op">An operation with an <c>ulp(n)</c> contract.</param>
  /// <param name="slack">The slack <c>N</c> of the native function.</param>
  /// <returns>The bound in values of the format.</returns>
  public static int Bound(IrIntrinsicOp op, int slack) {
    var n = FloatOperationTable.Of(op)!.Contract.Bound;
    return n + slack + Math.Max(n, slack);
  }

}
