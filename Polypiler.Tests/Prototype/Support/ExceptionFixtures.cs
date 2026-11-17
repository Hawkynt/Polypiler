using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of <c>tests/fixtures/prototype/exceptions</c>: traps caught by the clause of their
/// class, exception classes of the file, filters, finally blocks and throw expressions. A C# file
/// there is built into this assembly in the namespace <c>Polypiler.Fixtures.Prototype.Exceptions</c>,
/// and that build is the oracle.
/// </summary>
internal static class ExceptionFixtures {

  /// <summary>The namespace of the C# fixtures.</summary>
  public const string Namespace = "Polypiler.Fixtures.Prototype.Exceptions";

  /// <summary>The C# fixtures that translate to every profile, by file name, each with the name of its static class.</summary>
  public static readonly (string File, string Class)[] CSharp = [("Traps", "TrapsDemo"), ("Classes", "ClassesDemo")];

  /// <summary>The path of a fixture file of the folder, its extension included.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "exceptions", file);

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
    => typeof(ExceptionFixtures).Assembly.GetType($"{Namespace}.{className}", true)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
      ?? throw new MissingMethodException(className, method);

  /// <summary>
  /// The calls of the C# fixtures every implementation is compared on: every trap under every clause,
  /// the exception classes, the filter, the finally block, the rethrow and the throw expressions.
  /// </summary>
  /// <returns>Each call with the file and the class of its fixture.</returns>
  public static IEnumerable<(string File, string Class, PrototypeCall Call)> Calls() {
    (string, string, PrototypeCall) Call(string file, string className, string method, params object[] arguments)
      => (file, className, new PrototypeCall(method, arguments) { Returns = Compiled(className, method).ReturnType });

    for (var k = 0; k <= 5; ++k) {
      yield return Call("Traps", "TrapsDemo", "Classify", k);
      yield return Call("Traps", "TrapsDemo", "ByBase", k);
    }

    yield return Call("Traps", "TrapsDemo", "Cast", 0);
    yield return Call("Traps", "TrapsDemo", "Cast", 1);
    yield return Call("Classes", "ClassesDemo", "Chain");
    yield return Call("Classes", "ClassesDemo", "Filter", "x");
    yield return Call("Classes", "ClassesDemo", "Filter", "y");
    yield return Call("Classes", "ClassesDemo", "Fin");
    yield return Call("Classes", "ClassesDemo", "FinState");
    for (var k = 0; k <= 2; ++k) {
      yield return Call("Classes", "ClassesDemo", "Deep", k);
      yield return Call("Classes", "ClassesDemo", "Thrown", k + 1);
    }

    yield return Call("Classes", "ClassesDemo", "Rethrow");
    foreach (var fail in new[] { true, false }) {
      yield return Call("Regions", "RegionsDemo", "Observed", fail);
      yield return Call("Regions", "RegionsDemo", "Declined", fail);
    }

    for (var k = -1; k <= 4; ++k) {
      yield return Call("Regions", "RegionsDemo", "Returned", k);
      yield return Call("Regions", "RegionsDemo", "Ways", k);
      yield return Call("Regions", "RegionsDemo", "Passed", k);
      yield return Call("Regions", "RegionsDemo", "AroundCall", k);
    }

    for (var n = 0; n <= 7; ++n)
      yield return Call("Regions", "RegionsDemo", "Jumps", n);

    yield return Call("Regions", "RegionsDemo", "Nested");
    yield return Call("Regions", "RegionsDemo", "Replaced");
  }

}
