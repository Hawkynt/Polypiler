using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Prototype;

/// <summary>The <c>translate</c> command, run inside the test process. The acceptance tests start the real executable.</summary>
[TestFixture]
public sealed class TranslateCommandTests {

  private const string Usage = "usage: polypiler translate <file> --from <csharp|javascript> --to <javascript|csharp> --output <directory> [--export <name>[,<name>...]] [--explain-types] [--namespace <name>]";

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

  private string Source(string text) {
    var path = Path.Combine(this._directory, "Snippet.cs");
    File.WriteAllText(path, text);
    return path;
  }

  private (int ExitCode, string Output, string Error) Translate(string source)
    => Invoke("translate", this.Source(source), "--from", "csharp", "--to", "javascript", "--output", this._out);

  [Test]
  public void Translate_WritesTheModuleAndNamesIt() {
    var path = Path.Combine(this._out, "Adler32.mjs");
    var (exitCode, output, error) = Invoke("translate", PrototypeFixture.PathOf("Adler32"), "--from", "csharp", "--to", "javascript", "--output", this._out);
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    Assert.That(output.Trim(), Is.EqualTo(path));
    Assert.That(File.ReadAllText(path), Does.Contain("export function Compute(data) {"));
    Assert.That(Directory.GetFiles(this._out), Is.EqualTo(new[] { path }));
  }

  /// <summary>The options may stand before the file and in any order; an existing directory is reused.</summary>
  [Test]
  public void Translate_AcceptsOptionsInAnyOrder() {
    Directory.CreateDirectory(this._out);
    var source = this.Source(PrototypeFixture.Wrap("public static int F() => 1;"));
    var (exitCode, _, error) = Invoke("translate", "--output", this._out, "--to", "javascript", source, "--from", "csharp");
    Assert.That((exitCode, error), Is.EqualTo((Program.Success, "")));
    Assert.That(File.Exists(Path.Combine(this._out, "Snippet.mjs")), Is.True);
  }

  /// <summary>Every snippet the frontend tests refuse, so that the command is held to the same list.</summary>
  private static string[] UnsupportedMethods() => typeof(CSharpFrontendTests)
    .GetMethod(nameof(CSharpFrontendTests.UnsupportedConstruct_IsRefusedWithItsPosition))!
    .GetCustomAttributes(typeof(TestCaseAttribute), false)
    .Cast<TestCaseAttribute>()
    .Select(attribute => (string)attribute.Arguments[0]!)
    .Concat(CSharpFrontendTests.Transcendentals.SelectMany(name => new[] {
      CSharpFrontendTests.Transcendental("Math", name), CSharpFrontendTests.Transcendental("MathF", name)
    }))
    .ToArray();

  /// <summary>
  /// Test design 11 and 15: every unsupported construct is exit code 3, reported with its line and
  /// column, and nothing is written.
  /// </summary>
  [TestCaseSource(nameof(UnsupportedMethods))]
  public void UnsupportedConstruct_IsExitCode3AndWritesNothing(string method) {
    var (exitCode, output, error) = this.Translate(PrototypeFixture.Wrap(method));
    Assert.That((exitCode, output), Is.EqualTo((TranslateCommand.UnsupportedConstruct, "")));
    Assert.That(error, Does.Match(@"^.*Snippet\.cs\(1,\d+\): error PLY1002: '.+' is not supported yet\r?\n$"));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

  [Test]
  public void UnsupportedMethods_AreTheOnesOfTheFrontendTests()
    => Assert.That(UnsupportedMethods(), Has.Length.GreaterThan(120).And.Contain("public static decimal F(decimal a) => a + 1;")
      .And.Contain("public static double F(double x) => System.Math.Sin(x);").And.Contain("public static float F(float x) => System.MathF.Pow(x, x);"));

  /// <summary>Test design 12: invalid C# is exit code 4 and carries the compiler's message.</summary>
  [TestCase("public static int F( => 1;", "CS1001")]
  [TestCase("public static int F() => true;", "CS0029")]
  public void InvalidSource_IsExitCode4(string method, string id) {
    var (exitCode, output, error) = this.Translate(PrototypeFixture.Wrap(method));
    Assert.That((exitCode, output), Is.EqualTo((TranslateCommand.InvalidSource, "")));
    Assert.That(error, Does.Match(@"Snippet\.cs\(1,\d+\): error PLY1000: " + id + ": "));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

  /// <summary>A constant that overflows is the compiler's error too: exit code 4 under PLY1003.</summary>
  [Test]
  public void ConstantOverflow_IsExitCode4() {
    var (exitCode, output, error) = this.Translate(PrototypeFixture.Wrap("public static int F() => int.MaxValue + 1;"));
    Assert.That((exitCode, output), Is.EqualTo((TranslateCommand.InvalidSource, "")));
    Assert.That(error, Does.Match(@"Snippet\.cs\(1,\d+\): error PLY1003: CS0220: "));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

  /// <summary>Test design 13: a command line that cannot be carried out is PLY0001, exit code 2.</summary>
  [TestCase(new string[0], "no source file was given", TestName = "Misuse: nothing at all")]
  [TestCase(new[] { "--from", "csharp", "--to", "javascript", "--output", "out" }, "no source file was given", TestName = "Misuse: missing file")]
  [TestCase(new[] { "A.cs", "--from", "csharp", "--to", "javascript" }, "the option '--output' is missing", TestName = "Misuse: missing --output")]
  [TestCase(new[] { "A.cs", "--to", "javascript", "--output", "out" }, "the option '--from' is missing", TestName = "Misuse: missing --from")]
  [TestCase(new[] { "A.cs", "--from", "csharp", "--output", "out" }, "the option '--to' is missing", TestName = "Misuse: missing --to")]
  [TestCase(new[] { "A.cs", "--from", "kotlin", "--to", "javascript", "--output", "out" }, "unknown source language 'kotlin'", TestName = "Misuse: unknown --from")]
  [TestCase(new[] { "A.cs", "--from", "csharp", "--to", "cobol", "--output", "out" }, "unknown target language 'cobol'", TestName = "Misuse: unknown --to")]
  [TestCase(new[] { "A.cs", "--from", "CSharp", "--to", "javascript", "--output", "out" }, "unknown source language 'CSharp'", TestName = "Misuse: language names are case-sensitive")]
  [TestCase(new[] { "A.cs", "--from", "csharp", "--to", "javascript", "--output" }, "the option '--output' needs a value", TestName = "Misuse: option without value")]
  [TestCase(new[] { "A.cs", "--from", "csharp", "--to", "javascript", "--output", "out", "--verify" }, "unknown option '--verify'", TestName = "Misuse: unknown option")]
  [TestCase(new[] { "A.cs", "B.cs", "--from", "csharp", "--to", "javascript", "--output", "out" }, "more than one source file: 'A.cs' and 'B.cs'", TestName = "Misuse: two files")]
  [TestCase(new[] { "does-not-exist.cs", "--from", "csharp", "--to", "javascript", "--output", "out" }, "the source file 'does-not-exist.cs' cannot be read", TestName = "Misuse: unreadable file")]
  public void Misuse_IsAUsageError(string[] arguments, string message) {
    var (exitCode, output, error) = Invoke(arguments.Prepend("translate").ToArray());
    Assert.That((exitCode, output), Is.EqualTo((Program.UsageError, "")));
    Assert.That(error, Does.StartWith("error PLY0001: " + message).And.Contain(Usage));
  }

  /// <summary>A directory in place of the source file is as unreadable as a missing one.</summary>
  [Test]
  public void SourceThatIsADirectory_IsAUsageError() {
    var (exitCode, _, error) = Invoke("translate", this._directory, "--from", "csharp", "--to", "javascript", "--output", this._out);
    Assert.That(exitCode, Is.EqualTo(Program.UsageError));
    Assert.That(error, Does.StartWith("error PLY0001: the source file '").And.Contain("cannot be read"));
  }

  /// <summary>An output directory that cannot exist, because a file has its name.</summary>
  [Test]
  public void OutputThatIsAFile_IsAUsageError() {
    var blocked = this.Source(PrototypeFixture.Wrap("public static int F() => 1;"));
    var (exitCode, output, error) = Invoke("translate", blocked, "--from", "csharp", "--to", "javascript", "--output", blocked);
    Assert.That((exitCode, output), Is.EqualTo((Program.UsageError, "")));
    Assert.That(error, Does.StartWith("error PLY0001: the output directory '").And.Contain("cannot be written"));
  }

  /// <summary>Test design 14: IR the verifier rejects is an internal error, exit code 70, and writes nothing.</summary>
  [Test]
  public void IllTypedIr_IsAnInternalError() {
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [], [], new IrBlock([
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrConst(IrType.I32, 1), new IrConst(IrType.Bool, 1)))
    ]))]);
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(TranslateCommand.Emit(module, new Dictionary<string, string> { ["--to"] = "javascript", ["--output"] = this._out }, output, error), Is.EqualTo(TranslateCommand.InternalError));
    Assert.That((TranslateCommand.InternalError, output.ToString()), Is.EqualTo((70, "")));
    Assert.That(error.ToString(), Does.StartWith("error PLY0100: ill-typed IR in 'F': Add is applied to i32 and bool"));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

  [Test]
  public void Help_NamesTheTranslateCommand()
    => Assert.That(Invoke("--help").Output, Does.Contain(Usage["usage: ".Length..]).And.Contain("prototype"));

  [Test]
  public void Diagnostic_FormatsItsPosition() {
    var diagnostic = new Diagnostic("PLY1002", DiagnosticSeverity.Error, "message", "A.cs", 3, 7);
    Assert.That(diagnostic.ToString(), Is.EqualTo("A.cs(3,7): error PLY1002: message"));
    Assert.That((diagnostic with { Line = 0, Column = 0 }).ToString(), Is.EqualTo("A.cs: error PLY1002: message"));
    Assert.That(new Diagnostic("PLY0001", DiagnosticSeverity.Warning, "message").ToString(), Is.EqualTo("warning PLY0001: message"));
    Assert.That((diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.File, diagnostic.Line, diagnostic.Column),
      Is.EqualTo(("PLY1002", DiagnosticSeverity.Error, "message", "A.cs", 3, 7)));
  }

}
