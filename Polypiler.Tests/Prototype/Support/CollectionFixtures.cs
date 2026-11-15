using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of <c>tests/fixtures/prototype/collections</c>: lists, maps and sets of C# and the
/// arrays, maps and sets of JavaScript. A C# file there is built into this assembly in the namespace
/// <c>Polypiler.Fixtures.Prototype.Collections</c>, and that build is the oracle; node is the oracle
/// of a JavaScript file.
/// </summary>
internal static class CollectionFixtures {

  /// <summary>The namespace of the C# fixtures.</summary>
  public const string Namespace = "Polypiler.Fixtures.Prototype.Collections";

  /// <summary>The C# fixtures, by file name, each with the name of its static class.</summary>
  public static readonly (string File, string Class)[] CSharp = [("Collections", "CollectionsDemo")];

  /// <summary>The path of a fixture file of the folder, its extension included.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "collections", file);

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
    => typeof(CollectionFixtures).Assembly.GetType($"{Namespace}.{className}", true)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
      ?? throw new MissingMethodException(className, method);

  /// <summary>The calls of the C# fixtures every implementation is compared on.</summary>
  /// <returns>Each call with the file and the class of its fixture.</returns>
  public static IEnumerable<(string File, string Class, PrototypeCall Call)> Calls() {
    (string, string, PrototypeCall) Call(string method, params object[] arguments)
      => ("Collections", "CollectionsDemo", new PrototypeCall(method, arguments) { Returns = Compiled("CollectionsDemo", method).ReturnType });

    foreach (var method in new[] { "ListOps", "RemoveLastOfEmpty", "Copies", "Words", "Boxes", "Pairs", "ModifyWhileEnumerating", "Widened" })
      yield return Call(method);

    foreach (var count in new[] { 0, 1, 3, 4, 7, 8, 17, 1000 })
      yield return Call("Grow", count);

    foreach (var index in new[] { -1, 0, 2, 3 })
      yield return Call("Element", index);

    foreach (var index in new[] { -1, 0, 2, 3 })
      yield return Call("Write", index);

    foreach (var index in new[] { -1, 0, 2, 3 })
      yield return Call("InsertAt", index);

    foreach (var value in new[] { double.NaN, 0.0, -0.0, 1.0 })
      yield return Call("Search", value);

    for (var change = 0; change <= 4; ++change)
      yield return Call("Passes", change);
  }

}
