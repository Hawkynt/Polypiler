using System.IO;
using NUnit.Framework;
using Polypiler.Core.Frontends.Go.Syntax;
using Polypiler.Core.Syntax;
using Polypiler.Tests.Frontends.Go;

namespace Polypiler.Tests.Syntax.Go;

/// <summary>
/// The parser generated from the spike's Go grammar (#1606, test design 1): the generator's output for
/// a whole language, with hooks, a predicate, memo keyed by hook state and literals mapped to kinds.
/// </summary>
[TestFixture]
public sealed class GoSpikeParserTests {

  /// <summary>Every production of the Go grammar parses, as the hand parser parses it.</summary>
  [Test]
  public void EveryProductionOfTheGrammar_Parses() {
    var text = File.ReadAllText(GoSyntaxTests.Grammar);
    Assert.That(GoSpikeParser.Accepts(text), Is.True);
    var parser = new GoSpikeParser(new GoSource(text));
    parser.ParseSourceFile();
    Assert.That((parser.HookDepth, parser.SecondPasses), Is.EqualTo((0, 0)));
  }

  /// <summary>A composite literal after a bare type in a control clause is refused; in parentheses it is accepted.</summary>
  [TestCase("package p\nfunc f() { if x == (T{}) { } }\n", true)]
  [TestCase("package p\nfunc f() { if x == T{} { } }\n", false)]
  [TestCase("package p\nfunc f() { for _, v := range []int{1, 2} { _ = v } }\n", true)]
  [TestCase("package p\nfunc f() { switch x := T{} { } }\n", false)]
  [TestCase("package p\nfunc f() { switch x := (T{}); x { } }\n", true)]
  public void TheCompositeLiteralRule_IsTheSpikes(string source, bool accepted)
    => Assert.That(GoSpikeParser.Accepts(source), Is.EqualTo(accepted));

  /// <summary>What no rule explains is reported at the farthest token with the grammar's message.</summary>
  [Test]
  public void AnUnexplainedError_NamesTheFarthestToken() {
    var error = Assert.Throws<SyntaxErrorException>(() => new GoSpikeParser(new GoSource("package p\nfunc f() { x := }\n")).ParseSourceFile())!.Error;
    Assert.That((error.Message, error.Position.Line), Is.EqualTo(("expected }", 2)));
  }

  /// <summary>The token adapter gives the scanner's literal, or the spelling of a token without one.</summary>
  [Test]
  public void Tokens_CarryTheirText() {
    var tokens = new GoSpikeTokens(new GoSource("package p // c\n"));
    Assert.That((tokens.Next().Text, tokens.Next().Text, tokens.Next().Text, tokens.Next().Kind), Is.EqualTo(("package", "p", "\n", GoTokenKind.Eof)));
    var token = new GoSpikeToken(new GoToken(GoTokenKind.Add, 3, ""), new SourcePosition(1, 4, 3));
    Assert.That((token.Text, token.Start, token.End), Is.EqualTo(("+", new SourcePosition(1, 4, 3), new SourcePosition(1, 4, 3))));
  }

}
