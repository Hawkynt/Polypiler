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

/// <summary>The language-independent runner contract, met by the JavaScript runner on node with fixtures of <c>add</c> and <c>div</c>.</summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), Layer("js")]
[RequiresToolchain(NodeRunner.Toolchain)]
public sealed class NodeRunnerTests : LanguageRunnerConformance {

  /// <summary>u32 addition wraps with <c>&gt;&gt;&gt; 0</c>; i32 division truncates through BigInt, whose division by zero is the RangeError the trap table names.</summary>
  private const string _arithmetic = """
    export const add = (a, b) => (a + b) >>> 0;
    export const div = (a, b) => Number(BigInt(a) / BigInt(b));
    """;

  private DirectoryInfo? _workspace;

  [SetUp]
  public void CreateWorkspace() => this._workspace = Directory.CreateTempSubdirectory("polypiler-node-conformance-");

  [TearDown]
  public void DeleteWorkspace() => this._workspace?.Delete(true);

  protected override (ILanguageRunner Runner, RunnerInput Input) Fixture(VectorFile vectors) => this.Fixture(vectors, _arithmetic, null);

  protected override (ILanguageRunner Runner, RunnerInput Input, string RejectionCode) BrokenFixture(VectorFile vectors) {
    var (runner, input) = this.Fixture(vectors, "export const add = (a, b) => a + ;", null);
    return (runner, input, NodeRunnerDiagnostics.Rejected.Code);
  }

  protected override (ILanguageRunner Runner, RunnerInput Input) EndlessFixture(VectorFile vectors)
    => this.Fixture(vectors, File.ReadAllText(Path.Combine(NodeRunnerCaseTests.Fixtures, "spin.mjs")).Replace("function F", "function add", StringComparison.Ordinal),
      RunnerTesting.Limits(wallTime: TimeSpan.FromSeconds(2)));

  private (ILanguageRunner Runner, RunnerInput Input) Fixture(VectorFile vectors, string source, RunLimits? driverLimits) {
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["arithmetic.mjs"] = Encoding.UTF8.GetBytes(source) };
    return (NodeRunnerCaseTests.Runner(this._workspace!.FullName, driverLimits), new(files, vectors.Export!, vectors.Signature!, "node", TrustLevel.Trusted));
  }

}
