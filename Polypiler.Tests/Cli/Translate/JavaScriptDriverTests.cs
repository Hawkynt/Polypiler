using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Cli.Translate;

/// <summary>
/// The driver <c>translate --verify</c> runs generated JavaScript with: the signatures it passes, the
/// program it writes, and how its lines become output images.
/// </summary>
[TestFixture]
public sealed class JavaScriptDriverTests {

  private static CallSignature Signature(string parameters, string result)
    => CallSignature.Parse(System.Text.Json.Nodes.JsonNode.Parse($$"""{ "parameters": [{{parameters}}], "result": "{{result}}" }"""));

  [TestCase("""{ "name": "a", "type": "u32" }, { "name": "b", "type": "i8" }""", "bool")]
  [TestCase("""{ "name": "a", "type": "bool" }""", "i32")]
  [TestCase("", "u16")]
  public void AScalarSignature_IsDriven(string parameters, string result)
    => Assert.That(JavaScriptDriver.Unsupported(Signature(parameters, result)), Is.Null);

  [TestCase("""{ "name": "a", "type": "u64" }""", "u32", "the image type 'u64' is not one the JavaScript driver passes; it takes bool and the integers of up to 32 bits")]
  [TestCase("""{ "name": "a", "type": "u32" }""", "f64", "the image type 'f64' is not one the JavaScript driver passes")]
  [TestCase("""{ "name": "a", "type": "u32" }""", "void", "the signature has no result")]
  [TestCase("""{ "name": "a", "type": "u32", "mode": "out" }""", "u32", "the parameter 'a' is out; the JavaScript driver passes values in only")]
  [TestCase("""{ "name": "a", "type": "u32", "mode": "inout" }""", "u32", "the parameter 'a' is inout")]
  public void AnotherSignature_IsNot(string parameters, string result, string reason)
    => Assert.That(JavaScriptDriver.Unsupported(Signature(parameters, result)), Does.StartWith(reason));

  [Test]
  public void TheScript_CallsTheExportWithTheDecodedArguments() {
    var signature = Signature("""{ "name": "a", "type": "i32" }, { "name": "b", "type": "bool" }""", "i32");
    var script = JavaScriptDriver.Script("M.mjs", "F", signature, [new TestVector("v", [0xFF, 0xFF, 0xFF, 0xFF, 1], [0, 0, 0, 0]), new TestVector("w", [2, 0, 0, 0, 0], [0, 0, 0, 0])]);
    Assert.That(script, Does.Contain("const calls = [[-1, true], [2, false]];").And.Contain("await import(\"./M.mjs\")").And.Contain("m[\"F\"] ?? (m.default ? m.default[\"F\"] : undefined)"));
  }

  [Test, Category("EndToEnd")]
  public void TheScript_RunsUnderNodeAndReportsEveryCall() {
    if (PathToolchainResolver.ForHost().Resolve(TranslationVerifier.Toolchain).Executable is not { } node) {
      Assert.Ignore("node is not on the search path");
      return;
    }

    var directory = Path.Combine(Path.GetTempPath(), "polypiler-driver-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var signature = Signature("""{ "name": "a", "type": "u32" }""", "u32");
      File.WriteAllText(Path.Combine(directory, "M.mjs"), "export function F(a) { if (a === 7) throw new RangeError('x'); return (a + 1) >>> 0; }");
      File.WriteAllText(Path.Combine(directory, JavaScriptDriver.FileName), JavaScriptDriver.Script("M.mjs", "F", signature, [new TestVector("a", [1, 0, 0, 0], null), new TestVector("b", [7, 0, 0, 0], null)]));
      var info = new System.Diagnostics.ProcessStartInfo(node, JavaScriptDriver.FileName) { WorkingDirectory = directory, RedirectStandardOutput = true };
      using var process = System.Diagnostics.Process.Start(info)!;
      var output = process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      Assert.That(output, Is.EqualTo("ok 2\ntrap RangeError\n"));
    } finally {
      Directory.Delete(directory, true);
    }
  }

  [TestCase("ok 7\n", new byte[] { 7, 0, 0, 0 })]
  [TestCase("ok -1\r\n", new byte[0])]
  [TestCase("ok 4294967295\n", new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })]
  [TestCase("ok x\n", new byte[0])]
  [TestCase("trap TypeError\n", new byte[0])]
  [TestCase("", new byte[0])]
  public void AnIntegerLine_BecomesItsImage(string stdout, byte[] image)
    => Assert.That(JavaScriptDriver.Outputs(Signature("", "u32"), stdout, 1), Is.EqualTo(new[] { image }));

  [TestCase("ok true\n", new byte[] { 1 })]
  [TestCase("ok false\n", new byte[] { 0 })]
  [TestCase("ok 1\n", new byte[0])]
  public void ABoolLine_BecomesItsImage(string stdout, byte[] image)
    => Assert.That(JavaScriptDriver.Outputs(Signature("", "bool"), stdout, 1), Is.EqualTo(new[] { image }));

  [Test]
  public void NullArguments_AreRefused() {
    var signature = Signature("", "u32");
    Assert.That(() => JavaScriptDriver.Unsupported(null!), Throws.ArgumentNullException);
    Assert.That(() => JavaScriptDriver.Script(null!, "F", signature, []), Throws.ArgumentNullException);
    Assert.That(() => JavaScriptDriver.Script("M.mjs", null!, signature, []), Throws.ArgumentNullException);
    Assert.That(() => JavaScriptDriver.Script("M.mjs", "F", null!, []), Throws.ArgumentNullException);
    Assert.That(() => JavaScriptDriver.Script("M.mjs", "F", signature, null!), Throws.ArgumentNullException);
    Assert.That(() => JavaScriptDriver.Outputs(null!, "", 0), Throws.ArgumentNullException);
    Assert.That(() => JavaScriptDriver.Outputs(signature, null!, 0), Throws.ArgumentNullException);
  }

}

/// <summary>The toolchains of <c>translate --verify</c> found on a search path.</summary>
[TestFixture]
public sealed class PathToolchainResolverTests {

  private string _directory = "";

  [SetUp]
  public void CreateDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-path-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteDirectory() => Directory.Delete(this._directory, true);

  [TestCase("node", "node", false)]
  [TestCase("node", "node.exe", true)]
  [TestCase(SandboxFactory.BubblewrapToolchain, "bwrap", false)]
  [TestCase("python", "python", false)]
  public void AnExecutableOnThePath_IsFound(string key, string file, bool windows) {
    File.WriteAllText(Path.Combine(this._directory, file), "");
    var path = string.Join(Path.PathSeparator, Path.Combine(this._directory, "missing"), this._directory);
    Assert.That(new PathToolchainResolver(path, windows).Resolve(key).Executable, Is.EqualTo(Path.Combine(this._directory, file)));
  }

  [Test]
  public void AMissingExecutable_IsPLY0021() {
    var resolution = new PathToolchainResolver(this._directory, false).Resolve("node");
    Assert.That((resolution.Executable, resolution.Problem!.Code, resolution.Problem.Message), Is.EqualTo(((string?)null, "PLY0021", "toolchain 'node' is not installed (probe: node --version)")));
  }

  [Test]
  public void AnEmptyPath_FindsNothing()
    => Assert.That(new PathToolchainResolver("", false).Resolve("node").Executable, Is.Null);

  [Test]
  public void ANullKey_IsRefused()
    => Assert.That(() => new PathToolchainResolver("", false).Resolve(null!), Throws.ArgumentNullException);

}
