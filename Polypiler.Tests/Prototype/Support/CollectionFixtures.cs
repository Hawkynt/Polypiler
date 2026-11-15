using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
  public static readonly (string File, string Class)[] CSharp = [("Collections", "CollectionsDemo"), ("WordFrequency", "WordFrequencyDemo")];

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

    foreach (var method in new[] { "DictionaryOrder", "SetOrder", "RemoveWhileEnumerating", "ClearThenInsert", "FirstZeroKey", "NaNKey", "MissingKey", "Keys", "Values" })
      yield return Call(method);

    yield return Call("Count", [new[] { 3, 1, 3, 2, 3 }]);
    yield return Call("Count", [new int[0]]);
    for (var change = 0; change <= 4; ++change)
      yield return Call("MapPasses", change);

    foreach (var key in new[] { 0, 1 }) {
      yield return Call("Missing", key);
      yield return Call("DuplicateAdd", key);
      yield return Call("Tried", key);
    }

    // Test design 8: the histogram across the resizes of the dictionary, with removals.
    foreach (var draws in new[] { 0, 1, 3, 4, 7, 8, 17, 97, 1000 })
      yield return ("WordFrequency", "WordFrequencyDemo", new PrototypeCall("Histogram", [draws]) { Returns = Compiled("WordFrequencyDemo", "Histogram").ReturnType });
  }

  /// <summary>The calls of the JavaScript fixture <c>wordFrequency.js</c>: the histogram across the growth of the map, with removals.</summary>
  public static IEnumerable<PrototypeCall> HistogramCalls() => new[] { 0, 1, 3, 4, 7, 8, 17, 97, 1000 }.Select(draws => new PrototypeCall("histogram", [(double)draws]));

  /// <summary>The functions of the JavaScript fixture <c>collections.js</c>, which take nothing.</summary>
  public static readonly string[] ScriptFunctions = [
    "arrayOps", "pushWhileIterating", "popOfEmpty", "popped", "searches", "cleared", "words", "copied", "stepped", "passed", "fixedLoop",
    "mapOrder", "setOrder", "firstZeroKey", "nanKey", "missing", "liveMap", "clearedSet", "hasDelete", "wordSet"
  ];

  /// <summary>The calls of the JavaScript fixture every implementation is compared on.</summary>
  public static IEnumerable<PrototypeCall> ScriptCalls()
    => ScriptFunctions.Select(function => new PrototypeCall(function, []))
      .Append(new PrototypeCall("count", [new[] { 3.0, 1, 3, 2, 3 }]))
      .Append(new PrototypeCall("count", [new double[0]]));

}
