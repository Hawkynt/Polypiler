using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Go.Ast;
using Polypiler.Core.Frontends.Go.Checking;
using Polypiler.Core.Frontends.Go.Lowering;
using Polypiler.Core.Frontends.Go.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Go;

/// <summary>A point where the imported code is known to behave differently from the Go it came from.</summary>
/// <param name="Id">The stable name of the difference, for example <c>go-float-to-int</c>.</param>
/// <param name="Description">What Go does and what the imported code does instead.</param>
public sealed record GoCaveat(string Id, string Description);

/// <summary>How a Go source is imported.</summary>
public sealed record GoImportOptions {

  /// <summary>
  /// The language version of Go 1 the source is written for, its minor version: 22 and later give every
  /// pass of a three-clause loop its own loop variables and range over integers, 21 and later have
  /// <c>min</c>, <c>max</c> and <c>clear</c>, 26 and later <c>new</c> of an expression.
  /// </summary>
  public int LanguageMinor { get; init; } = 27;

}

/// <summary>What importing a Go source produced: a module, or the reasons there is none.</summary>
/// <param name="Module">The imported module, or <see langword="null"/> when there are diagnostics.</param>
/// <param name="Diagnostics">Why the source was refused; empty when <paramref name="Module"/> is set.</param>
/// <param name="Caveats">The known differences of the module, each at most once; empty without a module.</param>
public sealed record GoImportResult(IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<GoCaveat> Caveats);

/// <summary>What the type checker decided of a source.</summary>
public enum GoVerdict {

  /// <summary>The source is valid Go: it parses and type-checks.</summary>
  Valid,

  /// <summary>The source is no valid Go: Go refuses it with the error of the diagnostic.</summary>
  Invalid,

  /// <summary>The source uses a construct the checker does not model, so it gives no verdict.</summary>
  Unsupported

}

/// <summary>A constant declared at the package level, with the type and the exact value the checker gives it.</summary>
/// <param name="Name">The name.</param>
/// <param name="Type">The type, as <c>types.TypeString</c> writes it, for example <c>untyped float</c>.</param>
/// <param name="Value">The exact value, as <c>constant.Value.ExactString</c> writes it.</param>
public sealed record GoConstantValue(string Name, string Type, string Value);

/// <summary>What checking a Go source found.</summary>
/// <param name="Verdict">Whether the source is valid Go, invalid, or outside what the checker models.</param>
/// <param name="Diagnostics">The error of an invalid source, or what is outside the checker; empty for a valid one.</param>
/// <param name="Constants">The package-level constants of a valid source.</param>
public sealed record GoCheckResult(GoVerdict Verdict, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<GoConstantValue> Constants) {

  /// <summary>Whether the source parses: false for a syntax error, or a source nested deeper than the parser follows.</summary>
  public bool Parsed { get; init; } = true;

}

/// <summary>
/// Imports Go: the functions of one package file and the calls between them, the sized integer types
/// with Go's wrap-around, its truncating division that panics on zero and wraps the smallest value
/// divided by -1, its shifts that are not masked - a count of the width or more gives 0 or the sign,
/// a negative one panics - its exact untyped constants, <c>float32</c> and <c>float64</c> without
/// fusion, <c>bool</c>, slices that are not resliced, struct types, <c>if</c>, every form of
/// <c>for</c> with <c>range</c> over an integer or a slice, <c>switch</c>, <c>break</c> and
/// <c>continue</c>, the built-ins <c>len</c>, <c>make</c>, <c>min</c>, <c>max</c> and <c>panic</c>, and the
/// exact functions of <c>math</c> and <c>math/bits</c>. The source is parsed by the frontend's own
/// scanner and parser (<see cref="GoParser"/>) and type-checked by its own checker
/// (<see cref="GoChecker"/>), without any Go installed: types and constant values are the checker's,
/// and whatever Go refuses - a syntax error, an unused variable or import, a mismatched type, a
/// constant that overflows - is refused with <see cref="CheckError"/>. Everything outside the subset
/// is refused with a diagnostic that names the construct.
/// </summary>
public static class GoFrontend {

  /// <summary>The code for an error of Go: the source does not parse or does not type-check.</summary>
  public const string CheckError = "PLY2100";

  /// <summary>The registered diagnostic of <see cref="CheckError"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor CheckErrorDescriptor = new(CheckError, DiagnosticSeverity.Error, ExitCode.InvalidSource, "{0}", "go.check");

  /// <summary>The code for a type the frontend does not import.</summary>
  public const string UnsupportedType = "PLY2101";

  /// <summary>The registered diagnostic of <see cref="UnsupportedType"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedTypeDescriptor = new(UnsupportedType, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "go.frontend.type");

  /// <summary>The code for a construct the frontend does not import.</summary>
  public const string Unsupported = "PLY2102";

  /// <summary>The registered diagnostic of <see cref="Unsupported"/>.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedDescriptor = new(Unsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "go.frontend.construct");

  /// <summary>The caveat of a conversion of a floating-point number to an integer, which Go leaves to the implementation outside the range of the type.</summary>
  public static GoCaveat FloatToInteger { get; } = new("go-float-to-int",
    "Go leaves the conversion of a floating-point number outside the range of an integer type to the implementation; the imported code saturates, a NaN becoming 0");

  /// <summary>The caveat of a floating-point sum of a product, which Go may fuse into one rounding.</summary>
  public static GoCaveat Fusion { get; } = new("go-fma-unfused",
    "Go permits an implementation to fuse x*y + z into one rounding; the imported code rounds the product first, as Go does on amd64 and wherever the product is converted explicitly");

  /// <summary>Imports a source.</summary>
  /// <param name="path">The name of the file, which the diagnostics carry.</param>
  /// <param name="source">The text of the file.</param>
  /// <param name="options">How to import it; the defaults when <see langword="null"/>.</param>
  /// <returns>The module, or the diagnostics.</returns>
  public static GoImportResult Import(string path, string source, GoImportOptions? options = null) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(source);
    options ??= new GoImportOptions();
    var (file, checker, diagnostic) = Analyze(path, source, options);
    if (diagnostic is not null)
      return new(null, [diagnostic], []);

    try {
      var lowerer = new Lowerer(LanguageMinor(file!, options));
      var module = lowerer.Module(file!, checker!.PackageName);
      var problems = IrVerifier.Verify(module);
      if (problems.Count > 0)
        return new(null, [UnsupportedDescriptor.At($"the imported module is no valid IR: {problems[0].Message}", path)], []);

      return new(module, [], lowerer.Caveats);
    } catch (GoUnsupportedException exception) {
      return new(null, [exception.Descriptor.At(exception.Message, path, exception.Line, exception.Column)], []);
    }
  }

  /// <summary>Parses and type-checks a source without importing it: the verdict and the package-level constants.</summary>
  /// <param name="path">The name of the file, which the diagnostics carry.</param>
  /// <param name="source">The text of the file.</param>
  /// <param name="options">The language version; the defaults when <see langword="null"/>.</param>
  /// <returns>The verdict, the diagnostic of an invalid or unsupported source, and the constants of a valid one.</returns>
  public static GoCheckResult Check(string path, string source, GoImportOptions? options = null) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(source);
    var (file, checker, diagnostic) = Analyze(path, source, options ?? new GoImportOptions());
    if (diagnostic is not null)
      return new(diagnostic.Code == CheckError ? GoVerdict.Invalid : GoVerdict.Unsupported, [diagnostic], []) { Parsed = file is not null };

    var constants = checker!.Constants.Where(constant => constant.Name != "_")
      .Select(constant => new GoConstantValue(constant.Name, constant.Type!.ToString(), constant.Value!.ExactString)).ToList();
    return new(GoVerdict.Valid, [], constants);
  }

  /// <summary>The language version of a file: the one its <c>//go:build</c> line implies, go1.21 at the least, as go/types gives it; otherwise the one of the options.</summary>
  private static int LanguageMinor(GoNode file, GoImportOptions options)
    => file.Text("GoVersion") is { } version ? Math.Max(int.Parse(version, System.Globalization.CultureInfo.InvariantCulture), 21) : options.LanguageMinor;

  private static (GoNode? File, GoChecker? Checker, Diagnostic? Diagnostic) Analyze(string path, string source, GoImportOptions options) {
    var text = new GoSource(source);
    GoNode file;
    try {
      file = GoParser.ParseFile(text);
    } catch (GoSyntaxException exception) {
      var (line, column) = text.Position(exception.Offset);
      return (null, null, CheckErrorDescriptor.At($"Go syntax error: {exception.Message}", path, line, column));
    } catch (GoNestingException exception) {
      var (line, column) = text.Position(exception.Offset);
      return (null, null, UnsupportedDescriptor.At($"'{exception.Message}' is not supported by the Go frontend", path, line, column));
    }

    try {
      return (file, GoChecker.Check(file, LanguageMinor(file, options)), null);
    } catch (GoCheckException exception) {
      return (file, null, CheckErrorDescriptor.At($"Go type error: {exception.Message}", path, exception.Node?.Line ?? 1, exception.Node?.Column ?? 1));
    } catch (GoUnsupportedException exception) {
      return (file, null, exception.Descriptor.At(exception.Message, path, exception.Line, exception.Column));
    }
  }

}
