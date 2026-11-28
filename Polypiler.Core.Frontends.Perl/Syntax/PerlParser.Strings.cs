using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Polypiler.Core.Frontends.Perl.Ast;

namespace Polypiler.Core.Frontends.Perl.Syntax;

internal sealed partial class PerlParser {

  /// <summary>A string, a quote-like, a pattern, a heredoc or a glob token as an expression.</summary>
  private PerlExpr ParseQuoted(PerlToken token) {
    var line = token.Line;
    var body = token.Body!;
    switch (token.Kind) {
      case PerlTokenKind.Heredoc:
        return token.Interpolates
          ? this.Interpolate(body, pattern: false, line)
          : new PerlString(this.Bytes(body.Value)) { Line = line };
      case PerlTokenKind.Glob:
        return new PerlReadLine(token.Text, true) { Line = line };
      case PerlTokenKind.Match:
        return new PerlPattern(token.Keyword, this.PatternBody(body, line), null, token.Flags) { Line = line };
      case PerlTokenKind.Substitute: {
        var pattern = this.PatternBody(body, line);
        PerlExpr replacement;
        if (token.Flags.Contains('e', StringComparison.Ordinal)) {
          if (token.Flags.IndexOf('e') != token.Flags.LastIndexOf('e'))
            throw new PerlUnsupportedException("s///ee, which evaluates a string", line);
          replacement = this.ParseEmbedded(token.Replacement!.Text, 0, token.Replacement.Text.Length, block: true);
        } else {
          replacement = token.Replacement!.Open == '\'' ? new PerlString(this.Bytes(token.Replacement.Value)) { Line = line } : this.Interpolate(token.Replacement!, pattern: false, line);
        }
        return new PerlPattern("s", pattern, replacement, token.Flags) { Line = line };
      }
      case PerlTokenKind.Transliterate:
        return new PerlPattern("tr", new PerlString(body.Value) { Line = line }, new PerlString(token.Replacement!.Value) { Line = line }, token.Flags) { Line = line };
    }
    switch (token.Keyword) {
      case "'" or "q":
        return new PerlString(this.Bytes(SingleQuoted(body))) { Line = line };
      case "qw":
        return new PerlWords(Words(body)) { Line = line };
      case "`" or "qx":
        return new PerlCommand(body.Open == '\'' ? new PerlString(body.Value) { Line = line } : this.Interpolate(body, pattern: false, line)) { Line = line };
      default:
        return this.Interpolate(body, pattern: false, line);
    }
  }

  private PerlExpr PatternBody(PerlQuote body, int line) {
    if (body.Value.Contains("(?{", StringComparison.Ordinal) || body.Value.Contains("(??{", StringComparison.Ordinal) || body.Value.Contains("(*{", StringComparison.Ordinal))
      throw new PerlUnsupportedException("a code block in a regular expression", line);
    return this.PatternText(body, line);
  }

  private PerlExpr PatternText(PerlQuote body, int line)
    => body.Open == '\'' ? new PerlString(body.Value) { Line = line } : this.Interpolate(body, pattern: true, line);

  /// <summary>The value of a single-quoted body: <c>\\</c> and a backslashed delimiter lose the backslash.</summary>
  private static string SingleQuoted(PerlQuote body) {
    var value = body.Value;
    if (!value.Contains('\\', StringComparison.Ordinal))
      return value;
    var builder = new StringBuilder(value.Length);
    for (var i = 0; i < value.Length; ++i) {
      var c = value[i];
      if (c == '\\' && i + 1 < value.Length && (value[i + 1] == '\\' || value[i + 1] == body.Open || value[i + 1] == body.Close)) {
        builder.Append(value[i + 1]);
        ++i;
        continue;
      }
      builder.Append(c);
    }
    return builder.ToString();
  }

  private static List<string> Words(PerlQuote body) {
    var words = new List<string>();
    foreach (var word in SingleQuoted(body).Split([' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries))
      words.Add(word);
    return words;
  }

  /// <summary>Source characters beyond ASCII as perl sees them without <c>use utf8</c>: the bytes of their UTF-8.</summary>
  private string Bytes(string text) {
    if (this._scope.Utf8)
      return text;
    var ascii = true;
    foreach (var c in text)
      if (c > 0x7F) {
        ascii = false;
        break;
      }
    if (ascii)
      return text;
    var builder = new StringBuilder(text.Length);
    foreach (var b in Encoding.UTF8.GetBytes(text))
      builder.Append((char)b);
    return builder.ToString();
  }

  /// <summary>Parses the code at <c>[start, end)</c> of a text: an expression, or with <paramref name="block"/> statements whose last value counts.</summary>
  private PerlExpr ParseEmbedded(PerlText text, int start, int end, bool block) {
    var saved = this._lexer;
    this._lexer = new PerlLexer(text, start, end, this, nested: true);
    try {
      var line = this._lexer.Line;
      if (block) {
        var statements = this.InScope(() => this.ParseStatements(closing: false));
        return new PerlDoBlock(new PerlBlock(statements) { Line = line }) { Line = line };
      }
      var expression = this.ParseExpression();
      var token = this.PeekOperator();
      if (token.Kind != PerlTokenKind.End)
        throw this.Unexpected(token, "the end of the interpolated expression");
      return expression;
    } finally {
      this._lexer = saved;
    }
  }

  private sealed class CaseGroup(string function) {
    public string Function { get; } = function;
    public List<PerlExpr> Parts { get; } = [];
  }

  /// <summary>
  /// The parts of an interpolating body: literal text, with escapes processed in a string and kept in
  /// a pattern, and the variables, elements, slices and <c>${...}</c>/<c>@{...}</c> blocks it interpolates.
  /// </summary>
  private PerlExpr Interpolate(PerlQuote body, bool pattern, int line) {
    var text = body.Text;
    var value = text.Value;
    var groups = new Stack<CaseGroup>();
    groups.Push(new CaseGroup(""));
    var literal = new StringBuilder();
    string? pendingFirst = null;

    void Flush() {
      if (literal.Length == 0)
        return;
      var chunk = literal.ToString();
      if (pendingFirst is not null && chunk.Length > 0) {
        var first = pendingFirst == "ucfirst" ? chunk[..1].ToUpperInvariant() : chunk[..1].ToLowerInvariant();
        chunk = first + chunk[1..];
        pendingFirst = null;
      }
      groups.Peek().Parts.Add(new PerlString(chunk) { Line = line });
      literal.Clear();
    }

    void Add(PerlExpr part) {
      Flush();
      if (pendingFirst is not null) {
        part = new PerlCall(pendingFirst, [part]) { Line = line, Builtin = true };
        pendingFirst = null;
      }
      groups.Peek().Parts.Add(part);
    }

    void Close() {
      Flush();
      var group = groups.Pop();
      var joined = Join(group.Parts, line);
      groups.Peek().Parts.Add(new PerlCall(group.Function, [joined]) { Line = line, Builtin = true });
    }

    var i = 0;
    while (i < value.Length) {
      var c = value[i];
      if (c == '\\' && i + 1 < value.Length) {
        var e = value[i + 1];
        switch (e) {
          case 'U' or 'L' or 'Q' or 'F':
            Flush();
            groups.Push(new CaseGroup(e switch { 'U' => "uc", 'L' => "lc", 'Q' => "quotemeta", _ => "fc" }));
            i += 2;
            continue;
          case 'E':
            if (groups.Count > 1)
              Close();
            i += 2;
            continue;
          case 'u' or 'l':
            Flush();
            pendingFirst = e == 'u' ? "ucfirst" : "lcfirst";
            i += 2;
            continue;
        }
        if (pattern) {
          literal.Append(c).Append(this.Bytes(e.ToString()));
          i += 2;
          continue;
        }
        i = this.Escape(value, i, literal, line);
        continue;
      }
      if (c == '$' && this.InterpolatesScalar(value, i, pattern)) {
        var (expression, end) = this.InterpolatedScalar(text, i, pattern, line);
        Add(expression);
        i = end;
        continue;
      }
      if (c == '@' && InterpolatesArray(value, i)) {
        var (expression, end) = this.InterpolatedArray(text, i, pattern, line);
        Add(new PerlCall("join", [new PerlVariable('$', "\"", null) { Line = line }, expression]) { Line = line, Builtin = true });
        i = end;
        continue;
      }
      if (c == '$' && !pattern && i + 1 >= value.Length)
        throw this.Error("Final $ should be \\$ or $name", line);
      if (c > 0x7F)
        literal.Append(this.Bytes(c.ToString()));
      else
        literal.Append(c);
      ++i;
    }
    while (groups.Count > 1)
      Close();
    Flush();
    return Join(groups.Pop().Parts, line);
  }

  private static PerlExpr Join(List<PerlExpr> parts, int line) {
    var merged = new List<PerlExpr>();
    foreach (var part in parts) {
      if (part is PerlString s && merged.Count > 0 && merged[^1] is PerlString previous) {
        merged[^1] = new PerlString(previous.Value + s.Value) { Line = previous.Line };
        continue;
      }
      merged.Add(part);
    }
    if (merged.Count == 0)
      return new PerlString("") { Line = line };
    if (merged is [PerlString only])
      return only;
    return new PerlInterpolation(merged) { Line = line };
  }

  private int Escape(string value, int i, StringBuilder literal, int line) {
    var e = value[i + 1];
    switch (e) {
      case 'n': literal.Append('\n'); return i + 2;
      case 't': literal.Append('\t'); return i + 2;
      case 'r': literal.Append('\r'); return i + 2;
      case 'f': literal.Append('\f'); return i + 2;
      case 'b': literal.Append('\b'); return i + 2;
      case 'a': literal.Append('\a'); return i + 2;
      case 'e': literal.Append('\x1b'); return i + 2;
      case 'c': {
        if (i + 2 >= value.Length)
          throw this.Error("Missing control char name in \\c", line);
        var ch = char.ToUpperInvariant(value[i + 2]);
        literal.Append((char)(ch ^ 64));
        return i + 3;
      }
      case 'x': {
        var j = i + 2;
        int code;
        if (j < value.Length && value[j] == '{') {
          var close = value.IndexOf('}', j);
          if (close < 0)
            throw this.Error("Missing right brace on \\x{}", line);
          var hex = value[(j + 1)..close].Replace("_", "", StringComparison.Ordinal).Trim();
          code = hex.Length == 0 ? 0 : int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : 0;
          j = close + 1;
        } else {
          code = 0;
          var digits = 0;
          while (j < value.Length && digits < 2 && Uri.IsHexDigit(value[j])) {
            code = code * 16 + Convert.ToInt32(value[j].ToString(), 16);
            ++j;
            ++digits;
          }
        }
        AppendCode(literal, code);
        return j;
      }
      case 'o' when i + 2 < value.Length && value[i + 2] == '{': {
        var close = value.IndexOf('}', i + 2);
        if (close < 0)
          throw this.Error("Missing right brace on \\o{}", line);
        var octal = value[(i + 3)..close].Trim();
        var code = 0;
        foreach (var d in octal)
          if (d is >= '0' and <= '7')
            code = code * 8 + (d - '0');
        AppendCode(literal, code);
        return close + 1;
      }
      case 'N': {
        if (i + 2 < value.Length && value[i + 2] == '{') {
          var close = value.IndexOf('}', i + 2);
          if (close < 0)
            throw this.Error("Missing right brace on \\N{}", line);
          var name = value[(i + 3)..close];
          if (name.StartsWith("U+", StringComparison.Ordinal) && int.TryParse(name[2..].Replace("_", "", StringComparison.Ordinal), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) {
            AppendCode(literal, code);
            return close + 1;
          }
          if (name.StartsWith("U+", StringComparison.Ordinal))
            throw this.Error("Invalid hexadecimal number in \\N{U+...}", line);
          throw new PerlUnsupportedException($"the named character \\N{{{name}}}", line);
        }
        throw new PerlUnsupportedException("\\N without braces", line);
      }
      case >= '0' and <= '7': {
        var j = i + 1;
        var code = 0;
        var digits = 0;
        while (j < value.Length && digits < 3 && value[j] is >= '0' and <= '7') {
          code = code * 8 + (value[j] - '0');
          ++j;
          ++digits;
        }
        AppendCode(literal, code);
        return j;
      }
      default:
        literal.Append(e);
        return i + 2;
    }
  }

  private static void AppendCode(StringBuilder literal, int code) {
    if (code is < 0 or > 0x10FFFF)
      code = 0xFFFD;
    if (code is >= 0xD800 and <= 0xDFFF)
      literal.Append((char)code);
    else
      literal.Append(char.ConvertFromUtf32(code));
  }

  private bool InterpolatesScalar(string value, int i, bool pattern) {
    if (i + 1 >= value.Length)
      return false;
    var c = value[i + 1];
    if (PerlLexer.IsIdentifierStart(c, this._scope.Utf8) || char.IsAsciiDigit(c) || c == '{' || c == ':' && i + 2 < value.Length && value[i + 2] == ':')
      return c != ':' || i + 3 < value.Length && PerlLexer.IsIdentifierStart(value[i + 3], this._scope.Utf8);
    if (c == '$')
      return !pattern || i + 2 < value.Length && (PerlLexer.IsIdentifierStart(value[i + 2], this._scope.Utf8) || value[i + 2] is '{' or '$');
    if (c == '^' && i + 2 < value.Length && char.IsAsciiLetterUpper(value[i + 2]))
      return true;
    return (pattern ? "&`'+_.,;!<\"" : "&`'+_./,;!@<>[]\"-=~%?\\()|").Contains(c, StringComparison.Ordinal);
  }

  private static bool InterpolatesArray(string value, int i) {
    if (i + 1 >= value.Length)
      return false;
    var c = value[i + 1];
    return char.IsAsciiLetter(c) || c is '_' or '{' or '$' || c == ':' && i + 2 < value.Length && value[i + 2] == ':';
  }

  /// <summary>The extent of a name at <paramref name="i"/>: an identifier (with <c>::</c>), digits, or <c>^X</c>.</summary>
  private int NameEnd(string value, int i) {
    var j = i;
    if (j < value.Length && char.IsAsciiDigit(value[j])) {
      while (j < value.Length && char.IsAsciiDigit(value[j]))
        ++j;
      return j;
    }
    if (j < value.Length && value[j] == '^')
      return j + 2;
    while (j < value.Length) {
      if (value[j] == ':' && j + 2 < value.Length && value[j + 1] == ':' && PerlLexer.IsIdentifierStart(value[j + 2], this._scope.Utf8)) {
        j += 2;
        continue;
      }
      if (this.Feature("apostrophe_as_package_separator") && value[j] == '\'' && j > i && j + 1 < value.Length && PerlLexer.IsIdentifierStart(value[j + 1], this._scope.Utf8)) {
        ++j;
        continue;
      }
      if (!PerlLexer.IsWordChar(value[j], this._scope.Utf8))
        break;
      ++j;
    }
    return j;
  }

  private static int Matching(string value, int open) {
    var opening = value[open];
    var closing = opening switch { '[' => ']', '{' => '}', _ => ')' };
    var depth = 0;
    for (var i = open; i < value.Length; ++i) {
      var c = value[i];
      if (c == '\\') {
        ++i;
        continue;
      }
      if (c == opening)
        ++depth;
      else if (c == closing && --depth == 0)
        return i;
    }
    return -1;
  }

  private (PerlExpr Expression, int End) InterpolatedScalar(PerlText text, int i, bool pattern, int line) {
    var value = text.Value;
    var j = i + 1;
    PerlExpr scalar;
    if (value[j] == '{') {
      var close = Matching(value, j);
      if (close < 0)
        throw this.Error("Missing right curly in interpolated ${", line);
      var inner = value[(j + 1)..close].Trim();
      if (inner.Length == 1 && "&`'+_./,;!@<>[]\"-=~%?\\()|0123456789".Contains(inner[0], StringComparison.Ordinal))
        return (this.Variable('$', inner, line), close + 1);
      if (inner.Length > 0 && (PerlLexer.IsIdentifierStart(inner[0], this._scope.Utf8) || inner[0] == '^') && this.NameEnd(inner, inner[0] == '^' ? 1 : 0) == inner.Length && inner != "^") {
        var name = inner;
        return (this.Variable('$', name, line), close + 1);
      }
      var code = this.ParseEmbedded(text, j + 1, close, block: true);
      var reference = code is PerlDoBlock { Body.Statements: [PerlExpressionStatement { Modifier: null } only] } ? only.Expression : code;
      scalar = new PerlDeref('$', reference) { Line = line };
      return this.SubscriptsOf(text, close + 1, pattern, line, scalar, '$', derefOf: reference);
    }
    if (value[j] == '$' && j + 1 < value.Length && PerlLexer.IsIdentifierStart(value[j + 1], this._scope.Utf8)) {
      var nameEnd = this.NameEnd(value, j + 1);
      var reference = this.Variable('$', value[(j + 1)..nameEnd].Replace("'", "::", StringComparison.Ordinal), line);
      return this.SubscriptsOf(text, nameEnd, pattern, line, new PerlDeref('$', reference) { Line = line }, '$', derefOf: reference);
    }
    if (value[j] == '$' && j + 1 < value.Length && value[j + 1] is '{' or '$') {
      var (inner, end) = this.InterpolatedScalar(text, j, pattern: false, line);
      if (inner is PerlElement or PerlCall)
        throw new PerlUnsupportedException("a subscripted dereference in a string", line);
      return this.SubscriptsOf(text, end, pattern, line, new PerlDeref('$', inner) { Line = line }, '$', derefOf: inner);
    }
    if (PerlLexer.IsIdentifierStart(value[j], this._scope.Utf8) || char.IsAsciiDigit(value[j]) || value[j] == '^' || value[j] == ':') {
      var end = this.NameEnd(value, value[j] == ':' ? j + 2 : j);
      var name = value[j..end];
      if (name.StartsWith("::", StringComparison.Ordinal))
        name = "main" + name;
      name = name.Replace("'", "::", StringComparison.Ordinal);
      return this.Subscripts(text, end, pattern, line, name, '$');
    }
    return this.Subscripts(text, j + 1, pattern, line, value[j].ToString(), '$', special: true);
  }

  private (PerlExpr Expression, int End) Subscripts(PerlText text, int at, bool pattern, int line, string name, char sigil, bool special = false) {
    var value = text.Value;
    if (!special || name is "_" or "+" or "-")
      if (at < value.Length && (value[at] == '[' && this.IsElementSubscript(value, at, pattern) || value[at] == '{' && this.IsHashSubscript(value, at, pattern))) {
        var hash = value[at] == '{';
        var aggregate = this.Variable(hash ? '%' : '@', name, line);
        var (index, end) = this.SubscriptContent(text, at, hash, line);
        return this.SubscriptsOf(text, end, pattern, line, new PerlElement(aggregate, index, hash) { Line = line }, '$', chained: true);
      }
    return this.SubscriptsOf(text, at, pattern, line, this.Variable(sigil, name, line), '$');
  }

  private (PerlExpr Expression, int End) SubscriptsOf(PerlText text, int at, bool pattern, int line, PerlExpr expression, char sigil, bool chained = false, PerlExpr? derefOf = null) {
    var value = text.Value;
    if (derefOf is not null && at < value.Length && (value[at] == '[' && this.IsElementSubscript(value, at, pattern) || value[at] == '{' && this.IsHashSubscript(value, at, pattern))) {
      var hash = value[at] == '{';
      var (index, end) = this.SubscriptContent(text, at, hash, line);
      expression = new PerlElement(new PerlDeref(hash ? '%' : '@', derefOf) { Line = line }, index, hash) { Line = line };
      at = end;
      chained = true;
    }
    while (at < value.Length) {
      var arrow = value[at] == '-' && at + 1 < value.Length && value[at + 1] == '>';
      var open = arrow ? at + 2 : at;
      if (open >= value.Length)
        break;
      if (arrow && this.Feature("postderef_qq") && value[open] is '@' or '$' && open + 1 < value.Length && value[open + 1] == '*') {
        expression = new PerlDeref(value[open], expression) { Line = line };
        at = open + 2;
        break;
      }
      if (!(arrow || chained))
        break;
      if (value[open] == '[' && (arrow || this.IsElementSubscript(value, open, pattern)) || value[open] == '{' && (arrow || this.IsHashSubscript(value, open, pattern))) {
        if (pattern && !arrow && value[open] == '[' && !this.IsElementSubscript(value, open, pattern))
          break;
        var hash = value[open] == '{';
        var (index, end) = this.SubscriptContent(text, open, hash, line);
        expression = new PerlElement(new PerlDeref(hash ? '%' : '@', expression) { Line = line }, index, hash) { Line = line };
        at = end;
        chained = true;
        continue;
      }
      break;
    }
    _ = sigil;
    return (expression, at);
  }

  private bool IsElementSubscript(string value, int open, bool pattern) {
    var close = Matching(value, open);
    if (close < 0)
      return pattern ? false : throw this.Error("Missing right curly or square bracket within string", this._lexer.Line);
    if (!pattern)
      return true;
    var inner = value[(open + 1)..close].Trim();
    if (inner.Length == 0)
      return false;
    if (inner[0] == '-')
      inner = inner[1..];
    if (inner.Length > 0 && IsAllDigitsOrEmpty(inner))
      return true;
    if (inner.Length > 1 && inner[0] == '$' && this.NameEnd(inner, 1) == inner.Length)
      return true;
    return false;
  }

  private static bool IsAllDigitsOrEmpty(string text) {
    foreach (var c in text)
      if (!char.IsAsciiDigit(c))
        return false;
    return true;
  }

  private bool IsHashSubscript(string value, int open, bool pattern) {
    var close = Matching(value, open);
    if (close < 0)
      return pattern ? false : throw this.Error("Missing right curly or square bracket within string", this._lexer.Line);
    if (!pattern)
      return true;
    var inner = value[(open + 1)..close];
    var comma = inner.IndexOf(',');
    var lower = comma < 0 ? inner : inner[..comma];
    var upper = comma < 0 ? "" : inner[(comma + 1)..];
    return !(lower.Length > 0 && IsAllDigitsOrEmpty(lower) && IsAllDigitsOrEmpty(upper) || lower.Length == 0 && comma >= 0);
  }

  private (PerlExpr Index, int End) SubscriptContent(PerlText text, int open, bool hash, int line) {
    var value = text.Value;
    var close = Matching(value, open);
    if (close < 0)
      throw this.Error(hash ? "Missing right curly in string subscript" : "Missing right bracket in string subscript", line);
    var inner = value[(open + 1)..close];
    var trimmed = inner.Trim();
    if (hash) {
      var word = trimmed.StartsWith('-') ? trimmed[1..] : trimmed;
      if (word.Length > 0 && PerlLexer.IsIdentifierStart(word[0], this._scope.Utf8) && IsWord(word))
        return (new PerlString(trimmed) { Line = line, Bareword = true }, close + 1);
    }
    if (trimmed.Length == 0)
      throw this.Error("empty subscript in string", line);
    return (this.ParseEmbedded(text, open + 1, close, block: false), close + 1);
  }

  private bool IsWord(string text) {
    foreach (var c in text)
      if (!PerlLexer.IsWordChar(c, this._scope.Utf8))
        return false;
    return true;
  }

  private (PerlExpr Expression, int End) InterpolatedArray(PerlText text, int i, bool pattern, int line) {
    var value = text.Value;
    var j = i + 1;
    PerlExpr array;
    int end;
    if (value[j] == '{') {
      var close = Matching(value, j);
      if (close < 0)
        throw this.Error("Missing right curly in interpolated @{", line);
      var inner = value[(j + 1)..close].Trim();
      if (inner.Length == 1 && inner[0] is '+' or '-' or '_' or ':') {
        return (this.Variable('@', inner, line), close + 1);
      } else if (inner.Length > 0 && (PerlLexer.IsIdentifierStart(inner[0], this._scope.Utf8) || inner[0] == '^') && this.NameEnd(inner, inner[0] == '^' ? 1 : 0) == inner.Length) {
        return (this.Variable('@', inner, line), close + 1);
      } else {
        var code = this.ParseEmbedded(text, j + 1, close, block: true);
        array = new PerlDeref('@', code is PerlDoBlock { Body.Statements: [PerlExpressionStatement { Modifier: null } only] } ? only.Expression : code) { Line = line };
      }
      end = close + 1;
    } else if (value[j] == '$') {
      var (inner, after) = this.InterpolatedScalar(text, j, pattern: false, line);
      if (inner is PerlElement)
        throw new PerlUnsupportedException("a subscripted array dereference in a string", line);
      array = new PerlDeref('@', inner) { Line = line };
      end = after;
    } else {
      end = this.NameEnd(value, value[j] == ':' ? j + 2 : j);
      var name = value[j..end].Replace("'", "::", StringComparison.Ordinal);
      if (name.StartsWith("::", StringComparison.Ordinal))
        name = "main" + name;
      array = this.Variable('@', name, line);
    }
    if (end < value.Length && (value[end] == '[' && this.IsElementSubscript(value, end, pattern) || value[end] == '{' && this.IsHashSubscript(value, end, pattern))) {
      var hash = value[end] == '{';
      var aggregate = array is PerlVariable v ? this.Variable(hash ? '%' : '@', v.Name, line) : array is PerlDeref d ? new PerlDeref(hash ? '%' : '@', d.Reference) { Line = line } : array;
      var (index, after) = this.SubscriptContent(text, end, hash, line);
      return (new PerlSlice(aggregate, index, hash, false) { Line = line }, after);
    }
    return (array, end);
  }

}
