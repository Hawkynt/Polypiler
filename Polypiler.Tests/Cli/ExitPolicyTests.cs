using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Cli.Support;
using Polypiler.Tooling.Diagnostics;

namespace Polypiler.Tests.Cli;

/// <summary>
/// The stable exit codes of PRD §10.1 as ADR 0003 numbers them (issue #23): every way out of a
/// command goes through <see cref="ExitPolicy"/>, with the test-only command <c>raise</c>, in process.
/// The numbers in the comments are the rows of the issue's test design.
/// </summary>
[TestFixture]
public sealed partial class ExitPolicyTests {

  private sealed record Run(int ExitCode, string Output, string Error) {

    public string[] ErrorLines => this.Error.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

  }

  private static Run Invoke(params string[] args) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(args, output, error, RaiseCommand.Registry(), PrototypeFixture.RepositoryRoot);
    return new Run(exitCode, output.ToString(), error.ToString());
  }

  private static CliContext Context(StringWriter error, DiagnosticsFormat format = DiagnosticsFormat.Text, bool quiet = false)
    => new(TextWriter.Null, error, "polypiler raise", format, quiet, ".");

  /// <summary>An exception with a stack trace, as one a command throws has.</summary>
  private static Exception Thrown() {
    try {
      throw new InvalidOperationException("boom");
    } catch (InvalidOperationException exception) {
      return exception;
    }
  }

  /// <summary>Every error code of the registry, with the exit code it registers.</summary>
  private static IEnumerable<TestCaseData> RegisteredErrors()
    => RaiseCommand.RepositoryRegistry.Codes
      .Where(entry => entry.Severity == DiagnosticSeverity.Error)
      .Select(entry => new TestCaseData(entry.Code, (int)entry.ExitCode!.Value).SetName($"Registered error {entry.Code} exits with {(int)entry.ExitCode!.Value}"));

  /// <summary>Test design 1: no diagnostic, no tally line.</summary>
  [Test]
  public void NoDiagnostics_IsSuccess_WithoutATallyLine()
    => Assert.That(Invoke("raise"), Is.EqualTo(new Run(0, "", "")));

  /// <summary>Test design 2: one code per category of PRD §10.1 and the internal error, each with its own number.</summary>
  [TestCase("PLY0004", 1)]
  [TestCase("PLY0003", 2)]
  [TestCase("PLY0014", 3)]
  [TestCase("PLY0013", 4)]
  [TestCase("PLY0051", 5)]
  [TestCase("PLY0030", 6)]
  [TestCase("PLY0050", 7)]
  [TestCase("PLY0024", 8)]
  [TestCase("PLY0081", 9)]
  [TestCase("PLY0031", 10)]
  [TestCase("PLY0001", 70)]
  public void EachCategory_HasItsOwnExitCode(string code, int expected) {
    var run = Invoke("raise", code);
    Assert.That(run.ExitCode, Is.EqualTo(expected), run.Error);
    Assert.That(run.ErrorLines, Has.Length.EqualTo(2));
    Assert.That(run.ErrorLines[0], Does.StartWith($"polypiler: error {code}: "));
    Assert.That(run.ErrorLines[1], Is.EqualTo("polypiler: 1 error(s), 0 warning(s)"));
  }

  /// <summary>Every error code of <c>spec/diagnostics.yaml</c> exits with the number the registry gives it.</summary>
  [TestCaseSource(nameof(RegisteredErrors))]
  public void EveryRegisteredError_ExitsWithItsRegisteredCode(string code, int expected)
    => Assert.That(Invoke("raise", code).ExitCode, Is.EqualTo(expected));

  /// <summary>Every error of the registry carries an exit code, and no category of PRD §10.1 is without one.</summary>
  [Test]
  public void TheRegistry_HasACodeForEveryExitCode() {
    var registry = RaiseCommand.RepositoryRegistry;
    Assert.That(registry.Codes.Where(entry => entry.Severity == DiagnosticSeverity.Error).Select(entry => entry.ExitCode), Has.None.Null);
    var used = registry.Codes.Where(entry => entry is { Retired: false }).Select(entry => entry.ExitCode).OfType<ExitCode>().ToHashSet();
    Assert.That(Enum.GetValues<ExitCode>().Where(code => code != ExitCode.Success), Is.SubsetOf(used));
  }

  /// <summary>Test design 3: a warning or an info alone is success; the tally counts the warning.</summary>
  [TestCase("PLY0009", "polypiler: 0 error(s), 1 warning(s)")]
  [TestCase("PLY0098", "polypiler: 0 error(s), 0 warning(s)")]
  public void AWarningOrAnInfoAlone_IsSuccess(string code, string tally) {
    var run = Invoke("raise", code);
    Assert.That(run.ExitCode, Is.Zero);
    Assert.That(run.ErrorLines[^1], Is.EqualTo(tally));
  }

  /// <summary>Test design 4: zero, one and many errors; the first one wins, not the highest number.</summary>
  [TestCase(new string[0], 0)]
  [TestCase(new[] { "PLY0030" }, 6)]
  [TestCase(new[] { "PLY0031", "PLY0030", "PLY0004" }, 10)]
  [TestCase(new[] { "PLY0009", "PLY0030", "PLY0051" }, 6)]
  public void TheFirstError_Wins(string[] codes, int expected)
    => Assert.That(Invoke(["raise", .. codes]).ExitCode, Is.EqualTo(expected));

  /// <summary>Test design 5: <c>PLY0001</c> after other errors still makes the exit code 70.</summary>
  [Test]
  public void AnInternalErrorAfterOtherErrors_Is70() {
    var run = Invoke("raise", "PLY0030", "PLY0001");
    Assert.That(run.ExitCode, Is.EqualTo(70));
    Assert.That(run.ErrorLines[^1], Is.EqualTo("polypiler: 2 error(s), 0 warning(s)"));
  }

  /// <summary>Test design 6 and 15: the tally line is the last line of standard error in text format.</summary>
  [Test]
  public void TheTallyLine_IsLastInTextFormat() {
    var run = Invoke("raise", "PLY0009", "PLY0030", "PLY0051");
    Assert.That(run.ExitCode, Is.EqualTo(6));
    Assert.That(run.ErrorLines, Has.Length.EqualTo(4));
    Assert.That(run.ErrorLines[^1], Is.EqualTo("polypiler: 2 error(s), 1 warning(s)"));
    Assert.That(Invoke("raise", "PLY0004").ErrorLines[^1], Is.EqualTo("polypiler: 1 error(s), 0 warning(s)"));
  }

  /// <summary>Test design 15: with JSON diagnostics every line is one object and no tally line follows; the exit code is the same.</summary>
  [Test]
  public void JsonDiagnostics_HaveNoTallyLine() {
    var run = Invoke("--diagnostics", "json", "raise", "PLY0009", "PLY0030");
    Assert.That(run.ExitCode, Is.EqualTo(6));
    Assert.That(run.ErrorLines, Has.Length.EqualTo(2));
    foreach (var line in run.ErrorLines) {
      using var document = JsonDocument.Parse(line);
      Assert.That(document.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object));
    }

    Assert.That(run.Error, Does.Not.Contain("error(s)"));
  }

  /// <summary>Test design 13: <c>--quiet</c> leaves only the error, no tally line, and the same exit code.</summary>
  [Test]
  public void Quiet_LeavesOnlyTheError() {
    var run = Invoke("--quiet", "raise", "PLY0009", "PLY0030");
    Assert.That(run.ExitCode, Is.EqualTo(6));
    Assert.That(run.ErrorLines, Is.EqualTo(new[] { "polypiler: error PLY0030: target build failed: '<toolchain> <arguments>' exited with <code>" }));
  }

  /// <summary>Test design 16: a command line the grammar refuses has its one diagnostic and no tally line.</summary>
  [TestCase(new[] { "frobnicate" }, "polypiler: error PLY0002: unknown command 'frobnicate'; run 'polypiler --help'")]
  [TestCase(new[] { "raise", "--nope" }, "polypiler: error PLY0003: unknown option '--nope'")]
  [TestCase(new[] { "echo", "input.txt", "--left", "--right" }, "polypiler: error PLY0011: options '--left' and '--right' cannot be combined")]
  public void ARefusedCommandLine_HasNoTallyLine(string[] args, string line)
    => Assert.That(Invoke(args), Is.EqualTo(new Run(2, "", line + Environment.NewLine)));

  /// <summary>Test design 17: the same code raised by a command that ran is followed by the tally line.</summary>
  [Test]
  public void AUsageErrorOfACommandThatRan_HasATallyLine() {
    var run = Invoke("raise", "PLY0003");
    Assert.That(run.ExitCode, Is.EqualTo(2));
    Assert.That(run.ErrorLines, Is.EqualTo(new[] { "polypiler: error PLY0003: <reason>", "polypiler: 1 error(s), 0 warning(s)" }));
  }

  /// <summary>Test design 7: an exception escaping a command is <c>PLY0001</c>, exit 70, with nothing on standard output and no stack trace by default.</summary>
  [Test]
  public void AnEscapingException_IsAnInternalError() {
    var run = Invoke("raise", "--throw", "InvalidOperationException:boom");
    Assert.That(run.ExitCode, Is.EqualTo(70));
    Assert.That(run.Output, Is.Empty);
    Assert.That(run.ErrorLines, Is.EqualTo(new[] {
      "polypiler: error PLY0001: internal compiler error: InvalidOperationException: boom", "polypiler: 1 error(s), 0 warning(s)"
    }));
  }

  /// <summary>Test design 7: an exception after errors were reported still makes the exit code 70, and the tally counts both.</summary>
  [Test]
  public void AnExceptionAfterAnError_Is70() {
    var run = Invoke("raise", "PLY0030", "--throw", "InvalidOperationException");
    Assert.That(run.ExitCode, Is.EqualTo(70));
    Assert.That(run.ErrorLines[^1], Is.EqualTo("polypiler: 2 error(s), 0 warning(s)"));
  }

  /// <summary>Test design 8: with JSON diagnostics the exception is exactly one object whose cause is the stack trace.</summary>
  [Test]
  public void AnEscapingException_InJson_IsOneObjectWithTheStackTrace() {
    var run = Invoke("--diagnostics", "json", "raise", "--throw", "InvalidOperationException:boom");
    Assert.That(run.ExitCode, Is.EqualTo(70));
    Assert.That(run.ErrorLines, Has.Length.EqualTo(1));
    using var document = JsonDocument.Parse(run.ErrorLines[0]);
    var root = document.RootElement;
    Assert.That(root.GetProperty("code").GetString(), Is.EqualTo("PLY0001"));
    Assert.That(root.GetProperty("message").GetString(), Is.EqualTo("internal compiler error: InvalidOperationException: boom"));
    Assert.That(root.GetProperty("cause").GetString(), Does.Contain(nameof(RaiseCommand)));
    Assert.That(root.GetProperty("workaround").GetString(), Is.EqualTo("report this with the command line shown"));
    Assert.That(root.GetProperty("command").GetString(), Is.EqualTo("polypiler --diagnostics json raise --throw InvalidOperationException:boom"));
  }

  /// <summary>Test design 9: with <c>POLYPILER_DEBUG=1</c> the stack trace follows the diagnostic in text format, before the tally line.</summary>
  [Test]
  public void Debug_AddsTheStackTraceInText() {
    using var error = new StringWriter();
    Assert.That(ExitPolicy.Fail(Context(error), Thrown(), debug: true), Is.EqualTo(70));
    var lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    Assert.That(lines[0], Is.EqualTo("polypiler: error PLY0001: internal compiler error: InvalidOperationException: boom"));
    Assert.That(lines[1..^1], Is.Not.Empty.And.All.Contain(nameof(Thrown)));
    Assert.That(lines[^1], Is.EqualTo("polypiler: 1 error(s), 0 warning(s)"));
  }

  /// <summary>Test design 8 and 9: in JSON the stack trace is never written as lines of its own, debug or not.</summary>
  [Test]
  public void Debug_DoesNotAddLinesInJson() {
    using var error = new StringWriter();
    Assert.That(ExitPolicy.Fail(Context(error, DiagnosticsFormat.Json), Thrown(), debug: true), Is.EqualTo(70));
    Assert.That(error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), Has.Length.EqualTo(1));
  }

  /// <summary>An exception that was never thrown has no stack trace: no cause, and nothing to add in debug.</summary>
  [Test]
  public void AnExceptionWithoutAStackTrace_HasNoCause() {
    using var error = new StringWriter();
    var context = Context(error);
    Assert.That(ExitPolicy.Fail(context, new InvalidOperationException("never thrown"), debug: true), Is.EqualTo(70));
    Assert.That(context.Diagnostics.Items.Single().Cause, Is.Null);
    Assert.That(error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), Has.Length.EqualTo(2));
  }

  /// <summary>Test design 10: a command that returns non-zero without reporting an error is not trusted.</summary>
  [TestCase(new[] { "raise", "--return", "3" }, "3")]
  [TestCase(new[] { "raise", "PLY0009", "--return=-1" }, "-1")]
  public void AFailureWithoutADiagnostic_IsAnInternalError(string[] args, string returned) {
    var run = Invoke(args);
    Assert.That(run.ExitCode, Is.EqualTo(70));
    Assert.That(run.Error, Does.Contain($"polypiler: error PLY0001: command 'raise' returned {returned} without a diagnostic"));
  }

  /// <summary>A command's own number does not override the exit code of its first error (ADR 0003 decision 6).</summary>
  [Test]
  public void ACommandsOwnNumber_DoesNotOverrideItsErrors()
    => Assert.That(Invoke("raise", "PLY0030", "--return", "3").ExitCode, Is.EqualTo(6));

  /// <summary>Test design 11: a cancellation reports nothing and exits with 130.</summary>
  [TestCase("OperationCanceledException")]
  [TestCase("OperationCanceledException:stop")]
  public void ACancellation_Is130WithoutADiagnostic(string thrown)
    => Assert.That(Invoke("raise", "--throw", thrown), Is.EqualTo(new Run(130, "", "")));

  /// <summary>An exception outside any command, such as one of the grammar for the command line, is an internal error too.</summary>
  [Test]
  public void ACancellationOutsideACommand_Is130() {
    var registry = new CommandRegistry();
    registry.Add(new BrokenCommand(new OperationCanceledException()));
    using var error = new StringWriter();
    Assert.That(Program.Run(["broken"], TextWriter.Null, error, registry, "."), Is.EqualTo(130));
    Assert.That(error.ToString(), Is.Empty);
  }

  /// <summary>Test design 2 and the table: every member of <see cref="ExitCode"/> is a row, once and in ascending order, then 130.</summary>
  [Test]
  public void TheTable_HasEveryExitCodeOnceInOrder() {
    var rows = ExitCodeTable.Rows;
    Assert.That(rows.Select(row => row.Code), Is.EqualTo(Enum.GetValues<ExitCode>().Select(code => (int)code).Append(130)));
    Assert.That(rows.Take(rows.Count - 1).Select(row => row.Name), Is.EqualTo(Enum.GetNames<ExitCode>()));
    Assert.That(rows[^1], Is.EqualTo((130, (string?)null, "cancelled by the user")));
    Assert.That(rows.Select(row => row.Meaning), Is.Unique.And.All.Not.Empty);
  }

  /// <summary>Measurement: the nine failure categories of PRD §10.1 have distinct non-zero codes, 3 and 4 numbered as ADR 0003 does.</summary>
  [Test]
  public void ThePrdCategories_HaveDistinctNonZeroCodes() {
    ExitCode[] categories = [
      ExitCode.InvalidSource, ExitCode.UnsupportedConstruct, ExitCode.SemanticMismatch, ExitCode.TargetBuildFailure, ExitCode.OracleFailure,
      ExitCode.LicenseBlocker, ExitCode.MissingDependency, ExitCode.BudgetExceeded, ExitCode.InternalError
    ];
    Assert.That(categories.Select(code => (int)code), Is.Unique.And.All.Not.Zero);
    Assert.That(((int)ExitCode.UnsupportedConstruct, (int)ExitCode.InvalidSource), Is.EqualTo((3, 4)));
  }

  /// <summary><c>polypiler help exit-codes</c>: one line per row, the code right-aligned to three, exit 0.</summary>
  [Test]
  public void HelpExitCodes_PrintsTheTable() {
    var run = Invoke("help", "exit-codes");
    Assert.That((run.ExitCode, run.Error), Is.EqualTo((0, "")));
    var lines = run.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    Assert.That(lines, Is.EqualTo(ExitCodeTable.HelpLines()));
    Assert.That(lines[0], Is.EqualTo("  0  success; verification passed if it was requested"));
    Assert.That(lines, Does.Contain(" 70  internal compiler error").And.Contain("130  cancelled by the user"));
    Assert.That(Program.Run(["help", "exit-codes"], TextWriter.Null, TextWriter.Null), Is.Zero);
  }

  /// <summary>A help topic is no command, and takes no further word.</summary>
  [Test]
  public void ExitCodes_IsOnlyAHelpTopic() {
    Assert.That(Invoke("exit-codes"), Is.EqualTo(new Run(2, "", "polypiler: error PLY0002: unknown command 'exit-codes'; run 'polypiler --help'" + Environment.NewLine)));
    Assert.That(Invoke("help", "exit-codes", "extra").ErrorLines, Is.EqualTo(new[] { "polypiler: error PLY0003: unexpected argument 'extra'" }));
    Assert.That(Invoke("help", "raise").Output, Does.StartWith("polypiler "), "the help of a command is still there");
    Assert.That(Program.CreateHelpTopics().Commands.Select(topic => (topic.Name, topic.Description)),
      Is.EqualTo(new[] { ("exit-codes", "the exit codes of polypiler and what each means") }));
  }

  /// <summary>Test design 12: the committed <c>docs/cli/exit-codes.md</c> is the table as generated.</summary>
  [Test]
  public void TheCommittedDocument_IsCurrent()
    => Assert.That(Staleness(Path.Combine(PrototypeFixture.RepositoryRoot, ExitCodeTable.DocumentPath)), Is.Null);

  /// <summary>Test design 12: an edited or missing copy is named as out of date.</summary>
  [Test]
  public void AStaleDocument_IsNamed() {
    var directory = Path.Combine(Path.GetTempPath(), "polypiler-exit-codes-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var copy = Path.Combine(directory, "exit-codes.md");
      File.WriteAllText(copy, ExitCodeTable.Markdown().Replace("internal compiler error", "crash", StringComparison.Ordinal));
      Assert.That(Staleness(copy), Is.EqualTo($"PLY0091: generated document out of date: '{copy}' differs"));
      File.Delete(copy);
      Assert.That(Staleness(copy), Is.EqualTo($"PLY0091: generated document out of date: '{copy}' is missing"));
    } finally {
      Directory.Delete(directory, true);
    }
  }

  /// <summary>The document is a heading, the rule and one table row per row of the table.</summary>
  [Test]
  public void TheDocument_HasOneRowPerExitCode() {
    var markdown = ExitCodeTable.Markdown();
    Assert.That(markdown, Does.StartWith("# Exit codes\n").And.EndWith("| 130 | — | cancelled by the user |\n").And.Not.Contain("\r"));
    Assert.That(markdown, Does.Contain("| 3 | `UnsupportedConstruct` | a construct is outside the supported subset |"));
    Assert.That(markdown, Does.Contain("| 4 | `InvalidSource` | the source is not valid in its language |"));
    Assert.That(TableRow().Matches(markdown), Has.Count.EqualTo(ExitCodeTable.Rows.Count));
  }

  [GeneratedRegex(@"^\| \d+ \| ", RegexOptions.Multiline)]
  private static partial Regex TableRow();

  /// <summary>What the regeneration check says about a copy of the document: <see langword="null"/> when it is current.</summary>
  private static string? Staleness(string path) {
    if (!File.Exists(path))
      return $"PLY0091: generated document out of date: '{path}' is missing";

    return File.ReadAllText(path).ReplaceLineEndings("\n") == ExitCodeTable.Markdown() ? null : $"PLY0091: generated document out of date: '{path}' differs";
  }

  [Test]
  public void TheTallyLine_CountsErrorsAndWarnings() {
    var diagnostics = new DiagnosticBag();
    Assert.That(ExitPolicy.Tally(diagnostics), Is.EqualTo("polypiler: 0 error(s), 0 warning(s)"));
    diagnostics.Report(Diagnostic.Create(TestCommands.TestWarning, null));
    diagnostics.Report(Diagnostic.Create(TestCommands.TestError, null));
    diagnostics.Report(Diagnostic.Create(TestCommands.TestError, null));
    Assert.That(ExitPolicy.Tally(diagnostics), Is.EqualTo("polypiler: 2 error(s), 1 warning(s)"));
  }

  [Test]
  public void ThePolicy_RefusesNull() {
    using var error = new StringWriter();
    Assert.That(() => ExitPolicy.Complete(null!, "raise", 0), Throws.ArgumentNullException);
    Assert.That(() => ExitPolicy.Complete(Context(error), null!, 0), Throws.ArgumentNullException);
    Assert.That(() => ExitPolicy.Fail(null!, Thrown()), Throws.ArgumentNullException);
    Assert.That(() => ExitPolicy.Fail(Context(error), null!), Throws.ArgumentNullException);
    Assert.That(() => ExitPolicy.Tally(null!), Throws.ArgumentNullException);
  }

  /// <summary>Without <c>POLYPILER_DEBUG</c>, as the tests run, a text standard error has no stack trace.</summary>
  [Test]
  public void Fail_ReadsTheDebugVariable() {
    Assume.That(Environment.GetEnvironmentVariable(ExitPolicy.DebugVariable), Is.Not.EqualTo("1"));
    using var error = new StringWriter();
    Assert.That(ExitPolicy.Fail(Context(error), Thrown()), Is.EqualTo(70));
    Assert.That(error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), Has.Length.EqualTo(2));
  }

  /// <summary><c>Complete</c> with a quiet context writes no tally line.</summary>
  [Test]
  public void Complete_WritesNoTallyLineWhenQuiet() {
    using var error = new StringWriter();
    var context = Context(error, quiet: true);
    context.Report(Diagnostic.Create(TestCommands.TestError, null));
    Assert.That(ExitPolicy.Complete(context, "raise", 0), Is.EqualTo(1));
    Assert.That(error.ToString(), Is.EqualTo("polypiler: error PLY0098: test error" + Environment.NewLine));
  }

  /// <summary><c>PLY0081</c>, declared here for the adapters that raise it first, is a missing dependency, exit 9.</summary>
  [Test]
  public void MissingDependency_IsExit9() {
    var diagnostic = Diagnostic.Create(DiagnosticDescriptor.MissingDependency, null, "libgmp", "bigint adapter");
    Assert.That((diagnostic.Code, diagnostic.ExitCode, diagnostic.Message),
      Is.EqualTo(("PLY0081", (ExitCode?)ExitCode.MissingDependency, "native dependency 'libgmp' required by 'bigint adapter' is missing")));
  }

  /// <summary>A diagnostic an emitter words itself carries the exit code and rule of its descriptor.</summary>
  [Test]
  public void At_CarriesTheDescriptor() {
    var diagnostic = CliDiagnostics.Unreadable.At("message", "a.cs", 2, 3);
    Assert.That((diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.File, diagnostic.Line, diagnostic.Column, diagnostic.ExitCode, diagnostic.Rule),
      Is.EqualTo(("PLY0004", DiagnosticSeverity.Error, "message", "a.cs", 2, 3, (ExitCode?)ExitCode.EnvironmentError, "io.read")));
    Assert.That(TestCommands.TestWarning.At("w").ExitCode, Is.Null);
    Assert.That(() => CliDiagnostics.Unreadable.At(null!), Throws.ArgumentNullException);
  }

  /// <summary>
  /// No emitting site builds an error without its descriptor: an error without an exit code would make
  /// any run that reports it an internal error. Every product source is searched for an error built
  /// from a bare code.
  /// </summary>
  [Test]
  public void NoProductCode_BuildsAnErrorWithoutItsExitCode() {
    var offenders = Directory.EnumerateDirectories(PrototypeFixture.RepositoryRoot, "Polypiler*")
      .Where(directory => !Path.GetFileName(directory).StartsWith("Polypiler.Tests", StringComparison.Ordinal))
      .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
      .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
      .SelectMany(file => BareError().Matches(File.ReadAllText(file)).Select(match => $"{Path.GetRelativePath(PrototypeFixture.RepositoryRoot, file)}: {match.Value}"))
      .ToList();
    Assert.That(offenders, Is.Empty);
    Assert.That(BareError().IsMatch("new Diagnostic(Code, DiagnosticSeverity.Error, message)"), Is.True);
    Assert.That(BareError().IsMatch("=> new(Code, DiagnosticSeverity.Error, $\"text\")"), Is.True);
    Assert.That(BareError().IsMatch("new(Code, DiagnosticSeverity.Error, ExitCode.InternalError, \"{0}\", \"rule\")"), Is.False);
  }

  /// <summary>An error built from a code and a severity without an exit code following: a diagnostic, not a descriptor.</summary>
  [GeneratedRegex(@"new(?:\s+Diagnostic)?\(\s*[^,()]+,\s*DiagnosticSeverity\.Error\s*,(?!\s*(?:Core\.)?ExitCode\.)")]
  private static partial Regex BareError();

  /// <summary>A command whose options cannot be declared: the exception is thrown before it runs.</summary>
  private sealed class BrokenCommand(Exception exception) : ICliCommand {

    public string Name => "broken";

    public string Description => "broken";

    public void Configure(System.CommandLine.Command command) => throw exception;

    public int Execute(System.CommandLine.ParseResult parse, CliContext context) => 0;

  }

}
