using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tests.Tooling.Licensing;

/// <summary>
/// <c>polypiler-tooling licenses</c> in process, in a scratch repository holding a copy of the shipped
/// licensing folder: both verbs, the report's default place and a named one, a named policy, the
/// corpus of the repository, and every command line that is refused.
/// </summary>
[TestFixture]
public sealed class LicensesCommandTests {

  private string _root = "";

  [SetUp]
  public void CreateRoot() {
    this._root = Scratch.Create("polypiler-licenses-command-");
    var shipped = Path.Combine(PrototypeFixture.RepositoryRoot, "tooling", "licensing");
    foreach (var file in Directory.EnumerateFiles(shipped, "*", SearchOption.AllDirectories))
      Scratch.Write(this._root, Path.Combine("tooling", "licensing", Path.GetRelativePath(shipped, file)), File.ReadAllBytes(file));
  }

  [TearDown]
  public void DeleteRoot() => Scratch.Delete(this._root);

  private (int ExitCode, string Output, string Error) Run(params string[] args) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var nested = Directory.CreateDirectory(Path.Combine(this._root, "some", "folder")).FullName;
    var exitCode = new LicensesCommand(nested).Run(args, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  private static string Fixture(string tree) => Path.Combine(LicenceScannerTests.Fixtures, tree);

  [Test]
  public void ScanOfATree_WritesTheReportBelowTheRepositoryRoot() {
    var (exitCode, output, error) = this.Run("scan", "--mode", "distribution", "--root", Fixture("gpl-nested"));
    var report = Path.Combine(this._root, "artifacts", "licenses", "report.json");
    Assert.That((exitCode, error), Is.EqualTo((0, "")));
    Assert.That(output, Is.EqualTo($"6 files scanned in distribution mode, 0 violations, report written to {report}{Environment.NewLine}"));
    Assert.That(File.ReadAllText(report), Does.Contain("\"path\": \"sub/x.c\",\n    \"licence\": \"GPL-3.0-only\""));
  }

  [Test]
  public void Violations_GoToStandardError_WithTheLicenceBlockerExitCode_AndTheNamedReport() {
    var (exitCode, output, error) = this.Run("scan", "--mode", "corpus", "--root", Fixture("no-licence"), "--report", "../out/r.json");
    Assert.That(exitCode, Is.EqualTo(8));
    Assert.That(error, Is.EqualTo($"polypiler: error PLY0029: 'orphan.py': no licence could be determined{Environment.NewLine}"));
    Assert.That(output, Does.StartWith("1 files scanned in corpus mode, 1 violations"));
    Assert.That(File.Exists(Path.Combine(this._root, "some", "out", "r.json")), Is.True);
  }

  [Test]
  public void CorpusScan_ReadsTheManifestAndLockOfTheRepository() {
    var commit = new string('a', 40);
    var licence = File.ReadAllBytes(Path.Combine(PrototypeFixture.RepositoryRoot, "LICENSE"));
    Scratch.Write(this._root, CorpusManifest.RepositoryPath, $"schema: 1\nrepositories:\n  - {{ name: o/r, url: 'https://example.invalid/r.git', commit: {commit}, licence: LGPL-3.0-or-later, licenceFiles: [LICENSE] }}\nfixtures: []\n");
    Scratch.Write(this._root, CorpusLock.RepositoryPath, new CorpusLock([new("o/r", commit, "LICENSE", CorpusLockEntry.Hash(licence), licence.Length)]).ToJson());
    Scratch.Write(this._root, $"corpus/upstream/o/r@{commit}/LICENSE", licence);
    var (exitCode, output, error) = this.Run("scan", "--mode", "corpus");
    Assert.That((exitCode, error), Is.EqualTo((0, "")));
    Assert.That(output, Does.StartWith("1 files scanned in corpus mode, 0 violations"));
    Assert.That(File.ReadAllText(Path.Combine(this._root, "artifacts", "licenses", "report.json")), Does.Contain($"\"path\": \"o/r@{commit}/LICENSE\""));
  }

  [Test]
  public void CorpusScanWithoutManifest_IsPly0026() {
    var (exitCode, _, error) = this.Run("scan", "--mode", "corpus");
    Assert.That((exitCode, error.Contains("PLY0026", StringComparison.Ordinal)), Is.EqualTo((1, true)), error);
  }

  [Test]
  public void MalformedPolicy_IsPly0026WithExitCode1() {
    var policy = Scratch.Write(this._root, "bad/policy.yaml", "schema: 1\nallowed: 3\nreferenceOnly: []\nallowedExceptions: []\nrestrictedAssets: []\noverrides: []\npendingDecisions: []\n");
    var (exitCode, _, error) = this.Run("scan", "--mode", "corpus", "--root", Fixture("dual"), "--policy", policy);
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(error, Does.Contain("error PLY0026: corpus manifest invalid: licence policy: 'allowed' must be a list of non-empty strings"));
  }

  [Test]
  public void UnwritableReport_IsPly0025() {
    Scratch.Write(this._root, "blocker", "");
    var (exitCode, _, error) = this.Run("scan", "--mode", "corpus", "--root", Fixture("dual"), "--report", Path.Combine(this._root, "blocker", "r.json"));
    Assert.That(exitCode, Is.EqualTo(1));
    Assert.That(error, Does.Contain("error PLY0025: licence report '"));
  }

  [Test]
  public void Inventory_PassesForAnEmptyRepository_AndReportsWhatIsUnlisted() {
    Scratch.Write(this._root, ThirdPartyInventory.RepositoryPath, "schema: 1\nnotices: []\npackages: []\n");
    Assert.That(this.Run("inventory"), Is.EqualTo((0, $"0 notices and 0 packages checked{Environment.NewLine}", "")));
    Scratch.Write(this._root, "THIRD-PARTY-NOTICE.x.txt", "");
    var (exitCode, _, error) = this.Run("inventory");
    Assert.That(exitCode, Is.EqualTo(8));
    Assert.That(error, Does.Contain("PLY0029: 'THIRD-PARTY-NOTICE.x.txt': no licence could be determined"));
  }

  [Test]
  public void PolicyIsFoundAboveTheCurrentFolder_OrNotAtAll() {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var outside = Scratch.Create("polypiler-licenses-outside-");
    try {
      Assert.That(new LicensesCommand(outside).Run(["scan", "--mode", "corpus", "--root", Fixture("dual")], output, error), Is.EqualTo(1));
      Assert.That(error.ToString(), Does.Contain("licence policy: cannot read"));
    } finally {
      Scratch.Delete(outside);
    }
  }

  [TestCase(new string[0], "licenses needs 'scan' or 'inventory'")]
  [TestCase(new[] { "check" }, "unknown licenses command 'check'")]
  [TestCase(new[] { "scan" }, "licenses scan needs '--mode corpus' or '--mode distribution'")]
  [TestCase(new[] { "scan", "--mode", "both" }, "unknown mode 'both', expected corpus or distribution")]
  [TestCase(new[] { "scan", "--mode", "distribution" }, "licenses scan --mode distribution needs '--root'")]
  [TestCase(new[] { "scan", "--mode" }, "option '--mode' requires a value")]
  [TestCase(new[] { "scan", "--mode", "corpus", "--mode", "corpus" }, "option '--mode' is given more than once")]
  [TestCase(new[] { "scan", "--fixture", "x" }, "unknown option '--fixture'")]
  [TestCase(new[] { "inventory", "--root", "x" }, "unknown option '--root'")]
  [TestCase(new[] { "scan", "--mode", "corpus", "--root", "no-such-folder" }, "scan root 'no-such-folder' is not a folder")]
  public void RefusedCommandLine_IsAUsageError(string[] args, string message)
    => Assert.That(this.Run(args), Is.EqualTo((2, "", $"polypiler-tooling: error PLY0003: {message}{Environment.NewLine}")));

  [Test]
  public void Program_RoutesTheVerb_AndListsItInTheUsage() {
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(Program.Run(["licenses"], output, error), Is.EqualTo(2));
    Assert.That(error.ToString(), Does.Contain("licenses needs 'scan' or 'inventory'"));
    Assert.That(Program.Run([], output, error), Is.Zero);
    Assert.That(output.ToString(), Does.Contain("licenses scan --mode corpus|distribution"));
  }

}
