using System;
using System.Collections.Generic;
using Polypiler.Core.Frontends.Go.Ast;
using Polypiler.Core.Frontends.Go.AstDump;
using Polypiler.Core.Frontends.Go.Lowering;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Go;

/// <summary>A point where the imported code is known to behave differently from the Go it came from.</summary>
/// <param name="Id">The stable name of the difference, for example <c>go-float-to-int</c>.</param>
/// <param name="Description">What Go does and what the imported code does instead.</param>
public sealed record GoCaveat(string Id, string Description);

/// <summary>How a Go source is imported.</summary>
public sealed record GoImportOptions {

  /// <summary>The <c>go</c> command that builds the helper, the only one tried; <see langword="null"/> for <c>POLYPILER_GO</c>, then <c>go</c>.</summary>
  public string? Toolchain { get; init; }

  /// <summary>The language version of Go 1 the source is written for, its minor version; 22 and later give every pass of a three-clause loop its own loop variables.</summary>
  public int LanguageMinor { get; init; } = 27;

  /// <summary>The printed bound tree of the source, when one was recorded: the helper does not run.</summary>
  public string? Dump { get; init; }

}

/// <summary>What importing a Go source produced: a module, or the reasons there is none.</summary>
/// <param name="Module">The imported module, or <see langword="null"/> when there are diagnostics.</param>
/// <param name="Diagnostics">Why the source was refused; empty when <paramref name="Module"/> is set.</param>
/// <param name="Caveats">The known differences of the module, each at most once; empty without a module.</param>
public sealed record GoImportResult(IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<GoCaveat> Caveats) {

  /// <summary>The version of Go that checked the source; empty when none did.</summary>
  public string Go { get; init; } = "";

}

/// <summary>
/// Imports Go: the functions of one package file and the calls between them, the sized integer types
/// with Go's wrap-around, its truncating division that panics on zero and wraps the smallest value
/// divided by -1, its shifts that are not masked - a count of the width or more gives 0 or the sign,
/// a negative one panics - its exact untyped constants, <c>float32</c> and <c>float64</c> without
/// fusion, <c>bool</c>, slices that are not resliced, struct types, <c>if</c>, every form of
/// <c>for</c> with <c>range</c> over an integer or a slice, <c>switch</c>, <c>break</c> and
/// <c>continue</c>, the built-ins <c>len</c>, <c>make</c>, <c>min</c>, <c>max</c> and <c>panic</c>, and the
/// exact functions of <c>math</c> and <c>math/bits</c>. The source is parsed by <c>go/parser</c> and
/// type-checked by <c>go/types</c> of the installed Go through a helper that only prints the bound tree
/// as JSON (<see cref="GoHelper"/>): types and constant values are the type checker's, and whatever it
/// refuses - an unused variable or import, a mismatched type, a constant that overflows - is refused
/// with <see cref="CheckError"/>. Everything outside the subset is refused with a diagnostic that names
/// the construct.
/// </summary>
public static class GoFrontend {

  /// <summary>The code for an error of the Go parser or type checker, or a helper that fails.</summary>
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
    var json = options.Dump;
    if (json is null) {
      var (printed, failure) = GoHelper.Run(path, source, options.Toolchain);
      if (printed is null)
        return new(null, [CheckErrorDescriptor.At($"the Go helper failed: {failure}", path)], []);

      json = printed;
    }

    var parsed = DumpReader.Read(json);
    if (parsed.Error is { } error)
      return new(null, [CheckErrorDescriptor.At($"Go {error.Kind} error: {error.Message}", path, error.Line, error.Column)], []) { Go = parsed.Go };

    try {
      var lowerer = new Lowerer(options.LanguageMinor);
      var module = lowerer.Module(parsed.File!);
      var problems = IrVerifier.Verify(module);
      if (problems.Count > 0)
        return new(null, [UnsupportedDescriptor.At($"the imported module is no valid IR: {problems[0].Message}", path)], []) { Go = parsed.Go };

      return new(module, [], lowerer.Caveats) { Go = parsed.Go };
    } catch (GoUnsupportedException exception) {
      return new(null, [exception.Descriptor.At(exception.Message, path, exception.Line, exception.Column)], []) { Go = parsed.Go };
    }
  }

  /// <summary>The bound tree the helper prints for a source, as the frontend reads it, or why it printed none: what a recorded dump holds.</summary>
  /// <param name="path">The name of the file.</param>
  /// <param name="source">The text.</param>
  /// <param name="toolchain">The <c>go</c> command, or <see langword="null"/> for <c>POLYPILER_GO</c>, then <c>go</c>.</param>
  /// <returns>The JSON, or the failure.</returns>
  public static (string? Json, string? Failure) Print(string path, string source, string? toolchain = null) => GoHelper.Run(path, source, toolchain);

  /// <summary>The text of the helper the frontend builds and runs, for a test that records its output.</summary>
  public static string HelperSource => GoHelper.Source;

}
