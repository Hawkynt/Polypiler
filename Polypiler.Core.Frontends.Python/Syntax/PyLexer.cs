using System;
using System.Collections.Generic;
using System.Text;

namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>
/// CPython's tokenizer (<c>Parser/lexer/lexer.c</c>, converted) over the UTF-8 bytes of a source whose
/// newlines are already <c>\n</c> and which ends in one: indentation with tab consistency, implicit
/// and explicit line joining, names, numbers, strings with every prefix, f-strings and t-strings as
/// the token streams of PEP 701 and PEP 750, operators. The parser asks for one token at a time, as
/// CPython's does, so that <c>from __future__ import barry_as_FLUFL</c> changes what follows. An error
/// throws <see cref="PyError"/>.
/// </summary>
internal sealed class PyLexer : Polypiler.Core.Syntax.ITokenSource<PyToken> {

  private const int Eof = -1;
  private const int MaxIndent = 100;
  private const int MaxLevel = 200;
  private const int MaxFStringLevel = 150;
  private const int MaxExprNesting = 3;
  private const int TabSize = 8;

  private enum Done { Ok, Eof, Error, TabSpace, Dedent, TooDeep, LineContinuation }

  private sealed class Mode {
    public bool Regular = true;
    public int CurlyDepth;
    public int CurlyExprStartDepth = -1;
    public byte Quote;
    public int QuoteSize;
    public bool Raw;
    public int Start;
    public int MultiLineStart;
    public int FirstLine;
    public int LastExprStart = -1;
    public bool InDebug;
    public bool InFormatSpec;
    public bool Template;
  }

  private static readonly HashSet<string> _keywords = [
    "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else",
    "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise",
    "return", "try", "while", "with", "yield"];

  private readonly byte[] _b;
  private int _cur;
  private int _inp;
  private int _lineStart;
  private int _start = -1;
  private int _multiLineStart;
  private int _bufStart;
  private int _line;
  private int _col;
  private int _startCol;
  private int _firstLine;
  private bool _atBol = true;
  private int _pendingIndents;
  private int _indent;
  private readonly int[] _indents = new int[MaxIndent];
  private readonly int[] _altIndents = new int[MaxIndent];
  private int _level;
  private readonly byte[] _parens = new byte[MaxLevel];
  private readonly int[] _parenLines = new int[MaxLevel];
  private readonly int[] _parenColumns = new int[MaxLevel];
  private Done _done;
  private readonly List<Mode> _modes = [new Mode()];
  private int _modeIndex;
  private string? _metadata;

  /// <summary>Starts at the beginning of a source.</summary>
  /// <param name="utf8">The UTF-8 bytes; every newline is <c>\n</c> and the last byte is one, or the source is empty.</param>
  public PyLexer(byte[] utf8) => this._b = utf8;

  /// <summary>Whether <c>&lt;&gt;</c> is the inequality operator, after <c>from __future__ import barry_as_FLUFL</c>.</summary>
  public bool BarryAsBdfl { get; set; }

  /// <summary>Whether the tokenizer is inside an f-string or t-string.</summary>
  public bool InsideString => this._modeIndex > 0;

  /// <summary>Whether the f-string or t-string the tokenizer is inside is raw.</summary>
  public bool InsideRawString => this._modeIndex > 0 && this._modes[this._modeIndex].Raw;

  /// <summary>The prefix letter of the string the tokenizer is inside, for messages: <c>t</c> or <c>f</c>.</summary>
  public char StringPrefix => this._modes[this._modeIndex].Template ? 't' : 'f';

  /// <summary>Whether the tokenizer stopped at an error it raised with a message of its own, rather than one its state signals.</summary>
  public bool Raised => this._done == Done.Error;

  /// <summary>Whether the tokenizer has neither stopped at an error nor reached the end of the source.</summary>
  public bool Clean => this._done == Done.Ok;

  /// <summary>The innermost bracket still open, for <c>'(' was never closed</c>; <see langword="null"/> when none is.</summary>
  public (char Bracket, int Line, int Column)? OpenBracket
    => this._level > 0 ? ((char)this._parens[this._level - 1], this._parenLines[this._level - 1], this._parenColumns[this._level - 1]) : null;

  private int NextC() {
    while (true) {
      if (this._cur != this._inp) {
        this._col++;
        return this._b[this._cur++];
      }

      if (this._done != Done.Ok)
        return Eof;

      if (!this.Underflow()) {
        this._cur = this._inp;
        return Eof;
      }

      this._lineStart = this._cur;
    }
  }

  private bool Underflow() {
    if (this._inp >= this._b.Length) {
      this._done = Done.Eof;
      return false;
    }

    var end = Array.IndexOf(this._b, (byte)'\n', this._inp);
    end = end < 0 ? this._b.Length : end + 1;
    if (this._start < 0)
      this._bufStart = this._cur;

    this._lineStart = this._cur;
    this._line++;
    this._col = 0;
    this._inp = end;
    return true;
  }

  private void Backup(int c) {
    if (c == Eof)
      return;

    this._cur--;
    this._col--;
  }

  private string Decode(int from, int to) => Encoding.UTF8.GetString(this._b, from, Math.Max(0, to - from));

  private int CharColumn(int lineStart, int byteColumn) {
    // A 1-based byte column into a 1-based character column, skipping the continuation bytes of UTF-8.
    var column = 1;
    for (var i = 0; i < byteColumn - 1 && lineStart + i < this._b.Length; i++)
      if ((this._b[lineStart + i] & 0xC0) != 0x80)
        column++;

    return column;
  }

  private PyError SyntaxError(string message, int column = -1, int endColumn = -1) {
    // _syntaxerror_range: without a range, the column is the length in characters of the line up to the cursor.
    int start, end;
    if (column == -1) {
      start = Encoding.UTF8.GetString(this._b, this._lineStart, Math.Max(0, this._cur - this._lineStart)).Length;
    } else {
      start = column > 0 ? this.CharColumn(this._lineStart, column) : column;
    }

    end = endColumn == -1 ? start : endColumn > 0 ? this.CharColumn(this._lineStart, endColumn) : endColumn;
    this._done = Done.Error;
    return new PyError("SyntaxError", message, this._line, start, this._line, end);
  }

  /// <summary>The error the parser raises for a tokenizer that stopped without one of its own (<c>_Pypegen_tokenizer_error</c>).</summary>
  private PyError StopError() {
    var column = this._cur - this._lineStart;
    return this._done switch {
      Done.Eof when this.OpenBracket is var (bracket, line, col) => new PyError("SyntaxError", $"'{bracket}' was never closed", line, col + 1, line, col + 1),
      Done.Eof => new PyError("SyntaxError", "unexpected EOF while parsing", this._line, column, this._line, column),
      Done.Dedent => new PyError("IndentationError", "unindent does not match any outer indentation level", this._line, column, this._line, column),
      Done.TabSpace => new PyError("TabError", "inconsistent use of tabs and spaces in indentation", this._line, 1, this._line, 1),
      Done.TooDeep => new PyError("IndentationError", "too many levels of indentation", this._line, 1, this._line, 1),
      _ => new PyError("SyntaxError", "unexpected character after line continuation character", this._line, this._cur - this._bufStart, this._line, this._cur - this._bufStart)
    };
  }

  private PyToken Make(PyTokenKind kind, int start, int end, string? text = null) {
    var isString = kind is PyTokenKind.String or PyTokenKind.FStringMiddle or PyTokenKind.TStringMiddle;
    var source = start >= 0 ? this.Decode(start, end) : "";
    var token = new PyToken {
      Kind = kind,
      Text = text ?? source,
      Source = source,
      Line = isString ? this._firstLine : this._line,
      EndLine = this._line,
      Column = start >= 0 ? this._startCol : -1,
      EndColumn = start >= 0 ? this._col : -1,
      Level = this._level,
      Metadata = this._metadata
    };
    this._metadata = null;
    return token;
  }

  private PyToken Fail() => throw this.StopError();

  /// <summary>The next token; throws the tokenizer's error where there is one.</summary>
  public PyToken Next() {
    var mode = this._modes[this._modeIndex];
    return mode.Regular ? this.NormalMode(mode) : this.StringMode(mode);
  }

  private static bool IsPotentialIdentifierStart(int c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' or >= 128;

  private static bool IsPotentialIdentifierChar(int c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or >= 128;

  private static bool IsDigit(int c) => c is >= '0' and <= '9';

  private static bool IsHexDigit(int c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

  private bool Lookahead(string test) {
    var matched = 0;
    var result = false;
    while (true) {
      var c = this.NextC();
      if (matched == test.Length) {
        result = !IsPotentialIdentifierChar(c);
      } else if (c == test[matched]) {
        matched++;
        continue;
      }

      this.Backup(c);
      while (matched > 0) {
        this.Backup(test[--matched]);
      }

      return result;
    }
  }

  private void VerifyEndOfNumber(int c, string kind) {
    // A number directly followed by one of the keywords that may follow a number in valid code is only a
    // warning; any other letter, digit or underscore is an error.
    var warned = c switch {
      'a' => this.Lookahead("nd"),
      'e' => this.Lookahead("lse"),
      'f' => this.Lookahead("or"),
      'i' => this.PeekIn("fns"),
      'o' => this.Lookahead("r"),
      'n' => this.Lookahead("ot"),
      _ => false
    };
    if (warned)
      return;

    if (c is >= 0 and < 128 && IsPotentialIdentifierChar(c)) {
      this.Backup(c);
      throw this.SyntaxError($"invalid {kind} literal");
    }
  }

  private bool PeekIn(string letters) {
    var c = this.NextC();
    this.Backup(c);
    return c >= 0 && letters.Contains((char)c, StringComparison.Ordinal);
  }

  private void VerifyIdentifier() {
    var text = this.Decode(this._start, this._cur);
    var points = PyUnicode.CodePoints(text);
    var invalid = 0;
    if (points.Count > 0 && PyUnicode.IsXidStart(points[0])) {
      invalid = 1;
      while (invalid < points.Count && PyUnicode.IsXidContinue(points[invalid]))
        invalid++;
    }

    if (invalid >= points.Count)
      return;

    var point = points[invalid];
    if (invalid + 1 < points.Count)
      this._cur = this._start + Encoding.UTF8.GetByteCount(PyUnicode.Text(points.GetRange(0, invalid + 1)));

    throw this.SyntaxError(PyUnicode.IsPrintable(point)
      ? $"invalid character '{PyUnicode.Text([point])}' (U+{point:X4})"
      : $"invalid non-printable character U+{point:X4}");
  }

  private int DecimalTail() {
    int c;
    while (true) {
      do {
        c = this.NextC();
      } while (IsDigit(c));

      if (c != '_')
        break;

      c = this.NextC();
      if (!IsDigit(c)) {
        this.Backup(c);
        throw this.SyntaxError("invalid decimal literal");
      }
    }

    return c;
  }

  private int ContinuationLine() {
    var c = this.NextC();
    if (c != '\n') {
      this._done = Done.LineContinuation;
      return -1;
    }

    c = this.NextC();
    if (c == Eof) {
      this._done = Done.Eof;
      this._cur = this._inp;
      return -1;
    }

    this.Backup(c);
    return c;
  }

  private PyError IncompatiblePrefixes(bool b, bool r, bool u, bool f, bool t) {
    string? message = null;
    if (u && b)
      message = "'u' and 'b' prefixes are incompatible";
    else if (u && r)
      message = "'u' and 'r' prefixes are incompatible";
    else if (u && f)
      message = "'u' and 'f' prefixes are incompatible";
    else if (u && t)
      message = "'u' and 't' prefixes are incompatible";
    else if (b && f)
      message = "'b' and 'f' prefixes are incompatible";
    else if (b && t)
      message = "'b' and 't' prefixes are incompatible";
    else if (f && t)
      message = "'f' and 't' prefixes are incompatible";

    return message is null ? null! : this.SyntaxError(message, this._start + 1 - this._lineStart, this._cur - this._lineStart);
  }

  private PyToken NormalMode(Mode current) {
    int c;
    bool blankLine;
  nextline:
    this._start = -1;
    blankLine = false;

    if (this._atBol) {
      int col = 0, altCol = 0;
      this._atBol = false;
      var contLineCol = 0;
      while (true) {
        c = this.NextC();
        if (c == ' ') {
          col++;
          altCol++;
        } else if (c == '\t') {
          col = (col / TabSize + 1) * TabSize;
          altCol = altCol + 1;
        } else if (c == '\f') {
          col = altCol = 0;
        } else if (c == '\\') {
          contLineCol = contLineCol != 0 ? contLineCol : col;
          if (this.ContinuationLine() == -1)
            return this.Fail();
        } else {
          break;
        }
      }

      this.Backup(c);
      if (c is '#' or '\n' or '\r')
        blankLine = true;

      if (!blankLine && this._level == 0) {
        col = contLineCol != 0 ? contLineCol : col;
        altCol = contLineCol != 0 ? contLineCol : altCol;
        if (col == this._indents[this._indent]) {
          if (altCol != this._altIndents[this._indent])
            return this.IndentError();
        } else if (col > this._indents[this._indent]) {
          if (this._indent + 1 >= MaxIndent) {
            this._done = Done.TooDeep;
            this._cur = this._inp;
            return this.Fail();
          }

          if (altCol <= this._altIndents[this._indent])
            return this.IndentError();

          this._pendingIndents++;
          this._indents[++this._indent] = col;
          this._altIndents[this._indent] = altCol;
        } else {
          while (this._indent > 0 && col < this._indents[this._indent]) {
            this._pendingIndents--;
            this._indent--;
          }

          if (col != this._indents[this._indent]) {
            this._done = Done.Dedent;
            this._cur = this._inp;
            return this.Fail();
          }

          if (altCol != this._altIndents[this._indent])
            return this.IndentError();
        }
      }
    }

    this._start = this._cur;
    this._startCol = this._col;

    if (this._pendingIndents != 0) {
      if (this._pendingIndents < 0) {
        this._pendingIndents++;
        return this.Make(PyTokenKind.Dedent, -1, -1);
      }

      this._pendingIndents--;
      return this.Make(PyTokenKind.Indent, -1, -1);
    }

    c = this.NextC();
    this.Backup(c);

  again:
    this._start = -1;
    do {
      c = this.NextC();
    } while (c is ' ' or '\t' or '\f');

    this._start = this._cur - 1;
    this._startCol = this._col - 1;

    if (c == '#') {
      while (c != Eof && c != '\n')
        c = this.NextC();
    }

    if (c == Eof) {
      if (this._level != 0)
        return this.Fail();

      return this._done == Done.Eof ? this.Make(PyTokenKind.EndMarker, -1, -1) : this.Fail();
    }

    if (IsPotentialIdentifierStart(c)) {
      bool sawB = false, sawR = false, sawU = false, sawF = false, sawT = false;
      while (true) {
        if (!sawB && c is 'b' or 'B')
          sawB = true;
        else if (!sawU && c is 'u' or 'U')
          sawU = true;
        else if (!sawR && c is 'r' or 'R')
          sawR = true;
        else if (!sawF && c is 'f' or 'F')
          sawF = true;
        else if (!sawT && c is 't' or 'T')
          sawT = true;
        else
          break;

        c = this.NextC();
        if (c is '"' or '\'') {
          if (this.IncompatiblePrefixes(sawB, sawR, sawU, sawF, sawT) is { } error)
            throw error;

          return sawF || sawT ? this.FStringStart(c) : this.StringLiteral(c);
        }
      }

      var nonAscii = false;
      while (IsPotentialIdentifierChar(c)) {
        if (c >= 128)
          nonAscii = true;

        c = this.NextC();
      }

      this.Backup(c);
      if (nonAscii)
        this.VerifyIdentifier();

      var name = this.Decode(this._start, this._cur);
      return this.Make(_keywords.Contains(name) ? PyTokenKind.Keyword : PyTokenKind.Name, this._start, this._cur);
    }

    if (c == '\n') {
      this._atBol = true;
      if (blankLine || this._level > 0)
        goto nextline;

      return this.Make(PyTokenKind.Newline, this._start, this._cur - 1, "");
    }

    if (c == '.') {
      c = this.NextC();
      if (IsDigit(c))
        return this.Fraction(c);

      if (c == '.') {
        c = this.NextC();
        if (c == '.')
          return this.Make(PyTokenKind.Operator, this._start, this._cur);

        this.Backup(c);
        this.Backup('.');
      } else {
        this.Backup(c);
      }

      return this.Make(PyTokenKind.Operator, this._start, this._cur);
    }

    if (IsDigit(c))
      return this.Number(c);

    if (c is '\'' or '"')
      return this.StringLiteral(c);

    if (c == '\\') {
      if (this.ContinuationLine() == -1)
        return this.Fail();

      goto again;
    }

    var isPunctuation = c is ':' or '}' or '!' or '{';
    if (isPunctuation && this.InsideString && current.CurlyExprStartDepth >= 0) {
      var cursor = current.CurlyDepth - (c != '{' ? 1 : 0);
      var inFormatSpec = current.InFormatSpec;
      var cursorInFormatWithDebug = cursor == 1 && (current.InDebug || inFormatSpec);
      var cursorValid = cursor == 0 || cursorInFormatWithDebug;
      if (cursorValid && c != '{')
        this.SetStringExpression(current);

      if (c == ':' && cursor == current.CurlyExprStartDepth) {
        current.Regular = false;
        current.InFormatSpec = true;
        return this.Make(PyTokenKind.Operator, this._start, this._cur);
      }
    }

    {
      var c2 = this.NextC();
      var two = TwoChars(c, c2);
      if (c == '<' && c2 == '>' && !this.BarryAsBdfl)
        two = null;

      if (two is not null) {
        var c3 = this.NextC();
        var three = ThreeChars(c, c2, c3);
        if (three is not null)
          two = three;
        else
          this.Backup(c3);

        return this.Make(PyTokenKind.Operator, this._start, this._cur, two);
      }

      this.Backup(c2);
    }

    switch (c) {
      case '(' or '[' or '{':
        if (this._level >= MaxLevel)
          throw this.SyntaxError("too many nested parentheses");

        this._parens[this._level] = (byte)c;
        this._parenLines[this._level] = this._line;
        this._parenColumns[this._level] = this._start - this._lineStart;
        this._level++;
        if (this.InsideString)
          current.CurlyDepth++;

        break;
      case ')' or ']' or '}':
        if (this.InsideString && current.CurlyDepth == 0 && c == '}')
          throw this.SyntaxError($"{this.StringPrefix}-string: single '}}' is not allowed");

        if (this._level == 0)
          throw this.SyntaxError($"unmatched '{(char)c}'");

        this._level--;
        int opening = this._parens[this._level];
        if (!((opening == '(' && c == ')') || (opening == '[' && c == ']') || (opening == '{' && c == '}'))) {
          if (this.InsideString && opening == '{' && current.CurlyDepth - 1 == current.CurlyExprStartDepth)
            throw this.SyntaxError($"{this.StringPrefix}-string: unmatched '{(char)c}'");

          throw this._parenLines[this._level] != this._line
            ? this.SyntaxError($"closing parenthesis '{(char)c}' does not match opening parenthesis '{(char)opening}' on line {this._parenLines[this._level]}")
            : this.SyntaxError($"closing parenthesis '{(char)c}' does not match opening parenthesis '{(char)opening}'");
        }

        if (this.InsideString) {
          current.CurlyDepth--;
          if (current.CurlyDepth < 0)
            throw this.SyntaxError($"{this.StringPrefix}-string: unmatched '{(char)c}'");

          if (c == '}' && current.CurlyDepth == current.CurlyExprStartDepth) {
            current.CurlyExprStartDepth--;
            current.Regular = false;
            current.InFormatSpec = false;
            current.InDebug = false;
          }
        }

        break;
    }

    if (c < 32 || c == 127)
      throw this.SyntaxError($"invalid non-printable character U+{c:X4}");

    if (c == '=' && current.CurlyDepth - current.CurlyExprStartDepth == 1)
      current.InDebug = true;

    var one = OneChar(c);
    return this.Make(one is null ? PyTokenKind.Unknown : PyTokenKind.Operator, this._start, this._cur);
  }

  private PyToken IndentError() {
    this._done = Done.TabSpace;
    this._cur = this._inp;
    return this.Fail();
  }

  private PyToken Number(int c) {
    if (c == '0') {
      c = this.NextC();
      if (c is 'x' or 'X') {
        c = this.NextC();
        do {
          if (c == '_')
            c = this.NextC();

          if (!IsHexDigit(c)) {
            this.Backup(c);
            throw this.SyntaxError("invalid hexadecimal literal");
          }

          do {
            c = this.NextC();
          } while (IsHexDigit(c));
        } while (c == '_');

        this.VerifyEndOfNumber(c, "hexadecimal");
      } else if (c is 'o' or 'O') {
        c = this.NextC();
        do {
          if (c == '_')
            c = this.NextC();

          if (c is < '0' or >= '8') {
            if (IsDigit(c))
              throw this.SyntaxError($"invalid digit '{(char)c}' in octal literal");

            this.Backup(c);
            throw this.SyntaxError("invalid octal literal");
          }

          do {
            c = this.NextC();
          } while (c is >= '0' and < '8');
        } while (c == '_');

        if (IsDigit(c))
          throw this.SyntaxError($"invalid digit '{(char)c}' in octal literal");

        this.VerifyEndOfNumber(c, "octal");
      } else if (c is 'b' or 'B') {
        c = this.NextC();
        do {
          if (c == '_')
            c = this.NextC();

          if (c is not ('0' or '1')) {
            if (IsDigit(c))
              throw this.SyntaxError($"invalid digit '{(char)c}' in binary literal");

            this.Backup(c);
            throw this.SyntaxError("invalid binary literal");
          }

          do {
            c = this.NextC();
          } while (c is '0' or '1');
        } while (c == '_');

        if (IsDigit(c))
          throw this.SyntaxError($"invalid digit '{(char)c}' in binary literal");

        this.VerifyEndOfNumber(c, "binary");
      } else {
        var nonZero = false;
        while (true) {
          if (c == '_') {
            c = this.NextC();
            if (!IsDigit(c)) {
              this.Backup(c);
              throw this.SyntaxError("invalid decimal literal");
            }
          }

          if (c != '0')
            break;

          c = this.NextC();
        }

        var zerosEnd = this._cur;
        if (IsDigit(c)) {
          nonZero = true;
          c = this.DecimalTail();
        }

        if (c == '.') {
          c = this.NextC();
          return this.Fraction(c);
        }

        if (c is 'e' or 'E')
          return this.Exponent(c);

        if (c is 'j' or 'J')
          return this.Imaginary();

        if (nonZero) {
          this.Backup(c);
          throw this.SyntaxError("leading zeros in decimal integer literals are not permitted; use an 0o prefix for octal integers",
            this._start + 1 - this._lineStart, zerosEnd - this._lineStart);
        }

        this.VerifyEndOfNumber(c, "decimal");
      }
    } else {
      c = this.DecimalTail();
      if (c == '.') {
        c = this.NextC();
        return this.Fraction(c);
      }

      if (c is 'e' or 'E')
        return this.Exponent(c);

      if (c is 'j' or 'J')
        return this.Imaginary();

      this.VerifyEndOfNumber(c, "decimal");
    }

    this.Backup(c);
    return this.Make(PyTokenKind.Number, this._start, this._cur);
  }

  private PyToken Fraction(int c) {
    if (IsDigit(c))
      c = this.DecimalTail();

    if (c is 'e' or 'E')
      return this.Exponent(c);

    if (c is 'j' or 'J')
      return this.Imaginary();

    this.VerifyEndOfNumber(c, "decimal");
    this.Backup(c);
    return this.Make(PyTokenKind.Number, this._start, this._cur);
  }

  private PyToken Exponent(int e) {
    var c = this.NextC();
    if (c is '+' or '-') {
      c = this.NextC();
      if (!IsDigit(c)) {
        this.Backup(c);
        throw this.SyntaxError("invalid decimal literal");
      }
    } else if (!IsDigit(c)) {
      this.Backup(c);
      this.VerifyEndOfNumber(e, "decimal");
      this.Backup(e);
      return this.Make(PyTokenKind.Number, this._start, this._cur);
    }

    c = this.DecimalTail();
    if (c is 'j' or 'J')
      return this.Imaginary();

    this.VerifyEndOfNumber(c, "decimal");
    this.Backup(c);
    return this.Make(PyTokenKind.Number, this._start, this._cur);
  }

  private PyToken Imaginary() {
    var c = this.NextC();
    this.VerifyEndOfNumber(c, "imaginary");
    this.Backup(c);
    return this.Make(PyTokenKind.Number, this._start, this._cur);
  }

  private PyToken FStringStart(int quote) {
    var quoteSize = 1;
    this._firstLine = this._line;
    this._multiLineStart = this._lineStart;
    var afterQuote = this.NextC();
    if (afterQuote == quote) {
      var afterAfterQuote = this.NextC();
      if (afterAfterQuote == quote) {
        quoteSize = 3;
      } else {
        this.Backup(afterAfterQuote);
        this.Backup(afterQuote);
      }
    }

    if (afterQuote != quote)
      this.Backup(afterQuote);

    if (this._modeIndex + 1 >= MaxFStringLevel)
      throw this.SyntaxError("too many nested f-strings or t-strings");

    var first = char.ToLowerInvariant((char)this._b[this._start]);
    var second = char.ToLowerInvariant((char)this._b[this._start + 1]);
    var mode = new Mode {
      Regular = false,
      Quote = (byte)quote,
      QuoteSize = quoteSize,
      Start = this._start,
      MultiLineStart = this._lineStart,
      FirstLine = this._line,
      Raw = first == 'r' || second == 'r',
      Template = first == 't' || (first == 'r' && second == 't')
    };
    this._modeIndex++;
    if (this._modes.Count <= this._modeIndex)
      this._modes.Add(mode);
    else
      this._modes[this._modeIndex] = mode;

    return this.Make(mode.Template ? PyTokenKind.TStringStart : PyTokenKind.FStringStart, this._start, this._cur);
  }

  private PyToken StringLiteral(int quote) {
    var quoteSize = 1;
    var endQuoteSize = 0;
    var hasEscapedQuote = false;
    this._firstLine = this._line;
    this._multiLineStart = this._lineStart;

    var c = this.NextC();
    if (c == quote) {
      c = this.NextC();
      if (c == quote)
        quoteSize = 3;
      else
        endQuoteSize = 1;
    }

    if (c != quote)
      this.Backup(c);

    while (endQuoteSize != quoteSize) {
      c = this.NextC();
      if (c == Eof || (quoteSize == 1 && c == '\n')) {
        this._cur = this._start + 1;
        this._lineStart = this._multiLineStart;
        var start = this._line;
        this._line = this._firstLine;
        if (this.InsideString) {
          var mode = this._modes[this._modeIndex];
          if (mode.Quote == quote && mode.QuoteSize == quoteSize)
            throw this.SyntaxError($"{this.StringPrefix}-string: expecting '}}'");
        }

        if (quoteSize == 3)
          throw this.SyntaxError($"unterminated triple-quoted string literal (detected at line {start})");

        throw this.SyntaxError(hasEscapedQuote
          ? $"unterminated string literal (detected at line {start}); perhaps you escaped the end quote?"
          : $"unterminated string literal (detected at line {start})");
      }

      if (c == quote) {
        endQuoteSize++;
      } else {
        endQuoteSize = 0;
        if (c == '\\') {
          c = this.NextC();
          if (c == quote)
            hasEscapedQuote = true;
        }
      }
    }

    return this.Make(PyTokenKind.String, this._start, this._cur);
  }

  /// <summary>Records the source of the expression a <c>=</c>, <c>!</c>, <c>:</c> or <c>}</c> ends, without its comments (<c>set_ftstring_expr</c>).</summary>
  private void SetStringExpression(Mode mode) {
    if (!(mode.InDebug || mode.Template) || this._metadata is not null)
      return;

    var expression = this._b.AsSpan(mode.LastExprStart, this._start - mode.LastExprStart);
    var hashDetected = false;
    var inString = false;
    byte quote = 0;
    for (var i = 0; i < expression.Length; i++) {
      var ch = expression[i];
      if (ch == '\\') {
        i++;
        continue;
      }

      if (ch is (byte)'"' or (byte)'\'') {
        if (!inString) {
          inString = true;
          quote = ch;
        } else if (ch == quote) {
          inString = false;
        }

        continue;
      }

      if (ch == '#' && !inString) {
        hashDetected = true;
        break;
      }
    }

    if (!hashDetected) {
      this._metadata = Encoding.UTF8.GetString(expression);
      return;
    }

    var result = new List<byte>(expression.Length);
    inString = false;
    quote = 0;
    for (var i = 0; i < expression.Length; i++) {
      var ch = expression[i];
      if (ch == '\\') {
        result.Add(ch);
        i++;
        if (i < expression.Length)
          result.Add(expression[i]);
      } else if (ch is (byte)'"' or (byte)'\'') {
        if (!inString) {
          inString = true;
          quote = ch;
        } else if (ch == quote) {
          inString = false;
        }

        result.Add(ch);
      } else if (ch == '#' && !inString) {
        while (i < expression.Length && expression[i] != '\n')
          i++;

        if (i < expression.Length)
          result.Add((byte)'\n');
      } else {
        result.Add(ch);
      }
    }

    this._metadata = Encoding.UTF8.GetString([.. result]);
  }

  private PyToken StringMode(Mode current) {
    var endQuoteSize = 0;
    var unicodeEscape = false;
    this._start = this._cur;
    this._firstLine = this._line;
    this._startCol = this._col;
    var middle = current.Template ? PyTokenKind.TStringMiddle : PyTokenKind.FStringMiddle;

    var startChar = this.NextC();
    if (startChar == '{') {
      var peek1 = this.NextC();
      this.Backup(peek1);
      if (peek1 != '{')
        current.LastExprStart = this._cur;

      this.Backup(startChar);
      if (peek1 != '{') {
        current.CurlyExprStartDepth++;
        if (current.CurlyExprStartDepth >= MaxExprNesting)
          throw this.SyntaxError($"{this.StringPrefix}-string: expressions nested too deeply");

        current.Regular = true;
        return this.NormalMode(current);
      }
    } else {
      this.Backup(startChar);
    }

    for (var i = 0; i < current.QuoteSize; i++) {
      var quote = this.NextC();
      if (quote != current.Quote) {
        this.Backup(quote);
        goto middle;
      }
    }

    {
      var end = this.Make(current.Template ? PyTokenKind.TStringEnd : PyTokenKind.FStringEnd, this._start, this._cur);
      this._modeIndex--;
      return end;
    }

  middle:
    this._multiLineStart = this._lineStart;
    while (endQuoteSize != current.QuoteSize) {
      var c = this.NextC();
      var inFormatSpec = current.InFormatSpec && current.CurlyExprStartDepth >= 0;
      if (c == Eof || (current.QuoteSize == 1 && c == '\n')) {
        if (inFormatSpec && c == '\n') {
          if (current.QuoteSize == 1)
            throw this.SyntaxError($"{this.StringPrefix}-string: newlines are not allowed in format specifiers for single quoted {this.StringPrefix}-strings");

          this.Backup(c);
          current.Regular = true;
          current.InFormatSpec = false;
          return this.Make(middle, this._start, this._cur);
        }

        this._cur = current.Start + 1;
        this._lineStart = current.MultiLineStart;
        var start = this._line;
        this._line = current.FirstLine;
        throw current.QuoteSize == 3
          ? this.SyntaxError($"unterminated triple-quoted {this.StringPrefix}-string literal (detected at line {start})")
          : this.SyntaxError($"unterminated {this.StringPrefix}-string literal (detected at line {start})");
      }

      if (c == current.Quote) {
        endQuoteSize++;
        continue;
      }

      endQuoteSize = 0;
      if (c == '{') {
        var peek = this.NextC();
        if (peek != '{' || inFormatSpec) {
          this.Backup(peek);
          current.LastExprStart = this._cur;
          this.Backup(c);
          current.CurlyExprStartDepth++;
          if (current.CurlyExprStartDepth >= MaxExprNesting)
            throw this.SyntaxError($"{this.StringPrefix}-string: expressions nested too deeply");

          current.Regular = true;
          current.InFormatSpec = false;
          return this.Make(middle, this._start, this._cur);
        }

        return this.Make(middle, this._start, this._cur - 1);
      }

      if (c == '}') {
        if (unicodeEscape)
          return this.Make(middle, this._start, this._cur);

        var peek = this.NextC();
        if (peek == '}' && !inFormatSpec && current.CurlyDepth == 0)
          return this.Make(middle, this._start, this._cur - 1);

        this.Backup(peek);
        this.Backup(c);
        current.Regular = true;
        current.InFormatSpec = false;
        return this.Make(middle, this._start, this._cur);
      }

      if (c == '\\') {
        var peek = this.NextC();
        if (peek is '{' or '}') {
          this.Backup(peek);
          continue;
        }

        if (!current.Raw && peek == 'N') {
          peek = this.NextC();
          if (peek == '{')
            unicodeEscape = true;
          else
            this.Backup(peek);
        }
      }
    }

    for (var i = 0; i < current.QuoteSize; i++)
      this.Backup(current.Quote);

    return this.Make(middle, this._start, this._cur);
  }

  private static string? OneChar(int c) => c switch {
    '!' or '%' or '&' or '(' or ')' or '*' or '+' or ',' or '-' or '.' or '/' or ':' or ';' or '<' or '=' or '>' or '@' or '[' or ']' or '^' or '{' or '|' or '}' or '~' => ((char)c).ToString(),
    _ => null
  };

  private static string? TwoChars(int c1, int c2) => (c1, c2) switch {
    ('!', '=') => "!=",
    ('%', '=') => "%=",
    ('&', '=') => "&=",
    ('*', '*') => "**",
    ('*', '=') => "*=",
    ('+', '=') => "+=",
    ('-', '=') => "-=",
    ('-', '>') => "->",
    ('/', '/') => "//",
    ('/', '=') => "/=",
    (':', '=') => ":=",
    ('<', '<') => "<<",
    ('<', '=') => "<=",
    ('<', '>') => "!=",
    ('=', '=') => "==",
    ('>', '=') => ">=",
    ('>', '>') => ">>",
    ('@', '=') => "@=",
    ('^', '=') => "^=",
    ('|', '=') => "|=",
    _ => null
  };

  private static string? ThreeChars(int c1, int c2, int c3) => (c1, c2, c3) switch {
    ('*', '*', '=') => "**=",
    ('/', '/', '=') => "//=",
    ('<', '<', '=') => "<<=",
    ('>', '>', '=') => ">>=",
    _ => null
  };

}
