using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Verification.Runners;

/// <summary>
/// The C# runner without the toolchain (#220): the trap table, the plans it makes, and what it makes
/// of every kind of answer, given by a runner that replays a recorded one. The acceptance project
/// runs the same against the pinned SDK.
/// </summary>
[TestFixture]
public sealed class CSharpRunnerUnitTests {

  /// <summary>Answers each plan with the next result and keeps the plans.</summary>
  private sealed class Replay(params RunResult[] results) : ICrossLanguageRunner {

    private int _next;

    public List<RunPlan> Plans { get; } = [];

    public Task<RunResult> RunAsync(RunPlan plan, CancellationToken cancellationToken = default) {
      this.Plans.Add(plan);
      return Task.FromResult(results[Math.Min(this._next++, results.Length - 1)]);
    }

  }

  private static RunResult Result(RunOutcome outcome, int exitCode, string stdout = "", string stderr = "", params Diagnostic[] diagnostics)
    => new(outcome, exitCode, Encoding.UTF8.GetBytes(stdout), Encoding.UTF8.GetBytes(stderr), TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited, diagnostics);

  private static readonly CallSignature _add = new([new CallParameter("a", ImageType.I32, ParameterMode.In), new CallParameter("b", ImageType.I32, ParameterMode.In)], ImageType.I32);

  private static RunnerInput Input(string source = "static class T { public static int F(int a, int b) => a + b; }", string profile = "csharp/net10", string export = "T.F", CallSignature? signature = null)
    => new(new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["Add.cs"] = Encoding.UTF8.GetBytes(source) }, export, signature ?? _add, profile, TrustLevel.Trusted);

  private static Diagnostic BuildFailed(string output)
    => Diagnostic.Create(RunnerDiagnostics.BuildFailed, null, CSharpRunner.BuildStep, 1) with { Cause = output };

  private DirectoryInfo? _workspace;

  [SetUp]
  public void CreateWorkspace() => this._workspace = Directory.CreateTempSubdirectory("polypiler-csharp-unit-");

  [TearDown]
  public void DeleteWorkspace() => this._workspace?.Delete(true);

  private CSharpRunner Runner(Replay replay, string? packages = null) => new(replay, this._workspace!.FullName, packages: packages);

  [TestCase("OverflowException", "overflow")]
  [TestCase("DivideByZeroException", "divide-by-zero")]
  [TestCase("IndexOutOfRangeException", "bounds")]
  [TestCase("NullReferenceException", "null")]
  [TestCase("ArgumentException", "invalid-argument")]
  [TestCase("ArgumentNullException", "invalid-argument")]
  [TestCase("ArgumentOutOfRangeException", "invalid-argument")]
  [TestCase("UnreachableException", "unreachable")]
  [TestCase("InvalidOperationException", "other:InvalidOperationException")]
  [TestCase("overflowexception", "other:overflowexception")]
  public void EachRowOfTheTrapTable_IsTheClassOfItsException(string exception, string trap)
    => Assert.That(CSharpTraps.Classify(exception).ToString(), Is.EqualTo(trap));

  [TestCase("")]
  [TestCase(" ")]
  public void AnEmptyExceptionName_IsRefused(string name) => Assert.Throws<ArgumentException>(() => CSharpTraps.Classify(name));

  [Test]
  public void TheRunner_IsCSharpOnTheDotnetSdk_AndRegisters() {
    var runner = this.Runner(new Replay(Result(RunOutcome.Completed, 0)));
    var runners = new LanguageRunners();
    runners.Register(runner);

    Assert.That((runner.Language, runner.Toolchains.Single()), Is.EqualTo(("csharp", "dotnet-sdk")));
    Assert.That(runners.Resolve("csharp"), Is.SameAs(runner));
  }

  [Test]
  public void ABuild_PlansTheSourcesTheDriverAndAnOfflineRestore_AndKeepsTheOutput() {
    var built = Result(RunOutcome.Completed, 0) with { Collected = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["out/Driver.dll"] = new byte[] { 1, 2 } } };
    var replay = new Replay(built);
    var result = this.Runner(replay).Compile(Input());
    var plan = replay.Plans.Single();

    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Completed));
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(File.ReadAllBytes(Path.Combine(result.Directory, "out", "Driver.dll")), Is.EqualTo(new byte[] { 1, 2 }));
    Assert.That(plan.Files.Keys, Is.EquivalentTo(new[] { "Add.cs", "Driver.csproj", "Driver.cs", "NuGet.config" }));
    Assert.That(Encoding.UTF8.GetString(plan.Files["NuGet.config"].Span), Does.Contain("<clear />").And.Not.Contain("<add"));
    Assert.That(plan.Build.Single().ToString(), Does.StartWith("dotnet-sdk build Driver.csproj -c Release -o out"));
    Assert.That(plan.Collect, Is.EqualTo(new[] { "out" }));
  }

  [Test]
  public void ACheckedProfile_BuildsWithOverflowChecks() {
    var replay = new Replay(Result(RunOutcome.Completed, 0));
    _ = this.Runner(replay).Compile(Input(profile: "csharp/net10-checked"));

    Assert.That(Encoding.UTF8.GetString(replay.Plans.Single().Files["Driver.csproj"].Span), Does.Contain("<CheckForOverflowUnderflow>true</CheckForOverflowUnderflow>"));
  }

  [TestCase("csharp/net48", "the profile 'csharp/net48' is not one of csharp/net10, csharp/net10-checked")]
  [TestCase("csharp/net10", "the export 'T.G' does not exist")]
  public void AnUnknownProfileOrExport_IsAProtocolErrorWithoutABuild(string profile, string reason) {
    var replay = new Replay(Result(RunOutcome.Completed, 0));
    var result = this.Runner(replay).Compile(Input(profile: profile, export: profile == "csharp/net10" ? "T.G" : "T.F"));

    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.BuildFailed));
    Assert.That(result.Diagnostics.Single().Code, Is.EqualTo("PLY1095"));
    Assert.That(result.Diagnostics.Single().Message, Does.StartWith(reason));
    Assert.That(result.Build.Diagnostics, Is.EqualTo(result.Diagnostics));
    Assert.That(replay.Plans, Is.Empty);
  }

  [Test]
  public void ASignatureThatDoesNotFit_IsAProtocolErrorWithoutABuild() {
    var signature = new CallSignature([new CallParameter("a", ImageType.U32, ParameterMode.In), new CallParameter("b", ImageType.I32, ParameterMode.In)], ImageType.I32);
    var result = this.Runner(new Replay(Result(RunOutcome.Completed, 0))).Compile(Input(signature: signature));

    Assert.That(result.Diagnostics.Single().Message, Is.EqualTo("the parameter 'a' is u32 in the signature, i32 in the declaration"));
  }

  [Test]
  public void ARejectedSource_IsPLY0030WithEveryCompilerError() {
    const string Output = "  Determining projects to restore...\n"
      + "/tmp/run/Add.cs(1,49): error CS0029: Cannot implicitly convert type 'string' to 'int' [/tmp/run/Driver.csproj]\n"
      + "/tmp/run/Add.cs(1,49): error CS0029: Cannot implicitly convert type 'string' to 'int' [/tmp/run/Driver.csproj]\n"
      + "Add.cs(2,3): warning CS0168: The variable 'x' is declared but never used\n"
      + "Add.cs(3,5): error CS0168: The variable 'unused' is declared but never used\n";
    var result = this.Runner(new Replay(Result(RunOutcome.BuildFailed, 1, diagnostics: BuildFailed(Output)))).Compile(Input());

    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0030", "PLY1091" }));
    Assert.That(result.Diagnostics[1].Message, Is.EqualTo("the C# compiler rejected the input: Add.cs(1,49): CS0029: Cannot implicitly convert type 'string' to 'int'; "
      + "Add.cs(3,5): CS0168: The variable 'unused' is declared but never used"));
  }

  [Test]
  public void ABuildThatFailsWithoutACompilerError_CarriesWhatTheBuildPrinted() {
    var result = this.Runner(new Replay(Result(RunOutcome.BuildFailed, 1, "", "", BuildFailed("error NU1100: Unable to resolve 'X'")))).Compile(Input());

    Assert.That(result.Diagnostics[1].Message, Does.Contain("NU1100"));
  }

  [Test]
  public void ErrorsOnlyInTheDriver_AreAProtocolError() {
    const string Output = "Driver.cs(40,5): error CS0191: A readonly field cannot be assigned to [/tmp/run/Driver.csproj]\n";
    var result = this.Runner(new Replay(Result(RunOutcome.BuildFailed, 1, diagnostics: BuildFailed(Output)))).Compile(Input());

    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0030", "PLY1095" }));
    Assert.That(result.Diagnostics[1].Message, Is.EqualTo("the signature does not fit the declaration: Driver.cs(40,5): CS0191: A readonly field cannot be assigned to"));
  }

  [TestCase(RunOutcome.TimedOut)]
  [TestCase(RunOutcome.Refused)]
  public void ABuildThatDidNotEnd_KeepsTheRunnerDiagnostics(RunOutcome outcome) {
    var limit = Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 1000);
    var result = this.Runner(new Replay(Result(outcome, -1, diagnostics: limit))).Compile(Input());

    Assert.That((result.Outcome, result.Diagnostics.Single().Code), Is.EqualTo((outcome, "PLY0031")));
  }

  private string BuiltDirectory() {
    var directory = Path.Combine(this._workspace!.FullName, "built");
    Directory.CreateDirectory(Path.Combine(directory, "out", "runtimes"));
    File.WriteAllBytes(Path.Combine(directory, "out", "Driver.dll"), [7]);
    File.WriteAllBytes(Path.Combine(directory, "out", "runtimes", "native.so"), [8]);
    return directory;
  }

  [Test]
  public void ADriverRun_PassesTheExportAndTheImagesAndReadsTheAnswers() {
    var replay = new Replay(Result(RunOutcome.Completed, 0, "05000000\n!overflow OverflowException\n"));
    var outcomes = this.Runner(replay).ExecuteFunction(this.BuiltDirectory(), "T.F", _add, [[2, 0, 0, 0, 3, 0, 0, 0], [255, 255, 255, 127, 1, 0, 0, 0]]);
    var plan = replay.Plans.Single();

    Assert.That(outcomes[0].Image, Is.EqualTo(new byte[] { 5, 0, 0, 0 }));
    Assert.That((outcomes[1].Trap!.Kind, outcomes[1].NativeTrapName), Is.EqualTo((TrapKind.Overflow, "OverflowException")));
    Assert.That(plan.Execute.ToString(), Is.EqualTo("dotnet-sdk out/Driver.dll T.F"));
    Assert.That(Encoding.UTF8.GetString(plan.Execute.Stdin.Span), Is.EqualTo("0200000003000000\nffffff7f01000000\n"));
    Assert.That(plan.Files.Keys, Is.EquivalentTo(new[] { "out/Driver.dll", "out/runtimes/native.so" }));
  }

  [TestCase(2, "PLY1095", "call 1: the function under test wrote to standard output")]
  [TestCase(134, "PLY1092", "the C# driver crashed with exit code 134: Process terminated. gone")]
  public void ADriverThatEndsWithAnExitCode_IsAProtocolErrorOrACrash(int exitCode, string code, string message) {
    var stderr = exitCode == 2 ? "call 1: the function under test wrote to standard output\n" : "Process terminated. gone\n";
    var replay = new Replay(Result(RunOutcome.Completed, exitCode, "", stderr));
    var error = Assert.Throws<LanguageRunnerException>(() => this.Runner(replay).ExecuteFunction(this.BuiltDirectory(), "T.F", _add, [new byte[8]]));

    Assert.That(error!.Diagnostics.Single().Code, Is.EqualTo(code));
    Assert.That(error.Diagnostics.Single().Message, Is.EqualTo(message));
  }

  [Test]
  public void ADriverThatTimesOut_KeepsTheTimeLimitDiagnostic() {
    var replay = new Replay(Result(RunOutcome.TimedOut, -1, diagnostics: Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 5000)));
    var error = Assert.Throws<LanguageRunnerException>(() => this.Runner(replay).ExecuteFunction(this.BuiltDirectory(), "T.F", _add, [new byte[8]]));

    Assert.That(error!.Diagnostics.Single().Code, Is.EqualTo("PLY0031"));
  }

  [Test]
  public void Execute_RunsTheBuiltAssemblyWithTheArguments() {
    var replay = new Replay(Result(RunOutcome.Completed, 0, "x"));
    var compiled = new CompileResult(RunOutcome.Completed, this.BuiltDirectory(), [], Result(RunOutcome.Completed, 0));
    var result = this.Runner(replay).Execute(compiled, ["T.F"]);

    Assert.That(Encoding.UTF8.GetString(result.Stdout.Span), Is.EqualTo("x"));
    Assert.That(replay.Plans.Single().Execute.ToString(), Is.EqualTo("dotnet-sdk out/Driver.dll T.F"));
  }

  [Test]
  public void ADirectoryWithoutOutput_RunsWithoutFiles() {
    var replay = new Replay(Result(RunOutcome.Completed, 0));
    _ = this.Runner(replay).Execute(new CompileResult(RunOutcome.BuildFailed, Path.Combine(this._workspace!.FullName, "none"), [], Result(RunOutcome.BuildFailed, 1)), []);

    Assert.That(replay.Plans.Single().Files, Is.Empty);
  }

  private const string _trx = "\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
    + "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>"
    + "<UnitTestResult testName=\"AddsTwoNumbers\" outcome=\"Passed\" />"
    + "<UnitTestResult testName=\"FailsOnPurpose\" outcome=\"Failed\"><Output><ErrorInfo><Message>  this test fails on purpose\n</Message></ErrorInfo></Output></UnitTestResult>"
    + "<UnitTestResult testName=\"Quiet\" outcome=\"Failed\" />"
    + "<UnitTestResult testName=\"Later\" outcome=\"NotExecuted\" />"
    + "<UnitTestResult outcome=\"Passed\" />"
    + "<UnitTestResult outcome=\"Failed\" />"
    + "<UnitTestResult testName=\"Unknown\" />"
    + "</Results></TestRun>";

  [Test]
  public void ATrxFile_GivesTheCountsAndTheFailures() {
    var report = CSharpRunner.ReadTrx(_trx);

    Assert.That((report.Total, report.Passed, report.Failed, report.Skipped), Is.EqualTo((7, 2, 3, 2)));
    Assert.That(report.Failures, Is.EqualTo(new[] { new TestFailure("FailsOnPurpose", "this test fails on purpose"), new TestFailure("Quiet", ""), new TestFailure("", "") }));
  }

  [TestCase("not xml", "no XML")]
  [TestCase("<Other />", "the root is not TestRun")]
  public void TextThatIsNoTrxFile_IsRefused(string text, string reason)
    => Assert.That(Assert.Throws<FormatException>(() => CSharpRunner.ReadTrx(text))!.Message, Does.Contain(reason));

  private string Project() {
    var project = Path.Combine(this._workspace!.FullName, "project");
    Directory.CreateDirectory(Path.Combine(project, "bin"));
    Directory.CreateDirectory(Path.Combine(project, "obj"));
    Directory.CreateDirectory(Path.Combine(project, "src"));
    File.WriteAllText(Path.Combine(project, "Tests.csproj"),
      "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Root\" Version=\"1.0.0\" /><PackageReference Include=\"Absent\" Version=\"9.9.9\" /></ItemGroup></Project>");
    File.WriteAllText(Path.Combine(project, "src", "Tests.cs"), "class X { }");
    File.WriteAllText(Path.Combine(project, "bin", "stale.dll"), "");
    File.WriteAllText(Path.Combine(project, "obj", "stale.json"), "");
    return project;
  }

  private string Packages() {
    var packages = Path.Combine(this._workspace!.FullName, "packages");
    void Package(string id, string version, string dependencies) {
      var folder = Path.Combine(packages, id, version);
      Directory.CreateDirectory(folder);
      File.WriteAllBytes(Path.Combine(folder, $"{id}.{version}.nupkg"), Encoding.UTF8.GetBytes(id));
      if (dependencies.Length > 0)
        File.WriteAllText(Path.Combine(folder, id + ".nuspec"), $"<package><metadata><dependencies>{dependencies}</dependencies></metadata></package>");
    }

    Package("root", "1.0.0", "<group targetFramework=\"net8.0\"><dependency id=\"Core\" version=\"[2.0.0]\" /><dependency id=\"Root\" version=\"1.0.0\" /></group>"
      + "<group targetFramework=\".NETFramework4.6.2\"><dependency id=\"Framework\" version=\"1.0.0\" /></group>"
      + "<group targetFramework=\"net462\"><dependency id=\"Legacy\" version=\"1.0.0\" /></group>"
      + "<dependency id=\"Plain\" version=\"[3.0.0, )\" />");
    Package("core", "2.0.0", "<group><dependency id=\"Missing\" version=\"1.0.0\" /><dependency /></group>");
    Package("plain", "3.0.0", "");
    Package("framework", "1.0.0", "");
    Package("legacy", "1.0.0", "");
    return packages;
  }

  [Test]
  public void TheOfflinePackages_AreTheClosureOfTheReferencesOutsideTheNetFramework() {
    var files = CSharpRunner.OfflinePackages(this.Packages(), [("Root", "1.0.0"), ("Absent", "9.9.9")]);

    Assert.That(files.Keys, Is.EqualTo(new[] { "core.2.0.0.nupkg", "plain.3.0.0.nupkg", "root.1.0.0.nupkg" }));
  }

  [Test]
  public void ThePackageReferences_AreReadWithTheirVersions() {
    var references = CSharpRunner.PackageReferences("<Project><ItemGroup><PackageReference Include=\"A\" Version=\"1.0\" />"
      + "<PackageReference Include=\"B\"><Version>2.0</Version></PackageReference><PackageReference Include=\"C\" /><PackageReference Version=\"3\" /></ItemGroup></Project>");

    Assert.That(references, Is.EqualTo(new[] { ("A", "1.0"), ("B", "2.0") }));
    Assert.Throws<FormatException>(() => CSharpRunner.PackageReferences("<Project>"));
  }

  [Test]
  public void ATestRun_CopiesTheProjectWithItsPackagesAndReadsTheResults() {
    var trx = Result(RunOutcome.Completed, 1) with { Collected = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["results/results.trx"] = Encoding.UTF8.GetBytes(_trx) } };
    var replay = new Replay(trx);
    var report = this.Runner(replay, this.Packages()).RunTests(this.Project());
    var plan = replay.Plans.Single();

    Assert.That(report.Total, Is.EqualTo(7));
    Assert.That(plan.Files.Keys, Is.EquivalentTo(new[] { "Tests.csproj", "src/Tests.cs", "NuGet.config", "feed/root.1.0.0.nupkg", "feed/core.2.0.0.nupkg", "feed/plain.3.0.0.nupkg" }));
    Assert.That(Encoding.UTF8.GetString(plan.Files["NuGet.config"].Span), Does.Contain("<add key=\"run\" value=\"feed\" />").And.Contain("globalPackagesFolder"));
    Assert.That(plan.Execute.ToString(), Does.StartWith("dotnet-sdk test -c Release"));
    Assert.That(plan.Collect, Is.EqualTo(new[] { "results" }));
  }

  [Test]
  public void ATestRunWithoutResults_IsARejectionOrACrash() {
    var rejected = new Replay(Result(RunOutcome.Completed, 1, "Tests.cs(1,1): error CS1002: ; expected [Tests.csproj]\n"));
    var crashed = new Replay(Result(RunOutcome.Completed, 134, "out", "host died"));

    var rejection = Assert.Throws<LanguageRunnerException>(() => this.Runner(rejected, this.Packages()).RunTests(this.Project()));
    var crash = Assert.Throws<LanguageRunnerException>(() => this.Runner(crashed, this.Packages()).RunTests(this.Project()));

    Assert.That(rejection!.Diagnostics.Single().Code, Is.EqualTo("PLY1091"));
    Assert.That(crash!.Diagnostics.Single().Message, Is.EqualTo("the C# driver crashed with exit code 134: host died\nout"));
  }

  [Test]
  public void ATestRunThatDidNotEnd_IsAFailure() {
    var replay = new Replay(Result(RunOutcome.TimedOut, -1, diagnostics: Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 1000)));
    var error = Assert.Throws<LanguageRunnerException>(() => this.Runner(replay, this.Packages()).RunTests(this.Project()));

    Assert.That(error!.Diagnostics.Single().Code, Is.EqualTo("PLY0031"));
  }

  [Test]
  public void TheDefaultPackagesFolder_IsNuGetPackagesOrTheUserProfile() {
    var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
    try {
      Environment.SetEnvironmentVariable("NUGET_PACKAGES", this.Packages());
      var withVariable = new Replay(Result(RunOutcome.TimedOut, -1));
      Assert.Throws<LanguageRunnerException>(() => new CSharpRunner(withVariable, this._workspace!.FullName).RunTests(this.Project()));
      Assert.That(withVariable.Plans.Single().Files.Keys, Does.Contain("feed/root.1.0.0.nupkg"));

      Environment.SetEnvironmentVariable("NUGET_PACKAGES", null);
      var withoutVariable = new Replay(Result(RunOutcome.TimedOut, -1));
      Assert.Throws<LanguageRunnerException>(() => new CSharpRunner(withoutVariable, this._workspace!.FullName).RunTests(this.Project()));
      Assert.That(withoutVariable.Plans.Single().Files.Keys, Does.Not.Contain("feed/root.1.0.0.nupkg"));
    } finally {
      Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
    }
  }

  private static VectorFile Vectors(string expectation) => VectorFile.Parse($$$"""
    {
      "schema":1, "fixture":"runner-csharp/Add",
      "provenance":{"kind":"published","source":"fixture","detail":"by hand","generatedBy":"hand"},
      "export":"T.F", "signature":{"parameters":[{"name":"a","type":"i32"},{"name":"b","type":"i32"}],"result":"i32"},
      "vectors":[{"name":"good","input":{"hex":"0200000003000000"},"expected":{"hex":"05000000"}}, {"name":"bad","input":{"hex":"0200000003000000"}{{{expectation}}}}]
    }
    """, "Add.vectors.json");

  [TestCase(""","expected":{"hex":"06000000"}""", "05000000\n05000000\n", "call 1: expected 6, actual 5")]
  [TestCase(""","expectedTrap":"overflow" """, "05000000\n05000000\n", "call 1: expected !overflow, actual 5")]
  [TestCase(""","expected":{"hex":"05000000"}""", "05000000\n!divide-by-zero DivideByZeroException\n", "call 1: expected 5, actual !divide-by-zero")]
  public void AFailedVector_IsExplainedWithTheValuesOfTheCall(string expectation, string answers, string message) {
    var vectors = Vectors(expectation);
    var built = Result(RunOutcome.Completed, 0) with { Collected = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["out/Driver.dll"] = new byte[] { 1 } } };
    var runner = this.Runner(new Replay(built, Result(RunOutcome.Completed, 0, answers)));
    var report = runner.RunVectors(Input(), vectors);
    var diagnostics = CSharpRunner.Explain(report, vectors);

    Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0051", "PLY1094" }));
    Assert.That(diagnostics[0].Message, Does.Contain("'bad'"));
    Assert.That(diagnostics[1].Message, Is.EqualTo(message));
  }

  [Test]
  public void AVectorFileWithoutSignature_CannotBeExplained() {
    var vectors = VectorFile.Parse("""
      {"schema":1, "fixture":"f", "provenance":{"kind":"published","source":"s","detail":"d","generatedBy":"hand"},
       "vectors":[{"name":"v","input":{"hex":"01"},"expected":{"hex":"01"}}]}
      """, "v.vectors.json");

    Assert.Throws<ImageException>(() => CSharpRunner.Explain(new ComparisonReport(1, 1, []), vectors));
  }

  [Test]
  public void AReportWhereEveryVectorPassed_NeedsNoExplanation() {
    var vectors = Vectors(""","expected":{"hex":"05000000"}""");
    var report = new ComparisonReport(2, 2, [new VectorOutcome("good", true, null, "05000000", "05000000", null), new VectorOutcome("bad", true, null, "05000000", "05000000", null)]);

    Assert.That(CSharpRunner.Explain(report, vectors), Is.Empty);
  }

  [TestCase("0500")]
  [TestCase("0500000001")]
  public void AnImageThatDoesNotFitTheSignature_IsExplainedInHexadecimal(string actual) {
    var vectors = Vectors(""","expected":{"hex":"06000000"}""");
    var report = new ComparisonReport(2, 1, [new VectorOutcome("good", true, null, "05000000", "05000000", null), new VectorOutcome("bad", false, 0, "06000000", actual, null)]);

    Assert.That(CSharpRunner.Explain(report, vectors)[1].Message, Is.EqualTo($"call 1: expected 6, actual {actual}"));
  }

  [Test]
  public void OutputsWithoutAResult_AreExplainedByName_AndAnEmptyImageAsNothing() {
    var vectors = VectorFile.Parse("""
      {"schema":1, "fixture":"f", "provenance":{"kind":"published","source":"s","detail":"d","generatedBy":"hand"},
       "export":"T.V", "signature":{"parameters":[{"name":"a","type":"i32","mode":"inout"},{"name":"b","type":"i32","mode":"out"}],"result":"void"},
       "vectors":[{"name":"v","input":{"hex":"01000000"},"expected":{"hex":"0300000004000000"}}, {"name":"w","input":{"hex":"01000000"}}]}
      """, "v.vectors.json");
    var report = new ComparisonReport(2, 0, [new VectorOutcome("v", false, 0, "0300000004000000", "0300000005000000", null), new VectorOutcome("w", false, 0, "", "", null)]);
    var diagnostics = CSharpRunner.Explain(report, vectors);

    Assert.That(diagnostics[1].Message, Is.EqualTo("call 0: expected a=3, b=4, actual a=3, b=5"));
    Assert.That(diagnostics[3].Message, Is.EqualTo("call 1: expected nothing, actual "));
  }

  [Test]
  public void AVoidSignatureWithoutOutputs_IsExplainedAsNothing() {
    var vectors = VectorFile.Parse("""
      {"schema":1, "fixture":"f", "provenance":{"kind":"published","source":"s","detail":"d","generatedBy":"hand"},
       "export":"T.V", "signature":{"parameters":[{"name":"a","type":"i32"}],"result":"void"},
       "vectors":[{"name":"v","input":{"hex":"01000000"},"expected":{"hex":""}}]}
      """, "v.vectors.json");
    var report = new ComparisonReport(1, 0, [new VectorOutcome("v", false, 0, "", "", null)]);

    Assert.That(CSharpRunner.Explain(report, vectors)[1].Message, Is.EqualTo("call 0: expected nothing, actual nothing"));
  }

  [Test]
  public void CompilerErrors_AreReadWithTheirLocation() {
    var errors = CSharpRunner.CompilerErrors("C:\\run\\A.cs(10,2): error CS1002: ; expected [C:\\run\\Driver.csproj]\r\nA.cs(1,1): error CS0001: plain\nnot an error\n");

    Assert.That(errors.Select(error => error.ToString()), Is.EqualTo(new[] { "A.cs(10,2): CS1002: ; expected", "A.cs(1,1): CS0001: plain" }));
  }

  [Test]
  public void InvalidConstruction_IsRefused() {
    Assert.Throws<ArgumentNullException>(() => _ = new CSharpRunner(null!, "w"));
    Assert.Throws<ArgumentException>(() => _ = new CSharpRunner(new Replay(), " "));
    var runner = this.Runner(new Replay(Result(RunOutcome.Completed, 0)));
    Assert.Throws<ArgumentNullException>(() => runner.Compile(null!));
    Assert.Throws<ArgumentNullException>(() => runner.Execute(null!, []));
    Assert.Throws<ArgumentException>(() => runner.RunTests(""));
    Assert.Throws<ArgumentNullException>(() => CSharpRunner.ReadTrx(null!));
    Assert.Throws<ArgumentNullException>(() => CSharpRunner.CompilerErrors(null!));
    Assert.Throws<ArgumentNullException>(() => CSharpRunner.Explain(null!, Vectors("")));
  }

}
