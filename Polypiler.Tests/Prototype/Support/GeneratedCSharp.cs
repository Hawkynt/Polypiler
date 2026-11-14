using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// Generated C# as the real C# compiler builds it: Roslyn compiles the text into an assembly in
/// memory, which is loaded and called. The compiler, not the emitter, decides what the text means.
/// A text is compiled once per run with the same options, however many tests ask for it, and loaded
/// for each of them.
/// </summary>
internal sealed class GeneratedCSharp {

  private readonly Type _class;

  private GeneratedCSharp(Type type) => this._class = type;

  /// <summary>The full name of the one class the text declares, namespace included.</summary>
  public string ClassName => this._class.FullName!;

  /// <summary>
  /// Compiles the text of one generated file at the highest warning level and loads the result.
  /// </summary>
  /// <param name="source">The generated C#.</param>
  /// <param name="checkOverflow">
  /// The project default for arithmetic: <see langword="true"/> is <c>CheckForOverflowUnderflow</c>,
  /// which makes every operation outside an <c>unchecked</c> context throw on overflow.
  /// </param>
  /// <param name="fullyOptimized">
  /// Whether every method is marked <c>MethodImplOptions.AggressiveOptimization</c>, which makes the JIT
  /// compile it fully optimized at once, as a method the runtime calls often ends up after tiering;
  /// otherwise the first calls run the code of the first tier.
  /// </param>
  /// <exception cref="InvalidOperationException">The compiler reported an error or a warning.</exception>
  public static GeneratedCSharp Compile(string source, bool checkOverflow, bool fullyOptimized = false) {
    var image = _builds.GetOrAdd((source, checkOverflow, fullyOptimized), key => new(() => Build(key.Source, key.CheckOverflow, key.FullyOptimized))).Value;
    // The structs of the module stand beside the class; the class is the one static, so abstract and sealed, type.
    return new GeneratedCSharp(Assembly.Load(image).GetExportedTypes().Single(type => type is { IsAbstract: true, IsSealed: true }));
  }

  /// <summary>
  /// Every build of this run, by its text and options: the same text is compiled once. Each caller loads
  /// the image afresh, so that its methods start in the first tier, as they would after a compilation.
  /// </summary>
  private static readonly ConcurrentDictionary<(string Source, bool CheckOverflow, bool FullyOptimized), Lazy<byte[]>> _builds = new();

  /// <summary>What the compiler of a profile said about a text, by the text, the profile and the language version, during this run.</summary>
  private static readonly ConcurrentDictionary<(string Source, string Profile, string LanguageVersion), Lazy<IReadOnlyList<string>>> _complaints = new();

  private static byte[] Build(string source, bool checkOverflow, bool fullyOptimized) {
    var tree = CSharpSyntaxTree.ParseText(source);
    if (fullyOptimized) {
      var root = tree.GetRoot();
      var optimized = SyntaxFactory.Attribute(
        SyntaxFactory.ParseName("global::System.Runtime.CompilerServices.MethodImpl"),
        SyntaxFactory.ParseAttributeArgumentList("(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)"));
      tree = tree.WithRootAndOptions(
        root.ReplaceNodes(root.DescendantNodes().OfType<MethodDeclarationSyntax>(), (_, method) => method.AddAttributeLists(SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(optimized)))),
        tree.Options);
    }

    var compilation = CSharpCompilation.Create(
      "Generated" + Guid.NewGuid().ToString("N"),
      [tree],
      [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
      new CSharpCompilationOptions(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release,
        checkOverflow: checkOverflow,
        warningLevel: 9999));
    using var image = new MemoryStream();
    var complaints = compilation.Emit(image).Diagnostics
      .Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)
      .Select(diagnostic => diagnostic.ToString())
      .ToList();
    if (complaints.Count > 0)
      throw new InvalidOperationException($"The C# compiler does not accept the generated file without complaint:\n{string.Join('\n', complaints)}\n{source}");

    return image.ToArray();
  }

  /// <summary>
  /// Compiles the text of one generated file as the compiler of a profile would: in the language
  /// version of the profile, against the reference assemblies of its framework and nothing else, at
  /// the highest warning level with every warning an error. Nothing is loaded; the assembly could
  /// not run here.
  /// </summary>
  /// <param name="source">The generated C#.</param>
  /// <param name="profile">The name of the profile, which names the reference assemblies.</param>
  /// <param name="languageVersion">The language version, as <c>--langversion</c> takes it.</param>
  /// <returns>What the compiler reports; empty when it accepts the file without complaint.</returns>
  /// <exception cref="InvalidOperationException">The reference assemblies are not restored, or the language version is none.</exception>
  public static IReadOnlyList<string> ComplaintsFor(string source, string profile, string languageVersion)
    => _complaints.GetOrAdd((source, profile, languageVersion), key => new(() => Complaints(key.Source, key.Profile, key.LanguageVersion))).Value;

  private static IReadOnlyList<string> Complaints(string source, string profile, string languageVersion) {
    if (!LanguageVersionFacts.TryParse(languageVersion, out var version))
      throw new InvalidOperationException($"'{languageVersion}' is no language version of the C# compiler.");

    var compilation = CSharpCompilation.Create(
      "Generated" + Guid.NewGuid().ToString("N"),
      [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(version))],
      ReferenceAssemblies.Of(profile),
      new CSharpCompilationOptions(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release,
        generalDiagnosticOption: ReportDiagnostic.Error,
        warningLevel: 9999));
    using var image = new MemoryStream();
    return [.. compilation.Emit(image).Diagnostics
      .Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)
      .Select(diagnostic => diagnostic.ToString())];
  }

  /// <summary>A method of the class, public or private.</summary>
  public MethodInfo Method(string name)
    => this._class.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
      ?? throw new MissingMethodException(this._class.Name, name);

  /// <summary>
  /// What the compiled method does for a call, in the notation of <see cref="PrototypeFixture.Outcome"/>.
  /// A <c>char</c> of the call is handed over as the <c>ushort</c> the IR knows it as, which is the
  /// type the generated method declares.
  /// </summary>
  public string Outcome(PrototypeCall call)
    => PrototypeFixture.InvokedOutcome(this.Method(call.Method), call with { Arguments = [.. call.Arguments.Select(CodeUnits)] });

  private static object CodeUnits(object argument) => argument switch {
    char unit => (ushort)unit,
    char[] text => Array.ConvertAll(text, unit => (ushort)unit),
    _ => argument
  };

}
