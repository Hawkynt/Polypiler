using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Polypiler.Core.Frontends.Cpp.Lowering;
using Polypiler.Core.Frontends.Cpp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Cpp;

/// <summary>Options controlling how a C++ source file is imported.</summary>
public sealed record CppImportOptions {

  /// <summary>Default import options (<c>cpp20</c>).</summary>
  public static CppImportOptions Default { get; } = new();

  /// <summary>The C++ language standard: <c>"cpp17"</c>, <c>"cpp20"</c> (default), or <c>"cpp23"</c>.</summary>
  public string? Standard { get; init; }

}

/// <summary>The result of importing a C++ source file.</summary>
/// <param name="Module">The imported IR module, or <see langword="null"/> when errors occurred.</param>
/// <param name="Diagnostics">Diagnostics emitted during lexing, parsing, or lowering.</param>
public sealed record CppImportResult(IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Imports a pure-C++ subset (C++17, C++20, C++23) into Polypiler's structured typed IR using a
/// built-in lexer, recursive-descent/Pratt parser, and type-checking lowerer with no external
/// compiler process dependency.
/// </summary>
public static class CppFrontend {

  /// <summary>Diagnostic code for a C++ lexing or syntax error.</summary>
  public const string SyntaxErrorCode = "PLY1400";

  /// <summary>Diagnostic code for a C++ construct outside the supported subset.</summary>
  public const string UnsupportedConstructCode = "PLY1401";

  /// <summary>Diagnostic code for a C++ type outside the supported subset.</summary>
  public const string UnsupportedTypeCode = "PLY1402";

  /// <summary>Diagnostic code for an unsupported function, intrinsic, or method call.</summary>
  public const string UnsupportedCallCode = "PLY1403";

  /// <summary>Diagnostic code for an unresolved C++ identifier, type, or member.</summary>
  public const string UnresolvedSymbolCode = "PLY1404";

  /// <summary>Registered diagnostic descriptor for <see cref="SyntaxErrorCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor SyntaxError = new(
    SyntaxErrorCode, DiagnosticSeverity.Error, ExitCode.InvalidSource,
    "C++ syntax error: {0}", "cpp.frontend.syntax");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedConstructCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedConstruct = new(
    UnsupportedConstructCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "C++ construct '{0}' is not supported by the C++ frontend", "cpp.frontend.construct");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedTypeCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedType = new(
    UnsupportedTypeCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "C++ type '{0}' is not supported by the C++ frontend", "cpp.frontend.type");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedCallCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedCall = new(
    UnsupportedCallCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "C++ call '{0}' is not supported by the C++ frontend", "cpp.frontend.call");

  /// <summary>Registered diagnostic descriptor for <see cref="UnresolvedSymbolCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnresolvedSymbol = new(
    UnresolvedSymbolCode, DiagnosticSeverity.Error, ExitCode.InvalidSource,
    "unresolved C++ symbol '{0}'", "cpp.frontend.symbol");

  /// <summary>Imports a C++ source string into PolyIR.</summary>
  /// <param name="fileName">The logical file path used in diagnostics and module naming.</param>
  /// <param name="source">The C++ source text.</param>
  /// <param name="options">Optional import settings such as the C++ standard.</param>
  /// <returns>The imported IR module, or diagnostics describing why the source was refused.</returns>
  public static CppImportResult Import(string fileName, string source, CppImportOptions? options = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(source);
    var standard = (options ?? CppImportOptions.Default).Standard ?? "cpp20";
    if (!IsSupportedStandard(standard))
      return new(null, [Diagnostic.Create(UnsupportedConstruct, new(fileName, 1, 1, 1, 1), $"standard '{standard}'")]);

    try {
      var tokens = CppLexer.Tokenize(fileName, source);
      var unit = CppParser.Parse(fileName, tokens);
      var moduleName = ModuleNameOf(fileName);
      var module = CppLowerer.Lower(fileName, moduleName, unit);
      return new(module, []);
    } catch (CppDiagnosticException ex) {
      return new(null, [ex.Diagnostic]);
    }
  }

  private static bool IsSupportedStandard(string standard)
    => string.Equals(standard, "cpp17", StringComparison.OrdinalIgnoreCase)
      || string.Equals(standard, "cpp20", StringComparison.OrdinalIgnoreCase)
      || string.Equals(standard, "cpp23", StringComparison.OrdinalIgnoreCase);

  private static string ModuleNameOf(string fileName) {
    var stem = Path.GetFileNameWithoutExtension(fileName);
    if (string.IsNullOrEmpty(stem))
      return "CppModule";

    var builder = new StringBuilder(stem.Length);
    foreach (var ch in stem)
      builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');

    if (char.IsDigit(builder[0]))
      builder.Insert(0, '_');

    return builder.ToString();
  }

}

internal sealed class CppDiagnosticException(Diagnostic diagnostic) : Exception(diagnostic.Message) {

  public Diagnostic Diagnostic { get; } = diagnostic;

}
