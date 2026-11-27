using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Frontends.VisualBasic.Parsing;

/// <summary>A syntax error of the source: its line and what is wrong.</summary>
internal sealed class VbaSyntaxException(int line, string message) : Exception(message) {

  public int Line { get; } = line;

}

/// <summary>A construct the grammar has but the frontend does not import, found where the parser cannot skip it: its line and name.</summary>
internal sealed class VbaUnsupportedException(int line, string construct) : Exception(construct) {

  public int Line { get; } = line;

  public string Construct { get; } = construct;

}

/// <summary>
/// The lexer and recursive-descent parser of a classic Visual Basic standard module, written from the
/// grammar of [MS-VBAL] section 3 (lexical) and 5 (module bodies, statements, expressions): keywords
/// without regard to case, the line continuation <c> _</c>, <c>:</c> between statements, comments with
/// <c>'</c> and <c>Rem</c>, the type characters of names and literals, hexadecimal (<c>&amp;H</c>) and
/// octal (<c>&amp;O</c>) literals, single-line and block <c>If</c>, and the operator precedence of
/// section 5.6.9. Operator and keyword spellings are made canonical, names keep their case.
/// </summary>
internal static class VbaParser {

  private enum Kind { Name, Integer, Float, String, Symbol, End }

  private sealed record Token(Kind Kind, string Text, int Line) {

    public long Integer { get; init; }

    public double Float { get; init; }

    public string Suffix { get; init; } = "";

    public int Digits { get; init; }

    public bool Radix { get; init; }

    public bool Is(string word) => this.Kind is Kind.Name or Kind.Symbol && string.Equals(this.Text, word, StringComparison.OrdinalIgnoreCase);

  }

  private static readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase) {
    "And", "Or", "Xor", "Eqv", "Imp", "Not", "Mod", "Like", "Is", "To", "Step", "Then", "Else", "ElseIf", "End", "Next", "Loop", "Wend",
    "While", "Until", "Case", "Select", "If", "Do", "For", "Each", "In", "Dim", "Static", "Const", "Exit", "GoTo", "GoSub", "Return", "On",
    "Resume", "ReDim", "Preserve", "Erase", "Function", "Sub", "Property", "Type", "Enum", "Public", "Private", "Global", "Friend",
    "ByVal", "ByRef", "Optional", "ParamArray", "As", "New", "Set", "Let", "Call", "With", "Option", "True", "False", "Nothing", "Empty",
    "Null", "Declare", "Event", "Implements", "Stop"
  };

  /// <summary>Reads a module.</summary>
  /// <exception cref="VbaSyntaxException">The text is no module of the grammar.</exception>
  public static VbaModule Parse(string source) => new Parser(Tokenize(source)).Module();

  #region Lexer

  private static List<Token> Tokenize(string source) {
    var tokens = new List<Token>();
    var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    for (var index = 0; index < lines.Length; ++index) {
      var number = index + 1;
      var line = lines[index];
      var at = 0;
      var continued = false;
      while (at < line.Length) {
        var c = line[at];
        if (c is ' ' or '\t') {
          ++at;
          continue;
        }

        if (c == '\'')
          break;

        if (c == '_' && line[(at + 1)..].Trim().Length == 0 && (at == 0 || line[at - 1] is ' ' or '\t')) {
          continued = true;
          break;
        }

        if (char.IsAsciiLetter(c)) {
          var start = at;
          while (at < line.Length && (char.IsAsciiLetterOrDigit(line[at]) || line[at] == '_'))
            ++at;
          var word = line[start..at];
          if (string.Equals(word, "Rem", StringComparison.OrdinalIgnoreCase) && (at >= line.Length || line[at] is ' ' or '\t'))
            break;

          var suffix = at < line.Length && line[at] is '%' or '&' or '^' or '!' or '#' or '$' or '@' && !(at + 1 < line.Length && char.IsAsciiLetterOrDigit(line[at + 1]) && line[at] is '!' or '&')
            ? line[at++].ToString() : "";
          tokens.Add(new(Kind.Name, word, number) { Suffix = suffix });
          continue;
        }

        if (char.IsAsciiDigit(c) || c == '.' && at + 1 < line.Length && char.IsAsciiDigit(line[at + 1])) {
          tokens.Add(Number(line, ref at, number));
          continue;
        }

        if (c == '&' && at + 1 < line.Length && line[at + 1] is 'H' or 'h' or 'O' or 'o') {
          var hex = line[at + 1] is 'H' or 'h';
          at += 2;
          var start = at;
          while (at < line.Length && (hex ? char.IsAsciiHexDigit(line[at]) : line[at] is >= '0' and <= '7'))
            ++at;
          if (at == start)
            throw new VbaSyntaxException(number, "a hexadecimal or octal literal has no digits");

          var digits = line[start..at];
          var suffix = at < line.Length && line[at] is '%' or '&' or '^' ? line[at++].ToString() : "";
          ulong value;
          try {
            value = hex ? ulong.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) : System.Convert.ToUInt64(digits, 8);
          } catch (OverflowException) {
            throw new VbaSyntaxException(number, $"the literal &{(hex ? 'H' : 'O')}{digits} does not fit 64 bits");
          }

          tokens.Add(new(Kind.Integer, "&" + digits, number) { Integer = (long)value, Suffix = suffix, Digits = 64 - (int)ulong.LeadingZeroCount(value), Radix = true });
          continue;
        }

        if (c == '"') {
          var text = new StringBuilder();
          ++at;
          while (true) {
            if (at >= line.Length)
              throw new VbaSyntaxException(number, "a string is not closed on its line");
            if (line[at] == '"') {
              if (at + 1 < line.Length && line[at + 1] == '"') {
                text.Append('"');
                at += 2;
                continue;
              }

              ++at;
              break;
            }

            text.Append(line[at++]);
          }

          tokens.Add(new(Kind.String, text.ToString(), number));
          continue;
        }

        var two = at + 1 < line.Length ? line.Substring(at, 2) : "";
        if (two is "<>" or "<=" or ">=" or "=<" or "=>" or "><") {
          tokens.Add(new(Kind.Symbol, two switch { "=<" => "<=", "=>" => ">=", "><" => "<>", _ => two }, number));
          at += 2;
          continue;
        }

        if ("()=<>+-*/\\^&,.:;#".Contains(c, StringComparison.Ordinal)) {
          tokens.Add(new(Kind.Symbol, c.ToString(), number));
          ++at;
          continue;
        }

        throw new VbaSyntaxException(number, $"unexpected character '{c}'");
      }

      if (!continued)
        tokens.Add(new(Kind.End, "", number));
    }

    tokens.Add(new(Kind.End, "", lines.Length));
    return tokens;
  }

  private static Token Number(string line, ref int at, int number) {
    var start = at;
    while (at < line.Length && char.IsAsciiDigit(line[at]))
      ++at;

    var isFloat = false;
    if (at < line.Length && line[at] == '.') {
      isFloat = true;
      ++at;
      while (at < line.Length && char.IsAsciiDigit(line[at]))
        ++at;
    }

    if (at < line.Length && line[at] is 'E' or 'e' or 'D' or 'd' && at + 1 < line.Length && (char.IsAsciiDigit(line[at + 1]) || line[at + 1] is '+' or '-')) {
      isFloat = true;
      ++at;
      if (line[at] is '+' or '-')
        ++at;
      while (at < line.Length && char.IsAsciiDigit(line[at]))
        ++at;
    }

    var digits = line[start..at].Replace('D', 'E').Replace('d', 'E');
    var suffix = at < line.Length && line[at] is '%' or '&' or '^' or '#' or '!' or '@' ? line[at++].ToString() : "";
    if (isFloat || suffix is "#" or "!" or "@" || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var integer))
      return new(Kind.Float, digits, number) { Float = double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture), Suffix = suffix };

    return new(Kind.Integer, digits, number) { Integer = integer, Suffix = suffix, Digits = digits.Length };
  }

  #endregion

  private sealed class Parser(List<Token> tokens) {

    private int _at;

    private Token Current => tokens[this._at];

    private Token Peek(int ahead = 1) => tokens[Math.Min(this._at + ahead, tokens.Count - 1)];

    private VbaSyntaxException Error(string message) => new(this.Current.Line, message);

    private bool AtStatementEnd => this.Current.Kind == Kind.End || this.Current.Is(":") || this.Current.Is("Else") && this._singleLine > 0;

    private int _singleLine;

    private bool TakeWord(string word) {
      if (this.Current.Kind == Kind.Name && this.Current.Is(word)) {
        ++this._at;
        return true;
      }

      return false;
    }

    private void Word(string word) {
      if (!this.TakeWord(word))
        throw this.Error($"expected '{word}' but found '{this.Describe()}'");
    }

    private string Describe() => this.Current.Kind == Kind.End ? "the end of the line" : this.Current.Text;

    private bool TakeSymbol(string symbol) {
      if (this.Current.Kind == Kind.Symbol && this.Current.Text == symbol) {
        ++this._at;
        return true;
      }

      return false;
    }

    private void Symbol(string symbol) {
      if (!this.TakeSymbol(symbol))
        throw this.Error($"expected '{symbol}' but found '{this.Describe()}'");
    }

    private string Name() {
      if (this.Current.Kind == Kind.Name && !_reserved.Contains(this.Current.Text))
        return tokens[this._at++].Text;

      throw this.Error($"expected a name but found '{this.Describe()}'");
    }

    /// <summary>Skips the end of the statement: <c>:</c> or the end of a line, and blank lines after it.</summary>
    private void EndStatement() {
      if (this.TakeSymbol(":"))
        return;
      if (this.Current.Kind != Kind.End)
        throw this.Error($"unexpected '{this.Describe()}'");
      ++this._at;
    }

    private void SkipBlank() {
      while (this._at < tokens.Count - 1 && (this.Current.Kind == Kind.End || this.Current.Is(":")))
        ++this._at;
    }

    private bool AtEnd => this._at >= tokens.Count - 1;

    public VbaModule Module() {
      string? name = null;
      var declarations = new List<VbaDeclaration>();
      while (true) {
        this.SkipBlank();
        if (this.AtEnd)
          break;

        var line = this.Current.Line;
        if (this.TakeWord("Attribute")) {
          var attribute = this.Name();
          this.Symbol("=");
          var value = this.Expression();
          if (string.Equals(attribute, "VB_Name", StringComparison.OrdinalIgnoreCase) && value is VbaString text)
            name = text.Value;
          this.EndStatement();
          continue;
        }

        declarations.Add(this.Declaration(line));
      }

      return new(name, declarations);
    }

    private VbaDeclaration Declaration(int line) {
      if (this.TakeWord("Option")) {
        var words = new List<string>();
        while (!this.AtStatementEnd)
          words.Add(tokens[this._at++].Text);
        this.EndStatement();
        return new VbaOption(string.Join(' ', words), line);
      }

      var isPublic = this.TakeWord("Public") || this.TakeWord("Global");
      var isPrivate = !isPublic && this.TakeWord("Private");
      var isDim = !isPublic && !isPrivate && this.TakeWord("Dim");
      if (this.Current.Is("Friend") || this.Current.Is("Declare") || this.Current.Is("Event") || this.Current.Is("Implements") || this.Current.Is("Property")) {
        var construct = this.Current.Text;
        this.SkipLogicalUnit(construct);
        return new VbaUnsupportedDeclaration(construct, line);
      }

      if (this.TakeWord("Static")) {
        this.SkipLogicalUnit("Static");
        return new VbaUnsupportedDeclaration("Static procedure", line);
      }

      if (!isDim && this.TakeWord("Type")) {
        var name = this.Name();
        this.EndStatement();
        var fields = new List<VbaVariable>();
        while (true) {
          this.SkipBlank();
          if (this.AtEnd)
            throw new VbaSyntaxException(line, "Type has no End Type");
          if (this.TakeWord("End")) {
            this.Word("Type");
            this.EndStatement();
            break;
          }

          fields.Add(this.Variable());
          this.EndStatement();
        }

        return new VbaTypeDeclaration(!isPrivate, name, fields, line);
      }

      if (!isDim && this.TakeWord("Enum")) {
        var name = this.Name();
        this.EndStatement();
        var members = new List<VbaEnumMember>();
        while (true) {
          this.SkipBlank();
          if (this.AtEnd)
            throw new VbaSyntaxException(line, "Enum has no End Enum");
          if (this.TakeWord("End")) {
            this.Word("Enum");
            this.EndStatement();
            break;
          }

          var memberLine = this.Current.Line;
          var member = this.Name();
          var value = this.TakeSymbol("=") ? this.Expression() : null;
          members.Add(new(member, value, memberLine));
          this.EndStatement();
        }

        return new VbaEnumDeclaration(!isPrivate, name, members, line);
      }

      if (!isDim && this.TakeWord("Const")) {
        var constant = this.ConstBody();
        this.EndStatement();
        return new VbaConstDeclaration(isPublic, constant.Name, constant.Type, constant.Value, line);
      }

      var isFunction = !isDim && this.TakeWord("Function");
      if (isFunction || !isDim && this.TakeWord("Sub"))
        return this.Procedure(isPublic || !isPrivate, isFunction, line);

      if (isPublic || isPrivate || isDim) {
        var variables = new List<VbaVariable>();
        do
          variables.Add(this.Variable());
        while (this.TakeSymbol(","));
        this.EndStatement();
        return new VbaVariableDeclaration(isPublic, variables, line);
      }

      throw this.Error($"'{this.Describe()}' cannot start a declaration of a standard module");
    }

    /// <summary>Skips a declaration the frontend does not import, a block one up to its <c>End</c> line.</summary>
    private void SkipLogicalUnit(string construct) {
      var block = construct is "Property" or "Static" or "Friend";
      while (!this.AtEnd && this.Current.Kind != Kind.End)
        ++this._at;
      if (!block)
        return;

      while (!this.AtEnd) {
        this.SkipBlank();
        if (this.AtEnd)
          return;
        if (this.Current.Is("End") && this.Peek().Kind == Kind.Name && (this.Peek().Is("Property") || this.Peek().Is("Function") || this.Peek().Is("Sub"))) {
          this._at += 2;
          return;
        }

        while (!this.AtEnd && this.Current.Kind != Kind.End)
          ++this._at;
      }
    }

    private (string Name, VbaTypeRef? Type, VbaExpr Value) ConstBody() {
      var name = this.Name();
      VbaTypeRef? type = null;
      if (this.TakeWord("As"))
        type = this.TypeRef();
      this.Symbol("=");
      return (name, type, this.Expression());
    }

    private VbaTypeRef TypeRef() {
      var line = this.Current.Line;
      if (this.TakeWord("New"))
        return new("New " + this.Name(), line);
      var name = this.Current.Kind == Kind.Name ? tokens[this._at++].Text : throw this.Error("expected a type");
      while (this.TakeSymbol("."))
        name += "." + this.Name();
      if (this.TakeSymbol("*")) {
        this.Expression();
        return new(name + " * n", line);
      }

      return new(name, line);
    }

    private VbaVariable Variable() {
      var line = this.Current.Line;
      this.TakeWord("WithEvents");
      var token = this.Current;
      var name = this.Name();
      var dynamic = false;
      var bounds = new List<VbaBound>();
      if (this.TakeSymbol("(")) {
        if (this.TakeSymbol(")")) {
          dynamic = true;
        } else {
          do
            bounds.Add(this.Bound());
          while (this.TakeSymbol(","));
          this.Symbol(")");
        }
      }

      var type = this.TakeWord("As") ? this.TypeRef() : Suffixed(token.Suffix, line);
      return new(name, type, dynamic, bounds, line);
    }

    /// <summary>The type a type character declares: <c>%</c> Integer, <c>&amp;</c> Long, <c>^</c> LongLong, <c>!</c> Single, <c>#</c> Double, <c>$</c> String, <c>@</c> Currency.</summary>
    private static VbaTypeRef? Suffixed(string suffix, int line) => suffix switch {
      "%" => new("Integer", line),
      "&" => new("Long", line),
      "^" => new("LongLong", line),
      "!" => new("Single", line),
      "#" => new("Double", line),
      "$" => new("String", line),
      "@" => new("Currency", line),
      _ => null
    };

    private VbaBound Bound() {
      var first = this.Expression();
      return this.TakeWord("To") ? new(first, this.Expression()) : new(null, first);
    }

    private VbaProcedure Procedure(bool isPublic, bool isFunction, int line) {
      var token = this.Current;
      var name = this.Name();
      var parameters = new List<VbaParameter>();
      if (this.TakeSymbol("(") && !this.TakeSymbol(")")) {
        do
          parameters.Add(this.Parameter());
        while (this.TakeSymbol(","));
        this.Symbol(")");
      }

      VbaTypeRef? returns = isFunction ? Suffixed(token.Suffix, line) : null;
      var returnsArray = false;
      if (isFunction && this.TakeWord("As")) {
        returns = this.TypeRef();
        if (this.TakeSymbol("(")) {
          this.Symbol(")");
          returnsArray = true;
        }
      }

      this.EndStatement();
      var body = this.Block(isFunction ? "Function" : "Sub", out _);
      return new VbaProcedure(isPublic, isFunction, name, parameters, returns, returnsArray, body, line);
    }

    private VbaParameter Parameter() {
      var line = this.Current.Line;
      var optional = this.TakeWord("Optional");
      if (this.TakeWord("ParamArray"))
        throw new VbaUnsupportedException(line, "ParamArray");
      var byVal = this.TakeWord("ByVal");
      if (!byVal)
        this.TakeWord("ByRef");
      var token = this.Current;
      var name = this.Name();
      var isArray = false;
      if (this.TakeSymbol("(")) {
        this.Symbol(")");
        isArray = true;
      }

      var type = this.TakeWord("As") ? this.TypeRef() : Suffixed(token.Suffix, line);
      var value = this.TakeSymbol("=") ? this.Expression() : null;
      return new(byVal, optional, name, type, isArray, value, line);
    }

    /// <summary>The statements of a block up to one of its closing words, which is consumed: <c>End Function</c>, <c>Next</c>, <c>Loop</c> and the like.</summary>
    private List<VbaStatement> Block(string closer, out string found, params string[] alternatives) {
      var statements = new List<VbaStatement>();
      var start = this.Current.Line;
      while (true) {
        this.SkipBlank();
        if (this.AtEnd)
          throw new VbaSyntaxException(start, $"the block has no closing {(closer.Contains(' ', StringComparison.Ordinal) || closer is "Next" or "Loop" or "Wend" ? closer : "End " + closer)}");

        if (this.Current.Is("End") && this.Peek().Kind == Kind.Name && this.Peek().Is(closer)) {
          this._at += 2;
          found = "End";
          return statements;
        }

        foreach (var alternative in alternatives.Prepend(closer).Where(word => word is "Next" or "Loop" or "Wend" or "ElseIf" or "Else" or "Case" || word.Contains(' ', StringComparison.Ordinal))) {
          var parts = alternative.Split(' ');
          if (parts.Select((part, index) => this.Peek(index).Is(part)).All(matches => matches)) {
            this._at += parts.Length;
            found = alternative;
            return statements;
          }
        }

        statements.AddRange(this.Statements());
      }
    }

    /// <summary>The statements of one logical line: several separated by <c>:</c>, a label in front.</summary>
    private List<VbaStatement> Statements() {
      var statements = new List<VbaStatement>();
      var line = this.Current.Line;
      if (this.Current.Kind == Kind.Name && this.Peek().Is(":") && !_reserved.Contains(this.Current.Text)) {
        statements.Add(new VbaLabel(this.Current.Text, line));
        this._at += 2;
        if (this.Current.Kind == Kind.End)
          return statements;
      } else if (this.Current.Kind == Kind.Integer && !this.Current.Radix) {
        statements.Add(new VbaLabel(this.Current.Text, line));
        ++this._at;
        if (this.Current.Kind == Kind.End)
          return statements;
      }

      statements.Add(this.Statement());
      if (this.Current.Kind is Kind.End || this.TakeSymbol(":"))
        return statements;

      throw this.Error($"unexpected '{this.Describe()}'");
    }

    private VbaStatement Statement() {
      var line = this.Current.Line;
      if (this.TakeWord("Dim") || this.TakeWord("Private") || this.TakeWord("Public")) {
        var variables = new List<VbaVariable>();
        do
          variables.Add(this.Variable());
        while (this.TakeSymbol(","));
        return new VbaDim(variables, line);
      }

      if (this.TakeWord("Static"))
        return this.Rest(new VbaUnsupported("Static", line));

      if (this.TakeWord("Const")) {
        var constant = this.ConstBody();
        return new VbaLocalConst(constant.Name, constant.Type, constant.Value, line);
      }

      if (this.TakeWord("If"))
        return this.If(line);
      if (this.TakeWord("Select")) {
        this.Word("Case");
        return this.Select(line);
      }

      if (this.TakeWord("For")) {
        if (this.TakeWord("Each"))
          return this.Rest(new VbaUnsupported("For Each", line), "Next");
        var variable = this.Name();
        this.Symbol("=");
        var start = this.Expression();
        this.Word("To");
        var end = this.Expression();
        var step = this.TakeWord("Step") ? this.Expression() : null;
        var body = this.Block("Next", out _);
        if (this.Current.Kind == Kind.Name && !this.Current.Is(":")) {
          var named = this.Name();
          if (!string.Equals(named, variable, StringComparison.OrdinalIgnoreCase))
            throw this.Error($"Next {named} closes the loop of {variable}");
        }

        return new VbaFor(variable, start, end, step, body, line);
      }

      if (this.TakeWord("Do")) {
        var preUntil = this.Current.Is("Until");
        VbaExpr? pre = this.TakeWord("While") || this.TakeWord("Until") ? this.Expression() : null;
        var body = this.Block("Loop", out _);
        var postUntil = this.Current.Is("Until");
        VbaExpr? post = this.TakeWord("While") || this.TakeWord("Until") ? this.Expression() : null;
        if (pre is not null && post is not null)
          throw new VbaSyntaxException(line, "a Do loop has a test at both ends");
        return post is not null ? new VbaDo(false, postUntil, post, body, line) : new VbaDo(true, preUntil, pre, body, line);
      }

      if (this.TakeWord("While")) {
        var condition = this.Expression();
        var body = this.Block("Wend", out _);
        return new VbaDo(true, false, condition, body, line);
      }

      if (this.TakeWord("Exit")) {
        var kind = this.Current.Kind == Kind.Name ? tokens[this._at++].Text : throw this.Error("expected Do, For, Function, Sub or Property");
        return new VbaExit(Canonical(kind), line);
      }

      if (this.TakeWord("GoTo"))
        return new VbaGoTo(this.LabelName(), line);
      if (this.TakeWord("GoSub") || this.TakeWord("Return") || this.TakeWord("Stop") || this.TakeWord("Set") || this.TakeWord("With")) {
        var construct = tokens[this._at - 1].Text;
        return this.Rest(new VbaUnsupported(Canonical(construct), line), construct.Equals("With", StringComparison.OrdinalIgnoreCase) ? "End With" : null);
      }

      if (this.TakeWord("On")) {
        if (!this.TakeWord("Error"))
          return this.Rest(new VbaUnsupported("On … GoTo", line));
        if (this.TakeWord("Resume")) {
          this.Word("Next");
          return new VbaOnError(null, line);
        }

        this.Word("GoTo");
        if (this.Current.Kind == Kind.Integer && this.Current.Integer == 0) {
          ++this._at;
          return new VbaOnError("0", line);
        }

        if (this.TakeSymbol("-")) {
          this.Expression();
          return new VbaUnsupported("On Error GoTo -1", line);
        }

        return new VbaOnError(this.LabelName(), line);
      }

      if (this.TakeWord("Resume")) {
        if (this.AtStatementEnd)
          return new VbaResume("", line);
        return new VbaResume(this.TakeWord("Next") ? "Next" : this.LabelName(), line);
      }

      if (this.TakeWord("ReDim")) {
        var preserve = this.TakeWord("Preserve");
        var items = new List<VbaReDimItem>();
        do {
          var resized = this.Target();
          this.Symbol("(");
          var bounds = new List<VbaBound>();
          do
            bounds.Add(this.Bound());
          while (this.TakeSymbol(","));
          this.Symbol(")");
          items.Add(new(resized, bounds, this.TakeWord("As") ? this.TypeRef() : null));
        } while (this.TakeSymbol(","));
        return new VbaReDim(preserve, items, line);
      }

      if (this.TakeWord("Erase")) {
        var targets = new List<VbaExpr>();
        do
          targets.Add(this.Target());
        while (this.TakeSymbol(","));
        return new VbaErase(targets, line);
      }

      if (this.Current.Is("Error") && !this.Peek().Is(".")) {
        ++this._at;
        return new VbaError(this.Expression(), line);
      }

      if (this.TakeWord("Call")) {
        var called = this.Postfix();
        return called is VbaCall call ? new VbaCallStatement(call.Target, call.Arguments, line) : new VbaCallStatement(called, [], line);
      }

      this.TakeWord("Let");
      var subject = this.Postfix();
      if (this.TakeSymbol("="))
        return new VbaAssign(subject, this.Expression(), line);

      // A call without Call: the procedure, then its arguments without parentheses. "F (a), b" passes
      // (a) as its first argument; "F(a)" alone is the call of F with a.
      var arguments = new List<VbaExpr>();
      var callee = subject;
      if (subject is VbaCall { Target: var target } bare && this.AtStatementEnd) {
        callee = target;
        arguments.AddRange(bare.Arguments);
      } else if (subject is VbaCall { Target: var head } first && first.Arguments.Count == 1) {
        callee = head;
        arguments.Add(this.ContinueExpression(new VbaParenthesized(first.Arguments[0], line)));
        while (this.TakeSymbol(","))
          arguments.Add(this.Expression());
      } else if (!this.AtStatementEnd) {
        do
          arguments.Add(this.Expression());
        while (this.TakeSymbol(","));
      }

      return new VbaCallStatement(callee, arguments, line);
    }

    private static readonly string[] _spellings = ["GoSub", "GoTo", "Mod", "And", "Or", "Xor", "Eqv", "Imp", "Like", "Is", "Do", "For", "Function", "Sub", "Property", "Return", "Stop", "Set", "With", "Nothing", "Empty", "Null", "New"];

    /// <summary>A keyword as the Visual Basic editor spells it.</summary>
    private static string Canonical(string word) => _spellings.FirstOrDefault(spelling => string.Equals(spelling, word, StringComparison.OrdinalIgnoreCase))
      ?? char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();

    private string LabelName() {
      if (this.Current.Kind == Kind.Integer)
        return tokens[this._at++].Text;
      return this.Name();
    }

    /// <summary>Skips the rest of a statement the frontend does not import, and the block it opens.</summary>
    private VbaStatement Rest(VbaStatement statement, string? closer = null) {
      while (!this.AtStatementEnd)
        ++this._at;
      if (closer is not null)
        this.Block(closer, out _);
      return statement;
    }

    private VbaStatement If(int line) {
      var condition = this.Expression();
      this.Word("Then");
      if (this.Current.Kind != Kind.End && !this.Current.Is(":"))
        return this.SingleLineIf(condition, line);

      var branches = new List<VbaIfBranch>();
      List<VbaStatement>? otherwise = null;
      while (true) {
        var body = this.Block("If", out var found, "ElseIf", "Else", "EndIf");
        branches.Add(new(condition, body));
        if (found == "ElseIf") {
          condition = this.Expression();
          this.Word("Then");
          continue;
        }

        if (found == "Else") {
          otherwise = this.Block("If", out _, "EndIf");
        }

        return new VbaIf(branches, otherwise, line);
      }
    }

    private VbaStatement SingleLineIf(VbaExpr condition, int line) {
      ++this._singleLine;
      try {
        var then = new List<VbaStatement>();
        if (this.Current.Kind == Kind.Integer)
          then.Add(new VbaGoTo(tokens[this._at++].Text, line));
        else
          then.AddRange(this.InlineStatements());

        List<VbaStatement>? otherwise = null;
        if (this.TakeWord("Else"))
          otherwise = this.InlineStatements();
        return new VbaIf([new(condition, then)], otherwise, line);
      } finally {
        --this._singleLine;
      }
    }

    /// <summary>The statements of a single-line <c>If</c> branch: up to <c>Else</c> or the end of the line.</summary>
    private List<VbaStatement> InlineStatements() {
      var statements = new List<VbaStatement> { this.Statement() };
      while (this.TakeSymbol(":") && this.Current.Kind != Kind.End && !this.Current.Is("Else"))
        statements.Add(this.Statement());
      return statements;
    }

    private VbaStatement Select(int line) {
      var value = this.Expression();
      this.EndStatement();
      var cases = new List<VbaCase>();
      List<VbaStatement>? otherwise = null;
      this.SkipBlank();
      if (!this.TakeWord("Case")) {
        this.Word("End");
        this.Word("Select");
        return new VbaSelect(value, cases, null, line);
      }

      while (true) {
        if (this.TakeWord("Else")) {
          otherwise = this.Block("Select", out _);
          break;
        }

        var clauses = new List<VbaCaseClause>();
        do
          clauses.Add(this.Clause());
        while (this.TakeSymbol(","));
        var body = this.Block("Select", out var found, "Case");
        cases.Add(new(clauses, body));
        if (found == "End")
          break;
      }

      return new VbaSelect(value, cases, otherwise, line);
    }

    private VbaCaseClause Clause() {
      if (this.TakeWord("Is")) {
        var comparison = this.Current.Kind == Kind.Symbol && this.Current.Text is "=" or "<>" or "<" or "<=" or ">" or ">=" ? tokens[this._at++].Text : throw this.Error("expected a comparison after Is");
        return new(this.Expression(), null, comparison);
      }

      var value = this.Expression();
      return this.TakeWord("To") ? new(value, this.Expression(), null) : new(value, null, null);
    }

    /// <summary>A variable or a field of one, without indexes: what <c>ReDim</c> and <c>Erase</c> name.</summary>
    private VbaExpr Target() {
      var line = this.Current.Line;
      VbaExpr expression = new VbaName(this.Name(), "", line);
      while (this.TakeSymbol("."))
        expression = new VbaMember(expression, this.MemberName(), line);
      return expression;
    }

    #region Expressions

    private static readonly Dictionary<string, int> _binary = new(StringComparer.OrdinalIgnoreCase) {
      ["^"] = 14, ["*"] = 12, ["/"] = 12, ["\\"] = 11, ["Mod"] = 10, ["+"] = 9, ["-"] = 9, ["&"] = 8,
      ["="] = 7, ["<>"] = 7, ["<"] = 7, ["<="] = 7, [">"] = 7, [">="] = 7, ["Like"] = 7, ["Is"] = 7,
      ["And"] = 5, ["Or"] = 4, ["Xor"] = 3, ["Eqv"] = 2, ["Imp"] = 1
    };

    public VbaExpr Expression() => this.Binary(1);

    /// <summary>Continues an expression whose first operand is already read: the arguments of a call statement in parentheses.</summary>
    private VbaExpr ContinueExpression(VbaExpr first) {
      var left = first;
      for (var strength = 14; strength >= 1; --strength) {
        while (this.Operator(strength) is { } op) {
          ++this._at;
          left = new VbaBinary(left, op, this.Binary(strength + 1), left.Line);
        }
      }

      return left;
    }

    private string? Operator(int strength) {
      var token = this.Current;
      if (token.Kind is not (Kind.Symbol or Kind.Name))
        return null;
      if (!_binary.TryGetValue(token.Text, out var value) || value != strength)
        return null;

      return token.Kind == Kind.Name ? Canonical(token.Text) : token.Text;
    }

    private VbaExpr Binary(int strength) {
      if (strength == 6)
        return this.Not();
      if (strength == 13)
        return this.Negation();
      if (strength > 14)
        return this.Postfix();

      var left = this.Binary(strength + 1);
      while (this.Operator(strength) is { } op) {
        ++this._at;
        left = new VbaBinary(left, op, this.Binary(strength + 1), left.Line);
      }

      return left;
    }

    private VbaExpr Not() {
      var line = this.Current.Line;
      return this.TakeWord("Not") ? new VbaUnary("Not", this.Not(), line) : this.Binary(7);
    }

    private VbaExpr Negation() {
      var line = this.Current.Line;
      if (this.TakeSymbol("-"))
        return new VbaUnary("-", this.Negation(), line);
      if (this.TakeSymbol("+"))
        return this.Negation();
      return this.Binary(14);
    }

    private VbaExpr Postfix() {
      var expression = this.Primary();
      while (true) {
        if (this.TakeSymbol(".")) {
          expression = new VbaMember(expression, this.MemberName(), expression.Line);
          continue;
        }

        if (this.TakeSymbol("(")) {
          var arguments = new List<VbaExpr>();
          if (!this.TakeSymbol(")")) {
            do
              arguments.Add(this.Expression());
            while (this.TakeSymbol(","));
            this.Symbol(")");
          }

          expression = new VbaCall(expression, arguments, expression.Line);
          continue;
        }

        return expression;
      }
    }

    private string MemberName() => this.Current.Kind == Kind.Name ? tokens[this._at++].Text : throw this.Error("expected a member name");

    private VbaExpr Primary() {
      var token = this.Current;
      switch (token.Kind) {
        case Kind.Integer:
          ++this._at;
          return new VbaInteger(token.Integer, token.Suffix, token.Digits, token.Radix, token.Line);
        case Kind.Float:
          ++this._at;
          return new VbaFloat(token.Float, token.Suffix, token.Line);
        case Kind.String:
          ++this._at;
          return new VbaString(token.Text, token.Line);
        case Kind.Name when token.Is("True") || token.Is("False"):
          ++this._at;
          return new VbaBoolean(token.Is("True"), token.Line);
        case Kind.Name when token.Is("Nothing") || token.Is("Empty") || token.Is("Null") || token.Is("New"):
          throw new VbaUnsupportedException(token.Line, Canonical(token.Text));
        case Kind.Name when token.Is("Error") || token.Is("Err") || !_reserved.Contains(token.Text):
          ++this._at;
          return new VbaName(token.Text, token.Suffix, token.Line);
        case Kind.Symbol when token.Text == "(":
          ++this._at;
          var inner = this.Expression();
          this.Symbol(")");
          return new VbaParenthesized(inner, token.Line);
        default:
          throw this.Error($"expected an expression but found '{this.Describe()}'");
      }
    }

    #endregion

  }

}
