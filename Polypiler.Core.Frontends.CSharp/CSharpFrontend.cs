using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Polypiler.Core.IR;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>What importing a source file produced: a module, or the reasons there is none.</summary>
/// <param name="Module">The imported module, or <see langword="null"/> when there are diagnostics.</param>
/// <param name="Diagnostics">Why the source was refused; empty when <paramref name="Module"/> is set.</param>
public sealed record ImportResult(IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Imports the prototype subset of C#: one static class of static methods over the integer types of
/// 8 to 64 bits, <c>char</c>, <c>float</c>, <c>double</c>, <c>bool</c> and one-dimensional arrays of
/// the numeric types.
/// </summary>
public static class CSharpFrontend {

  /// <summary>The code for an error the C# compiler reports.</summary>
  public const string CompilerError = "PLY1000";

  /// <summary>The code for a construct outside the supported subset.</summary>
  public const string Unsupported = "PLY1002";

  /// <summary>The code for a constant expression that overflows where overflow is checked.</summary>
  public const string ConstantOverflow = "PLY1003";

  private static readonly Lazy<MetadataReference> _coreLibrary = new(LoadCoreLibrary);

  /// <summary>Imports one C# source file.</summary>
  /// <param name="path">The name of the file, used in diagnostics.</param>
  /// <param name="source">The text of the file.</param>
  /// <returns>The module, or the diagnostics that explain why there is none.</returns>
  public static ImportResult Import(string path, string source) => Import(path, source, false);

  /// <summary>Imports one C# source file under a given default for overflow checking.</summary>
  /// <param name="path">The name of the file, used in diagnostics.</param>
  /// <param name="source">The text of the file.</param>
  /// <param name="checkedByDefault">
  /// Whether arithmetic outside any <c>checked</c> or <c>unchecked</c> traps on overflow, as the
  /// compiler option <c>CheckForOverflowUnderflow</c> makes it.
  /// </param>
  /// <returns>The module, or the diagnostics that explain why there is none.</returns>
  public static ImportResult Import(string path, string source, bool checkedByDefault) {
    var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path);
    var compilation = CSharpCompilation.Create(
      "Imported",
      [tree],
      [_coreLibrary.Value],
      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, checkOverflow: checkedByDefault));

    var errors = compilation.GetDiagnostics()
      .Where(diagnostic => diagnostic.Severity == RoslynSeverity.Error)
      .Select(diagnostic => {
        var position = diagnostic.Location.GetLineSpan().StartLinePosition;
        return new Diagnostic(
          diagnostic.Id is "CS0220" or "CS0221" ? ConstantOverflow : CompilerError,
          DiagnosticSeverity.Error,
          $"{diagnostic.Id}: {diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}",
          path,
          position.Line + 1,
          position.Character + 1);
      })
      .ToList();
    if (errors.Count > 0)
      return new(null, errors);

    return new Lowering(path, compilation.GetSemanticModel(tree)).Run(tree.GetCompilationUnitRoot());
  }

  /// <summary>
  /// The core library of the running process as a compiler reference. It is read from memory rather
  /// than from a file, so it is also there in a single-file executable.
  /// </summary>
  private static unsafe MetadataReference LoadCoreLibrary() {
    typeof(object).Assembly.TryGetRawMetadata(out var blob, out var length);
    return AssemblyMetadata.Create(ModuleMetadata.CreateFromMetadata((nint)blob, length)).GetReference();
  }

}
