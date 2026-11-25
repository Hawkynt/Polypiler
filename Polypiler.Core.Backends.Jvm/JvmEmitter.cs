using System;
using System.Collections.Generic;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.Jvm;

/// <summary>
/// Emits verified IR modules directly as verifier-clean JVMS Chapter 4 <c>.class</c> bytecode
/// (Java 8, 17, or 21) packaged either as a deterministic <c>.jar</c> archive or standalone
/// <c>.class</c> files.
/// </summary>
public static partial class JvmEmitter {

  /// <summary>The runtime key in <c>tooling/toolchains.lock</c>.</summary>
  public const string Runtime = "jdk";

  /// <summary>An IR statement or expression construct is not supported by the JVM backend.</summary>
  public const string UnsupportedConstructCode = "PLY3150";

  /// <summary>An IR type is not supported by the JVM backend.</summary>
  public const string UnsupportedTypeCode = "PLY3151";

  /// <summary>An IR intrinsic is not supported by the JVM backend.</summary>
  public const string UnsupportedIntrinsicCode = "PLY3152";

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedConstructCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedConstruct = new(
    UnsupportedConstructCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "{0}", "jvm.backend.construct");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedTypeCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedType = new(
    UnsupportedTypeCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "{0}", "jvm.backend.type");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedIntrinsicCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedIntrinsic = new(
    UnsupportedIntrinsicCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "{0}", "jvm.backend.intrinsic");

  /// <summary>
  /// Replaces floating-point and integer intrinsics that the JVM backend does not emit natively
  /// with calls into Polypiler's verified IR libraries.
  /// </summary>
  /// <param name="module">The IR module to legalize.</param>
  /// <param name="implementations">Supplies the precomputed float and integer helper library module.</param>
  /// <param name="profile">The target JVM profile, or <see langword="null"/> for default.</param>
  /// <param name="floatMode">The floating-point accuracy mode.</param>
  /// <returns>The legalized IR module.</returns>
  public static IrModule Legalize(IrModule module, Func<IrModule> implementations, JvmProfile? profile = null, FloatMode floatMode = FloatMode.Accurate) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(implementations);
    _ = profile;
    var floats = FloatLegalization.Rewrite(intrinsic => floatMode == FloatMode.Accurate && JvmLibrary.WritesFloat(intrinsic));
    var ecma = JvmLibrary.EcmaRewrite();
    var integers = JvmLibrary.IntegerRewrite();
    return LibraryLegalization.Lower(SequenceFusion.Run(module), implementations,
      (expression, calls) => floats(expression, calls) ?? ecma(expression, calls) ?? integers(expression, calls) ?? SequenceLegalization.Rewrite(expression, calls));
  }

  /// <summary>Checks whether an IR module can be emitted for the chosen JVM profile.</summary>
  /// <param name="module">The IR module to check.</param>
  /// <param name="profile">The target JVM profile, or <see langword="null"/> for default.</param>
  /// <returns>Verification and backend diagnostics; empty when emission will succeed.</returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module, JvmProfile? profile = null) {
    ArgumentNullException.ThrowIfNull(module);
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      return problems;

    var emitter = new ModuleEmitter(module, profile ?? JvmProfile.Default, JvmEmitOptions.Default);
    return emitter.CheckOnly();
  }

  /// <summary>Emits a verified IR module as JVM bytecode (<c>.jar</c> or <c>.class</c>).</summary>
  /// <param name="module">The verified IR module.</param>
  /// <param name="profile">The target JVM profile, or <see langword="null"/> for default.</param>
  /// <param name="options">Optional packaging, Java package, and main-class options.</param>
  /// <returns>The generated binary file(s).</returns>
  /// <exception cref="ArgumentException">The module fails verification or contains an unsupported construct.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, JvmProfile? profile = null, JvmEmitOptions? options = null)
    => EmitTarget(module, profile, options).Files;

  /// <summary>Emits a verified IR module and returns a <see cref="TargetEmission"/> for the CLI pipeline.</summary>
  /// <param name="module">The verified IR module.</param>
  /// <param name="profile">The target JVM profile, or <see langword="null"/> for default.</param>
  /// <param name="options">Optional packaging, Java package, and main-class options.</param>
  /// <returns>The emitted binary file(s) and target pass applications.</returns>
  /// <exception cref="ArgumentException">The module fails verification or contains an unsupported construct.</exception>
  public static TargetEmission EmitTarget(IrModule module, JvmProfile? profile = null, JvmEmitOptions? options = null) {
    ArgumentNullException.ThrowIfNull(module);
    var effectiveProfile = profile ?? JvmProfile.Default;
    var effectiveOptions = options ?? JvmEmitOptions.Default;
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {problems[0].Message}.", nameof(module));

    var emitter = new ModuleEmitter(module, effectiveProfile, effectiveOptions);
    var checkProblems = emitter.CheckOnly();
    if (checkProblems.Count > 0)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {checkProblems[0].Message}.", nameof(module));

    return new(emitter.EmitFiles(), []);
  }

}
