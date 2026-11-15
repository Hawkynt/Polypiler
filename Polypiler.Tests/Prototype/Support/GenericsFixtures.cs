using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of <c>tests/fixtures/prototype/generics</c>: generic methods and a generic class of
/// C#, and a JavaScript helper that two kinds of typed array reach. A C# file there is built into
/// this assembly in the namespace <c>Polypiler.Fixtures.Prototype.Generics</c>, and that build is the oracle.
/// </summary>
internal static class GenericsFixtures {

  /// <summary>The namespace of the C# fixtures.</summary>
  public const string Namespace = "Polypiler.Fixtures.Prototype.Generics";

  /// <summary>The C# fixtures that translate, by file name, each with the name of its static class.</summary>
  public static readonly (string File, string Class)[] CSharp = [("Search", "SearchDemo"), ("Ring", "RingDemo")];

  /// <summary>The path of a fixture file of the folder, its extension included.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "generics", file);

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
    => typeof(GenericsFixtures).Assembly.GetType($"{Namespace}.{className}", true)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
      ?? throw new MissingMethodException(className, method);

  /// <summary>The calls of the C# fixtures every implementation is compared on, with fixed and drawn arguments.</summary>
  /// <returns>Each call with the file and the class of its fixture.</returns>
  public static IEnumerable<(string File, string Class, PrototypeCall Call)> Calls() {
    (string, string, PrototypeCall) Call(string file, string className, string method, params object[] arguments)
      => (file, className, new PrototypeCall(method, arguments) { Returns = Compiled(className, method).ReturnType });

    foreach (var method in new[] { "IndexOfNaN", "IndexOfZero", "IndexOfSeven", "IndexOfFour", "SwapInts", "SwapDoubles", "MaxNaN", "MaxZero",
        "DefInt", "DefDouble", "DefBool", "DefBoxIsNull", "MakeBoxIsNew", "MakeBoxValue" })
      yield return Call("Search", "SearchDemo", method);

    yield return Call("Search", "SearchDemo", "IndexOfDouble", new[] { 1.0, double.NaN, -0.0 }, double.NaN);
    yield return Call("Search", "SearchDemo", "IndexOfDouble", new[] { 1.0, double.NaN, -0.0 }, 0.0);
    yield return Call("Search", "SearchDemo", "IndexOfInt", new[] { 4, -7, 9 }, 9);
    yield return Call("Search", "SearchDemo", "MaxDouble", double.NaN, double.NegativeInfinity);
    yield return Call("Search", "SearchDemo", "MaxDouble", -0.0, 0.0);
    yield return Call("Search", "SearchDemo", "MaxInt", int.MinValue, int.MaxValue);
    foreach (var method in new[] { "LastLong", "CountLong", "ReciprocalOfLastDouble" })
      yield return Call("Ring", "RingDemo", method);

    var random = new Random(2026);
    for (var draw = 0; draw < 4; ++draw) {
      var values = new[] { random.Next(-9, 9) / 2.0, random.Next(-9, 9) / 2.0, random.Next(-9, 9) / 2.0 };
      yield return Call("Search", "SearchDemo", "IndexOfDouble", values, random.Next(-9, 9) / 2.0);
      yield return Call("Search", "SearchDemo", "MaxInt", random.Next(-100, 100), random.Next(-100, 100));
      yield return Call("Search", "SearchDemo", "MaxDouble", random.Next(-9, 9) / 4.0, random.Next(-9, 9) / 4.0);
    }
  }

}
