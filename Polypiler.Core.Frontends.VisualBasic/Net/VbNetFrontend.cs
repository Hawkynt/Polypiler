using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Polypiler.Core.IR;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Polypiler.Core.Frontends.VisualBasic.Net;

/// <summary>
/// How a Visual Basic .NET source file is imported: the profile inputs of the compiler. A file's own
/// <c>Option</c> statements override the options given here, as they do in the compiler.
/// </summary>
public sealed record VbNetImportOptions {

  /// <summary>The language version, as the compiler's <c>LangVersion</c> takes it: <c>9</c> to <c>16.9</c>, the default.</summary>
  public string LanguageVersion { get; init; } = "16.9";

  /// <summary>Whether integer arithmetic and conversions wrap instead of throwing an <c>OverflowException</c>: the compiler option <c>/removeintchecks</c>.</summary>
  public bool RemoveIntegerChecks { get; init; }

  /// <summary><c>Option Strict</c>: implicit narrowing conversions and late binding are errors.</summary>
  public bool OptionStrict { get; init; } = true;

  /// <summary><c>Option Explicit</c>: every variable is declared.</summary>
  public bool OptionExplicit { get; init; } = true;

  /// <summary><c>Option Infer</c>: a local without a type takes its initializer's.</summary>
  public bool OptionInfer { get; init; } = true;

  /// <summary><c>Option Compare Text</c>: strings compare by the culture without case; otherwise by code unit.</summary>
  public bool OptionCompareText { get; init; }

}

/// <summary>
/// Imports Visual Basic .NET: one <c>Module</c> of <c>Shared</c> procedures over the numeric types,
/// <c>Boolean</c>, <c>Char</c>, <c>String</c>, one-dimensional arrays, <c>Structure</c>s of fields and
/// <c>Enum</c>s, with <c>Try</c>/<c>Catch</c>/<c>Finally</c>. Roslyn (<c>Microsoft.CodeAnalysis.VisualBasic</c>)
/// parses and binds the source in process; the bound operations (<c>IOperation</c>), which state every
/// conversion Visual Basic inserts and whether an operation checks for overflow, are lowered to the IR.
/// </summary>
public static class VbNetFrontend {

  /// <summary>The code for an error the Visual Basic compiler reports.</summary>
  public const string CompilerErrorCode = "PLY2610";

  /// <summary>The code for a construct outside the supported subset.</summary>
  public const string UnsupportedCode = "PLY2611";

  /// <summary>The registered diagnostic of <see cref="CompilerErrorCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor CompilerError = new(CompilerErrorCode, DiagnosticSeverity.Error, ExitCode.InvalidSource, "{0}", "visualbasic.net.compiler");

  /// <summary>The registered diagnostic of <see cref="UnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unsupported = new(UnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "'{0}' is not supported by the Visual Basic .NET frontend", "visualbasic.net.construct");

  /// <summary>
  /// The caveat of a module that converts a number to text without a format provider, which .NET does
  /// by the culture of the thread: the import fixes it to the invariant culture.
  /// </summary>
  public const string InvariantCulture = "vbnet-invariant-culture";

  /// <summary>
  /// The caveat of a module with a <c>String</c> variable: one that holds <c>Nothing</c> in Visual Basic
  /// holds the empty string in the IR, which every operation but <c>.Length</c> treats alike.
  /// </summary>
  public const string NothingString = "vbnet-nothing-string";

  private static readonly Lazy<MetadataReference[]> _references = new(() => [
    Loaded(typeof(object).Assembly), Loaded(System.Reflection.Assembly.Load("System.Runtime")), Loaded(typeof(Console).Assembly),
    Loaded(typeof(Microsoft.VisualBasic.Strings).Assembly), Loaded(System.Reflection.Assembly.Load("System.Collections"))
  ]);

  /// <summary>The namespaces a Visual Basic project imports for every file, as the .NET SDK's project template does.</summary>
  private static readonly IReadOnlyList<GlobalImport> _imports = GlobalImport.Parse(["Microsoft.VisualBasic", "System", "System.Collections", "System.Collections.Generic", "System.Diagnostics"]).ToList();

  /// <summary>Whether a language version is one the import takes, as <c>LangVersion</c> writes it.</summary>
  /// <param name="version">The version, such as <c>16.9</c>.</param>
  /// <returns><see langword="true"/> for 9 to 16.9.</returns>
  public static bool IsLanguageVersion(string? version) {
    var parsed = Microsoft.CodeAnalysis.VisualBasic.LanguageVersion.Default;
    return version is not null && LanguageVersionFacts.TryParse(version, ref parsed) && parsed >= Microsoft.CodeAnalysis.VisualBasic.LanguageVersion.VisualBasic9
      && parsed <= Microsoft.CodeAnalysis.VisualBasic.LanguageVersion.VisualBasic16_9;
  }

  /// <summary>Imports one Visual Basic .NET source file.</summary>
  /// <param name="path">The name of the file, used in diagnostics.</param>
  /// <param name="source">The text of the file.</param>
  /// <param name="options">The profile inputs; the defaults of a Visual Basic project when <see langword="null"/>.</param>
  /// <returns>The module, or the diagnostics that explain why there is none.</returns>
  /// <exception cref="ArgumentException">The language version is none of 9 to 16.9.</exception>
  public static VisualBasicImportResult Import(string path, string source, VbNetImportOptions? options = null) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(source);
    options ??= new();
    if (!IsLanguageVersion(options.LanguageVersion))
      throw new ArgumentException($"'{options.LanguageVersion}' is no Visual Basic language version from 9 to 16.9.", nameof(options));

    var version = Microsoft.CodeAnalysis.VisualBasic.LanguageVersion.Default;
    LanguageVersionFacts.TryParse(options.LanguageVersion, ref version);
    var tree = VisualBasicSyntaxTree.ParseText(source, new VisualBasicParseOptions(version), path);
    var compilation = VisualBasicCompilation.Create("Imported", [tree], _references.Value, new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
      globalImports: _imports, optionStrict: options.OptionStrict ? OptionStrict.On : OptionStrict.Off, optionExplicit: options.OptionExplicit,
      optionInfer: options.OptionInfer, optionCompareText: options.OptionCompareText, checkOverflow: !options.RemoveIntegerChecks));
    var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == RoslynSeverity.Error).Select(diagnostic => {
      var position = diagnostic.Location.GetLineSpan().StartLinePosition;
      return CompilerError.At($"{diagnostic.Id}: {diagnostic.GetMessage(CultureInfo.InvariantCulture)}", path, position.Line + 1, position.Character + 1);
    }).ToList();
    if (errors.Count > 0)
      return new(null, errors);

    var diagnostics = new List<Diagnostic>();
    var module = new VbNetLowerer(path, compilation, tree, !options.RemoveIntegerChecks, diagnostics).Lower();
    // A module is null only after a refusal.
    if (module is null || diagnostics.Count > 0)
      return new(null, diagnostics);

    var problems = IrVerifier.Verify(module);
    return problems.Count > 0 ? new(null, problems) : new(module, []);
  }

  /// <summary>An assembly of the running process as a compiler reference, read from memory so that it is there in a single-file executable too.</summary>
  private static unsafe MetadataReference Loaded(System.Reflection.Assembly assembly) {
    assembly.TryGetRawMetadata(out var blob, out var length);
    return AssemblyMetadata.Create(ModuleMetadata.CreateFromMetadata((nint)blob, length)).GetReference();
  }

}
