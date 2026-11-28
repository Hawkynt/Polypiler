using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Polypiler.Core.Frontends.Python.Ast;

namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>
/// CPython's PEG parser for <c>file</c> input, converted rule by rule from <c>Grammar/python.gram</c>
/// of 3.14, with the actions of <c>Parser/action_helpers.c</c>. As pegen's, it asks the tokenizer for
/// tokens as it needs them, backtracks by resetting the position, memoizes the rules the grammar marks
/// and grows the left-recursive ones as loops. A first pass without the <c>invalid_*</c> rules decides
/// whether the source parses; where it does not, a second pass with them finds CPython's message.
/// </summary>
internal sealed partial class PyParser {

  private const int MaxStack = 6000;

  private readonly PyLexer _lexer;
  private readonly byte[] _source;
  private readonly int[] _lineStarts;
  private readonly List<PyToken> _tokens = [];
  private readonly int _minor;
  private int _mark;
  private int _level;
  private bool _invalid;
  private bool _barry;

  /// <summary>Prepares a parse.</summary>
  /// <param name="utf8">The bytes of the source as <see cref="PySource"/> prepares them.</param>
  /// <param name="minor">The minor version of Python 3 whose grammar applies, as <c>feature_version</c> sets it.</param>
  public PyParser(byte[] utf8, int minor) {
    this._source = utf8;
    this._lexer = new PyLexer(utf8);
    this._minor = minor;
    var starts = new List<int> { 0 };
    for (var i = 0; i < utf8.Length; i++)
      if (utf8[i] == '\n')
        starts.Add(i + 1);

    this._lineStarts = [.. starts];
  }

  /// <summary>Parses the source as a module; throws <see cref="PyError"/> where CPython raises.</summary>
  public PyNode ParseModule() {
    PyNode? module;
    try {
      module = this.File();
    } catch (PyError error) when (error.Type is "SyntaxError" or "IndentationError" or "TabError") {
      // An error of the first pass stands, unless the tokenizer raises one in the rest of the source.
      throw this.CheckRestOfSource(error);
    }

    if (module is not null)
      return module;

    // The second pass: the invalid_* rules raise CPython's specific messages.
    var last = this._tokens[^1];
    foreach (var token in this._tokens)
      token.Memo = null;

    this._mark = 0;
    this._invalid = true;
    try {
      this.File();
    } catch (PyError error) when (error.Type is "SyntaxError" or "IndentationError" or "TabError") {
      throw this.CheckRestOfSource(error);
    }

    throw this.GenericError(last);
  }

  // ---------------------------------------------------------------------------------------------
  // Tokens

  private PyToken Fill() {
    var token = this._lexer.Next();
    this._tokens.Add(token);
    return token;
  }

  private PyToken Peek() => this._mark == this._tokens.Count ? this.Fill() : this._tokens[this._mark];

  private PyToken? Expect(PyTokenKind kind) {
    var token = this.Peek();
    if (token.Kind != kind)
      return null;

    this._mark++;
    return token;
  }

  /// <summary>An operator or a keyword by its text.</summary>
  private PyToken? Expect(string text) {
    var token = this.Peek();
    if (token.Kind is not (PyTokenKind.Operator or PyTokenKind.Keyword) || token.Text != text)
      return null;

    this._mark++;
    return token;
  }

  private bool Check(string text) {
    var mark = this._mark;
    var found = this.Expect(text) is not null;
    this._mark = mark;
    return found;
  }

  private bool Check(PyTokenKind kind) {
    var mark = this._mark;
    var found = this.Expect(kind) is not null;
    this._mark = mark;
    return found;
  }

  private bool CheckAny(params string[] texts) {
    foreach (var text in texts)
      if (this.Check(text))
        return true;

    return false;
  }

  /// <summary>A token that must follow (<c>&amp;&amp;'x'</c>): its absence is an error at once.</summary>
  private PyToken Forced(string text) {
    var token = this.Peek();
    if (token.Kind is PyTokenKind.Operator or PyTokenKind.Keyword && token.Text == text) {
      this._mark++;
      return token;
    }

    throw this.ErrorAt(token, token, $"expected '{text}'");
  }

  /// <summary>A soft keyword as a name: <c>"match"</c>.</summary>
  private PyNode? SoftKeyword(string keyword) {
    var token = this.Peek();
    return token.Kind == PyTokenKind.Name && token.Text == keyword ? this.NameToken() : null;
  }

  private bool CheckSoftKeyword(string keyword) {
    var token = this.Peek();
    return token.Kind == PyTokenKind.Name && token.Text == keyword;
  }

  private static readonly string[] _softKeywords = ["_", "case", "match", "type"];

  /// <summary>Any soft keyword (<c>SOFT_KEYWORD</c>).</summary>
  private bool CheckAnySoftKeyword() {
    var token = this.Peek();
    return token.Kind == PyTokenKind.Name && Array.IndexOf(_softKeywords, token.Text) >= 0;
  }

  /// <summary>The last token consumed that is no NEWLINE, INDENT, DEDENT or ENDMARKER (<c>_PyPegen_get_last_nonnwhitespace_token</c>).</summary>
  private PyToken LastToken() {
    PyToken token = null!;
    for (var m = this._mark - 1; m >= 0; m--) {
      token = this._tokens[m];
      if (token.Kind is not (PyTokenKind.EndMarker or PyTokenKind.Newline or PyTokenKind.Indent or PyTokenKind.Dedent))
        break;
    }

    return token;
  }

  // ---------------------------------------------------------------------------------------------
  // Ranges

  private int CharColumn(int line, int offset) {
    if (offset < 0 || line < 1 || line > this._lineStarts.Length)
      return offset + 1;

    var start = this._lineStarts[line - 1];
    var column = 1;
    for (var i = 0; i < offset && start + i < this._source.Length; i++)
      if ((this._source[start + i] & 0xC0) != 0x80)
        column++;

    return column;
  }

  private PyRange Range(int line, int offset, int endLine, int endOffset)
    => new(line, offset, endLine, endOffset, this.CharColumn(line, offset), this.CharColumn(endLine, endOffset));

  /// <summary>From the start of a token to the end of the last token consumed (pegen's <c>EXTRA</c>).</summary>
  private PyRange Extra(PyToken start) {
    var end = this.LastToken();
    return this.Range(start.Line, start.Column, end.EndLine, end.EndColumn);
  }

  private PyRange Span(PyToken start, PyToken end) => this.Range(start.Line, start.Column, end.EndLine, end.EndColumn);

  private PyRange Span(PyNode start, PyNode end) => this.Range(start.Range!.Line, start.Range.Offset, end.Range!.EndLine, end.Range.EndOffset);


  // ---------------------------------------------------------------------------------------------
  // Rules: entering, memoizing

  private void Enter() {
    if (++this._level > MaxStack)
      throw new PyError("MemoryError", "Parser stack overflowed - Python source too complex to parse", 1, 1, 1, 1);
  }

  private void Leave() => this._level--;

  /// <summary>A rule's result memoized at the token it starts at; the rule restores the position where it fails.</summary>
  private T? Memo<T>(int rule, Func<T?> body) where T : class {
    var token = this.Peek();
    if (token.Memo is { } memo && memo.TryGetValue(rule, out var known)) {
      this._mark = known.End;
      return (T?)known.Result;
    }

    var mark = this._mark;
    this.Enter();
    T? result;
    try {
      result = body();
    } finally {
      this.Leave();
    }

    if (result is null)
      this._mark = mark;

    (token.Memo ??= [])[rule] = (result, this._mark);
    return result;
  }

  /// <summary>Runs a rule, giving back the position where it fails.</summary>
  private T? Rule<T>(Func<T?> body) where T : class {
    var mark = this._mark;
    this.Enter();
    try {
      var result = body();
      if (result is null)
        this._mark = mark;

      return result;
    } finally {
      this.Leave();
    }
  }

  /// <summary>Tries one alternative: the position is given back where it fails.</summary>
  private T? Try<T>(Func<T?> alternative) where T : class {
    var mark = this._mark;
    var result = alternative();
    if (result is null)
      this._mark = mark;

    return result;
  }

  /// <summary>Whether an item could be parsed here, without consuming it (<c>&amp;e</c>).</summary>
  private bool Lookahead<T>(Func<T?> item) where T : class {
    var mark = this._mark;
    var result = item();
    this._mark = mark;
    return result is not null;
  }

  private List<T>? Repeat<T>(Func<T?> item, int minimum) where T : class {
    var items = new List<T>();
    while (true) {
      var mark = this._mark;
      var next = item();
      if (next is null) {
        this._mark = mark;
        break;
      }

      items.Add(next);
    }

    return items.Count >= minimum ? items : null;
  }

  /// <summary><c>s.e+</c>: one or more items, separated, the separators dropped.</summary>
  private List<T>? Gather<T>(string separator, Func<T?> item) where T : class {
    var start = this._mark;
    var first = item();
    if (first is null) {
      this._mark = start;
      return null;
    }

    var items = new List<T> { first };
    while (true) {
      var mark = this._mark;
      if (this.Expect(separator) is null)
        break;

      var next = item();
      if (next is null) {
        this._mark = mark;
        break;
      }

      items.Add(next);
    }

    return items;
  }

  // ---------------------------------------------------------------------------------------------
  // Errors

  private PyError ErrorAt(int line, int offset, int endLine, int endOffset, string message, string type = "SyntaxError") {
    // RAISE_ERROR_KNOWN_LOCATION: columns from bytes to characters on the line of the error.
    var column = offset < 0 ? offset : this.CharColumn(line, offset);
    var endColumn = endOffset < 0 ? endOffset : this.CharColumn(endLine, endOffset);
    return new PyError(type, message, line, column, endLine, endColumn);
  }

  private PyError ErrorAt(PyToken start, PyToken end, string message) => this.ErrorAt(start.Line, start.Column, end.EndLine, end.EndColumn, message);

  private PyError ErrorAt(PyNode start, PyNode end, string message) => this.ErrorAt(start.Range!.Line, start.Range.Offset, end.Range!.EndLine, end.Range.EndOffset, message);

  private PyError ErrorAt(PyNode node, string message) => this.ErrorAt(node, node, message);

  private PyError ErrorAt(PyToken start, PyNode end, string message) => this.ErrorAt(start.Line, start.Column, end.Range!.EndLine, end.Range.EndOffset, message);

  private PyError ErrorAt(PyNode start, PyToken end, string message) => this.ErrorAt(start.Range!.Line, start.Range.Offset, end.EndLine, end.EndColumn, message);

  /// <summary>From a token to the tokenizer's position (<c>RAISE_SYNTAX_ERROR_STARTING_FROM</c>).</summary>
  private PyError ErrorFrom(PyToken start, string message) => this.ErrorAt(start.Line, start.Column, start.Line, -1, message);

  private PyError ErrorFrom(PyNode start, string message) => this.ErrorAt(start.Range!.Line, start.Range.Offset, start.Range.Line, -1, message);

  /// <summary>At the last token read (<c>RAISE_SYNTAX_ERROR</c>), or at the next one (<c>RAISE_SYNTAX_ERROR_ON_NEXT_TOKEN</c>).</summary>
  private PyError Error(string message, bool next = false, string type = "SyntaxError") {
    var token = next ? this.Peek() : this._tokens[^1];
    return token.Column < 0
      ? new PyError(type, message, token.Line, 1, token.EndLine, 1)
      : this.ErrorAt(token.Line, token.Column, token.EndLine, token.EndColumn, message, type);
  }

  private PyError IndentationError(string message) => this.Error(message, type: "IndentationError");

  /// <summary>The error of a parse that failed without a specific one (<c>_Pypegen_set_syntax_error</c>).</summary>
  private PyError GenericError(PyToken last) {
    if (last.Kind == PyTokenKind.Indent || last.Kind == PyTokenKind.Dedent)
      return new PyError("IndentationError", last.Kind == PyTokenKind.Indent ? "unexpected indent" : "unexpected unindent", last.Line, 1, last.Line, 1);

    var error = last.Column < 0
      ? new PyError("SyntaxError", "invalid syntax", last.Line, 1, last.Line, 1)
      : this.ErrorAt(last.Line, last.Column, last.EndLine, last.EndColumn, "invalid syntax");
    return this.CheckRestOfSource(error);
  }

  /// <summary>
  /// The rest of the source tokenized for an error of the tokenizer, which has priority over a syntax
  /// error of the parser (<c>_PyPegen_tokenize_full_source_to_check_for_errors</c>).
  /// </summary>
  private PyError CheckRestOfSource(PyError error) {
    if (!this._lexer.Clean)
      return error;

    // The line of the last token read, by either pass.
    var line = this._tokens[^1].Line;
    try {
      while (this._lexer.Next().Kind != PyTokenKind.EndMarker) {
      }
    } catch (PyError tokenizer) {
      // An error the tokenizer raises itself replaces the parser's, outside an f-string; one it only
      // signals by its state (a continuation, an indentation, the end) counts only as an unclosed bracket.
      if (this._lexer.Raised)
        return this._lexer.InsideString ? error : tokenizer;

      return this._lexer.OpenBracket is { } open && line > open.Line
        ? new PyError("SyntaxError", $"'{open.Bracket}' was never closed", open.Line, open.Column + 1, open.Line, open.Column + 1)
        : error;
    }

    return error;
  }

  /// <summary>A construct of a later version than the one parsed for (<c>CHECK_VERSION</c>).</summary>
  private T Version<T>(int minor, string what, T node) {
    if (this._minor < minor)
      throw this.Error($"{what} only supported in Python 3.{minor} and greater");

    return node;
  }

  // ---------------------------------------------------------------------------------------------
  // Names and numbers

  /// <summary>An identifier as CPython makes it: NFKC where it is not ASCII; <c>None</c>, <c>True</c> and <c>False</c> are refused.</summary>
  private string Identifier(string text) {
    var id = text.All(char.IsAscii) ? text : PyUnicode.Nfkc(text);
    if (id is "None" or "True" or "False")
      throw new PyError("ValueError", $"identifier field can't represent '{id}' constant", 1, 1, 1, 1);

    return id;
  }

  /// <summary>A <c>NAME</c> token as a <c>Name</c> expression.</summary>
  private PyNode? NameToken() {
    var token = this.Expect(PyTokenKind.Name);
    return token is null ? null : PyAst.Make("Name", this.Span(token, token), this.Identifier(token.Text), PyAst.Singleton("Load"));
  }

  /// <summary>A <c>NUMBER</c> token as a <c>Constant</c>.</summary>
  private PyNode? NumberToken() {
    var token = this.Expect(PyTokenKind.Number);
    if (token is null)
      return null;

    object value;
    try {
      value = PyLiterals.Number(token.Text);
    } catch (FormatException exception) {
      throw new PyError("SyntaxError", exception.Message + " - Consider hexadecimal for huge integer literals to avoid decimal conversion limits.", token.Line, 0, token.EndLine, 0);
    }

    return PyAst.Make("Constant", this.Span(token, token), value, null);
  }

  private static List<PyNode?> Nullable(List<PyNode> nodes) => [.. nodes];


  private static List<T> Prepend<T>(T first, List<T>? rest) {
    var list = new List<T> { first };
    if (rest is not null)
      list.AddRange(rest);

    return list;
  }


}
