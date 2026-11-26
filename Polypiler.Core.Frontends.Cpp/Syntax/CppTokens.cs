using System;
using System.Collections.Generic;
using System.Text;

namespace Polypiler.Core.Frontends.Cpp.Syntax;

internal enum CppTokenKind {
  EndOfFile,
  Identifier,
  IntLiteral,
  FloatLiteral,
  CharLiteral,
  StringLiteral,
  LParen,
  RParen,
  LBrace,
  RBrace,
  LBracket,
  RBracket,
  Semicolon,
  Comma,
  Dot,
  Arrow,
  ColonColon,
  Colon,
  Question,
  Ellipsis,
  Plus,
  Minus,
  Star,
  Slash,
  Percent,
  Amp,
  Pipe,
  Caret,
  Tilde,
  Bang,
  Assign,
  Less,
  Greater,
  PlusPlus,
  MinusMinus,
  ShiftLeft,
  ShiftRight,
  LessEqual,
  GreaterEqual,
  EqualEqual,
  BangEqual,
  AmpAmp,
  PipePipe,
  PlusAssign,
  MinusAssign,
  StarAssign,
  SlashAssign,
  PercentAssign,
  AmpAssign,
  PipeAssign,
  CaretAssign,
  ShiftLeftAssign,
  ShiftRightAssign
}

internal readonly record struct CppToken(CppTokenKind Kind, string Text, int Line, int Column) {

  public SourceRange Span(string file) => new(file, this.Line, this.Column, this.Line, this.Column + Math.Max(0, this.Text.Length - 1));

}

internal sealed class CppLexer {

  private readonly string _file;
  private readonly string _source;
  private int _pos;
  private int _line = 1;
  private int _col = 1;

  private CppLexer(string file, string source) {
    this._file = file;
    this._source = source;
  }

  public static IReadOnlyList<CppToken> Tokenize(string file, string source) => new CppLexer(file, source).Run();

  private char Peek(int offset = 0) => this._pos + offset < this._source.Length ? this._source[this._pos + offset] : '\0';

  private char Advance() {
    var ch = this._source[this._pos++];
    if (ch == '\n') {
      ++this._line;
      this._col = 1;
    } else {
      ++this._col;
    }

    return ch;
  }

  private SourceRange Here(int line, int col) => new(this._file, line, col, line, col);

  private CppDiagnosticException SyntaxError(int line, int col, string message)
    => new(Diagnostic.Create(CppFrontend.SyntaxError, this.Here(line, col), message));

  private List<CppToken> Run() {
    var tokens = new List<CppToken>();
    while (this._pos < this._source.Length) {
      this.SkipTrivia();
      if (this._pos >= this._source.Length)
        break;

      var line = this._line;
      var col = this._col;
      var ch = this.Peek();

      if (ch == '#') {
        this.HandlePreprocessor(line, col);
        continue;
      }

      if (char.IsLetter(ch) || ch == '_') {
        tokens.Add(this.ReadIdentifier(line, col));
        continue;
      }

      if (char.IsDigit(ch) || (ch == '.' && char.IsDigit(this.Peek(1)))) {
        tokens.Add(this.ReadNumber(line, col));
        continue;
      }

      if (ch == '\'') {
        tokens.Add(this.ReadChar(line, col));
        continue;
      }

      if (ch == '"') {
        tokens.Add(this.ReadString(line, col));
        continue;
      }

      tokens.Add(this.ReadPunctuation(line, col));
    }

    tokens.Add(new CppToken(CppTokenKind.EndOfFile, "", this._line, this._col));
    return tokens;
  }

  private void SkipTrivia() {
    while (this._pos < this._source.Length) {
      var ch = this.Peek();
      if (char.IsWhiteSpace(ch)) {
        this.Advance();
        continue;
      }

      if (ch == '/' && this.Peek(1) == '/') {
        while (this.Peek() is not ('\n' or '\0'))
          this.Advance();

        continue;
      }

      if (ch == '/' && this.Peek(1) == '*') {
        var line = this._line;
        var col = this._col;
        this.Advance();
        this.Advance();
        var closed = false;
        while (this._pos < this._source.Length) {
          if (this.Peek() == '*' && this.Peek(1) == '/') {
            this.Advance();
            this.Advance();
            closed = true;
            break;
          }

          this.Advance();
        }

        if (!closed)
          throw this.SyntaxError(line, col, "unterminated block comment");

        continue;
      }

      break;
    }
  }

  private void HandlePreprocessor(int line, int col) {
    this.Advance();
    while (this.Peek() is ' ' or '\t')
      this.Advance();

    var start = this._pos;
    while (char.IsLetterOrDigit(this.Peek()) || this.Peek() == '_')
      this.Advance();

    var directive = this._source[start..this._pos];
    while (this.Peek() is not ('\n' or '\0')) {
      if (this.Peek() == '\\' && (this.Peek(1) == '\n' || (this.Peek(1) == '\r' && this.Peek(2) == '\n'))) {
        this.Advance();
        if (this.Peek() == '\r')
          this.Advance();
        this.Advance();
        continue;
      }

      this.Advance();
    }

    if (directive is not ("include" or "pragma"))
      throw new CppDiagnosticException(Diagnostic.Create(CppFrontend.UnsupportedConstruct, this.Here(line, col), $"#{directive}"));
  }

  private CppToken ReadIdentifier(int line, int col) {
    var start = this._pos;
    while (char.IsLetterOrDigit(this.Peek()) || this.Peek() == '_')
      this.Advance();

    return new(CppTokenKind.Identifier, this._source[start..this._pos], line, col);
  }

  private CppToken ReadNumber(int line, int col) {
    var start = this._pos;
    var isFloat = false;
    if (this.Peek() == '0' && (this.Peek(1) is 'x' or 'X' or 'b' or 'B')) {
      this.Advance();
      this.Advance();
      while (char.IsAsciiHexDigit(this.Peek()) || this.Peek() == '\'')
        this.Advance();
    } else {
      while (char.IsDigit(this.Peek()) || this.Peek() == '\'')
        this.Advance();

      if (this.Peek() == '.' && this.Peek(1) != '.') {
        isFloat = true;
        this.Advance();
        while (char.IsDigit(this.Peek()) || this.Peek() == '\'')
          this.Advance();
      }

      if (this.Peek() is 'e' or 'E') {
        isFloat = true;
        this.Advance();
        if (this.Peek() is '+' or '-')
          this.Advance();

        while (char.IsDigit(this.Peek()) || this.Peek() == '\'')
          this.Advance();
      }
    }

    while (char.IsLetter(this.Peek())) {
      if (this.Peek() is 'f' or 'F')
        isFloat = true;

      this.Advance();
    }

    return new(isFloat ? CppTokenKind.FloatLiteral : CppTokenKind.IntLiteral, this._source[start..this._pos], line, col);
  }

  private CppToken ReadChar(int line, int col) {
    this.Advance();
    if (this._pos >= this._source.Length || this.Peek() == '\n')
      throw this.SyntaxError(line, col, "unterminated character literal");

    var ch = this.Peek() == '\\' ? this.ReadEscape(line, col) : this.Advance();
    if (this.Peek() != '\'')
      throw this.SyntaxError(line, col, "unterminated character literal");

    this.Advance();
    return new(CppTokenKind.CharLiteral, ch.ToString(), line, col);
  }

  private CppToken ReadString(int line, int col) {
    this.Advance();
    var builder = new StringBuilder();
    while (this._pos < this._source.Length) {
      var ch = this.Peek();
      if (ch == '"') {
        this.Advance();
        return new(CppTokenKind.StringLiteral, builder.ToString(), line, col);
      }

      if (ch == '\n')
        throw this.SyntaxError(line, col, "unterminated string literal");

      builder.Append(ch == '\\' ? this.ReadEscape(line, col) : this.Advance());
    }

    throw this.SyntaxError(line, col, "unterminated string literal");
  }

  private char ReadEscape(int line, int col) {
    this.Advance();
    if (this._pos >= this._source.Length)
      throw this.SyntaxError(line, col, "unterminated escape sequence");

    var ch = this.Advance();
    return ch switch {
      'n' => '\n',
      'r' => '\r',
      't' => '\t',
      '\\' => '\\',
      '\'' => '\'',
      '"' => '"',
      '0' => '\0',
      'x' => this.ReadHexEscape(line, col),
      _ => throw this.SyntaxError(line, col, $"unknown escape sequence '\\{ch}'")
    };
  }

  private char ReadHexEscape(int line, int col) {
    var value = 0;
    var digits = 0;
    while (digits < 2 && char.IsAsciiHexDigit(this.Peek())) {
      var ch = this.Advance();
      value = (value * 16) + (char.IsDigit(ch) ? ch - '0' : char.ToLowerInvariant(ch) - 'a' + 10);
      ++digits;
    }

    if (digits == 0)
      throw this.SyntaxError(line, col, "invalid hexadecimal escape sequence");

    return (char)value;
  }

  private CppToken ReadPunctuation(int line, int col) {
    var c0 = this.Peek();
    var c1 = this.Peek(1);
    var c2 = this.Peek(2);

    if (c0 == '.' && c1 == '.' && c2 == '.') {
      this.Advance();
      this.Advance();
      this.Advance();
      return new(CppTokenKind.Ellipsis, "...", line, col);
    }

    if (c0 == '<' && c1 == '<' && c2 == '=') {
      this.Advance();
      this.Advance();
      this.Advance();
      return new(CppTokenKind.ShiftLeftAssign, "<<=", line, col);
    }

    if (c0 == '>' && c1 == '>' && c2 == '=') {
      this.Advance();
      this.Advance();
      this.Advance();
      return new(CppTokenKind.ShiftRightAssign, ">>=", line, col);
    }

    var two = (c0, c1) switch {
      (':', ':') => CppTokenKind.ColonColon,
      ('-', '>') => CppTokenKind.Arrow,
      ('+', '+') => CppTokenKind.PlusPlus,
      ('-', '-') => CppTokenKind.MinusMinus,
      ('<', '<') => CppTokenKind.ShiftLeft,
      ('>', '>') => CppTokenKind.ShiftRight,
      ('<', '=') => CppTokenKind.LessEqual,
      ('>', '=') => CppTokenKind.GreaterEqual,
      ('=', '=') => CppTokenKind.EqualEqual,
      ('!', '=') => CppTokenKind.BangEqual,
      ('&', '&') => CppTokenKind.AmpAmp,
      ('|', '|') => CppTokenKind.PipePipe,
      ('+', '=') => CppTokenKind.PlusAssign,
      ('-', '=') => CppTokenKind.MinusAssign,
      ('*', '=') => CppTokenKind.StarAssign,
      ('/', '=') => CppTokenKind.SlashAssign,
      ('%', '=') => CppTokenKind.PercentAssign,
      ('&', '=') => CppTokenKind.AmpAssign,
      ('|', '=') => CppTokenKind.PipeAssign,
      ('^', '=') => CppTokenKind.CaretAssign,
      _ => CppTokenKind.EndOfFile
    };

    if (two != CppTokenKind.EndOfFile) {
      this.Advance();
      this.Advance();
      return new(two, $"{c0}{c1}", line, col);
    }

    var one = c0 switch {
      '(' => CppTokenKind.LParen,
      ')' => CppTokenKind.RParen,
      '{' => CppTokenKind.LBrace,
      '}' => CppTokenKind.RBrace,
      '[' => CppTokenKind.LBracket,
      ']' => CppTokenKind.RBracket,
      ';' => CppTokenKind.Semicolon,
      ',' => CppTokenKind.Comma,
      '.' => CppTokenKind.Dot,
      ':' => CppTokenKind.Colon,
      '?' => CppTokenKind.Question,
      '+' => CppTokenKind.Plus,
      '-' => CppTokenKind.Minus,
      '*' => CppTokenKind.Star,
      '/' => CppTokenKind.Slash,
      '%' => CppTokenKind.Percent,
      '&' => CppTokenKind.Amp,
      '|' => CppTokenKind.Pipe,
      '^' => CppTokenKind.Caret,
      '~' => CppTokenKind.Tilde,
      '!' => CppTokenKind.Bang,
      '=' => CppTokenKind.Assign,
      '<' => CppTokenKind.Less,
      '>' => CppTokenKind.Greater,
      _ => CppTokenKind.EndOfFile
    };

    if (one != CppTokenKind.EndOfFile) {
      this.Advance();
      return new(one, c0.ToString(), line, col);
    }

    throw this.SyntaxError(line, col, $"unexpected character '{c0}'");
  }

}
