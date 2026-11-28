using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Verification.Runners;

/// <summary>
/// The parts of the Visual Basic .NET runner that need no SDK: the driver and its project, the trap
/// table, the reading of the answer and of the dialect table, and the outcomes of a run stood in for.
/// </summary>
[TestFixture]
public sealed class VbNetRunnerTests {

  private sealed class Fake(Func<RunPlan, RunResult> run) : ICrossLanguageRunner {

    public RunPlan? Plan { get; private set; }

    public Task<RunResult> RunAsync(RunPlan plan, CancellationToken cancellationToken = default) {
      this.Plan = plan;
      return Task.FromResult(run(plan));
    }

  }

  private static RunResult Result(RunOutcome outcome, string stdout, params Diagnostic[] diagnostics)
    => new(outcome, 3, Encoding.UTF8.GetBytes(stdout), Encoding.UTF8.GetBytes("stderr"), TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited, diagnostics);

  [TestCase("OverflowException", TrapKind.Overflow)]
  [TestCase("DivideByZeroException", TrapKind.DivideByZero)]
  [TestCase("IndexOutOfRangeException", TrapKind.Bounds)]
  [TestCase("NullReferenceException", TrapKind.Null)]
  [TestCase("ArgumentException", TrapKind.InvalidArgument)]
  [TestCase("ArgumentNullException", TrapKind.InvalidArgument)]
  [TestCase("ArgumentOutOfRangeException", TrapKind.InvalidArgument)]
  [TestCase("InvalidCastException", TrapKind.Other)]
  public void Classify_MapsTheExceptionToItsTrapClass(string exception, TrapKind kind) {
    Assert.That(VbNetRunner.Classify(exception).Kind, Is.EqualTo(kind));
    Assert.That(() => VbNetRunner.Classify(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Driver_PrintsEveryCallInsideATry() {
    var driver = VbNetRunner.Driver([new("F(1)"), new(["Dim a As Integer() = New Integer() {1}"], "G(a)")]);
    Assert.That(driver, Does.Contain("System.Console.WriteLine(PlyCase0())").And.Contain("System.Console.WriteLine(PlyCase1())"));
    Assert.That(driver, Does.Contain("            Dim a As Integer() = New Integer() {1}\n            Return PlyShow(G(a))"));
    Assert.That(driver, Does.Contain("Return \"!\" & ex.GetType().Name"));
    Assert.That(() => VbNetRunner.Driver(null!), Throws.ArgumentNullException);
  }

  [TestCase(true, "<RemoveIntegerChecks>true</RemoveIntegerChecks>")]
  [TestCase(false, "<RemoveIntegerChecks>false</RemoveIntegerChecks>")]
  public void Project_StatesTheOptionsAndTheLanguageVersion(bool removed, string checks) {
    var project = VbNetRunner.Project("15.5", removed);
    Assert.That(project, Does.Contain("<LangVersion>15.5</LangVersion>").And.Contain(checks).And.Contain("<OptionStrict>On</OptionStrict>").And.Contain("<NuGetAudit>false</NuGetAudit>"));
    Assert.That(() => VbNetRunner.Project(null!, true), Throws.ArgumentNullException);
  }

  [Test]
  public void Read_TakesValuesAndExceptions() {
    var execution = VbNetRunner.Read("5\r\n!OverflowException\nTrue\n", 3);
    Assert.That(execution.Outcomes!.Select(outcome => outcome.ToString()), Is.EqualTo(new[] { "5", "!OverflowException", "True" }));
    Assert.That(execution.Outcomes![1].Trap!.Kind, Is.EqualTo(TrapKind.Overflow));
    Assert.That(execution.Outcomes![0].Trap, Is.Null);
    Assert.That(() => VbNetRunner.Read(null!, 1), Throws.ArgumentNullException);
  }

  [TestCase("", 1)]
  [TestCase("1\n2\n", 1)]
  public void Read_OfAnotherNumberOfLines_IsPly2693(string text, int calls)
    => Assert.That(VbNetRunner.Read(text, calls, 1, "boom").Diagnostics.Single().Code, Is.EqualTo("PLY2693"));

  [Test]
  public void Table_ReadsEntriesAndSkipsComments() {
    var table = VbNetRunner.Table("# comment\r\nnumber:overflow=6\n\nKept=6\n");
    Assert.That(table, Is.EqualTo(new[] { ("number:overflow", "6"), ("Kept", "6") }));
    Assert.That(() => VbNetRunner.Table(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Compare_NamesTheFirstEntryThatDiffers() {
    (string, string)[] table = [("a", "1"), ("b", "2")];
    Assert.That(VbNetRunner.Compare(table, ["1", "2"]), Is.Empty);
    Assert.That(VbNetRunner.Compare(table, ["1", "3"]).Single().Code, Is.EqualTo("PLY2696"));
    Assert.That(VbNetRunner.Compare(table, ["1"]).Single().Message, Does.Contain("'b'"));
    Assert.That(() => VbNetRunner.Compare(null!, []), Throws.ArgumentNullException);
    Assert.That(() => VbNetRunner.Compare(table, null!), Throws.ArgumentNullException);
  }

  [Test]
  public async Task Execute_BuildsTheDriverOfflineAndReadsItsAnswer() {
    var fake = new Fake(_ => Result(RunOutcome.Completed, "7\n"));
    var execution = await new VbNetRunner(fake).ExecuteAsync(new Dictionary<string, string> { ["M.vb"] = "Module M\nEnd Module\n" }, [new("F()")]);
    Assert.That(execution.Outcomes!.Single().Value, Is.EqualTo("7"));
    Assert.That(fake.Plan!.Files.Keys, Is.EquivalentTo(new[] { "M.vb", "PlyDriver.vb", "PlyDriver.vbproj", "NuGet.config" }));
    Assert.That(fake.Plan.Build.Single().Arguments, Does.Contain("PlyDriver.vbproj"));
  }

  [Test]
  public async Task Execute_OfAProjectThatDoesNotBuild_IsPly2690WithWhatTheBuildPrinted() {
    var failure = new Diagnostic("PLY0031", DiagnosticSeverity.Error, "target build failed");
    var execution = await new VbNetRunner(new Fake(_ => Result(RunOutcome.BuildFailed, "error BC30201", failure))).ExecuteAsync(new Dictionary<string, string>(), [new("F()")]);
    Assert.That(execution.Diagnostics.Single().Code, Is.EqualTo("PLY2690"));
    Assert.That(execution.Diagnostics.Single().Message, Does.Contain("BC30201").And.Contain("target build failed"));
  }

  [Test]
  public async Task Execute_OfARunThatTimesOut_GivesItsDiagnostics() {
    var timeout = new Diagnostic("PLY0033", DiagnosticSeverity.Error, "timed out");
    var execution = await new VbNetRunner(new Fake(_ => Result(RunOutcome.TimedOut, "", timeout))).ExecuteAsync(new Dictionary<string, string>(), [new("F()")]);
    Assert.That(execution.Outcomes, Is.Null);
    Assert.That(execution.Diagnostics, Is.EqualTo(new[] { timeout }));
  }

  [Test]
  public void Execute_RefusesNullArguments() {
    var runner = new VbNetRunner(new Fake(_ => Result(RunOutcome.Completed, "")));
    Assert.That(() => runner.ExecuteAsync(null!, []), Throws.ArgumentNullException);
    Assert.That(() => runner.ExecuteAsync(new Dictionary<string, string>(), null!), Throws.ArgumentNullException);
    Assert.That(() => new VbNetRunner(null!), Throws.ArgumentNullException);
  }

}
