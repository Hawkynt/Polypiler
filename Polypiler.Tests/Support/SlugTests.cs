using NUnit.Framework;
using Polypiler.Tests.Acceptance.Support;

namespace Polypiler.Tests.Support;

/// <summary>The slug of a feature or scenario title.</summary>
[TestFixture]
public sealed class SlugTests {

  /// <summary>The examples of the feature, and the boundaries of test design 11 and 12.</summary>
  [TestCase("A shift/or pair becomes a rotate", "a-shift-or-pair-becomes-a-rotate")]
  [TestCase("O0100 recognize rotate-left", "o0100-recognize-rotate-left")]
  [TestCase("--opt none  vs. --opt speed!", "opt-none-vs-opt-speed", TestName = "Leading, trailing and doubled separators are one or none")]
  [TestCase("!!!", "", TestName = "A title of only punctuation has the empty slug")]
  [TestCase("", "")]
  [TestCase("abc", "abc")]
  [TestCase("Ünïcode ß", "n-code", TestName = "Letters outside a-z separate")]
  [TestCase("C# 14", "c-14")]
  public void Slug_IsTheLowerCaseTitleWithSeparatorsCollapsed(string title, string slug) => Assert.That(Slug.Of(title), Is.EqualTo(slug));

  /// <summary>Test design 8: the rows of an outline end in their 1-based number.</summary>
  [TestCase(1, "titles-become-stable-slugs-1")]
  [TestCase(2, "titles-become-stable-slugs-2")]
  [TestCase(3, "titles-become-stable-slugs-3")]
  public void OutlineRow_EndsInItsNumber(int row, string slug) => Assert.That(Slug.Of("Titles become stable slugs", row), Is.EqualTo(slug));

}
