using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Verification.Runners;

/// <summary>
/// The parts of the LibreOffice Basic runner that need no office: the files of the profile, the driver,
/// the reading of the answer and the outcomes of a run, the run itself stood in for.
/// </summary>
[TestFixture]
public sealed class VisualBasicRunnerTests {

  private sealed class Fake(Func<RunPlan, RunResult> run) : ICrossLanguageRunner {

    public RunPlan? Plan { get; private set; }

    public Task<RunResult> RunAsync(RunPlan plan, CancellationToken cancellationToken = default) {
      this.Plan = plan;
      return Task.FromResult(run(plan));
    }

  }

  private static RunResult Result(RunOutcome outcome, int exitCode, IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? collected = null)
    => new(outcome, exitCode, ReadOnlyMemory<byte>.Empty, Encoding.UTF8.GetBytes("stderr"), TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited, []) {
      Collected = collected ?? new Dictionary<string, ReadOnlyMemory<byte>>()
    };

  [Test]
  public void Prepare_DropsTheAttributesAndSetsVbaSupport() {
    Assert.That(VisualBasicRunner.Prepare("Attribute VB_Name = \"M\"\r\nOption Explicit\r\n  attribute x = 1\n"), Is.EqualTo("Option VBASupport 1\nOption Explicit\n"));
    Assert.That(() => VisualBasicRunner.Prepare(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Driver_CallsEveryCaseInAFunctionOfItsOwn() {
    var driver = VisualBasicRunner.Driver([new("F(1)"), new(["Dim a As Long", "a = 2"], "G(a)")]);
    Assert.That(driver, Does.Contain("Print #f, PlyCase0()").And.Contain("Print #f, PlyCase1()").And.Contain("    Dim a As Long\n    a = 2\n    PlyCase1 = CStr(G(a))")
      .And.Contain("On Error GoTo Failed").And.Contain("PlyCase0 = \"!\" & CStr(Err.Number)").And.Contain("Environ(\"HOME\") & \"/out.txt\""));
    Assert.That(() => VisualBasicRunner.Driver(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Files_AreAProfileWithTheStandardLibrary() {
    var files = VisualBasicRunner.Files(new Dictionary<string, string> { ["M"] = "Sub S()\nEnd Sub\n" }, "Sub Main()\nEnd Sub\n");
    var root = VisualBasicRunner.ProfileFolder + "/";
    Assert.That(files.Keys, Is.EquivalentTo(new[] {
      root + "registrymodifications.xcu", root + "basic/script.xlc", root + "basic/dialog.xlc", root + "basic/Standard/dialog.xlb",
      root + "basic/Standard/script.xlb", root + "basic/Standard/M.xba", root + "basic/Standard/PlyDriver.xba"
    }));
    Assert.That(Encoding.UTF8.GetString(files[root + "basic/Standard/script.xlb"].Span), Does.Contain("library:name=\"M\"").And.Contain("library:name=\"PlyDriver\""));
    Assert.That(Encoding.UTF8.GetString(files[root + "basic/Standard/M.xba"].Span), Does.Contain("Option VBASupport 1"));
    Assert.That(Encoding.UTF8.GetString(files[root + "registrymodifications.xcu"].Span), Does.Contain("ooSetupInstCompleted").And.Contain("ooSetupSystemLocale").And.Contain("<value>en-US</value>"));
    var german = VisualBasicRunner.Files(new Dictionary<string, string>(), "", "de-DE");
    Assert.That(Encoding.UTF8.GetString(german[root + "registrymodifications.xcu"].Span), Does.Contain("<value>de-DE</value>"));
    Assert.That(Encoding.UTF8.GetString(VisualBasicRunner.Files(new Dictionary<string, string> { ["M"] = "x = a < b & \"c\"" }, "")[root + "basic/Standard/M.xba"].Span), Does.Contain("a &lt; b &amp; &quot;c&quot;"));
    Assert.That(() => VisualBasicRunner.Files(null!, ""), Throws.ArgumentNullException);
    Assert.That(() => VisualBasicRunner.Files(new Dictionary<string, string>(), null!), Throws.ArgumentNullException);
    Assert.That(VisualBasicRunner.Command.ToString(), Is.EqualTo("libreoffice --headless --invisible --norestore --nolockcheck macro:///Standard.PlyDriver.Main"));
  }

  [Test]
  public void Read_GivesAValueOrAnErrorPerLine() {
    var read = VisualBasicRunner.Read("5\r\n!11\n\n", 3);
    Assert.That(read.Outcomes!.Select(outcome => outcome.ToString()), Is.EqualTo(new[] { "5", "!11", "" }));
    Assert.That(read.Outcomes![1].Trap, Is.EqualTo(new TrapClass(TrapKind.DivideByZero)));
    Assert.That(read.Outcomes![0].Trap, Is.Null);
    Assert.That(VisualBasicRunner.Read("1\n", 2).Diagnostics.Single().Code, Is.EqualTo("PLY2695"));
    Assert.That(VisualBasicRunner.Read("!x\n", 1).Diagnostics.Single().Message, Does.Contain("no error number"));
    Assert.That(() => VisualBasicRunner.Read(null!, 0), Throws.ArgumentNullException);
  }

  [TestCase(6, TrapKind.Overflow)]
  [TestCase(11, TrapKind.DivideByZero)]
  [TestCase(9, TrapKind.Bounds)]
  [TestCase(91, TrapKind.Null)]
  [TestCase(5, TrapKind.InvalidArgument)]
  [TestCase(13, TrapKind.Other)]
  public void Classify_MapsErrorNumbersToTrapClasses(int number, TrapKind kind) {
    var trap = VisualBasicRunner.Classify(number);
    Assert.That(trap.Kind, Is.EqualTo(kind));
    if (kind == TrapKind.Other)
      Assert.That(trap.ToString(), Is.EqualTo("other:VbError13"));
  }

  [Test]
  public async Task Execute_ReadsTheCollectedAnswer() {
    var fake = new Fake(_ => Result(RunOutcome.Completed, 0, new Dictionary<string, ReadOnlyMemory<byte>> { [VisualBasicRunner.AnswerFile] = Encoding.UTF8.GetBytes("7\n") }));
    var execution = await new VisualBasicRunner(fake).ExecuteAsync(new Dictionary<string, string> { ["M"] = "" }, [new("F()")]);
    Assert.That(execution.Outcomes!.Single().Value, Is.EqualTo("7"));
    Assert.That(fake.Plan!.Collect, Is.EqualTo(new[] { VisualBasicRunner.AnswerFile }));
    Assert.That(fake.Plan.Limits.WallTime, Is.EqualTo(VisualBasicRunner.WallTime));
    Assert.That(fake.Plan.Trust, Is.EqualTo(TrustLevel.Trusted));
  }

  [Test]
  public async Task Execute_ReportsARunWithoutAnswer() {
    var timedOut = await new VisualBasicRunner(new Fake(_ => Result(RunOutcome.TimedOut, -1))).ExecuteAsync(new Dictionary<string, string>(), [new("1")]);
    Assert.That(timedOut.Diagnostics.Single().Code, Is.EqualTo("PLY2691"));
    var crashed = await new VisualBasicRunner(new Fake(_ => Result(RunOutcome.Completed, 134))).ExecuteAsync(new Dictionary<string, string>(), [new("1")]);
    Assert.That(crashed.Diagnostics.Single().Code, Is.EqualTo("PLY2692"));
    Assert.That(crashed.Diagnostics.Single().Message, Does.Contain("134").And.Contain("stderr"));
    var refused = await new VisualBasicRunner(new Fake(_ => Result(RunOutcome.Refused, -1))).ExecuteAsync(new Dictionary<string, string>(), [new("1")]);
    Assert.That(refused.Outcomes, Is.Null);
    Assert.That(() => new VisualBasicRunner(null!), Throws.ArgumentNullException);
    Assert.That(async () => await new VisualBasicRunner(new Fake(_ => Result(RunOutcome.Completed, 0))).ExecuteAsync(new Dictionary<string, string>(), null!), Throws.ArgumentNullException);
  }

}
