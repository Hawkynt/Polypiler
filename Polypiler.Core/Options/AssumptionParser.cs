using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Polypiler.Core.Options;

/// <summary>
/// The parser of the value facts of <c>--assume</c>, a recursive descent over the grammar below.
/// Whitespace between tokens is optional; identifiers are case-sensitive; integers are unbounded.
/// <code>
/// fact    := path "in" "[" int "," int "]"      (Range, bounds inclusive)
///          | path "!=" "null"                    (NonNull)
///          | path "is" type                      (Type)
///          | path "noalias" path                 (NoAlias)
/// path    := ident ("." ident)*
/// ident   := [A-Za-z_][A-Za-z0-9_]*
/// int     := ["-"] ( [0-9]+ | "0x" [0-9A-Fa-f]+ )
/// type    := "u8"|"u16"|"u32"|"u64"|"i8"|"i16"|"i32"|"i64"|"f32"|"f64"|"bool"
/// </code>
/// </summary>
public static class AssumptionParser {

  /// <summary>The scalar types a <see cref="AssumptionKind.Type"/> fact may name, in the order of the grammar.</summary>
  public static IReadOnlyList<string> Types { get; } = ["u8", "u16", "u32", "u64", "i8", "i16", "i32", "i64", "f32", "f64", "bool"];

  /// <summary>Parses one fact.</summary>
  /// <param name="text">The text of one <c>--assume</c>.</param>
  /// <param name="result">The fact, or <see langword="null"/> when the text is none.</param>
  /// <param name="error">Why the text is no fact, the reason of <c>PLY0007</c>; <see langword="null"/> for a fact.</param>
  /// <returns>Whether the text is a fact.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
  public static bool TryParse(string text, out Assumption? result, out string? error) {
    ArgumentNullException.ThrowIfNull(text);
    var reader = new Reader(text);
    result = reader.Fact(out error);
    return result is not null;
  }

  /// <summary>Reads an integer of the grammar: <c>["-"] ( [0-9]+ | "0x" [0-9A-Fa-f]+ )</c>, of any size.</summary>
  /// <param name="token">The text of the integer, nothing before or after it.</param>
  /// <param name="value">The value, or 0.</param>
  /// <returns>Whether the text is an integer.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="token"/> is <see langword="null"/>.</exception>
  public static bool TryParseInteger(string token, out BigInteger value) {
    ArgumentNullException.ThrowIfNull(token);
    var parsed = Reader.ParseInteger(token);
    value = parsed ?? BigInteger.Zero;
    return parsed is not null;
  }

  /// <summary>Whether an identifier is a word of the grammar.</summary>
  private static bool IsIdentifierStart(char character) => char.IsAsciiLetter(character) || character == '_';

  private static bool IsIdentifierPart(char character) => char.IsAsciiLetterOrDigit(character) || character == '_';

  /// <summary>The state of one parse: the text and the position in it.</summary>
  private sealed class Reader(string text) {

    private int _position;

    public Assumption? Fact(out string? error) {
      this.SkipSpace();
      if (this.AtEnd) {
        error = "empty assumption";
        return null;
      }

      var subject = this.Path();
      if (subject is null) {
        error = $"expected a path at '{this.Rest}'";
        return null;
      }

      this.SkipSpace();
      Assumption? fact;
      if (this.Take("!=")) {
        this.SkipSpace();
        fact = this.Word() == "null" ? new Assumption(AssumptionKind.NonNull, subject, null, null, null, null, $"{subject} != null") : null;
        error = fact is null ? "expected 'null' after '!='" : null;
      } else {
        fact = this.Word() switch {
          "in" => this.Range(subject, out error),
          "is" => this.Type(subject, out error),
          "noalias" => this.NoAlias(subject, out error),
          _ => Fail("expected 'in', '!=', 'is' or 'noalias' after the path", out error)
        };
      }

      if (fact is null)
        return null;

      this.SkipSpace();
      if (!this.AtEnd)
        return Fail($"unexpected '{this.Rest}' after the assumption", out error);

      return fact;
    }

    private Assumption? Range(string subject, out string? error) {
      this.SkipSpace();
      if (!this.Take("["))
        return Fail("expected '[' after 'in'", out error);

      if (this.Integer(',', out error) is not { } low)
        return null;

      if (!this.Take(","))
        return Fail("expected ',' between the bounds", out error);

      if (this.Integer(']', out error) is not { } high)
        return null;

      if (!this.Take("]"))
        return Fail("expected ']' after the upper bound", out error);

      if (low > high)
        return Fail($"lower bound {low} exceeds upper bound {high}", out error);

      return new Assumption(AssumptionKind.Range, subject, low, high, null, null, $"{subject} in [{low}, {high}]");
    }

    private Assumption? Type(string subject, out string? error) {
      this.SkipSpace();
      var start = this._position;
      while (!this.AtEnd && IsIdentifierPart(text[this._position]))
        ++this._position;

      var type = text[start..this._position];
      if (!AssumptionParser.Types.Contains(type))
        return Fail(type.Length == 0 ? "expected a type after 'is'" : $"unknown type '{type}'", out error);

      error = null;
      return new Assumption(AssumptionKind.Type, subject, null, null, type, null, $"{subject} is {type}");
    }

    private Assumption? NoAlias(string subject, out string? error) {
      this.SkipSpace();
      if (this.Path() is not { } other)
        return Fail("expected a path after 'noalias'", out error);

      error = null;
      return new Assumption(AssumptionKind.NoAlias, subject, null, null, null, other, $"{subject} noalias {other}");
    }

    /// <summary>An integer of the grammar, read up to whitespace or the delimiter that follows it.</summary>
    private BigInteger? Integer(char delimiter, out string? error) {
      this.SkipSpace();
      var start = this._position;
      while (!this.AtEnd && !char.IsWhiteSpace(text[this._position]) && text[this._position] != delimiter && text[this._position] != ']')
        ++this._position;

      var token = text[start..this._position];
      this.SkipSpace();
      if (ParseInteger(token) is { } value) {
        error = null;
        return value;
      }

      error = token.Length == 0 ? "expected an integer" : $"'{token}' is not an integer";
      return null;
    }

    public static BigInteger? ParseInteger(string token) {
      var negative = token.StartsWith('-');
      var digits = negative ? token[1..] : token;
      BigInteger value;
      if (digits.StartsWith("0x", StringComparison.Ordinal)) {
        var hex = digits[2..];
        if (hex.Length == 0 || !hex.All(char.IsAsciiHexDigit))
          return null;

        // A leading zero keeps the value non-negative whatever the first hexadecimal digit is.
        value = BigInteger.Parse("0" + hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
      } else {
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
          return null;

        value = BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
      }

      return negative ? -value : value;
    }

    private string? Path() {
      var start = this._position;
      do {
        if (this.AtEnd || !IsIdentifierStart(text[this._position])) {
          this._position = start;
          return null;
        }

        while (!this.AtEnd && IsIdentifierPart(text[this._position]))
          ++this._position;
      } while (this.Take("."));

      return text[start..this._position];
    }

    /// <summary>The identifier at the position, or the empty text.</summary>
    private string Word() {
      var start = this._position;
      while (!this.AtEnd && IsIdentifierPart(text[this._position]))
        ++this._position;

      return text[start..this._position];
    }

    private bool Take(string token) {
      if (string.CompareOrdinal(text, this._position, token, 0, token.Length) != 0)
        return false;

      this._position += token.Length;
      return true;
    }

    private void SkipSpace() {
      while (!this.AtEnd && char.IsWhiteSpace(text[this._position]))
        ++this._position;
    }

    private bool AtEnd => this._position >= text.Length;

    private string Rest => text[this._position..];

    private static Assumption? Fail(string reason, out string? error) {
      error = reason;
      return null;
    }

  }

}
