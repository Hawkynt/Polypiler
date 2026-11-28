using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Verification.Running.Support;

namespace Polypiler.Tests.Acceptance.Runners;

/// <summary>The language-independent runner contract, met by the C# runner on C# fixtures of <c>add</c> and <c>div</c>.</summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), Layer("core")]
[RequiresToolchain(CSharpRunner.Toolchain)]
public sealed class CSharpRunnerConformanceTests : LanguageRunnerConformance {

  private const string _arithmetic = """
    public static class Conformance {
      public static uint add(uint a, uint b) => a + b;
      public static int div(int a, int b) => a / b;
    }
    """;

  private DirectoryInfo? _workspace;

  [SetUp]
  public void CreateWorkspace() => this._workspace = Directory.CreateTempSubdirectory("polypiler-csharp-conformance-");

  [TearDown]
  public void DeleteWorkspace() => this._workspace?.Delete(true);

  protected override (ILanguageRunner Runner, RunnerInput Input) Fixture(VectorFile vectors) => this.Fixture(vectors, _arithmetic, null);

  protected override (ILanguageRunner Runner, RunnerInput Input, string RejectionCode) BrokenFixture(VectorFile vectors) {
    var (runner, input) = this.Fixture(vectors, "public static class Conformance { public static uint add(uint a, uint b) => a + ; }", null);
    return (runner, input, CSharpRunnerDiagnostics.Rejected.Code);
  }

  protected override (ILanguageRunner Runner, RunnerInput Input) EndlessFixture(VectorFile vectors)
    => this.Fixture(vectors, "public static class Conformance { public static uint add(uint a, uint b) { while (true) { } } }", RunnerTesting.Limits(wallTime: TimeSpan.FromSeconds(2)));

  private (ILanguageRunner Runner, RunnerInput Input) Fixture(VectorFile vectors, string source, RunLimits? driverLimits) {
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["Conformance.cs"] = Encoding.UTF8.GetBytes(source) };
    return (CSharpRunnerTests.Runner(this._workspace!.FullName, driverLimits), new(files, vectors.Export!, vectors.Signature!, CSharpProfile.Net10.Name, TrustLevel.Trusted));
  }

}
