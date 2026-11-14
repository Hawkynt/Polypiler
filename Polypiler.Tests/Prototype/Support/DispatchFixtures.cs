using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of <c>tests/fixtures/prototype/dispatch</c>: class hierarchies, interfaces, virtual
/// dispatch and type tests. A C# file there is built into this assembly in the namespace
/// <c>Polypiler.Fixtures.Prototype.Dispatch</c>, and that build is the oracle.
/// </summary>
internal static class DispatchFixtures {

  /// <summary>The namespace of the C# fixtures.</summary>
  public const string Namespace = "Polypiler.Fixtures.Prototype.Dispatch";

  /// <summary>The C# fixtures that translate, by file name, each with the name of its static class.</summary>
  public static readonly (string File, string Class)[] CSharp = [("Shapes", "ShapesDemo"), ("Animals", "AnimalsDemo")];

  /// <summary>The path of a fixture file of the folder, its extension included.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "dispatch", file);

  /// <summary>The text of a fixture file of the folder, its extension included.</summary>
  public static string SourceOf(string file) => File.ReadAllText(PathOf(file));

  /// <summary>A C# fixture of the folder imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule Imported(string file) {
    var result = CSharpFrontend.Import(file + ".cs", SourceOf(file + ".cs"));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>A JavaScript fixture of the folder imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule ImportedScript(string file) {
    var result = JavaScriptFrontend.Import(file, SourceOf(file));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>A static method of a fixture class as the C# compiler built it into this assembly.</summary>
  public static MethodInfo Compiled(string className, string method)
    => typeof(DispatchFixtures).Assembly.GetType($"{Namespace}.{className}", true)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
      ?? throw new MissingMethodException(className, method);

  /// <summary>The calls of the C# fixtures every implementation is compared on, the cast that traps among them.</summary>
  /// <returns>Each call with the file and the class of its fixture.</returns>
  public static IEnumerable<(string File, string Class, PrototypeCall Call)> Calls() {
    (string, string, PrototypeCall) Call(string file, string className, string method, params object[] arguments)
      => (file, className, new PrototypeCall(method, arguments) { Returns = Compiled(className, method).ReturnType });

    yield return Call("Shapes", "ShapesDemo", "SumTwice", 2.0, 3.0, 1.0);
    yield return Call("Shapes", "ShapesDemo", "SumSides", 2.0, 3.0, 1.0);
    yield return Call("Shapes", "ShapesDemo", "AreaThroughInterface", 2.5, 4.0);
    yield return Call("Shapes", "ShapesDemo", "IsRect", 1.0);
    yield return Call("Shapes", "ShapesDemo", "IsShape", 1.0);
    yield return Call("Shapes", "ShapesDemo", "AsRectIsNull", 1.0);
    yield return Call("Shapes", "ShapesDemo", "CastToRect", 1.0);
    yield return Call("Shapes", "ShapesDemo", "CastToCircle", 2.0);
    yield return Call("Shapes", "ShapesDemo", "CastOfNull");
    yield return Call("Shapes", "ShapesDemo", "Pattern", 2.0, 3.0, true);
    yield return Call("Shapes", "ShapesDemo", "Pattern", 2.0, 3.0, false);
    yield return Call("Animals", "AnimalsDemo", "DogDescribe");
    yield return Call("Animals", "AnimalsDemo", "AnimalDescribe");
    var random = new Random(2026);
    for (var draw = 0; draw < 4; ++draw) {
      var (a, b, c) = (random.Next(-50, 50) / 4.0, random.Next(-50, 50) / 4.0, random.Next(-50, 50) / 4.0);
      yield return Call("Shapes", "ShapesDemo", "SumTwice", a, b, c);
      yield return Call("Shapes", "ShapesDemo", "Pattern", a, b, draw % 2 == 0);
    }
  }

}
