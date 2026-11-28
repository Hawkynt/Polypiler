using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Frontends.Perl.Ast;
using Polypiler.Core.Frontends.Perl.Lowering;
using Polypiler.Core.Frontends.Perl.Syntax;
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

/// <summary>What the parser decides of a Perl file.</summary>
public enum PerlVerdict {
  /// <summary>The file is valid Perl.</summary>
  Valid,
  /// <summary>The file is not valid Perl.</summary>
  Invalid,
  /// <summary>The file uses a construct the parser does not decide.</summary>
  Unsupported
}

/// <summary>The result of parsing a Perl file.</summary>
/// <param name="Verdict">Whether the file is valid Perl, invalid, or outside the parser.</param>
/// <param name="Diagnostics">The syntax error or the refused construct.</param>
/// <param name="Outline">The subs the file defines and the lexicals of each pad, one unit per line; empty unless valid.</param>
public sealed record PerlParseResult(PerlVerdict Verdict, IReadOnlyList<Diagnostic> Diagnostics, string Outline);

/// <summary>
/// Imports verified Perl 5 subroutines and modules into Polypiler's structured typed IR. The
/// frontend lexes and parses Perl itself (<see cref="Syntax.PerlParser"/>) and starts no process;
/// perl is only the oracle its layer test compares it with.
/// </summary>
public static class PerlFrontend {

  /// <summary>The source is not valid Perl, or its contracts are malformed.</summary>
  public const string CompileFailedCode = "PLY1700";

  /// <summary>A construct the parser does not decide.</summary>
  public const string ParseUnsupportedCode = "PLY1701";

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
    "Perl {0}", "perl.frontend.compile");

  /// <summary>Registered diagnostic descriptor for <see cref="ParseUnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ParseUnsupported = new(ParseUnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct,
    "Perl construct outside the parser: {0}", "perl.frontend.parse");

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

  /// <summary>Parses a Perl file and gives the verdict, the diagnostic and the outline of what it defines.</summary>
  /// <param name="file">The logical file path used in diagnostics.</param>
  /// <param name="source">The Perl source.</param>
  /// <returns>The verdict: valid, invalid with the syntax error, or outside the parser with the construct it does not decide.</returns>
  public static PerlParseResult Parse(string file, string source) {
    ArgumentNullException.ThrowIfNull(file);
    ArgumentNullException.ThrowIfNull(source);
    var (document, _, diagnostic) = ParseDocument(file, source);
    if (document is null)
      return new(diagnostic!.Code == CompileFailedCode ? PerlVerdict.Invalid : PerlVerdict.Unsupported, [diagnostic], "");
    return new(PerlVerdict.Valid, [], PerlOutline.Of(document));
  }

  private static (PerlDocument? Document, IReadOnlyDictionary<string, PerlExpr?> Constants, Diagnostic? Diagnostic) ParseDocument(string file, string source) {
    try {
      var (document, constants) = PerlParser.ParseWithConstants(file, source);
      return (document, constants, null);
    } catch (PerlSyntaxException error) {
      return (null, new Dictionary<string, PerlExpr?>(), Diagnostic.Create(CompileFailed, new(file, error.Line, 1, error.Line, 1), "syntax error: " + StripPrefix(error.Message)));
    } catch (PerlUnsupportedException error) {
      return (null, new Dictionary<string, PerlExpr?>(), Diagnostic.Create(ParseUnsupported, new(file, error.Line, 1, error.Line, 1), error.Construct));
    }
  }

  private static string StripPrefix(string message) => message.StartsWith("syntax error: ", StringComparison.Ordinal) ? message[14..] : message;

  /// <summary>
  /// The shape of the op tree perl builds for one expression, as the parser sees the expression:
  /// every operator as <c>(name operand ...)</c> under the name of perl's op, package variables as
  /// <c>$name</c>, constants as their value; <c>error: ...</c> if it is not valid Perl. The layer test
  /// compares it with perl's own op tree, which checks precedence, associativity and how much named
  /// unary and list operators take.
  /// </summary>
  /// <param name="expression">One Perl expression.</param>
  /// <returns>The shape.</returns>
  public static string ExpressionShape(string expression) {
    ArgumentNullException.ThrowIfNull(expression);
    var (document, _, diagnostic) = ParseDocument("expression.pl", "$Polypiler::Oracle::result = (" + expression + "\n);");
    if (document is null)
      return "error: " + diagnostic!.Message;
    var value = ((PerlAssign)((PerlExpressionStatement)document.Statements[0]).Expression).Value;
    return PerlOpShape.Of(value);
  }

  /// <summary>Parses a Perl 5 source string and imports it into PolyIR.</summary>
  /// <param name="file">The logical file path used in diagnostics and module naming.</param>
  /// <param name="source">The Perl 5 source code.</param>
  /// <param name="options">Import options including contracts.</param>
  /// <returns>The imported module or diagnostics.</returns>
  public static PerlImportResult Import(string file, string source, PerlImportOptions? options = null) {
    ArgumentNullException.ThrowIfNull(file);
    ArgumentNullException.ThrowIfNull(source);
    var effective = options ?? PerlImportOptions.Default;
    var (document, constants, diagnostic) = ParseDocument(file, source);
    if (document is null)
      return new(null, [diagnostic!], [], [], []);
    return PerlLowerer.LowerTree(file, document, constants, effective);
  }

}
