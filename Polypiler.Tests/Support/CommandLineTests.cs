using System;
using NUnit.Framework;
using Polypiler.Tests.Acceptance.Support;

namespace Polypiler.Tests.Support;

/// <summary>The arguments of the command line of a step.</summary>
[TestFixture]
public sealed class CommandLineTests {

  /// <summary>Whitespace separates, single quotes group, a quote is not part of the argument (test design 10).</summary>
  [TestCase("polypiler --version", new[] { "polypiler", "--version" })]
  [TestCase("a '' 'b c'", new[] { "a", "", "b c" }, TestName = "An empty quoted argument and one with spaces")]
  [TestCase("  a \t b  ", new[] { "a", "b" }, TestName = "Runs of whitespace of any kind separate")]
  [TestCase("a'b c'd", new[] { "ab cd" }, TestName = "Quoted text joins the text around it")]
  [TestCase("''", new[] { "" })]
  [TestCase("", new string[0])]
  [TestCase("   ", new string[0])]
  [TestCase("polypiler", new[] { "polypiler" }, TestName = "A command line with no arguments (test design 9)")]
  public void Split_FollowsTheQuotes(string line, string[] arguments) => Assert.That(CommandLine.Split(line), Is.EqualTo(arguments));

  /// <summary>Test design 17: a quote that is not closed is named by its 0-based position.</summary>
  [TestCase("polypiler 'abc", "unterminated quote at position 10")]
  [TestCase("'", "unterminated quote at position 0")]
  [TestCase("a 'b' 'c", "unterminated quote at position 6")]
  public void UnterminatedQuote_IsRefused(string line, string message)
    => Assert.That(() => CommandLine.Split(line), Throws.TypeOf<FormatException>().With.Message.EqualTo(message));

}
