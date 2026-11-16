using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// Generated C# with native calls (#1332) as the C# compiler builds it: a file with <c>[LibraryImport]</c> is compiled
/// with the interop source generator of the reference pack of its framework,
/// <c>analyzers/dotnet/cs/Microsoft.Interop.LibraryImportGenerator.dll</c>, as a project of that framework runs it; every
/// other file as <see cref="GeneratedCSharp"/> compiles it. The generators of two packs load into contexts of their
/// own, so that both can be used in one run.
/// </summary>
internal static class InteropBuild {

  /// <summary>The version of <c>Microsoft.NETCore.App.Ref</c> whose generator a profile runs; the running runtime takes that of net10.0.</summary>
  private static string PackOf(string profile) => profile == "net8.0" ? "8.0.0" : "10.0.0";

  private static readonly ConcurrentDictionary<string, Lazy<ISourceGenerator>> _generators = new();

  private static readonly ConcurrentDictionary<string, Lazy<Type>> _builds = new();

  /// <summary>The interop source generator of a reference pack.</summary>
  private static ISourceGenerator Generator(string pack) => _generators.GetOrAdd(pack, key => new(() => {
    var directory = Path.Combine(ReferenceAssemblies.PackageRoot, "microsoft.netcore.app.ref", key, "analyzers", "dotnet", "cs");
    var assembly = new PackContext(directory).LoadFromAssemblyPath(Path.Combine(directory, "Microsoft.Interop.LibraryImportGenerator.dll"));
    // The code fixes of the assembly need the workspaces of the IDE, which no compiler loads: their types are left out.
    Type?[] types;
    try {
      types = assembly.GetTypes();
    } catch (ReflectionTypeLoadException partial) {
      types = partial.Types;
    }

    var type = types.OfType<Type>().Single(candidate => candidate.Name == "LibraryImportGenerator");
    return ((IIncrementalGenerator)Activator.CreateInstance(type)!).AsSourceGenerator();
  })).Value;

  /// <summary>The compilation with the generator's output where the file asks for it.</summary>
  private static Compilation Generated(CSharpCompilation compilation, string profile) {
    if (!compilation.SyntaxTrees.Any(tree => tree.ToString().Contains("LibraryImport(", StringComparison.Ordinal)))
      return compilation;

    CSharpGeneratorDriver.Create([Generator(PackOf(profile))], parseOptions: (CSharpParseOptions)compilation.SyntaxTrees[0].Options)
      .RunGeneratorsAndUpdateCompilation(compilation, out var generated, out _);
    return generated;
  }

  /// <summary>
  /// What the compiler of a profile says about a file: in the profile's language version, against its reference
  /// assemblies, with the generator where the file has <c>[LibraryImport]</c>, every warning an error.
  /// </summary>
  public static IReadOnlyList<string> Complaints(string source, string profile, string languageVersion) {
    LanguageVersionFacts.TryParse(languageVersion, out var version);
    var compilation = CSharpCompilation.Create(
      "Generated" + Guid.NewGuid().ToString("N"),
      [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(version))],
      ReferenceAssemblies.Of(profile),
      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, generalDiagnosticOption: ReportDiagnostic.Error, warningLevel: 9999, allowUnsafe: true));
    using var image = new MemoryStream();
    return [.. Generated(compilation, profile).Emit(image).Diagnostics.Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning).Select(diagnostic => diagnostic.ToString())];
  }

  /// <summary>
  /// A file compiled for the running runtime, with the generator where it has <c>[LibraryImport]</c>, and loaded: the
  /// static class it declares, whose native calls call the real C library of the operating system.
  /// </summary>
  /// <exception cref="InvalidOperationException">The compiler reported an error or a warning.</exception>
  public static Type Compile(string source) => _builds.GetOrAdd(source, text => new(() => {
    var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
      .Where(path => Path.GetFileName(path) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "System.Runtime.InteropServices.dll")
      .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));
    var compilation = CSharpCompilation.Create(
      "Generated" + Guid.NewGuid().ToString("N"),
      [CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest))],
      references,
      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, warningLevel: 9999, allowUnsafe: true));
    using var image = new MemoryStream();
    var complaints = Generated(compilation, "net10.0").Emit(image).Diagnostics.Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning).ToList();
    if (complaints.Count > 0)
      throw new InvalidOperationException($"The C# compiler does not accept the generated file without complaint:\n{string.Join('\n', complaints)}\n{text}");

    return Assembly.Load(image.ToArray()).GetExportedTypes().Single(type => type is { IsAbstract: true, IsSealed: true });
  })).Value;

  /// <summary>The assemblies of one reference pack's analyzers; the compiler's own come from the tests.</summary>
  private sealed class PackContext(string directory) : AssemblyLoadContext($"interop-generator {directory}") {

    protected override Assembly? Load(AssemblyName name) {
      var path = Path.Combine(directory, name.Name + ".dll");
      return File.Exists(path) ? this.LoadFromAssemblyPath(path) : null;
    }

  }

}
