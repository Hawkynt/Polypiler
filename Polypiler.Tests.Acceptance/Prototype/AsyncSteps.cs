using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/ir/ir-async.feature</c> (#1330). What a call should give is never
/// computed here: it is what .NET computes of the C# fixture built into this assembly, or what node
/// computes of the JavaScript fixture; the generated code is awaited by the test (.NET) or by a driver
/// module (node).
/// </summary>
[Binding, Scope(Feature = "async and await between Task and Promise")]
public sealed class AsyncSteps(PrototypeScenario scenario) {

  /// <summary>The ordering probes of the issue in C#, each a method that returns its trace.</summary>
  private const string CSharpProbes = """
    #pragma warning disable CS1998
    using System;
    using System.Threading.Tasks;

    public static class Probes {

      private static string _log = "";

      private static void L(string s) => _log = _log.Length == 0 ? s : _log + " " + s;

      private static async Task F() { L("f1"); await Task.CompletedTask; L("f2"); }

      public static async Task<string> Stored() { _log = ""; L("m1"); var t = F(); L("m2"); await t; L("m3"); return _log; }

      public static async Task<string> Awaited() { _log = ""; L("m1"); await F(); L("m3"); return _log; }

      private static async Task G(string n) { L(n + "1"); await Task.CompletedTask; L(n + "2"); await Task.CompletedTask; L(n + "3"); }

      public static async Task<string> All() { _log = ""; await Task.WhenAll(G("a"), G("bb")); return _log; }

      private static async Task W(TaskCompletionSource<int> s) { L("w1"); var v = await s.Task; L("w2:" + v); }

      public static async Task<string> ByHand() { _log = ""; var s = new TaskCompletionSource<int>(); var w = W(s); L("before"); s.SetResult(7); L("after"); await w; return _log; }

      private static async Task A() { await Task.CompletedTask; await Task.CompletedTask; throw new Exception("A"); }

      private static async Task B() { await Task.CompletedTask; throw new Exception("B"); }

      public static async Task<string> Faults() { try { await Task.WhenAll(A(), B()); return "none"; } catch (Exception e) { return e.Message; } }

      private static async Task<int> T() { L("t1"); throw new Exception("boom"); }

      public static async Task<string> Early() { _log = ""; var t = T(); L("returned"); try { await t; } catch (Exception e) { L("caught " + e.Message); } return _log; }

    }
    """;

  /// <summary>The same probes in JavaScript, each printing its trace on a line.</summary>
  private const string ScriptProbes = """
    let log = [];
    const L = s => log.push(s);
    async function f() { L("f1"); await 0; L("f2"); }
    async function g(n) { L(n + "1"); await 0; L(n + "2"); await 0; L(n + "3"); }
    async function w(p) { L("w1"); const v = await p; L("w2:" + v); }
    async function a() { await 0; await 0; throw new Error("A"); }
    async function b() { await 0; throw new Error("B"); }
    async function t() { L("t1"); throw new Error("boom"); }
    const probes = {
      Stored: async () => { log = []; L("m1"); const p = f(); L("m2"); await p; L("m3"); return log.join(" "); },
      Awaited: async () => { log = []; L("m1"); await f(); L("m3"); return log.join(" "); },
      All: async () => { log = []; await Promise.all([g("a"), g("bb")]); return log.join(" "); },
      ByHand: async () => { log = []; let resolve; const p = new Promise(r => { resolve = r; }); const done = w(p); L("before"); resolve(7); L("after"); await done; return log.join(" "); },
      Faults: async () => { try { await Promise.all([a(), b()]); return "none"; } catch (e) { return e.message; } },
      Early: async () => { log = []; const p = t(); L("returned"); try { await p; } catch (e) { L("caught " + e.message); } return log.join(" "); }
    };
    for (const name of Object.keys(probes)) console.log(name + ": " + await probes[name]());
    """;

  /// <summary>The traces the issue measured with .NET 10.0.112 and node v22.23.3: .NET, then node.</summary>
  private static readonly (string Probe, string DotNet, string Node)[] _documented = [
    ("Stored", "m1 f1 f2 m2 m3", "m1 f1 m2 f2 m3"),
    ("Awaited", "m1 f1 f2 m3", "m1 f1 f2 m3"),
    ("All", "a1 a2 a3 bb1 bb2 bb3", "a1 bb1 a2 bb2 a3 bb3"),
    ("ByHand", "w1 before w2:7 after", "w1 before after w2:7"),
    ("Faults", "A", "B"),
    ("Early", "t1 returned caught boom", "t1 returned caught boom")
  ];

  private (string Path, string Text) _generated;
  private string _to = "";

  private static bool IsScript(string path) => Path.GetExtension(path) is ".mjs" or ".js";

  /// <summary>A function of the fixture by the name the C# fixture gives it; JavaScript names it in lower case.</summary>
  private static string Named(string source, string method) => IsScript(source) ? char.ToLowerInvariant(method[0]) + method[1..] : method;

  /// <summary>
  /// Runs calls of a module in node, awaiting each, and returns one outcome per call as
  /// <see cref="AsyncFixtures.Awaited"/> writes them: the value, an array in brackets, or <c>fault: message</c>.
  /// </summary>
  private string[] Node(string module, IEnumerable<PrototypeCall> calls) {
    var driver = scenario.Workspace.Resolve("driver-" + Guid.NewGuid().ToString("N") + ".mjs");
    var list = string.Join(", ", calls.Select(call => $"[\"{call.Method}\", [{string.Join(", ", call.Arguments.Select(argument => Convert.ToString(argument, System.Globalization.CultureInfo.InvariantCulture)!.ToLowerInvariant()))}]]"));
    File.WriteAllText(driver, $$"""
      const m = await import({{System.Text.Json.JsonSerializer.Serialize(new Uri(Path.GetFullPath(module)).AbsoluteUri)}});
      const show = r => r === undefined ? "void" : ArrayBuffer.isView(r) || Array.isArray(r) ? "[" + Array.from(r).join(",") + "]" : String(r);
      for (const [name, args] of [{{list}}]) {
        try { console.log(show(await m[name](...args))); } catch (e) { console.log("fault: " + e.message); }
      }
      """);
    var (exitCode, output) = Toolchain.RunNode(Path.GetDirectoryName(driver)!, driver);
    Assert.That(exitCode, Is.Zero, output);
    return output.TrimEnd().Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
  }

  /// <summary>What .NET computes of a call of the C# fixture, awaited.</summary>
  private static string DotNet(string method, params object[] arguments) => AsyncFixtures.DotNetOutcome(new PrototypeCall(method, arguments));

  /// <summary>What node computes of a call of the JavaScript fixture.</summary>
  private string Original(string function, params object[] arguments) {
    var copy = scenario.Workspace.Resolve("original-" + Guid.NewGuid().ToString("N") + ".mjs");
    File.WriteAllText(copy, AsyncFixtures.SourceOf("pipeline.mjs"));
    return this.Node(copy, [new PrototypeCall(function, arguments)]).Single();
  }

  /// <summary>What the generated code computes of calls: node on the JavaScript, the compiled class on the C#.</summary>
  private string[] Generated(IEnumerable<PrototypeCall> calls)
    => this._to == "javascript"
      ? this.Node(this._generated.Path, calls)
      : [.. calls.Select(call => AsyncFixtures.Awaited(GeneratedCSharp.Compile(this._generated.Text, false).Method(call.Method).Invoke(null, call.FreshArguments())))];

  /// <summary>Translates the fixture with the options of the step; a refused translation is left for a Then step to judge.</summary>
  [When("^it is translated with " + StepText.Quoted + "$")]
  public void TranslatedWith(string options) {
    var arguments = CommandLine.Split(options).ToList();
    this._to = arguments[arguments.IndexOf("--to") + 1];
    var profile = arguments[arguments.IndexOf("--profile") + 1];
    var from = IsScript(scenario.Source) ? "javascript" : "csharp";
    var output = "out-" + Guid.NewGuid().ToString("N");
    var result = scenario.Polypiler.Run(["translate", scenario.Source, "--from", from, "--to", this._to, "--output", output, "--profile", profile]);
    if (result.ExitCode != 0)
      return;

    var file = Directory.GetFiles(scenario.Workspace.Resolve(output)).Single(path => Path.GetFileName(path) != "polypiler.manifest.json");
    this._generated = (file, File.ReadAllText(file));
    if (this._to == "javascript") {
      Assert.That(GeneratedJavaScript.Problems(this._generated.Text, JavaScriptProfile.Parse(profile, "esm")), Is.Empty, this._generated.Text);
    } else {
      var parsed = CSharpProfile.Parse(profile);
      Assert.That(GeneratedCSharp.ComplaintsFor(this._generated.Text, parsed.Name, parsed.LanguageVersionName), Is.Empty, this._generated.Text);
    }
  }

  /// <summary>Run(4) and Run(0) in the generated code and in the source language.</summary>
  [Then(@"^awaiting Run\((\d+)\) returns (\d+) and awaiting Run\((\d+)\) returns (\d+)$")]
  public void Runs(int first, string firstResult, int second, string secondResult) {
    Assert.That(scenario.Log.Last.ExitCode, Is.Zero, scenario.Log.Last.Output);
    Assert.That((DotNet("Run", first), DotNet("Run", second)), Is.EqualTo((firstResult, secondResult)), ".NET");
    Assert.That(this.Generated([new("Run", [first]), new("Run", [second])]), Is.EqualTo(new[] { firstResult, secondResult }), this._generated.Text);
  }

  /// <summary>run(n) of the JavaScript fixture, in node and in the generated C#.</summary>
  [Then(@"^awaiting Run\((\d+)\) returns (\d+), as in node$")]
  public void RunsAsInNode(int n, string expected) {
    Assert.That(scenario.Log.Last.ExitCode, Is.Zero, scenario.Log.Last.Output);
    var name = Named(scenario.Source, "Run");
    Assert.That(this.Original(name, (double)n), Is.EqualTo(expected), "node");
    Assert.That(this.Generated([new(name, [(double)n])]).Single(), Is.EqualTo(expected), this._generated.Text);
  }

  /// <summary>Squares() of both fixtures, in their own runtime and in every admitted profile of the other backend and their own.</summary>
  [Then(@"^Squares\(\) returns \[(\d+), (\d+)\] in \.NET, in node and in every admitted profile of both backends$")]
  public void Squares(string low, string high) {
    var expected = $"[{low},{high}]";
    Assert.That(DotNet("Squares"), Is.EqualTo(expected), ".NET");
    Assert.That(this.Original("squares"), Is.EqualTo(expected), "node");
    foreach (var (fixture, method) in new[] { ("async/Pipeline.cs", "Squares"), ("async/pipeline.mjs", "squares") }) {
      new PrototypeSteps(scenario).Fixture(fixture);
      foreach (var level in new[] { "es2015", "es2017", "es2020", "es2022" }) {
        this.TranslatedWith($"--to javascript --profile {level}");
        Assert.That(this.Generated([new(method, [])]).Single(), Is.EqualTo(expected), $"{fixture} {level}");
      }

      foreach (var profile in CSharpProfile.All.Skip(2)) {
        this.TranslatedWith($"--to csharp --profile {profile.Name}");
        Assert.That(this.Generated([new(method, [])]).Single(), Is.EqualTo(expected), $"{fixture} {profile.Name}");
      }
    }
  }

  /// <summary>Faults() gives the fault of the first call in argument order, where Promise.all alone gives the first in time.</summary>
  [Then("^Faults\\(\\) returns " + StepText.Quoted + " in \\.NET and in the generated JavaScript, although Promise\\.all alone rejects with " + StepText.Quoted + "$")]
  public void Faults(string first, string earliest) {
    Assert.That(DotNet("Faults"), Is.EqualTo(first), ".NET");
    Assert.That(this.Generated([new("Faults", [])]).Single(), Is.EqualTo(first), this._generated.Text);
    var native = this._generated.Text.Replace("await $whenAll([", "await Promise.all([", StringComparison.Ordinal);
    var copy = scenario.Workspace.Resolve("native-" + Guid.NewGuid().ToString("N") + ".mjs");
    File.WriteAllText(copy, native);
    Assert.That(this.Node(copy, [new PrototypeCall("Faults", [])]).Single(), Is.EqualTo(earliest), native);
  }

  /// <summary>Translates the C# method of the scenario to JavaScript, in a file that names the task types.</summary>
  [When("^I run the translate command to JavaScript$")]
  public void ToJavaScript() {
    var path = scenario.Workspace.Resolve(scenario.Source);
    File.WriteAllText(path, "using System.Threading.Tasks;\n" + File.ReadAllText(path));
    scenario.Polypiler.Run("translate", scenario.Source, "--from", "csharp", "--to", "javascript", "--output", "out");
  }

  /// <summary>The command was refused with the exit code and a diagnostic naming the construct, and wrote nothing.</summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void RefusedWith(int exitCode, string code, string words) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
    Assert.That(result.Output, Does.Contain(code).And.Contain(words));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

  /// <summary>The translation was refused with the exit code and the diagnostic.</summary>
  [Then(@"^the exit code is (\d+) and the output contains " + StepText.Quoted + "$")]
  public void Refused(int exitCode, string code) {
    var result = scenario.Log.Last;
    Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.Output);
    Assert.That(result.Output, Does.Contain(code).And.Contain("'async function' cannot be expressed in"));
  }

  /// <summary>Test design 13: the six probes of the issue, run on .NET and on node, still give the traces it documents.</summary>
  [Then("^every ordering probe gives its documented trace in \\.NET and in node$")]
  public void Probes() {
    var compiled = GeneratedCSharp.Compile(CSharpProbes, false);
    var script = scenario.Workspace.Resolve("probes.mjs");
    File.WriteAllText(script, ScriptProbes);
    var (exitCode, output) = Toolchain.RunNode(Path.GetDirectoryName(script)!, script);
    Assert.That(exitCode, Is.Zero, output);
    var node = output.TrimEnd().Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
    foreach (var (probe, dotNet, inNode) in _documented) {
      Assert.That(AsyncFixtures.Awaited(compiled.Method(probe).Invoke(null, [])), Is.EqualTo(dotNet), $"{probe} in .NET");
      Assert.That(node, Does.Contain($"{probe}: {inNode}"), $"{probe} in node");
    }
  }

}
