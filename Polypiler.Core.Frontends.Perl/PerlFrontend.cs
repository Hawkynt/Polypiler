using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Polypiler.Core.Frontends.Perl.Lowering;
using Polypiler.Core.Frontends.Perl.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl;

/// <summary>Options controlling how a Perl 5 source file is imported.</summary>
public sealed record PerlImportOptions {

  /// <summary>Default import options.</summary>
  public static PerlImportOptions Default { get; } = new();

  /// <summary>
  /// Contract lines specifying parameter and return types, e.g.
  /// <c>main::add.a: i32</c>, <c>main::add.b: i32</c>, <c>main::add.return: i32</c>.
  /// </summary>
  public string Contracts { get; init; } = "";

  /// <summary>An explicit Perl interpreter path tried before <c>POLYPILER_PERL</c> and <c>perl</c>.</summary>
  public string? Interpreter { get; init; }

  /// <summary>Optional export filter narrowing which functions are marked public.</summary>
  public IReadOnlySet<string>? ExportFilter { get; init; }

}

/// <summary>The result of importing a Perl 5 source file.</summary>
/// <param name="Module">The imported verified IR module, or <see langword="null"/> on failure.</param>
/// <param name="Diagnostics">Diagnostics emitted during compilation or lowering.</param>
/// <param name="Contracts">Recorded source contract lines.</param>
/// <param name="Exports">Qualified names of exported subroutines.</param>
/// <param name="TestFunctions">Names of test functions imported from <c>Test::More</c> files.</param>
public sealed record PerlImportResult(
  IrModule? Module,
  IReadOnlyList<Diagnostic> Diagnostics,
  IReadOnlyList<string> Contracts,
  IReadOnlyList<string> Exports,
  IReadOnlyList<string> TestFunctions);

/// <summary>
/// Imports verified Perl 5 subroutines and modules into Polypiler's structured typed IR using
/// Perl's own compiler via the embedded <c>B::PolypilerDump</c> backend.
/// </summary>
public static class PerlFrontend {

  /// <summary>The relative path of the embedded <c>B::PolypilerDump</c> compiler backend.</summary>
  public const string ParserPath = PerlOpTreeHelper.ParserRelativePath;

  /// <summary>Returns the UTF-8 source bytes of the embedded <c>B::PolypilerDump</c> module.</summary>
  public static byte[] ParserSource() => [.. PerlOpTreeHelper.ParserSource];

  /// <summary>Perl compilation or op-tree helper failed.</summary>
  public const string CompileFailedCode = "PLY1700";

  /// <summary>Unsupported scalar construct.</summary>
  public const string ScalarUnsupportedCode = "PLY1702";

  /// <summary>Unsupported control-flow construct.</summary>
  public const string ControlUnsupportedCode = "PLY1710";

  /// <summary>Dynamic loop control (last/next in do-while or unknown label).</summary>
  public const string DynamicLoopControlCode = "PLY1711";

  /// <summary>Unsupported array or list operation.</summary>
  public const string ArrayUnsupportedCode = "PLY1720";

  /// <summary>Unsupported reference construct.</summary>
  public const string ReferenceUnsupportedCode = "PLY1721";

  /// <summary>Unsupported string operation or wide character string.</summary>
  public const string StringUnsupportedCode = "PLY1722";

  /// <summary>Unsupported pack/unpack template.</summary>
  public const string PackTemplateUnsupportedCode = "PLY1723";

  /// <summary>Unsupported subroutine or module construct.</summary>
  public const string SubUnsupportedCode = "PLY1730";

  /// <summary>Escaping code reference.</summary>
  public const string EscapingClosureCode = "PLY1732";

  /// <summary>Unknown module or unresolved subroutine.</summary>
  public const string UnresolvedModuleCode = "PLY1733";

  /// <summary>Perl object-system construct outside the plain subset.</summary>
  public const string ObjectSystemUnsupportedCode = "PLY1740";

  /// <summary>Order-dependent hash operation.</summary>
  public const string HashOrderUnsupportedCode = "PLY1741";

  /// <summary>Unsupported error-handling construct.</summary>
  public const string ErrorHandlingUnsupportedCode = "PLY1742";

  /// <summary>Unsupported tie, typeglob or magic variable.</summary>
  public const string MagicUnsupportedCode = "PLY1743";

  /// <summary>Registered diagnostic descriptor for <see cref="CompileFailedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor CompileFailed = new(CompileFailedCode, DiagnosticSeverity.Error, ExitCode.InvalidSource,
    "Perl compilation failed: {0}", "perl.frontend.compile");

  /// <summary>Registered diagnostic descriptor for <see cref="ScalarUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ScalarUnsupported = new(ScalarUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "'{0}' is not supported by the Perl frontend", "perl.frontend.scalar");

  /// <summary>Registered diagnostic descriptor for <see cref="ControlUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ControlUnsupported = new(ControlUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "'{0}' is not supported by the Perl frontend", "perl.frontend.control");

  /// <summary>Registered diagnostic descriptor for <see cref="DynamicLoopControlCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor DynamicLoopControl = new(DynamicLoopControlCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "'{0}' does not target a loop that lexically encloses it in the same sub; dynamic loop control is not imported", "perl.frontend.loop-control");

  /// <summary>Registered diagnostic descriptor for <see cref="ArrayUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ArrayUnsupported = new(ArrayUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "array or list operation '{0}' is not supported by the Perl frontend", "perl.frontend.array");

  /// <summary>Registered diagnostic descriptor for <see cref="ReferenceUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ReferenceUnsupported = new(ReferenceUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "reference construct '{0}' is not supported by the Perl frontend", "perl.frontend.reference");

  /// <summary>Registered diagnostic descriptor for <see cref="StringUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor StringUnsupported = new(StringUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "string operation '{0}' is not supported; the Perl subset handles byte strings only", "perl.frontend.string");

  /// <summary>Registered diagnostic descriptor for <see cref="PackTemplateUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor PackTemplateUnsupported = new(PackTemplateUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "pack/unpack template '{0}' is not supported: {1}", "perl.frontend.pack");

  /// <summary>Registered diagnostic descriptor for <see cref="SubUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor SubUnsupported = new(SubUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "'{0}' is not supported for subs and modules by the Perl frontend", "perl.frontend.sub");

  /// <summary>Registered diagnostic descriptor for <see cref="EscapingClosureCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor EscapingClosure = new(EscapingClosureCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "the code reference '{0}' escapes; only anonymous subs called through the lexical they are assigned to are imported", "perl.frontend.closure");

  /// <summary>Registered diagnostic descriptor for <see cref="UnresolvedModuleCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnresolvedModule = new(UnresolvedModuleCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "'{0}' is neither part of this import nor in the Perl call map", "perl.frontend.module");

  /// <summary>Registered diagnostic descriptor for <see cref="ObjectSystemUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ObjectSystemUnsupported = new(ObjectSystemUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "'{0}' uses Perl's object system, which is outside the first Perl subset", "perl.frontend.object");

  /// <summary>Registered diagnostic descriptor for <see cref="HashOrderUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor HashOrderUnsupported = new(HashOrderUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "'{0}' observes the order of a hash, which Perl leaves unspecified; sort the keys", "perl.frontend.hash");

  /// <summary>Registered diagnostic descriptor for <see cref="ErrorHandlingUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ErrorHandlingUnsupported = new(ErrorHandlingUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "error-handling construct '{0}' is not supported by the Perl frontend", "perl.frontend.error");

  /// <summary>Registered diagnostic descriptor for <see cref="MagicUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor MagicUnsupported = new(MagicUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "'{0}' relies on tie, typeglob or special-variable behaviour that is not imported", "perl.frontend.magic");

  /// <summary>Compiles a Perl 5 source string using the installed Perl interpreter and imports it into PolyIR.</summary>
  /// <param name="file">The logical file path used in diagnostics and module naming.</param>
  /// <param name="source">The Perl 5 source code.</param>
  /// <param name="options">Import options including contracts and interpreter override.</param>
  /// <returns>The imported module or diagnostics.</returns>
  public static PerlImportResult Import(string file, string source, PerlImportOptions? options = null) {
    ArgumentNullException.ThrowIfNull(file);
    ArgumentNullException.ThrowIfNull(source);
    var effective = options ?? PerlImportOptions.Default;
    var (dump, error, line) = PerlOpTreeHelper.Compile(file, source, effective.Interpreter);
    if (dump is null)
      return new(null, [Diagnostic.Create(CompileFailed, new(file, line, 1, line, 1), error!)], [], [], []);

    return ImportDump(file, dump, effective);
  }

  /// <summary>Imports a pre-dumped <c>B::PolypilerDump</c> JSON op tree into PolyIR.</summary>
  /// <param name="file">The logical file path used in diagnostics and module naming.</param>
  /// <param name="dump">The UTF-8 JSON bytes emitted by <c>B::PolypilerDump</c>.</param>
  /// <param name="options">Import options including contracts.</param>
  /// <returns>The imported module or diagnostics.</returns>
  public static PerlImportResult ImportDump(string file, ReadOnlySpan<byte> dump, PerlImportOptions? options = null) {
    ArgumentNullException.ThrowIfNull(file);
    var effective = options ?? PerlImportOptions.Default;
    PerlTree? tree;
    try {
      tree = JsonSerializer.Deserialize<PerlTree>(dump, PerlTreeJson.Options);
    } catch (JsonException ex) {
      return new(null, [Diagnostic.Create(CompileFailed, new(file, 1, 1, 1, 1), ex.Message)], [], [], []);
    }
    if (tree is not { Wire: 1 })
      return new(null, [Diagnostic.Create(CompileFailed, new(file, 1, 1, 1, 1), "expected op-tree wire version 1")], [], [], []);

    return PerlLowerer.LowerTree(file, tree, effective);
  }

}
