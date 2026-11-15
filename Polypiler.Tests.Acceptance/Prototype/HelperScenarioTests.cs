using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The scenario of #1353 in <c>node</c>: <c>ushort.RotateLeft(x, 3)</c> for ECMAScript 5 is straight-line
/// code once its helper is inlined and folded, and <c>F(0x8001)</c> returns <c>0x000C</c> - with the
/// inlining and without it, where the module calls the helper.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("core")]
[Category("Prototype")]
public sealed class HelperScenarioTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  [TestCase(new string[0], false)]
  [TestCase(new[] { "--disable-opt", "O0006" }, true)]
  public void ConstantRotate_ComputesTheReferenceInNode(string[] options, bool calls) {
    var source = Path.Combine(this._directory, "Snippet.cs");
    File.WriteAllText(source, PrototypeFixture.Wrap("public static ushort F(ushort x) => ushort.RotateLeft(x, 3);"));
    var output = Path.Combine(this._directory, "out");
    using var standard = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(["translate", source, "--from", "csharp", "--to", "javascript", "--profile", "es5", "--output", output, .. options], standard, error);
    Assert.That((exitCode, error.ToString()), Is.EqualTo(((int)ExitCode.Success, "")));
    var module = File.ReadAllText(Path.Combine(output, "Snippet.cjs"));
    Assert.That(module.Contains("RotateLeftNarrow(x, 3, 16)", StringComparison.Ordinal), Is.EqualTo(calls), module);

    File.WriteAllText(Path.Combine(output, "call.cjs"), "const m = require(\"./Snippet.cjs\");\nconsole.log(m.F(0x8001));\n");
    var (code, printed) = Toolchain.RunNode(output, "call.cjs");
    Assert.That((code, printed.Trim()), Is.EqualTo((0, "12")));
  }

}
