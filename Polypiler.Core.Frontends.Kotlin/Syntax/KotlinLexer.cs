using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Frontends.Kotlin.Syntax;

/// <summary>The kinds of tokens of Kotlin the lexer tells apart.</summary>
public enum KTokenKind {

  /// <summary>An identifier or a keyword.</summary>
  Name,

  /// <summary>An integer literal.</summary>
  Integer,

  /// <summary>A floating-point literal.</summary>
  Float,

  /// <summary>A character literal; the text is the code unit.</summary>
  Character,

  /// <summary>A string literal; the text is the raw content between the quotes, templates unread.</summary>
  String,

  /// <summary>A raw string literal, <c>"""…"""</c>; the text is its content.</summary>
  RawString,

  /// <summary>An operator or a punctuation mark.</summary>
  Symbol,

  /// <summary>The end of the text.</summary>
  End

}

/// <summary>A token: its kind, text, place, and whether a line break stands before it.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Text">The text, for a literal its value as written without quotes.</param>
/// <param name="At">Where it starts.</param>
/// <param name="NewlineBefore">Whether a line break stands between the token before and this one.</param>
public sealed record KToken(KTokenKind Kind, string Text, KPosition At, bool NewlineBefore);

/// <summary>A syntax error: what is wrong and where.</summary>
/// <param name="message">What is wrong.</param>
/// <param name="at">Where.</param>
public sealed class KotlinSyntaxException(string message, KPosition at) : Exception($"{at}: {message}") {

  /// <summary>Where the error is.</summary>
  public KPosition At { get; } = at;

  /// <summary>What is wrong, without the place.</summary>
  public string Reason { get; } = message;

}

/// <summary>
/// Cuts Kotlin source into tokens (kotlinlang.org/spec, "Lexical grammar"): identifiers and
/// keywords, backticked names, integer literals in decimal, hexadecimal and binary with underscores
/// and the suffixes <c>u</c>, <c>U</c>, <c>L</c>, floating-point literals with an exponent and the
/// suffix <c>f</c>, character and string literals with their escapes, raw strings, the operators,
/// and the comments, nested block comments included, which it drops. A line break is not a token: the
/// token after it knows it stands on a new line.
/// </summary>
public static class KotlinLexer {

  private static readonly string[] _symbols = [
    "!==", "===", "..<", "?.", "?:", "!!", "++", "--", "->", "::", "==", "!=", "<=", ">=", "&&", "||", "+=", "-=", "*=", "/=", "%=", "..",
    "{", "}", "(", ")", "[", "]", ",", ".", ":", ";", "@", "!", "+", "-", "*", "/", "%", "=", "<", ">", "?", "&", "|", "^", "~", "#"
  ];

  /// <summary>The tokens of a text, the end last.</summary>
  /// <param name="text">The text.</param>
  /// <returns>The tokens.</returns>
  /// <exception cref="KotlinSyntaxException">The text has a character or a literal Kotlin does not have.</exception>
  public static IReadOnlyList<KToken> Tokens(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var tokens = new List<KToken>();
    var (at, line, column) = (0, 1, 1);
    var newline = false;
    void Advance(int count) {
      for (var step = 0; step < count; ++step) {
        if (text[at] == '\n') {
          ++line;
          column = 1;
        } else {
          ++column;
        }

        ++at;
      }
    }

    while (at < text.Length) {
      var character = text[at];
      var position = new KPosition(line, column);
      if (character == '\n') {
        newline = true;
        Advance(1);
        continue;
      }

      if (char.IsWhiteSpace(character) || character == '﻿') {
        Advance(1);
        continue;
      }

      if (character == '/' && at + 1 < text.Length && text[at + 1] == '/') {
        while (at < text.Length && text[at] != '\n')
          Advance(1);

        continue;
      }

      if (character == '/' && at + 1 < text.Length && text[at + 1] == '*') {
        var depth = 0;
        do {
          if (at + 1 >= text.Length)
            throw new KotlinSyntaxException("an unterminated comment", position);

          if (text[at] == '/' && text[at + 1] == '*') {
            ++depth;
            Advance(2);
          } else if (text[at] == '*' && text[at + 1] == '/') {
            --depth;
            Advance(2);
          } else {
            Advance(1);
          }
        } while (depth > 0);

        continue;
      }

      KToken token;
      var start = at;
      if (char.IsLetter(character) || character == '_') {
        while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] == '_'))
          Advance(1);

        token = new(KTokenKind.Name, text[start..at], position, newline);
      } else if (character == '`') {
        var end = text.IndexOf('`', at + 1);
        if (end < 0 || text.AsSpan(at, end - at).Contains('\n'))
          throw new KotlinSyntaxException("an unterminated backticked name", position);

        token = new(KTokenKind.Name, text[(at + 1)..end], position, newline);
        Advance(end + 1 - at);
      } else if (char.IsAsciiDigit(character) || character == '.' && at + 1 < text.Length && char.IsAsciiDigit(text[at + 1]) && (tokens.Count == 0 || tokens[^1].Text != ".")) {
        token = Number(text, ref at, position, newline);
        column += at - start;
      } else if (text.AsSpan(at).StartsWith("\"\"\"")) {
        var end = text.IndexOf("\"\"\"", at + 3, StringComparison.Ordinal);
        if (end < 0)
          throw new KotlinSyntaxException("an unterminated raw string", position);

        // A raw string ends at the last of a run of quotes.
        while (end + 3 < text.Length && text[end + 3] == '"')
          ++end;

        token = new(KTokenKind.RawString, text[(at + 3)..end], position, newline);
        Advance(end + 3 - at);
      } else if (character == '"') {
        var end = StringEnd(text, at, position);
        token = new(KTokenKind.String, text[(at + 1)..end], position, newline);
        Advance(end + 1 - at);
      } else if (character == '\'') {
        var (value, length) = Character(text, at, position);
        token = new(KTokenKind.Character, value.ToString(), position, newline);
        Advance(length);
      } else {
        var symbol = _symbols.FirstOrDefault(candidate => text.AsSpan(at).StartsWith(candidate))
          ?? throw new KotlinSyntaxException($"the character '{character}' has no meaning in Kotlin", position);
        token = new(KTokenKind.Symbol, symbol, position, newline);
        Advance(symbol.Length);
      }

      tokens.Add(token);
      newline = false;
    }

    tokens.Add(new(KTokenKind.End, "", new KPosition(line, column), true));
    return tokens;
  }

  /// <summary>The index of the quote that ends a string literal, its templates' braces skipped.</summary>
  private static int StringEnd(string text, int at, KPosition position) {
    var depth = 0;
    for (var index = at + 1; index < text.Length; ++index) {
      var character = text[index];
      if (character == '\n' && depth == 0)
        break;

      if (character == '\\') {
        ++index;
        continue;
      }

      if (character == '$' && index + 1 < text.Length && text[index + 1] == '{') {
        ++depth;
        ++index;
      } else if (character == '}' && depth > 0) {
        --depth;
      } else if (character == '"' && depth == 0) {
        return index;
      }
    }

    throw new KotlinSyntaxException("an unterminated string", position);
  }

  /// <summary>Reads an escape sequence at <paramref name="at"/>, the backslash; gives the code unit and the length.</summary>
  /// <param name="text">The text.</param>
  /// <param name="at">The index of the backslash.</param>
  /// <param name="position">Where the literal starts, for an error.</param>
  /// <returns>The code unit and how many characters the escape takes.</returns>
  /// <exception cref="KotlinSyntaxException">The escape is none of Kotlin's.</exception>
  public static (char Value, int Length) Escape(string text, int at, KPosition position) {
    ArgumentNullException.ThrowIfNull(text);
    if (at + 1 >= text.Length)
      throw new KotlinSyntaxException("an unterminated escape", position);

    return text[at + 1] switch {
      't' => ('\t', 2),
      'b' => ('\b', 2),
      'n' => ('\n', 2),
      'r' => ('\r', 2),
      '\'' => ('\'', 2),
      '"' => ('"', 2),
      '\\' => ('\\', 2),
      '$' => ('$', 2),
      'u' when at + 6 <= text.Length && int.TryParse(text.AsSpan(at + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var unit) => ((char)unit, 6),
      var other => throw new KotlinSyntaxException($"the escape '\\{other}' is none of Kotlin's", position)
    };
  }

  private static (char Value, int Length) Character(string text, int at, KPosition position) {
    if (at + 2 >= text.Length)
      throw new KotlinSyntaxException("an unterminated character", position);

    var (value, length) = text[at + 1] == '\\' ? Escape(text, at + 1, position) : (text[at + 1], 1);
    if (at + 1 + length >= text.Length || text[at + 1 + length] != '\'')
      throw new KotlinSyntaxException("a character literal holds one character", position);

    return (value, length + 2);
  }

  /// <summary>A number: an integer in its radix with its suffix, or a floating-point number with its exponent and suffix.</summary>
  private static KToken Number(string text, ref int at, KPosition position, bool newline) {
    var start = at;
    if (text[at] == '0' && at + 1 < text.Length && text[at + 1] is 'x' or 'X' or 'b' or 'B') {
      var hexadecimal = text[at + 1] is 'x' or 'X';
      at += 2;
      var digits = at;
      while (at < text.Length && (Uri.IsHexDigit(text[at]) && (hexadecimal || text[at] is '0' or '1') || text[at] == '_'))
        ++at;

      if (digits == at)
        throw new KotlinSyntaxException("a number without digits", position);

      Suffix(text, ref at);
      return new(KTokenKind.Integer, text[start..at], position, newline);
    }

    var floating = false;
    while (at < text.Length && (char.IsAsciiDigit(text[at]) || text[at] == '_'))
      ++at;

    if (at + 1 < text.Length && text[at] == '.' && char.IsAsciiDigit(text[at + 1])) {
      floating = true;
      ++at;
      while (at < text.Length && (char.IsAsciiDigit(text[at]) || text[at] == '_'))
        ++at;
    }

    if (at < text.Length && text[at] is 'e' or 'E') {
      floating = true;
      ++at;
      if (at < text.Length && text[at] is '+' or '-')
        ++at;

      if (at >= text.Length || !char.IsAsciiDigit(text[at]))
        throw new KotlinSyntaxException("an exponent without digits", position);

      while (at < text.Length && (char.IsAsciiDigit(text[at]) || text[at] == '_'))
        ++at;
    }

    if (at < text.Length && text[at] is 'f' or 'F') {
      ++at;
      return new(KTokenKind.Float, text[start..at], position, newline);
    }

    if (floating)
      return new(KTokenKind.Float, text[start..at], position, newline);

    Suffix(text, ref at);
    return new(KTokenKind.Integer, text[start..at], position, newline);
  }

  private static void Suffix(string text, ref int at) {
    if (at < text.Length && text[at] is 'u' or 'U')
      ++at;

    if (at < text.Length && text[at] == 'L')
      ++at;
  }

  /// <summary>The value of an integer literal and its suffixes, or <see langword="null"/> for one that does not fit 64 bits.</summary>
  /// <param name="text">The literal as the lexer read it.</param>
  /// <returns>The value, whether it is unsigned and whether it is long.</returns>
  public static (ulong Value, bool Unsigned, bool Long)? Integer(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var isLong = text.EndsWith('L');
    var digits = isLong ? text[..^1] : text;
    var unsigned = digits.EndsWith('u') || digits.EndsWith('U');
    if (unsigned)
      digits = digits[..^1];

    digits = digits.Replace("_", "", StringComparison.Ordinal);
    var (radix, body) = digits.Length > 2 && digits[0] == '0' && digits[1] is 'x' or 'X' ? (16, digits[2..])
      : digits.Length > 2 && digits[0] == '0' && digits[1] is 'b' or 'B' ? (2, digits[2..])
      : (10, digits);
    var value = System.Numerics.BigInteger.Zero;
    foreach (var digit in body)
      value = value * radix + (digit <= '9' ? digit - '0' : char.ToLowerInvariant(digit) - 'a' + 10);

    return value > ulong.MaxValue ? null : ((ulong)value, unsigned, isLong);
  }

  /// <summary>The value of a floating-point literal, rounded as its type rounds it, and whether it is a <c>Float</c>.</summary>
  /// <param name="text">The literal as the lexer read it.</param>
  /// <returns>The value and whether it has the suffix <c>f</c>.</returns>
  public static (double Value, bool Single) Floating(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var single = text.EndsWith('f') || text.EndsWith('F');
    var digits = (single ? text[..^1] : text).Replace("_", "", StringComparison.Ordinal);
    return single
      ? (float.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture), true)
      : (double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture), false);
  }

  /// <summary>The characters of a string literal's literal part, its escapes read.</summary>
  /// <param name="text">The part.</param>
  /// <param name="position">Where the literal starts, for an error.</param>
  /// <returns>The characters.</returns>
  public static string Unescape(string text, KPosition position) {
    ArgumentNullException.ThrowIfNull(text);
    var value = new StringBuilder(text.Length);
    for (var index = 0; index < text.Length; ++index) {
      if (text[index] != '\\') {
        value.Append(text[index]);
        continue;
      }

      var (unit, length) = Escape(text, index, position);
      value.Append(unit);
      index += length - 1;
    }

    return value.ToString();
  }

}
