using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Verification.Running.Support;

namespace Polypiler.Tests.Acceptance.Runners;

[TestFixture, Category("Acceptance"), Category("EndToEnd"), Layer("core")]
public sealed class EchoRunnerTests : LanguageRunnerConformance {

  private DirectoryInfo? _directory;

  [SetUp]
  public void CreateWorkspace() => this._directory = Directory.CreateTempSubdirectory("polypiler-echo-");

  [TearDown]
  public void DeleteWorkspace() => this._directory?.Delete(true);

  protected override (ILanguageRunner Runner, RunnerInput Input) Fixture(VectorFile vectors)
    => this.Fixture(vectors, null, RunLimits.Default);

  protected override (ILanguageRunner Runner, RunnerInput Input, string RejectionCode) BrokenFixture(VectorFile vectors) {
    var (runner, input) = this.Fixture(vectors, "int missing = ;", RunLimits.Default);
    return (runner, input, EchoRunner.RejectionCode);
  }

  protected override (ILanguageRunner Runner, RunnerInput Input) EndlessFixture(VectorFile vectors)
    => this.Fixture(vectors, "while (true) { }", RunnerTesting.Limits(wallTime: TimeSpan.FromSeconds(2)));

  private (ILanguageRunner Runner, RunnerInput Input) Fixture(VectorFile vectors, string? sourceOverride, RunLimits driverLimits) {
    var source = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "runner-lang", "EchoDriver");
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
    foreach (var name in new[] { "EchoDriver.csproj", "Program.cs" })
      files[name] = File.ReadAllBytes(Path.Combine(source, name));

    if (sourceOverride is not null)
      files["Program.cs"] = Encoding.UTF8.GetBytes(sourceOverride);

    return (new EchoRunner(this._directory!.FullName, driverLimits),
      new(files, vectors.Export!, vectors.Signature!, "net10.0", TrustLevel.Trusted));
  }

  private sealed class EchoRunner(string workspace, RunLimits driverLimits) : LanguageRunnerBase {

    public const string RejectionCode = "ECHO0091";
    private const string _assembly = "out/EchoDriver.dll";
    private readonly CrossLanguageRunner _runner = new(RunnerTesting.Host, RunnerTesting.Toolchains);

    public override string Language => "echo";
    public override IReadOnlyList<string> Toolchains => ["dotnet-sdk"];

    public override CompileResult Compile(RunnerInput input) {
      var build = new ProcessSpec("dotnet-sdk", "build", "EchoDriver.csproj", "-c", "Release", "-o", "out", "--nologo", "--disable-build-servers");
      var plan = new RunPlan(input.Files, [build], new("dotnet-sdk", "--version"), RunLimits.Default, input.Trust) { Collect = ["out"] };
      var result = this.Run(plan);
      var directory = Path.Combine(workspace, Guid.NewGuid().ToString("N"));
      if (result.Outcome == RunOutcome.Completed)
        foreach (var (name, bytes) in result.Collected) {
          var destination = Path.Combine(directory, name);
          _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
          File.WriteAllBytes(destination, bytes.Span);
        }

      var diagnostics = result.Outcome == RunOutcome.BuildFailed
        ? [.. result.Diagnostics, new Diagnostic(RejectionCode, DiagnosticSeverity.Error, "the reference compiler rejected the fixture")]
        : result.Diagnostics;
      return new(result.Outcome, directory, diagnostics, result);
    }

    public override RunResult Execute(CompileResult compiled, IReadOnlyList<string> arguments) => this.Run(
      new(ReadOutput(compiled.Directory), [], new("dotnet-sdk", [_assembly, .. arguments]), RunLimits.Default, TrustLevel.Trusted));

    public override TestReport RunTests(string projectDirectory) => throw new NotSupportedException();

    protected override RunResult RunDriver(string directory, string export, CallSignature signature, ReadOnlyMemory<byte> inputs, TrustLevel trust) {
      var files = ReadOutput(directory);
      files["inputs.hex"] = inputs;
      return this.Run(new(files, [], new("dotnet-sdk", _assembly, "inputs.hex", export), driverLimits, trust));
    }

    private RunResult Run(RunPlan plan) => this._runner.RunAsync(plan).GetAwaiter().GetResult();

    private static Dictionary<string, ReadOnlyMemory<byte>> ReadOutput(string directory) {
      var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
      foreach (var file in Directory.EnumerateFiles(Path.Combine(directory, "out")))
        files["out/" + Path.GetFileName(file)] = File.ReadAllBytes(file);

      return files;
    }

  }

}
