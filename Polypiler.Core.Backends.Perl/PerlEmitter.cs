using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.Perl;

/// <summary>
/// Emits verified IR modules as standalone plain Perl 5 modules in CPAN layout (<c>lib/…</c>, plus
/// <c>t/….t</c> with <c>Test::More</c> when test functions are present). Output uses core modules
/// only, requires 64-bit Perl integers and runs cleanly under <c>use strict; use warnings;</c>.
/// </summary>
public static partial class PerlEmitter {

  /// <summary>The runtime key in <c>tooling/toolchains.lock</c>.</summary>
  public const string Runtime = "perl";

  /// <summary>A type has no Perl representation in the chosen profile.</summary>
  public const string TypeUnsupportedCode = "PLY1750";

  /// <summary>An operation cannot be emitted as Perl.</summary>
  public const string OperationUnsupportedCode = "PLY1751";

  /// <summary>A control-flow construct has no Perl form.</summary>
  public const string ControlUnsupportedCode = "PLY1758";

  /// <summary>A pointer construct cannot be represented in Perl.</summary>
  public const string PointerUnsupportedCode = "PLY1766";

  /// <summary>A live reinterpreting view is not supported by the Perl backend.</summary>
  public const string OverlayUnsupportedCode = "PLY1767";

  /// <summary>Two exports map to the same Perl name or a name is empty after mapping.</summary>
  public const string NameCollisionCode = "PLY1774";

  /// <summary>An export signature uses a construct the Perl backend cannot expose.</summary>
  public const string ExportUnsupportedCode = "PLY1775";

  /// <summary>A construct is beyond the Perl backend's plain-Perl subset.</summary>
  public const string TypesUnsupportedCode = "PLY1782";

  /// <summary>A trap kind has no Perl mapping.</summary>
  public const string TrapUnsupportedCode = "PLY1783";

  /// <summary>Registered diagnostic descriptor for <see cref="TypeUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor TypeUnsupported = new(TypeUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "type '{0}' has no Perl representation in profile '{1}'", "perl.backend.type");

  /// <summary>Registered diagnostic descriptor for <see cref="OperationUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor OperationUnsupported = new(OperationUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "operation '{0}' cannot be emitted as Perl", "perl.backend.operation");

  /// <summary>Registered diagnostic descriptor for <see cref="ControlUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ControlUnsupported = new(ControlUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "control construct '{0}' has no Perl form", "perl.backend.control");

  /// <summary>Registered diagnostic descriptor for <see cref="PointerUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor PointerUnsupported = new(PointerUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "pointer '{0}' cannot be represented in Perl", "perl.backend.pointer");

  /// <summary>Registered diagnostic descriptor for <see cref="OverlayUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor OverlayUnsupported = new(OverlayUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "a live view of '{0}' as '{1}' is not supported by the Perl backend; use explicit loads and stores", "perl.backend.overlay");

  /// <summary>Registered diagnostic descriptor for <see cref="NameCollisionCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NameCollision = new(NameCollisionCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "{0}", "perl.backend.names");

  /// <summary>Registered diagnostic descriptor for <see cref="ExportUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ExportUnsupported = new(ExportUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "the signature of export '{0}' uses '{1}', which the Perl backend cannot expose", "perl.backend.export");

  /// <summary>Registered diagnostic descriptor for <see cref="TypesUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor TypesUnsupported = new(TypesUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "'{0}' is beyond the Perl backend's plain-Perl subset (hash records, constants, undef, die/eval)", "perl.backend.types");

  /// <summary>Registered diagnostic descriptor for <see cref="TrapUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor TrapUnsupported = new(TrapUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "trap kind '{0}' has no Perl mapping", "perl.backend.traps");

  /// <summary>The CPAN library path a module is emitted to (<c>lib/…</c>).</summary>
  /// <param name="module">The IR module.</param>
  /// <param name="options">Optional emission options overriding the package name.</param>
  /// <returns>The relative path of the generated <c>.pm</c> file.</returns>
  public static string FileName(IrModule module, PerlEmitOptions? options = null) {
    ArgumentNullException.ThrowIfNull(module);
    var package = options?.PackageName is { Length: > 0 } custom ? custom : PerlNaming.Package(module.Name);
    return PerlNaming.PackagePath(package);
  }

  /// <summary>Whether the Perl backend writes a floating-point intrinsic directly instead of lowering via the portable float library.</summary>
  /// <param name="intrinsic">The intrinsic node.</param>
  /// <returns><see langword="true"/> when the emitter writes it natively.</returns>
  public static bool Writes(IrIntrinsic intrinsic) {
    ArgumentNullException.ThrowIfNull(intrinsic);
    return !FloatLegalization.IsReplaceable(intrinsic);
  }

  /// <summary>
  /// Replaces floating-point and integer intrinsics that Perl does not emit natively with calls to
  /// Polypiler's verified IR libraries.
  /// </summary>
  /// <param name="module">The IR module.</param>
  /// <param name="implementations">Supplies the precomputed float and integer library module.</param>
  /// <param name="profile">The target Perl profile.</param>
  /// <param name="mode">The floating-point mode.</param>
  /// <returns>The legalized module.</returns>
  public static IrModule Legalize(IrModule module, Func<IrModule> implementations, PerlProfile? profile = null, FloatMode mode = FloatMode.Accurate) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(implementations);
    _ = profile;
    _ = mode;
    var floats = FloatLegalization.Rewrite(Writes);
    var integers = PerlLibrary.Rewrite();
    return LibraryLegalization.Lower(SequenceFusion.Run(module), implementations,
      (expression, calls) => floats(expression, calls) ?? integers(expression, calls) ?? SequenceLegalization.Rewrite(expression, calls));
  }

  /// <summary>Checks whether a module can be emitted for the chosen Perl profile.</summary>
  /// <param name="module">The IR module.</param>
  /// <param name="profile">The Perl profile, or <see langword="null"/> for default.</param>
  /// <param name="options">Optional emission options.</param>
  /// <returns>The verification and backend diagnostics; empty when emission will succeed.</returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module, PerlProfile? profile = null, PerlEmitOptions? options = null) {
    ArgumentNullException.ThrowIfNull(module);
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      return problems;

    var writer = new Writer(profile ?? PerlProfile.Default, options ?? PerlEmitOptions.Default);
    writer.EmitModule(module);
    return writer.Problems;
  }

  /// <summary>Emits a verified IR module as Perl 5 files.</summary>
  /// <param name="module">The IR module.</param>
  /// <param name="profile">The Perl profile, or <see langword="null"/> for default.</param>
  /// <param name="options">Optional emission options.</param>
  /// <returns>The generated Perl module file and, when test functions are present, the <c>Test::More</c> test file.</returns>
  /// <exception cref="ArgumentException">The module fails verification or contains an unsupported construct.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, PerlProfile? profile = null, PerlEmitOptions? options = null)
    => EmitTarget(module, profile ?? PerlProfile.Default, options ?? PerlEmitOptions.Default).Files;

  /// <summary>Emits a verified IR module and returns a <see cref="TargetEmission"/> for the CLI pipeline.</summary>
  /// <param name="module">The IR module.</param>
  /// <param name="profile">The Perl profile.</param>
  /// <param name="options">Emission options.</param>
  /// <returns>The emitted files and target pass applications.</returns>
  public static TargetEmission EmitTarget(IrModule module, PerlProfile profile, PerlEmitOptions? options = null) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(profile);
    var effective = options ?? PerlEmitOptions.Default;
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {problems[0].Message}.", nameof(module));

    var writer = new Writer(profile, effective);
    var files = writer.EmitModule(module);
    if (writer.Problems.Count > 0)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {writer.Problems[0].Message}.", nameof(module));

    return new(files, []);
  }

  /// <summary>The Perl sub names assigned to each IR function of a module.</summary>
  /// <param name="module">The IR module.</param>
  /// <returns>A mapping from IR function name to Perl sub name.</returns>
  public static IReadOnlyDictionary<string, string> FunctionNames(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var writer = new Writer(PerlProfile.Default, PerlEmitOptions.Default);
    return writer.AssignFunctionNames(module);
  }

}
