using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling;
using Polypiler.Tooling.Report;
using static Polypiler.Tests.Tooling.Report.ErrorCatalogueTesting;

namespace Polypiler.Tests.Tooling.Report;

/// <summary>
/// <c>polypiler-tooling report errors</c> in process, in a scratch directory of its own: writing,
/// checking, the notes, and every refusal with its code and exit code.
/// </summary>
[TestFixture]
public sealed class ErrorCatalogueCommandTests {

  private static readonly string _newLine = Environment.NewLine;

  private string _directory = "";

  [SetUp]
  public void CreateDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-errors-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteDirectory() {
    if (Directory.Exists(this._directory))
      Directory.Delete(this._directory, true);
  }

  private string At(string path) => Path.Combine(this._directory, path.Replace('/', Path.DirectorySeparatorChar));

  private void Write(string path, string text) {
    Directory.CreateDirectory(Path.GetDirectoryName(this.At(path))!);
    File.WriteAllText(this.At(path), text);
  }

  /// <summary>Copies the fixture registry and the valid notes into the scratch directory.</summary>
  private void CopyFixture() {
    this.Write("registry.yaml", File.ReadAllText(Fixture("registry.yaml")));
    this.Write("notes-valid/PLY0031.md", File.ReadAllText(Fixture("notes-valid/PLY0031.md")));
  }

  private (int ExitCode, string Output, string Error) Run(params string[] args) {
    var (output, error) = (new StringWriter(), new StringWriter());
    var exitCode = ErrorCatalogueCommand.Run(args, output, error, this._directory);
    return (exitCode, output.ToString(), error.ToString());
  }

  private (int ExitCode, string Output, string Error) Generate(params string[] extra)
    => this.Run(["errors", "--registry", "registry.yaml", "--notes", "notes-valid", "--out", "out", .. extra]);

  /// <summary>Test design 1 through the command: the five files equal the hand-written ones.</summary>
  [Test]
  public void Generate_WritesTheExpectedFiles() {
    this.CopyFixture();
    Assert.That(this.Generate(), Is.EqualTo((0, "5 files, 5 written, 0 deleted" + _newLine, "")));
    var expected = Snapshot(Fixture("expected")).ToDictionary(pair => pair.Key, pair => Lf(pair.Value));
    Assert.That(Snapshot(this.At("out")), Is.EquivalentTo(expected));
  }

  /// <summary>A second run writes nothing and leaves the files as they are.</summary>
  [Test]
  public void SecondRun_WritesNothing() {
    this.CopyFixture();
    this.Generate();
    var before = Directory.GetFiles(this.At("out")).ToDictionary(path => path, File.GetLastWriteTimeUtc);
    Assert.That(this.Generate(), Is.EqualTo((0, "5 files, 0 written, 0 deleted" + _newLine, "")));
    Assert.That(Directory.GetFiles(this.At("out")).ToDictionary(path => path, File.GetLastWriteTimeUtc), Is.EqualTo(before));
  }

  /// <summary>A page with CRLF line endings passes the check and is rewritten with LF by a run.</summary>
  [Test]
  public void CrLfPage_PassesTheCheckAndIsRewritten() {
    this.CopyFixture();
    this.Generate();
    var page = this.At("out/PLY0004.md");
    File.WriteAllText(page, File.ReadAllText(page).Replace("\n", "\r\n", StringComparison.Ordinal));
    Assert.That(this.Generate("--check").ExitCode, Is.Zero);
    Assert.That(this.Generate().Output, Is.EqualTo("5 files, 1 written, 0 deleted" + _newLine));
    Assert.That(File.ReadAllText(page), Does.Not.Contain("\r"));
  }

  /// <summary>Test design 2: the repository registry with the repository notes, generated and then checked.</summary>
  [Test]
  public void RepositoryRegistry_GeneratesAndChecks() {
    var root = PrototypeFixture.RepositoryRoot;
    var registry = Path.Combine(root, "spec", "diagnostics.yaml");
    var notes = Path.Combine(root, "docs", "errors", "notes");
    var output = this.At("out");
    Assert.That(this.Run("errors", "--registry", registry, "--notes", notes, "--out", output).ExitCode, Is.Zero);
    Assert.That(this.Run("errors", "--registry", registry, "--notes", notes, "--out", output, "--check"), Is.EqualTo((0, $"{Directory.GetFiles(output).Length} files up to date" + _newLine, "")));
  }

  /// <summary>The committed catalogue <c>docs/errors</c> is what the repository registry and notes give, with the defaults of the command.</summary>
  [Test]
  public void CommittedCatalogue_IsUpToDate() {
    var (output, error) = (new StringWriter(), new StringWriter());
    var exitCode = ErrorCatalogueCommand.Run(["errors", "--check"], output, error, PrototypeFixture.RepositoryRoot);
    Assert.That(exitCode, Is.Zero, "run 'polypiler-tooling report errors' and commit docs/errors:" + _newLine + error);
  }

  /// <summary>Every registered code has a page in the committed catalogue.</summary>
  [Test]
  public void CommittedCatalogue_HasAPagePerCode() {
    var registry = Polypiler.Tooling.Diagnostics.DiagnosticRegistryValidator.Validate(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "diagnostics.yaml"));
    var pages = Directory.GetFiles(Path.Combine(PrototypeFixture.RepositoryRoot, "docs", "errors"), "PLY*.md").Select(Path.GetFileNameWithoutExtension);
    Assert.That(pages, Is.EquivalentTo(registry.Codes.Select(entry => entry.Code)));
  }

  /// <summary>Test design 12: a changed message makes the page and the index differ; nothing is written.</summary>
  [Test]
  public void Check_ChangedMessage_ReportsBothFilesAndWritesNothing() {
    this.CopyFixture();
    this.Generate();
    var before = Snapshot(this.At("out"));
    this.Write("registry.yaml", File.ReadAllText(this.At("registry.yaml")).Replace("cannot read input", "cannot open input", StringComparison.Ordinal));
    Assert.That(this.Generate("--check"), Is.EqualTo((1, "",
      "polypiler-tooling: error PLY0091: generated document out of date: 'out/PLY0004.md' differs" + _newLine
      + "polypiler-tooling: error PLY0091: generated document out of date: 'out/README.md' differs" + _newLine)));
    Assert.That(Snapshot(this.At("out")), Is.EqualTo(before));
  }

  /// <summary>Test design 13: a deleted page is missing.</summary>
  [Test]
  public void Check_MissingPage() {
    this.CopyFixture();
    this.Generate();
    File.Delete(this.At("out/PLY0031.md"));
    Assert.That(this.Generate("--check"), Is.EqualTo((1, "", "polypiler-tooling: error PLY0091: generated document out of date: 'out/PLY0031.md' is missing" + _newLine)));
    Assert.That(File.Exists(this.At("out/PLY0031.md")), Is.False);
  }

  /// <summary>Test design 14: a page without a registered code; the notes folder below the output is no file of it.</summary>
  [Test]
  public void Check_PageWithoutRegisteredCode() {
    this.CopyFixture();
    this.Generate();
    this.Write("out/PLY0500.md", "stale");
    this.Write("out/notes/PLY0004.md", "## Cause\nwhy\n");
    Assert.That(this.Generate("--check"), Is.EqualTo((1, "", "polypiler-tooling: error PLY0091: generated document out of date: 'out/PLY0500.md' has no registered code" + _newLine)));
    Assert.That(File.Exists(this.At("out/PLY0500.md")), Is.True);
  }

  /// <summary>Without an output directory every file is missing.</summary>
  [Test]
  public void Check_WithoutOutputDirectory_ReportsEveryFileMissing() {
    this.CopyFixture();
    var (exitCode, _, error) = this.Generate("--check");
    Assert.That((exitCode, error.Split(_newLine, StringSplitOptions.RemoveEmptyEntries).Length), Is.EqualTo((1, 5)));
    Assert.That(error, Does.Contain("'out/README.md' is missing"));
    Assert.That(Directory.Exists(this.At("out")), Is.False);
  }

  /// <summary>Test design 15: a note of an unregistered code refuses the run before anything is written.</summary>
  [Test]
  public void OrphanNote_IsRefusedAndNothingIsWritten() {
    this.CopyFixture();
    this.Write("notes/PLY0031.md", File.ReadAllText(Fixture("notes/PLY0031.md")));
    this.Write("notes/PLY0777.md", File.ReadAllText(Fixture("notes/PLY0777.md")));
    Assert.That(this.Run("errors", "--registry", "registry.yaml", "--notes", "notes", "--out", "out"),
      Is.EqualTo((1, "", "polypiler-tooling: error PLY0091: note 'notes/PLY0777.md' belongs to no registered code" + _newLine)));
    Assert.That(Directory.Exists(this.At("out")), Is.False);
  }

  /// <summary>A Markdown file of the notes that is not named after a code belongs to no code either; other files are not notes.</summary>
  [Test]
  public void NoteNotNamedAfterACode_IsRefused() {
    this.CopyFixture();
    this.Write("notes-valid/README.md", "## Cause\nx\n");
    this.Write("notes-valid/.gitkeep", "");
    Assert.That(this.Generate().Error, Is.EqualTo("polypiler-tooling: error PLY0091: note 'notes-valid/README.md' belongs to no registered code" + _newLine));
  }

  /// <summary>Test design 16: a note with an unknown heading refuses the run.</summary>
  [Test]
  public void NoteWithUnknownHeading_IsRefused() {
    this.CopyFixture();
    this.Write("notes-valid/PLY0004.md", "## Background\n\ntext\n");
    Assert.That(this.Generate(), Is.EqualTo((1, "", "polypiler-tooling: error PLY0091: note 'notes-valid/PLY0004.md' has an unknown section '## Background'" + _newLine)));
    Assert.That(Directory.Exists(this.At("out")), Is.False);
  }

  /// <summary>Test design 9 through the command: a registry without codes gives the index alone, and the check passes.</summary>
  [Test]
  public void RegistryWithoutCodes_WritesTheIndexOnly() {
    this.Write("registry.yaml", RegistryText());
    Assert.That(this.Run("errors", "--registry", "registry.yaml", "--out", "out"), Is.EqualTo((0, "1 file, 1 written, 0 deleted" + _newLine, "")));
    Assert.That(this.Run("errors", "--registry", "registry.yaml", "--out", "out", "--check"), Is.EqualTo((0, "1 file up to date" + _newLine, "")));
    Assert.That(Snapshot(this.At("out")).Keys, Is.EqualTo(new[] { "README.md" }));
  }

  /// <summary>A missing notes folder holds no notes.</summary>
  [Test]
  public void MissingNotesFolder_GivesDefaultSections() {
    this.CopyFixture();
    Assert.That(this.Run("errors", "--registry", "registry.yaml", "--notes", "nowhere", "--out", "out").ExitCode, Is.Zero);
    Assert.That(File.ReadAllText(this.At("out/PLY0031.md")), Does.Contain("## What to do\n\n" + ErrorCatalogueWriter.NoExplanation));
  }

  /// <summary>Test design 17: an invalid registry is passed through as PLY0045 and nothing is written.</summary>
  [Test]
  public void InvalidRegistry_IsPassedThrough() {
    this.Write("registry.yaml", RegistryText(Entry("PLY0004"), Entry("PLY0004")));
    var (exitCode, output, error) = this.Run("errors", "--registry", "registry.yaml", "--out", "out");
    Assert.That((exitCode, output), Is.EqualTo((1, "")));
    Assert.That(error, Does.Contain("error PLY0045: duplicate code PLY0004"));
    Assert.That(Directory.Exists(this.At("out")), Is.False);
  }

  /// <summary>A registry that cannot be read is a registry problem too.</summary>
  [Test]
  public void MissingRegistry_IsPassedThrough()
    => Assert.That(this.Run("errors", "--registry", "absent.yaml").Error, Does.Contain("error PLY0045: cannot read registry"));

  /// <summary>Test design 18: an output path that is a file.</summary>
  [Test]
  public void OutputIsAFile_IsPLY0005() {
    this.CopyFixture();
    this.Write("README.md", "a file");
    var (exitCode, output, error) = this.Run("errors", "--registry", "registry.yaml", "--notes", "notes-valid", "--out", "README.md");
    Assert.That((exitCode, output), Is.EqualTo((1, "")));
    Assert.That(error, Does.StartWith("polypiler-tooling: error PLY0005: the output directory 'README.md' cannot be written: "));
    Assert.That(File.ReadAllText(this.At("README.md")), Is.EqualTo("a file"));
  }

  /// <summary>A page that is out of date and cannot be overwritten is PLY0005 as well.</summary>
  [Test]
  public void ReadOnlyPage_IsPLY0005() {
    this.CopyFixture();
    this.Write("out/PLY0004.md", "stale");
    File.SetAttributes(this.At("out/PLY0004.md"), FileAttributes.ReadOnly);
    try {
      var (exitCode, _, error) = this.Generate();
      Assert.That(exitCode, Is.EqualTo(1));
      Assert.That(error, Does.StartWith("polypiler-tooling: error PLY0005: the output directory 'out' cannot be written: "));
    } finally {
      File.SetAttributes(this.At("out/PLY0004.md"), FileAttributes.Normal);
    }
  }

  /// <summary>Test design 19: the page of a code no longer registered is deleted; other files and the notes stay.</summary>
  [Test]
  public void RemovedCode_DeletesItsPageOnly() {
    this.CopyFixture();
    this.Write("out/notes/PLY0009.md", "## Cause\nwhy\n");
    this.Write("out/PLYxxxx.md", "not a page");
    this.Generate();
    var registry = File.ReadAllText(this.At("registry.yaml"));
    var start = registry.IndexOf("  - code: PLY0009", StringComparison.Ordinal);
    this.Write("registry.yaml", registry.Remove(start, registry.IndexOf("  - code: PLY0031", StringComparison.Ordinal) - start));

    Assert.That(this.Generate(), Is.EqualTo((0, "4 files, 1 written, 1 deleted" + _newLine, "")));
    Assert.That(Snapshot(this.At("out")).Keys, Is.EquivalentTo(new[] { "README.md", "PLY0004.md", "PLY0031.md", "PLY0035.md", "PLYxxxx.md" }));
    Assert.That(File.ReadAllText(this.At("out/notes/PLY0009.md")), Is.EqualTo("## Cause\nwhy\n"));
  }

  /// <summary>The defaults: the repository paths below the current directory, shown as such.</summary>
  [Test]
  public void Defaults_AreTheRepositoryPaths() {
    this.Write("spec/diagnostics.yaml", File.ReadAllText(Fixture("registry.yaml")));
    this.Write("docs/errors/notes/PLY0031.md", File.ReadAllText(Fixture("notes-valid/PLY0031.md")));
    Assert.That(this.Run("errors").ExitCode, Is.Zero);
    File.Delete(this.At("docs/errors/PLY0004.md"));
    Assert.That(this.Run("errors", "--check").Error, Is.EqualTo("polypiler-tooling: error PLY0091: generated document out of date: 'docs/errors/PLY0004.md' is missing" + _newLine));
  }

  /// <summary>A trailing separator of the output directory is not doubled in the paths shown.</summary>
  [Test]
  public void TrailingSeparator_IsNotDoubled() {
    this.CopyFixture();
    Assert.That(this.Run("errors", "--registry", "registry.yaml", "--notes", "notes-valid", "--out", "out/", "--check").Error, Does.Contain("'out/README.md' is missing"));
  }

  [TestCase(new string[0], "report needs 'errors'")]
  [TestCase(new[] { "warnings" }, "unknown report 'warnings'")]
  [TestCase(new[] { "errors", "--force" }, "unknown option '--force'")]
  [TestCase(new[] { "errors", "--out" }, "option '--out' requires a value")]
  [TestCase(new[] { "errors", "--notes", "a", "--notes", "b" }, "option '--notes' is given more than once")]
  [TestCase(new[] { "errors", "--registry", "a", "--registry", "b" }, "option '--registry' is given more than once")]
  [TestCase(new[] { "errors", "--check", "--check" }, "option '--check' is given more than once")]
  public void CommandLine_NotUnderstood(string[] args, string message)
    => Assert.That(this.Run(args), Is.EqualTo((2, "", $"polypiler-tooling: error PLY0003: {message}" + _newLine)));

  /// <summary>The verb is reached through the program and listed in its usage.</summary>
  [Test]
  public void Program_DispatchesAndListsTheVerb() {
    var (output, error) = (new StringWriter(), new StringWriter());
    Assert.That(Program.Run(["report"], output, error), Is.EqualTo(2));
    Assert.That(error.ToString(), Does.Contain("report needs 'errors'"));
    var usage = new StringWriter();
    Program.Run([], usage, new StringWriter());
    Assert.That(usage.ToString(), Does.Contain("polypiler-tooling report errors [--registry <file>] [--notes <dir>] [--out <dir>] [--check]"));
  }

  /// <summary>The overload without a directory takes paths from the process's current directory.</summary>
  [Test]
  public void WithoutDirectory_UsesTheCurrentDirectory() {
    var (output, error) = (new StringWriter(), new StringWriter());
    var registry = Path.Combine(this._directory, "absent.yaml");
    Assert.That(ErrorCatalogueCommand.Run(["errors", "--registry", registry], output, error), Is.EqualTo(1));
    Assert.That(error.ToString(), Does.Contain(registry));
  }

}
