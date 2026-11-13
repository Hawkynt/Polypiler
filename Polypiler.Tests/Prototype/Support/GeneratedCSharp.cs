using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// Generated C# as the real C# compiler builds it: Roslyn compiles the text into an assembly in
/// memory, which is loaded and called. The compiler, not the emitter, decides what the text means.
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
  /// <exception cref="InvalidOperationException">The compiler reported an error or a warning.</exception>
  public static GeneratedCSharp Compile(string source, bool checkOverflow) {
    var compilation = CSharpCompilation.Create(
      "Generated" + Guid.NewGuid().ToString("N"),
      [CSharpSyntaxTree.ParseText(source)],
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

    return new GeneratedCSharp(Assembly.Load(image.ToArray()).GetExportedTypes().Single());
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
