using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Polypiler.Core.Frontends.VBScript.Parsing;

internal sealed class VBScriptLexer {

  private static readonly Dictionary<string, VBScriptTokenKind> _keywords = new(StringComparer.OrdinalIgnoreCase) {
    ["option"] = VBScriptTokenKind.KwOption,
    ["explicit"] = VBScriptTokenKind.KwExplicit,
    ["const"] = VBScriptTokenKind.KwConst,
    ["dim"] = VBScriptTokenKind.KwDim,
    ["redim"] = VBScriptTokenKind.KwReDim,
    ["preserve"] = VBScriptTokenKind.KwPreserve,
    ["public"] = VBScriptTokenKind.KwPublic,
    ["private"] = VBScriptTokenKind.KwPrivate,
    ["default"] = VBScriptTokenKind.KwDefault,
    ["byval"] = VBScriptTokenKind.KwByVal,
    ["byref"] = VBScriptTokenKind.KwByRef,
    ["function"] = VBScriptTokenKind.KwFunction,
    ["sub"] = VBScriptTokenKind.KwSub,
    ["property"] = VBScriptTokenKind.KwProperty,
    ["get"] = VBScriptTokenKind.KwGet,
    ["let"] = VBScriptTokenKind.KwLet,
    ["set"] = VBScriptTokenKind.KwSet,
    ["end"] = VBScriptTokenKind.KwEnd,
    ["exit"] = VBScriptTokenKind.KwExit,
    ["call"] = VBScriptTokenKind.KwCall,
    ["class"] = VBScriptTokenKind.KwClass,
    ["new"] = VBScriptTokenKind.KwNew,
    ["me"] = VBScriptTokenKind.KwMe,
    ["if"] = VBScriptTokenKind.KwIf,
    ["then"] = VBScriptTokenKind.KwThen,
    ["elseif"] = VBScriptTokenKind.KwElseIf,
    ["else"] = VBScriptTokenKind.KwElse,
    ["select"] = VBScriptTokenKind.KwSelect,
    ["case"] = VBScriptTokenKind.KwCase,
    ["for"] = VBScriptTokenKind.KwFor,
    ["to"] = VBScriptTokenKind.KwTo,
    ["step"] = VBScriptTokenKind.KwStep,
    ["next"] = VBScriptTokenKind.KwNext,
    ["each"] = VBScriptTokenKind.KwEach,
    ["in"] = VBScriptTokenKind.KwIn,
    ["do"] = VBScriptTokenKind.KwDo,
    ["while"] = VBScriptTokenKind.KwWhile,
    ["until"] = VBScriptTokenKind.KwUntil,
    ["loop"] = VBScriptTokenKind.KwLoop,
    ["wend"] = VBScriptTokenKind.KwWend,
    ["on"] = VBScriptTokenKind.KwOn,
    ["error"] = VBScriptTokenKind.KwError,
    ["resume"] = VBScriptTokenKind.KwResume,
    ["goto"] = VBScriptTokenKind.KwGoTo,
    ["true"] = VBScriptTokenKind.KwTrue,
    ["false"] = VBScriptTokenKind.KwFalse,
    ["empty"] = VBScriptTokenKind.KwEmpty,
    ["null"] = VBScriptTokenKind.KwNull,
    ["nothing"] = VBScriptTokenKind.KwNothing,
    ["mod"] = VBScriptTokenKind.KwMod,
    ["not"] = VBScriptTokenKind.KwNot,
    ["and"] = VBScriptTokenKind.KwAnd,
    ["or"] = VBScriptTokenKind.KwOr,
    ["xor"] = VBScriptTokenKind.KwXor,
    ["is"] = VBScriptTokenKind.KwIs,
    ["with"] = VBScriptTokenKind.KwWith,
    ["erase"] = VBScriptTokenKind.KwErase,
    ["execute"] = VBScriptTokenKind.KwExecute,
    ["executeglobal"] = VBScriptTokenKind.KwExecuteGlobal,
    ["eval"] = VBScriptTokenKind.KwEval
  };

  private readonly string _file;
  private readonly string _source;
  private readonly List<Diagnostic> _diagnostics;
  private int _pos;
  private int _line = 1;
  private int _column = 1;

  private VBScriptLexer(string file, string source, List<Diagnostic> diagnostics) {
    this._file = file;
    this._source = source;
    this._diagnostics = diagnostics;
  }

  public static List<VBScriptToken> Tokenize(string file, string source, List<Diagnostic> diagnostics)
    => new VBScriptLexer(file, source, diagnostics).ScanAll();

  private bool AtEnd => this._pos >= this._source.Length;
  private char Current => this._source[this._pos];
  private char Peek(int offset) => this._pos + offset < this._source.Length ? this._source[this._pos + offset] : '\0';

  private char Advance() {
    var c = this._source[this._pos++];
    ++this._column;
    return c;
  }

  private void SkipToEndOfLine() {
    while (!this.AtEnd && this.Current is not ('\r' or '\n'))
      this.Advance();
  }

  private void ConsumeNewLine() {
    if (this.Current == '\r' && this.Peek(1) == '\n')
      this._pos += 2;
    else
      ++this._pos;

    ++this._line;
    this._column = 1;
  }

  private void Error(int line, int column, string message)
    => this._diagnostics.Add(Diagnostic.Create(VBScriptFrontend.SyntaxError, new(this._file, line, column, line, column), message));

  private List<VBScriptToken> ScanAll() {
    var tokens = new List<VBScriptToken>();
    var atStatementStart = true;

    while (!this.AtEnd) {
      var c = this.Current;

      if (c is ' ' or '\t' or '\f' or '\v') {
        this.Advance();
        continue;
      }

      if (c is '\r' or '\n') {
        var line = this._line;
        var col = this._column;
        this.ConsumeNewLine();
        if (tokens.Count > 0 && tokens[^1].Kind is not (VBScriptTokenKind.NewLine or VBScriptTokenKind.DocComment))
          tokens.Add(new(VBScriptTokenKind.NewLine, "\n", line, col));

        atStatementStart = true;
        continue;
      }

      // Line continuation: standalone '_' followed by optional spaces/tabs and newline.
      if (c == '_' && !IsIdentPart(this.Peek(1))) {
        var line = this._line;
        var col = this._column;
        this.Advance();
        while (!this.AtEnd && this.Current is ' ' or '\t')
          this.Advance();

        if (!this.AtEnd && this.Current is '\r' or '\n') {
          this.ConsumeNewLine();
          continue;
        }

        this.Error(line, col, "expected newline after line continuation '_'");
        continue;
      }

      // Single-quote comment (preserving '@param / '@return doc comments).
      if (c == '\'') {
        var line = this._line;
        var col = this._column;
        this.Advance();
        var start = this._pos;
        this.SkipToEndOfLine();
        var text = this._source[start..this._pos].Trim();
        if (text.StartsWith("@param", StringComparison.OrdinalIgnoreCase) || text.StartsWith("@return", StringComparison.OrdinalIgnoreCase))
          tokens.Add(new(VBScriptTokenKind.DocComment, text, line, col, StringValue: text));

        continue;
      }

      // String literal ("..." with "" escape).
      if (c == '"') {
        tokens.Add(this.ScanString());
        atStatementStart = false;
        continue;
      }

      // Hex literal (&H... or &h...) vs '&' concatenation operator.
      if (c == '&' && this.Peek(1) is 'h' or 'H') {
        tokens.Add(this.ScanHexLiteral());
        atStatementStart = false;
        continue;
      }

      // Numeric literal (integer, float, or leading dot float like .5).
      if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(this.Peek(1)))) {
        tokens.Add(this.ScanNumber());
        atStatementStart = false;
        continue;
      }

      // Identifier or keyword (or Rem comment at statement start).
      if (IsIdentStart(c)) {
        var token = this.ScanIdentifierOrKeyword();
        if (atStatementStart && token.Text.Equals("rem", StringComparison.OrdinalIgnoreCase)) {
          this.SkipToEndOfLine();
          continue;
        }

        tokens.Add(token);
        atStatementStart = false;
        continue;
      }

      // Operators and punctuation.
      var tokLine = this._line;
      var tokCol = this._column;
      this.Advance();
      VBScriptTokenKind? kind = c switch {
        '(' => VBScriptTokenKind.LParen,
        ')' => VBScriptTokenKind.RParen,
        ',' => VBScriptTokenKind.Comma,
        '.' => VBScriptTokenKind.Dot,
        ':' => VBScriptTokenKind.Colon,
        '^' => VBScriptTokenKind.Caret,
        '*' => VBScriptTokenKind.Star,
        '/' => VBScriptTokenKind.Slash,
        '\\' => VBScriptTokenKind.Backslash,
        '+' => VBScriptTokenKind.Plus,
        '-' => VBScriptTokenKind.Minus,
        '&' => VBScriptTokenKind.Ampersand,
        '=' => VBScriptTokenKind.Equal,
        '<' => this.ScanLess(),
        '>' => this.ScanGreater(),
        _ => null
      };

      if (kind is null) {
        this.Error(tokLine, tokCol, $"unexpected character '{c}'");
        continue;
      }

      var tokText = kind.Value switch {
        VBScriptTokenKind.NotEqual => "<>",
        VBScriptTokenKind.LessEqual => "<=",
        VBScriptTokenKind.GreaterEqual => ">=",
        _ => c.ToString()
      };
      tokens.Add(new(kind.Value, tokText, tokLine, tokCol));
      atStatementStart = kind.Value == VBScriptTokenKind.Colon;
    }

    tokens.Add(new(VBScriptTokenKind.EndOfFile, "", this._line, this._column));
    return tokens;
  }

  private VBScriptTokenKind ScanLess() {
    if (!this.AtEnd && this.Current == '>') {
      this.Advance();
      return VBScriptTokenKind.NotEqual;
    }

    if (!this.AtEnd && this.Current == '=') {
      this.Advance();
      return VBScriptTokenKind.LessEqual;
    }

    return VBScriptTokenKind.Less;
  }

  private VBScriptTokenKind ScanGreater() {
    if (!this.AtEnd && this.Current == '=') {
      this.Advance();
      return VBScriptTokenKind.GreaterEqual;
    }

    return VBScriptTokenKind.Greater;
  }

  private VBScriptToken ScanString() {
    var line = this._line;
    var col = this._column;
    var start = this._pos;
    this.Advance(); // opening "
    var sb = new StringBuilder();
    while (!this.AtEnd) {
      var c = this.Current;
      if (c is '\r' or '\n') {
        this.Error(line, col, "unterminated string literal");
        return new(VBScriptTokenKind.StringLiteral, this._source[start..this._pos], line, col, StringValue: sb.ToString());
      }

      this.Advance();
      if (c == '"') {
        if (!this.AtEnd && this.Current == '"') {
          this.Advance();
          sb.Append('"');
          continue;
        }

        return new(VBScriptTokenKind.StringLiteral, this._source[start..this._pos], line, col, StringValue: sb.ToString());
      }

      sb.Append(c);
    }

    this.Error(line, col, "unterminated string literal");
    return new(VBScriptTokenKind.StringLiteral, this._source[start..this._pos], line, col, StringValue: sb.ToString());
  }

  private VBScriptToken ScanHexLiteral() {
    var line = this._line;
    var col = this._column;
    var start = this._pos;
    this.Advance(); // &
    this.Advance(); // H / h
    var hexStart = this._pos;
    while (!this.AtEnd && char.IsAsciiHexDigit(this.Current))
      this.Advance();

    var hex = this._source[hexStart..this._pos];
    var isLongSuffix = false;
    if (!this.AtEnd && this.Current == '&') {
      isLongSuffix = true;
      this.Advance();
    }

    var raw = this._source[start..this._pos];
    if (hex.Length == 0 || hex.Length > 8) {
      this.Error(line, col, $"invalid hexadecimal literal '{raw}'");
      return new(VBScriptTokenKind.IntLiteral, raw, line, col, LongValue: 0);
    }

    var unsigned = uint.Parse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    // VBScript 5.8 semantics: 1..4 hex digits without '&' suffix sign-extend as 16-bit signed Integer;
    // >4 hex digits or trailing '&' sign-extend as 32-bit signed Long.
    long value = !isLongSuffix && hex.Length <= 4 ? (short)unsigned : (int)unsigned;
    return new(VBScriptTokenKind.IntLiteral, raw, line, col, LongValue: value);
  }

  private VBScriptToken ScanNumber() {
    var line = this._line;
    var col = this._column;
    var start = this._pos;
    var isFloat = false;

    while (!this.AtEnd && char.IsAsciiDigit(this.Current))
      this.Advance();

    if (!this.AtEnd && this.Current == '.') {
      isFloat = true;
      this.Advance();
      while (!this.AtEnd && char.IsAsciiDigit(this.Current))
        this.Advance();
    }

    if (!this.AtEnd && this.Current is 'e' or 'E') {
      isFloat = true;
      this.Advance();
      if (!this.AtEnd && this.Current is '+' or '-')
        this.Advance();

      var expStart = this._pos;
      while (!this.AtEnd && char.IsAsciiDigit(this.Current))
        this.Advance();

      if (this._pos == expStart) {
        this.Error(line, col, $"malformed exponent in numeric literal '{this._source[start..this._pos]}'");
        return new(VBScriptTokenKind.FloatLiteral, this._source[start..this._pos], line, col, DoubleValue: 0.0);
      }
    }

    var text = this._source[start..this._pos];
    if (isFloat) {
      var d = double.Parse(text, CultureInfo.InvariantCulture);
      return new(VBScriptTokenKind.FloatLiteral, text, line, col, DoubleValue: d);
    }

    if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var l))
      return new(VBScriptTokenKind.IntLiteral, text, line, col, LongValue: l);

    var large = double.Parse(text, CultureInfo.InvariantCulture);
    return new(VBScriptTokenKind.FloatLiteral, text, line, col, DoubleValue: large);
  }

  private VBScriptToken ScanIdentifierOrKeyword() {
    var line = this._line;
    var col = this._column;
    var start = this._pos;
    while (!this.AtEnd && IsIdentPart(this.Current))
      this.Advance();

    var text = this._source[start..this._pos];
    var kind = _keywords.GetValueOrDefault(text, VBScriptTokenKind.Identifier);
    return new(kind, text, line, col);
  }

  private static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';
  private static bool IsIdentPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

}
