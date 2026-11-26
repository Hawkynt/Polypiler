using System;
using System.Collections.Generic;
using Polypiler.Core.Frontends.VBScript.Lowering;
using Polypiler.Core.Frontends.VBScript.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VBScript;

/// <summary>Options controlling how a VBScript 5.8 source file is imported.</summary>
public sealed record VBScriptImportOptions {

  /// <summary>Default import options.</summary>
  public static VBScriptImportOptions Default { get; } = new();

  /// <summary>
  /// Optional contract lines specifying parameter, return, field, or global types, e.g.
  /// <c>Add.a: i32</c>, <c>Add.b: i32</c>, <c>Add.return: i32</c>.
  /// </summary>
  public string? Contracts { get; init; }

}

/// <summary>The result of importing a VBScript 5.8 source file.</summary>
/// <param name="Module">The imported verified IR module, or <see langword="null"/> on failure.</param>
/// <param name="Diagnostics">Diagnostics emitted during lexing, parsing, or lowering.</param>
public sealed record VBScriptImportResult(IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Imports VBScript 5.8 source code into Polypiler's structured, statically typed PolyIR using a
/// pure C# lexer, recursive-descent parser, and type-inferring lowerer.
/// </summary>
public static class VBScriptFrontend {

  /// <summary>VBScript lexical or syntax error.</summary>
  public const string SyntaxErrorCode = "PLY2600";

  /// <summary>Unsupported VBScript language construct.</summary>
  public const string UnsupportedConstructCode = "PLY2601";

  /// <summary>Unsupported or unknown VBScript procedure or function call.</summary>
  public const string UnsupportedCallCode = "PLY2602";

  /// <summary>Unresolved VBScript variable, class, or member symbol.</summary>
  public const string UnresolvedSymbolCode = "PLY2603";

  /// <summary>Malformed or invalid VBScript type contract.</summary>
  public const string InvalidContractCode = "PLY2604";

  /// <summary>Registered diagnostic descriptor for <see cref="SyntaxErrorCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor SyntaxError = new(
    SyntaxErrorCode,
    DiagnosticSeverity.Error,
    ExitCode.InvalidSource,
    "VBScript syntax error: {0}",
    "vbscript.frontend.syntax");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedConstructCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedConstruct = new(
    UnsupportedConstructCode,
    DiagnosticSeverity.Error,
    ExitCode.UnsupportedConstruct,
    "VBScript construct '{0}' is not supported",
    "vbscript.frontend.construct");

  /// <summary>Registered diagnostic descriptor for <see cref="UnsupportedCallCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedCall = new(
    UnsupportedCallCode,
    DiagnosticSeverity.Error,
    ExitCode.UnsupportedConstruct,
    "VBScript call '{0}' is not supported",
    "vbscript.frontend.call");

  /// <summary>Registered diagnostic descriptor for <see cref="UnresolvedSymbolCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnresolvedSymbol = new(
    UnresolvedSymbolCode,
    DiagnosticSeverity.Error,
    ExitCode.InvalidSource,
    "unresolved VBScript symbol '{0}'",
    "vbscript.frontend.symbol");

  /// <summary>Registered diagnostic descriptor for <see cref="InvalidContractCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor InvalidContract = new(
    InvalidContractCode,
    DiagnosticSeverity.Error,
    ExitCode.InvalidSource,
    "invalid VBScript contract: {0}",
    "vbscript.frontend.contract");

  /// <summary>Parses and lowers a VBScript 5.8 source string into a verified PolyIR module.</summary>
  /// <param name="fileName">The logical file path used in diagnostics and module naming.</param>
  /// <param name="source">The VBScript 5.8 source code.</param>
  /// <param name="options">Optional import options such as explicit type contracts.</param>
  /// <returns>The imported verified module, or diagnostics describing any errors.</returns>
  public static VBScriptImportResult Import(string fileName, string source, VBScriptImportOptions? options = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(source);

    var diagnostics = new List<Diagnostic>();
    var tokens = VBScriptLexer.Tokenize(fileName, source, diagnostics);
    if (diagnostics.Count > 0)
      return new(null, diagnostics);

    var program = VBScriptParser.Parse(fileName, tokens, diagnostics);
    if (diagnostics.Count > 0)
      return new(null, diagnostics);

    var effective = options ?? VBScriptImportOptions.Default;
    var module = VBScriptLowerer.Lower(fileName, program, effective, diagnostics);
    if (diagnostics.Count > 0 || module is null)
      return new(null, diagnostics);

    var verifyErrors = IrVerifier.Verify(module);
    if (verifyErrors.Count > 0)
      return new(null, verifyErrors);

    return new(module, []);
  }

}
