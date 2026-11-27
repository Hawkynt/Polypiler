using System;
using System.Collections.Generic;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.Msil;

/// <summary>
/// Emits verified IR modules directly as verifiable .NET PE/CLI assemblies (<c>.dll</c>, plus
/// <c>.runtimeconfig.json</c> for console executables) using <c>System.Reflection.Metadata.Ecma335</c>.
/// </summary>
public static class MsilEmitter {

  /// <summary>An IR statement or expression construct is not supported by the MSIL backend.</summary>
  public const string UnsupportedConstructCode = "PLY3050";

  /// <summary>An IR type is not supported by the MSIL backend.</summary>
  public const string UnsupportedTypeCode = "PLY3051";

  /// <summary>An IR intrinsic operation is not supported natively by the MSIL backend.</summary>
  public const string UnsupportedIntrinsicCode = "PLY3052";

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedConstructCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedConstruct = new(
    UnsupportedConstructCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "construct '{0}' is not supported by the MSIL backend", "msil.backend.construct");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedTypeCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedType = new(
    UnsupportedTypeCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "type '{0}' is not supported by the MSIL backend", "msil.backend.type");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedIntrinsicCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedIntrinsic = new(
    UnsupportedIntrinsicCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "intrinsic '{0}' is not supported by the MSIL backend", "msil.backend.intrinsic");

  /// <summary>The runtime key in <c>tooling/toolchains.lock</c> (<see cref="NativeAccuracy.DotNet"/>).</summary>
  public const string Runtime = NativeAccuracy.DotNet;

  /// <summary>
  /// Replaces floating-point and integer intrinsics that the MSIL backend does not emit natively
  /// with calls to Polypiler's verified IR libraries.
  /// </summary>
  /// <param name="module">The IR module.</param>
  /// <param name="implementations">Supplies the precomputed float and integer library module.</param>
  /// <param name="profile">The target MSIL profile, or <see langword="null"/> for default.</param>
  /// <param name="floatMode">The floating-point mode.</param>
  /// <returns>The legalized module.</returns>
  public static IrModule Legalize(IrModule module, Func<IrModule> implementations, MsilProfile? profile = null, FloatMode floatMode = FloatMode.Accurate) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(implementations);
    _ = profile;
    _ = floatMode;
    var floats = FloatLegalization.Rewrite(WritesFloat);
    var integers = RewriteInteger();
    var ecma = RewriteEcma();
    return LibraryLegalization.Lower(SequenceFusion.Run(module), implementations,
      (expression, calls) => floats(expression, calls) ?? ecma(expression, calls) ?? integers(expression, calls) ?? SequenceLegalization.Rewrite(expression, calls));
  }

  /// <summary>Checks whether a module can be emitted for the chosen MSIL profile.</summary>
  /// <param name="module">The IR module.</param>
  /// <param name="profile">The MSIL profile, or <see langword="null"/> for default.</param>
  /// <returns>The verification and backend diagnostics; empty when emission will succeed.</returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module, MsilProfile? profile = null) {
    ArgumentNullException.ThrowIfNull(module);
    _ = profile;
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      return problems;

    return MsilChecker.Check(module);
  }

  /// <summary>Emits a verified IR module as a .NET PE/CLI assembly.</summary>
  /// <param name="module">The IR module.</param>
  /// <param name="profile">The MSIL profile, or <see langword="null"/> for default.</param>
  /// <param name="options">Optional emission options.</param>
  /// <returns>The generated <c>.dll</c> file, plus <c>.runtimeconfig.json</c> for console executables.</returns>
  /// <exception cref="ArgumentException">The module fails verification or contains an unsupported construct.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, MsilProfile? profile = null, MsilEmitOptions? options = null)
    => EmitTarget(module, profile, options).Files;

  /// <summary>Emits a verified IR module and returns a <see cref="TargetEmission"/> for the CLI pipeline.</summary>
  /// <param name="module">The IR module.</param>
  /// <param name="profile">The MSIL profile, or <see langword="null"/> for default.</param>
  /// <param name="options">Optional emission options.</param>
  /// <returns>The emitted files and target pass applications.</returns>
  /// <exception cref="ArgumentException">The module fails verification or contains an unsupported construct.</exception>
  public static TargetEmission EmitTarget(IrModule module, MsilProfile? profile = null, MsilEmitOptions? options = null) {
    ArgumentNullException.ThrowIfNull(module);
    var effectiveProfile = profile ?? MsilProfile.Default;
    var effectiveOptions = options ?? MsilEmitOptions.Default;
    var problems = Check(module, effectiveProfile);
    if (problems.Count > 0)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {problems[0].Message}.", nameof(module));

    var files = MsilAssemblyBuilder.Build(module, effectiveProfile, effectiveOptions);
    return new(files, []);
  }

  /// <summary>Whether the MSIL backend emits a floating-point intrinsic natively rather than lowering it.</summary>
  internal static bool WritesFloat(IrIntrinsic intrinsic) => intrinsic.Op switch {
    IrIntrinsicOp.CopySign or IrIntrinsicOp.Clamp or IrIntrinsicOp.FusedMultiplyAdd
      or IrIntrinsicOp.IsNaN or IrIntrinsicOp.IsInfinity
      or IrIntrinsicOp.IsFinite or IrIntrinsicOp.IsNormal or IrIntrinsicOp.IsSubnormal
      or IrIntrinsicOp.IsNegative => true,
    _ => !FloatLegalization.IsReplaceable(intrinsic)
  };

  /// <summary>Whether the MSIL backend lowers an integer intrinsic through <see cref="IntegerLibrary"/>.</summary>
  private static bool CoversInteger(IrIntrinsic intrinsic) {
    if (intrinsic.Arguments.Count == 0)
      return false;

    var operand = IntegerLibrary.Operand(intrinsic);
    if (!operand.IsInteger)
      return false;

    return intrinsic.Op switch {
      IrIntrinsicOp.Min or IrIntrinsicOp.Max or IrIntrinsicOp.Abs or IrIntrinsicOp.Sign or IrIntrinsicOp.Clamp => false,
      IrIntrinsicOp.ByteSwap or IrIntrinsicOp.BitReverse or IrIntrinsicOp.IsPow2 or IrIntrinsicOp.RoundUpPow2
        or IrIntrinsicOp.HighWord or IrIntrinsicOp.JoinWords or IrIntrinsicOp.BigMul or IrIntrinsicOp.Log2 => false,
      IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig or IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig => false,
      IrIntrinsicOp.ParallelBitExtract or IrIntrinsicOp.ParallelBitDeposit => false,
      IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight or IrIntrinsicOp.LeadingZeroCount
        or IrIntrinsicOp.TrailingZeroCount or IrIntrinsicOp.PopCount when operand.Bits >= 32 => false,
      _ => true
    };
  }

  private static LibraryRewrite RewriteInteger()
    => (expression, calls) => expression is IrIntrinsic intrinsic && CoversInteger(intrinsic) ? IntegerLibrary.Call(intrinsic, calls) : null;

  private static LibraryRewrite RewriteEcma()
    => (expression, calls) => expression is IrIntrinsic { Op: IrIntrinsicOp.ToInt32 or IrIntrinsicOp.ToUint32 or IrIntrinsicOp.RoundHalfUp } intrinsic
      ? calls.Call(intrinsic.Op.ToString(), intrinsic.Type, intrinsic.Arguments)
      : null;

}
