using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Syntax;

namespace Polypiler.Tests.Syntax;

/// <summary>The runtime of the generated parsers (#1605), driven by a parser written by hand over a token source of the test.</summary>
[TestFixture]
public sealed class ParserBaseTests {

  internal enum Kind { Number, Name, Operator, End }

  /// <summary>Numbers, names, one-character operators; <c>!</c> makes the source throw.</summary>
  internal sealed class Source(string text) : ITokenSource<Token<Kind>> {

    private int _offset;

    public int Reads { get; private set; }

    public Token<Kind> Next() {
      this.Reads++;
      while (this._offset < text.Length && text[this._offset] == ' ')
        this._offset++;

      var start = this._offset;
      if (start == text.Length)
        return Make(Kind.End, start, start);

      var c = text[start];
      if (c == '!')
        throw new InvalidOperationException("lexer error at " + start);

      if (char.IsDigit(c)) {
        while (this._offset < text.Length && char.IsDigit(text[this._offset]))
          this._offset++;

        return Make(Kind.Number, start, this._offset);
      }

      if (char.IsLetter(c)) {
        while (this._offset < text.Length && char.IsLetter(text[this._offset]))
          this._offset++;

        return Make(Kind.Name, start, this._offset);
      }

      this._offset++;
      return Make(Kind.Operator, start, this._offset);

      Token<Kind> Make(Kind kind, int from, int to) => new(kind, new(1, from + 1, from), new(1, to + 1, to), text[from..to]);
    }

  }

  /// <summary>
  /// <c>sum: term ('+' term)*</c>, <c>(memo) term: NUMBER | '(' sum ')'</c>, by hand on the runtime, with a
  /// hook state of its own and counters of the bodies that ran.
  /// </summary>
  internal sealed class TestParser : ParserBase<Token<Kind>, Kind> {

    public const int TermRule = 1;

    public TestParser(string text, int nesting = DefaultMaxNesting) : base(new Source(text)) => this.MaxNesting = nesting;

    public int TermBodies { get; private set; }

    public int Position { get => this.Mark; set => this.Mark = value; }

    public int State { get => this.MemoState; set => this.MemoState = value; }

    public bool Invalid => this.CallInvalidRules;

    public int Farthest => this.FarthestFailure;

    public int Tables => this.MemoTables;

    public TokenBuffer<Token<Kind>> Buffer => this.Tokens;

    public GrammarProfile ProfileUsed { get => this.Profile; set => this.Profile = value; }

    public object? Sum() {
      var mark = this.Mark;
      object? result = null;
      if (this.Term() is not null) {
        while (true) {
          var m = this.Mark;
          if (this.ExpectOperator("+") is null || this.Term() is null) {
            this.Mark = m;
            break;
          }
        }

        result = Dummy;
      } else {
        this.Mark = mark;
      }

      return result;
    }

    public object? Term() {
      if (this.IsMemoized(TermRule, out object? memo))
        return memo;

      var mark = this.Mark;
      this.TermBodies++;
      object? result = this.Expect(Kind.Number);
      if (result is null) {
        this.Mark = mark;
        result = this.Parenthesized();
      }

      this.InsertMemo(TermRule, mark, result);
      return result;
    }

    /// <summary>The one rule that nests: the n-th parenthesis is read at level n.</summary>
    private object? Parenthesized() {
      this.EnterRule();
      var mark = this.Mark;
      object? result = null;
      if (this.ExpectOperator("(") is not null && this.Sum() is not null && this.ExpectOperator(")") is not null)
        result = Dummy;
      else
        this.Mark = mark;

      this.LeaveRule();
      return result;
    }

    public Token<Kind>? Keyword(string text) => this.ExpectKeyword(text);

    public Token<Kind>? Soft(string text) => this.ExpectSoftKeyword(text);

    public Token<Kind>? Of(Kind kind) => this.Expect(kind);

    public Token<Kind> Next => this.Peek();

    public Token<Kind> At(int index) => this.TokenAt(index);

    public bool Ahead(Func<object?> item) => this.EndLookahead(this.BeginLookahead(), item() is not null);

    public Token<Kind> Force(string text) => this.Forced(this.ExpectOperator(text), text);

    public object ForceGroup(object? result, string expected) => this.ForcedResult(result, expected);

    public static bool Opt(object? item) => Optional(item);

    public bool Guarded(string axis, GuardOperator op, string value) => this.Guard(axis, op, value);

    public Exception GuardError(string message) => this.GuardFailed(message);

    public object Parse(Func<object?> start, bool secondPass) => this.Run(start, secondPass);

    public SyntaxErrorException OnNext(string message) => this.RaiseSyntaxErrorOnNextToken(message);

    public SyntaxErrorException Last(string message) => this.RaiseSyntaxError(message);

    public static SyntaxErrorException Range(Token<Kind> start, Token<Kind> end, string message) => RaiseSyntaxErrorKnownRange(start, end, message);

    public void Insert(int rule, int mark, object? result) => this.InsertMemo(rule, mark, result);

    public bool Known(int rule, out object? result) => this.IsMemoized(rule, out result);

    public void Enter() => this.EnterRule();

  }

  private static SyntaxError ErrorOf(TestDelegate action) => Assert.Throws<SyntaxErrorException>(action)!.Error;

  /// <summary>Test design 1: a memoized rule called twice at token 0 runs its body once.</summary>
  [Test]
  public void AMemoizedRule_RunsOncePerPosition() {
    var parser = new TestParser("1 + 2");
    Assert.That(parser.Term(), Is.Not.Null);
    var end = parser.Position;
    parser.Position = 0;
    Assert.That(parser.Term(), Is.Not.Null);
    Assert.That((parser.TermBodies, parser.Position), Is.EqualTo((1, end)));
  }

  /// <summary>A memoized failure is known as a failure and leaves the position where it was.</summary>
  [Test]
  public void AMemoizedFailure_IsKnownAsAFailure() {
    var parser = new TestParser("+");
    Assert.That(parser.Term(), Is.Null);
    Assert.That(parser.Term(), Is.Null);
    Assert.That((parser.TermBodies, parser.Position), Is.EqualTo((1, 0)));
  }

  /// <summary>Test design 4: the same rule under two hook states has an entry each.</summary>
  [Test]
  public void TheMemo_IsKeyedByHookState() {
    var parser = new TestParser("1");
    Assert.That(parser.Tables, Is.Zero);
    parser.Term();
    parser.Position = 0;
    parser.State = 1;
    parser.Term();
    Assert.That((parser.TermBodies, parser.Tables), Is.EqualTo((2, 2)));
    parser.Position = 0;
    parser.Term();
    parser.State = 0;
    parser.Position = 0;
    parser.Term();
    Assert.That(parser.TermBodies, Is.EqualTo(2));
  }

  /// <summary>Memo tables grow with the rule number and the position, and a state never seen has no entry.</summary>
  [Test]
  public void MemoTables_GrowOnDemand() {
    var source = string.Join(" ", Enumerable.Repeat("1", 200));
    var parser = new TestParser(source);
    parser.Position = 150;
    parser.Insert(40, 150, "x");
    Assert.That(parser.Known(40, out var hit), Is.True);
    Assert.That(hit, Is.EqualTo("x"));
    parser.State = 7;
    parser.Position = 150;
    Assert.That(parser.Known(40, out _), Is.False);
    parser.Insert(40, 150, "y");
    parser.Insert(40, 190, "z");
    parser.Position = 190;
    Assert.That((parser.Known(40, out var state), state), Is.EqualTo((true, (object)"z")));
    Assert.That(parser.Known(41, out _), Is.False);
    Assert.That(parser.Known(400, out _), Is.False);
    parser.Position = 199;
    Assert.That(parser.Known(40, out _), Is.False);
    parser.State = 0;
    Assert.That(parser.Known(40, out _), Is.False);
  }

  /// <summary>Test design 2: 1000 nested parentheses parse under the limit 1000; 1001 are refused at the 1001st.</summary>
  [TestCase(1000, false)]
  [TestCase(1001, true)]
  public void Nesting_IsLimited(int depth, bool refused) {
    var source = new string('(', depth) + "1" + new string(')', depth);
    var parser = new TestParser(source, 1000);
    if (!refused) {
      Assert.That(parser.Sum(), Is.Not.Null);
      return;
    }

    var error = Assert.Throws<SyntaxNestingException>(() => parser.Sum())!;
    Assert.That((error.Position.Offset, error.Limit), Is.EqualTo((depth - 1, 1000)));
    Assert.That(error.Message, Does.Contain("nesting deeper than 1000"));
  }

  /// <summary>The default limit is pegen's 1000, as <c>GoParser.MaxNesting</c>.</summary>
  [Test]
  public void TheDefaultLimit_Is1000() {
    var parser = new TestParser("1");
    Assert.That(parser.MaxNesting, Is.EqualTo(1000));
    for (var i = 0; i < 1000; i++)
      parser.Enter();

    Assert.Throws<SyntaxNestingException>(parser.Enter);
  }

  /// <summary>Test design 3: an empty source is its end token and no error.</summary>
  [Test]
  public void AnEmptySource_IsItsEndToken() {
    var parser = new TestParser("");
    Assert.That(parser.Of(Kind.End), Is.Not.Null);
    Assert.That(parser.Sum(), Is.Null);
    Assert.That((parser.Buffer.Count, parser.Buffer.Last.Kind, parser.Farthest), Is.EqualTo((2, Kind.End, 1)));
  }

  /// <summary>Test design 5: an exception of the token source passes unchanged.</summary>
  [Test]
  public void ATokenSourceError_IsPropagatedUnchanged() {
    var parser = new TestParser("1 + !");
    var error = Assert.Throws<InvalidOperationException>(() => parser.Sum())!;
    Assert.That(error.Message, Is.EqualTo("lexer error at 4"));
  }

  /// <summary>Test design 6: after backtracking from token 9 to an error at token 7, the farthest failure is token 9.</summary>
  [Test]
  public void TheFarthestFailure_SurvivesBacktracking() {
    var parser = new TestParser("a b c d e f g h i j k");
    parser.Position = 9;
    Assert.That(parser.Of(Kind.Number), Is.Null);
    parser.Position = 7;
    Assert.That(parser.Of(Kind.Number), Is.Null);
    Assert.That(parser.Farthest, Is.EqualTo(9));
  }

  /// <summary>A failure inside a lookahead is no failure of the parse.</summary>
  [Test]
  public void ALookahead_LeavesThePositionAndTheFarthestFailure() {
    var parser = new TestParser("1 + x");
    Assert.That(parser.Ahead(() => parser.Sum()), Is.True);
    Assert.That((parser.Position, parser.Farthest), Is.EqualTo((0, 0)));
    parser.Position = 2;
    Assert.That(parser.Ahead(() => parser.Term()), Is.False);
    Assert.That((parser.Position, parser.Farthest), Is.EqualTo((2, 0)));
  }

  /// <summary>The token buffer reads lazily and grows past its first array.</summary>
  [Test]
  public void TheTokenBuffer_ReadsOnlyWhatIsAskedFor() {
    var source = new Source(string.Join(" ", Enumerable.Range(0, 100)));
    var buffer = new TokenBuffer<Token<Kind>>(source);
    Assert.That(buffer.Count, Is.Zero);
    Assert.That(buffer.Last.Text, Is.EqualTo("0"));
    Assert.That(buffer[3].Text, Is.EqualTo("3"));
    Assert.That((buffer.Count, source.Reads), Is.EqualTo((4, 4)));
    Assert.That(buffer[99].Text, Is.EqualTo("99"));
    Assert.That(buffer[100].Kind, Is.EqualTo(Kind.End));
    Assert.That(buffer.Last.Kind, Is.EqualTo(Kind.End));
    Assert.Throws<ArgumentNullException>(() => _ = new TokenBuffer<Token<Kind>>(null!));
  }

  /// <summary>Keywords, soft keywords and operators match by text by default; kinds by kind.</summary>
  [Test]
  public void Expectations_MatchAndConsume() {
    var parser = new TestParser("if match + 1");
    Assert.That(parser.Keyword("else"), Is.Null);
    Assert.That(parser.Keyword("if")!.Text, Is.EqualTo("if"));
    Assert.That(parser.Soft("match")!.Kind, Is.EqualTo(Kind.Name));
    Assert.That(parser.Next.Text, Is.EqualTo("+"));
    Assert.That(parser.Force("+").Text, Is.EqualTo("+"));
    Assert.That(parser.Of(Kind.Number)!.Start, Is.EqualTo(new SourcePosition(1, 12, 11)));
    Assert.That(parser.At(0).Text, Is.EqualTo("if"));
  }

  /// <summary>A missing forced token is <c>expected 'x'</c> at the next token, a missing forced group <c>expected (…)</c> at the last token read.</summary>
  [Test]
  public void ForcedItems_RaiseAtOnce() {
    var parser = new TestParser("1 2");
    parser.Position = 1;
    var token = ErrorOf(() => parser.Force(":"));
    Assert.That((token.Message, token.Position.Offset, token.End!.Value.Offset), Is.EqualTo(("expected ':'", 2, 3)));
    var group = ErrorOf(() => parser.ForceGroup(null, "a | b"));
    Assert.That((group.Message, group.Position.Offset), Is.EqualTo(("expected (a | b)", 2)));
    Assert.That(parser.ForceGroup("x", "a | b"), Is.EqualTo("x"));
    Assert.That((TestParser.Opt(null), TestParser.Opt("x")), Is.EqualTo((true, true)));
  }

  /// <summary>The errors at the next token, at the last token read and over a range.</summary>
  [Test]
  public void RaiseHelpers_PlaceTheirErrors() {
    var parser = new TestParser("a b c");
    parser.At(2);
    Assert.That(parser.OnNext("next").Error.Position.Offset, Is.Zero);
    Assert.That(parser.Last("last").Error.Position.Offset, Is.EqualTo(4));
    var range = TestParser.Range(parser.At(0), parser.At(1), "range").Error;
    Assert.That((range.Position.Offset, range.End!.Value.Offset, range.ToString()), Is.EqualTo((0, 3, "1:1: range")));
    Assert.Throws<ArgumentNullException>(() => TestParser.Range(null!, parser.At(0), "x"));
    Assert.Throws<ArgumentNullException>(() => TestParser.Range(parser.At(0), null!, "x"));
    Assert.Throws<ArgumentNullException>(() => _ = new SyntaxErrorException(null!));
  }

  /// <summary>A successful first pass never runs the second.</summary>
  [Test]
  public void Run_ReturnsTheFirstPassesResult() {
    var parser = new TestParser("1 + 2");
    Assert.That(parser.Parse(parser.Sum, true), Is.SameAs(ParserBase<Token<Kind>, Kind>.Dummy));
    Assert.That((parser.SecondPasses, parser.Invalid), Is.EqualTo((0, false)));
  }

  /// <summary>A failed first pass runs the second with the invalid rules, and reports what neither explains at the farthest failure.</summary>
  [Test]
  public void Run_ReportsTheUnexplainedAtTheFarthestFailure() {
    var parser = new TestParser("1 + + 2");
    var seen = new List<bool>();
    var error = ErrorOf(() => parser.Parse(() => {
      seen.Add(parser.Invalid);
      return parser.Sum() is not null && parser.Of(Kind.End) is not null ? "ok" : null;
    }, true));
    Assert.That(seen, Is.EqualTo(new[] { false, true }));
    Assert.That((parser.SecondPasses, error.Message, error.Position.Offset), Is.EqualTo((1, "invalid syntax", 4)));
    Assert.Throws<ArgumentNullException>(() => parser.Parse(null!, false));
  }

  /// <summary>Without invalid rules, a failed parse runs one pass.</summary>
  [Test]
  public void Run_WithoutInvalidRules_RunsOnePass() {
    var parser = new TestParser("+");
    Assert.Throws<SyntaxErrorException>(() => parser.Parse(parser.Sum, false));
    Assert.That(parser.SecondPasses, Is.Zero);
  }

  /// <summary>Guards check the profile; the failed guard's message is raised at the last token read.</summary>
  [Test]
  public void Guards_CheckTheProfile() {
    var parser = new TestParser("x");
    Assert.That(parser.ProfileUsed, Is.SameAs(GrammarProfile.None));
    Assert.That(parser.Guarded("version", GuardOperator.GreaterOrEqual, "3.10"), Is.False);
    parser.ProfileUsed = new GrammarProfile(new Dictionary<string, string> { ["version"] = "3.10" });
    Assert.That(parser.Guarded("version", GuardOperator.GreaterOrEqual, "3.10"), Is.True);
    parser.Next.ToString();
    Assert.That(((SyntaxErrorException)parser.GuardError("too new")).Error.Message, Is.EqualTo("too new"));
  }

  /// <summary>Every comparison of a guard, on versions and on text.</summary>
  [TestCase("version", "3.9", GuardOperator.GreaterOrEqual, "3.10", false)]
  [TestCase("version", "3.10", GuardOperator.GreaterOrEqual, "3.10", true)]
  [TestCase("version", "3.10", GuardOperator.Greater, "3.9", true)]
  [TestCase("version", "3.10", GuardOperator.Greater, "3.10", false)]
  [TestCase("version", "3.9", GuardOperator.Less, "3.10", true)]
  [TestCase("version", "3.10", GuardOperator.Less, "3.10", false)]
  [TestCase("version", "3.10", GuardOperator.LessOrEqual, "3.10", true)]
  [TestCase("version", "3.11", GuardOperator.LessOrEqual, "3.10", false)]
  [TestCase("version", "3.10.0", GuardOperator.Equal, "3.10", true)]
  [TestCase("version", "3", GuardOperator.Equal, "3.1", false)]
  [TestCase("version", "3.10", GuardOperator.NotEqual, "3.10", false)]
  [TestCase("dialect", "vbscript", GuardOperator.Equal, "vbscript", true)]
  [TestCase("dialect", "vba7", GuardOperator.Equal, "vbscript", false)]
  [TestCase("dialect", "vba7", GuardOperator.NotEqual, "vbscript", true)]
  public void Guards_Compare(string axis, string actual, GuardOperator op, string value, bool satisfied) {
    var profile = new GrammarProfile(new Dictionary<string, string> { [axis] = actual });
    Assert.That(profile.Satisfies(axis, op, value), Is.EqualTo(satisfied));
    Assert.That(profile.Values[axis], Is.EqualTo(actual));
  }

  /// <summary>A profile refuses what it cannot compare.</summary>
  [Test]
  public void Profiles_RefuseBadArguments() {
    Assert.Throws<ArgumentNullException>(() => _ = new GrammarProfile(null!));
    Assert.Throws<ArgumentNullException>(() => GrammarProfile.None.Satisfies(null!, GuardOperator.Equal, "1"));
    Assert.Throws<ArgumentNullException>(() => GrammarProfile.None.Satisfies("version", GuardOperator.Equal, null!));
    Assert.Throws<ArgumentNullException>(() => GrammarProfile.CompareVersions(null!, "1"));
    Assert.Throws<ArgumentNullException>(() => GrammarProfile.CompareVersions("1", null!));
    Assert.Throws<FormatException>(() => GrammarProfile.CompareVersions("3.x", "3.1"));
  }

  /// <summary>The value types print as their positions.</summary>
  [Test]
  public void Values_Print() {
    var position = new SourcePosition(3, 7, 40);
    Assert.That(position.ToString(), Is.EqualTo("3:7"));
    Assert.That(new SyntaxError(position, "boom").ToString(), Is.EqualTo("3:7: boom"));
    Assert.That(new Token<Kind>(Kind.Name, position, position, "x").Text, Is.EqualTo("x"));
  }

}
