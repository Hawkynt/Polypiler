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
/// The fixtures of <c>tests/fixtures/prototype/resources</c>: using statements and declarations over
/// classes that implement <c>IDisposable</c>, and the same scopes written as JavaScript's
/// <c>try … finally</c>. A C# file there is built into this assembly in the namespace
/// <c>Polypiler.Fixtures.Prototype.Resources</c>, and that build is the oracle; node is the oracle of
/// the JavaScript file.
/// </summary>
internal static class ResourceFixtures {

  /// <summary>The namespace of the C# fixtures.</summary>
  public const string Namespace = "Polypiler.Fixtures.Prototype.Resources";

  /// <summary>The static class of the C# fixture.</summary>
  public const string Class = "ScopesDemo";

  /// <summary>The methods of the C# fixture, none of which takes an argument.</summary>
  public static readonly string[] Methods = [
    "Nest", "BodyThrows", "DisposeThrows", "NullResource", "BothThrow", "BodyAndDisposeThrow", "Declarators", "Expression", "Three", "Returns", "Breaks", "Continues", "Explicit"
  ];

  /// <summary>The functions of the JavaScript fixture, none of which takes an argument.</summary>
  public static readonly string[] Functions = ["nest", "bodyThrows", "disposeThrows", "nullResource", "bothThrow"];

  /// <summary>The path of a fixture file of the folder, its extension included.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "resources", file);

  /// <summary>The text of a fixture file of the folder, its extension included.</summary>
  public static string SourceOf(string file) => File.ReadAllText(PathOf(file));

  /// <summary>The C# fixture imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule Imported() {
    var result = CSharpFrontend.Import("Scopes.cs", SourceOf("Scopes.cs"));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>The JavaScript fixture imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule ImportedScript() {
    var result = JavaScriptFrontend.Import("scopes.js", SourceOf("scopes.js"));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>A static method of the C# fixture as the C# compiler built it into this assembly.</summary>
  public static MethodInfo Compiled(string method)
    => typeof(ResourceFixtures).Assembly.GetType($"{Namespace}.{Class}", true)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
      ?? throw new MissingMethodException(Class, method);

  /// <summary>The calls of the C# fixture every implementation is compared on.</summary>
  public static IEnumerable<PrototypeCall> Calls() => Methods.Select(method => new PrototypeCall(method, []) { Returns = typeof(string) });

  /// <summary>The calls of the JavaScript fixture.</summary>
  public static IEnumerable<PrototypeCall> ScriptCalls() => Functions.Select(function => new PrototypeCall(function, []) { Returns = typeof(string) });

}
