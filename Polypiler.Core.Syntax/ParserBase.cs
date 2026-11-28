using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Polypiler.Core.Syntax;

/// <summary>
/// The runtime of a generated parser, modelled on pegen's (<c>Parser/pegen.c</c>): the position in a
/// lazily filled <see cref="TokenBuffer{TToken}"/>, the expectation of tokens, keywords and
/// operators, memo tables per rule allocated on first use and keyed by position and hook state, the
/// nesting limit, the farthest failure, lookahead, forced tokens, guards on the profile and the two
/// passes of pegen's error reporting. The generated half of a parser derives from it; the
/// hand-written half of the same partial class supplies the token source, the hooks and the helpers
/// the actions call.
/// </summary>
/// <typeparam name="TToken">The token type of the language.</typeparam>
/// <typeparam name="TKind">The token kinds of the language.</typeparam>
public abstract class ParserBase<TToken, TKind> where TToken : class, IToken<TKind> where TKind : struct, Enum {

  /// <summary>The nesting limit a parser has unless it sets another, as <c>GoParser.MaxNesting</c>.</summary>
  public const int DefaultMaxNesting = 1000;

  private const int InitialMemoLength = 64;

  private readonly TokenBuffer<TToken> _tokens;
  private MemoTable?[]? _memo;
  private int _level;
  private int _lookahead;
  private int _farthest;

  /// <summary>Starts a parse at the first token of a source.</summary>
  /// <param name="source">The token source.</param>
  protected ParserBase(ITokenSource<TToken> source) => this._tokens = new(source);

  /// <summary>The value of a default action over more than one item: a non-null result that carries nothing (pegen's dummy name).</summary>
  public static object Dummy { get; } = new();

  /// <summary>The number of rules a parse may nest before <see cref="NestingExceeded"/> stops it.</summary>
  public int MaxNesting { get; protected set; } = DefaultMaxNesting;

  /// <summary>How often the second pass ran: once after a failed first pass, never after a successful one.</summary>
  public int SecondPasses { get; private set; }

  /// <summary>The index of the next token.</summary>
  protected int Mark { get; set; }

  /// <summary>Whether the alternatives that consist of an <c>invalid_*</c> rule are tried: only in the second pass.</summary>
  protected bool CallInvalidRules { get; set; }

  /// <summary>The state of the hooks that memo entries are keyed by, for grammars that declare <c>@state</c>.</summary>
  protected int MemoState { get; set; }

  /// <summary>The profile the guards of the grammar are checked against.</summary>
  protected GrammarProfile Profile { get; set; } = GrammarProfile.None;

  /// <summary>The tokens read so far.</summary>
  protected TokenBuffer<TToken> Tokens => this._tokens;

  /// <summary>The farthest token index at which a token was expected and not found, lookaheads left out.</summary>
  protected int FarthestFailure => this._farthest;

  // ---------------------------------------------------------------------------------------------
  // Tokens

  /// <summary>The token at the position, read where it is not yet.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  protected TToken Peek() => this._tokens[this.Mark];

  /// <summary>The token at an index, read where it is not yet.</summary>
  /// <param name="index">The token index.</param>
  protected TToken TokenAt(int index) => this._tokens[index];

  /// <summary>The token of a kind, consumed; <see langword="null"/> where another is next.</summary>
  /// <param name="kind">The kind.</param>
  protected TToken? Expect(TKind kind) {
    var token = this._tokens[this.Mark];
    if (!EqualityComparer<TKind>.Default.Equals(token.Kind, kind)) {
      this.Fail();
      return null;
    }

    this.Mark++;
    return token;
  }

  /// <summary>A keyword (<c>'if'</c> in the grammar), consumed.</summary>
  /// <param name="text">The keyword.</param>
  protected virtual TToken? ExpectKeyword(string text) => this.ExpectText(text);

  /// <summary>A soft keyword (<c>"match"</c> in the grammar), consumed.</summary>
  /// <param name="text">The soft keyword.</param>
  protected virtual TToken? ExpectSoftKeyword(string text) => this.ExpectText(text);

  /// <summary>An operator or delimiter (<c>'+'</c> in the grammar), consumed.</summary>
  /// <param name="text">The operator.</param>
  protected virtual TToken? ExpectOperator(string text) => this.ExpectText(text);

  /// <summary>The next token where its text is the one given, consumed.</summary>
  /// <param name="text">The text.</param>
  protected TToken? ExpectText(string text) {
    var token = this._tokens[this.Mark];
    if (!string.Equals(token.Text, text, StringComparison.Ordinal)) {
      this.Fail();
      return null;
    }

    this.Mark++;
    return token;
  }

  /// <summary>Records a failed expectation at the position, outside lookaheads.</summary>
  protected void Fail() {
    if (this._lookahead == 0 && this.Mark > this._farthest)
      this._farthest = this.Mark;
  }

  /// <summary>A token that must follow (<c>&amp;&amp;'x'</c>): its absence is an error at once.</summary>
  /// <param name="token">The result of expecting it.</param>
  /// <param name="expected">Its spelling, for the message.</param>
  protected TToken Forced(TToken? token, string expected) => token ?? throw this.ForcedTokenMissing(expected);

  /// <summary>A group that must follow (<c>&amp;&amp;(a | b)</c>): its absence is an error at once.</summary>
  /// <typeparam name="T">The result type of the group.</typeparam>
  /// <param name="result">The result of the group.</param>
  /// <param name="expected">The group as the grammar spells it, for the message.</param>
  protected T ForcedResult<T>(T? result, string expected) where T : class => result ?? throw this.ForcedResultMissing(expected);

  /// <summary>The error of a missing forced token: <c>expected 'x'</c> at the next token (<c>_PyPegen_expect_forced_token</c>).</summary>
  /// <param name="expected">The token's spelling.</param>
  protected virtual Exception ForcedTokenMissing(string expected) => this.RaiseSyntaxErrorKnownLocation(this.Peek(), $"expected '{expected}'");

  /// <summary>The error of a missing forced group: <c>expected (a | b)</c> at the last token read (<c>_PyPegen_expect_forced_result</c>).</summary>
  /// <param name="expected">The group's spelling.</param>
  protected virtual Exception ForcedResultMissing(string expected) => this.RaiseSyntaxError($"expected ({expected})");

  /// <summary>Starts a lookahead (<c>&amp;e</c>, <c>!e</c>): the position to come back to.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  protected int BeginLookahead() {
    this._lookahead++;
    return this.Mark;
  }

  /// <summary>Ends a lookahead: back at its position, with whether the item matched.</summary>
  /// <param name="mark">What <see cref="BeginLookahead"/> returned.</param>
  /// <param name="matched">Whether the item matched.</param>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  protected bool EndLookahead(int mark, bool matched) {
    this._lookahead--;
    this.Mark = mark;
    return matched;
  }

  /// <summary>Always true: an optional item (<c>e?</c>) matches whether or not its item does.</summary>
  /// <typeparam name="T">The item's type.</typeparam>
  /// <param name="item">The item's result, unused.</param>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  protected static bool Optional<T>(T? item) where T : class => true;

  // ---------------------------------------------------------------------------------------------
  // Rules

  /// <summary>Enters a rule: one level deeper, refused past <see cref="MaxNesting"/>.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  protected void EnterRule() {
    if (++this._level > this.MaxNesting)
      throw this.NestingExceeded();
  }

  /// <summary>Leaves a rule.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  protected void LeaveRule() => this._level--;

  /// <summary>The error of nesting past the limit: <see cref="SyntaxNestingException"/> at the next token.</summary>
  protected virtual Exception NestingExceeded() => new SyntaxNestingException(this.Peek().Start, this.MaxNesting);

  /// <summary>
  /// Whether a rule's result is known at the position under the current <see cref="MemoState"/>;
  /// where it is, the position moves to where the rule ended. The token at the position is read
  /// first, as pegen's <c>_PyPegen_is_memoized</c> does.
  /// </summary>
  /// <typeparam name="T">The rule's result type.</typeparam>
  /// <param name="rule">The rule's number.</param>
  /// <param name="result">The result, <see langword="null"/> for a failure.</param>
  protected bool IsMemoized<T>(int rule, out T? result) where T : class {
    this.Peek();
    var mark = this.Mark;
    if (this._memo is { } memo && rule < memo.Length && memo[rule]?.Entries(this.MemoState, false) is { } entries
        && mark < entries.Length && entries[mark].End != 0) {
      this.Mark = entries[mark].End - 1;
      result = (T?)entries[mark].Result;
      return true;
    }

    result = null;
    return false;
  }

  /// <summary>Records a rule's result at the position it started at, ending at the current position (<c>_PyPegen_insert_memo</c>).</summary>
  /// <param name="rule">The rule's number.</param>
  /// <param name="mark">The position the rule started at.</param>
  /// <param name="result">The result, <see langword="null"/> for a failure.</param>
  protected void InsertMemo(int rule, int mark, object? result) {
    var memo = this._memo ??= new MemoTable?[Math.Max(rule + 1, 16)];
    if (rule >= memo.Length) {
      Array.Resize(ref memo, Math.Max(rule + 1, memo.Length * 2));
      this._memo = memo;
    }

    var table = memo[rule] ??= new MemoTable();
    var entries = table.Entries(this.MemoState, true)!;
    if (mark >= entries.Length) {
      Array.Resize(ref entries, Math.Max(mark + 1, entries.Length * 2));
      table.Replace(this.MemoState, entries);
    }

    entries[mark] = new(this.Mark + 1, result);
  }

  /// <summary>The number of rules that have a memo table: tables are made on first use only.</summary>
  protected int MemoTables {
    get {
      var count = 0;
      foreach (var table in this._memo ?? [])
        if (table is not null)
          count += table.Count;

      return count;
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Guards

  /// <summary>Whether the profile satisfies a guard of the grammar.</summary>
  /// <param name="axis">The axis.</param>
  /// <param name="op">The comparison.</param>
  /// <param name="value">The value.</param>
  protected bool Guard(string axis, GuardOperator op, string value) => this.Profile.Satisfies(axis, op, value);

  /// <summary>The error of an alternative whose guard fails after it matched, with the guard's <c>@message</c> (pegen's <c>CHECK_VERSION</c>).</summary>
  /// <param name="message">The message.</param>
  protected virtual Exception GuardFailed(string message) => this.RaiseSyntaxError(message);

  // ---------------------------------------------------------------------------------------------
  // Passes and errors

  /// <summary>
  /// Parses with a start rule as pegen does: a first pass without the <c>invalid_*</c> rules, and where
  /// it fails, a second with them from the start, whose actions raise the reference compiler's
  /// messages; what neither explains is <see cref="UnexplainedError"/>.
  /// </summary>
  /// <typeparam name="TResult">The start rule's result type.</typeparam>
  /// <param name="start">The start rule.</param>
  /// <param name="secondPass">Whether the grammar has invalid rules, without which a second pass would only repeat the first.</param>
  protected TResult Run<TResult>(Func<TResult?> start, bool secondPass) where TResult : class {
    ArgumentNullException.ThrowIfNull(start);
    if (start() is { } result)
      return result;

    var last = this._tokens.Count - 1;
    if (secondPass) {
      this._memo = null;
      this.Mark = 0;
      this.CallInvalidRules = true;
      this.SecondPasses++;
      start();
    }

    throw this.UnexplainedError(last);
  }

  /// <summary>The error of a parse no rule explained: <see cref="UnexplainedMessage"/> at the farthest failure.</summary>
  /// <param name="lastFetched">The index of the last token the first pass read, where pegen reports.</param>
  protected virtual Exception UnexplainedError(int lastFetched) {
    var token = this.TokenAt(this._farthest);
    return new SyntaxErrorException(new(token.Start, this.UnexplainedMessage(token)) { End = token.End });
  }

  /// <summary>The message of an unexplained error at a token; <c>invalid syntax</c> unless the grammar sets <c>@unexplained</c>.</summary>
  /// <param name="token">The token.</param>
  protected virtual string UnexplainedMessage(TToken token) => "invalid syntax";

  /// <summary>An error at the last token read (<c>RAISE_SYNTAX_ERROR</c>).</summary>
  /// <param name="message">The message.</param>
  protected SyntaxErrorException RaiseSyntaxError(string message) => this.RaiseSyntaxErrorKnownLocation(this._tokens.Last, message);

  /// <summary>An error at the next token (<c>RAISE_SYNTAX_ERROR_ON_NEXT_TOKEN</c>).</summary>
  /// <param name="message">The message.</param>
  protected SyntaxErrorException RaiseSyntaxErrorOnNextToken(string message) => this.RaiseSyntaxErrorKnownLocation(this.Peek(), message);

  /// <summary>An error at a token (<c>RAISE_SYNTAX_ERROR_KNOWN_LOCATION</c>).</summary>
  /// <param name="token">The token.</param>
  /// <param name="message">The message.</param>
  protected SyntaxErrorException RaiseSyntaxErrorKnownLocation(IToken<TKind> token, string message)
    => RaiseSyntaxErrorKnownRange(token, token, message);

  /// <summary>An error from one token to another (<c>RAISE_SYNTAX_ERROR_KNOWN_RANGE</c>).</summary>
  /// <param name="start">The first token.</param>
  /// <param name="end">The last token.</param>
  /// <param name="message">The message.</param>
  protected static SyntaxErrorException RaiseSyntaxErrorKnownRange(IToken<TKind> start, IToken<TKind> end, string message) {
    ArgumentNullException.ThrowIfNull(start);
    ArgumentNullException.ThrowIfNull(end);
    return new(new(start.Start, message) { End = end.End });
  }

  /// <summary>The memo entries of one rule: one array per hook state, made on first use.</summary>
  private sealed class MemoTable {

    private Entry[]? _plain;
    private Dictionary<int, Entry[]>? _states;

    public int Count => (this._plain is null ? 0 : 1) + (this._states?.Count ?? 0);

    public Entry[]? Entries(int state, bool create) {
      if (state == 0)
        return this._plain ??= create ? new Entry[InitialMemoLength] : null;

      if (this._states is { } states && states.TryGetValue(state, out var entries))
        return entries;

      if (!create)
        return null;

      entries = new Entry[InitialMemoLength];
      (this._states ??= [])[state] = entries;
      return entries;
    }

    public void Replace(int state, Entry[] entries) {
      if (state == 0)
        this._plain = entries;
      else
        this._states![state] = entries;
    }

  }

  /// <summary>A memoized result: where the rule ended plus one (0 for no entry), and the result.</summary>
  private readonly record struct Entry(int End, object? Result);

}
