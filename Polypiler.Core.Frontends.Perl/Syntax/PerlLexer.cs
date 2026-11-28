using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Polypiler.Core.Frontends.Perl.Ast;

namespace Polypiler.Core.Frontends.Perl.Syntax;

/// <summary>
/// The lexer of perldata, perlop and perlsyn, driven by the parser: every token is read either where
/// a term is expected or where an operator is, which decides <c>/</c> (a match or a division),
/// <c>&lt;</c> (a read line, a glob, a heredoc or less-than), <c>%</c>, <c>&amp;</c> and <c>*</c>
/// (a sigil or an operator), <c>-e</c> (a file test or a minus) and <c>x</c>. Whitespace, comments
/// and POD are skipped; a heredoc's body is cut out of the lines after its marker's line when the
/// marker is taken.
/// </summary>
internal sealed class PerlLexer {

  private const string FileTestLetters = "rwxoRWXOezsfdlpSbcugktTBAMC";

  private static readonly string[] _operators = [
    "<<>>", "**=", "||=", "&&=", "//=", "^^=", "<=>", "<<=", ">>=", "...", "&.=", "|.=", "^.=",
    "**", "||", "&&", "//", "^^", "<<", ">>", "<=", ">=", "==", "!=", "=~", "!~", "~~", "=>", "->", "++", "--",
    "+=", "-=", "*=", "/=", ".=", "%=", "&=", "|=", "^=", "..", "&.", "|.", "^.", "~.", "::",
    "+", "-", "*", "/", "%", "<", ">", "=", "!", "~", "\\", "?", ":", ".", ",", ";", "(", ")", "[", "]", "{", "}", "&", "|", "^"
  ];

  private readonly PerlText _text;
  private readonly int _end;
  private readonly IPerlLexerHost _host;
  private readonly bool _nested;
  private int _position;
  private PerlToken? _peeked;
  private bool _peekedTerm;
  private int _peekedAt = -1;
  private readonly Dictionary<int, int> _heredocSkips = [];
  private readonly Dictionary<int, PerlToken> _heredocs = [];

  public PerlLexer(PerlText text, int start, int end, IPerlLexerHost host, bool nested) {
    this._text = text;
    this._position = start;
    this._end = end;
    this._host = host;
    this._nested = nested;
  }

  /// <summary>The text read.</summary>
  public PerlText Text => this._text;

  /// <summary>The position of the next character not yet taken.</summary>
  public int Position {
    get => this._position;
    set {
      this._position = value;
      this._peeked = null;
    }
  }

  /// <summary>The end of the text read.</summary>
  public int End => this._end;

  /// <summary>Whether the text ended at <c>__END__</c> or <c>__DATA__</c>.</summary>
  public bool SawEndMarker { get; private set; }

  /// <summary>The line of a position.</summary>
  public int LineAt(int position) => this._text.LineAt(position);

  /// <summary>The line of the next token.</summary>
  public int Line => this._text.LineAt(this.SkipSpace(this._position));

  /// <summary>The character at a position of the text, or <c>'\0'</c> beyond the end.</summary>
  public char At(int position) => position < this._end ? this._text[position] : '\0';

  /// <summary>The token at the position, read as a term (<paramref name="term"/>) or as an operator.</summary>
  public PerlToken Peek(bool term) {
    if (this._peeked is not null && this._peekedAt == this._position && this._peekedTerm == term)
      return this._peeked;
    this._peeked = this.Read(term);
    this._peekedAt = this._position;
    this._peekedTerm = term;
    return this._peeked;
  }

  /// <summary>Takes the token at the position; a heredoc's body is read here.</summary>
  public PerlToken Next(bool term) {
    var token = this.Peek(term);
    this._position = token.End;
    this._peeked = null;
    return token.Kind == PerlTokenKind.Heredoc ? this.ReadHeredocBody(token) : token;
  }

  /// <summary>The position of the next character that is no whitespace, comment or POD.</summary>
  public int SkipSpace(int position) {
    var i = position;
    while (i < this._end) {
      var c = this._text[i];
      if (c == '\n') {
        i = this._heredocSkips.TryGetValue(i, out var resume) ? resume : i + 1;
        if (i < this._end && this._text[i] == '=' && IsAsciiLetter(this._text[i + 1]))
          i = this.SkipPod(i);
        continue;
      }
      if (c is ' ' or '\t' or '\r' or '\f' or '\v') {
        ++i;
        continue;
      }
      if (c == '#') {
        while (i < this._end && this._text[i] != '\n')
          ++i;
        continue;
      }
      if (i == 0 && c == '=' && IsAsciiLetter(this._text[1])) {
        i = this.SkipPod(i);
        continue;
      }
      break;
    }
    return i;
  }

  /// <summary>Skips POD from the line at <paramref name="i"/> through the next line that starts with <c>=cut</c>; the first line never ends it.</summary>
  private int SkipPod(int i) {
    var first = true;
    while (i < this._end) {
      var lineEnd = this.LineEnd(i);
      var isCut = !first && string.CompareOrdinal(this._text.Value, i, "=cut", 0, 4) == 0 && !IsWordChar(this._text[i + 4]);
      i = lineEnd < this._end ? lineEnd + 1 : this._end;
      first = false;
      if (isCut)
        return i;
    }
    return this._end;
  }

  private int LineEnd(int i) {
    var index = this._text.Value.IndexOf('\n', i, this._end - i);
    return index < 0 ? this._end : index;
  }

  private PerlToken Read(bool term) {
    var start = this.SkipSpace(this._position);
    var space = start != this._position;
    if (start >= this._end || this._text[start] is '\x04' or '\x1a')
      return new(PerlTokenKind.End, "", start, start, this._text.LineAt(start)) { SpaceBefore = space };

    var c = this._text[start];
    var token = IsIdentifierStart(c, this._host.Utf8) || (c == ':' && this.At(start + 1) == ':' && IsIdentifierStart(this.At(start + 2), this._host.Utf8))
      ? this.ReadWord(start, term)
      : char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(this.At(start + 1)) && term)
        ? this.ReadNumber(start)
        : this.ReadSymbol(start, term);
    return token with { SpaceBefore = space };
  }

  private PerlToken Make(PerlTokenKind kind, string text, int start, int end)
    => new(kind, text, start, end, this._text.LineAt(start));

  private PerlSyntaxException Error(string message, int position) => new(message, this._text.LineAt(position));

  // ---- words ----

  /// <summary>Reads an identifier at a position: <c>Foo::Bar</c>, <c>::foo</c>, <c>Foo::</c>; returns its end.</summary>
  public int ScanIdentifier(int start) {
    var i = start;
    while (true) {
      if (this.At(i) == ':' && this.At(i + 1) == ':') {
        i += 2;
        continue;
      }
      if (this._host.ApostropheSeparates && this.At(i) == '\'' && i > start && IsIdentifierStart(this.At(i + 1), this._host.Utf8) && IsWordChar(this.At(i - 1), this._host.Utf8)
          && !PerlBuiltins.IsBuiltin(this._text.Value[start..i]) && !PerlWordSets.Quotes.Contains(this._text.Value[start..i])) {
        ++i;
        continue;
      }
      if (i < this._end && IsWordChar(this._text[i], this._host.Utf8)) {
        ++i;
        continue;
      }
      break;
    }
    return i;
  }

  /// <summary>The identifier text with <c>'</c> separators spelled as <c>::</c>.</summary>
  public string IdentifierText(int start, int end) => this._text.Value[start..end].Replace("'", "::", StringComparison.Ordinal);

  private PerlToken ReadWord(int start, bool term) {
    if (!term && this._text[start] == 'x' && start + 1 <= this._end) {
      var after = this.At(start + 1);
      if (after == '=' && this.At(start + 2) is not ('=' or '~'))
        return this.Make(PerlTokenKind.Operator, "x=", start, start + 2);
      if (!IsWordChar(after, this._host.Utf8))
        return this.Make(PerlTokenKind.Operator, "x", start, start + 1);
      var digits = start + 1;
      while (char.IsAsciiDigit(this.At(digits)))
        ++digits;
      if (digits > start + 1 && !IsWordChar(this.At(digits), this._host.Utf8))
        return this.Make(PerlTokenKind.Operator, "x", start, start + 1);
    }

    var end = this.ScanIdentifier(start);
    var text = this.IdentifierText(start, end);
    var next = this.SkipSpace(end);
    var arrow = this.At(next) == '=' && this.At(next + 1) == '>';
    if (arrow && !text.Contains("::", StringComparison.Ordinal))
      return this.Make(PerlTokenKind.Word, text, start, end) with { AutoQuoted = true };

    if (text is "__END__" or "__DATA__" or "CORE::__END__" or "CORE::__DATA__" && !this._nested) {
      this.SawEndMarker = true;
      return this.Make(PerlTokenKind.End, "", start, this._end);
    }

    if (term && text.Length == 2 && text[0] == 'v' && char.IsAsciiDigit(text[1]) || term && text.Length > 1 && text[0] == 'v' && IsAllDigits(text, 1)) {
      var vend = end;
      while (this.At(vend) == '.' && char.IsAsciiDigit(this.At(vend + 1))) {
        vend += 2;
        while (char.IsAsciiDigit(this.At(vend)) || this.At(vend) == '_')
          ++vend;
      }
      return this.Make(PerlTokenKind.Version, this._text.Value[start..vend], start, vend);
    }

    if (term && PerlWordSets.Quotes.Contains(text))
      if (this.TryQuoteLike(text, start, end) is { } quote)
        return quote;

    return this.Make(PerlTokenKind.Word, text, start, end);
  }

  private static bool IsAllDigits(string text, int from) {
    for (var i = from; i < text.Length; ++i)
      if (!char.IsAsciiDigit(text[i]))
        return false;
    return true;
  }

  // ---- numbers ----

  private PerlToken ReadNumber(int start) {
    var i = start;
    var c = this._text[i];
    if (c == '0' && this.At(i + 1) is 'x' or 'X' or 'b' or 'B' or 'o' or 'O' || c == '0' && char.IsAsciiDigit(this.At(i + 1)) || c == '0' && this.At(i + 1) == '_')
      return this.ReadRadixNumber(start);

    while (char.IsAsciiDigit(this.At(i)) || this.At(i) == '_')
      ++i;
    var isFloat = false;
    if (this.At(i) == '.' && this.At(i + 1) != '.') {
      var dot = i;
      ++i;
      while (char.IsAsciiDigit(this.At(i)) || this.At(i) == '_')
        ++i;
      if (this.At(i) == '.' && char.IsAsciiDigit(this.At(i + 1)) && dot > start) {
        while (this.At(i) == '.' && char.IsAsciiDigit(this.At(i + 1))) {
          ++i;
          while (char.IsAsciiDigit(this.At(i)) || this.At(i) == '_')
            ++i;
        }
        return this.Make(PerlTokenKind.Version, this._text.Value[start..i], start, i);
      }
      isFloat = true;
    }
    if (this.At(i) is 'e' or 'E' && (char.IsAsciiDigit(this.At(i + 1)) || this.At(i + 1) is '+' or '-' && char.IsAsciiDigit(this.At(i + 2)))) {
      i += 2;
      while (char.IsAsciiDigit(this.At(i)) || this.At(i) == '_')
        ++i;
      isFloat = true;
    }
    var digits = this._text.Value[start..i].Replace("_", "", StringComparison.Ordinal);
    PerlNumeric value;
    if (!isFloat && ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var integer))
      value = PerlNumeric.OfUnsigned(integer);
    else
      value = PerlNumeric.Of(double.Parse(digits.EndsWith('.') ? digits + "0" : digits, NumberStyles.Float, CultureInfo.InvariantCulture));
    return this.Make(PerlTokenKind.Number, digits, start, i) with { Number = value };
  }

  private PerlToken ReadRadixNumber(int start) {
    var i = start + 1;
    var radix = 8;
    switch (this.At(i)) {
      case 'x' or 'X': radix = 16; ++i; break;
      case 'b' or 'B': radix = 2; ++i; break;
      case 'o' or 'O': ++i; break;
    }
    var digitsStart = i;
    var value = 0UL;
    var overflow = false;
    var floatValue = 0.0;
    while (true) {
      var d = this.At(i);
      if (d == '_') {
        ++i;
        continue;
      }
      var digit = HexValue(d);
      if (digit < 0 || digit >= radix) {
        if (radix == 8 && d is '8' or '9')
          throw this.Error($"Illegal octal digit '{d}'", i);
        if (radix == 2 && char.IsAsciiDigit(d))
          throw this.Error($"Illegal binary digit '{d}'", i);
        break;
      }
      if (!overflow && value > (ulong.MaxValue - (ulong)digit) / (ulong)radix) {
        overflow = true;
        floatValue = value;
      }
      if (overflow)
        floatValue = floatValue * radix + digit;
      else
        value = value * (ulong)radix + (ulong)digit;
      ++i;
    }
    if (radix == 16 && (this.At(i) == '.' && (HexValue(this.At(i + 1)) >= 0 || this.At(i + 1) is 'p' or 'P') || this.At(i) is 'p' or 'P') || radix != 16 && this.IsRadixFloat(i))
      return this.ReadHexFloat(start, digitsStart, i, radix);
    if (radix != 8 && i == digitsStart && this.At(i - 1) is not '_')
      return this.Make(PerlTokenKind.Number, "0", start, start + 1) with { Number = PerlNumeric.Of(0L) };
    var number = overflow ? PerlNumeric.Of(floatValue) : PerlNumeric.OfUnsigned(value);
    return this.Make(PerlTokenKind.Number, this._text.Value[start..i], start, i) with { Number = number };
  }

  private bool IsRadixFloat(int i) {
    if (this.At(i) == '.' && char.IsAsciiDigit(this.At(i + 1))) {
      ++i;
      while (char.IsAsciiDigit(this.At(i)) || this.At(i) == '_')
        ++i;
    }
    return this.At(i) is 'p' or 'P' && (char.IsAsciiDigit(this.At(i + 1)) || this.At(i + 1) is '+' or '-');
  }

  private PerlToken ReadHexFloat(int start, int digitsStart, int i, int radix) {
    var bits = radix switch { 16 => 4, 8 => 3, _ => 1 };
    var mantissa = 0.0;
    for (var k = digitsStart; k < i; ++k)
      if (this._text[k] != '_')
        mantissa = mantissa * radix + HexValue(this._text[k]);
    var exponent = 0;
    if (this.At(i) == '.') {
      ++i;
      while (HexValue(this.At(i)) is >= 0 and var d && d < radix || this.At(i) == '_') {
        if (this.At(i) != '_') {
          mantissa = mantissa * radix + HexValue(this.At(i));
          exponent -= bits;
        }
        ++i;
      }
    }
    if (this.At(i) is 'p' or 'P') {
      ++i;
      var sign = 1;
      if (this.At(i) is '+' or '-') {
        sign = this.At(i) == '-' ? -1 : 1;
        ++i;
      }
      var e = 0;
      while (char.IsAsciiDigit(this.At(i)) || this.At(i) == '_') {
        if (this.At(i) != '_')
          e = e * 10 + (this.At(i) - '0');
        ++i;
      }
      exponent += sign * e;
    }
    return this.Make(PerlTokenKind.Number, this._text.Value[start..i], start, i) with { Number = PerlNumeric.Of(mantissa * Math.Pow(2, exponent)) };
  }

  private static int HexValue(char c) => c switch {
    >= '0' and <= '9' => c - '0',
    >= 'a' and <= 'f' => c - 'a' + 10,
    >= 'A' and <= 'F' => c - 'A' + 10,
    _ => -1
  };

  // ---- symbols ----

  private PerlToken ReadSymbol(int start, bool term) {
    var c = this._text[start];
    var next = this.At(start + 1);
    switch (c) {
      case '$':
        return this.ReadScalarSigil(start);
      case '@':
        return this.ReadAggregateSigil(start, '@');
      case '%' when term:
        return this.ReadAggregateSigil(start, '%');
      case '&' when term && next != '&':
        return this.ReadAggregateSigil(start, '&');
      case '*' when term && next != '*':
        return this.ReadAggregateSigil(start, '*');
      case '\'' when term:
      case '"' when term:
      case '`' when term:
        return this.ReadQuote(c.ToString(), start, start);
      case '/' when term:
        return this.ReadQuote("m", start, start);
      case '<' when term:
        return this.ReadAngle(start);
      case '-' when term && FileTestLetters.Contains(next) && !IsWordChar(this.At(start + 2), this._host.Utf8) && !(this.At(this.SkipSpace(start + 2)) == '=' && this.At(this.SkipSpace(start + 2) + 1) == '>'):
        return this.Make(PerlTokenKind.FileTest, "-" + next, start, start + 2);
    }

    foreach (var op in _operators) {
      if (term && op == "~~")
        continue;
      if (string.CompareOrdinal(this._text.Value, start, op, 0, op.Length) == 0 && start + op.Length <= this._end) {
        if (op == "<<>>" && !term)
          continue;
        return this.Make(PerlTokenKind.Operator, op, start, start + op.Length);
      }
    }
    if (!term)
      return this.Make(PerlTokenKind.Operator, c.ToString(), start, start + 1);
    throw this.Error($"Unrecognized character {Describe(c)}", start);
  }

  private static string Describe(char c) => c < ' ' || c > '~' ? $"\\x{(int)c:X2}" : c.ToString();

  private PerlToken ReadScalarSigil(int start) {
    var i = start + 1;
    var c = this.At(i);
    if (c is ' ' or '\t' or '\n' && this.SkippedSigilSpace(i) is var named && named > i)
      return this.ReadSpacedSigil(start, '$', named);
    if (c == '#') {
      var after = this.At(i + 1);
      if (after == '{' && this.BracedName(i + 1) is { } braced)
        return this.Make(PerlTokenKind.Variable, braced.Name, start, braced.End) with { Sigil = '#' };
      if (after is '{' or '$')
        return this.Make(PerlTokenKind.Cast, "$#", start, i + 1);
      if (after == '[')
        return this.Make(PerlTokenKind.Variable, "#", start, i + 1) with { Sigil = '$' };
      if (after is '+' or '-')
        return this.Make(PerlTokenKind.Variable, after.ToString(), start, i + 2) with { Sigil = '#' };
      if (IsIdentifierStart(after, this._host.Utf8) || after == ':' && this.At(i + 2) == ':') {
        var end = this.ScanIdentifier(i + 1);
        return this.Make(PerlTokenKind.Variable, this.IdentifierText(i + 1, end), start, end) with { Sigil = '#' };
      }
      throw this.Error("$# is no longer supported", start);
    }
    if (c == '$') {
      var after = this.At(i + 1);
      if (IsIdentifierStart(after, this._host.Utf8) || after is '{' or '$' || after == ':' && this.At(i + 2) == ':')
        return this.Make(PerlTokenKind.Cast, "$", start, i);
      return this.Make(PerlTokenKind.Variable, "$", start, i + 1) with { Sigil = '$' };
    }
    return this.ReadNamedOrCast(start, '$');
  }

  private PerlToken ReadAggregateSigil(int start, char sigil) {
    var c = this.At(start + 1);
    if (sigil == '*' && c == '$' && !(IsIdentifierStart(this.At(start + 2), this._host.Utf8) || "{$:".Contains(this.At(start + 2))))
      return this.Make(PerlTokenKind.Variable, "$", start, start + 2) with { Sigil = '*' };
    if (c is ' ' or '\t' or '\n' && this.SkippedSigilSpace(start + 1) is var named && named > start + 1)
      return this.ReadSpacedSigil(start, sigil, named);
    if (c == '$')
      return this.Make(PerlTokenKind.Cast, sigil.ToString(), start, start + 1);
    return this.ReadNamedOrCast(start, sigil);
  }

  private int SkippedSigilSpace(int i) {
    var j = this.SkipSpace(i);
    var c = this.At(j);
    return IsIdentifierStart(c, this._host.Utf8) || c is '{' or '$' ? j : i;
  }

  /// <summary>A sigil, whitespace and then a name or a block (<c>$ x</c>, <c>$ {...}</c>), which perl still allows.</summary>
  private PerlToken ReadSpacedSigil(int start, char sigil, int at) {
    var c = this.At(at);
    if (c is '{' or '$')
      return this.Make(PerlTokenKind.Cast, sigil.ToString(), start, at);
    var end = this.ScanIdentifier(at);
    return this.Make(PerlTokenKind.Variable, this.IdentifierText(at, end), start, end) with { Sigil = sigil };
  }

  /// <summary>A name in braces at <paramref name="open"/> (<c>{name}</c>, <c>{^NAME}</c>, <c>{+}</c>): the name and the position after the brace.</summary>
  private (string Name, int End)? BracedName(int open) {
    var j = this.SkipBlanks(open + 1);
    var nameStart = j;
    if (this.At(j) == '^')
      ++j;
    var nameEnd = j;
    while (IsWordChar(this.At(nameEnd), this._host.Utf8))
      ++nameEnd;
    if (nameEnd > j && (this.At(nameStart) == '^' || IsIdentifierStart(this.At(nameStart), this._host.Utf8) || char.IsAsciiDigit(this.At(nameStart)))) {
      var close = this.SkipBlanks(nameEnd);
      if (this.At(close) == '}')
        return (this._text.Value[nameStart..nameEnd], close + 1);
    }
    return null;
  }

  private PerlToken ReadNamedOrCast(int start, char sigil) {
    var i = start + 1;
    var c = this.At(i);
    if (c == '{') {
      var j = this.SkipBlanks(i + 1);
      var nameStart = j;
      if (this.At(j) == '^')
        ++j;
      var nameEnd = j;
      while (IsWordChar(this.At(nameEnd), this._host.Utf8))
        ++nameEnd;
      if (nameEnd > j && (this.At(nameStart) == '^' || IsIdentifierStart(this.At(nameStart), this._host.Utf8) || char.IsAsciiDigit(this.At(nameStart)))) {
        var close = this.SkipBlanks(nameEnd);
        if (this.At(close) == '}')
          return this.Make(PerlTokenKind.Variable, this._text.Value[nameStart..nameEnd], start, close + 1) with { Sigil = sigil };
      }
      return this.Make(PerlTokenKind.Cast, sigil.ToString(), start, i);
    }
    if (c == ':' && this.At(i + 1) == ':' && !IsIdentifierStart(this.At(i + 2), this._host.Utf8))
      return this.Make(PerlTokenKind.Variable, "main::", start, i + 2) with { Sigil = sigil };
    if (IsIdentifierStart(c, this._host.Utf8) || c == ':' && this.At(i + 1) == ':') {
      var end = this.ScanIdentifier(i);
      var name = this.IdentifierText(i, end);
      return this.Make(PerlTokenKind.Variable, name.StartsWith("::", StringComparison.Ordinal) ? "main" + name : name, start, end) with { Sigil = sigil };
    }
    if (char.IsAsciiDigit(c)) {
      var end = i;
      while (char.IsAsciiDigit(this.At(end)))
        ++end;
      return this.Make(PerlTokenKind.Variable, this._text.Value[i..end], start, end) with { Sigil = sigil };
    }
    if (c == '^' && (char.IsAsciiLetterUpper(this.At(i + 1)) || "[]\\^_?".Contains(this.At(i + 1))))
      return this.Make(PerlTokenKind.Variable, "^" + this.At(i + 1), start, i + 2) with { Sigil = sigil };
    if (sigil is '$' or '*' && c == '\'' && this._host.ApostropheSeparates && IsIdentifierStart(this.At(i + 1), this._host.Utf8)) {
      var end = this.ScanIdentifier(i + 1);
      return this.Make(PerlTokenKind.Variable, "main::" + this.IdentifierText(i + 1, end), start, end) with { Sigil = sigil };
    }
    if (sigil is '$' or '*' && c != '\0' && "&`'+_./\\,;!?@<>()[]-:%=~^|\"0".Contains(c))
      return this.Make(PerlTokenKind.Variable, c.ToString(), start, i + 1) with { Sigil = sigil };
    if (sigil == '@' && "_-+:?!;.,".Contains(c))
      return this.Make(PerlTokenKind.Variable, c.ToString(), start, i + 1) with { Sigil = sigil };
    if (sigil == '%' && "+-!^:".Contains(c))
      return c == '^' && char.IsAsciiLetterUpper(this.At(i + 1))
        ? this.Make(PerlTokenKind.Variable, "^" + this.At(i + 1), start, i + 2) with { Sigil = sigil }
        : this.Make(PerlTokenKind.Variable, c.ToString(), start, i + 1) with { Sigil = sigil };
    if (sigil is '%' or '&' or '*')
      return this.Make(PerlTokenKind.Operator, sigil.ToString(), start, start + 1);
    throw this.Error($"syntax error near \"{sigil}{c}\"", start);
  }

  private int SkipBlanks(int i) {
    while (this.At(i) is ' ' or '\t' or '\n' or '\r')
      ++i;
    return i;
  }

  // ---- quotes ----

  private PerlToken? TryQuoteLike(string keyword, int start, int end) {
    var i = end;
    var spaced = false;
    while (true) {
      var c = this.At(i);
      if (c is ' ' or '\t' or '\n' or '\r' or '\f') {
        spaced = true;
        ++i;
        continue;
      }
      if (c == '#' && spaced) {
        while (i < this._end && this._text[i] != '\n')
          ++i;
        continue;
      }
      break;
    }
    var delimiter = this.At(i);
    if (delimiter == '\0' || delimiter == '=' && this.At(i + 1) == '>' || !spaced && IsWordChar(delimiter, this._host.Utf8))
      return null;
    return this.ReadQuote(keyword, start, i);
  }

  private PerlToken ReadQuote(string keyword, int start, int delimiterAt) {
    var (body, after) = this.ScanDelimited(delimiterAt);
    var kind = keyword switch {
      "m" or "qr" => PerlTokenKind.Match,
      "s" => PerlTokenKind.Substitute,
      "tr" or "y" => PerlTokenKind.Transliterate,
      _ => PerlTokenKind.Quote
    };
    PerlQuote? replacement = null;
    if (kind is PerlTokenKind.Substitute or PerlTokenKind.Transliterate) {
      if (IsOpening(body.Open)) {
        var j = this.SkipSpace(after);
        if (j >= this._end)
          throw this.Error($"{(kind == PerlTokenKind.Substitute ? "Substitution replacement" : "Transliteration replacement")} not terminated", start);
        (replacement, after) = this.ScanDelimited(j);
      } else {
        (replacement, after) = this.ScanDelimited(after - 1);
      }
    }
    var flagsEnd = after;
    if (kind == PerlTokenKind.Transliterate) {
      while ("cdsr".Contains(this.At(flagsEnd)))
        ++flagsEnd;
    } else if (kind != PerlTokenKind.Quote) {
      while (char.IsAsciiLetter(this.At(flagsEnd)))
        ++flagsEnd;
      this.CheckPatternFlags(keyword, this._text.Value[after..flagsEnd], start);
    }
    return this.Make(kind, this._text.Value[start..flagsEnd], start, flagsEnd) with {
      Keyword = keyword,
      Body = body,
      Replacement = replacement,
      Flags = this._text.Value[after..flagsEnd]
    };
  }

  /// <summary>The modifiers perl knows for a match (<c>m//</c> and <c>//</c>), <c>qr//</c> and <c>s///</c> (perlop, "Regexp Quote-Like Operators").</summary>
  private static string PatternFlags(string keyword) => keyword switch {
    "qr" => "msixxpodualn",
    "s" => "msixxpodualngcer",
    _ => "msixxpodualngc"
  };

  /// <summary>Refuses the modifiers perl refuses: an unknown letter, two charsets, <c>/a</c> thrice, <c>/l</c>, <c>/u</c> or <c>/d</c> twice.</summary>
  private void CheckPatternFlags(string keyword, string flags, int start) {
    var known = PatternFlags(keyword);
    char? charset = null;
    var count = 0;
    foreach (var c in flags) {
      if (!known.Contains(c))
        throw this.Error($"Unknown regexp modifier \"/{c}\"", start);
      if (!"alud".Contains(c))
        continue;
      if (charset is { } other && other != c)
        throw this.Error($"Regexp modifiers \"/{other}\" and \"/{c}\" are mutually exclusive", start);
      charset = c;
      if (++count > (c == 'a' ? 2 : 1))
        throw this.Error(c == 'a' ? "Regexp modifier \"/a\" may appear a maximum of twice" : $"Regexp modifier \"/{c}\" may not appear twice", start);
    }
  }

  private static bool IsOpening(char c) => c is '(' or '[' or '{' or '<';

  private static char Closing(char c) => c switch {
    '(' => ')',
    '[' => ']',
    '{' => '}',
    '<' => '>',
    _ => c
  };

  /// <summary>Scans a delimited body whose opening delimiter is at <paramref name="at"/>; returns it and the position after the closing one.</summary>
  public (PerlQuote Body, int After) ScanDelimited(int at) {
    var open = this.At(at);
    var close = Closing(open);
    var nests = close != open;
    var depth = 0;
    var builder = new StringBuilder();
    var i = at + 1;
    while (true) {
      if (i >= this._end)
        throw this.Error($"Can't find string terminator \"{close}\" anywhere before EOF", at);
      var c = this._text[i];
      if (c == '\\' && open != '\\' && i + 1 < this._end) {
        var escaped = this._text[i + 1];
        if (!nests && escaped == close)
          builder.Append(escaped);
        else
          builder.Append(c).Append(escaped);
        i += 2;
        continue;
      }
      if (nests && c == open) {
        ++depth;
      } else if (c == close) {
        if (depth == 0)
          break;
        --depth;
      }
      builder.Append(c);
      ++i;
    }
    return (new PerlQuote(new PerlText(builder.ToString(), this._text.LineAt(at)), open, close), i + 1);
  }

  // ---- angle brackets: heredocs, read lines, globs ----

  private PerlToken ReadAngle(int start) {
    if (this.At(start + 1) == '<') {
      if (this.At(start + 2) == '>' && this.At(start + 3) == '>')
        return this.Make(PerlTokenKind.ReadLine, "<<>>", start, start + 4);
      if (this.TryHeredocMarker(start) is { } heredoc)
        return heredoc;
      return this.Make(PerlTokenKind.Operator, this.At(start + 2) == '=' ? "<<=" : "<<", start, start + (this.At(start + 2) == '=' ? 3 : 2));
    }
    var close = start + 1;
    while (close < this._end && this._text[close] is not ('>' or '\n'))
      ++close;
    if (this.At(close) != '>')
      return this.Make(PerlTokenKind.Operator, this.At(start + 1) == '=' ? "<=" : "<", start, start + (this.At(start + 1) == '=' ? 2 : 1));
    var content = this._text.Value[(start + 1)..close];
    var simple = content.Length == 0 || content[0] == '$' && content.Length > 1 && IsIdentifier(content[1..]) || IsIdentifier(content);
    if (content.Length > 0 && content[0] == '=' && !simple)
      return this.Make(PerlTokenKind.Operator, "<=", start, start + 2);
    return this.Make(simple ? PerlTokenKind.ReadLine : PerlTokenKind.Glob, content, start, close + 1) with {
      Body = simple ? null : new PerlQuote(new PerlText(content, this._text.LineAt(start)), '<', '>')
    };
  }

  private static bool IsIdentifier(string text) {
    if (text.Length == 0 || !(IsIdentifierStart(text[0], true) || text[0] == ':'))
      return false;
    foreach (var c in text)
      if (!IsWordChar(c, true) && c != ':')
        return false;
    return true;
  }

  private PerlToken? TryHeredocMarker(int start) {
    var i = start + 2;
    var indented = false;
    if (this.At(i) == '~') {
      indented = true;
      ++i;
    }
    var spaced = i;
    while (this.At(spaced) is ' ' or '\t')
      ++spaced;
    if (spaced > i && "\"'`".Contains(this.At(spaced)))
      i = spaced;
    var c = this.At(i);
    string terminator;
    bool interpolates;
    int end;
    if (c is '"' or '\'' or '`') {
      var close = this._text.Value.IndexOf(c, i + 1);
      if (close < 0 || close >= this._end)
        throw this.Error("Unterminated delimiter for here document", start);
      terminator = this._text.Value[(i + 1)..close];
      interpolates = c != '\'';
      end = close + 1;
    } else if (c == '\\' && IsIdentifierStart(this.At(i + 1), this._host.Utf8)) {
      end = i + 1;
      while (IsWordChar(this.At(end), this._host.Utf8))
        ++end;
      terminator = this._text.Value[(i + 1)..end];
      interpolates = false;
    } else if (IsIdentifierStart(c, this._host.Utf8)) {
      end = i;
      while (IsWordChar(this.At(end), this._host.Utf8))
        ++end;
      terminator = this._text.Value[i..end];
      interpolates = true;
    } else {
      if (indented)
        throw this.Error("Use of bare <<~ to mean <<~\"\" is forbidden", start);
      return null;
    }
    return this.Make(PerlTokenKind.Heredoc, terminator, start, end) with { Interpolates = interpolates, Indented = indented };
  }

  private PerlToken ReadHeredocBody(PerlToken marker) {
    if (this._heredocs.TryGetValue(marker.Start, out var known))
      return known;
    var lineEnd = this.LineEnd(marker.End);
    var bodyStart = this._heredocSkips.TryGetValue(lineEnd, out var resume) ? resume : lineEnd + 1;
    if (lineEnd >= this._end) {
      if (this._nested)
        throw new PerlUnsupportedException("a heredoc inside a one-line quote", marker.Line);
      throw this.Error($"Can't find string terminator \"{marker.Text}\" anywhere before EOF", marker.Start);
    }
    var lines = new List<string>();
    var i = bodyStart;
    string? indentation = null;
    while (true) {
      if (i >= this._end)
        throw this.Error($"Can't find string terminator \"{marker.Text}\" anywhere before EOF", marker.Start);
      var end = this.LineEnd(i);
      var line = this._text.Value[i..end];
      var bare = line.EndsWith('\r') ? line[..^1] : line;
      if (marker.Indented ? bare.TrimStart(' ', '\t') == marker.Text : bare == marker.Text) {
        indentation = bare[..^marker.Text.Length];
        i = end < this._end ? end + 1 : this._end;
        break;
      }
      lines.Add(line);
      i = end < this._end ? end + 1 : this._end;
    }
    var builder = new StringBuilder();
    for (var k = 0; k < lines.Count; ++k) {
      var line = lines[k];
      if (marker.Indented && indentation!.Length > 0) {
        if (line.StartsWith(indentation, StringComparison.Ordinal))
          line = line[indentation.Length..];
        else if (line.Trim(' ', '\t', '\r').Length == 0)
          line = "";
        else
          throw new PerlSyntaxException($"Indentation on line {k + 1} of here-doc doesn't match delimiter", marker.Line);
      }
      builder.Append(line).Append('\n');
    }
    this._heredocSkips[lineEnd] = i;
    var body = new PerlQuote(new PerlText(builder.ToString(), this._text.LineAt(bodyStart)), '\0', '\0');
    return this._heredocs[marker.Start] = marker with { Body = body };
  }

  // ---- characters ----

  public static bool IsAsciiLetter(char c) => char.IsAsciiLetter(c);

  public static bool IsIdentifierStart(char c, bool utf8) => char.IsAsciiLetter(c) || c == '_' || utf8 && c > 0x7F && IsUnicodeStart(c);

  /// <summary>The categories that start an identifier under <c>use utf8</c>: the letters and the letter numbers (XID_Start).</summary>
  private const int _unicodeStart = 1 << (int)UnicodeCategory.UppercaseLetter | 1 << (int)UnicodeCategory.LowercaseLetter
    | 1 << (int)UnicodeCategory.TitlecaseLetter | 1 << (int)UnicodeCategory.ModifierLetter | 1 << (int)UnicodeCategory.OtherLetter
    | 1 << (int)UnicodeCategory.LetterNumber;

  /// <summary>The categories that continue one besides those (XID_Continue): marks, digits and connectors.</summary>
  private const int _unicodeContinue = _unicodeStart | 1 << (int)UnicodeCategory.NonSpacingMark | 1 << (int)UnicodeCategory.SpacingCombiningMark
    | 1 << (int)UnicodeCategory.DecimalDigitNumber | 1 << (int)UnicodeCategory.ConnectorPunctuation;

  private static bool IsUnicodeStart(char c) => (1 << (int)char.GetUnicodeCategory(c) & _unicodeStart) != 0 || char.IsSurrogate(c);

  private static bool IsUnicodeContinue(char c) => (1 << (int)char.GetUnicodeCategory(c) & _unicodeContinue) != 0 || char.IsSurrogate(c);

  public static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

  public static bool IsWordChar(char c, bool utf8) => char.IsAsciiLetterOrDigit(c) || c == '_' || utf8 && c > 0x7F && IsUnicodeContinue(c);

}
