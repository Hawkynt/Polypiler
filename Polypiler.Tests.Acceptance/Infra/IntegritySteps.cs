using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.Tooling.Integrity;
using Polypiler.Tooling.Integrity;
using Polypiler.Tooling.Toolchains;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/infra-integrity.feature</c>. The scenario's workspace becomes a
/// <see cref="GoldenTree"/>: the repository's toolchain and corpus locks, the vector files a step puts
/// there, sealed through the gate, and for the change rule a git history; the real
/// <c>polypiler-tooling</c> executable checks it.
/// </summary>
[Binding, Scope(Feature = "Golden vectors are read-only and independently sourced")]
public sealed class IntegritySteps(ScenarioWorkspace workspace, CommandLog log) {

  private GoldenTree? _tree;
  private VectorFile? _file;
  private string _savedLock = "";

  private GoldenTree Tree => this._tree ??= new(workspace.Path);

  /// <summary>Puts the repository's vector file into the workspace with the lock entry the repository records for it.</summary>
  [Given("^the vector file " + StepText.Quoted + "$")]
  public void TheVectorFile(string path) {
    var repositoryLock = GoldenLock.Load(Path.Combine(PrototypeFixture.RepositoryRoot, GoldenLock.RepositoryPath));
    var entry = repositoryLock.Find(path);
    Assert.That(entry, Is.Not.Null, $"{path} is not in the repository's golden lock");
    this.Tree.Write(path, File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, path.Replace('/', Path.DirectorySeparatorChar))));
    this.Tree.Write(GoldenLock.RepositoryPath, new GoldenLock([entry!]).ToJson());
    this._file = VectorFile.Load(this.Tree.Resolve(path), path);
  }

  /// <summary>Checks the provenance of the vector file of the scenario.</summary>
  [Then("^the file's provenance kind is " + StepText.Quoted + " with source " + StepText.Quoted + " and generatedBy " + StepText.Quoted + "$")]
  public void ProvenanceIs(string kind, string source, string generatedBy) {
    var provenance = this._file!.Provenance;
    Assert.That((Provenance.KindName(provenance.Kind), provenance.Source, provenance.GeneratedBy), Is.EqualTo((kind, source, generatedBy)));
  }

  /// <summary>Checks one vector of the vector file of the scenario.</summary>
  [Then("^its vector " + StepText.Quoted + " has input text " + StepText.Quoted + " and expected hex " + StepText.Quoted + "$")]
  public void VectorIs(string name, string input, string hex) {
    var vector = this._file!.Find(name);
    Assert.That(vector, Is.Not.Null);
    Assert.That((vector!.Input.Kind, vector.Input.Text, vector.Expected.Kind, Convert.ToHexString([.. vector.Expected.Bytes])),
      Is.EqualTo((VectorValueKind.Text, input, VectorValueKind.Hex, hex)));
    Assert.That(Convert.ToUInt32(hex, 16), Is.EqualTo(Adler32(System.Text.Encoding.UTF8.GetBytes(input))), "RFC 1950 section 9 disagrees");
  }

  /// <summary>Runs <c>python3</c>, the probe of the toolchain lock's <c>python</c>, on an expression and checks what it prints.</summary>
  [Then("^the python reference " + StepText.Quoted + @" prints (\d+)$")]
  public void PythonPrints(string expression, string printed) {
    var info = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    info.ArgumentList.Add("-I");
    info.ArgumentList.Add("-c");
    info.ArgumentList.Add($"import zlib; print({expression})");
    Process process;
    try {
      process = Process.Start(info)!;
    } catch (Win32Exception exception) {
      if (Environment.GetEnvironmentVariable(ToolchainLocator.RequireVariable) == "1")
        Assert.Fail($"toolchain 'python' is not installed: {exception.Message}");
      Assert.Inconclusive($"toolchain 'python' is not installed: {exception.Message}");
      return;
    }

    using (process) {
      var output = process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      Assert.That((process.ExitCode, output.Trim()), Is.EqualTo((0, printed)), process.StandardError.ReadToEnd());
    }
  }

  /// <summary>Puts the repository's Adler-32 vector file into the workspace and seals it there.</summary>
  [Given("^a sealed tree whose vector " + StepText.Quoted + " expects hex " + StepText.Quoted + "$")]
  public void SealedTree(string name, string hex) {
    this.Tree.Write(GoldenTree.Adler32, GoldenTree.RepositoryAdler32).Seal(GoldenTree.Adler32);
    var vector = VectorFile.Load(this.Tree.Resolve(GoldenTree.Adler32)).Find(name);
    Assert.That(Convert.ToHexString([.. vector!.Expected.Bytes]), Is.EqualTo(hex));
  }

  /// <summary>Edits the one expected hex value of the sealed tree that was <c>024D0127</c>.</summary>
  [Given("^the expected value is edited to " + StepText.Quoted + " without sealing$")]
  public void EditExpected(string hex) {
    var text = this.Tree.Read(GoldenTree.Adler32);
    Assert.That(text, Does.Contain("\"024D0127\""));
    this.Tree.Write(GoldenTree.Adler32, text.Replace("\"024D0127\"", $"\"{hex}\"", StringComparison.Ordinal));
  }

  /// <summary>Seals the repository's Adler-32 file, then puts a copy that names another generator next to it.</summary>
  [Given("^a vector file whose provenance has generatedBy " + StepText.Quoted + "$")]
  public void GeneratedBy(string generator) {
    this.Tree.Write(GoldenTree.Adler32, GoldenTree.RepositoryAdler32).Seal(GoldenTree.Adler32);
    this._savedLock = this.Tree.Read(GoldenLock.RepositoryPath);
    this.Tree.Write("tests/fixtures/verify/self.vectors.json", GoldenTree.RepositoryAdler32.Replace("\"generatedBy\": \"python\"", $"\"generatedBy\": \"{generator}\"", StringComparison.Ordinal));
  }

  /// <summary>Runs <c>integrity seal</c> on the vector file the scenario wrote last.</summary>
  [When("^I run " + StepText.Quoted + " on it$")]
  public void RunOnIt(string line)
    => log.Add(Toolchain.Tooling(workspace.Path, [.. CommandLine.Split(line), "tests/fixtures/verify/self.vectors.json"]));

  /// <summary>Checks that the lock is the one the scenario sealed.</summary>
  [Then("^golden.lock.json is unchanged$")]
  public void LockUnchanged() => Assert.That(this.Tree.Read(GoldenLock.RepositoryPath), Is.EqualTo(this._savedLock));

  /// <summary>Commits <c>c1</c> and changes the files a later step commits as <c>c2</c>.</summary>
  [Given("^a git history where commit \"c2\" changes " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void HistoryWithCompiler(string vectors, string compiler) {
    Assert.That((vectors, compiler), Is.EqualTo((GoldenTree.Adler32, GoldenTree.CompilerFile)));
    this.Tree.History(changeCompiler: true);
  }

  /// <summary>Commits <c>c1</c> and changes the vector file a later step commits as <c>c2</c>.</summary>
  [Given("^a git history where commit \"c2\" changes only " + StepText.Quoted + ", " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void IsolatedHistory(string vectors, string lockPath, string changeLog) {
    Assert.That((vectors, lockPath, changeLog), Is.EqualTo((GoldenTree.Adler32, GoldenLock.RepositoryPath, GoldenLock.ChangeLogPath)));
    this.Tree.History(changeCompiler: false);
  }

  /// <summary>Seals the changed vector file, adds its change-log line and commits <c>c2</c>.</summary>
  [Given("^the vector file is sealed and golden.changes.md has its line$")]
  public void SealAndDeclare() => this.SealDeclareCommit($"- {GoldenTree.Adler32}: add 'a' vector (source: python)");

  /// <summary>Seals the changed vector file, adds the given change-log line and commits <c>c2</c>.</summary>
  [Given("^golden.changes.md gained " + StepText.Quoted + "$")]
  public void ChangeLogGained(string line) => this.SealDeclareCommit(line);

  /// <summary>Writes a vector file whose upstream-embedded source names a file of the pinned commit the corpus lock does not hold.</summary>
  [Given("^a vector file with provenance kind \"upstream-embedded\" whose source names the pinned " + StepText.Quoted + " commit and the path " + StepText.Quoted + "$")]
  public void UpstreamSource(string repository, string path) {
    var fixture = GoldenTree.Fixture("upstream-embedded.json");
    Assert.That(fixture, Does.Contain($"\"{repository}@b3a8a8e92cbca83475986391c3cac56f0b185d29:Cipher/algorithms/checksum/adler.js\""));
    var text = fixture.Replace("Cipher/algorithms/checksum/adler.js", path, StringComparison.Ordinal);
    this.Tree.Write("tests/fixtures/verify/upstream.vectors.json", text);
    this.Tree.Write(GoldenLock.RepositoryPath, new GoldenLock([GoldenLockEntry.For("tests/fixtures/verify/upstream.vectors.json", System.Text.Encoding.UTF8.GetBytes(text))]).ToJson());
  }

  private void SealDeclareCommit(string line) => this.Tree.Seal(GoldenTree.Adler32).Declare(line).Commit("c2");

  /// <summary>Adler-32 as RFC 1950 section 9 writes it, to hold the vector file to the normative text as well.</summary>
  private static uint Adler32(byte[] data) {
    uint a = 1, b = 0;
    foreach (var value in data) {
      a = (a + value) % 65521;
      b = (b + a) % 65521;
    }

    return (b << 16) | a;
  }

}
