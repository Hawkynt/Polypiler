using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Runners;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Infra.Ci;
using Polypiler.Tooling.Cipher;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Toolchains;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The steps of <c>tests/features/lang/rs/be-rs-cipher-framework.feature</c> (#1422): the checksum files
/// of the Cipher corpus through polypiler, cargo and their known-answer tests; snippets through the Rust
/// backend, their text, clippy and rustfmt; and the refusal of what the backend does not write.
/// </summary>
[Binding, Scope(Feature = "Rust backend: the Cipher framework")]
public sealed class RustCipherFrameworkSteps {

  private string _file = "";
  private KatRow? _row;
  private string _source = "";
  private string _language = "";
  private string _text = "";
  private IReadOnlyList<GeneratedFile> _files = [];
  private IReadOnlyList<Diagnostic> _problems = [];

  [Given("^the Cipher file \"(.*)\" of the pinned corpus$")]
  public void TheCipherFile(string file) => this._file = file;

  [When("^polypiler-tooling cipher kat runs it with --targets rust$")]
  public void Run() {
    var root = PrototypeFixture.RepositoryRoot;
    var manifest = CorpusManifest.Load(Path.Combine(root, CorpusManifest.RepositoryPath));
    var fixture = manifest.Fixture(CipherCommand.Fixture)!;
    var pin = fixture.PinOf(manifest.Repository(fixture.Repository)!);
    var corpus = Path.GetDirectoryName(CorpusImporter.FilePath(root, pin.Name, pin.Commit, fixture.Path))!;
    lock (CipherKatRustTests.Import)
      if (!Directory.Exists(Path.Combine(corpus, "algorithms"))) {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.That(new CorpusCommand(new GitProcess(), root).Run(["import", "--fixture", CipherCommand.Fixture], output, error), Is.EqualTo(0), error.ToString());
      }

    var configuration = new DirectoryInfo(Path.GetDirectoryName(typeof(RustCipherFrameworkSteps).Assembly.Location)!).Parent!.Name;
    var polypiler = CipherCommand.Command(Path.Combine(root, "Polypiler.Cli", "bin", configuration, "net10.0", "polypiler.dll"));
    var work = Directory.CreateTempSubdirectory("polypiler-cipher-framework-").FullName;
    try {
      this._row = new KatHarness(new ProcessRunner(), polypiler, corpus, Path.Combine(root, CipherCommand.Goldens), work).Check(this._file, [KatTarget.Rust]);
    } finally {
      Directory.Delete(work, true);
    }
  }

  [Then("^the translation builds and (\\d+) of (\\d+) checked vectors pass$")]
  public void Passes(int passed, int checkable) {
    var result = this._row!.Targets[0];
    Assert.That(this._row.Checkable, Is.EqualTo(checkable));
    Assert.That((result.Stage, result.Passed), Is.EqualTo((KatStage.Ran, passed)), KatReport.Status(result));
    Assert.That(result.Failures, Is.Empty);
  }

  [Given("^the C# source \"(.*)\"$")]
  public void TheCSharpSource(string source) => (this._source, this._language) = (source, "csharp");

  [Given("^the JavaScript source \"(.*)\"$")]
  public void TheJavaScriptSource(string source) => (this._source, this._language) = (source, "javascript");

  private IrModule Imported() => this._language == "csharp"
    ? PrototypeFixture.Import("Snippet.cs", this._source)
    : JavaScriptFrontend.Import("snippet.mjs", this._source).Module!;

  [When("^it is translated to Rust$")]
  public void Translated() {
    this._files = RustCrates.Emit(this.Imported());
    this._text = RustCrates.ModuleText(this._files);
  }

  [When("^it is checked for Rust$")]
  public void Checked() => this._problems = RustCrates.Check(this.Imported());

  [Then("^the module contains \"(.*)\"$")]
  public void Contains(string text) => Assert.That(this._text, Does.Contain(text));

  [Then("^PLY1550 names \"(.*)\"$")]
  public void Names(string construct)
    => Assert.That(this._problems.Where(problem => problem.Code == "PLY1550").Select(problem => problem.Message), Has.Some.Contain($"'{construct}'"));

  [Then("^the crate is clean under cargo fmt --check and cargo clippy -D warnings$")]
  public async Task Clean() {
    var runner = RustRun.Runner(OverflowChecks.Panic);
    var package = RustCrates.Package(this._files);
    Assert.That(await runner.FormatCheckAsync(package), Is.Empty);
    Assert.That(await runner.ClippyAsync(package), Is.Empty);
  }

}
