using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Cli;

/// <summary>
/// The manifest <c>translate</c> writes beside the generated files: valid, in the state
/// <c>generated</c>, free of absolute paths, and saying what the header of the generated file says.
/// </summary>
[TestFixture]
public sealed class TranslateManifestTests {

  private string _directory = "";
  private string _out = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-manifest-" + Guid.NewGuid().ToString("N"));
    this._out = Path.Combine(this._directory, "out");
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private (int ExitCode, string Output, string Error) Invoke(params string[] arguments) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(arguments, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  private string Source(string name, string text) {
    var path = Path.Combine(this._directory, name);
    File.WriteAllText(path, text);
    return path;
  }

  private Manifest Read() {
    var bag = new DiagnosticBag();
    var manifest = ManifestReader.Read(Path.Combine(this._out, Manifest.FileName), bag);
    Assert.That(bag.Items, Is.Empty);
    return manifest!;
  }

  [Test]
  public void Translate_WritesAValidManifestInTheStateGenerated() {
    var source = this.Source("Snippet.cs", PrototypeFixture.Wrap("public static int F(int a, byte[] b) => a + b.Length;"));
    var (exitCode, output, error) = this.Invoke("translate", source, "--from", "csharp", "--to", "javascript", "--output", this._out);
    Assert.That((exitCode, error, output.Trim()), Is.EqualTo(((int)ExitCode.Success, "", Path.Combine(this._out, "Snippet.mjs"))));

    var manifest = this.Read();
    var generated = File.ReadAllBytes(Path.Combine(this._out, "Snippet.mjs"));
    Assert.That(new object?[] {
      manifest.State, manifest.Command, manifest.Inputs.Single(), manifest.Frontend, manifest.Target, manifest.Outputs.Single(),
      manifest.Api.Single(), manifest.Intrinsics.Count, manifest.FloatingPoint, manifest.Compiler, manifest.Commands, manifest.Licensing.OutputLicence
    }, Is.EqualTo(new object?[] {
      ManifestState.Generated, new[] { "translate", "../Snippet.cs", "--from", "csharp", "--to", "javascript", "--output", "." },
      new ManifestInput("../Snippet.cs", ManifestWriter.Sha256(File.ReadAllBytes(source)), null), new ManifestFrontend("csharp", null),
      new ManifestTarget("javascript", "es2022, esm", NativeAccuracy.Node, "any"), new ManifestOutput("Snippet.mjs", ManifestWriter.Sha256(generated), ManifestOutputRole.Source),
      new ManifestApiSymbol("F", "i32 F(i32 a, u8[] b)"), 0, null, new ManifestCompiler(ProductInfo.Name, ProductInfo.Version, IrModuleFormat.Version),
      new ManifestCommands("polypiler translate ../Snippet.cs --from csharp --to javascript --output .", null), "NOASSERTION"
    }));
  }

  [Test]
  public void TheFloatingPointRecord_IsTheHeaders() {
    var source = this.Source("Snippet.cs", PrototypeFixture.Wrap("public static double F(double a, float b) => System.Math.Sin(a) + System.MathF.Cos(b) + System.Math.Sqrt(a);"));
    var (exitCode, _, error) = this.Invoke("translate", source, "--from", "csharp", "--to", "csharp", "--output", this._out, "--profile", "net10.0");
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));

    var manifest = this.Read();
    var header = File.ReadAllLines(Path.Combine(this._out, "Snippet.cs")).Where(line => line.StartsWith("//   ", StringComparison.Ordinal)).ToArray();
    Assert.That(manifest.FloatingPoint!.Natives.Select(native => $"//   {native.Operation["INTRINSICS.".Length..]} of {native.Format}: {native.Accuracy}, slack {native.Slack} on {native.Runtime}, bound {native.Bound}"),
      Is.EqualTo(header));
    Assert.That((manifest.FloatingPoint.Mode, header.Length, manifest.Target.Profile, manifest.Target.Runtime), Is.EqualTo(("accurate", 2, "net10.0, C# 14", NativeAccuracy.DotNet)));
    Assert.That(manifest.Intrinsics, Is.EqualTo(new[] { "INTRINSICS.Cos", "INTRINSICS.Sin", "INTRINSICS.Sqrt" }));
  }

  [Test]
  public void StrictFloatingPoint_HasNoNatives() {
    var source = this.Source("Snippet.cs", PrototypeFixture.Wrap("public static double F(double a) => System.Math.Sin(a);"));
    var (exitCode, _, error) = this.Invoke("translate", source, "--from", "csharp", "--to", "javascript", "--output", this._out, "--float", "strict");
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
    var manifest = this.Read();
    Assert.That((manifest.FloatingPoint!.Mode, manifest.FloatingPoint.Natives.Count), Is.EqualTo(("strict", 0)));
    Assert.That(manifest.Command.TakeLast(2), Is.EqualTo(new[] { "--float", "strict" }));
  }

  [Test]
  public void TheOptionsOfTheCommandLine_AreRecordedWithTheOptimization() {
    var source = this.Source("Snippet.cs", PrototypeFixture.Wrap("public static uint F(uint x) => (x << 8) | (x >> 24);"));
    var (exitCode, _, _) = this.Invoke("translate", source, "--from", "csharp", "--to", "csharp", "--output", this._out, "--explain-opt", "--namespace", "Demo");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success));
    var manifest = this.Read();
    Assert.That(manifest.Command.Skip(8), Is.EquivalentTo(new[] { "--explain-opt", "--namespace", "Demo" }));
    Assert.That(manifest.Optimization.Passes.Select(pass => pass.ProofClass).Distinct(), Is.EqualTo(new[] { "unproven" }));
    Assert.That(manifest.Optimization.Passes, Is.Not.Empty);
  }

  [Test]
  public void DisabledOptimizations_AreRecorded() {
    var source = this.Source("Snippet.cs", PrototypeFixture.Wrap("public static uint F(uint x) => (x << 8) | (x >> 24);"));
    var (exitCode, _, _) = this.Invoke("translate", source, "--from", "csharp", "--to", "csharp", "--output", this._out, "--disable-opt", "O0100");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success));
    var manifest = this.Read();
    Assert.That((manifest.Optimization.Level, manifest.Optimization.Disabled.Single(), manifest.Optimization.Passes.Any(pass => pass.Id == "O0100")), Is.EqualTo(("default", "O0100", false)));
  }

  [Test]
  public void TheCaveatsOfAJavaScriptImport_AreRecorded() {
    var source = this.Source("snippet.js", "function g(d, a) { return ((d[a | 0] / (a | 0)) | 0) + (a << 1); } function f() { return g(new Uint8Array(1), 1); }");
    var (exitCode, _, _) = this.Invoke("translate", source, "--from", "javascript", "--to", "javascript", "--output", this._out, "--export", "f,g", "--typed-arrays", "--profile", "es5", "--module", "cjs");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success));
    var manifest = this.Read();
    Assert.That(manifest.Caveats.Select(caveat => caveat.Code), Is.EqualTo(new[] { "js-oob-read", "js-div-zero", "js-param-integer-inferred", "js-param-array-inferred" }));
    Assert.That((manifest.Caveats.All(caveat => caveat.Message.Length > 0), manifest.Frontend.Language), Is.EqualTo((true, "javascript")));
    Assert.That(manifest.Command.Skip(8), Is.EquivalentTo(new[] { "--export", "f,g", "--profile", "es5", "--module", "cjs", "--typed-arrays" }));
  }

  [Test]
  public void AFailedTranslation_WritesNoManifest() {
    var source = this.Source("Snippet.cs", PrototypeFixture.Wrap("public static object F() => null;"));
    Assert.That(this.Invoke("translate", source, "--from", "csharp", "--to", "javascript", "--output", this._out).ExitCode, Is.Not.EqualTo((int)ExitCode.Success));
    Assert.That(File.Exists(Path.Combine(this._out, Manifest.FileName)), Is.False);
  }

  [Test]
  public void AManifestThatCannotBeWritten_IsPly0005() {
    var source = this.Source("Snippet.cs", PrototypeFixture.Wrap("public static int F() => 1;"));
    Directory.CreateDirectory(Path.Combine(this._out, Manifest.FileName));
    var (exitCode, _, error) = this.Invoke("translate", source, "--from", "csharp", "--to", "javascript", "--output", this._out);
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.EnvironmentError, error)));
    Assert.That(error, Does.Contain("PLY0005"));
    Assert.That(Directory.GetFiles(this._out).Select(Path.GetFileName), Is.EqualTo(new[] { "Snippet.mjs" }));
  }

  [Test]
  public void AnInputWithAByteOrderMark_IsHashedAsItsBytes() {
    var source = Path.Combine(this._directory, "Snippet.cs");
    File.WriteAllText(source, PrototypeFixture.Wrap("public static int F() => 1;"), new UTF8Encoding(true));
    Assert.That(this.Invoke("translate", source, "--from", "csharp", "--to", "javascript", "--output", this._out).ExitCode, Is.EqualTo((int)ExitCode.Success));
    Assert.That(this.Read().Inputs.Single().Sha256, Is.EqualTo(ManifestWriter.Sha256(File.ReadAllBytes(source))));
  }

  [TestCase("/w/out", "/w/src/a.cs", "../src/a.cs")]
  [TestCase("/w/out", "/w/out/sub/a.cs", "sub/a.cs")]
  [TestCase("/w/out", "/v/a.cs", "a.cs")]
  [TestCase("/w/x/out", "/w/a.cs", "../../a.cs")]
  [TestCase("/out", "/a.cs", "a.cs")]
  public void TheInputPath_IsRelative_UnlessItWouldClimbToTheRoot(string output, string input, string recorded) {
    if (OperatingSystem.IsWindows())
      (output, input) = ("C:" + output.Replace('/', '\\'), "C:" + input.Replace('/', '\\'));

    Assert.That(TranslateCommand.InputPath(output, input), Is.EqualTo(recorded));
  }

  [Test]
  public void AnInputOnAnotherDrive_IsRecordedByItsName() {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("drives exist on Windows only");

    Assert.That(TranslateCommand.InputPath(@"C:\w\out", @"D:\src\a.cs"), Is.EqualTo("a.cs"));
  }

}
