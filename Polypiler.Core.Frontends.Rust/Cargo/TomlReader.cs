using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Polypiler.Core.Frontends.Rust.Cargo;

/// <summary>A TOML document that is not well-formed, with the line where the reader stopped.</summary>
/// <param name="message">What is wrong.</param>
/// <param name="line">The 1-based line.</param>
public sealed class TomlException(string message, int line) : Exception($"line {line}: {message}") {

  /// <summary>The 1-based line where the reader stopped.</summary>
  public int Line { get; } = line;

}

/// <summary>A TOML value the reader keeps only as its text: an integer, a float, a date or a time.</summary>
/// <param name="Text">The text as written.</param>
public sealed record TomlRaw(string Text);

/// <summary>
/// Reads a TOML 1.0.0 document (https://toml.io/en/v1.0.0) into nested tables: a table is a
/// <see cref="Dictionary{TKey, TValue}"/> of <see cref="string"/> to its values, an array a
/// <see cref="List{T}"/> of <see cref="object"/>, a string a <see cref="string"/>, a boolean a
/// <see cref="bool"/> and every other scalar (numbers, dates, times) a <see cref="TomlRaw"/> with
/// its text. It reads every form a <c>Cargo.toml</c> may use - tables and arrays of tables, dotted
/// and quoted keys, inline tables, the four kinds of string, nested arrays across lines - and
/// rejects keys and tables defined twice; it does not validate the text of numbers and dates.
/// </summary>
public static class TomlReader {

  /// <summary>Reads a document.</summary>
  /// <param name="text">The text of the document.</param>
  /// <returns>The root table.</returns>
  /// <exception cref="TomlException">The document is not well-formed.</exception>
  public static Dictionary<string, object> Read(string text) {
    ArgumentNullException.ThrowIfNull(text);
    return new Cursor(text).Document();
  }

  private sealed class Cursor(string text) {

    private readonly string _text = text;
    private readonly HashSet<Dictionary<string, object>> _defined = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Dictionary<string, object>> _inline = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<List<object>> _tableArrays = new(ReferenceEqualityComparer.Instance);
    private int _position;
    private int _line = 1;

    private char Current => this._position < this._text.Length ? this._text[this._position] : '\0';

    private bool AtEnd => this._position >= this._text.Length;

    private TomlException Error(string message) => new(message, this._line);

    private void Advance() {
      if (this.Current == '\n')
        ++this._line;
      ++this._position;
    }

    private bool StartsWith(string prefix) => string.CompareOrdinal(this._text, this._position, prefix, 0, prefix.Length) == 0;

    /// <summary>Skips spaces and tabs.</summary>
    private void Blank() {
      while (this.Current is ' ' or '\t')
        this.Advance();
    }

    /// <summary>Skips spaces, tabs, comments and line breaks.</summary>
    private void Space() {
      while (true) {
        this.Blank();
        if (this.Current == '#')
          this.Comment();
        else if (this.Current == '\n' || this.StartsWith("\r\n"))
          this.Newline();
        else
          return;
      }
    }

    private void Comment() {
      while (!this.AtEnd && this.Current != '\n' && !this.StartsWith("\r\n"))
        this.Advance();
    }

    private void Newline() {
      if (this.Current == '\r')
        this.Advance();
      this.Advance();
    }

    /// <summary>The end of a line: blanks, an optional comment, then a line break or the end.</summary>
    private void EndOfLine() {
      this.Blank();
      if (this.Current == '#')
        this.Comment();
      if (this.AtEnd)
        return;
      if (this.Current != '\n' && !this.StartsWith("\r\n"))
        throw this.Error($"unexpected '{this.Current}' after a value");
      this.Newline();
    }

    public Dictionary<string, object> Document() {
      var root = new Dictionary<string, object>(StringComparer.Ordinal);
      var current = root;
      while (true) {
        this.Space();
        if (this.AtEnd)
          return root;
        current = this.Current == '[' ? this.Header(root) : this.KeyValue(current, current);
        this.EndOfLine();
      }
    }

    /// <summary>A table header, <c>[a.b]</c>, or an array-of-tables header, <c>[[a.b]]</c>; the table the next keys go to.</summary>
    private Dictionary<string, object> Header(Dictionary<string, object> root) {
      this.Advance();
      var array = this.Current == '[';
      if (array)
        this.Advance();
      this.Blank();
      var keys = this.Key();
      if (this.Current != ']' || (array && !this.StartsWith("]]")))
        throw this.Error(array ? "an array-of-tables header ends with ']]'" : "a table header ends with ']'");
      this.Advance();
      if (array)
        this.Advance();

      var parent = this.Walk(root, keys[..^1]);
      var last = keys[^1];
      if (array) {
        var table = new Dictionary<string, object>(StringComparer.Ordinal);
        if (!parent.TryGetValue(last, out var existing)) {
          var tables = new List<object> { table };
          this._tableArrays.Add(tables);
          parent[last] = tables;
        } else if (existing is List<object> list && this._tableArrays.Contains(list))
          list.Add(table);
        else
          throw this.Error($"'{last}' is no array of tables");
        this._defined.Add(table);
        return table;
      }

      if (!parent.TryGetValue(last, out var found)) {
        var table = new Dictionary<string, object>(StringComparer.Ordinal);
        parent[last] = table;
        this._defined.Add(table);
        return table;
      }

      if (found is not Dictionary<string, object> defined || this._inline.Contains(defined) || !this._defined.Add(defined))
        throw this.Error($"table '{string.Join('.', keys)}' is defined twice");
      return defined;
    }

    /// <summary>The table at a path of keys below a table, creating the missing ones; the last table of an array of tables.</summary>
    private Dictionary<string, object> Walk(Dictionary<string, object> table, IEnumerable<string> keys) {
      foreach (var key in keys) {
        if (!table.TryGetValue(key, out var next)) {
          var created = new Dictionary<string, object>(StringComparer.Ordinal);
          table[key] = created;
          table = created;
          continue;
        }

        table = next switch {
          Dictionary<string, object> inner when !this._inline.Contains(inner) => inner,
          List<object> tables when this._tableArrays.Contains(tables) => (Dictionary<string, object>)tables[^1],
          _ => throw this.Error($"'{key}' is no table")
        };
      }

      return table;
    }

    /// <summary>A pair <c>key = value</c> into a table; returns <paramref name="next"/>, the table the following pairs go to.</summary>
    private Dictionary<string, object> KeyValue(Dictionary<string, object> table, Dictionary<string, object> next) {
      var keys = this.Key();
      if (this.Current != '=')
        throw this.Error("a key is followed by '='");
      this.Advance();
      this.Blank();
      var value = this.Value();
      var target = this.Walk(table, keys[..^1]);
      if (!target.TryAdd(keys[^1], value))
        throw this.Error($"key '{string.Join('.', keys)}' is defined twice");
      return next;
    }

    /// <summary>A dotted key, <c>a."b c".'d'</c>, and the blanks after it.</summary>
    private string[] Key() {
      var keys = new List<string>();
      while (true) {
        keys.Add(this.Current switch {
          '"' => this.BasicString(),
          '\'' => this.LiteralString(),
          _ => this.BareKey()
        });
        this.Blank();
        if (this.Current != '.')
          return [.. keys];
        this.Advance();
        this.Blank();
      }
    }

    private string BareKey() {
      var start = this._position;
      while (char.IsAsciiLetterOrDigit(this.Current) || this.Current is '_' or '-')
        this.Advance();
      if (this._position == start)
        throw this.Error(this.AtEnd ? "a key is missing at the end" : $"unexpected '{this.Current}' where a key belongs");
      return this._text[start..this._position];
    }

    private object Value() {
      if (this.StartsWith("\"\"\""))
        return this.MultilineBasicString();
      if (this.StartsWith("'''"))
        return this.MultilineLiteralString();
      switch (this.Current) {
        case '"':
          return this.BasicString();
        case '\'':
          return this.LiteralString();
        case '[':
          return this.Array();
        case '{':
          return this.InlineTable();
      }

      var start = this._position;
      this.Token();
      // A date and a time may be separated by a space (TOML 1.0.0, "Offset Date-Time").
      if (this._position - start == 10 && this._text[start + 4] == '-' && this._text[start + 7] == '-'
        && this.Current == ' ' && this._position + 1 < this._text.Length && char.IsAsciiDigit(this._text[this._position + 1])) {
        this.Advance();
        this.Token();
      }

      var token = this._text[start..this._position];
      return token switch {
        "" => throw this.Error("a value is missing"),
        "true" => true,
        "false" => false,
        _ => new TomlRaw(token)
      };
    }

    /// <summary>The characters of a scalar that is neither a string nor a boolean: up to white space, a delimiter or a comment.</summary>
    private void Token() {
      while (!this.AtEnd && this.Current is not (',' or ']' or '}' or '#' or '\n' or '\r' or ' ' or '\t'))
        this.Advance();
    }

    private List<object> Array() {
      this.Advance();
      var values = new List<object>();
      while (true) {
        this.Space();
        if (this.Current == ']') {
          this.Advance();
          return values;
        }

        values.Add(this.Value());
        this.Space();
        if (this.Current == ',')
          this.Advance();
        else if (this.Current != ']')
          throw this.Error("the values of an array are separated by ','");
      }
    }

    private Dictionary<string, object> InlineTable() {
      this.Advance();
      var table = new Dictionary<string, object>(StringComparer.Ordinal);
      this.Blank();
      if (this.Current == '}') {
        this.Advance();
        this._inline.Add(table);
        return table;
      }

      while (true) {
        this.Blank();
        this.KeyValue(table, table);
        this.Blank();
        if (this.Current == '}') {
          this.Advance();
          this._inline.Add(table);
          return table;
        }

        if (this.Current != ',')
          throw this.Error("the pairs of an inline table are separated by ',' on one line");
        this.Advance();
      }
    }

    private string LiteralString() {
      this.Advance();
      var start = this._position;
      while (this.Current != '\'') {
        if (this.AtEnd || this.Current == '\n')
          throw this.Error("a string is not closed on its line");
        this.Advance();
      }

      var value = this._text[start..this._position];
      this.Advance();
      return value;
    }

    private string MultilineLiteralString() {
      this._position += 3;
      if (this.Current == '\n' || this.StartsWith("\r\n"))
        this.Newline();
      var start = this._position;
      while (!this.StartsWith("'''") || this.StartsWith("''''")) {
        if (this.AtEnd)
          throw this.Error("a multi-line string is not closed");
        this.Advance();
      }

      var value = this._text[start..this._position];
      this._position += 3;
      return value;
    }

    private string BasicString() {
      this.Advance();
      var value = new StringBuilder();
      while (this.Current != '"') {
        if (this.AtEnd || this.Current == '\n')
          throw this.Error("a string is not closed on its line");
        if (this.Current == '\\')
          this.Escape(value);
        else {
          value.Append(this.Current);
          this.Advance();
        }
      }

      this.Advance();
      return value.ToString();
    }

    private string MultilineBasicString() {
      this._position += 3;
      if (this.Current == '\n' || this.StartsWith("\r\n"))
        this.Newline();
      var value = new StringBuilder();
      while (!this.StartsWith("\"\"\"") || this.StartsWith("\"\"\"\"")) {
        if (this.AtEnd)
          throw this.Error("a multi-line string is not closed");
        if (this.Current != '\\') {
          value.Append(this.Current);
          this.Advance();
          continue;
        }

        // A backslash at the end of a line removes the line break and the white space after it.
        var after = this._position + 1;
        while (after < this._text.Length && this._text[after] is ' ' or '\t')
          ++after;
        if (after < this._text.Length && this._text[after] is '\n' or '\r') {
          this._position = after;
          this.Space();
        } else
          this.Escape(value);
      }

      this._position += 3;
      return value.ToString();
    }

    private void Escape(StringBuilder value) {
      this.Advance();
      var escape = this.Current;
      this.Advance();
      switch (escape) {
        case 'b': value.Append('\b'); return;
        case 't': value.Append('\t'); return;
        case 'n': value.Append('\n'); return;
        case 'f': value.Append('\f'); return;
        case 'r': value.Append('\r'); return;
        case '"': value.Append('"'); return;
        case '\\': value.Append('\\'); return;
        case 'u': value.Append(this.CodePoint(4)); return;
        case 'U': value.Append(this.CodePoint(8)); return;
        default: throw this.Error($"unknown escape '\\{escape}'");
      }
    }

    private string CodePoint(int digits) {
      if (this._position + digits > this._text.Length
        || !int.TryParse(this._text.AsSpan(this._position, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
        || code is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
        throw this.Error($"an escape takes {digits} hexadecimal digits of a Unicode scalar value");
      this._position += digits;
      return char.ConvertFromUtf32(code);
    }

  }

}
