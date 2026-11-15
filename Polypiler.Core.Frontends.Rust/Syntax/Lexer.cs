using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Polypiler.Core.Frontends.Rust.Syntax;

/// <summary>A position in the source: the 1-based line and column.</summary>
/// <param name="Line">The line.</param>
/// <param name="Column">The column, counted in UTF-16 code units.</param>
internal readonly record struct Span(int Line, int Column);

/// <summary>What a token is.</summary>
internal enum TokenKind {

  /// <summary>An identifier or a keyword.</summary>
  Identifier,

  /// <summary>A label or lifetime, <c>'a</c>; the text is the name without the quote.</summary>
  Lifetime,

  /// <summary>An integer literal, its value in <see cref="Token.Value"/> and its suffix in <see cref="Token.Suffix"/>.</summary>
  Integer,

  /// <summary>A floating-point literal, its digits in <see cref="Token.Text"/> and its suffix in <see cref="Token.Suffix"/>.</summary>
  Float,

  /// <summary>A character literal, <c>'a'</c>.</summary>
  Character,

  /// <summary>A string literal of any kind; the text is its value.</summary>
  String,

  /// <summary>Punctuation, the text being the operator or delimiter.</summary>
  Punctuation,

  /// <summary>The end of the source.</summary>
  End

}

/// <summary>One token of Rust source.</summary>
/// <param name="Kind">What the token is.</param>
/// <param name="Text">The identifier, the punctuation, the digits of a float or the value of a string.</param>
/// <param name="At">Where the token starts.</param>
internal sealed record Token(TokenKind Kind, string Text, Span At) {

  /// <summary>The value of an integer literal.</summary>
  public BigInteger Value { get; init; }

  /// <summary>The type suffix of a numeric literal (<c>u8</c>, <c>f32</c>, ...), or <see langword="null"/>.</summary>
  public string? Suffix { get; init; }

  /// <summary>Whether the token is the punctuation or the identifier <paramref name="text"/>.</summary>
  public bool Is(string text) => this.Kind is TokenKind.Punctuation or TokenKind.Identifier && !this.Raw && this.Text == text;

  /// <summary>Whether an identifier was written raw (<c>r#type</c>): it is never a keyword.</summary>
  public bool Raw { get; init; }

  /// <inheritdoc/>
  public override string ToString() => this.Kind == TokenKind.End ? "end of file" : $"'{this.Text}'";

}

/// <summary>A token or a construct that is not valid Rust.</summary>
/// <param name="message">What is wrong.</param>
/// <param name="at">Where.</param>
internal sealed class RustSyntaxException(string message, Span at) : System.Exception(message) {

  /// <summary>Where the error is.</summary>
  public Span At { get; } = at;

}

/// <summary>
/// Splits Rust source into tokens as The Rust Reference's lexical structure describes it: identifiers
/// and raw identifiers, labels, integer literals of every base with <c>_</c> separators and type
/// suffixes, floating-point literals with exponent and suffix, byte, character and string literals,
/// nested block comments and line comments, and the punctuation of the language.
/// </summary>
internal static class Lexer {

  /// <summary>The punctuation, longest first, so that the longest match wins.</summary>
  private static readonly string[] _punctuation = [
    "<<=", ">>=", "...", "..=",
    "::", "->", "=>", "==", "!=", "<=", ">=", "&&", "||", "+=", "-=", "*=", "/=", "%=", "^=", "&=", "|=", "<<", ">>", "..",
    "+", "-", "*", "/", "%", "^", "!", "&", "|", "=", "<", ">", "@", ".", ",", ";", ":", "#", "$", "?", "~", "{", "}", "[", "]", "(", ")"
  ];

  /// <summary>The tokens of a source, ending with one of <see cref="TokenKind.End"/>.</summary>
  /// <exception cref="RustSyntaxException">The source has a character or a literal that is not valid Rust.</exception>
  public static List<Token> Tokens(string source) {
    var tokens = new List<Token>();
    var (index, line, column) = (0, 1, 1);
    if (source.StartsWith('﻿'))
      index = 1;

    void Advance(int count) {
      for (var step = 0; step < count; ++step) {
        if (source[index] == '\n') {
          ++line;
          column = 1;
        } else {
          ++column;
        }

        ++index;
      }
    }

    char Peek(int offset) => index + offset < source.Length ? source[index + offset] : '\0';

    while (index < source.Length) {
      var c = source[index];
      if (char.IsWhiteSpace(c)) {
        Advance(1);
        continue;
      }

      if (c == '/' && Peek(1) == '/') {
        while (index < source.Length && source[index] != '\n')
          Advance(1);

        continue;
      }

      var at = new Span(line, column);
      if (c == '/' && Peek(1) == '*') {
        Advance(2);
        var depth = 1;
        while (depth > 0) {
          if (index >= source.Length)
            throw new RustSyntaxException("unterminated block comment", at);

          if (source[index] == '/' && Peek(1) == '*') {
            ++depth;
            Advance(2);
          } else if (source[index] == '*' && Peek(1) == '/') {
            --depth;
            Advance(2);
          } else {
            Advance(1);
          }
        }

        continue;
      }

      var previousIsDot = tokens.Count > 0 && tokens[^1].Is(".");
      if (c == 'b' && Peek(1) == '\'') {
        Advance(2);
        var value = CharacterValue(source, ref index, ref column, at);
        if (Peek(0) != '\'')
          throw new RustSyntaxException("unterminated byte literal", at);

        Advance(1);
        if (value > 0x7F)
          throw new RustSyntaxException("non-ASCII character in byte literal", at);

        tokens.Add(new Token(TokenKind.Integer, value.ToString(CultureInfo.InvariantCulture), at) { Value = value, Suffix = "u8" });
        continue;
      }

      if ((c == 'b' && (Peek(1) == '"' || (Peek(1) == 'r' && (Peek(2) == '"' || Peek(2) == '#')))) || (c == 'r' && (Peek(1) == '"' || (Peek(1) == '#' && Peek(2) is '"' or '#')))) {
        if (c == 'b')
          Advance(1);

        tokens.Add(new Token(TokenKind.String, StringLiteral(source, ref index, ref line, ref column, at), at));
        continue;
      }

      if (c == '"') {
        tokens.Add(new Token(TokenKind.String, StringLiteral(source, ref index, ref line, ref column, at), at));
        continue;
      }

      if (c == '\'') {
        // A label is a quote and an identifier that no closing quote follows; anything else is a character.
        if (IsIdentifierStart(Peek(1))) {
          var end = index + 1;
          while (end < source.Length && IsIdentifierPart(source[end]))
            ++end;

          if (end >= source.Length || source[end] != '\'') {
            var name = source[(index + 1)..end];
            Advance(end - index);
            tokens.Add(new Token(TokenKind.Lifetime, name, at));
            continue;
          }
        }

        Advance(1);
        var value = CharacterValue(source, ref index, ref column, at);
        if (Peek(0) != '\'')
          throw new RustSyntaxException("unterminated character literal", at);

        Advance(1);
        tokens.Add(new Token(TokenKind.Character, char.ConvertFromUtf32(value), at));
        continue;
      }

      if (char.IsAsciiDigit(c)) {
        var start = index;
        tokens.Add(Number(source, ref index, previousIsDot, at));
        column += index - start;
        continue;
      }

      if (IsIdentifierStart(c)) {
        var start = index;
        if (c == 'r' && Peek(1) == '#' && IsIdentifierStart(Peek(2)))
          start += 2;

        var end = start;
        while (end < source.Length && IsIdentifierPart(source[end]))
          ++end;

        var raw = start > index;
        var text = source[start..end];
        Advance(end - index);
        tokens.Add(new Token(TokenKind.Identifier, text, at) { Raw = raw });
        continue;
      }

      var punctuation = System.Array.Find(_punctuation, candidate => string.CompareOrdinal(source, index, candidate, 0, candidate.Length) == 0)
        ?? throw new RustSyntaxException($"unknown start of token '{c}'", at);
      Advance(punctuation.Length);
      tokens.Add(new Token(TokenKind.Punctuation, punctuation, at));
    }

    tokens.Add(new Token(TokenKind.End, "", new Span(line, column)));
    return tokens;
  }

  private static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);

  private static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);

  /// <summary>An integer or floating-point literal starting at <paramref name="index"/>.</summary>
  private static Token Number(string source, ref int index, bool afterDot, Span at) {
    char Peek(int position) => position < source.Length ? source[position] : '\0';
    var start = index;
    var radix = 10;
    if (source[index] == '0' && Peek(index + 1) is 'x' or 'o' or 'b') {
      radix = Peek(index + 1) switch { 'x' => 16, 'o' => 8, _ => 2 };
      index += 2;
    }

    var digits = new StringBuilder();
    while (index < source.Length && (source[index] == '_' || IsDigit(source[index], radix == 16 ? 16 : 10)))
      digits.Append(source[index++]);

    var isFloat = false;
    // A dot makes a float unless a second dot (a range), an identifier (a method) or a tuple index follows.
    if (radix == 10 && !afterDot && Peek(index) == '.' && Peek(index + 1) != '.' && !IsIdentifierStart(Peek(index + 1))) {
      isFloat = true;
      digits.Append('.');
      ++index;
      while (index < source.Length && (source[index] == '_' || char.IsAsciiDigit(source[index])))
        digits.Append(source[index++]);
    }

    if (radix == 10 && !afterDot && Peek(index) is 'e' or 'E'
        && (char.IsAsciiDigit(Peek(index + 1)) || (Peek(index + 1) is '+' or '-' && char.IsAsciiDigit(Peek(index + 2))))) {
      isFloat = true;
      digits.Append('e');
      ++index;
      if (source[index] is '+' or '-')
        digits.Append(source[index++]);

      while (index < source.Length && (source[index] == '_' || char.IsAsciiDigit(source[index])))
        digits.Append(source[index++]);
    }

    var suffixStart = index;
    while (index < source.Length && IsIdentifierPart(source[index]))
      ++index;

    var suffix = suffixStart == index ? null : source[suffixStart..index];
    var text = digits.ToString().Replace("_", "", System.StringComparison.Ordinal);
    if (isFloat || (radix == 10 && suffix is "f32" or "f64")) {
      if (suffix is not (null or "f32" or "f64"))
        throw new RustSyntaxException($"invalid suffix '{suffix}' for float literal", at);

      return new Token(TokenKind.Float, text, at) { Suffix = suffix };
    }

    if (text.Length == 0)
      throw new RustSyntaxException("no valid digits found for number", at);

    BigInteger value = 0;
    foreach (var digit in text) {
      var digitValue = HexValue(digit);
      if (digitValue >= radix)
        throw new RustSyntaxException($"invalid digit for a base {radix} literal", at);

      value = value * radix + digitValue;
    }

    return new Token(TokenKind.Integer, source[start..index], at) { Value = value, Suffix = suffix };
  }

  private static bool IsDigit(char c, int radix) => radix == 16 ? char.IsAsciiHexDigit(c) : char.IsAsciiDigit(c);

  private static int HexValue(char c) => c <= '9' ? c - '0' : char.ToLowerInvariant(c) - 'a' + 10;

  /// <summary>The value of one character of a character, byte or string literal, escapes resolved.</summary>
  private static int CharacterValue(string source, ref int index, ref int column, Span at) {
    if (index >= source.Length)
      throw new RustSyntaxException("unterminated literal", at);

    var c = source[index];
    if (c != '\\') {
      var value = char.ConvertToUtf32(source, index);
      var width = char.IsSurrogatePair(source, index) ? 2 : 1;
      index += width;
      column += width;
      return value;
    }

    var escape = index + 1 < source.Length ? source[index + 1] : '\0';
    index += 2;
    column += 2;
    switch (escape) {
      case 'n': return '\n';
      case 'r': return '\r';
      case 't': return '\t';
      case '0': return 0;
      case '\\': return '\\';
      case '\'': return '\'';
      case '"': return '"';
      case 'x': {
        if (index + 2 > source.Length || !char.IsAsciiHexDigit(source[index]) || !char.IsAsciiHexDigit(source[index + 1]))
          throw new RustSyntaxException("invalid \\x escape", at);

        var value = HexValue(source[index]) * 16 + HexValue(source[index + 1]);
        index += 2;
        column += 2;
        return value;
      }
      case 'u': {
        var close = source.IndexOf('}', index);
        if (index >= source.Length || source[index] != '{' || close < 0)
          throw new RustSyntaxException("invalid unicode escape", at);

        var digits = source[(index + 1)..close].Replace("_", "", System.StringComparison.Ordinal);
        column += close + 1 - index;
        index = close + 1;
        return int.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
      }
      default:
        throw new RustSyntaxException($"unknown character escape '{escape}'", at);
    }
  }

  /// <summary>A string literal, plain or raw, starting at its opening quote or <c>r</c>; its value.</summary>
  private static string StringLiteral(string source, ref int index, ref int line, ref int column, Span at) {
    var text = new StringBuilder();
    if (source[index] == 'r') {
      ++index;
      ++column;
      var hashes = 0;
      while (index < source.Length && source[index] == '#') {
        ++hashes;
        ++index;
        ++column;
      }

      if (index >= source.Length || source[index] != '"')
        throw new RustSyntaxException("invalid raw string literal", at);

      var terminator = "\"" + new string('#', hashes);
      var end = source.IndexOf(terminator, index + 1, System.StringComparison.Ordinal);
      if (end < 0)
        throw new RustSyntaxException("unterminated raw string", at);

      var body = source[(index + 1)..end];
      Count(body, ref line, ref column);
      column += 1 + terminator.Length;
      index = end + terminator.Length;
      return body;
    }

    ++index;
    ++column;
    while (true) {
      if (index >= source.Length)
        throw new RustSyntaxException("unterminated double quote string", at);

      var c = source[index];
      if (c == '"') {
        ++index;
        ++column;
        return text.ToString();
      }

      if (c == '\\' && index + 1 < source.Length && source[index + 1] == '\n') {
        index += 2;
        ++line;
        column = 1;
        while (index < source.Length && char.IsWhiteSpace(source[index])) {
          Count(source[index].ToString(), ref line, ref column);
          ++index;
        }

        continue;
      }

      if (c == '\n') {
        text.Append(c);
        ++index;
        ++line;
        column = 1;
        continue;
      }

      text.Append(char.ConvertFromUtf32(CharacterValue(source, ref index, ref column, at)));
    }
  }

  private static void Count(string text, ref int line, ref int column) {
    foreach (var c in text) {
      if (c == '\n') {
        ++line;
        column = 1;
      } else {
        ++column;
      }
    }
  }

}
