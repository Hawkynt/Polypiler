using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.CSharp;

/// <summary>
/// The <c>translate</c> command with <c>--to csharp</c>, run inside the test process. The acceptance
/// tests start the real executable.
/// </summary>
[TestFixture]
public sealed class TranslateToCSharpTests {

  private string _directory = "";
  private string _out = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    this._out = Path.Combine(this._directory, "out");
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private static (int ExitCode, string Output, string Error) Invoke(params string[] arguments) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(arguments, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  private (int ExitCode, string Output, string Error) Emit(IrModule module, params (string Option, string Value)[] options) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var all = options.Append(("--to", "csharp")).Append(("--output", this._out)).ToDictionary(option => option.Item1, option => option.Item2);
    var exitCode = TranslateCommand.Emit(module, all, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  /// <summary>The round trip: the file the command writes is C# again and computes the published value.</summary>
  [Test]
  public void Translate_WritesTheClassAndNamesIt() {
    var path = Path.Combine(this._out, "Adler32.cs");
    var (exitCode, output, error) = Invoke("translate", PrototypeFixture.PathOf("Adler32"), "--from", "csharp", "--to", "csharp", "--output", this._out);
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    Assert.That(output.Trim(), Is.EqualTo(path));
    Assert.That(Directory.GetFiles(this._out), Is.EqualTo(new[] { path }));
    var text = File.ReadAllText(path);
    Assert.That(text, Does.Contain("\npublic static class Adler32 {\n").And.Contain("  public static uint Compute(byte[] data) {\n").And.Not.Contain("namespace"));
    Assert.That(GeneratedCSharp.Compile(text, true).Method("Compute").Invoke(null, [PrototypeCases.Text("Wikipedia")]), Is.EqualTo(300286872u));
  }

  /// <summary>Scenario: The namespace option wraps the class — wherever the option stands.</summary>
  [Test]
  public void Namespace_IsWrittenIntoTheFile() {
    var (exitCode, _, error) = Invoke("translate", "--namespace", "Demo.Checksums", PrototypeFixture.PathOf("Crc32"), "--to", "csharp", "--output", this._out, "--from", "csharp");
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    var text = File.ReadAllText(Path.Combine(this._out, "Crc32.cs"));
    Assert.That(text.Split('\n').First(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal)), Is.EqualTo("namespace Demo.Checksums;"));
    Assert.That(GeneratedCSharp.Compile(text, false).ClassName, Is.EqualTo("Demo.Checksums.Crc32"));
  }

  /// <summary>Test design 10: a command line that cannot be carried out is PLY0001, exit code 2, and writes nothing.</summary>
  [TestCase(new[] { "--to", "cobol", "--output", "out" }, "unknown target language 'cobol'; the prototype writes 'javascript' and 'csharp'", TestName = "Misuse: unknown target")]
  [TestCase(new[] { "--to", "CSharp", "--output", "out" }, "unknown target language 'CSharp'", TestName = "Misuse: the target is case-sensitive")]
  [TestCase(new[] { "--to", "csharp" }, "the option '--output' is missing", TestName = "Misuse: missing output")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--namespace" }, "the option '--namespace' needs a value", TestName = "Misuse: namespace without a value")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--namespace", "" }, "'' is not a namespace name", TestName = "Misuse: empty namespace")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--namespace", "2fast" }, "'2fast' is not a namespace name", TestName = "Misuse: namespace starting with a digit")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--namespace", "Demo..Checksums" }, "'Demo..Checksums' is not a namespace name", TestName = "Misuse: namespace with an empty part")]
  [TestCase(new[] { "--to", "csharp", "--output", "out", "--namespace", "Demo; class X" }, "'Demo; class X' is not a namespace name", TestName = "Misuse: namespace that is more than a name")]
  [TestCase(new[] { "--to", "javascript", "--output", "out", "--namespace", "Demo" }, "the option '--namespace' does not apply to the target language 'javascript'", TestName = "Misuse: namespace for another target")]
  public void Misuse_IsAUsageError(string[] arguments, string message) {
    string[] all = ["translate", PrototypeFixture.PathOf("Adler32"), "--from", "csharp", .. arguments.Select(argument => argument == "out" ? this._out : argument)];
    var (exitCode, output, error) = Invoke(all);
    Assert.That((exitCode, output), Is.EqualTo((Program.UsageError, "")));
    Assert.That(error, Does.StartWith("error PLY0001: " + message)
      .And.Contain("usage: polypiler translate <file.cs> --from csharp --to <javascript|csharp> --output <directory> [--namespace <name>]"));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

  /// <summary>What the frontend refuses is refused whatever the target is.</summary>
  [TestCase("public static long F(long a) => a + 1;", TranslateCommand.UnsupportedConstruct, "PLY1002")]
  [TestCase("public static int F( => 1;", TranslateCommand.InvalidSource, "PLY1000")]
  public void RefusedSource_WritesNothing(string method, int expected, string code) {
    var source = Path.Combine(this._directory, "Snippet.cs");
    File.WriteAllText(source, PrototypeFixture.Wrap(method));
    var (exitCode, output, error) = Invoke("translate", source, "--from", "csharp", "--to", "csharp", "--output", this._out);
    Assert.That((exitCode, output), Is.EqualTo((expected, "")));
    Assert.That(error, Does.Contain($"error {code}: "));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

  private sealed record Riddle : IrStmt;

  /// <summary>Test design 9: an IR construct the C# backend does not know is exit code 3 and writes nothing.</summary>
  [Test]
  public void UnknownConstruct_IsExitCode3AndWritesNothing() {
    var module = new IrModule("M", [new IrFunction("F", true, IrType.Void, [], [], new IrBlock([new Riddle()]))]);
    var (exitCode, output, error) = this.Emit(module);
    Assert.That((exitCode, TranslateCommand.UnsupportedConstruct, output), Is.EqualTo((3, 3, "")));
    Assert.That(error.TrimEnd(), Is.EqualTo("error PLY1052: 'Riddle' is not supported by the C# backend yet"));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

  /// <summary>Test design 9: IR the verifier rejects is an internal error, exit code 70, and writes nothing.</summary>
  [Test]
  public void IllTypedIr_IsAnInternalError() {
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [], [], new IrBlock([
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrConst(IrType.I32, 1), new IrConst(IrType.Bool, 1)))
    ]))]);
    var (exitCode, output, error) = this.Emit(module);
    Assert.That((exitCode, output), Is.EqualTo((TranslateCommand.InternalError, "")));
    Assert.That(error, Does.StartWith("error PLY0100: ill-typed IR in 'F': Add is applied to i32 and bool"));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

  [Test]
  public void Emit_TakesTheNamespaceFromTheOptions() {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap("public static int F() => 1;"));
    var (exitCode, output, error) = this.Emit(module, ("--namespace", "Demo"));
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    Assert.That(File.ReadAllText(output.Trim()), Does.Contain("\nnamespace Demo;\n"));
  }

  /// <summary>An output directory that cannot exist, because a file has its name.</summary>
  [Test]
  public void OutputThatIsAFile_IsAUsageError() {
    var blocked = Path.Combine(this._directory, "Snippet.cs");
    File.WriteAllText(blocked, PrototypeFixture.Wrap("public static int F() => 1;"));
    var (exitCode, output, error) = Invoke("translate", blocked, "--from", "csharp", "--to", "csharp", "--output", blocked);
    Assert.That((exitCode, output), Is.EqualTo((Program.UsageError, "")));
    Assert.That(error, Does.StartWith("error PLY0001: the output directory '").And.Contain("cannot be written"));
  }

  [Test]
  public void Help_NamesBothTargetsAndTheNamespaceOption()
    => Assert.That(Invoke("--help").Output, Does.Contain(TranslateCommand.UsageLine).And.Contain("--to <javascript|csharp>").And.Contain("[--namespace <name>]"));

}
