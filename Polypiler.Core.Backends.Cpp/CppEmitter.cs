using System;
using System.Collections.Generic;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.Cpp;

/// <summary>
/// Emits verified IR modules as idiomatic, UB-free, standard-library-only ISO C++17, C++20, or C++23
/// header (<c>.hpp</c>) and implementation (<c>.cpp</c>) files.
/// </summary>
public static partial class CppEmitter {

  /// <summary>An IR construct has no representation in the C++ backend.</summary>
  public const string UnsupportedConstructCode = "PLY1450";

  /// <summary>An IR type has no representation in the chosen C++ profile.</summary>
  public const string UnsupportedTypeCode = "PLY1451";

  /// <summary>An IR intrinsic has no representation in the C++ backend.</summary>
  public const string UnsupportedIntrinsicCode = "PLY1452";

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedConstructCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedConstruct = new(UnsupportedConstructCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "construct '{0}' cannot be emitted as C++", "cpp.backend.construct");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedTypeCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedType = new(UnsupportedTypeCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "type '{0}' has no C++ representation in profile '{1}'", "cpp.backend.type");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedIntrinsicCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedIntrinsic = new(UnsupportedIntrinsicCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "intrinsic '{0}' cannot be emitted as C++", "cpp.backend.intrinsic");

  /// <summary>The runtime key in <c>tooling/toolchains.lock</c>.</summary>
  public const string Runtime = "gcc";

  /// <summary>
  /// Replaces floating-point, integer, and sequence intrinsics that C++ does not emit natively
  /// with calls to Polypiler's verified IR libraries.
  /// </summary>
  /// <param name="module">The IR module to legalize.</param>
  /// <param name="implementations">Supplies the precomputed library module on demand.</param>
  /// <param name="profile">The target C++ profile, or <see langword="null"/> for <see cref="CppProfile.Default"/>.</param>
  /// <param name="floatMode">The floating-point accuracy mode.</param>
  /// <returns>The legalized IR module.</returns>
  public static IrModule Legalize(IrModule module, Func<IrModule> implementations, CppProfile? profile = null, FloatMode floatMode = FloatMode.Accurate) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(implementations);
    _ = profile;
    _ = floatMode;
    var floats = FloatLegalization.Rewrite(WritesFloat);
    LibraryRewrite integers = static (expression, calls) =>
      expression is IrIntrinsic intrinsic && NeedsIntegerLibrary(intrinsic)
        ? IntegerLibrary.Call(intrinsic, calls)
        : null;
    return LibraryLegalization.Lower(SequenceFusion.Run(module), implementations,
      (expression, calls) => floats(expression, calls) ?? integers(expression, calls) ?? SequenceLegalization.Rewrite(expression, calls));
  }

  private static bool WritesFloat(IrIntrinsic intrinsic) => intrinsic.Op switch {
    IrIntrinsicOp.CopySign or IrIntrinsicOp.IsNaN or IrIntrinsicOp.IsInfinity
      or IrIntrinsicOp.IsFinite or IrIntrinsicOp.IsNegative or IrIntrinsicOp.IsNormal
      or IrIntrinsicOp.IsSubnormal or IrIntrinsicOp.IsInteger => true,
    _ => !FloatLegalization.IsReplaceable(intrinsic)
  };

  /// <summary>Checks whether a module can be emitted for the chosen C++ profile.</summary>
  /// <param name="module">The IR module.</param>
  /// <param name="profile">The C++ profile, or <see langword="null"/> for <see cref="CppProfile.Default"/>.</param>
  /// <returns>The verification and backend diagnostics; empty when emission will succeed.</returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module, CppProfile? profile = null) {
    ArgumentNullException.ThrowIfNull(module);
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      return problems;

    var writer = new Writer(profile ?? CppProfile.Default, CppEmitOptions.Default);
    writer.EmitModule(module);
    return writer.Problems;
  }

  /// <summary>Emits a verified IR module as a C++ header (<c>.hpp</c>) and source (<c>.cpp</c>) pair.</summary>
  /// <param name="module">The IR module.</param>
  /// <param name="profile">The C++ profile, or <see langword="null"/> for <see cref="CppProfile.Default"/>.</param>
  /// <param name="options">Optional emission options.</param>
  /// <returns>The generated <c>.hpp</c> and <c>.cpp</c> files.</returns>
  /// <exception cref="ArgumentException">The module fails verification or contains an unsupported construct.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, CppProfile? profile = null, CppEmitOptions? options = null)
    => EmitTarget(module, profile, options).Files;

  /// <summary>Emits a verified IR module and returns a <see cref="TargetEmission"/> for the CLI pipeline.</summary>
  /// <param name="module">The IR module.</param>
  /// <param name="profile">The C++ profile, or <see langword="null"/> for <see cref="CppProfile.Default"/>.</param>
  /// <param name="options">Optional emission options.</param>
  /// <returns>The emitted files and target pass applications.</returns>
  /// <exception cref="ArgumentException">The module fails verification or contains an unsupported construct.</exception>
  public static TargetEmission EmitTarget(IrModule module, CppProfile? profile = null, CppEmitOptions? options = null) {
    ArgumentNullException.ThrowIfNull(module);
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {problems[0].Message}.", nameof(module));

    var writer = new Writer(profile ?? CppProfile.Default, options ?? CppEmitOptions.Default);
    var files = writer.EmitModule(module);
    if (writer.Problems.Count > 0)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {writer.Problems[0].Message}.", nameof(module));

    return new(files, []);
  }

  private static bool NeedsIntegerLibrary(IrIntrinsic intrinsic) {
    if (intrinsic.Arguments.Count == 0 || !IntegerLibrary.Operand(intrinsic).IsInteger)
      return false;

    return intrinsic.Op switch {
      IrIntrinsicOp.Abs or IrIntrinsicOp.Min or IrIntrinsicOp.Max or IrIntrinsicOp.Sign => false,
      IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight or IrIntrinsicOp.PopCount
        or IrIntrinsicOp.LeadingZeroCount or IrIntrinsicOp.TrailingZeroCount or IrIntrinsicOp.ByteSwap => false,
      IrIntrinsicOp.HighWord or IrIntrinsicOp.JoinWords => false,
      IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig or IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig => false,
      _ => true
    };
  }

}
