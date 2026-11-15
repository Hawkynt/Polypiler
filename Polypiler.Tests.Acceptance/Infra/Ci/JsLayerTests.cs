using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The rows of the test design of the JS/TS layer that <c>tests/features/infra/infra-ci-js.feature</c>
/// does not hold: the boundaries of the number model, a module that does not parse or throws, which
/// fixtures the type check applies to, and the probe of an absent or mismatching Node.
/// </summary>
[TestFixture]
[Layer("js")]
public sealed class JsLayerTests {

  private static async Task<RunResult> RunAsync(string file, ProcessSpec command) {
    var result = await JsCommands.RunAsync(JsCommands.Fixtures(file), command);
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Completed), string.Join("\n", result.Diagnostics));
    return result;
  }

  private static string Lines(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span).ReplaceLineEndings("\n");

  /// <summary>Test design 6: 2^53 is the largest integer a Number holds exactly; its successor rounds back to it.</summary>
  [Test, Category("EndToEnd"), RequiresToolchain(JsCommands.Node)]
  public async Task LargestExactInteger_AndItsSuccessor_PrintAlike() {
    var result = await RunAsync("coercion.mjs", JsCommands.Run("coercion.mjs", "--limits"));
    Assert.That((result.ExitCode, Lines(result.Stdout)), Is.EqualTo((0, "9007199254740992\n9007199254740992\n")));
  }

  /// <summary>Test design 7: the shift count is masked with 31, so 32 shifts by 0 and 33 by 1.</summary>
  [Test, Category("EndToEnd"), RequiresToolchain(JsCommands.Node)]
  public async Task ShiftCount_AtAndBeyondTheWidth_IsMasked() {
    var result = await RunAsync("coercion.mjs", JsCommands.Run("coercion.mjs", "--shifts"));
    Assert.That((result.ExitCode, Lines(result.Stdout)), Is.EqualTo((0, "-2147483648\n1\n2\n")));
  }

  /// <summary>Test design 11: a module that does not parse.</summary>
  [Test, Category("EndToEnd"), RequiresToolchain(JsCommands.Node)]
  public async Task SyntaxError_ExitsWithOne_AndNamesIt() {
    var result = await RunAsync("syntax-error.mjs", JsCommands.Run("syntax-error.mjs"));
    Assert.That(result.ExitCode, Is.EqualTo(1));
    Assert.That(Encoding.UTF8.GetString(result.Stderr.Span), Does.Contain("SyntaxError"));
    Assert.That(result.Stdout.IsEmpty, Is.True);
  }

  /// <summary>Test design 12: an exception nobody catches completes the run with a non-zero exit and its stack trace.</summary>
  [Test, Category("EndToEnd"), RequiresToolchain(JsCommands.Node)]
  public async Task UncaughtException_CompletesWithExitOne_AndAStackTrace() {
    var result = await RunAsync("throws.mjs", JsCommands.Run("throws.mjs"));
    var stderr = Encoding.UTF8.GetString(result.Stderr.Span);
    Assert.That(result.ExitCode, Is.EqualTo(1));
    Assert.That(stderr, Does.Contain("Error: thrown on purpose"));
    Assert.That(stderr, Does.Match(@"at fail \(.*throws\.mjs:2:\d+\)"));
  }

  /// <summary>Test design 4: a fixture with a <c>tsconfig.json</c> is type-checked, a JavaScript-only one records that the check does not apply.</summary>
  [Test]
  public void TypeCheck_AppliesOnlyToAFixtureWithAProject() {
    var typed = JsCommands.Fixtures("adler32.ts", JsCommands.ProjectFile);
    var plain = JsCommands.Fixtures("coercion.mjs");
    Assert.That(JsCommands.TypeCheckOf(typed), Is.EqualTo(JsCommands.TypeCheck(".")).Using<ProcessSpec>((a, b) => a.ToString() == b.ToString()));
    Assert.That(JsCommands.TypeScriptRecord(typed), Is.EqualTo("typescript: checked"));
    Assert.That(JsCommands.TypeCheckOf(plain), Is.Null);
    Assert.That(JsCommands.TypeScriptRecord(plain), Is.EqualTo("typescript: not applicable"));
  }

  /// <summary>The commands as the layer specifies them, each started without a shell.</summary>
  [Test]
  public void Commands_AreTheDocumentedOnes() {
    Assert.That(JsCommands.Run("a.mjs", "--limits").ToString(), Is.EqualTo("node a.mjs --limits"));
    Assert.That(JsCommands.Test("a.test.mjs").ToString(), Is.EqualTo("node --test a.test.mjs"));
    Assert.That(JsCommands.TypeCheck(".").ToString(), Is.EqualTo("typescript --noEmit -p ."));
    Assert.That(JsCommands.TypeCheckFile("a.ts").ToString(), Is.EqualTo("typescript --noEmit a.ts"));
  }

  /// <summary>
  /// Test design 13: Node absent, or installed at a version other than the pinned one. The fixture
  /// locks pin a version command no host has and a version no Node has, so any installed Node shows both.
  /// </summary>
  [TestCase("node-absent.lock", ToolchainStatus.Missing, "PLY0021", TestName = "An absent Node probes as PLY0021")]
  [TestCase("node-other-version.lock", ToolchainStatus.Mismatch, "PLY0022", TestName = "A Node at another version probes as PLY0022")]
  [Category("EndToEnd")]
  public void Probe_OfAnAbsentOrMismatchingNode_NamesTheProblem(string lockFile, ToolchainStatus status, string code) {
    var path = Path.Combine(JsCommands.FixtureFolder, lockFile);
    var probe = new ToolchainLocator(ToolchainLock.Load(path), new ProcessRunner()).Probe(JsCommands.Node);
    Assert.That((probe.Status, probe.Problem?.Code), Is.EqualTo((status, code)));
  }

}
