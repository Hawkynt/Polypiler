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

/// <summary>A C# source file to import.</summary>
/// <param name="Path">The name of the file, used in diagnostics.</param>
/// <param name="Text">The text of the file.</param>
public sealed record CSharpSource(string Path, string Text);

/// <summary>
/// Imports the prototype subset of C#: one static class of static methods over the integer types of
/// 8 to 64 bits, <c>char</c>, <c>float</c>, <c>double</c>, <c>bool</c> and one-dimensional arrays of
/// the numeric types, with <c>const</c> and <c>static readonly</c> fields as its constants.
/// </summary>
public static class CSharpFrontend {

  /// <summary>The code for an error the C# compiler reports.</summary>
  public const string CompilerError = "PLY1000";

  /// <summary>The registered diagnostic of <see cref="CompilerError"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor CompilerErrorDescriptor = new(CompilerError, DiagnosticSeverity.Error, ExitCode.InvalidSource, "{0}", "csharp.compiler");

  /// <summary>The code for a construct outside the supported subset.</summary>
  public const string Unsupported = "PLY1002";

  /// <summary>The registered diagnostic of <see cref="Unsupported"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedDescriptor = new(Unsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "csharp.subset");

  /// <summary>The code for a constant expression that overflows where overflow is checked.</summary>
  public const string ConstantOverflow = "PLY1003";

  /// <summary>The registered diagnostic of <see cref="ConstantOverflow"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor ConstantOverflowDescriptor = new(ConstantOverflow, DiagnosticSeverity.Error, ExitCode.InvalidSource, "{0}", "csharp.constant-overflow");

  /// <summary>
  /// The caveat of a module that converts a number to text or back without a format provider, which
  /// .NET does by the culture of the thread (<c>"a" + 2.5</c> is <c>a2,5</c> under de-DE): the import
  /// fixes it to the invariant culture.
  /// </summary>
  public const string InvariantCulture = "cs-invariant-culture";

  /// <summary>What <see cref="InvariantCulture"/> says.</summary>
  public const string InvariantCultureDescription =
    "a number converted to text or back without a format provider uses the culture of the thread in .NET; the imported code uses the invariant culture";

  private static readonly Lazy<MetadataReference> _coreLibrary = new(() => Loaded(typeof(object).Assembly));

  /// <summary>
  /// The collections of .NET outside the core library - <c>SortedDictionary</c>, <c>SortedSet</c>,
  /// <c>LinkedList</c> - which a source may name and the import refuses by name.
  /// </summary>
  private static readonly Lazy<MetadataReference> _collections = new(() => Loaded(typeof(System.Collections.Generic.SortedSet<>).Assembly));

  /// <summary>
  /// LINQ to Objects, and the parallel and queryable LINQ a source may name and the import refuses by
  /// name, with the facade of the runtime their signatures name the core types through.
  /// </summary>
  private static readonly Lazy<MetadataReference[]> _linq = new(() => [
    Loaded(typeof(System.Linq.Enumerable).Assembly),
    Loaded(typeof(System.Linq.ParallelEnumerable).Assembly),
    Loaded(typeof(System.Linq.Queryable).Assembly),
    Loaded(typeof(System.Linq.Expressions.Expression).Assembly),
    Loaded(typeof(System.Collections.Concurrent.Partitioner).Assembly),
    Loaded(System.Reflection.Assembly.Load("System.Runtime"))
  ]);

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
  public static ImportResult Import(string path, string source, bool checkedByDefault)
    => Import([new CSharpSource(path, source)], checkedByDefault);

  /// <summary>
  /// Imports one static class whose parts several files declare, each part <c>partial</c>: the files
  /// are compiled together, and a method may call a method or read a constant of another file.
  /// </summary>
  /// <param name="files">The files, at least one; the functions of the module follow their order.</param>
  /// <param name="checkedByDefault">Whether arithmetic outside any <c>checked</c> or <c>unchecked</c> traps on overflow.</param>
  /// <returns>The module, or the diagnostics that explain why there is none.</returns>
  public static ImportResult Import(IReadOnlyList<CSharpSource> files, bool checkedByDefault = false) => Import(files, checkedByDefault, []);

  /// <summary>
  /// Imports one static class as <see cref="Import(IReadOnlyList{CSharpSource}, bool)"/> does, with
  /// instantiations of its public generic methods that the class itself does not use: a generic
  /// method is otherwise instantiated only for the type arguments the module uses, and a public one
  /// that nothing instantiates is refused.
  /// </summary>
  /// <param name="files">The files, at least one; the functions of the module follow their order.</param>
  /// <param name="checkedByDefault">Whether arithmetic outside any <c>checked</c> or <c>unchecked</c> traps on overflow.</param>
  /// <param name="instantiations">The instantiations to lower besides those the module uses.</param>
  /// <returns>The module, or the diagnostics that explain why there is none.</returns>
  /// <exception cref="ArgumentException">An instantiation names no generic method of the class, a type that is no type, or another number of type arguments.</exception>
  public static ImportResult Import(IReadOnlyList<CSharpSource> files, bool checkedByDefault, IReadOnlyList<CSharpInstantiation> instantiations)
    => Import(files, checkedByDefault, instantiations, new Dictionary<string, IrIntrinsicOp>());

  /// <summary>
  /// Imports a library of Polypiler's own: the files as <see cref="Import(IReadOnlyList{CSharpSource}, bool)"/>
  /// imports them, the methods <paramref name="intrinsics"/> names being operations of the IR - a call of
  /// one is the operation, and the method is no function of the module.
  /// </summary>
  /// <param name="files">The files of the library, each declaring part of one static class.</param>
  /// <param name="intrinsics">The methods that stand for an operation of the IR, by their names.</param>
  /// <returns>The module, or the diagnostics of what keeps it from being imported.</returns>
  public static ImportResult ImportLibrary(IReadOnlyList<CSharpSource> files, IReadOnlyDictionary<string, IrIntrinsicOp> intrinsics)
    => Import(files, false, [], intrinsics);

  private static ImportResult Import(IReadOnlyList<CSharpSource> files, bool checkedByDefault, IReadOnlyList<CSharpInstantiation> instantiations, IReadOnlyDictionary<string, IrIntrinsicOp> intrinsics) {
    ArgumentNullException.ThrowIfNull(instantiations);
    var trees = files
      .Select(file => CSharpSyntaxTree.ParseText(file.Text, new CSharpParseOptions(LanguageVersion.Latest), file.Path))
      .ToList();
    var compilation = CSharpCompilation.Create(
      "Imported",
      trees,
      // LINQ is bound only for a source that names it: its assemblies are costs every other import would pay at startup.
      [_coreLibrary.Value, _collections.Value, .. files.Any(file => file.Text.Contains("Linq", StringComparison.Ordinal)) ? _linq.Value : []],
      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, checkOverflow: checkedByDefault, allowUnsafe: true));

    var errors = compilation.GetDiagnostics()
      .Where(diagnostic => diagnostic.Severity == RoslynSeverity.Error)
      .Select(diagnostic => {
        var position = diagnostic.Location.GetLineSpan().StartLinePosition;
        return (diagnostic.Id is "CS0220" or "CS0221" ? ConstantOverflowDescriptor : CompilerErrorDescriptor).At(
          $"{diagnostic.Id}: {diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}",
          diagnostic.Location.SourceTree?.FilePath ?? files[0].Path,
          position.Line + 1,
          position.Character + 1);
      })
      .ToList();
    if (errors.Count > 0)
      return new(null, errors);

    var parts = trees.Select(tree => (tree.FilePath, new BoundModel(compilation.GetSemanticModel(tree)), tree.GetCompilationUnitRoot())).ToList();
    return new Lowering(parts[0].FilePath, parts[0].Item2) { Requested = instantiations, Intrinsics = intrinsics }.Run(parts);
  }

  /// <summary>
  /// An assembly of the running process as a compiler reference. It is read from memory rather than
  /// from a file, so it is also there in a single-file executable.
  /// </summary>
  private static unsafe MetadataReference Loaded(System.Reflection.Assembly assembly) {
    assembly.TryGetRawMetadata(out var blob, out var length);
    return AssemblyMetadata.Create(ModuleMetadata.CreateFromMetadata((nint)blob, length)).GetReference();
  }

}
