using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Syntax;

namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>
/// CPython's PEG parser for <c>file</c> input, generated from <c>python.pgram</c> - CPython 3.14.8's
/// <c>Grammar/python.gram</c> with its actions in C# - into <c>PyParser.g.cs</c>. This is the
/// hand-written half: the tokenizer as the token source, the tokens the grammar names by their kind
/// or by a method, the positions and errors as CPython reports them (<c>Parser/pegen_errors.c</c>),
/// and the error reporting of <c>_PyPegen_run_parser</c> around the two passes of the runtime: an
/// error of the tokenizer in the rest of the source has priority, and what no rule explains is
/// reported at the last token the first pass read.
/// </summary>
internal sealed partial class PyParser {

  /// <summary>CPython's <c>MAXSTACK</c>: the rules a parse may nest.</summary>
  public const int MaxStack = 6000;

  private static readonly string[] _softKeywords = ["_", "case", "match", "type"];

  private readonly PyLexer _lexer;
  private readonly byte[] _source;
  private readonly int[] _lineStarts;
  private readonly int _minor;
  private bool _barry;

  /// <summary>Prepares a parse.</summary>
  /// <param name="utf8">The bytes of the source as <see cref="PySource"/> prepares them.</param>
  /// <param name="minor">The minor version of Python 3 whose grammar applies, as <c>feature_version</c> sets it.</param>
  public PyParser(byte[] utf8, int minor) : this(new PyLexer(utf8), utf8, minor) {
  }

  private PyParser(PyLexer lexer, byte[] utf8, int minor) : base(lexer) {
    this._lexer = lexer;
    this._source = utf8;
    this._minor = minor;
    this.MaxNesting = MaxStack;
    var starts = new List<int> { 0 };
    for (var i = 0; i < utf8.Length; i++)
      if (utf8[i] == '\n')
        starts.Add(i + 1);

    this._lineStarts = [.. starts];
  }

  /// <summary>An error of a parse no rule explained, to be thrown as it is.</summary>
  private sealed class Unexplained(PyError error) : Exception(error.Message) {

    public PyError Error { get; } = error;

  }

  /// <summary>Parses the source as a module; throws <see cref="PyError"/> where CPython raises.</summary>
  public PyNode ParseModule() {
    try {
      return this.ParseFile();
    } catch (Unexplained unexplained) {
      throw unexplained.Error;
    } catch (PyError error) when (error.Type is "SyntaxError" or "IndentationError" or "TabError") {
      // An error of either pass stands, unless the tokenizer raises one in the rest of the source.
      throw this.CheckRestOfSource(error);
    }
  }

  /// <inheritdoc />
  protected override Exception UnexplainedError(int lastFetched) => new Unexplained(this.GenericError(this.TokenAt(lastFetched)));

  /// <inheritdoc />
  protected override Exception NestingExceeded() => new PyError("MemoryError", "Parser stack overflowed - Python source too complex to parse", 1, 1, 1, 1);

  /// <inheritdoc />
  protected override Exception ForcedTokenMissing(string expected) => this.ErrorAt(this.Peek(), this.Peek(), $"expected '{expected}'");

  /// <inheritdoc />
  protected override Exception ForcedResultMissing(string expected) => this.Error($"expected ({expected})");

  // ---------------------------------------------------------------------------------------------
  // Tokens

  /// <summary>A keyword by its text.</summary>
  protected override PyToken? ExpectKeyword(string text) {
    var token = this.Peek();
    if (token.Kind != PyTokenKind.Keyword || token.Text != text) {
      this.Fail();
      return null;
    }

    this.Mark++;
    return token;
  }

  /// <summary>An operator or delimiter by its text.</summary>
  protected override PyToken? ExpectOperator(string text) {
    var token = this.Peek();
    if (token.Kind != PyTokenKind.Operator || token.Text != text) {
      this.Fail();
      return null;
    }

    this.Mark++;
    return token;
  }

  /// <summary>A soft keyword as a name: <c>"match"</c> (<c>_PyPegen_expect_soft_keyword</c>).</summary>
  private PyNode? SoftKeyword(string keyword) {
    var token = this.Peek();
    return token.Kind == PyTokenKind.Name && token.Text == keyword ? this.NameToken() : null;
  }

  /// <summary>Any soft keyword as a name (<c>SOFT_KEYWORD</c>, <c>_PyPegen_soft_keyword_token</c>).</summary>
  private PyNode? SoftKeywordToken() {
    var token = this.Peek();
    return token.Kind == PyTokenKind.Name && Array.IndexOf(_softKeywords, token.Text) >= 0 ? this.NameToken() : null;
  }

  /// <summary>The last token consumed that is no NEWLINE, INDENT, DEDENT or ENDMARKER (<c>_PyPegen_get_last_nonnwhitespace_token</c>).</summary>
  private PyToken LastToken() {
    PyToken token = null!;
    for (var m = this.Mark - 1; m >= 0; m--) {
      token = this.TokenAt(m);
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

  /// <summary>From the token a rule started at to the end of the last token consumed (pegen's <c>EXTRA</c>).</summary>
  private PyRange Extra(int mark) {
    var start = this.TokenAt(mark);
    var end = this.LastToken();
    return this.Range(start.Line, start.Column, end.EndLine, end.EndColumn);
  }

  private PyRange Span(PyToken start, PyToken end) => this.Range(start.Line, start.Column, end.EndLine, end.EndColumn);

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
    var token = next ? this.Peek() : this.Tokens.Last;
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
    var line = this.Tokens.Last.Line;
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
  private static string Identifier(string text) {
    var id = text.All(char.IsAscii) ? text : PyUnicode.Nfkc(text);
    if (id is "None" or "True" or "False")
      throw new PyError("ValueError", $"identifier field can't represent '{id}' constant", 1, 1, 1, 1);

    return id;
  }

  /// <summary>A <c>NAME</c> token as a <c>Name</c> expression (<c>_PyPegen_name_token</c>).</summary>
  private PyNode? NameToken() {
    var token = this.Expect(PyTokenKind.Name);
    return token is null ? null : PyAst.Make("Name", this.Span(token, token), Identifier(token.Text), PyAst.Singleton("Load"));
  }

  /// <summary>A <c>NUMBER</c> token as a <c>Constant</c> (<c>_PyPegen_number_token</c>).</summary>
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

}
