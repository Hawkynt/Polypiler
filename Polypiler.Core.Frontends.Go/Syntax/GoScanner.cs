using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Polypiler.Core.Frontends.Go.Syntax;

/// <summary>An error of the Go syntax - of the scanner or of the parser - at a byte offset of the source.</summary>
internal sealed class GoSyntaxException(int offset, string message) : Exception(message) {

  /// <summary>The byte offset the error is at.</summary>
  public int Offset { get; } = offset;

}

/// <summary>The bytes of a Go source with the offsets its lines start at, for the 1-based line and byte column of an offset.</summary>
internal sealed class GoSource {

  private readonly List<int> _lines = [0];

  /// <summary>Reads a source.</summary>
  /// <param name="text">The text, which is encoded as UTF-8 as Go reads it.</param>
  public GoSource(string text) : this(Encoding.UTF8.GetBytes(text)) {
  }

  /// <summary>Reads a source from its bytes, which Go reads as UTF-8.</summary>
  /// <param name="bytes">The bytes.</param>
  public GoSource(byte[] bytes) {
    this.Bytes = bytes;
    for (var index = 0; index < this.Bytes.Length; ++index)
      if (this.Bytes[index] == '\n')
        this._lines.Add(index + 1);
  }

  /// <summary>The UTF-8 bytes.</summary>
  public byte[] Bytes { get; }

  /// <summary>The 1-based line and byte column of an offset.</summary>
  public (int Line, int Column) Position(int offset) {
    var line = this._lines.BinarySearch(offset);
    if (line < 0)
      line = ~line - 1;

    return (line + 1, offset - this._lines[line] + 1);
  }

  /// <summary>The text of a range of bytes.</summary>
  public string Text(int start, int end) => Encoding.UTF8.GetString(this.Bytes, start, end - start);

}

/// <summary>
/// The scanner of Go (The Go Programming Language Specification, "Lexical elements"), converted from
/// <c>go/scanner</c> of the Go distribution (BSD-3-Clause, see <c>THIRD-PARTY-NOTICE.Go.txt</c>):
/// identifiers, keywords, operators, the integer, floating-point, imaginary, rune and string literals
/// in every form of the specification, comments, and the automatic semicolon after a line's last
/// token where that token can end a statement. The first error ends the scan with a
/// <see cref="GoSyntaxException"/>, which decides the verdict as <c>go/parser</c>'s first error does.
/// Comments are skipped; <c>//line</c> directives are not applied.
/// </summary>
internal sealed class GoScanner {

  private const int Eof = -1;
  private const int Bom = 0xFEFF;

  private readonly GoSource _source;
  private readonly byte[] _src;
  private int _ch = ' ';
  private int _offset;
  private int _readOffset;
  private bool _insertSemi;

  /// <summary>Starts a scan at the beginning of a source, a byte order mark there skipped.</summary>
  public GoScanner(GoSource source) {
    this._source = source;
    this._src = source.Bytes;
    this.Next();
    if (this._ch == Bom)
      this.Next();
  }

  /// <summary>The offset just after the last token scanned.</summary>
  public int End { get; private set; }

  /// <summary>The expression of the first <c>//go:build</c> line before the first token, or <see langword="null"/>.</summary>
  public string? BuildConstraint { get; private set; }

  private bool _started;

  private static GoSyntaxException Error(int offset, string message) => new(offset, message);

  private void Next() {
    if (this._readOffset >= this._src.Length) {
      this._offset = this._src.Length;
      this._ch = Eof;
      return;
    }

    this._offset = this._readOffset;
    int rune = this._src[this._readOffset];
    var width = 1;
    if (rune == 0)
      throw Error(this._offset, "illegal character NUL");

    if (rune >= 0x80) {
      if (System.Text.Rune.DecodeFromUtf8(this._src.AsSpan(this._readOffset), out var decoded, out width) != System.Buffers.OperationStatus.Done)
        throw Error(this._offset, "illegal UTF-8 encoding");

      rune = decoded.Value;
      if (rune == Bom && this._offset > 0)
        throw Error(this._offset, "illegal byte order mark");
    }

    this._readOffset += width;
    this._ch = rune;
  }

  private int Peek() => this._readOffset < this._src.Length ? this._src[this._readOffset] : 0;

  private static bool IsLetter(int ch) => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_'
    || ch >= 0x80 && System.Text.Rune.IsLetter(new System.Text.Rune(ch));

  private static bool IsDigit(int ch) => IsDecimal(ch) || ch >= 0x80 && System.Text.Rune.GetUnicodeCategory(new System.Text.Rune(ch)) == UnicodeCategory.DecimalDigitNumber;

  private static bool IsDecimal(int ch) => ch is >= '0' and <= '9';

  private static bool IsHex(int ch) => ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

  private static int Lower(int ch) => ('a' - 'A') | ch;

  private static int DigitValue(int ch) => ch switch {
    >= '0' and <= '9' => ch - '0',
    _ when Lower(ch) is >= 'a' and <= 'f' => Lower(ch) - 'a' + 10,
    _ => 16
  };

  /// <summary>Skips a comment whose first <c>/</c> is consumed; the offset of the first newline inside a general comment, or 0.</summary>
  private int SkipComment() {
    var start = this._offset - 1;
    if (this._ch == '/') {
      while (this._ch != '\n' && this._ch >= 0)
        this.Next();

      // A build constraint counts only before the package clause, as go/parser reads it.
      var text = this._source.Text(start, this._offset);
      if (!this._started && text.StartsWith("//go:build", StringComparison.Ordinal) && (text.Length == 10 || text[10] is ' ' or '\t'))
        this.BuildConstraint ??= text[10..].Trim();

      return 0;
    }

    var newline = 0;
    this.Next();
    while (this._ch >= 0) {
      var ch = this._ch;
      if (ch == '\n' && newline == 0)
        newline = this._offset;

      this.Next();
      if (ch == '*' && this._ch == '/') {
        this.Next();
        return newline;
      }
    }

    throw Error(start, "comment not terminated");
  }

  private string ScanIdentifier() {
    var start = this._offset;
    while (IsLetter(this._ch) || IsDigit(this._ch))
      this.Next();

    return this._source.Text(start, this._offset);
  }

  private int Digits(int @base, ref int invalid) {
    var separators = 0;
    if (@base <= 10) {
      var max = '0' + @base;
      while (IsDecimal(this._ch) || this._ch == '_') {
        var kind = 1;
        if (this._ch == '_')
          kind = 2;
        else if (this._ch >= max && invalid < 0)
          invalid = this._offset;

        separators |= kind;
        this.Next();
      }
    } else {
      while (IsHex(this._ch) || this._ch == '_') {
        separators |= this._ch == '_' ? 2 : 1;
        this.Next();
      }
    }

    return separators;
  }

  /// <summary>The name of a literal of a prefix in a message: a decimal literal is never wrong in its digits or radix.</summary>
  private static string LiteralName(char prefix) => prefix switch {
    'x' => "hexadecimal literal",
    'b' => "binary literal",
    _ => "octal literal"
  };

  private (GoTokenKind, string) ScanNumber() {
    var start = this._offset;
    var token = GoTokenKind.Illegal;
    var @base = 10;
    var prefix = '\0';
    var separators = 0;
    var invalid = -1;
    if (this._ch != '.') {
      token = GoTokenKind.Int;
      if (this._ch == '0') {
        this.Next();
        switch (Lower(this._ch)) {
          case 'x':
            this.Next();
            (@base, prefix) = (16, 'x');
            break;
          case 'o':
            this.Next();
            (@base, prefix) = (8, 'o');
            break;
          case 'b':
            this.Next();
            (@base, prefix) = (2, 'b');
            break;
          default:
            (@base, prefix) = (8, '0');
            separators = 1;
            break;
        }
      }

      separators |= this.Digits(@base, ref invalid);
    }

    if (this._ch == '.') {
      token = GoTokenKind.Float;
      if (prefix is 'o' or 'b')
        throw Error(this._offset, "invalid radix point in " + LiteralName(prefix));

      this.Next();
      separators |= this.Digits(@base, ref invalid);
    }

    if ((separators & 1) == 0)
      throw Error(this._offset, LiteralName(prefix) + " has no digits");

    var exponent = Lower(this._ch);
    if (exponent is 'e' or 'p') {
      if (exponent == 'e' && prefix != '\0' && prefix != '0')
        throw Error(this._offset, $"'{(char)this._ch}' exponent requires decimal mantissa");

      if (exponent == 'p' && prefix != 'x')
        throw Error(this._offset, $"'{(char)this._ch}' exponent requires hexadecimal mantissa");

      this.Next();
      token = GoTokenKind.Float;
      if (this._ch is '+' or '-')
        this.Next();

      var none = -1;
      var digits = this.Digits(10, ref none);
      separators |= digits;
      if ((digits & 1) == 0)
        throw Error(this._offset, "exponent has no digits");
    } else if (prefix == 'x' && token == GoTokenKind.Float) {
      throw Error(this._offset, "hexadecimal mantissa requires a 'p' exponent");
    }

    if (this._ch == 'i') {
      token = GoTokenKind.Imag;
      this.Next();
    }

    var literal = this._source.Text(start, this._offset);
    if (token == GoTokenKind.Int && invalid >= 0)
      throw Error(invalid, $"invalid digit '{literal[invalid - start]}' in {LiteralName(prefix)}");

    if ((separators & 2) != 0 && InvalidSeparator(literal) is var at and >= 0)
      throw Error(start + at, "'_' must separate successive digits");

    return (token, literal);
  }

  /// <summary>The index of the first <c>_</c> of a literal that does not separate two digits, or -1.</summary>
  internal static int InvalidSeparator(string literal) {
    var prefix = ' ';
    var digit = '.';
    var index = 0;
    if (literal.Length >= 2 && literal[0] == '0') {
      prefix = (char)Lower(literal[1]);
      if (prefix is 'x' or 'o' or 'b') {
        digit = '0';
        index = 2;
      }
    }

    for (; index < literal.Length; ++index) {
      var previous = digit;
      digit = literal[index];
      if (digit == '_') {
        if (previous != '0')
          return index;
      } else if (IsDecimal(digit) || prefix == 'x' && IsHex(digit)) {
        digit = '0';
      } else {
        if (previous == '_')
          return index - 1;

        digit = '.';
      }
    }

    return digit == '_' ? literal.Length - 1 : -1;
  }

  private void ScanEscape(int quote) {
    var start = this._offset;
    int count;
    uint @base, max;
    switch (this._ch) {
      case 'a' or 'b' or 'f' or 'n' or 'r' or 't' or 'v' or '\\':
        this.Next();
        return;
      case var ch when ch == quote:
        this.Next();
        return;
      case >= '0' and <= '7':
        (count, @base, max) = (3, 8, 255);
        break;
      case 'x':
        this.Next();
        (count, @base, max) = (2, 16, 255);
        break;
      case 'u':
        this.Next();
        (count, @base, max) = (4, 16, 0x10FFFF);
        break;
      case 'U':
        this.Next();
        (count, @base, max) = (8, 16, 0x10FFFF);
        break;
      default:
        throw Error(start, this._ch < 0 ? "escape sequence not terminated" : "unknown escape sequence");
    }

    uint value = 0;
    for (; count > 0; --count) {
      var digit = (uint)DigitValue(this._ch);
      if (digit >= @base)
        throw Error(this._offset, this._ch < 0 ? "escape sequence not terminated" : $"illegal character {CodePoint(this._ch)} in escape sequence");

      value = value * @base + digit;
      this.Next();
    }

    if (value > max || value is >= 0xD800 and < 0xE000)
      throw Error(start, "escape sequence is invalid Unicode code point");
  }

  /// <summary>A code point as <c>%#U</c> writes it: <c>U+0041 'A'</c>.</summary>
  internal static string CodePoint(int ch) {
    var hex = "U+" + ch.ToString("X4", CultureInfo.InvariantCulture);
    return System.Text.Rune.IsValid(ch) && !System.Text.Rune.IsControl(new System.Text.Rune(ch)) && ch != ' ' ? $"{hex} '{char.ConvertFromUtf32(ch)}'" : hex;
  }

  private string ScanRune() {
    var start = this._offset - 1;
    var count = 0;
    while (true) {
      var ch = this._ch;
      if (ch is '\n' or < 0)
        throw Error(start, "rune literal not terminated");

      this.Next();
      if (ch == '\'')
        break;

      ++count;
      if (ch == '\\')
        this.ScanEscape('\'');
    }

    if (count != 1)
      throw Error(start, "illegal rune literal");

    return this._source.Text(start, this._offset);
  }

  private string ScanString() {
    var start = this._offset - 1;
    while (true) {
      var ch = this._ch;
      if (ch is '\n' or < 0)
        throw Error(start, "string literal not terminated");

      this.Next();
      if (ch == '"')
        break;

      if (ch == '\\')
        this.ScanEscape('"');
    }

    return this._source.Text(start, this._offset);
  }

  private string ScanRawString() {
    var start = this._offset - 1;
    while (true) {
      var ch = this._ch;
      if (ch < 0)
        throw Error(start, "raw string literal not terminated");

      this.Next();
      if (ch == '`')
        break;
    }

    return this._source.Text(start, this._offset).Replace("\r", "", StringComparison.Ordinal);
  }

  private void SkipWhitespace() {
    while (this._ch is ' ' or '\t' or '\r' || this._ch == '\n' && !this._insertSemi)
      this.Next();
  }

  private GoTokenKind Switch2(GoTokenKind plain, GoTokenKind assign) {
    if (this._ch != '=')
      return plain;

    this.Next();
    return assign;
  }

  private GoTokenKind Switch3(GoTokenKind plain, GoTokenKind assign, int second, GoTokenKind doubled) {
    if (this._ch == '=') {
      this.Next();
      return assign;
    }

    if (this._ch != second)
      return plain;

    this.Next();
    return doubled;
  }

  private GoTokenKind Switch4(GoTokenKind plain, GoTokenKind assign, int second, GoTokenKind doubled, GoTokenKind doubledAssign) {
    if (this._ch == '=') {
      this.Next();
      return assign;
    }

    if (this._ch != second)
      return plain;

    this.Next();
    if (this._ch != '=')
      return doubled;

    this.Next();
    return doubledAssign;
  }

  /// <summary>The next token, a semicolon where a newline or the end of the file follows a token that can end a statement.</summary>
  public GoToken Scan() {
    while (true) {
      this.SkipWhitespace();
      var offset = this._offset;
      var insertSemi = false;
      var ch = this._ch;
      GoToken token;
      if (IsLetter(ch)) {
        var literal = this.ScanIdentifier();
        var kind = literal.Length > 1 ? GoTokens.Lookup(literal) : GoTokenKind.Ident;
        insertSemi = kind is GoTokenKind.Ident or GoTokenKind.Break or GoTokenKind.Continue or GoTokenKind.Fallthrough or GoTokenKind.Return;
        token = new(kind, offset, literal);
      } else if (IsDecimal(ch) || ch == '.' && IsDecimal(this.Peek())) {
        insertSemi = true;
        var (kind, literal) = this.ScanNumber();
        token = new(kind, offset, literal);
      } else {
        this.Next();
        GoTokenKind kind;
        var literal = "";
        switch (ch) {
          case Eof:
            if (this._insertSemi) {
              this._insertSemi = false;
              this.End = offset;
              return new(GoTokenKind.Semicolon, offset, "\n");
            }

            kind = GoTokenKind.Eof;
            break;
          case '\n':
            this._insertSemi = false;
            this.End = offset + 1;
            return new(GoTokenKind.Semicolon, offset, "\n");
          case '"':
            insertSemi = true;
            kind = GoTokenKind.String;
            literal = this.ScanString();
            break;
          case '\'':
            insertSemi = true;
            kind = GoTokenKind.Char;
            literal = this.ScanRune();
            break;
          case '`':
            insertSemi = true;
            kind = GoTokenKind.String;
            literal = this.ScanRawString();
            break;
          case ':':
            kind = this.Switch2(GoTokenKind.Colon, GoTokenKind.Define);
            break;
          case '.':
            kind = GoTokenKind.Period;
            if (this._ch == '.' && this.Peek() == '.') {
              this.Next();
              this.Next();
              kind = GoTokenKind.Ellipsis;
            }

            break;
          case ',':
            kind = GoTokenKind.Comma;
            break;
          case ';':
            kind = GoTokenKind.Semicolon;
            literal = ";";
            break;
          case '(':
            kind = GoTokenKind.LParen;
            break;
          case ')':
            insertSemi = true;
            kind = GoTokenKind.RParen;
            break;
          case '[':
            kind = GoTokenKind.LBrack;
            break;
          case ']':
            insertSemi = true;
            kind = GoTokenKind.RBrack;
            break;
          case '{':
            kind = GoTokenKind.LBrace;
            break;
          case '}':
            insertSemi = true;
            kind = GoTokenKind.RBrace;
            break;
          case '+':
            kind = this.Switch3(GoTokenKind.Add, GoTokenKind.AddAssign, '+', GoTokenKind.Inc);
            insertSemi = kind == GoTokenKind.Inc;
            break;
          case '-':
            kind = this.Switch3(GoTokenKind.Sub, GoTokenKind.SubAssign, '-', GoTokenKind.Dec);
            insertSemi = kind == GoTokenKind.Dec;
            break;
          case '*':
            kind = this.Switch2(GoTokenKind.Mul, GoTokenKind.MulAssign);
            break;
          case '/':
            if (this._ch is '/' or '*') {
              var newline = this.SkipComment();
              if (this._insertSemi && newline != 0) {
                this._insertSemi = false;
                this.End = newline + 1;
                return new(GoTokenKind.Semicolon, newline, "\n");
              }

              continue;
            }

            kind = this.Switch2(GoTokenKind.Quo, GoTokenKind.QuoAssign);
            break;
          case '%':
            kind = this.Switch2(GoTokenKind.Rem, GoTokenKind.RemAssign);
            break;
          case '^':
            kind = this.Switch2(GoTokenKind.Xor, GoTokenKind.XorAssign);
            break;
          case '<':
            if (this._ch == '-') {
              this.Next();
              kind = GoTokenKind.Arrow;
            } else {
              kind = this.Switch4(GoTokenKind.Lss, GoTokenKind.Leq, '<', GoTokenKind.Shl, GoTokenKind.ShlAssign);
            }

            break;
          case '>':
            kind = this.Switch4(GoTokenKind.Gtr, GoTokenKind.Geq, '>', GoTokenKind.Shr, GoTokenKind.ShrAssign);
            break;
          case '=':
            kind = this.Switch2(GoTokenKind.Assign, GoTokenKind.Eql);
            break;
          case '!':
            kind = this.Switch2(GoTokenKind.Not, GoTokenKind.Neq);
            break;
          case '&':
            if (this._ch == '^') {
              this.Next();
              kind = this.Switch2(GoTokenKind.AndNot, GoTokenKind.AndNotAssign);
            } else {
              kind = this.Switch3(GoTokenKind.And, GoTokenKind.AndAssign, '&', GoTokenKind.LAnd);
            }

            break;
          case '|':
            kind = this.Switch3(GoTokenKind.Or, GoTokenKind.OrAssign, '|', GoTokenKind.LOr);
            break;
          case '~':
            kind = GoTokenKind.Tilde;
            break;
          default:
            throw Error(offset, ch is 0x201C or 0x201D ? $"curly quotation mark '{char.ConvertFromUtf32(ch)}' (use neutral '\"')" : $"illegal character {CodePoint(ch)}");
        }

        token = new(kind, offset, literal);
      }

      this._insertSemi = insertSemi;
      this.End = this._offset;
      this._started = true;
      return token;
    }
  }

}
