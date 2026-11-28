using System.Collections.Generic;
using NUnit.Framework;
using Polypiler.Core.Syntax;

namespace Polypiler.Tests.Tooling.Grammar;

/// <summary>
/// The parsers <c>polypiler-tooling grammar generate</c> makes, compiled and run: every construct of
/// the format (#1606), the second pass of the invalid rules (#1609) and the guards of the profile
/// evaluated at run time and at generation (#1608).
/// </summary>
[TestFixture, Category("Slow")]
public sealed class GeneratedParserTests {

  private static readonly Dictionary<string, string> _grammars = new() {
    ["Arith"] = """
      start[string]: e=expr ENDMARKER { e }
      expr[string]:
          | a=expr '+' b=term { "(" + a + "+" + b + ")" }
          | a=expr '-' b=term { "(" + a + "-" + b + ")" }
          | term
      term[string] (memo): a=term '*' b=atom { "(" + a + "*" + b + ")" } | atom
      atom[string]: n=NUMBER { n.Text } | '(' e=expr ')' { e } | n=NAME { n.Text }

      """,
    ["Indirect"] = """
      start[string]: x=a $ { x }
      a[string]: x=b 'x' { x + "x" } | 'y' { "y" }
      b[string]: x=a 'z' { x + "z" } | 'w' { "w" }

      """,
    ["Constructs"] = """
      @hooks quiet
      start: x=thing $ { x }
      thing:
          | 'opt' a=NAME? { "opt:" + (a?.Text ?? "-") }
          | 'brk' a=[NAME NUMBER] { "brk:" + P.Render(a) }
          | 'star' a=NAME* { "star:" + a.Count }
          | 'plus' a=NAME+ { "plus:" + a.Count }
          | 'gather' a=','.NAME+ { "gather:" + string.Join("|", a.Select(t => t.Text)) }
          | 'pos' &NUMBER n=NUMBER { "pos:" + n.Text }
          | 'neg' !NUMBER n=NAME { "neg:" + n.Text }
          | 'grp' a=('a' | 'b') { "grp:" + a.Text }
          | 'soft' "match" { "soft" }
          | 'seq' NAME NUMBER
          | 'one' n=NUMBER
          | 'tmp' a=(x=NAME '=' { x })+ { "tmp:" + P.Render(a) }
          | 'new' a=(x=NAME { new List<string> { x.Text } }) { "new:" + a.Count }
          | 'cast' a=(x=NAME { (object)x.Text }) { "cast:" + a }
          | 'typed' a[List<Token<Kind>>]=NAME+ { "typed:" + a.Count }
          | 'hook' x=quiet(y=NAME { Quiet }) ?loud { "hook:" + x }
          | 'pred' ?loud n=NAME { "pred:" + n.Text }
          | 'mixed' a=(NAME | NUMBER '!') { P.Render(a) }
          | 'empty' a=NUMBER* b=NAME* { "empty:" + a.Count + b.Count }
          | 'throw' NUMBER { throw RaiseSyntaxError("thrown") }
          | 'brk2' a=[NAME NUMBER] { a }
          | 'gat2' a=','.NAME+ { a }
          | 'inl' a=(NAME) { a }
          | 'hopt' x=quiet(NAME?) { x }
          | 'desc' a=(&NAME !NUMBER b[Token<Kind>]=NAME &&'.' { b }) { a }
          | 'cast2' a=typedfrom { a }
          | 'otyped' a[Token<Kind>]=NAME? { a?.Text ?? "-" }
          | 'word' a=WORD+ { a.Count + ":" + a[0] }
          | 'word2' b=TOKEN2 { b.Text }
          | 'kws' a=','* { a.Count }
          | 'sk' a="match"+ { a.Count }
          | 'optg' a=(x=NAME? { x }) NUMBER { P.Render(a) }
          | 'fg' a=(&&'.')+ { a.Count }
          | 'hk' a=quiet(NAME)* { a.Count }
          | 'cg' a=(~ &NAME) NAME { P.Render(a) }
      typedfrom[string]: plain
      plain: n=NAME { n.Text }

      """,
    ["Cut"] = """
      start: 'c' ~ NAME $ { "cut" } | 'c' NUMBER $ { "after" } | 'd' NUMBER $ { "d" }

      """,
    ["Forced"] = """
      start: 'f' &&':' NAME $ { "f" } | 'g' &&('a' | 'b') $ { "g" }

      """,
    ["Invalid"] = """
      start: a=assignment $ { a } | invalid_assignment | !('a' 'b' 'c' 'd') NAME NUMBER $ { "pair" } | x=inner_without_invalid $ { x }
      assignment[string]: n=NAME '=' v=NUMBER { n.Text + "=" + v.Text }
      invalid_assignment: a=NUMBER '=' NAME { throw RaiseSyntaxErrorKnownLocation(a, "cannot assign to literal") }
      inner_without_invalid: invalid_inner | 'w' NAME { "inner" }
      invalid_inner: 'w' NUMBER { throw RaiseSyntaxError("never: the rule turns the invalid rules off") }

      """,
    ["Soft"] = """
      @soft_keyword 'SoftWord string'
      start: a="match" NAME $ { a } | b="case"+ $ { b.Count }

      """,
    ["Runtime"] = """
      @axis version
      start: m=match_stmt $ { m } | @version < 3.9 'old' $ { "old" } | n=NUMBER $ { n.Text }
      match_stmt: @version >= 3.10 @message "Pattern matching is only supported in Python 3.10 and greater" "match" NAME ':' { "match" }

      """,
  };

  private static readonly Dictionary<string, string> _generate = new() {
    ["GenNest"] = """
      @axis version
      @guards generate
      @hooks quiet
      start: 'nest' (@version >= 3.10 'new' | 'old') [NAME] (NUMBER)? NAME* ','.NUMBER+ &'x' 'x' &&'y' quiet('z') $ { "nest" }

      """,
    ["Gen39"] = _grammars["Runtime"].Replace("@axis version", "@axis version\n@guards generate", System.StringComparison.Ordinal),
    ["Gen310"] = _grammars["Runtime"].Replace("@axis version", "@axis version\n@guards generate", System.StringComparison.Ordinal),
    ["Gen38"] = _grammars["Runtime"].Replace("@axis version", "@axis version\n@guards generate", System.StringComparison.Ordinal),
  };

  private static IReadOnlyDictionary<string, GrammarHarness.Parse> _parsers = null!;

  [OneTimeSetUp]
  public static void Generate() {
    var all = new Dictionary<string, string>(_grammars);
    foreach (var (name, text) in _generate)
      all[name] = text;

    static GrammarProfile Version(string version) => new(new Dictionary<string, string> { ["version"] = version });
    _parsers = GrammarHarness.Build(all, new Dictionary<string, GrammarProfile> { ["Gen39"] = Version("3.9"), ["Gen310"] = Version("3.10"), ["Gen38"] = Version("3.8"), ["GenNest"] = Version("3.10") });
  }

  private static string Run(string grammar, string text, string? version = null) => _parsers[grammar](text, version, out _);

  /// <summary>Test design 5: left recursion grows a left-associative tree; a memoized left-recursive rule still grows.</summary>
  [TestCase("1 - 2 - 3", "((1-2)-3)")]
  [TestCase("1 + 2 * 3 * 4", "(1+((2*3)*4))")]
  [TestCase("(1 + 2) * x", "((1+2)*x)")]
  [TestCase("7", "7")]
  [TestCase("1 +", "error: invalid syntax @3")]
  public void LeftRecursion_IsLeftAssociative(string text, string expected) => Assert.That(Run("Arith", text), Is.EqualTo(expected));

  /// <summary>Indirect left recursion grows through its leader.</summary>
  [TestCase("y", "y")]
  [TestCase("y z x z x", "yzxzx")]
  [TestCase("w x", "wx")]
  [TestCase("y z", "error: invalid syntax @3")]
  public void IndirectLeftRecursion_Grows(string text, string expected) => Assert.That(Run("Indirect", text), Is.EqualTo(expected));

  /// <summary>Test design 3: every construct of the format, one alternative each.</summary>
  [TestCase("opt", "opt:-")]
  [TestCase("opt x", "opt:x")]
  [TestCase("brk", "brk:null")]
  [TestCase("brk x 1", "brk:dummy")]
  [TestCase("star", "star:0")]
  [TestCase("star a b c", "star:3")]
  [TestCase("plus", "error: invalid syntax @4")]
  [TestCase("plus a b", "plus:2")]
  [TestCase("gather a , b , c", "gather:a|b|c")]
  [TestCase("gather a ,", "error: invalid syntax @10")]
  [TestCase("pos 5", "pos:5")]
  [TestCase("pos x", "error: invalid syntax @0")]
  [TestCase("neg x", "neg:x")]
  [TestCase("neg 5", "error: invalid syntax @0")]
  [TestCase("grp b", "grp:b")]
  [TestCase("grp c", "error: invalid syntax @4")]
  [TestCase("soft match", "soft")]
  [TestCase("seq a 1", "dummy")]
  [TestCase("one 1", "dummy")]
  [TestCase("tmp a = b =", "tmp:[a,b]")]
  [TestCase("new a", "new:1")]
  [TestCase("cast a", "cast:a")]
  [TestCase("typed a b c", "typed:3")]
  [TestCase("hook x", "hook:1")]
  [TestCase("pred x", "pred:x")]
  [TestCase("mixed x", "x")]
  [TestCase("mixed 1 !", "dummy")]
  [TestCase("empty", "empty:00")]
  [TestCase("throw 1", "error: thrown @6")]
  [TestCase("brk2 x 1", "dummy")]
  [TestCase("gat2 a , b", "[a,b]")]
  [TestCase("inl x", "x")]
  [TestCase("hopt y", "y")]
  [TestCase("hopt", "error: invalid syntax @4")]
  [TestCase("desc x .", "x")]
  [TestCase("desc x", "error: expected '.' @6")]
  [TestCase("cast2 x", "x")]
  [TestCase("otyped", "-")]
  [TestCase("word x y", "2:x")]
  [TestCase("word2 z", "z")]
  [TestCase("kws , ,", "2")]
  [TestCase("sk match match", "2")]
  [TestCase("optg y 1", "y")]
  [TestCase("fg .", "error: expected '.' @4")]
  [TestCase("hk a b", "2")]
  [TestCase("cg x", "dummy")]
  public void EveryConstruct_ParsesAsSpecified(string text, string expected) => Assert.That(Run("Constructs", text), Is.EqualTo(expected));

  /// <summary>A soft keyword read by the method <c>@soft_keyword</c> names; guards inside every kind of item, evaluated at generation.</summary>
  [TestCase("Soft", "match x", "match")]
  [TestCase("Soft", "x x", "error: invalid syntax @0")]
  [TestCase("Soft", "case case", "2")]
  [TestCase("GenNest", "nest new a 1 b 2 , 3 x y z", "nest")]
  [TestCase("GenNest", "nest old 1 2 x y z", "nest")]
  [TestCase("GenNest", "nest new 1 2 x z", "error: expected 'y' @15")]
  public void SoftKeywordsAndNestedGuards_Work(string grammar, string text, string expected) => Assert.That(Run(grammar, text), Is.EqualTo(expected));

  /// <summary>After a cut, the rule's later alternatives are not tried.</summary>
  [TestCase("c x", "cut")]
  [TestCase("c 1", "error: invalid syntax @2")]
  [TestCase("d 1", "d")]
  public void ACut_CommitsToItsAlternative(string text, string expected) => Assert.That(Run("Cut", text), Is.EqualTo(expected));

  /// <summary>A missing forced token or group is an error at once, with pegen's messages.</summary>
  [TestCase("f : x", "f")]
  [TestCase("f x", "error: expected ':' @2")]
  [TestCase("g b", "g")]
  [TestCase("g c", "error: expected ('a' | 'b') @2")]
  public void ForcedItems_AreErrorsAtOnce(string text, string expected) => Assert.That(Run("Forced", text), Is.EqualTo(expected));

  /// <summary>#1609: the invalid rules give their message in the second pass, which a valid source never runs.</summary>
  [TestCase("x = 1", "x=1", 0)]
  [TestCase("1 = x", "error: cannot assign to literal @0", 1)]
  [TestCase("a b c d e +", "error: invalid syntax @2", 1)]
  [TestCase("w x", "inner", 0)]
  [TestCase("w 1 2", "error: invalid syntax @4", 1)]
  [TestCase("+", "error: invalid syntax @0", 1)]
  [TestCase("x 1 +", "error: invalid syntax @4", 1)]
  public void TheSecondPass_GivesTheInvalidRulesMessages(string text, string expected, int passes) {
    Assert.That(_parsers["Invalid"](text, null, out var secondPasses), Is.EqualTo(expected));
    Assert.That(secondPasses, Is.EqualTo(passes));
  }

  /// <summary>#1608: a guard with a message refuses with it after its alternative matched; one without skips its alternative.</summary>
  [TestCase("match x :", "3.9", "error: Pattern matching is only supported in Python 3.10 and greater @8")]
  [TestCase("match x :", "3.10", "match")]
  [TestCase("old", "3.8", "old")]
  [TestCase("old", "3.9", "error: invalid syntax @0")]
  [TestCase("5", null, "5")]
  public void RuntimeGuards_CheckTheProfile(string text, string? version, string expected) => Assert.That(Run("Runtime", text, version), Is.EqualTo(expected));

  /// <summary>#1608 test design 2: guards evaluated at generation give the verdicts of the same guards at run time.</summary>
  [TestCase("match x :", "Gen39", "3.9")]
  [TestCase("match x :", "Gen310", "3.10")]
  [TestCase("old", "Gen38", "3.8")]
  [TestCase("old", "Gen39", "3.9")]
  [TestCase("5", "Gen310", "3.10")]
  public void GeneratedGuards_AgreeWithRuntimeGuards(string text, string grammar, string version)
    => Assert.That(Run(grammar, text), Is.EqualTo(Run("Runtime", text, version)));

}
