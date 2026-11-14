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
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
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
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
    Assert.That(File.Exists(Path.Combine(this._out, "Snippet.mjs")), Is.True);
  }

  /// <summary>Every snippet the frontend tests refuse, so that the command is held to the same list.</summary>
  private static string[] UnsupportedMethods() => typeof(CSharpFrontendTests)
    .GetMethod(nameof(CSharpFrontendTests.UnsupportedConstruct_IsRefusedWithItsPosition))!
    .GetCustomAttributes(typeof(TestCaseAttribute), false)
    .Cast<TestCaseAttribute>()
    .Select(attribute => (string)attribute.Arguments[0]!)
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
    => Assert.That(UnsupportedMethods(), Has.Length.GreaterThan(80).And.Contain("public static decimal F(decimal a) => a + 1;")
      .And.Contain("public static double F(double a) => System.Math.ReciprocalSqrtEstimate(a);"));

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

  /// <summary>Test design 13: a command line that cannot be carried out is PLY0003, exit code 2, the diagnostic alone.</summary>
  [TestCase(new string[0], "missing argument <file>", TestName = "Misuse: nothing at all")]
  [TestCase(new[] { "--from", "csharp", "--to", "javascript", "--output", "out" }, "missing argument <file>", TestName = "Misuse: missing file")]
  [TestCase(new[] { "A.cs", "--from", "csharp", "--to", "javascript" }, "option '--output' is required", TestName = "Misuse: missing --output")]
  [TestCase(new[] { "A.cs", "--to", "javascript", "--output", "out" }, "option '--from' is required", TestName = "Misuse: missing --from")]
  [TestCase(new[] { "A.cs", "--from", "csharp", "--output", "out" }, "option '--to' is required", TestName = "Misuse: missing --to")]
  [TestCase(new[] { "A.cs", "--from", "kotlin", "--to", "javascript", "--output", "out" }, "unknown source language 'kotlin'", TestName = "Misuse: unknown --from")]
  [TestCase(new[] { "A.cs", "--from", "csharp", "--to", "cobol", "--output", "out" }, "unknown target language 'cobol'", TestName = "Misuse: unknown --to")]
  [TestCase(new[] { "A.cs", "--from", "CSharp", "--to", "javascript", "--output", "out" }, "unknown source language 'CSharp'", TestName = "Misuse: language names are case-sensitive")]
  [TestCase(new[] { "A.cs", "--from", "csharp", "--to", "javascript", "--output" }, "option '--output' requires a value", TestName = "Misuse: option without value")]
  [TestCase(new[] { "A.cs", "--from", "csharp", "--to", "javascript", "--output", "out", "--verify" }, "unknown option '--verify'", TestName = "Misuse: unknown option")]
  [TestCase(new[] { "A.cs", "B.cs", "--from", "csharp", "--to", "javascript", "--output", "out" }, "unexpected argument 'B.cs'", TestName = "Misuse: two files")]
  public void Misuse_IsAUsageError(string[] arguments, string message) {
    var (exitCode, output, error) = Invoke(arguments.Prepend("translate").ToArray());
    Assert.That((exitCode, output), Is.EqualTo(((int)ExitCode.UsageError, "")));
    Assert.That(error, Does.StartWith("polypiler: error PLY0003: " + message));
    Assert.That(error.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), Has.Length.EqualTo(1));
  }

  /// <summary>A source file that is missing or a directory is PLY0004, exit code 1, and nothing is written.</summary>
  [Test]
  public void SourceThatCannotBeRead_IsPly0004() {
    foreach (var source in new[] { Path.Combine(this._directory, "does-not-exist.cs"), this._directory }) {
      var (exitCode, output, error) = Invoke("translate", source, "--from", "csharp", "--to", "javascript", "--output", this._out);
      Assert.That((exitCode, output), Is.EqualTo(((int)ExitCode.EnvironmentError, "")));
      Assert.That(error, Is.EqualTo($"polypiler: error PLY0004: input '{source}' does not exist or is not readable{Environment.NewLine}"));
    }

    Assert.That(Directory.Exists(this._out), Is.False);
  }

  /// <summary>An output directory that cannot exist, because a file has its name: PLY0005, exit code 1.</summary>
  [Test]
  public void OutputThatIsAFile_IsPly0005() {
    var blocked = this.Source(PrototypeFixture.Wrap("public static int F() => 1;"));
    var (exitCode, output, error) = Invoke("translate", blocked, "--from", "csharp", "--to", "javascript", "--output", blocked);
    Assert.That((exitCode, output), Is.EqualTo(((int)ExitCode.EnvironmentError, "")));
    Assert.That(error, Does.StartWith($"polypiler: error PLY0005: output path '{blocked}' cannot be created: "));
  }

  /// <summary>With <c>--diagnostics json</c> the diagnostics of a translation are JSON objects that carry the invocation.</summary>
  [Test]
  public void JsonDiagnostics_CarryTheInvocation() {
    var source = this.Source(PrototypeFixture.Wrap("public static int F() => int.MaxValue + 1;"));
    var (exitCode, output, error) = Invoke("--diagnostics", "json", "translate", source, "--from", "csharp", "--to", "javascript", "--output", this._out);
    Assert.That((exitCode, output), Is.EqualTo((TranslateCommand.InvalidSource, "")));
    Assert.That(error, Does.StartWith("{\"code\":\"PLY1003\"").And.Contain($"\"command\":\"polypiler --diagnostics json translate {source} --from"));
  }

  /// <summary>The notes and the explanations of the optimizer are no diagnostics: text only, and not with <c>--quiet</c>.</summary>
  [TestCase("--quiet")]
  [TestCase("--diagnostics=json")]
  public void NotesAreLeftOff_QuietAndJsonStandardError(string global) {
    var script = Path.Combine(this._directory, "rotate.js");
    File.WriteAllText(script, "/** @param {int32} x */ export function f(x) { return (x << 8) | (x >>> 24); }");
    var (exitCode, output, error) = Invoke("translate", script, "--from", "javascript", "--to", "csharp", "--output", this._out, "--explain-opt", global);
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
    Assert.That(output, Does.Contain("rotate.cs"));
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
    Assert.That(error.ToString(), Does.StartWith("polypiler: error PLY0100: ill-typed IR in 'F': Add is applied to i32 and bool"));
    Assert.That(Directory.Exists(this._out), Is.False);
  }

  [Test]
  public void Help_NamesTheTranslateCommand()
    => Assert.That(Invoke("--help").Output, Does.Contain("  translate  ").And.Contain("prototype"));

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
