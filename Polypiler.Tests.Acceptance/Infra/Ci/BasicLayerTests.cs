using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Verification.Running.Support;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The rows of the test design of the BASIC/PB layer that its feature does not state
/// (<c>tests/features/infra/infra-ci-basic.feature</c>): the oracle table's values and checks, the
/// boundaries of <c>ARITH.BAS</c>, a program without output, the time limit around a one-second run and
/// the toolchain gate. The runs need the pinned <c>pbc</c> and DOSBox-X.
/// </summary>
[TestFixture, Layer("basic")]
public sealed class BasicLayerTests {

  private const string EndToEnd = "EndToEnd";
  private const string Fixtures = "tests/fixtures/infra/ci/basic";

  private static readonly Lazy<Task<(RunResult Run, string Expected)>> _arith = new(async () => {
    var run = await PbCommands.RunAsync(PbCommands.Plan(BasicUpstream.Read("tests/ARITH.BAS"), RunLimits.Default));
    PbCommands.Record("ARITH", run);
    return (run, Encoding.Latin1.GetString(BasicUpstream.Read("tests/ARITH.expected")));
  });

  private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(PrototypeFixture.RepositoryRoot, Fixtures, name));

  private static string Yaml(params string[] rows) => "schema: 1\noracles:\n" + string.Concat(rows.Select(row => $"  - {row}\n"));

  #region the oracle table

  [TestCase("ci", OracleAvailability.Ci, true, TestName = "An oracle that runs in CI can make a test pass")]
  [TestCase("external", OracleAvailability.External, false, TestName = "An external oracle is reported only")]
  [TestCase("none", OracleAvailability.None, false, TestName = "An oracle that does not exist is reported only")]
  public void Availability_EachValue_IsReadAndOnlyCiCanPass(string text, OracleAvailability availability, bool canPass) {
    var oracle = BasicOracleTable.Parse(Yaml($"{{ oracle: x, toolchain: x, availability: {text} }}")).Single();
    Assert.That((oracle.Availability, oracle.CanPass, BasicOracleTable.Render([oracle]).Split('\n')[2]), Is.EqualTo((availability, canPass, $"| x | x | {text} |")));
  }

  [Test]
  public void Table_OfTheRepository_EqualsTheExpectation() {
    var table = BasicOracleTable.Render(BasicOracleTable.Load(PrototypeFixture.RepositoryRoot));
    Assert.That(BasicOracleTable.Differences(table, File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, BasicOracleTable.ExpectedPath))), Is.Empty);
  }

  [Test]
  public void Table_OfTheRepository_GivesEveryOracleAnAvailabilityAndNoPassToTheGenuineCompiler() {
    var oracles = BasicOracleTable.Load(PrototypeFixture.RepositoryRoot);
    Assert.That(oracles.Select(oracle => (oracle.Oracle, oracle.CanPass)), Is.EqualTo(new[] { ("pb-compiler", true), ("dosbox-x", true), ("vice", true), ("powerbasic-genuine", false) }));
  }

  [Test]
  public void Table_WhenARowWasEdited_ShowsTheDifferingRow() {
    var edited = BasicOracleTable.Render(BasicOracleTable.Parse(Yaml("{ oracle: pb-compiler, toolchain: pb-compiler, availability: ci }", "{ oracle: powerbasic-genuine, availability: ci }")));
    var expected = "| Oracle | Toolchain | Availability |\r\n| --- | --- | --- |\r\n| pb-compiler | pb-compiler | ci |\r\n| powerbasic-genuine | - | external |\r\n";
    Assert.That(BasicOracleTable.Differences(edited, expected), Is.EqualTo(new[] { "line 4: expected '| powerbasic-genuine | - | external |', actual '| powerbasic-genuine | - | ci |'" }));
  }

  [Test]
  public void Table_WhenARowIsMissingOrExtra_SaysNoLine() {
    var two = "| a |\n| b |\n";
    Assert.That((BasicOracleTable.Differences("| a |\n", two), BasicOracleTable.Differences(two, "| a |\n"), BasicOracleTable.Differences("", "")),
      Is.EqualTo((new[] { "line 2: expected '| b |', actual no line" }, new[] { "line 2: expected no line, actual '| b |'" }, Array.Empty<string>())));
  }

  [TestCase("oracles: [", "not valid YAML", TestName = "A file that is not YAML is refused")]
  [TestCase("schema: 1\n", "no 'oracles' list", TestName = "A file without the list is refused")]
  [TestCase("schema: 1\noracles: {}\n", "no 'oracles' list", TestName = "A list that is a mapping is refused")]
  [TestCase("schema: 1\noracles: [x]\n", "row 1 is not a mapping", TestName = "A row that is no mapping is refused")]
  [TestCase("schema: 1\noracles: [{ availability: ci }]\n", "row 1 has no oracle", TestName = "A row without its oracle is refused")]
  [TestCase("schema: 1\noracles: [{ oracle: x, availability: maybe }]\n", "oracle 'x' has the availability 'maybe'", TestName = "An unknown availability is refused")]
  [TestCase("schema: 1\noracles: [{ oracle: x }]\n", "oracle 'x' has the availability ''", TestName = "A row without availability is refused")]
  public void Parse_InvalidFile_NamesTheProblem(string text, string message)
    => Assert.That(() => BasicOracleTable.Parse(text), Throws.TypeOf<FormatException>().With.Message.Contains(message));

  [Test]
  public void Parse_RowWithoutToolchain_RendersADash()
    => Assert.That(BasicOracleTable.Render(BasicOracleTable.Parse(Yaml("{ oracle: g, availability: external }"))), Does.EndWith("| g | - | external |\n"));

  #endregion

  #region the comparison with PB-Compiler's expectations

  [Test]
  public void Differences_IgnoreLineEndsAndTrailingBlanksOnly() {
    Assert.That(BasicUpstream.Differences(" 3 \r\n 7 \r\n", " 3\n 7\n"), Is.Empty);
    Assert.That(BasicUpstream.Differences(" 3 \n7 \n", " 3\n 7\n"), Is.EqualTo(new[] { "line 2: expected ' 7', actual '7'" }));
  }

  [Test]
  public void Captured_WithoutAnOutputFile_IsNull() {
    var run = new RunResult(RunOutcome.Completed, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited, []);
    Assert.That(PbCommands.Captured(run), Is.Null);
  }

  [Test]
  public void Plan_CompilesThenRunsTheEmulatorAndCollectsTheOutput() {
    var plan = PbCommands.Plan(new byte[] { 0x45 }, RunLimits.Default);
    Assert.Multiple(() => {
      Assert.That(plan.Build.Single().ToString(), Is.EqualTo("pb-compiler PROG.BAS -O PROG.EXE"));
      Assert.That((plan.Execute.Toolchain, plan.Execute.Environment["SDL_VIDEODRIVER"], plan.Collect), Is.EqualTo(("dosbox-x", "dummy", new[] { "OUT.TXT" })));
      Assert.That(Encoding.ASCII.GetString(plan.Files["dosbox.conf"].Span), Does.Contain("\r\nmount c .\r\nc:\r\nPROG.EXE > OUT.TXT\r\nexit\r\n"));
    });
  }

  #endregion

  #region runs in the emulator

  /// <summary>Test design 4 and 5: positive division and modulo, and the 16-bit integer limits, as <c>ARITH.expected</c> has them.</summary>
  [Category(EndToEnd), RequiresToolchain(PbCommands.Compiler), RequiresToolchain(PbCommands.Emulator)]
  [TestCase(3, " 3", TestName = "7 \\ 2 prints 3")]
  [TestCase(4, " 1", TestName = "7 MOD 3 prints 1")]
  [TestCase(8, " 32767", TestName = "The largest INTEGER prints as itself")]
  [TestCase(9, "-32768", TestName = "The smallest INTEGER prints as itself")]
  public async Task Arith_Line_IsAsExpected(int index, string line) {
    var (run, expected) = await _arith.Value;
    var captured = PbCommands.Captured(run) ?? throw new AssertionException($"no output: {run.Outcome}");
    Assert.That((BasicUpstream.Trimmed(captured)[index], BasicUpstream.Trimmed(expected)[index]), Is.EqualTo((line, line)));
  }

  /// <summary>Test design 6: a program that prints nothing leaves an empty output file.</summary>
  [Test, Category(EndToEnd), RequiresToolchain(PbCommands.Compiler), RequiresToolchain(PbCommands.Emulator)]
  public async Task Empty_LeavesAnEmptyOutputAndCompletes() {
    var run = await PbCommands.RunAsync(PbCommands.Plan(Fixture("empty.bas"), RunLimits.Default));
    PbCommands.Record("empty", run);
    Assert.That((run.Outcome, run.ExitCode, PbCommands.Captured(run)), Is.EqualTo((RunOutcome.Completed, 0, "")));
  }

  /// <summary>Test design 7: a run of about one second completes within 5 s and is stopped at 0.2 s; the compiler runs before, so the limits hold the emulator alone.</summary>
  [Category(EndToEnd), RequiresToolchain(PbCommands.Compiler), RequiresToolchain(PbCommands.Emulator)]
  [TestCase(5000, RunOutcome.Completed, TestName = "A one-second run completes within 5 s")]
  [TestCase(200, RunOutcome.TimedOut, TestName = "A one-second run is stopped at 0.2 s")]
  public async Task Second_AroundTheLimit_CompletesOrTimesOut(int milliseconds, RunOutcome outcome) {
    var compiled = await PbCommands.RunAsync(PbCommands.CompilePlan(Fixture("second.bas"), RunLimits.Default));
    Assert.That(compiled.Outcome, Is.EqualTo(RunOutcome.Completed), Encoding.UTF8.GetString(compiled.Stderr.Span));
    var run = await PbCommands.RunAsync(PbCommands.ExecutePlan(compiled.Collected[PbCommands.Executable], RunnerTesting.Limits(wallTime: TimeSpan.FromMilliseconds(milliseconds))));
    PbCommands.Record($"second-{milliseconds}ms", run);
    Assert.That((run.Outcome, RunnerTesting.Survivors(run.WorkingDirectory!)), Is.EqualTo((outcome, Array.Empty<int>())));
    if (outcome == RunOutcome.Completed)
      Assert.That(PbCommands.Captured(run), Is.EqualTo("done\n"));
  }

  #endregion

  #region the toolchain gate

  /// <summary>Test design 10: without DOSBox-X a test that needs it is inconclusive, and fails in CI.</summary>
  [Test]
  public void EmulatorAbsent_IsNotRunRatherThanPassed() {
    var attribute = new RequiresToolchainAttribute(PbCommands.Emulator) { Lock = $"{Fixtures}/absent.lock" };
    Assert.Multiple(() => {
      Assert.That(Assert.Throws<InconclusiveException>(() => attribute.Check(null))!.Message, Does.Contain("toolchain 'dosbox-x'").And.Contain("PLY0021"));
      Assert.That(Assert.Throws<AssertionException>(() => attribute.Check("1"))!.Message, Does.Contain("PLY0021"));
    });
  }

  /// <summary>Test design 11: a PB-Compiler built from another commit than the pin is a version mismatch.</summary>
  [Test, RequiresToolchain(PbCommands.Compiler)]
  public void CompilerAtAnotherCommit_IsAMismatch() {
    var attribute = new RequiresToolchainAttribute(PbCommands.Compiler) { Lock = $"{Fixtures}/other-commit.lock" };
    Assert.That(Assert.Throws<AssertionException>(() => attribute.Check("1"))!.Message, Does.Contain("PLY0022").And.Contain("1111111111111111111111111111111111111111"));
  }

  #endregion

}
