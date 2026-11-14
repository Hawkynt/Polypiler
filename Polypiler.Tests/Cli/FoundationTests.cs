using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Cli.Support;

namespace Polypiler.Tests.Cli;

/// <summary>
/// The command-line foundation of ADR 0004 with the test-only commands <c>echo</c> and <c>demand</c>,
/// in process, relative paths resolved against the repository root (issue #25, test design 1-51).
/// </summary>
[TestFixture]
public sealed class FoundationTests {

  private const string Input = "tests/fixtures/cli/foundation/input.txt";
  private const string NonEmpty = "tests/fixtures/cli/foundation/nonempty";

  /// <summary>The tally line after a command that reported one error and nothing else (issue #23).</summary>
  private static readonly string OneError = "polypiler: 1 error(s), 0 warning(s)" + Environment.NewLine;

  private sealed record Run(int ExitCode, string Output, string Error, EchoCommand Echo) {

    public string[] ErrorLines => this.Error.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

  }

  private static Run Invoke(params string[] args) => InvokeWith([], args);

  private static Run InvokeWith(DiagnosticDescriptor[] reports, params string[] args) {
    var (registry, echo, _) = TestCommands.Registry(reports);
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(args, output, error, registry, PrototypeFixture.RepositoryRoot);
    return new Run(exitCode, output.ToString(), error.ToString(), echo);
  }

  private static void AssertRefused(Run run, string line) {
    Assert.That(run.ExitCode, Is.EqualTo((int)ExitCode.UsageError), run.Error);
    Assert.That(run.Output, Is.Empty);
    Assert.That(run.Error, Is.EqualTo(line + Environment.NewLine));
    Assert.That(run.Echo.Executions, Is.Zero);
  }

  private static string TemporaryDirectory() => Path.Combine(Path.GetTempPath(), "polypiler-foundation-" + Guid.NewGuid().ToString("N"));

  /// <summary>Test design 1.</summary>
  [Test]
  public void ARegisteredCommand_Runs() {
    var run = Invoke("echo", Input);
    Assert.That((run.ExitCode, run.Output, run.Error), Is.EqualTo((0, "echo " + Input + Environment.NewLine, "")));
    Assert.That(run.Echo.Executions, Is.EqualTo(1));
  }

  [Test]
  public void ADemandingCommand_RunsWithItsRequiredOption()
    => Assert.That(Invoke("demand", "--key", "k").Output, Is.EqualTo("demand k" + Environment.NewLine));

  /// <summary>Test design 2.</summary>
  [Test]
  public void CommandHelp_ListsTheOptionsOfTheCommand() {
    var run = Invoke("echo", "--help");
    Assert.That(run.ExitCode, Is.Zero);
    Assert.That(run.Output, Does.Contain("usage: polypiler echo <input> [options]")
      .And.Contain("  --output <path>").And.Contain("  --force ").And.Contain("  --mode <a|b>")
      .And.Contain("  --export <name>[,<name>...]").And.Contain("  -h, --help").And.Contain("  --diagnostics <text|json>"));
    Assert.That(run.Error, Is.Empty);
  }

  [Test]
  public void CommandHelp_MarksARequiredOptionAndShowsAnOptionWithoutDescription() {
    var output = Invoke("demand", "--help").Output;
    Assert.That(output, Does.Contain("  --key <value>  (required)"));
    Assert.That(output, Does.Not.Contain("arguments:"));
  }

  /// <summary>Test design 3 and 8.</summary>
  [Test]
  public void HelpSpellings_GiveOneRootHelp() {
    var expected = Invoke().Output;
    Assert.That(expected, Is.EqualTo($"polypiler {ProductInfo.Version}{Environment.NewLine}usage: polypiler <command> [options]{Environment.NewLine}"
      + $"  demand  print the key{Environment.NewLine}  echo    print the input{Environment.NewLine}"));
    foreach (var spelling in new[] { "--help", "-h", "help" }) {
      var run = Invoke(spelling);
      Assert.That((run.ExitCode, run.Output, run.Error), Is.EqualTo((0, expected, "")), spelling);
    }
  }

  /// <summary>Test design 9.</summary>
  [Test]
  public void AnEmptyRegistry_ShowsNoCommandLine() {
    using var output = new StringWriter();
    Assert.That(Program.Run([], output, TextWriter.Null, new CommandRegistry(), "."), Is.Zero);
    Assert.That(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), Has.Length.EqualTo(2));
  }

  /// <summary>Test design 4, 5 and 31.</summary>
  [TestCase("echo", Input, "--mode", "a")]
  [TestCase("echo", Input, "--mode=a")]
  [TestCase("--quiet", "echo", Input)]
  [TestCase("echo", Input, "--quiet")]
  [TestCase("--diagnostics", "json", "echo", Input)]
  [TestCase("echo", Input, "--diagnostics=json")]
  [TestCase("echo", Input, "--note=-x")]
  [TestCase("echo", Input, "--note=")]
  [TestCase("echo", Input, "--note", "")]
  [TestCase("echo", Input, "--note", "-")]
  [TestCase("echo", Input, "--note==")]
  public void AcceptedSpellings_RunTheCommand(params string[] args) {
    var run = Invoke(args);
    Assert.That((run.ExitCode, run.Output, run.Error), Is.EqualTo((0, "echo " + Input + Environment.NewLine, "")));
  }

  [TestCase("--note=-x", "-x")]
  [TestCase("--note=", "")]
  [TestCase("--note==", "=")]
  [TestCase("--mode=b", null)]
  public void AnAttachedValue_IsTheValueAfterTheFirstEqualsSign(string option, string? note)
    => Assert.That(Invoke("echo", Input, option).Echo.Last.Note, Is.EqualTo(note));

  /// <summary>Test design 6.</summary>
  [TestCase("--export", "a,b")]
  [TestCase("--export", "a", "--export", "b")]
  [TestCase("--export=a", "--export", "b")]
  public void AList_IsTheItemsOfEveryOccurrenceInOrder(params string[] options)
    => Assert.That(Invoke(["echo", Input, .. options]).Echo.Last.Export, Is.EqualTo(new[] { "a", "b" }));

  /// <summary>Test design 7.</summary>
  [Test]
  public void TheDiagnosticsFormat_SelectsTextOrJson() {
    Assert.That(Invoke("frobnicate").Error, Does.StartWith("polypiler: error PLY0002: "));
    var json = Invoke("--diagnostics", "json", "frobnicate").ErrorLines.Single();
    using var document = JsonDocument.Parse(json);
    Assert.That(document.RootElement.GetProperty("code").GetString(), Is.EqualTo("PLY0002"));
    Assert.That(document.RootElement.GetProperty("command").GetString(), Is.EqualTo("polypiler --diagnostics json frobnicate"));
  }

  /// <summary>Test design 10 and 44: after <c>--</c> every argument is an operand.</summary>
  [TestCase("--mode")]
  [TestCase("--help")]
  public void AfterTheTerminator_AnOptionIsAnOperand(string operand) {
    var run = Invoke("echo", "--", operand);
    Assert.That(run.ExitCode, Is.EqualTo((int)ExitCode.EnvironmentError));
    Assert.That(run.Error, Is.EqualTo($"polypiler: error PLY0004: input '{operand}' does not exist or is not readable{Environment.NewLine}" + OneError));
    Assert.That(run.Echo.Executions, Is.EqualTo(1));
  }

  /// <summary>Test design 11 and 12.</summary>
  [Test]
  public void TheOutputDirectory_IsCreatedOrAcceptedOrRefused() {
    var root = TemporaryDirectory();
    try {
      var absent = Path.Combine(root, "absent");
      Assert.That(Invoke("echo", Input, "--output", absent).ExitCode, Is.Zero);
      Assert.That(Directory.Exists(absent), Is.True);

      Assert.That(Invoke("echo", Input, "--output", absent).ExitCode, Is.Zero, "an empty directory is accepted");

      File.WriteAllText(Path.Combine(absent, "one.txt"), "1");
      var refused = Invoke("echo", Input, "--output", absent);
      Assert.That(refused.ExitCode, Is.EqualTo((int)ExitCode.EnvironmentError));
      Assert.That(refused.Output, Is.Empty);
      Assert.That(refused.Error, Is.EqualTo($"polypiler: error PLY0005: output directory '{absent}' is not empty; pass --force to write into it{Environment.NewLine}" + OneError));

      var forced = Invoke("echo", Input, "--output", absent, "--force");
      Assert.That((forced.ExitCode, forced.Error), Is.EqualTo((0, "")));
      Assert.That(File.ReadAllText(Path.Combine(absent, "one.txt")), Is.EqualTo("1"));
    } finally {
      if (Directory.Exists(root))
        Directory.Delete(root, true);
    }
  }

  [Test]
  public void ANonEmptyFixture_NeedsForce_AndKeepsItsFile() {
    var keep = Path.Combine(PrototypeFixture.RepositoryRoot, NonEmpty, "keep.txt");
    var before = File.ReadAllBytes(keep);
    Assert.That(Invoke("echo", Input, "--output", NonEmpty).Error, Does.Contain("error PLY0005: output directory 'tests/fixtures/cli/foundation/nonempty' is not empty; pass --force to write into it"));
    Assert.That(Invoke("echo", Input, "--output", NonEmpty, "--force").ExitCode, Is.Zero);
    Assert.That(File.ReadAllBytes(keep), Is.EqualTo(before));
  }

  /// <summary>Test design 20: an output path that is a file, one below a file, and the empty one.</summary>
  [TestCase(Input, "a file of that name exists")]
  [TestCase(Input + "/below", null)]
  [TestCase("", "the path is empty")]
  public void AnOutputPathThatCannotBeADirectory_IsRefused(string path, string? reason) {
    var run = Invoke("echo", Input, "--output=" + path);
    Assert.That(run.ExitCode, Is.EqualTo((int)ExitCode.EnvironmentError));
    Assert.That(run.Output, Is.Empty);
    Assert.That(run.Error, Does.StartWith($"polypiler: error PLY0005: output path '{path}' cannot be created: {reason}"));
  }

  /// <summary>Test design 13.</summary>
  [Test]
  public void AnUnknownCommand_IsPly0002()
    => AssertRefused(Invoke("frobnicate"), "polypiler: error PLY0002: unknown command 'frobnicate'; run 'polypiler --help'");

  /// <summary>Test design 14-18, 21, 23-30, 32-37, 41, 45 and 47: every refusal of the grammar, one line, exit code 2, nothing executed.</summary>
  [TestCase("unknown option '--nope'", "echo", "x", "--nope")]
  [TestCase("option '--mode' requires a value", "echo", "x", "--mode")]
  [TestCase("'c' is not a valid value for '--mode'; expected one of: a, b", "echo", "x", "--mode", "c")]
  [TestCase("'c' is not a valid value for '--mode'; expected one of: a, b", "echo", "x", "--mode=c")]
  [TestCase("option '--mode' given more than once", "echo", "x", "--mode", "a", "--mode", "b")]
  [TestCase("option '--force' given more than once", "echo", "x", "--force", "--force")]
  [TestCase("option '--quiet' given more than once", "--quiet", "echo", "x", "--quiet")]
  [TestCase("unknown option '--Mode'", "echo", "x", "--Mode", "a")]
  [TestCase("unknown option '--mode'", "--mode", "a", "echo", "x")]
  [TestCase("unknown option '--output'", "--output", "out", "echo", "x")]
  [TestCase("unknown option '--key'", "echo", "x", "--key", "k")]
  [TestCase("option '--force' does not take a value", "echo", "x", "--force=true")]
  [TestCase("unexpected argument 'true'", "echo", "x", "--force", "true")]
  [TestCase("option '--key' is required", "demand")]
  [TestCase("missing argument <input>", "echo")]
  [TestCase("missing argument <input>", "echo", "--mode", "a")]
  [TestCase("unexpected argument 'y'", "echo", "x", "y")]
  [TestCase("unexpected argument 'y'", "echo", "x", "--", "y")]
  [TestCase("unexpected argument 'y'", "demand", "y", "--key", "k")]
  [TestCase("option '--export' has an empty list element", "echo", "x", "--export", "a,,b")]
  [TestCase("option '--export' has an empty list element", "echo", "x", "--export", "a,")]
  [TestCase("option '--export' has an empty list element", "echo", "x", "--export", ",a")]
  [TestCase("option '--export' has an empty list element", "echo", "x", "--export=")]
  [TestCase("option '--export' has an empty list element", "echo", "x", "--export", "")]
  [TestCase("no command given", "--quiet")]
  [TestCase("no command given", "--diagnostics", "text")]
  [TestCase("no command given", "--", "echo")]
  [TestCase("option '--note' requires a value", "echo", "x", "--note", "-x")]
  [TestCase("option '--mode' requires a value", "echo", "x", "--mode", "--")]
  [TestCase("option '--note' requires a value", "echo", "x", "--note")]
  [TestCase("unknown option '--output:out'", "echo", "x", "--output:out")]
  [TestCase("option '--output' requires a value", "echo", "x", "--output", "--force")]
  [TestCase("unknown option '--nope'", "echo", "--nope")]
  [TestCase("unknown option '-?'", "-?")]
  [TestCase("unexpected argument '/?'", "echo", "x", "/?")]
  [TestCase("unknown option '--version'", "--version", "extra")]
  [TestCase("unknown option '--version'", "echo", "x", "--version")]
  [TestCase("unknown option '--version'", "--quiet", "--version")]
  [TestCase("unknown option '-H'", "-H")]
  [TestCase("unknown option '--Help'", "echo", "--Help")]
  [TestCase("unknown option '-hq'", "echo", "x", "-hq")]
  [TestCase("unknown option '--out'", "echo", "x", "--out", "o")]
  [TestCase("unknown option '-f'", "echo", "x", "-f")]
  [TestCase("unexpected argument 'extra'", "help", "echo", "extra")]
  [TestCase("'xml' is not a valid value for '--diagnostics'; expected one of: text, json", "--diagnostics", "xml", "echo", "x")]
  public void AGrammarViolation_IsOnePly0003(string message, params string[] args)
    => AssertRefused(Invoke(args), "polypiler: error PLY0003: " + message);

  /// <summary>Test design 35: the directive of the library is an unknown command.</summary>
  [Test]
  public void TheSuggestDirective_IsAnUnknownCommand()
    => AssertRefused(Invoke("[suggest]", "echo"), "polypiler: error PLY0002: unknown command '[suggest]'; run 'polypiler --help'");

  /// <summary>Test design 18.</summary>
  [Test]
  public void ExclusiveOptions_ArePly0011()
    => AssertRefused(Invoke("echo", "x", "--left", "--right"), "polypiler: error PLY0011: options '--left' and '--right' cannot be combined");

  [TestCase("--left")]
  [TestCase("--right")]
  public void OneOfTwoExclusiveOptions_IsAccepted(string option)
    => Assert.That(Invoke("echo", Input, option).ExitCode, Is.Zero);

  /// <summary>Test design 19 and 38: an input that is absent, a directory, or written as a response file.</summary>
  [TestCase("tests/fixtures/cli/foundation/absent.txt")]
  [TestCase("tests/fixtures/cli/foundation")]
  [TestCase("@tests/fixtures/cli/foundation/input.txt")]
  [TestCase("@absent.rsp")]
  [TestCase("")]
  [TestCase("a\0b")]
  public void AnInputThatCannotBeRead_IsPly0004(string input) {
    var run = Invoke("echo", input);
    Assert.That(run.ExitCode, Is.EqualTo((int)ExitCode.EnvironmentError));
    Assert.That(run.Output, Is.Empty);
    Assert.That(run.Error, Is.EqualTo($"polypiler: error PLY0004: input '{input}' does not exist or is not readable{Environment.NewLine}" + OneError));
  }

  /// <summary>Test design 22 and 46.</summary>
  [Test]
  public void Quiet_DropsInfosAndWarnings_AndChangesNeitherStdoutNorTheExitCode() {
    DiagnosticDescriptor[] reports = [TestCommands.TestInfo, TestCommands.TestWarning, TestCommands.TestError];
    var loud = InvokeWith(reports, "echo", Input);
    var quiet = InvokeWith(reports, "echo", Input, "--quiet");
    Assert.That(loud.ErrorLines, Is.EqualTo(new[] {
      "polypiler: info PLY0098: test info", "polypiler: warning PLY0098: test warning", "polypiler: error PLY0098: test error", "polypiler: 1 error(s), 1 warning(s)"
    }));
    Assert.That(quiet.ErrorLines, Is.EqualTo(new[] { "polypiler: error PLY0098: test error" }));
    Assert.That((quiet.Output, quiet.ExitCode), Is.EqualTo((loud.Output, loud.ExitCode)));
    Assert.That(quiet.ExitCode, Is.EqualTo((int)ExitCode.EnvironmentError));
  }

  [Test]
  public void Json_LeavesOnlyJsonOnStderr() {
    var run = InvokeWith([TestCommands.TestWarning, TestCommands.TestError], "--diagnostics", "json", "echo", Input);
    Assert.That(run.ExitCode, Is.EqualTo((int)ExitCode.EnvironmentError));
    Assert.That(run.Output, Is.EqualTo("echo " + Input + Environment.NewLine));
    Assert.That(run.ErrorLines, Has.Length.EqualTo(2));
    foreach (var line in run.ErrorLines) {
      using var document = JsonDocument.Parse(line);
      Assert.That(document.RootElement.GetProperty("code").GetString(), Is.EqualTo("PLY0098"));
    }
  }

  /// <summary>Test design 39: with <c>--diagnostics json</c> a refusal is one JSON object, wherever the option stands.</summary>
  [TestCase("--diagnostics", "json", "echo", "x", "--nope")]
  [TestCase("echo", "x", "--nope", "--diagnostics=json")]
  [TestCase("--diagnostics=json", "frobnicate")]
  public void ARefusalInJson_IsOneObject(params string[] args) {
    var run = Invoke(args);
    Assert.That(run.ExitCode, Is.EqualTo((int)ExitCode.UsageError));
    Assert.That(run.Output, Is.Empty);
    using var document = JsonDocument.Parse(run.ErrorLines.Single());
    Assert.That(document.RootElement.GetProperty("severity").GetString(), Is.EqualTo("error"));
  }

  [Test]
  public void AJsonValueAfterTheTerminator_SelectsNothing()
    => Assert.That(Invoke("echo", "--", "--diagnostics=json", "y").Error, Does.StartWith("polypiler: error PLY0003: "));

  [Test]
  public void AnInvalidOptionValueInJson_NamesTheAllowedValues() {
    var run = Invoke("--diagnostics", "json", "echo", Input, "--mode", "c");
    using var document = JsonDocument.Parse(run.ErrorLines.Single());
    Assert.That(document.RootElement.GetProperty("message").GetString(), Is.EqualTo("'c' is not a valid value for '--mode'; expected one of: a, b"));
    Assert.That(run.Echo.Executions, Is.Zero);
  }

  /// <summary>Test design 40-43: help names the command on the line and takes precedence.</summary>
  [TestCase("help", "echo")]
  [TestCase("echo", "-h")]
  [TestCase("echo", "--nope", "--help")]
  [TestCase("--nope", "echo", "-h")]
  [TestCase("--diagnostics", "json", "echo", "x", "--help")]
  [TestCase("echo", "--mode", "--help")]
  public void Help_ShowsTheHelpOfTheCommandOnTheLine(params string[] args) {
    var run = Invoke(args);
    Assert.That((run.ExitCode, run.Output, run.Error), Is.EqualTo((0, Invoke("echo", "--help").Output, "")));
    Assert.That(run.Echo.Executions, Is.Zero);
  }

  [TestCase("help")]
  [TestCase("--nope", "--help")]
  [TestCase("--quiet", "-h")]
  [TestCase("help", "--quiet")]
  [TestCase("--diagnostics", "-h")]
  public void HelpWithoutACommand_IsTheRootHelp(params string[] args) {
    var run = Invoke(args);
    Assert.That((run.ExitCode, run.Output, run.Error), Is.EqualTo((0, Invoke("--help").Output, "")));
  }

  [TestCase("frobnicate", "--help")]
  [TestCase("help", "frobnicate")]
  public void HelpForAnUnknownCommand_IsPly0002(params string[] args)
    => AssertRefused(Invoke(args), "polypiler: error PLY0002: unknown command 'frobnicate'; run 'polypiler --help'");

  /// <summary>Test design 45.</summary>
  [Test]
  public void Version_IsTheSoleArgument()
    => Assert.That((Invoke("--version").ExitCode, Invoke("--version").Output), Is.EqualTo((0, ProductInfo.Version + Environment.NewLine)));

  /// <summary>Test design 48: a refused command line creates no output directory.</summary>
  [Test]
  public void ARefusal_CreatesNoOutputDirectory() {
    var directory = TemporaryDirectory();
    AssertRefused(Invoke("echo", Input, "--output", directory, "--nope"), "polypiler: error PLY0003: unknown option '--nope'");
    Assert.That(Directory.Exists(directory), Is.False);
  }

  /// <summary>Test design 33: the next argument is not taken as the value, and no directory of its name is created.</summary>
  [Test]
  public void AnOptionAsTheValueOfAnother_CreatesNothing() {
    AssertRefused(Invoke("echo", Input, "--output", "--force"), "polypiler: error PLY0003: option '--output' requires a value");
    Assert.That(Directory.Exists(Path.Combine(PrototypeFixture.RepositoryRoot, "--force")), Is.False);
  }

  /// <summary>Test design 49-51: the invocation as typed, quoted, and not normalised.</summary>
  [TestCase("a.cs", "a.cs")]
  [TestCase("--to=javascript", "--to=javascript")]
  [TestCase("C:/v.json", "C:/v.json")]
  [TestCase("a,b", "a,b")]
  [TestCase("x@y%z+1", "x@y%z+1")]
  [TestCase("my file.cs", "\"my file.cs\"")]
  [TestCase("a;b", "\"a;b\"")]
  [TestCase("(x)", "\"(x)\"")]
  [TestCase("*", "\"*\"")]
  [TestCase("", "\"\"")]
  [TestCase("a\\b", "\"a\\\\b\"")]
  [TestCase("a\"b", "\"a\\\"b\"")]
  [TestCase("a$b", "\"a\\$b\"")]
  [TestCase("a`b", "\"a\\`b\"")]
  [TestCase("é", "\"é\"")]
  public void TheInvocation_QuotesAnArgumentOnlyWhenItMust(string argument, string written)
    => Assert.That(CliContext.Invocation([argument]), Is.EqualTo("polypiler " + written));

  [TestCase("--quiet", "echo", "x", "--mode=a")]
  [TestCase("echo", "x", "--mode", "a", "--quiet")]
  public void TheInvocation_KeepsOrderAndSpelling(params string[] args)
    => Assert.That(CliContext.Invocation(args), Is.EqualTo("polypiler " + string.Join(' ', args)));

  [Test]
  public void ARefusedLine_CarriesTheInvocationAsTyped() {
    var run = Invoke("--diagnostics=json", "echo", "my file.txt", "--mode", "c");
    using var document = JsonDocument.Parse(run.ErrorLines.Single());
    Assert.That(document.RootElement.GetProperty("code").GetString(), Is.EqualTo("PLY0003"));
    Assert.That(document.RootElement.GetProperty("command").GetString(), Is.EqualTo("polypiler --diagnostics=json echo \"my file.txt\" --mode c"));
  }

  [Test]
  public void ADiagnosticOfACommand_CarriesTheInvocation() {
    var run = InvokeWith([TestCommands.TestError], "echo", Input, "--diagnostics", "json");
    using var document = JsonDocument.Parse(run.ErrorLines.Single());
    Assert.That(document.RootElement.GetProperty("command").GetString(), Is.EqualTo($"polypiler echo {Input} --diagnostics json"));
  }

  /// <summary>A value the grammar accepts but the library cannot convert is a defect of polypiler: an internal error, without the library's words.</summary>
  [Test]
  public void AValueTheLibraryRefuses_IsAnInternalError() {
    var registry = new CommandRegistry();
    registry.Add(new CountCommand());
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(Program.Run(["count", "--count", "x"], output, error, registry, "."), Is.EqualTo((int)ExitCode.InternalError));
    Assert.That(output.ToString(), Is.Empty);
    Assert.That(error.ToString(), Is.EqualTo("polypiler: error PLY0001: internal compiler error: System.CommandLine refused a command line the grammar accepts" + Environment.NewLine));
  }

  /// <summary>ADR 0003 decision 6 (issue #23): a number of the command's own that no error stands for is an internal error.</summary>
  [Test]
  public void ACommandsOwnExitCode_WithoutADiagnostic_IsAnInternalError() {
    var registry = new CommandRegistry();
    registry.Add(new CountCommand());
    using var error = new StringWriter();
    Assert.That(Program.Run(["count", "--count", "7"], TextWriter.Null, error, registry, "."), Is.EqualTo((int)ExitCode.InternalError));
    Assert.That(error.ToString(), Does.StartWith("polypiler: error PLY0001: command 'count' returned 7 without a diagnostic"));
  }

  /// <summary>An exception outside a command, here from the declaration of its options, is an internal error too (issue #23).</summary>
  [Test]
  public void AnOptionDeclaredTwice_IsADefectOfTheCommand() {
    var registry = new CommandRegistry();
    registry.Add(new CountCommand { Twice = true });
    using var error = new StringWriter();
    Assert.That(Program.Run(["count"], TextWriter.Null, error, registry, "."), Is.EqualTo((int)ExitCode.InternalError));
    Assert.That(error.ToString(), Does.StartWith(
      "polypiler: error PLY0001: internal compiler error: InvalidOperationException: the command 'count' declares the option '--quiet' twice or as a global option"));
  }

  [Test]
  public void Run_RefusesNull() {
    var registry = new CommandRegistry();
    Assert.That(() => Program.Run(null!, TextWriter.Null, TextWriter.Null, registry, "."), Throws.ArgumentNullException);
    Assert.That(() => Program.Run([], null!, TextWriter.Null, registry, "."), Throws.ArgumentNullException);
    Assert.That(() => Program.Run([], TextWriter.Null, null!, registry, "."), Throws.ArgumentNullException);
    Assert.That(() => Program.Run([], TextWriter.Null, TextWriter.Null, null!, "."), Throws.ArgumentNullException);
    Assert.That(() => Program.Run([], TextWriter.Null, TextWriter.Null, registry, null!), Throws.ArgumentNullException);
  }

  /// <summary><c>count [--count &lt;n&gt;]</c>: an integer option, whose conversion the library does, and an exit code of its own.</summary>
  private sealed class CountCommand : ICliCommand {

    public bool Twice { get; init; }

    public string Name => "count";

    public string Description => "count";

    public void Configure(Command command) {
      command.Options.Add(new Option<int>("--count"));
      if (this.Twice)
        command.Options.Add(new Option<bool>("--quiet"));
    }

    public int Execute(ParseResult parse, CliContext context) => parse.GetValue<int>("--count");

  }

}
