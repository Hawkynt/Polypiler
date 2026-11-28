using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Frontends.Kotlin.Lowering;
using Polypiler.Core.Frontends.Kotlin.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Kotlin;

/// <summary>What an import of a Kotlin file gives: the module, or the diagnostics of why there is none.</summary>
/// <param name="Module">The module, or <see langword="null"/> when the source is refused.</param>
/// <param name="Diagnostics">The diagnostics, in source order.</param>
public sealed record KotlinImportResult(IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// The Kotlin frontend (#414): imports a Kotlin file for the JVM into the IR. The parser is this
/// repository's recursive-descent parser after the grammar of the Kotlin language specification, so
/// that an import needs no JVM; <c>kotlinc</c> is the oracle the tests compare with. The subset is
/// that of the first verified families: the numbers with Kotlin's semantics (<c>Int</c> and
/// <c>Long</c> wrap, <c>/</c> truncates and throws on a zero divisor, the smallest value divided by
/// -1 wraps, shifts mask the count), the unsigned types, conversions, <c>if</c>, <c>when</c>,
/// <c>while</c>, <c>do</c>-<c>while</c>, <c>for</c> over ranges, progressions, arrays and strings,
/// labelled jumps, top-level functions, constants and properties, primitive arrays and strings. A
/// construct outside it is refused with a code of its family: <c>PLY1600</c> a syntax error,
/// <c>PLY1601</c> a number or operator, <c>PLY1610</c> control flow, <c>PLY1620</c> arrays and
/// strings, <c>PLY1630</c> functions and names, <c>PLY1640</c> declared types.
/// </summary>
public static class KotlinFrontend {

  /// <summary>The code of a syntax error.</summary>
  public const string SyntaxErrorCode = "PLY1600";

  /// <summary>The code of a number, an operator or a conversion outside the subset.</summary>
  public const string ScalarCode = "PLY1601";

  /// <summary>The code of a control construct outside the subset.</summary>
  public const string ControlCode = "PLY1610";

  /// <summary>The code of an array or string construct outside the subset.</summary>
  public const string MemoryCode = "PLY1620";

  /// <summary>The code of a function, a call or a name outside the subset or unresolved.</summary>
  public const string FunctionCode = "PLY1630";

  /// <summary>The code of a declared type outside the subset: a class, an interface, an object, a nullable type.</summary>
  public const string TypeCode = "PLY1640";

  /// <summary>The registered diagnostic of <see cref="SyntaxErrorCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor SyntaxError = new(SyntaxErrorCode, DiagnosticSeverity.Error, ExitCode.InvalidSource, "Kotlin syntax error: {0}", "kotlin.frontend.syntax");

  /// <summary>The registered diagnostic of <see cref="ScalarCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Scalar = new(ScalarCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "kotlin.frontend.scalar");

  /// <summary>The registered diagnostic of <see cref="ControlCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Control = new(ControlCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "kotlin.frontend.control");

  /// <summary>The registered diagnostic of <see cref="MemoryCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Memory = new(MemoryCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "kotlin.frontend.memory");

  /// <summary>The registered diagnostic of <see cref="FunctionCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Function = new(FunctionCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "kotlin.frontend.function");

  /// <summary>The registered diagnostic of <see cref="TypeCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Type = new(TypeCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "kotlin.frontend.type");

  /// <summary>Imports a Kotlin file.</summary>
  /// <param name="fileName">The file's name, which names the module.</param>
  /// <param name="source">Its text.</param>
  /// <returns>The module, or the diagnostics of why there is none.</returns>
  public static KotlinImportResult Import(string fileName, string source) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(source);
    KFile file;
    try {
      file = KotlinParser.Parse(source);
    } catch (KotlinSyntaxException exception) {
      return new(null, [Diagnostic.Create(SyntaxError, new SourceRange(fileName, exception.At.Line, exception.At.Column, exception.At.Line, exception.At.Column), exception.Reason)]);
    }

    var lowerer = new KotlinLowerer(fileName, ModuleName(fileName));
    var module = lowerer.Lower(file);
    if (lowerer.Diagnostics.Count > 0)
      return new(null, lowerer.Diagnostics);

    var problems = IrVerifier.Verify(module);
    return problems.Count > 0 ? new(null, problems) : new(module, []);
  }

  /// <summary>The name of the module of a file: its name without extension, every character that cannot be in an identifier replaced by <c>_</c>, the first letter in upper case.</summary>
  /// <param name="fileName">The file's name.</param>
  /// <returns>The name.</returns>
  public static string ModuleName(string fileName) {
    ArgumentNullException.ThrowIfNull(fileName);
    var stem = new string([.. Path.GetFileNameWithoutExtension(fileName).Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_')]);
    if (stem.Length == 0 || char.IsAsciiDigit(stem[0]))
      stem = "Kotlin" + stem;

    return char.ToUpperInvariant(stem[0]) + stem[1..];
  }

}
