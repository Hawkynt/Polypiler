using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Core.Recognize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.VisualBasic;

/// <summary>A module translated to Visual Basic .NET as <c>translate</c> does it, or the reasons it is not.</summary>
/// <param name="Source">The generated source file, or <see langword="null"/> when the backend refused the module.</param>
/// <param name="Project">The generated project file, or <see langword="null"/>.</param>
/// <param name="Module">The module the backend wrote, after the passes.</param>
/// <param name="Problems">The diagnostics of a refusal, as text.</param>
internal sealed record VbNetTranslation(string? Source, string? Project, IrModule? Module, IReadOnlyList<string> Problems);

/// <summary>
/// Sources through the Visual Basic .NET backend and the Visual Basic compiler, Roslyn, in memory: the
/// generated file compiled with the options of its project - <c>Option Strict On</c>,
/// <c>RemoveIntegerChecks</c>, every warning an error - and the language version of its profile.
/// </summary>
internal static class VbNetFixtures {

  /// <summary>Optimizes, legalizes, checks and emits a module.</summary>
  public static VbNetTranslation Translate(IrModule imported, VisualBasicProfile? profile = null) {
    profile ??= VisualBasicProfile.VbNetLatest;
    var optimized = Optimizer.Run(imported, new OptimizerOptions { Target = RuleTarget.Neutral }).Module;
    var legalized = VisualBasicEmitter.Legalize(optimized, () => PrecomputedImplementations.Module, profile, FloatMode.Accurate);
    var problems = VisualBasicEmitter.Check(legalized, profile);
    if (problems.Count > 0)
      return new(null, null, null, [.. problems.Select(problem => problem.ToString())]);

    var module = IrPassPipeline.Default.Run(legalized, new OptimizerOptions()).Module;
    var files = VisualBasicEmitter.Emit(module, profile, FloatMode.Accurate, null, Core.Optimize.Target.TargetPassOptions.Default).Files;
    return new(files[0].Content, files[1].Content, module, []);
  }

  /// <summary>Imports C# methods of a snippet class and translates them.</summary>
  public static VbNetTranslation Snippet(string methods, VisualBasicProfile? profile = null)
    => Translate(PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(methods)), profile);

  private static readonly Lazy<MetadataReference[]> _references = new(() => [
    .. new[] { typeof(object).Assembly, Assembly.Load("System.Runtime"), typeof(Console).Assembly, typeof(Microsoft.VisualBasic.Strings).Assembly,
      Assembly.Load("System.Collections") }.Select(assembly => MetadataReference.CreateFromFile(assembly.Location))
  ]);

  /// <summary>The language version of a profile as Roslyn names it.</summary>
  public static LanguageVersion LanguageOf(VisualBasicProfile profile) {
    var version = LanguageVersion.Default;
    return LanguageVersionFacts.TryParse(profile.LanguageVersion, ref version) ? version : throw new InvalidOperationException($"'{profile.LanguageVersion}' is no Visual Basic language version.");
  }

  /// <summary>The compiler's complaints about a source for a profile, against the references given or the running .NET's.</summary>
  public static IReadOnlyList<string> Complaints(string source, VisualBasicProfile profile, IEnumerable<MetadataReference>? references = null, bool removeIntegerChecks = true)
    => Compilation(source, profile, references, removeIntegerChecks).GetDiagnostics()
      .Where(diagnostic => diagnostic.Severity >= Microsoft.CodeAnalysis.DiagnosticSeverity.Warning || diagnostic.IsWarningAsError)
      .Select(diagnostic => diagnostic.ToString()).ToList();

  private static VisualBasicCompilation Compilation(string source, VisualBasicProfile profile, IEnumerable<MetadataReference>? references, bool removeIntegerChecks) {
    var tree = VisualBasicSyntaxTree.ParseText(source, new VisualBasicParseOptions(LanguageOf(profile)), "Generated.vb");
    var options = new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary, globalImports: GlobalImport.Parse(["Microsoft.VisualBasic", "System", "System.Collections.Generic"]),
      optionStrict: OptionStrict.On, optionExplicit: true, optionInfer: true,
      checkOverflow: !removeIntegerChecks, generalDiagnosticOption: ReportDiagnostic.Error, deterministic: true);
    return VisualBasicCompilation.Create("Generated" + Guid.NewGuid().ToString("N"), [tree], references ?? _references.Value, options);
  }

  /// <summary>Compiles a source for a profile and loads it; fails with the compiler's complaints.</summary>
  public static Assembly Load(string source, VisualBasicProfile? profile = null, bool removeIntegerChecks = true) {
    var compilation = Compilation(source, profile ?? VisualBasicProfile.VbNetLatest, null, removeIntegerChecks);
    using var stream = new MemoryStream();
    var result = compilation.Emit(stream);
    if (!result.Success)
      throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics) + Environment.NewLine + source);

    stream.Position = 0;
    return new AssemblyLoadContext(null, true).LoadFromStream(stream);
  }

  /// <summary>A public procedure of the one module of a compiled source.</summary>
  public static MethodInfo Procedure(Assembly assembly, string name)
    => assembly.GetTypes().Where(type => type.DeclaringType is null)
      .Select(type => type.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)).FirstOrDefault(method => method is not null)
      ?? throw new MissingMethodException(name);

}
