using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Runners;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Infra.Ci;
using Polypiler.Tooling.Cipher;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The Rust target of <c>polypiler-tooling cipher kat</c> (#1365) with the real cargo. A stand-in
/// translation of <c>tests/fixtures/infra/cipher-mini</c>, the Rust form the harness reaches a
/// translation through, gets its test crate: every vector passes; an altered byte, a panic of the
/// translation, a property it lacks and an algorithm it does not register fail with their reason; a
/// translation that does not compile names the compiler's first error. Then the scenario files of the
/// corpus go through polypiler itself: each is either refused with the backend's own diagnostic, or
/// it passes every checked vector.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd")]
[Layer("rust")]
public sealed class CipherKatRustTests {

  private static readonly string _mini = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "cipher-mini");

  private string _work = "";

  [SetUp]
  public void CreateWork() => this._work = Directory.CreateTempSubdirectory("polypiler-cipher-kat-rust-").FullName;

  [TearDown]
  public void DeleteWork() => Directory.Delete(this._work, true);

  /// <summary>Polypiler for the translation, which writes the stand-in crate, perhaps changed; cargo as it is.</summary>
  private sealed class StandIn(Action<string>? change = null) : IProcessRunner {

    private readonly ProcessRunner _real = new();

    public ProcessResult Run(IReadOnlyList<string> argv, TimeSpan timeout) {
      if (!argv.Contains("translate"))
        return this._real.Run(argv, timeout);

      var output = argv[argv.ToList().IndexOf("--output") + 1];
      foreach (var file in Directory.EnumerateFiles(Path.Combine(_mini, "rust"), "*", SearchOption.AllDirectories)) {
        var target = Path.Combine(output, Path.GetRelativePath(Path.Combine(_mini, "rust"), file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
      }

      change?.Invoke(output);
      return new(0, "", "", false);
    }

  }

  private KatTargetResult Check(string goldens, Action<string>? change = null)
    => new KatHarness(new StandIn(change), ["polypiler"], _mini, Path.Combine(_mini, goldens), this._work).Check("checksum/xorsum.js", [KatTarget.Rust]).Targets[0];

  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public void Translation_PassesEveryKnownAnswerTest() {
    var result = this.Check("goldens");
    Assert.That((result.Stage, result.Reason, result.Passed, result.Failures, result.AllPassed), Is.EqualTo((KatStage.Ran, (string?)null, 3, new List<string>(), true)), string.Join("; ", result.Failures));
  }

  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public void WrongAnswer_APanic_AMissingPropertyAndAMissingAlgorithm_FailLoudly() {
    var result = this.Check("altered");
    Assert.That((result.Stage, result.Passed, result.AllPassed), Is.EqualTo((KatStage.Ran, 0, false)));
    Assert.That(result.Failures, Is.EqualTo(new[] {
      "XorSum-0 #1: first differing byte at 0: expected 0x01, got 0x00 (1 bytes expected, 1 given)",
      "XorSum-0 #2: the translation panicked: boom",
      "XorSum-0 #3: the translation of IAlgorithmInstance has no property 'rounds' to set",
      "XorSum-9 #1: the translation registers no algorithm named 'XorSum-9'"
    }));
  }

  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public void TranslationThatDoesNotCompile_NamesTheFirstError() {
    var result = this.Check("goldens", output => {
      var module = Path.Combine(output, "src", "xorsum.rs");
      File.WriteAllText(module, File.ReadAllText(module).Replace("vec![sum as u8]", "vec![sum as u8, missing]", StringComparison.Ordinal));
    });
    Assert.That((result.Stage, result.Reason), Is.EqualTo((KatStage.BuildFailed, (string?)"E0425: cannot find value `missing` in this scope")));
  }

  /// <summary>
  /// The scenario files of #1365 through polypiler and cargo: the first diagnostic of a refusal is the
  /// Rust backend's own - the target is known - and a file that translates passes every checked vector.
  /// </summary>
  [TestCase("checksum/adler.js")]
  [TestCase("checksum/check-digit.js")]
  [TestCase("checksum/isbn.js")]
  [RequiresToolchain(RustRunner.Toolchain)]
  public void ScenarioFile_IsRefusedByTheBackend_OrPassesEveryVector(string file) {
    var root = PrototypeFixture.RepositoryRoot;
    var manifest = CorpusManifest.Load(Path.Combine(root, CorpusManifest.RepositoryPath));
    var fixture = manifest.Fixture(CipherCommand.Fixture)!;
    var pin = fixture.PinOf(manifest.Repository(fixture.Repository)!);
    var corpus = Path.GetDirectoryName(CorpusImporter.FilePath(root, pin.Name, pin.Commit, fixture.Path))!;
    if (!Directory.Exists(Path.Combine(corpus, "algorithms"))) {
      using var output = new StringWriter();
      using var error = new StringWriter();
      Assert.That(new CorpusCommand(new GitProcess(), root).Run(["import", "--fixture", CipherCommand.Fixture], output, error), Is.EqualTo(0), error.ToString());
    }

    var configuration = new DirectoryInfo(Path.GetDirectoryName(typeof(CipherKatRustTests).Assembly.Location)!).Parent!.Name;
    var polypiler = CipherCommand.Command(Path.Combine(root, "Polypiler.Cli", "bin", configuration, "net10.0", "polypiler.dll"));
    var row = new KatHarness(new ProcessRunner(), polypiler, corpus, Path.Combine(root, CipherCommand.Goldens), this._work).Check(file, [KatTarget.Rust]);
    var result = row.Targets[0];
    TestContext.Out.WriteLine($"{row.File}: {row.Checkable} / {row.Vectors} KATs checkable; rust {KatReport.Status(result)}");
    Assert.That(row.Checkable, Is.GreaterThan(0), "the golden file has vectors a translation can be checked on");
    if (result.Stage == KatStage.Refused)
      Assert.That(result.Reason, Does.StartWith("PLY").And.Not.StartWith("PLY0003"), "the refusal is the backend's own");
    else
      Assert.That((result.Stage, result.AllPassed, result.Failures), Is.EqualTo((KatStage.Ran, true, new List<string>())), KatReport.Status(result));
  }

}
