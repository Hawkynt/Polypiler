using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Cipher;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Tooling.Cipher;

/// <summary>
/// The harness of <c>cipher kat</c> end to end (#1365), on an algorithm file of the shape of Cipher's
/// small enough for what translates today (<c>tests/fixtures/infra/cipher-mini</c>): polypiler
/// translates it with the framework it requires, dotnet builds and runs the test project, and every
/// vector passes. Test design 4: an altered expected value fails with its first differing byte, a step
/// that throws and a property the translation lacks fail with their reason, and so does an algorithm
/// the translation does not register; none of them counts as passed.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
public sealed class CipherKatEndToEndTests {

  private static readonly string _mini = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "cipher-mini");

  private string _work = "";

  [SetUp]
  public void CreateWork() => this._work = Scratch.Create("polypiler-cipher-kat-e2e-");

  [TearDown]
  public void DeleteWork() => Scratch.Delete(this._work);

  /// <summary>The polypiler of the configuration the tests were built in: the Cli project's own output, which has its runtime configuration.</summary>
  private static IReadOnlyList<string> Polypiler() {
    var configuration = new DirectoryInfo(Path.GetDirectoryName(typeof(CipherKatEndToEndTests).Assembly.Location)!).Parent!.Name;
    return CipherCommand.Command(Path.Combine(PrototypeFixture.RepositoryRoot, "Polypiler.Cli", "bin", configuration, "net10.0", "polypiler.dll"));
  }

  private KatTargetResult Check(string goldens)
    => new KatHarness(new ProcessRunner(), Polypiler(), _mini, Path.Combine(_mini, goldens), this._work).Check("checksum/xorsum.js", [KatTarget.CSharp]).Targets[0];

  [Test]
  public void Translation_PassesEveryKnownAnswerTest() {
    var result = this.Check("goldens");
    Assert.That((result.Stage, result.Reason, result.Passed, result.Failures, result.AllPassed), Is.EqualTo((KatStage.Ran, (string?)null, 3, new List<string>(), true)), string.Join("; ", result.Failures));
  }

  [Test]
  public void WrongAnswer_AStepThatThrows_AMissingPropertyAndAMissingAlgorithm_FailLoudly() {
    var result = this.Check("altered");
    Assert.That((result.Stage, result.Passed, result.AllPassed), Is.EqualTo((KatStage.Ran, 0, false)));
    Assert.That(result.Failures, Is.EqualTo(new[] {
      "XorSum-0 #1: first differing byte at 0: expected 0x01, got 0x00 (1 bytes expected, 1 given)",
      "XorSum-0 #2: Explode threw Exception: boom",
      "XorSum-0 #3: the translation of XorSumInstance has no property 'rounds' to set",
      "XorSum-9 #1: the translation registers no algorithm named 'XorSum-9'"
    }));
  }

}
