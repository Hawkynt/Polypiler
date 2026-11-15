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
/// The fixtures of <c>tests/fixtures/prototype/functions</c>: delegates, lambdas, local functions and
/// closures of C#, and their JavaScript counterparts (#1326). A C# file there is built into this
/// assembly in the namespace <c>Polypiler.Fixtures.Prototype.Functions</c>, and that build is the
/// oracle; for a JavaScript file it is node.
/// </summary>
internal static class FunctionsFixtures {

  /// <summary>The namespace of the C# fixtures.</summary>
  public const string Namespace = "Polypiler.Fixtures.Prototype.Functions";

  /// <summary>The C# fixtures, by file name, each with the name of its static class.</summary>
  public static readonly (string File, string Class)[] CSharp = [("Closures", "Closures"), ("Sort", "Sorting"), ("Accumulate", "Accumulate")];

  /// <summary>The JavaScript fixtures, by file name.</summary>
  public static readonly string[] JavaScript = ["closures.js", "sort.js", "accumulate.js"];

  /// <summary>The path of a fixture file of the folder, its extension included.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "functions", file);

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
    => typeof(FunctionsFixtures).Assembly.GetType($"{Namespace}.{className}", true)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
      ?? throw new MissingMethodException(className, method);

  /// <summary>Arrays of 0, 1, 2 and 50 elements drawn with the fixed seed 2026, as test design 2 asks.</summary>
  private static IEnumerable<int[]> Integers() {
    var random = new Random(2026);
    foreach (var length in new[] { 0, 1, 2, 50 })
      yield return [.. Enumerable.Range(0, length).Select(_ => random.Next(-1000, 1000))];
  }

  private static IEnumerable<double[]> Doubles() {
    var random = new Random(2026);
    foreach (var length in new[] { 0, 1, 2, 50 })
      yield return [.. Enumerable.Range(0, length).Select(_ => random.Next(-4000, 4000) / 8.0)];
  }

  /// <summary>The calls of the C# fixtures every implementation is compared on.</summary>
  /// <returns>Each call with the file and the class of its fixture.</returns>
  public static IEnumerable<(string File, string Class, PrototypeCall Call)> Calls() {
    (string, string, PrototypeCall) Call(string file, string className, string method, params object[] arguments)
      => (file, className, new PrototypeCall(method, arguments) { Returns = Compiled(className, method).ReturnType });

    foreach (var method in new[] { "ForCapture", "Counter", "Interleaved", "CallNull", "NullChecks", "BodyLocal" })
      yield return Call("Closures", "Closures", method);

    foreach (var values in new[] { new[] { 0, 1, 2 }, [], [7], [5, -5, 9, 1] })
      yield return Call("Closures", "Closures", "ForeachCapture", [values]);

    foreach (var value in new[] { 2, -3 })
      yield return Call("Closures", "Closures", "Late", value);

    foreach (var value in new[] { 0, 1, 5, 12, 13 })
      yield return Call("Closures", "Closures", "Fact", value);

    foreach (var value in new[] { 4, -1 }) {
      yield return Call("Closures", "Closures", "Composed", value);
      yield return Call("Closures", "Closures", "Anonymous", value);
      yield return Call("Closures", "Closures", "Bound", value);
      yield return Call("Closures", "Closures", "StaticLocal", value);
      yield return Call("Closures", "Closures", "Deep", value);
    }

    yield return Call("Closures", "Closures", "MethodGroup", -7);
    yield return Call("Closures", "Closures", "StaticLambda", 2, 40);
    foreach (var times in new[] { 0, 1, 4 })
      yield return Call("Closures", "Closures", "Shared", times);

    foreach (var values in Integers()) {
      yield return Call("Sort", "Sorting", "Ascending", [values.ToArray()]);
      yield return Call("Sort", "Sorting", "ByDistance", values.ToArray(), 17);
    }

    foreach (var values in Doubles()) {
      yield return Call("Accumulate", "Accumulate", "Sum", [values]);
      yield return Call("Accumulate", "Accumulate", "SumOfSquares", [values]);
      yield return Call("Accumulate", "Accumulate", "Calls", [values]);
    }
  }

}
