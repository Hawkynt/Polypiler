using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The steps of <c>tests/features/interop/iop-cs-pinvoke.feature</c> (#1332). An expected value is never computed
/// here: it is what the fixture built into this assembly returns when .NET calls the C library of the operating system.
/// The translated C# is compiled for the running runtime - with the interop generator where it writes
/// <c>[LibraryImport]</c> - and calls the same library; the translated JavaScript runs in <c>node</c> with the binding
/// module of the fixtures.
/// </summary>
[Binding, Scope(Feature = "Native calls through P/Invoke")]
public sealed class PInvokeSteps(PrototypeScenario scenario) {

  private (string Path, string Text) _generated;
  private string _profile = "";

  /// <summary>The fixture's own class LibC, as the C# compiler built it into this assembly.</summary>
  private static Type Fixture => InteropFixtures.CompiledLibC;

  private static object? Call(Type type, string method, params object?[] arguments) => InteropFixtures.Invoke(InteropFixtures.Method(type, method), arguments);

  /// <summary>The quotient and the remainder of a <c>div_t</c> of either build.</summary>
  private static (int, int) Fields(object value) => ((int)value.GetType().GetField("Quot")!.GetValue(value)!, (int)value.GetType().GetField("Rem")!.GetValue(value)!);

  [Given("^the C library fixture of the operating system$")]
  public void CLibrary() {
    scenario.Workspace.Copy(InteropFixtures.PathOf(InteropFixtures.LibC), InteropFixtures.LibC);
    scenario.Source = InteropFixtures.LibC;
  }

  [Given("^the C# declaration " + StepText.Quoted + "$")]
  public void Declaration(string declaration) => scenario.Snippet(StepText.Unescape(declaration));

  [When("^it is translated with " + StepText.Quoted + "$")]
  public void Translated(string options) {
    var arguments = CommandLine.Split(options).ToList();
    this._profile = arguments[arguments.IndexOf("--profile") + 1];
    this._generated = scenario.Translate(scenario.Source, "csharp", "csharp", "--profile", this._profile);
  }

  [When("^it is translated without a binding with " + StepText.Quoted + "$")]
  public void TranslatedWithoutBinding(string options) => scenario.Polypiler.Run(["translate", scenario.Source, .. CommandLine.Split(options), "--output", "out"]);

  [When("^it is translated with " + StepText.Quoted + " and the binding module " + StepText.Quoted + "$")]
  public void TranslatedWithBinding(string options, string binding) {
    var arguments = CommandLine.Split(options).Skip(4).ToArray();
    this._generated = scenario.Translate(scenario.Source, "csharp", "javascript", [.. arguments, "--bind", $"{InteropFixtures.Library}=./{binding}"]);
    File.Copy(InteropFixtures.PathOf(binding), Path.Combine(Path.GetDirectoryName(this._generated.Path)!, binding));
  }

  [Then("^the output declares StrLen, Abs, Div, MemSet and StrToL with " + StepText.Quoted + " and StrLenUtf8 with " + StepText.Quoted + "$")]
  public void Declares(string dllImport, string utf8) {
    var text = this._generated.Text;
    foreach (var name in new[] { "StrLen", "Abs", "Div", "MemSet", "StrToL" })
      Assert.That(text, Does.Match($@"\[global::System\.Runtime\.InteropServices\.{dllImport}\([^\]]*\]\n\s*public static extern [^ ]+ {name}\("), name);

    // Without LPUTF8Str the extern takes the bytes behind a wrapper of the source's signature.
    if (utf8 == "DllImport") {
      Assert.That(text, Does.Match(@"DllImport\([^\]]*\]\n\s*private static extern [^ ]+ StrLenUtf8_native\(byte\[\] s\);"), text);
      return;
    }

    var at = text.LastIndexOf(" StrLenUtf8(", StringComparison.Ordinal);
    var declaration = text[text.LastIndexOf("\n\n", at, StringComparison.Ordinal)..text.IndexOf('\n', at)];
    Assert.That(declaration, Does.Contain(utf8), text);
  }

  [Then("^it compiles against the reference assemblies of the profile without diagnostics$")]
  public void Compiles() {
    var profile = CSharpProfile.Parse(this._profile);
    Assert.That(InteropBuild.Complaints(this._generated.Text, profile.Name, profile.LanguageVersionName), Is.Empty, this._generated.Text);
  }

  [Then(@"^compiled for the running runtime, StrLen\(""hello""\) returns (\d+), StrLenUtf8\(""Grüße""\) returns (\d+), Abs\(-7\) returns (\d+), Div\(7, 2\) returns (-?\d+) and (-?\d+), Div\(-7, 2\) returns (-?\d+) and (-?\d+), StrToL\(""ff"", out _, 16\) returns (\d+)$")]
  public void CallsTheLibrary(int strlen, int utf8, int abs, int quotient, int remainder, int negativeQuotient, int negativeRemainder, int strtol) {
    foreach (var type in new[] { Fixture, InteropBuild.Compile(this._generated.Text) }) {
      Assert.That(Call(type, "StrLen", "hello"), Is.EqualTo((nuint)strlen), type.FullName);
      Assert.That(Call(type, "StrLenUtf8", "Grüße"), Is.EqualTo((nuint)utf8), type.FullName);
      Assert.That(Call(type, "Abs", -7), Is.EqualTo(abs), type.FullName);
      Assert.That(Fields(Call(type, "Div", 7, 2)!), Is.EqualTo((quotient, remainder)), type.FullName);
      Assert.That(Fields(Call(type, "Div", -7, 2)!), Is.EqualTo((negativeQuotient, negativeRemainder)), type.FullName);
      Assert.That(Convert.ToInt64(Call(type, "StrToL", "ff", null, 16)), Is.EqualTo(strtol), type.FullName);
    }
  }

  [Then(@"^after MemSet\(b, 0xAB, 3\) on a 4-byte zero array b is (\d+), (\d+), (\d+), (\d+)$")]
  public void ArrayInAndOut(byte first, byte second, byte third, byte fourth) {
    foreach (var type in new[] { Fixture, InteropBuild.Compile(this._generated.Text) }) {
      var buffer = new byte[4];
      Call(type, "MemSet", buffer, 0xAB, (nuint)3);
      Assert.That(buffer, Is.EqualTo(new[] { first, second, third, fourth }), type.FullName);
    }
  }

  [Then(@"^Abs\((-?\d+)\) returns (-?\d+) in the fixture and in the translated C# of every profile$")]
  public void NotRepaired(int argument, int result) {
    Assert.That(Call(Fixture, "Abs", argument), Is.EqualTo(result));
    foreach (var profile in CSharpProfile.All) {
      var (_, text) = scenario.Translate(scenario.Source, "csharp", "csharp", "--profile", profile.Name);
      Assert.That(Call(InteropBuild.Compile(text), "Abs", argument), Is.EqualTo(result), profile.Name);
    }
  }

  [Then("^calling the function of the missing library throws DllNotFoundException and the one with the missing entry point throws EntryPointNotFoundException$")]
  public void FailsAsDotNetFails() {
    var translated = InteropBuild.Compile(this._generated.Text);
    var fixture = typeof(InteropFixtures).Assembly.GetType("Polypiler.Fixtures.Prototype.Interop.Missing", true)!;
    var entry = OperatingSystem.IsWindows() ? "NoEntryWindows" : "NoEntryLinux";
    foreach (var type in new[] { fixture, translated }) {
      Assert.Throws<DllNotFoundException>(() => Call(type, "NoLibrary"), type.Name);
      Assert.Throws<EntryPointNotFoundException>(() => Call(type, entry), type.Name);
    }
  }

  [Then("^the exit code is (\\d+), the output contains " + StepText.Quoted + ", " + StepText.Quoted + " and the library of the fixture, and nothing is written$")]
  public void RefusedForTheLibrary(int exitCode, string code, string symbol) => this.Refused(exitCode, code, symbol, InteropFixtures.Library);

  [Then("^the exit code is (\\d+), the output contains " + StepText.Quoted + ", " + StepText.Quoted + " and " + StepText.Quoted + ", and nothing is written$")]
  public void Refused(int exitCode, string code, string first, string second) {
    var last = scenario.Log.Last;
    Assert.That(last.ExitCode, Is.EqualTo(exitCode), last.Output);
    Assert.That(last.Output, Does.Contain(code).And.Contain(first).And.Contain(second));
    Assert.That(Directory.Exists(scenario.Workspace.Resolve("out")), Is.False);
  }

  [Then(@"^in node StrLen\(""hello""\) returns (\d+), Abs\(-7\) returns (\d+) and Div\(7, 2\) returns \{ Quot: (-?\d+), Rem: (-?\d+) \}, as in the \.NET run$")]
  public void NodeComputesWhatDotNetComputes(int strlen, int abs, int quotient, int remainder) {
    var directory = Path.GetDirectoryName(this._generated.Path)!;
    var module = Path.GetFileName(this._generated.Path);
    var esm = module.EndsWith(".mjs", StringComparison.Ordinal);
    var script = (esm ? $"import * as m from \"./{module}\";\n" : $"const m = require(\"./{module}\");\n")
      + "const n = (v) => typeof v === \"object\" && v !== null && \"hi\" in v ? v.hi * 4294967296 + v.lo : Number(v);\n"
      + "const d = m.Div(7, 2);\n"
      + "console.log(JSON.stringify([n(m.StrLen(\"hello\")), m.Abs(-7), d.Quot, d.Rem]));\n";
    var runner = Path.Combine(directory, esm ? "run.mjs" : "run.cjs");
    File.WriteAllText(runner, script);
    var (exitCode, output) = Toolchain.RunNode(directory, runner);
    Assert.That(exitCode, Is.Zero, output);
    Assert.That(JsonSerializer.Deserialize<int[]>(output.Trim()), Is.EqualTo(new[] { strlen, abs, quotient, remainder }), this._generated.Text);
    var dotNet = Fields(Call(Fixture, "Div", 7, 2)!);
    Assert.That(new[] { (int)(nuint)Call(Fixture, "StrLen", "hello")!, (int)Call(Fixture, "Abs", -7)!, dotNet.Item1, dotNet.Item2 }, Is.EqualTo(new[] { strlen, abs, quotient, remainder }));
  }

  [Then("^the manifest records every native function as an unverified external$")]
  public void UnverifiedExternals() {
    using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(this._generated.Path)!, "polypiler.manifest.json")));
    var caveats = manifest.RootElement.GetProperty("caveats").EnumerateArray().Where(caveat => caveat.GetProperty("code").GetString() == "unverified-external")
      .Select(caveat => caveat.GetProperty("message").GetString()!).ToList();
    foreach (var symbol in new[] { "strlen", "abs", "div", "memset", "strtol" })
      Assert.That(caveats, Has.Some.Contains($"'{symbol}' of {InteropFixtures.Library}"), symbol);

    Assert.That(manifest.RootElement.GetProperty("nativeDependencies").EnumerateArray().Select(dependency => dependency.GetProperty("name").GetString()), Is.EqualTo(new[] { InteropFixtures.Library }));
  }

}
