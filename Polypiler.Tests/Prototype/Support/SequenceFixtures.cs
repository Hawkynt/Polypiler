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
/// The fixtures of <c>tests/fixtures/prototype/sequences</c> (#1327): iterator methods of C# and
/// generators of JavaScript. <c>Iterators.cs</c> is built into this assembly in the namespace
/// <c>Polypiler.Fixtures.Prototype.Sequences</c>, and that build is its oracle; node is the oracle of
/// <c>iterators.js</c>.
/// </summary>
internal static class SequenceFixtures {

  /// <summary>The namespace of the C# fixture.</summary>
  public const string Namespace = "Polypiler.Fixtures.Prototype.Sequences";

  /// <summary>The path of a fixture file of the folder, its extension included.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "sequences", file);

  /// <summary>The text of a fixture file of the folder, its extension included.</summary>
  public static string SourceOf(string file) => File.ReadAllText(PathOf(file));

  /// <summary>The C# fixture imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule Imported() {
    var result = CSharpFrontend.Import("Iterators.cs", SourceOf("Iterators.cs"));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>The JavaScript fixture imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule ImportedScript() {
    var result = JavaScriptFrontend.Import("iterators.js", SourceOf("iterators.js"));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>A static method of the fixture class as the C# compiler built it into this assembly.</summary>
  public static MethodInfo Compiled(string method)
    => typeof(SequenceFixtures).Assembly.GetType($"{Namespace}.Iterators", true)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
      ?? throw new MissingMethodException("Iterators", method);

  /// <summary>The calls of the C# fixture every implementation is compared on, each with the type it returns.</summary>
  public static IEnumerable<PrototypeCall> Calls() {
    PrototypeCall Call(string method, params object[] arguments) => new(method, arguments) { Returns = Compiled(method).ReturnType };
    foreach (var method in new[] { "EarlyBreak", "Full", "Twice", "Throws", "Returned", "ThrowInside", "NestedClose", "NestedFull", "Restarted", "FromArray", "Longs", "Floats", "Truths", "Objects", "Widened", "FromList", "Captured" })
      yield return Call(method);

    foreach (var n in new[] { 0, 1, 10, 47 })
      yield return Call("FibSum", n);

    foreach (var n in new[] { -1, 0 })
      yield return Call("Guard", n);

    foreach (var n in new[] { 0, 3 })
      yield return Call("Nested", n);

    foreach (var n in new[] { 0, 1, 5 })
      yield return Call("Taken", n);

    yield return Call("Method", 4);
  }

  /// <summary>The functions of the JavaScript fixture, which take nothing.</summary>
  public static readonly string[] ScriptFunctions = ["earlyBreak", "full", "twice", "returned", "throwInside", "nestedClose", "nestedFull", "fromArray", "truths", "objects", "delegated"];

  /// <summary>The calls of the JavaScript fixture every implementation is compared on.</summary>
  public static IEnumerable<PrototypeCall> ScriptCalls()
    => ScriptFunctions.Select(function => new PrototypeCall(function, []))
      .Concat(new[] { 0, 1, 10, 47 }.Select(n => new PrototypeCall("fibSum", [(double)n])));

}
