using System;
using System.Collections.Generic;
using Polypiler.Core.Frontends.VisualBasic.Lowering;
using Polypiler.Core.Frontends.VisualBasic.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VisualBasic;

/// <summary>The result of importing a classic Visual Basic standard module.</summary>
/// <param name="Module">The imported, verified IR module, or <see langword="null"/> on failure.</param>
/// <param name="Diagnostics">What the parser and the lowering reported.</param>
public sealed record VisualBasicImportResult(IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Imports a classic Visual Basic (VB6 / VBA 6 / VBA 7) standard module (<c>.bas</c>) into the IR,
/// with the semantics of the language reference and [MS-VBAL]: <c>Integer</c> is 16 bits, <c>Long</c>
/// 32, <c>LongLong</c> 64, <c>Byte</c> unsigned 8; every integer result outside its type and every
/// floating-point result that is infinite or no number raises run-time error 6 (11 for a division by
/// zero); <c>CInt</c>, <c>CLng</c>, <c>CByte</c> and the implicit conversion of a floating-point
/// number round half to even; <c>\</c> and <c>Mod</c> round their operands to integers and truncate;
/// <c>And</c> and <c>Or</c> evaluate both operands; arrays start at the lower bound they are declared
/// with, <c>Option Base</c> by default; a parameter is passed by reference unless declared
/// <c>ByVal</c>. <c>On Error GoTo</c> with a handler at the end of the procedure, <c>On Error Resume
/// Next</c> and <c>Err.Number</c>/<c>Err.Clear</c>/<c>Err.Raise</c> become the IR's exceptions. What
/// the subset does not hold - <c>Variant</c>, <c>Currency</c>, objects, class modules, <c>GoSub</c>,
/// a parameter passed by reference and assigned - is refused by name.
/// </summary>
public static class VisualBasicFrontend {

  /// <summary>The code of a syntax error.</summary>
  public const string SyntaxErrorCode = "PLY2645";

  /// <summary>The code of a construct outside the subset.</summary>
  public const string UnsupportedCode = "PLY2646";

  /// <summary>The code of a name that names nothing, or an operand of the wrong type.</summary>
  public const string UnresolvedCode = "PLY2648";

  /// <summary>The registered diagnostic of <see cref="SyntaxErrorCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor SyntaxError = new(SyntaxErrorCode, DiagnosticSeverity.Error, ExitCode.InvalidSource, "the classic Visual Basic parser reports: {0}", "visualbasic.frontend.syntax");

  /// <summary>The registered diagnostic of <see cref="UnsupportedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unsupported = new(UnsupportedCode, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "'{0}' is not supported by the classic Visual Basic profile", "visualbasic.frontend.construct");

  /// <summary>The registered diagnostic of <see cref="UnresolvedCode"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Unresolved = new(UnresolvedCode, DiagnosticSeverity.Error, ExitCode.InvalidSource, "{0}", "visualbasic.frontend.symbol");

  /// <summary>
  /// The semantics table of classic Visual Basic (ADR 0300 decision 4): the kinds a <c>Variant</c>
  /// operation gives, its errors and the typed IR of the pairs that have one, which the shared
  /// de-dynamicization reads for a module whose <see cref="IrModule.DynamicSemantics"/> names it.
  /// </summary>
  public static Polypiler.Core.Analysis.Dynamic.IDynamicSemantics Semantics => VbaLowerer.Semantics.Instance;

  /// <summary>Parses and lowers a standard module.</summary>
  /// <param name="fileName">The file name, which names the module when it has no <c>Attribute VB_Name</c>.</param>
  /// <param name="source">The text of the module.</param>
  /// <param name="options">The locale the module runs under; the default one for <see langword="null"/>.</param>
  /// <returns>The verified module, or the diagnostics of why there is none.</returns>
  public static VisualBasicImportResult Import(string fileName, string source, VisualBasicImportOptions? options = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(source);
    VbaModule parsed;
    try {
      parsed = VbaParser.Parse(source);
    } catch (VbaSyntaxException exception) {
      return new(null, [Report(SyntaxError, fileName, exception.Line, exception.Message)]);
    } catch (VbaUnsupportedException exception) {
      return new(null, [Report(Unsupported, fileName, exception.Line, exception.Construct)]);
    }

    var diagnostics = new List<Diagnostic>();
    var module = new VbaLowerer(fileName, diagnostics, (options ?? new()).Locale).Lower(parsed);
    if (diagnostics.Count > 0 || module is null)
      return new(null, diagnostics);

    var problems = IrVerifier.Verify(module);
    return problems.Count > 0 ? new(null, problems) : new(module, []);
  }

  /// <summary>A diagnostic of a descriptor at a line of the file, its one argument formatted into the message.</summary>
  internal static Diagnostic Report(DiagnosticDescriptor descriptor, string file, int line, string argument)
    => Diagnostic.Create(descriptor, line > 0 ? new SourceRange(file, line, 1, line, 1) : null, argument);

}
