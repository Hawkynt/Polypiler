using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using JavaScript = Polypiler.Core.Frontends.JavaScript;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of <c>tests/fixtures/prototype/state</c>, whose functions keep state between calls in
/// static fields or in variables of the module, and the sequences of calls every implementation is
/// compared on. A sequence runs in one fresh instance of the module: one load of a class, one import
/// of a module, one <see cref="IrInterpreter.Instantiate"/>. The .NET oracle is the C# compiler's
/// build of the fixture's text, loaded afresh for every sequence, not the build in the test assembly,
/// whose static fields every test of the run would share.
/// </summary>
internal static class StateFixtures {

  /// <summary>The fixtures: the C# file, its class, and the JavaScript file of the same algorithm.</summary>
  public static readonly (string File, string Class, string Script)[] All = [
    ("Xorshift32", "Xorshift32", "xorshift32"),
    ("Xorshift64", "Xorshift64State", "xorshift64"),
    ("Histogram", "Histogram", "histogram"),
    ("Lazy", "Lazy", "lazy"),
    ("Journal", "Journal", "journal")
  ];

  /// <summary>The bytes of "123456789", whose CRC-32 is the published check value 0xCBF43926.</summary>
  public static readonly byte[] Check = "123456789"u8.ToArray();

  /// <summary>The path of a file of the folder, its extension included.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "state", file);

  /// <summary>The text of a file of the folder, its extension included.</summary>
  public static string SourceOf(string file) => File.ReadAllText(PathOf(file));

  /// <summary>A C# fixture imported; fails with its diagnostics when it is refused.</summary>
  public static IrModule ImportedCSharp(string file) {
    var result = CSharpFrontend.Import(file + ".cs", SourceOf(file + ".cs"));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>A JavaScript fixture imported; fails with its diagnostics when it is refused.</summary>
  public static IrModule ImportedJavaScript(string script) {
    var result = JavaScript.JavaScriptFrontend.Import(script + ".js", SourceOf(script + ".js"));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>
  /// The sequence of calls of a fixture, by the names its C# class has; a JavaScript module has the
  /// same functions named in camel case (<see cref="Script"/>). Lazy is called before and after its
  /// table is filled, twice with the check value; Journal reads its ring of texts back between and after
  /// the letters it adds.
  /// </summary>
  public static IReadOnlyList<PrototypeCall> Sequence(string file) => file switch {
    "Xorshift32" => [new("Next", []), new("Next", []), new("Next", []), new("Calls", [])],
    "Xorshift64" => [new("Next", []) { Returns = typeof(ulong) }, new("Next", []) { Returns = typeof(ulong) }, new("Next", []) { Returns = typeof(ulong) }],
    "Histogram" => [new("Add", [1]), new("Add", [5]), new("Add", [2]), new("Add", [-3]), new("Add", [int.MaxValue])],
    "Journal" => [
      new("Add", [104]), new("Add", [105]), new("Back", [1]), new("Back", [2]),
      new("Add", [33]), new("Add", [63]), new("Add", [46]), new("Back", [1]), new("Back", [2]), new("Back", [4])
    ],
    _ => [new("Filled", []), new("Compute", [Check]), new("Filled", []), new("Compute", [Check]), new("Compute", [Array.Empty<byte>()])]
  };

  /// <summary>The outcomes the fixtures' issue states, which .NET and node reproduce.</summary>
  public static IReadOnlyList<string> Expected(string file) => file switch {
    "Xorshift32" => ["723471715", "2497366906", "2064144800", "3"],
    "Xorshift64" => ["8748534153485358512", "3040900993826735515", "3453997556048239312"],
    "Histogram" => ["1", "2", "1", "3", "1"],
    "Journal" => ["1", "2", "hi", "h", "3", "4", "5", "hi!?.", "hi!?", "hi"],
    _ => ["false", "3421780262 | [49,50,51,52,53,54,55,56,57]", "true", "3421780262 | [49,50,51,52,53,54,55,56,57]", "0 | []"]
  };

  /// <summary>A call of a C# fixture as its JavaScript module names the function: in camel case, Filled as isFilled.</summary>
  public static PrototypeCall Script(PrototypeCall call) => call with {
    Method = call.Method == "Filled" ? "isFilled" : char.ToLowerInvariant(call.Method[0]) + call.Method[1..]
  };

  /// <summary>The outcomes of a sequence in one instance of the interpreter, as <see cref="PrototypeFixture.Outcome"/> writes them.</summary>
  public static IReadOnlyList<string> Interpreted(IrModule module, IEnumerable<PrototypeCall> calls) {
    var instance = IrInterpreter.Instantiate(module);
    return [.. calls.Select(call => {
      var arguments = call.FreshArguments();
      return PrototypeFixture.Outcome(instance.Call(call.Method, arguments), arguments);
    })];
  }

  /// <summary>The outcomes of a sequence in one fresh load of generated or original C#.</summary>
  public static IReadOnlyList<string> Compiled(string source, IEnumerable<PrototypeCall> calls) {
    var build = GeneratedCSharp.Compile(source, false);
    return [.. calls.Select(build.Outcome)];
  }

}
