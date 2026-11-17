using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Options;

namespace Polypiler.Core.Analysis.Dynamic;

/// <summary>
/// The parser of caller contracts (<c>spec/ir/assumptions.md</c>), a recursive descent over the
/// grammar below; whitespace between tokens is insignificant.
/// <code>
/// assumption := target ":" contract
/// target     := function "." parameter | function ".return" | "global" "." name
/// contract   := scalar | "seq&lt;" contract "&gt;" [ "[" length "]" ] | record
///             | "nullable" "(" contract ")" | "nonnull" "(" contract ")"
/// record     := "record" "{" field ":" contract { "," field ":" contract } "}"
/// scalar     := "u8" | "u16" | "u32" | "u64" | "i8" | "i16" | "i32" | "i64" | "f32" | "f64" | "bool" | "text"
///             | "int" "[" integer "," integer "]"
/// length     := integer | integer ".." integer
/// </code>
/// The language-native spellings are read as their contracts and remembered in
/// <see cref="ContractAssumption.Aliases"/>: the typed arrays of JavaScript (<c>Uint8Array</c> =
/// <c>seq&lt;u8&gt;</c> and its siblings), Python's <c>bytes</c> = <c>seq&lt;u8&gt;</c> and
/// <c>Optional[c]</c> = <c>nullable(c)</c>, Perl's <c>arrayref&lt;c&gt;</c> = <c>seq&lt;c&gt;</c> and
/// <c>hashref{…}</c> = <c>record{…}</c>.
/// </summary>
public static class ContractParser {

  /// <summary>The origin of a contract of <c>--assume</c>.</summary>
  public const string Checked = "--assume";

  /// <summary>The origin of a contract of <c>--assume-unchecked</c>.</summary>
  public const string Unchecked = "--assume-unchecked";

  /// <summary>The origin of a contract of a file, followed by the path and the line.</summary>
  public const string File = "--assume-file";

  /// <summary>The scalars of the grammar.</summary>
  public static IReadOnlyList<string> Scalars { get; } = ["u8", "u16", "u32", "u64", "i8", "i16", "i32", "i64", "f32", "f64", "bool", "text"];

  /// <summary>JavaScript's typed arrays and the element each holds.</summary>
  private static readonly Dictionary<string, string> _typedArrays = new(StringComparer.Ordinal) {
    ["Int8Array"] = "i8", ["Uint8Array"] = "u8", ["Uint8ClampedArray"] = "u8", ["Int16Array"] = "i16", ["Uint16Array"] = "u16",
    ["Int32Array"] = "i32", ["Uint32Array"] = "u32", ["Float32Array"] = "f32", ["Float64Array"] = "f64", ["BigInt64Array"] = "i64", ["BigUint64Array"] = "u64"
  };

  /// <summary>Reads one assumption.</summary>
  /// <param name="text">The text, <c>&lt;target&gt;: &lt;contract&gt;</c>.</param>
  /// <param name="isChecked">Whether the contract is checked at run time.</param>
  /// <param name="origin">Where it was stated.</param>
  /// <param name="result">The assumption, or <see langword="null"/>.</param>
  /// <param name="problem">Why the text is none, the reason of <c>PLY0514</c>; <see langword="null"/> for an assumption.</param>
  /// <returns>Whether the text is an assumption.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static bool TryParse(string text, bool isChecked, string origin, out ContractAssumption? result, out string? problem) {
    ArgumentNullException.ThrowIfNull(text);
    ArgumentNullException.ThrowIfNull(origin);
    var reader = new Reader(text);
    result = reader.Assumption(isChecked, origin, out problem);
    return result is not null;
  }

  /// <summary>Reads one checked assumption of <c>--assume</c>.</summary>
  /// <param name="text">The text.</param>
  /// <returns>The assumption.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
  /// <exception cref="FormatException">The text is no assumption; the message is the problem.</exception>
  public static ContractAssumption Parse(string text)
    => TryParse(text, true, Checked, out var result, out var problem) ? result! : throw new FormatException(problem);

  /// <summary>
  /// Reads a file of assumptions: one per line, a <c>#</c> starts a comment to the end of the line,
  /// blank lines are skipped. Every line that is no assumption, and a file that cannot be read, is
  /// reported as <c>PLY0514</c>.
  /// </summary>
  /// <param name="path">The full path of the file.</param>
  /// <param name="shown">The path as the command line wrote it, for the origin and the diagnostics.</param>
  /// <param name="isChecked">Whether the contracts are checked at run time.</param>
  /// <param name="sink">Receives the diagnostics.</param>
  /// <returns>The assumptions read, in line order.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static IReadOnlyList<ContractAssumption> ParseFile(string path, string shown, bool isChecked, IDiagnosticSink sink) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(shown);
    ArgumentNullException.ThrowIfNull(sink);
    string[] lines;
    try {
      lines = System.IO.File.ReadAllLines(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
      sink.Report(Diagnostic.Create(ContractDiagnostics.Invalid, null, shown, $"cannot read the file: {exception.Message}"));
      return [];
    }

    var assumptions = new List<ContractAssumption>();
    for (var index = 0; index < lines.Length; ++index) {
      var line = lines[index];
      var comment = line.IndexOf('#', StringComparison.Ordinal);
      var text = (comment < 0 ? line : line[..comment]).Trim();
      if (text.Length == 0)
        continue;

      if (TryParse(text, isChecked, $"{File} {shown}:{index + 1}", out var assumption, out var problem))
        assumptions.Add(assumption!);
      else
        sink.Report(Diagnostic.Create(ContractDiagnostics.Invalid, new SourceRange(shown, index + 1, 1, index + 1, 1), text, problem!));
    }

    return assumptions;
  }

  private static bool IsIdentifierStart(char character) => char.IsAsciiLetter(character) || character == '_';

  private static bool IsIdentifierPart(char character) => char.IsAsciiLetterOrDigit(character) || character == '_';

  /// <summary>The state of one parse.</summary>
  private sealed class Reader(string text) {

    private readonly List<(string, string)> _aliases = [];
    private int _position;

    public ContractAssumption? Assumption(bool isChecked, string origin, out string? problem) {
      this.SkipSpace();
      if (this.AtEnd)
        return Refuse("empty assumption", out problem);

      var function = this.Identifier();
      if (function.Length == 0)
        return Refuse("expected a function name or 'global' at the start", out problem);

      this.SkipSpace();
      if (!this.Take("."))
        return Refuse($"expected '.' and a parameter, 'return' or a global name after '{function}'", out problem);

      this.SkipSpace();
      var name = this.Identifier();
      if (name.Length == 0)
        return Refuse($"expected a parameter, 'return' or a global name after '{function}.'", out problem);

      this.SkipSpace();
      if (!this.Take(":"))
        return Refuse("expected ':' after the target", out problem);

      if (this.Contract(out problem) is not { } contract)
        return null;

      this.SkipSpace();
      if (!this.AtEnd)
        return Refuse($"unexpected '{text[this._position..]}' after the contract", out problem);

      return new ContractAssumption($"{function}.{name}", contract, isChecked, origin) { Aliases = this._aliases };
    }

    private Contract? Contract(out string? problem) {
      this.SkipSpace();
      var word = this.Identifier();
      problem = null;
      switch (word) {
        case "int":
          return this.Range(out problem);
        case "seq":
          return this.Sequence('<', '>', out problem);
        case "arrayref":
          this._aliases.Add((word, "perl"));
          return this.Sequence('<', '>', out problem);
        case "record":
          return this.Record(out problem);
        case "hashref":
          this._aliases.Add((word, "perl"));
          return this.Record(out problem);
        case "nullable":
          return this.Wrapped('(', ')', out problem) is { } inner ? new NullableContract(inner) : null;
        case "Optional":
          this._aliases.Add((word, "python"));
          return this.Wrapped('[', ']', out problem) is { } optional ? new NullableContract(optional) : null;
        case "nonnull":
          // nonnull(c) is c: a contract excludes the absent value unless it is nullable.
          return this.Wrapped('(', ')', out problem) is { } present ? (present as NullableContract)?.Inner ?? present : null;
        case "bytes":
          this._aliases.Add((word, "python"));
          return new SequenceContract(new ScalarContract("u8"), null, null);
        case var typed when _typedArrays.TryGetValue(typed, out var element):
          this._aliases.Add((typed, "javascript"));
          return new SequenceContract(new ScalarContract(element), null, null);
        case var scalar when Scalars.Contains(scalar):
          return new ScalarContract(scalar);
        case "":
          return Fail(this.AtEnd ? "expected a contract" : $"expected a contract at '{text[this._position..]}'", out problem);
        default:
          return Fail($"unknown contract '{word}'", out problem);
      }
    }

    private Contract? Range(out string? problem) {
      this.SkipSpace();
      if (!this.Take("["))
        return Fail("expected '[' after 'int'", out problem);

      if (this.Integer(out problem) is not { } low)
        return null;

      this.SkipSpace();
      if (!this.Take(","))
        return Fail("expected ',' between the bounds", out problem);

      if (this.Integer(out problem) is not { } high)
        return null;

      this.SkipSpace();
      if (!this.Take("]"))
        return Fail("expected ']' after the upper bound", out problem);

      return low > high ? Fail($"lower bound {low} exceeds upper bound {high}", out problem) : new IntegerContract(low, high);
    }

    private Contract? Sequence(char open, char close, out string? problem) {
      if (this.Wrapped(open, close, out problem) is not { } element)
        return null;

      this.SkipSpace();
      if (!this.Take("["))
        return new SequenceContract(element, null, null);

      if (this.Length(out problem) is not { } shortest)
        return null;

      var longest = shortest;
      this.SkipSpace();
      if (this.Take("..")) {
        if (this.Length(out problem) is not { } bound)
          return null;

        longest = bound;
        this.SkipSpace();
      }

      if (!this.Take("]"))
        return Fail("expected ']' after the length", out problem);

      return shortest > longest ? Fail($"shortest length {shortest} exceeds longest length {longest}", out problem) : new SequenceContract(element, shortest, longest);
    }

    private BigInteger? Length(out string? problem) {
      if (this.Integer(out problem) is not { } length)
        return null;

      if (length >= 0)
        return length;

      problem = $"length {length} is negative";
      return null;
    }

    private Contract? Wrapped(char open, char close, out string? problem) {
      this.SkipSpace();
      if (!this.Take(open.ToString()))
        return Fail($"expected '{open}'", out problem);

      if (this.Contract(out problem) is not { } inner)
        return null;

      this.SkipSpace();
      return this.Take(close.ToString()) ? inner : Fail($"expected '{close}' after '{inner.Text}'", out problem);
    }

    private Contract? Record(out string? problem) {
      this.SkipSpace();
      if (!this.Take("{"))
        return Fail("expected '{' after 'record'", out problem);

      var fields = new List<ContractField>();
      do {
        this.SkipSpace();
        var name = this.Identifier();
        if (name.Length == 0)
          return Fail(fields.Count == 0 && this.Peek('}') ? "a record lists at least one field" : "expected a field name", out problem);

        if (fields.Any(field => field.Name == name))
          return Fail($"the field '{name}' is listed twice", out problem);

        this.SkipSpace();
        if (!this.Take(":"))
          return Fail($"expected ':' after the field '{name}'", out problem);

        if (this.Contract(out problem) is not { } contract)
          return null;

        fields.Add(new ContractField(name, contract));
        this.SkipSpace();
      } while (this.Take(","));

      return this.Take("}") ? new RecordContract(fields) : Fail("expected ',' or '}' after a field", out problem);
    }

    private BigInteger? Integer(out string? problem) {
      this.SkipSpace();
      var start = this._position;
      while (!this.AtEnd && (char.IsAsciiLetterOrDigit(text[this._position]) || text[this._position] == '-'))
        ++this._position;

      var token = text[start..this._position];
      if (AssumptionParser.TryParseInteger(token, out var value)) {
        problem = null;
        return value;
      }

      problem = token.Length == 0 ? "expected an integer" : $"'{token}' is not an integer";
      return null;
    }

    private string Identifier() {
      if (this.AtEnd || !IsIdentifierStart(text[this._position]))
        return "";

      var start = this._position;
      while (!this.AtEnd && IsIdentifierPart(text[this._position]))
        ++this._position;

      return text[start..this._position];
    }

    private bool Peek(char character) => !this.AtEnd && text[this._position] == character;

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

    private static Contract? Fail(string reason, out string? problem) {
      problem = reason;
      return null;
    }

    private static ContractAssumption? Refuse(string reason, out string? problem) {
      problem = reason;
      return null;
    }

  }

}
