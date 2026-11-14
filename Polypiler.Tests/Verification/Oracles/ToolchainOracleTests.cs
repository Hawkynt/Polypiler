using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Running;
using static Polypiler.Tests.Verification.Oracles.Support.OracleTesting;

namespace Polypiler.Tests.Verification.Oracles;

/// <summary>
/// The source-toolchain and emulator oracles and the program runs beneath them. The mechanism is
/// tested against a scripted runner; what node computes is tested against node itself.
/// </summary>
[TestFixture]
public sealed class ToolchainOracleTests {

  private static readonly TestVector[] _two = [new("a", "a"u8.ToArray(), null), new("abc", "abc"u8.ToArray(), null)];

  private static ToolchainProgram Scripted(ScriptedRunner runner, string program = "adler32.js")
    => new(runner, Node, Fixture(program), RunLimits.Default, TrustLevel.Untrusted);

  [Test]
  public async Task EveryVector_RunsOnce_WithItsInputAsHexOnStdin() {
    var runner = new ScriptedRunner(Run(RunOutcome.Completed, 0, "00620062\n"), Run(RunOutcome.Completed, 0, " 024D0127 "));
    var run = await (Scripted(runner) with { Arguments = ["--no-warnings"] }).RunAsync(_two);
    Assert.That((run.Failure, run.Outputs.Select(output => Hex.Format(output))), Is.EqualTo(((string?)null, new[] { "00620062", "024d0127" })));
    Assert.That(runner.Plans.Select(plan => Encoding.ASCII.GetString(plan.Execute.Stdin.Span)), Is.EqualTo(new[] { "61", "616263" }));
    Assert.That(runner.Plans[0].Execute.ToString(), Is.EqualTo("node --no-warnings adler32.js"));
    Assert.That((runner.Plans[0].Files.Keys.Single(), runner.Plans[0].Trust, runner.Plans[0].Build.Count), Is.EqualTo(("adler32.js", TrustLevel.Untrusted, 0)));
  }

  /// <summary>Test design 14 and 16: a reference that is missing, refused, killed, failing or unreadable is PLY0050 and stops the runs.</summary>
  [TestCase("absent.js", RunOutcome.Completed, 0, "00000001", "the program '*absent.js' does not exist", 0)]
  [TestCase("adler32.js", RunOutcome.TimedOut, -1, "", "vector 'a': PLY0031: time limit of 30000 ms exceeded", 1)]
  [TestCase("adler32.js", RunOutcome.Completed, 3, "", "vector 'a': 'node adler32.js' exited with 3: boom", 1)]
  [TestCase("adler32.js", RunOutcome.Completed, 0, "checksum", "vector 'a': the output 'checksum' is not hexadecimal", 1)]
  [TestCase("adler32.js", RunOutcome.Completed, 0, "abc", "vector 'a': the output 'abc' is not hexadecimal", 1)]
  public async Task AFailingReference_IsPLY0050(string program, RunOutcome outcome, int exitCode, string stdout, string cause, int runs) {
    var diagnostics = outcome == RunOutcome.TimedOut ? new[] { Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 30000L) } : [];
    var runner = new ScriptedRunner(Run(outcome, exitCode, stdout, "boom\n", diagnostics));
    var oracle = new ToolchainOracle("node", OracleKind.SourceToolchain, Scripted(runner, program));
    var result = await oracle.EvaluateAsync(new OracleRequest("cipher.adler32", _two));
    var diagnostic = result.Diagnostics.Single();
    Assert.That((diagnostic.Code, diagnostic.ExitCode, result.Outputs.Count, runner.Plans.Count), Is.EqualTo(("PLY0050", (ExitCode?)ExitCode.OracleFailure, 0, runs)));
    const string Prefix = "reference oracle 'node' failed for 'cipher.adler32': ";
    Assert.That(diagnostic.Message, Does.StartWith(Prefix));
    Assert.That(diagnostic.Message[Prefix.Length..], Does.Match("^" + System.Text.RegularExpressions.Regex.Escape(cause).Replace("\\*", ".*") + "$"));
  }

  [Test]
  public async Task ALongStandardError_IsCut() {
    var runner = new ScriptedRunner(Run(RunOutcome.Completed, 1, "", new string('e', 500)));
    var run = await Scripted(runner).RunAsync(_two);
    Assert.That(run.Failure, Does.EndWith(new string('e', 200) + "..."));
  }

  /// <summary>Test design 9: the emulator oracle is the same mechanism with another kind.</summary>
  [Test]
  public async Task TheEmulatorOracle_RunsItsProgramTheSameWay() {
    var runner = new ScriptedRunner(Run(RunOutcome.Completed, 0, "00"));
    var oracle = new ToolchainOracle("vice", OracleKind.Emulator, Scripted(runner) with { Toolchain = "vice", Arguments = ["-console"] });
    Assert.That((oracle.Kind, oracle.Producer, oracle.ReferenceFile), Is.EqualTo((OracleKind.Emulator, "vice", Fixture("adler32.js"))));
    var result = await oracle.EvaluateAsync(new OracleRequest("f", _two));
    Assert.That((result.Succeeded, result.Outputs.Count, runner.Plans[1].Execute.ToString()), Is.EqualTo((true, 2, "vice -console adler32.js")));
  }

  [Test]
  public void ToolchainOracle_RefusesOtherKindsAndNulls() {
    var program = Scripted(new ScriptedRunner());
    Assert.That(() => new ToolchainOracle("x", OracleKind.PublishedVectors, program), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(() => new ToolchainOracle("x", OracleKind.Prover, program), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(() => new ToolchainOracle(null!, OracleKind.Emulator, program), Throws.ArgumentNullException);
    Assert.That(() => new ToolchainOracle("x", OracleKind.Emulator, null!), Throws.ArgumentNullException);
    Assert.That(() => new ToolchainOracle("x", OracleKind.Emulator, program).EvaluateAsync(null!), Throws.ArgumentNullException);
    Assert.That(() => program.RunAsync(null!), Throws.ArgumentNullException);
  }

  /// <summary>Test design 9: node computes the published vectors through the sandboxed runner.</summary>
  [Test, Category("ExternalInterop")]
  public async Task Node_ComputesThePublishedVectors() {
    var file = VectorFile.Load(AdlerVectors);
    var registry = new OracleRegistry();
    Assert.That(registry.Register(file.Fixture, new ToolchainOracle("node", OracleKind.SourceToolchain, NodeProgram(Fixture("adler32.js")))), Is.True);
    var candidate = await NodeProgram(Fixture("adler32.js")).RunAsync(file.Vectors);
    var result = await OracleVerification.VerifyAsync(registry, file.Fixture, file.Vectors, candidate.Outputs, file.Exactness);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That((result.Report.Total, result.Report.Passed, result.Report.Verified), Is.EqualTo((4, 4, true)));
  }

  /// <summary>Test design 15: the wrong-modulus program as the reference disagrees with the published vectors.</summary>
  [Test, Category("ExternalInterop")]
  public async Task TheWrongModulusAsReference_IsPLY0050() {
    var file = VectorFile.Load(AdlerVectors);
    var registry = new OracleRegistry();
    registry.Register(file.Fixture, new ToolchainOracle("node", OracleKind.SourceToolchain, NodeProgram(Fixture("adler32-wrong-modulus.js"))));
    var result = await OracleVerification.VerifyAsync(registry, file.Fixture, file.Vectors, [.. file.Vectors.Select(vector => vector.Expected!)], file.Exactness);
    Assert.That(result.Diagnostics.Single().Message, Does.EndWith("vector '300 x ff': the reference computed aece2ad5, the published value is b90f2ae4"));
    Assert.That((result.ExitCode, result.Report.Passed), Is.EqualTo((ExitCode.OracleFailure, 0)));
  }

  /// <summary>Test design 16: a reference that loops is killed at the runner's time limit and the cause names PLY0031.</summary>
  [Test, Category("ExternalInterop")]
  public async Task ALoopingReference_IsPLY0050_NamingTheTimeLimit() {
    var looping = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, $"loop-{Guid.NewGuid():N}.js");
    await System.IO.File.WriteAllTextAsync(looping, "for (;;) {}\n");
    try {
      var limits = Polypiler.Tests.Verification.Running.Support.RunnerTesting.Limits(TimeSpan.FromMilliseconds(1500));
      var oracle = new ToolchainOracle("node", OracleKind.SourceToolchain, NodeProgram(looping, limits));
      var result = await oracle.EvaluateAsync(new OracleRequest("f", _two));
      Assert.That(result.Diagnostics.Single().Message, Is.EqualTo("reference oracle 'node' failed for 'f': vector 'a': PLY0031: time limit of 1500 ms exceeded"));
    } finally {
      System.IO.File.Delete(looping);
    }
  }

  /// <summary>The seeded faults of the measurements: wrong modulus, truncated output, a 2-ULP error.</summary>
  [Test, Category("ExternalInterop")]
  public async Task TheSeededFaults_AreDetected() {
    var file = VectorFile.Load(AdlerVectors);
    var reference = file.Vectors.Select(vector => vector.Expected!).ToArray();
    var wrong = await NodeProgram(Fixture("adler32-wrong-modulus.js")).RunAsync(file.Vectors);
    var truncated = reference.Select(output => output[..3]).ToArray();
    var faults = new[] {
      DifferentialComparer.Compare(file.Vectors, reference, wrong.Outputs, file.Exactness).Verified,
      DifferentialComparer.Compare(file.Vectors, reference, truncated, file.Exactness).Verified,
      DifferentialComparer.Compare(Named("x"), [F64(0x3FD3333333333333)], [F64(0x3FD3333333333335)], new ExactnessPolicy.Ulp(1, FloatFormat.F64, reason: "r")).Verified
    };
    Assert.That(faults, Is.All.False);
  }

}
