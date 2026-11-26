using System;
using System.Collections.Generic;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.VBScript;

/// <summary>
/// Emits verified IR modules as standalone <c>Option Explicit</c> VBScript 5.8 scripts (<c>.vbs</c>)
/// runnable under Windows Script Host (<c>cscript //Nologo</c>).
/// </summary>
public static partial class VBScriptEmitter {

  /// <summary>A construct cannot be emitted as VBScript.</summary>
  public const string UnsupportedConstructCode = "PLY2650";

  /// <summary>A type has no VBScript representation in the chosen profile.</summary>
  public const string UnsupportedTypeCode = "PLY2651";

  /// <summary>An intrinsic cannot be emitted as VBScript.</summary>
  public const string UnsupportedIntrinsicCode = "PLY2652";

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedConstructCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedConstruct = new(
    UnsupportedConstructCode,
    DiagnosticSeverity.Error,
    ExitCode.UnsupportedConstruct,
    "construct '{0}' cannot be emitted as VBScript",
    "vbscript.backend.construct");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedTypeCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedType = new(
    UnsupportedTypeCode,
    DiagnosticSeverity.Error,
    ExitCode.UnsupportedConstruct,
    "type '{0}' has no VBScript representation in profile '{1}'",
    "vbscript.backend.type");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedIntrinsicCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedIntrinsic = new(
    UnsupportedIntrinsicCode,
    DiagnosticSeverity.Error,
    ExitCode.UnsupportedConstruct,
    "intrinsic '{0}' cannot be emitted as VBScript",
    "vbscript.backend.intrinsic");

  /// <summary>The runtime key in <c>tooling/toolchains.lock</c>.</summary>
  public const string Runtime = "cscript";

  /// <summary>
  /// Replaces floating-point, integer and sequence intrinsics that VBScript does not emit natively
  /// with calls into Polypiler's verified IR libraries.
  /// </summary>
  /// <param name="module">The IR module.</param>
  /// <param name="implementations">Supplies the precomputed float and integer library module.</param>
  /// <param name="profile">The target VBScript profile, or <see langword="null"/> for default.</param>
  /// <param name="floatMode">The floating-point mode.</param>
  /// <returns>The legalized module.</returns>
  public static IrModule Legalize(IrModule module, Func<IrModule> implementations, VBScriptProfile? profile = null, FloatMode floatMode = FloatMode.Accurate) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(implementations);
    _ = profile;
    _ = floatMode;
    var floats = FloatLegalization.Rewrite(VBScriptLibrary.WritesFloat);
    var integers = VBScriptLibrary.Rewrite();
    return LibraryLegalization.Lower(
      SequenceFusion.Run(module),
      implementations,
      (expression, calls) => floats(expression, calls) ?? integers(expression, calls) ?? SequenceLegalization.Rewrite(expression, calls));
  }

  /// <summary>Checks whether a module can be emitted for the chosen VBScript profile.</summary>
  /// <param name="module">The IR module.</param>
  /// <param name="profile">The VBScript profile, or <see langword="null"/> for default.</param>
  /// <returns>The verification and backend diagnostics; empty when emission will succeed.</returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module, VBScriptProfile? profile = null) {
    ArgumentNullException.ThrowIfNull(module);
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      return problems;

    var writer = new Writer(profile ?? VBScriptProfile.Default, VBScriptEmitOptions.Default);
    writer.EmitModule(module);
    return writer.Problems;
  }

  /// <summary>Emits a verified IR module as a VBScript 5.8 script file.</summary>
  /// <param name="module">The IR module.</param>
  /// <param name="profile">The VBScript profile, or <see langword="null"/> for default.</param>
  /// <param name="options">Optional emission options.</param>
  /// <returns>The generated <c>.vbs</c> file.</returns>
  /// <exception cref="ArgumentException">The module fails verification or contains an unsupported construct.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, VBScriptProfile? profile = null, VBScriptEmitOptions? options = null)
    => EmitTarget(module, profile, options).Files;

  /// <summary>Emits a verified IR module and returns a <see cref="TargetEmission"/> for the CLI pipeline.</summary>
  /// <param name="module">The IR module.</param>
  /// <param name="profile">The VBScript profile, or <see langword="null"/> for default.</param>
  /// <param name="options">Optional emission options.</param>
  /// <returns>The emitted files and target pass applications.</returns>
  /// <exception cref="ArgumentException">The module fails verification or contains an unsupported construct.</exception>
  public static TargetEmission EmitTarget(IrModule module, VBScriptProfile? profile = null, VBScriptEmitOptions? options = null) {
    ArgumentNullException.ThrowIfNull(module);
    var effectiveProfile = profile ?? VBScriptProfile.Default;
    var effectiveOptions = options ?? VBScriptEmitOptions.Default;
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {problems[0].Message}.", nameof(module));

    var writer = new Writer(effectiveProfile, effectiveOptions);
    var files = writer.EmitModule(module);
    if (writer.Problems.Count > 0)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {writer.Problems[0].Message}.", nameof(module));

    return new(files, []);
  }

}
