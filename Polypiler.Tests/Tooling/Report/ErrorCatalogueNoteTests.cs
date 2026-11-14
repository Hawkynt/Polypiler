using System.Collections.Generic;
using NUnit.Framework;
using Polypiler.Tooling.Report;

namespace Polypiler.Tests.Tooling.Report;

/// <summary><see cref="ErrorNote.Parse"/>: the three sections of a note and every way a note is refused.</summary>
[TestFixture]
public sealed class ErrorCatalogueNoteTests {

  private static (ErrorNote Note, List<string> Problems) Parse(string text) {
    var problems = new List<string>();
    return (ErrorNote.Parse(text, "notes/PLY0004.md", problems), problems);
  }

  /// <summary>Test design 6: all three sections.</summary>
  [Test]
  public void AllThreeSections_AreRead() {
    var (note, problems) = Parse("## Cause\n\nwhy\n\n## Example\n\nhow\n\n## What to do\n\nfix\n");
    Assert.That((note, problems), Is.EqualTo((new ErrorNote("why", "how", "fix"), new List<string>())));
  }

  /// <summary>Test design 6: one section; the others stay empty.</summary>
  [Test]
  public void OneSection_LeavesTheOthersEmpty()
    => Assert.That(Parse("## What to do\n\nRaise the limit.\n").Note, Is.EqualTo(new ErrorNote(null, null, "Raise the limit.")));

  /// <summary>Test design 6: no section at all.</summary>
  [TestCase("")]
  [TestCase("\n\n")]
  public void NoSection_IsTheEmptyNote(string text) => Assert.That(Parse(text), Is.EqualTo((ErrorNote.Empty, new List<string>())));

  /// <summary>A section with nothing but blank lines counts as missing.</summary>
  [Test]
  public void BlankSection_CountsAsMissing() => Assert.That(Parse("## Cause\n\n   \n## Example\nx").Note, Is.EqualTo(new ErrorNote(null, "x", null)));

  /// <summary>Several lines and paragraphs are kept; CRLF becomes LF and trailing blank lines are dropped.</summary>
  [Test]
  public void SectionText_KeepsItsLinesAndParagraphs()
    => Assert.That(Parse("## Cause\r\n\r\nfirst\r\nsecond\r\n\r\nthird  \r\n\r\n").Note.Cause, Is.EqualTo("first\nsecond\n\nthird"));

  /// <summary>Test design 16: a heading other than the three sections.</summary>
  [TestCase("## Background", "## Background")]
  [TestCase("# PLY0004", "# PLY0004")]
  [TestCase("### Detail  ", "### Detail")]
  [TestCase("##Cause", "##Cause")]
  public void UnknownHeading_IsRefused(string heading, string shown)
    => Assert.That(Parse($"## Cause\n\nwhy\n\n{heading}\n\nmore\n").Problems,
      Is.EqualTo(new[] { $"note 'notes/PLY0004.md' has an unknown section '{shown}'" }));

  /// <summary>The text under an unknown heading belongs to no section.</summary>
  [Test]
  public void UnknownHeading_EndsTheSectionBeforeIt() => Assert.That(Parse("## Cause\nwhy\n## Background\nmore\n").Note.Cause, Is.EqualTo("why"));

  [Test]
  public void SectionTwice_IsRefused()
    => Assert.That(Parse("## Cause\na\n## Cause\nb\n").Problems, Is.EqualTo(new[] { "note 'notes/PLY0004.md' has the section '## Cause' twice" }));

  [Test]
  public void TextBeforeTheFirstSection_IsRefusedOnce()
    => Assert.That(Parse("intro\nmore intro\n## Cause\nwhy\n").Problems, Is.EqualTo(new[] { "note 'notes/PLY0004.md' has text before its first section" }));

  /// <summary>A line starting with <c>#</c> inside a fenced code block is code, not a heading.</summary>
  [TestCase("```")]
  [TestCase("~~~")]
  [TestCase("````")]
  public void HashInsideAFence_IsNoHeading(string fence) {
    var (note, problems) = Parse($"## Example\n\n{fence}sh\n# a shell comment\n{fence}\n\n## What to do\nfix\n");
    Assert.That((note.Example, note.WhatToDo, problems), Is.EqualTo(($"{fence}sh\n# a shell comment\n{fence}", "fix", new List<string>())));
  }

  /// <summary>A fence closes only on a line of its own character, at least as long as the opening one.</summary>
  [Test]
  public void Fence_ClosesOnlyOnAMatchingLine() {
    var (note, problems) = Parse("## Example\n````\n```\n~~~~\n``` text\n# still code\n`````\n# after\n");
    Assert.That((note.Example, problems), Is.EqualTo(("````\n```\n~~~~\n``` text\n# still code\n`````", new List<string> { "note 'notes/PLY0004.md' has an unknown section '# after'" })));
  }

  /// <summary>Two backticks open no fence, so a heading after them is still a heading.</summary>
  [TestCase("``")]
  [TestCase("``x")]
  public void TwoBackticks_OpenNoFence(string line) => Assert.That(Parse($"## Example\n{line}\n## What to do\nfix\n").Note, Is.EqualTo(new ErrorNote(null, line, "fix")));

}
