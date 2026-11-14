using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Tooling.Diagnostics;
using Polypiler.Tooling.Report;
using static Polypiler.Tests.Tooling.Report.ErrorCatalogueTesting;

namespace Polypiler.Tests.Tooling.Report;

/// <summary>
/// <see cref="ErrorCatalogueWriter"/>: the index and the pages it writes for a registry, compared with
/// the hand-written pages of <c>tests/fixtures/infra/docs-errors/expected</c> and checked cell by cell
/// for each kind of code.
/// </summary>
[TestFixture]
public sealed class ErrorCatalogueWriterTests {

  private static string Row(IReadOnlyDictionary<string, string> files, string code)
    => files[ErrorCatalogueWriter.IndexName].Split('\n').Single(line => line.Contains($"[{code}]", StringComparison.Ordinal));

  private static string Property(IReadOnlyDictionary<string, string> files, string code, string property)
    => files[code + ".md"].Split('\n').Single(line => line.StartsWith($"| {property} |", StringComparison.Ordinal));

  /// <summary>Test design 1: the fixture registry with its notes gives the five hand-written files byte for byte.</summary>
  [Test]
  public void FixtureRegistry_GivesTheExpectedFiles() {
    var registry = DiagnosticRegistryValidator.Validate(Fixture("registry.yaml"));
    Assert.That(registry.Problems, Is.Empty);
    var notes = new Dictionary<string, ErrorNote> { ["PLY0031"] = ErrorNote.Parse(File.ReadAllText(Fixture("notes-valid/PLY0031.md")), "PLY0031.md", []) };

    var files = ErrorCatalogueWriter.Generate(registry, notes);

    Assert.That(files.Select(file => file.Path), Is.EqualTo(new[] { "README.md", "PLY0004.md", "PLY0009.md", "PLY0031.md", "PLY0035.md" }));
    foreach (var file in files)
      Assert.That(file.Content, Is.EqualTo(Lf(File.ReadAllText(Fixture("expected/" + file.Path)))), file.Path);
  }

  /// <summary>Output determinism: two runs on the same input give the same text.</summary>
  [Test]
  public void Generate_IsDeterministic() {
    var registry = DiagnosticRegistryValidator.Validate(Fixture("registry.yaml"));
    var first = ErrorCatalogueWriter.Generate(registry, new Dictionary<string, ErrorNote>());
    var second = ErrorCatalogueWriter.Generate(registry, new Dictionary<string, ErrorNote>());
    Assert.That(second, Is.EqualTo(first));
  }

  /// <summary>Every generated file starts with the marker, uses line feeds only and ends with exactly one.</summary>
  [Test]
  public void EveryFile_StartsWithTheMarkerAndEndsWithOneLineFeed() {
    foreach (var (name, text) in Generate(Registry(Entry("PLY0004"), Entry("PLY0009", "warning", null))))
      Assert.That((text.StartsWith(ErrorCatalogueWriter.Marker + "\n", StringComparison.Ordinal), text.Contains('\r'), text.EndsWith('\n'), text.EndsWith("\n\n", StringComparison.Ordinal)),
        Is.EqualTo((true, false, true, false)), name);
  }

  /// <summary>Test design 3: an error shows its number and name, a warning and an info a dash.</summary>
  [TestCase("error", "UsageError", "2 (UsageError)")]
  [TestCase("error", "InternalError", "70 (InternalError)")]
  [TestCase("warning", null, "—")]
  [TestCase("info", null, "—")]
  public void ExitCodeColumn_BySeverity(string severity, string? exit, string expected) {
    var files = Generate(Registry(Entry("PLY0004", severity, exit)));
    Assert.That(Row(files, "PLY0004"), Is.EqualTo($"| [PLY0004](PLY0004.md) | {severity} | {expected} | `a message` | TEST-OWNER |"));
    Assert.That(Property(files, "PLY0004", "Exit code"), Is.EqualTo($"| Exit code | {expected} |"));
  }

  /// <summary>Test design 4: a strict warning names the exit code <c>--strict</c> gives it; a warning that is not strict does not.</summary>
  [Test]
  public void StrictWarning_ShowsTheExitCodeUnderStrict() {
    var files = Generate(Registry(Entry("PLY0008", "warning", null), Entry("PLY0009", "warning", null, extra: "strict: true")));
    Assert.That(Row(files, "PLY0008"), Does.Contain("| warning | — |"));
    Assert.That(Row(files, "PLY0009"), Does.Contain("| warning | — (3 with --strict) |"));
    Assert.That((Property(files, "PLY0008", "Strict promotion"), Property(files, "PLY0009", "Strict promotion")),
      Is.EqualTo(("| Strict promotion | no |", "| Strict promotion | yes |")));
  }

  /// <summary>The number under <c>--strict</c> is that of the refusal PLY0010 the registry records.</summary>
  [Test]
  public void StrictExitCode_IsThatOfTheStrictRefusal() {
    var registry = DiagnosticRegistryValidator.Validate(Path.Combine(Polypiler.Prototype.Testing.PrototypeFixture.RepositoryRoot, DiagnosticRegistry.RepositoryPath));
    Assert.That(registry.Find("PLY0010")?.ExitCode, Is.EqualTo(ErrorCatalogueWriter.StrictExitCode));
  }

  /// <summary>Test design 5: an active, a reserved and a retired code; a retired row is struck through.</summary>
  [Test]
  public void Status_ActiveReservedRetired() {
    var files = Generate(Registry(Entry("PLY0004"), Entry("PLY0005", extra: "reserved: true"), Entry("PLY0035", extra: "retired: true")));
    Assert.That(Row(files, "PLY0004"), Does.StartWith("| [PLY0004](PLY0004.md) | "));
    Assert.That(Row(files, "PLY0005"), Does.StartWith("| [PLY0005](PLY0005.md) (reserved) | "));
    Assert.That(Row(files, "PLY0035"), Does.StartWith("| ~~[PLY0035](PLY0035.md)~~ (retired) | "));
    Assert.That(new[] { "PLY0004", "PLY0005", "PLY0035" }.Select(code => Property(files, code, "Status")),
      Is.EqualTo(new[] { "| Status | active |", "| Status | reserved |", "| Status | retired |" }));
  }

  /// <summary>Test design 7: a message with a bar, a backtick and an angle bracket stays one valid table cell.</summary>
  [Test]
  public void MessageWithMarkup_IsEscapedInTheCell() {
    var files = Generate(Registry(Entry("PLY0004", message: "a|b `c` <d>")));
    Assert.That(Row(files, "PLY0004"), Does.Contain("| ``a\\|b `c` <d>`` |"));
    Assert.That(Property(files, "PLY0004", "Message"), Is.EqualTo("| Message | ``a\\|b `c` <d>`` |"));
  }

  [TestCase("plain", "`plain`")]
  [TestCase("", "—")]
  [TestCase("a ``b`` c", "```a ``b`` c```")]
  [TestCase("`edge", "`` `edge ``")]
  [TestCase("edge`", "`` edge` ``")]
  [TestCase(" both ", "`  both  `")]
  [TestCase(" left", "` left`")]
  [TestCase("right ", "`right `")]
  [TestCase("x|y", "`x\\|y`")]
  public void CodeSpan_FencesAndPads(string text, string expected) => Assert.That(ErrorCatalogueWriter.CodeSpan(text), Is.EqualTo(expected));

  [TestCase("plain text", "plain text")]
  [TestCase(@"a\b`c*d_e[f]g<h>i|j~k&l", @"a\\b\`c\*d\_e\[f\]g\<h\>i\|j\~k\&l")]
  public void Escape_BackslashesMarkup(string text, string expected) => Assert.That(ErrorCatalogueWriter.Escape(text), Is.EqualTo(expected));

  [Test]
  public void Helpers_RefuseNull() {
    Assert.That(() => ErrorCatalogueWriter.CodeSpan(null!), Throws.ArgumentNullException);
    Assert.That(() => ErrorCatalogueWriter.Escape(null!), Throws.ArgumentNullException);
    Assert.That(() => ErrorCatalogueWriter.ExitCodeText(null!), Throws.ArgumentNullException);
  }

  /// <summary>Test design 8: the first and the last code of a block are listed under its heading.</summary>
  [Test]
  public void FirstAndLastCodeOfABlock_AreUnderItsHeading() {
    var index = Generate(Registry(Entry("PLY0001"), Entry("PLY0099"), Entry("PLY0100", area: "ir")))[ErrorCatalogueWriter.IndexName];
    var (infrastructure, ir) = (index.IndexOf("## PLY0001–PLY0099 · infrastructure\n", StringComparison.Ordinal), index.IndexOf("## PLY0100–PLY0199 · ir\n", StringComparison.Ordinal));
    var (first, last, next) = (index.IndexOf("[PLY0001]", StringComparison.Ordinal), index.IndexOf("[PLY0099]", StringComparison.Ordinal), index.IndexOf("[PLY0100]", StringComparison.Ordinal));
    Assert.That(new[] { infrastructure, first, last, ir, next }, Is.Ordered.And.All.GreaterThan(0));
  }

  /// <summary>A code of a nested block is listed under the innermost block only; the outer block without codes of its own has no heading.</summary>
  [Test]
  public void NestedBlock_ListsTheCodeUnderTheInnermostBlock() {
    var index = Generate(Registry(Entry("PLY1002", area: "csharp", exit: "UnsupportedConstruct"), Entry("PLY2900", area: "languages")))[ErrorCatalogueWriter.IndexName];
    Assert.That(index, Does.Contain("## PLY1000–PLY1099 · csharp\n"));
    Assert.That(index, Does.Contain("## PLY1000–PLY2999 · languages\n"));
    Assert.That(index.IndexOf("languages", StringComparison.Ordinal), Is.LessThan(index.IndexOf("csharp", StringComparison.Ordinal)));
    Assert.That(index.IndexOf("[PLY1002]", StringComparison.Ordinal), Is.GreaterThan(index.IndexOf("csharp", StringComparison.Ordinal)));
  }

  /// <summary>Test design 9: a registry without codes gives the index with its introduction only.</summary>
  [Test]
  public void RegistryWithoutCodes_GivesTheIntroductionOnly() {
    var files = Generate(Registry());
    Assert.That(files.Keys, Is.EqualTo(new[] { ErrorCatalogueWriter.IndexName }));
    Assert.That(files[ErrorCatalogueWriter.IndexName], Does.Not.Contain("##").And.Not.Contain("| Code |").And.Contain("# Error catalogue\n\nEvery diagnostic"));
  }

  /// <summary>Test design 10: a block without codes gets no heading.</summary>
  [Test]
  public void BlockWithoutCodes_HasNoHeading()
    => Assert.That(Generate(Registry(Entry("PLY0004")))[ErrorCatalogueWriter.IndexName], Does.Not.Contain("· ir").And.Not.Contain("· csharp"));

  /// <summary>Test design 11: exactly one code gives one table and one page.</summary>
  [Test]
  public void OneCode_GivesOneTableAndOnePage() {
    var files = Generate(Registry(Entry("PLY0004")));
    Assert.That(files.Keys, Is.EqualTo(new[] { ErrorCatalogueWriter.IndexName, "PLY0004.md" }));
    Assert.That(files[ErrorCatalogueWriter.IndexName].Split('\n').Count(line => line == "| Code | Severity | Exit code | Message | Owner |"), Is.EqualTo(1));
  }

  /// <summary>Test design 6, on the page: provided sections, a mixture and three default sentences.</summary>
  [Test]
  public void Notes_FillTheirSectionsAndTheRestReadTheDefault() {
    var notes = new Dictionary<string, ErrorNote> {
      ["PLY0004"] = new("why", "how", "fix"),
      ["PLY0005"] = new(null, "how", null)
    };
    var files = Generate(Registry(Entry("PLY0004"), Entry("PLY0005"), Entry("PLY0006")), notes);
    string Sections(string code) => files[code + ".md"][files[code + ".md"].IndexOf("## Cause", StringComparison.Ordinal)..];
    var none = ErrorCatalogueWriter.NoExplanation;
    Assert.That(Sections("PLY0004"), Is.EqualTo("## Cause\n\nwhy\n\n## Example\n\nhow\n\n## What to do\n\nfix\n\n[Back to the error catalogue](README.md)\n"));
    Assert.That(Sections("PLY0005"), Is.EqualTo($"## Cause\n\n{none}\n\n## Example\n\nhow\n\n## What to do\n\n{none}\n\n[Back to the error catalogue](README.md)\n"));
    Assert.That(Sections("PLY0006"), Is.EqualTo($"## Cause\n\n{none}\n\n## Example\n\n{none}\n\n## What to do\n\n{none}\n\n[Back to the error catalogue](README.md)\n"));
  }

  /// <summary>The page names the rule in code, the surfaces escaped, and a dash for a missing rule; a code without surfaces has no such row.</summary>
  [Test]
  public void Page_RuleAndSurfaces() {
    var files = Generate(Registry(Entry("PLY0004", extra: "rule: io.read\nsurfaces: \"stderr of <tool>\""), Entry("PLY0005")));
    Assert.That(Property(files, "PLY0004", "Rule"), Is.EqualTo("| Rule | `io.read` |"));
    Assert.That(Property(files, "PLY0004", "Seen in"), Is.EqualTo("| Seen in | stderr of \\<tool\\> |"));
    Assert.That(Property(files, "PLY0005", "Rule"), Is.EqualTo("| Rule | — |"));
    Assert.That(files["PLY0005.md"], Does.Not.Contain("| Seen in |"));
  }

  [Test]
  public void PageName_IsTheCodeWithTheMarkdownExtension() => Assert.That(ErrorCatalogueWriter.PageName("PLY0004"), Is.EqualTo("PLY0004.md"));

  [Test]
  public void ExitCodeText_OfAnEntry()
    => Assert.That(ErrorCatalogueWriter.ExitCodeText(new DiagnosticEntry("PLY0031", DiagnosticSeverity.Error, ExitCode.BudgetExceeded, "t", "m", null, "a", "o", null, false, false, false)),
      Is.EqualTo("10 (BudgetExceeded)"));

}
