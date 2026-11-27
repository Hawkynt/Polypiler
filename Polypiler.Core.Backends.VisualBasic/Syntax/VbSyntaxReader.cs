using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.Backends.VisualBasic.Printing;

namespace Polypiler.Core.Backends.VisualBasic.Syntax;

/// <summary>
/// Reads the Visual Basic the printer writes back into a syntax tree: the parse-back half of the
/// backend's round trip, and how the helpers, written as Visual Basic source, become declarations.
/// It reads the subset <see cref="VbPrinter"/> writes - keywords in any case, a line continuation
/// <c> _</c>, comments - and throws a <see cref="FormatException"/> naming the line for anything else.
/// A minus sign in front of a decimal literal is read as a negative literal, as the printer writes one.
/// </summary>
public static class VbSyntaxReader {

  /// <summary>Reads a module.</summary>
  /// <param name="text">The text, as <see cref="VbPrinter.Print(VbModule)"/> writes it.</param>
  /// <returns>The module.</returns>
  /// <exception cref="FormatException">The text is not of the subset.</exception>
  public static VbModule ReadModule(string text) {
    ArgumentNullException.ThrowIfNull(text);
    return new Reader(Lines(text)).Module();
  }

  /// <summary>Reads the declarations of a text without an <c>Attribute VB_Name</c> line: how a helper becomes a tree.</summary>
  /// <param name="text">The declarations.</param>
  /// <returns>The declarations in order.</returns>
  /// <exception cref="FormatException">The text is not of the subset.</exception>
  public static IReadOnlyList<VbDeclaration> ReadDeclarations(string text) {
    ArgumentNullException.ThrowIfNull(text);
    return new Reader(Lines(text)).Declarations();
  }

  /// <summary>Reads one expression.</summary>
  /// <param name="text">The expression.</param>
  /// <returns>The expression.</returns>
  /// <exception cref="FormatException">The text is no expression of the subset.</exception>
  public static VbExpression ReadExpression(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var line = new LineReader(Tokenize(text, 1), 1);
    var expression = line.Expression();
    line.End();
    return expression;
  }

  /// <summary>One logical line: its number, whether it is blank, a comment, and its tokens.</summary>
  private sealed record SourceLine(int Number, string? Comment, IReadOnlyList<Token> Tokens) {

    public bool IsBlank => this.Comment is null && this.Tokens.Count == 0;

  }

  private enum Kind { Name, Integer, Float, String, Symbol }

  private sealed record Token(Kind Kind, string Text, int Line) {

    public long Integer { get; init; }

    public double Float { get; init; }

    public bool Single { get; init; }

    public string Suffix { get; init; } = "";

    public bool Hex { get; init; }

    public bool Is(string word) => this.Kind == Kind.Name && string.Equals(this.Text, word, StringComparison.OrdinalIgnoreCase)
      || this.Kind == Kind.Symbol && this.Text == word;

  }

  /// <summary>The logical lines of a text: physical lines joined at a continuation, each with its comment split off.</summary>
  private static List<SourceLine> Lines(string text) {
    var physical = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    var lines = new List<SourceLine>();
    var pending = new StringBuilder();
    var first = 0;
    for (var index = 0; index < physical.Length; ++index) {
      var line = physical[index];
      if (pending.Length == 0)
        first = index + 1;

      var (code, comment) = SplitComment(line, index + 1);
      if (comment is null && code.EndsWith(" _", StringComparison.Ordinal)) {
        pending.Append(code[..^1]);
        continue;
      }

      pending.Append(code);
      var tokens = Tokenize(pending.ToString(), first);
      pending.Clear();
      if (index == physical.Length - 1 && tokens.Count == 0 && comment is null)
        break;

      lines.Add(new(first, comment, tokens));
    }

    return lines;
  }

  /// <summary>The code of a line and its comment, the text after an apostrophe outside a string.</summary>
  private static (string Code, string? Comment) SplitComment(string line, int number) {
    var inString = false;
    for (var index = 0; index < line.Length; ++index) {
      if (line[index] == '"')
        inString = !inString;
      else if (line[index] == '\'' && !inString)
        return (line[..index], line[(index + 1)..].StartsWith(' ') ? line[(index + 2)..] : line[(index + 1)..]);
    }

    if (inString)
      throw new FormatException($"line {number}: a string is not closed");

    return (line, null);
  }

  private static List<Token> Tokenize(string code, int line) {
    var tokens = new List<Token>();
    var at = 0;
    while (at < code.Length) {
      var c = code[at];
      if (c is ' ' or '\t') {
        ++at;
        continue;
      }

      if (char.IsAsciiLetter(c) || c == '_' && at + 1 < code.Length && char.IsAsciiLetterOrDigit(code[at + 1])) {
        var start = at;
        while (at < code.Length && (char.IsAsciiLetterOrDigit(code[at]) || code[at] == '_'))
          ++at;
        tokens.Add(new(Kind.Name, code[start..at], line));
        continue;
      }

      if (char.IsAsciiDigit(c) || c == '.' && at + 1 < code.Length && char.IsAsciiDigit(code[at + 1])) {
        tokens.Add(Number(code, ref at, line));
        continue;
      }

      if (c == '&' && at + 1 < code.Length && (code[at + 1] is 'H' or 'h')) {
        at += 2;
        var start = at;
        while (at < code.Length && char.IsAsciiHexDigit(code[at]))
          ++at;
        if (at == start)
          throw new FormatException($"line {line}: a hexadecimal literal has no digits");

        var digits = code[start..at];
        var suffix = at < code.Length && code[at] is '&' or '^' or '%' ? code[at++].ToString() : "";
        var value = long.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        // Eight digits are a Long, sixteen a LongLong: the bits as Visual Basic reads them.
        value = suffix == "^" || digits.Length > 8 ? value : (int)(uint)value;
        tokens.Add(new(Kind.Integer, code[(start - 2)..at], line) { Integer = value, Suffix = suffix, Hex = true });
        continue;
      }

      if (c == '"') {
        var text = new StringBuilder();
        ++at;
        while (true) {
          if (at >= code.Length)
            throw new FormatException($"line {line}: a string is not closed");
          if (code[at] == '"') {
            if (at + 1 < code.Length && code[at + 1] == '"') {
              text.Append('"');
              at += 2;
              continue;
            }

            ++at;
            break;
          }

          text.Append(code[at++]);
        }

        tokens.Add(new(Kind.String, text.ToString(), line));
        continue;
      }

      var two = at + 1 < code.Length ? code.Substring(at, 2) : "";
      if (two is "<>" or "<=" or ">=") {
        tokens.Add(new(Kind.Symbol, two, line));
        at += 2;
        continue;
      }

      if ("()=<>+-*/\\^&,.:".Contains(c, StringComparison.Ordinal)) {
        tokens.Add(new(Kind.Symbol, c.ToString(), line));
        ++at;
        continue;
      }

      throw new FormatException($"line {line}: unexpected character '{c}'");
    }

    return tokens;
  }

  private static Token Number(string code, ref int at, int line) {
    var start = at;
    while (at < code.Length && char.IsAsciiDigit(code[at]))
      ++at;

    var isFloat = false;
    if (at < code.Length && code[at] == '.') {
      isFloat = true;
      ++at;
      while (at < code.Length && char.IsAsciiDigit(code[at]))
        ++at;
    }

    if (at < code.Length && code[at] is 'E' or 'e') {
      isFloat = true;
      ++at;
      if (at < code.Length && code[at] is '+' or '-')
        ++at;
      while (at < code.Length && char.IsAsciiDigit(code[at]))
        ++at;
    }

    var digits = code[start..at];
    var suffix = at < code.Length && code[at] is '%' or '&' or '^' or '#' or '!' ? code[at++].ToString() : "";
    if (isFloat || suffix is "#" or "!") {
      var value = double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture);
      return new(Kind.Float, code[start..at], line) { Float = suffix == "!" ? (float)value : value, Single = suffix == "!" };
    }

    return new(Kind.Integer, code[start..at], line) { Integer = long.Parse(digits, CultureInfo.InvariantCulture), Suffix = suffix };
  }

  /// <summary>Reads the declarations and statements of the logical lines.</summary>
  private sealed class Reader(List<SourceLine> lines) {

    private int _at;

    private SourceLine? Peek() => this._at < lines.Count ? lines[this._at] : null;

    private VbTrivia TakeTrivia() {
      var blank = 0;
      var comments = new List<string>();
      while (this.Peek() is { Tokens.Count: 0 } line) {
        if (line.Comment is { } comment)
          comments.Add(comment);
        else if (comments.Count == 0)
          ++blank;
        else
          break;
        ++this._at;
      }

      return blank == 0 && comments.Count == 0 ? VbTrivia.None : new(blank, comments);
    }

    private static FormatException Error(SourceLine line, string message) => new($"line {line.Number}: {message}");

    public VbModule Module() {
      var first = this.Peek() ?? throw new FormatException("line 1: a module starts with Attribute VB_Name");
      var head = new LineReader(first.Tokens, first.Number);
      head.Word("Attribute");
      head.Word("VB_Name");
      head.Symbol("=");
      var name = head.String();
      head.End();
      ++this._at;
      var header = new List<string>();
      while (this.Peek() is { Tokens.Count: 0, Comment: { } comment }) {
        header.Add(comment);
        ++this._at;
      }

      return new(name, header, this.Declarations());
    }

    public List<VbDeclaration> Declarations() {
      var declarations = new List<VbDeclaration>();
      while (true) {
        var leading = this.TakeTrivia();
        if (this.Peek() is not { } line)
          break;

        declarations.Add(this.Declaration(line) with { Leading = leading });
      }

      return declarations;
    }

    private VbDeclaration Declaration(SourceLine line) {
      var reader = new LineReader(line.Tokens, line.Number);
      ++this._at;
      if (reader.TakeWord("Option")) {
        var text = string.Join(' ', reader.Rest());
        return new VbOption(text);
      }

      var isPublic = reader.TakeWord("Public");
      if (!isPublic)
        reader.TakeWord("Private");

      if (reader.TakeWord("Type")) {
        var name = reader.Name();
        reader.End();
        var fields = new List<VbVariable>();
        while (true) {
          var next = this.Peek() ?? throw Error(line, "Type has no End Type");
          ++this._at;
          if (next.IsBlank)
            continue;

          var field = new LineReader(next.Tokens, next.Number);
          if (field.TakeWord("End")) {
            field.Word("Type");
            field.End();
            break;
          }

          fields.Add(field.Variable());
          field.End();
        }

        return new VbTypeDeclaration(isPublic, name, fields);
      }

      if (reader.TakeWord("Const")) {
        var name = reader.Name();
        reader.Word("As");
        var type = new VbTypeName(reader.Name());
        reader.Symbol("=");
        var value = reader.Expression();
        reader.End();
        return new VbConst(isPublic, name, type, value);
      }

      var isFunction = reader.TakeWord("Function");
      if (isFunction || reader.TakeWord("Sub")) {
        var name = reader.Name();
        reader.Symbol("(");
        var parameters = new List<VbParameter>();
        if (!reader.TakeSymbol(")")) {
          do
            parameters.Add(reader.Parameter());
          while (reader.TakeSymbol(","));
          reader.Symbol(")");
        }

        VbTypeName? returns = null;
        var returnsArray = false;
        if (isFunction) {
          reader.Word("As");
          returns = new(reader.Name());
          if (reader.TakeSymbol("(")) {
            reader.Symbol(")");
            returnsArray = true;
          }
        }

        reader.End();
        var body = this.Block(line, isFunction ? ["End Function"] : ["End Sub"], out _);
        return new VbProcedure(isPublic, name, parameters, returns, body) { ReturnsArray = returnsArray };
      }

      var variable = reader.Variable();
      reader.End();
      return new VbModuleVariable(isPublic, variable);
    }

    /// <summary>Reads statements up to one of the closing lines, which is consumed and returned.</summary>
    private List<VbStatement> Block(SourceLine opener, IReadOnlyList<string> closers, out LineReader closer) {
      var statements = new List<VbStatement>();
      while (true) {
        var leading = this.TakeTrivia();
        var line = this.Peek() ?? throw Error(opener, $"the block has no {closers[0]}");
        var reader = new LineReader(line.Tokens, line.Number);
        if (closers.FirstOrDefault(reader.StartsWith) is { } found) {
          ++this._at;
          reader.Skip(found.Split(' ').Length);
          closer = reader;
          if (leading.Comments.Count > 0)
            throw Error(line, "a comment stands in front of the end of a block");
          return statements;
        }

        statements.Add(this.Statement(line) with { Leading = leading });
      }
    }

    private VbStatement Statement(SourceLine line) {
      var reader = new LineReader(line.Tokens, line.Number);
      ++this._at;
      if (line.Tokens.Count == 2 && line.Tokens[0].Kind == Kind.Name && line.Tokens[1].Text == ":")
        return new VbLabel(line.Tokens[0].Text);

      if (reader.TakeWord("Dim"))
        return reader.Finish(new VbDim(reader.Variables()));
      if (reader.TakeWord("Static"))
        return reader.Finish(new VbStatic(reader.Variables()));

      if (reader.TakeWord("If")) {
        var branches = new List<VbIfBranch>();
        var condition = reader.Expression();
        reader.Word("Then");
        reader.End();
        List<VbStatement>? otherwise = null;
        while (true) {
          var body = this.Block(line, ["ElseIf", "Else", "End If"], out var closer);
          branches.Add(new(condition, body));
          if (closer.Previous("ElseIf")) {
            condition = closer.Expression();
            closer.Word("Then");
            closer.End();
            continue;
          }

          if (closer.Previous("Else")) {
            closer.End();
            otherwise = this.Block(line, ["End If"], out var end);
            end.End();
          } else {
            closer.End();
          }

          return new VbIf(branches, otherwise);
        }
      }

      if (reader.TakeWord("Select")) {
        reader.Word("Case");
        var value = reader.Expression();
        reader.End();
        var cases = new List<VbCase>();
        List<VbStatement>? otherwise = null;
        var next = this.Peek() ?? throw Error(line, "Select Case has no End Select");
        ++this._at;
        var head = new LineReader(next.Tokens, next.Number);
        while (true) {
          if (head.TakeWord("End")) {
            head.Word("Select");
            head.End();
            break;
          }

          head.Word("Case");
          if (head.TakeWord("Else")) {
            head.End();
            otherwise = this.Block(line, ["End Select"], out var end);
            end.End();
            break;
          }

          var clauses = new List<VbCaseClause>();
          do
            clauses.Add(head.Clause());
          while (head.TakeSymbol(","));
          head.End();
          var body = this.Block(line, ["Case", "End Select"], out var closer);
          cases.Add(new(clauses, body));
          head = closer.Previous("Case") ? closer.Rewound(1) : closer.Rewound(2);
        }

        return new VbSelect(value, cases, otherwise);
      }

      if (reader.TakeWord("For")) {
        var variable = reader.Name();
        reader.Symbol("=");
        var start = reader.Expression();
        reader.Word("To");
        var end = reader.Expression();
        var step = reader.TakeWord("Step") ? reader.Expression() : null;
        reader.End();
        var body = this.Block(line, ["Next"], out var closer);
        if (!string.Equals(closer.Name(), variable, StringComparison.OrdinalIgnoreCase))
          throw Error(line, "Next names another counter");
        closer.End();
        return new VbFor(variable, start, end, step, body);
      }

      if (reader.TakeWord("Do")) {
        var pre = reader.Test();
        reader.End();
        var body = this.Block(line, ["Loop"], out var closer);
        var post = closer.Test();
        closer.End();
        return new VbDoLoop(pre, post, body);
      }

      if (reader.TakeWord("Exit"))
        return reader.Finish(new VbExit(reader.Name()));
      if (reader.TakeWord("GoTo"))
        return reader.Finish(new VbGoTo(reader.Name()));

      if (reader.TakeWord("On")) {
        reader.Word("Error");
        if (reader.TakeWord("Resume")) {
          reader.Word("Next");
          return reader.Finish(new VbOnError(null));
        }

        reader.Word("GoTo");
        return reader.Finish(new VbOnError(reader.LabelOrZero()));
      }

      if (reader.TakeWord("Resume"))
        return reader.Finish(new VbResume(reader.AtEnd ? "" : reader.Name()));

      if (reader.TakeWord("ReDim")) {
        var preserve = reader.TakeWord("Preserve");
        var target = reader.Target();
        reader.Symbol("(");
        var bounds = reader.Bounds();
        reader.Symbol(")");
        return reader.Finish(new VbReDim(preserve, target, bounds));
      }

      if (reader.TakeWord("Erase"))
        return reader.Finish(new VbErase(reader.Postfix()));

      if (reader.TakeWord("Call")) {
        var called = reader.Postfix();
        return reader.Finish(called is VbCall { Target: var invoked } invocation ? new VbCallStatement(invoked, invocation.Arguments) : new VbCallStatement(called, []));
      }

      var subject = reader.Postfix();
      if (reader.TakeSymbol("="))
        return reader.Finish(new VbAssign(subject, reader.Expression()));

      // A call statement: the procedure, then the arguments without parentheses.
      var callee = subject;
      var arguments = new List<VbExpression>();
      if (subject is VbCall { Target: var procedure } call && !reader.AtEnd) {
        callee = procedure;
        arguments.AddRange(call.Arguments);
      }

      if (!reader.AtEnd) {
        do
          arguments.Add(reader.Expression());
        while (reader.TakeSymbol(","));
      }

      return reader.Finish(new VbCallStatement(callee, arguments));
    }

  }

  /// <summary>Reads the tokens of one logical line.</summary>
  private sealed class LineReader(IReadOnlyList<Token> tokens, int number) {

    private int _at;

    /// <summary>Words that end an expression and are no names.</summary>
    private static readonly HashSet<string> _keywords = new(StringComparer.OrdinalIgnoreCase) {
      "And", "Or", "Xor", "Eqv", "Imp", "Not", "Mod", "To", "Step", "Then", "Is", "As", "Else", "ElseIf", "End", "Next", "Loop",
      "While", "Until", "Case", "Select", "If", "Do", "For", "Dim", "Exit", "GoTo", "On", "Error", "Resume", "ReDim", "Preserve",
      "Function", "Sub", "Type", "Const", "Public", "Private", "ByVal", "ByRef", "Static", "Erase", "Option", "True", "False", "Call"
    };

    public bool AtEnd => this._at >= tokens.Count;

    private Token? Current => this.AtEnd ? null : tokens[this._at];

    private FormatException Error(string message) => new($"line {number}: {message}");

    public T Finish<T>(T value) {
      this.End();
      return value;
    }

    public void End() {
      if (!this.AtEnd)
        throw this.Error($"unexpected '{tokens[this._at].Text}'");
    }

    public IEnumerable<string> Rest() {
      while (!this.AtEnd)
        yield return tokens[this._at++].Text;
    }

    public void Skip(int count) => this._at += count;

    public bool Previous(string word) => this._at > 0 && tokens[this._at - 1].Is(word) && (word != "Else" || this._at == 1);

    public LineReader Rewound(int count) => new(tokens, number) { _at = this._at - count };

    public bool StartsWith(string words) {
      var parts = words.Split(' ');
      if (tokens.Count < parts.Length)
        return false;

      for (var index = 0; index < parts.Length; ++index)
        if (!tokens[index].Is(parts[index]))
          return false;

      // "Else" alone, not "ElseIf"; "End If" and the like as a whole.
      return parts.Length > 1 || parts[0] != "Else" || tokens.Count == 1;
    }

    public bool TakeWord(string word) {
      if (this.Current is { Kind: Kind.Name } token && token.Is(word)) {
        ++this._at;
        return true;
      }

      return false;
    }

    public void Word(string word) {
      if (!this.TakeWord(word))
        throw this.Error($"expected '{word}'");
    }

    public bool TakeSymbol(string symbol) {
      if (this.Current is { Kind: Kind.Symbol } token && token.Text == symbol) {
        ++this._at;
        return true;
      }

      return false;
    }

    public void Symbol(string symbol) {
      if (!this.TakeSymbol(symbol))
        throw this.Error($"expected '{symbol}'");
    }

    public string Name() {
      if (this.Current is { Kind: Kind.Name } token && !_keywords.Contains(token.Text)) {
        ++this._at;
        return token.Text;
      }

      if (this.Current is { Kind: Kind.Name } word && word.Text is "Do" or "For" or "Function" or "Sub" or "Next") {
        ++this._at;
        return word.Text;
      }

      throw this.Error("expected a name");
    }

    public string String() {
      if (this.Current is { Kind: Kind.String } token) {
        ++this._at;
        return token.Text;
      }

      throw this.Error("expected a string");
    }

    public string LabelOrZero() {
      if (this.Current is { Kind: Kind.Integer, Integer: 0 }) {
        ++this._at;
        return "0";
      }

      return this.Name();
    }

    public VbVariable Variable() {
      var name = this.Name();
      var isArray = false;
      var bounds = new List<VbBounds>();
      if (this.TakeSymbol("(")) {
        if (!this.TakeSymbol(")")) {
          do
            bounds.Add(this.Bounds());
          while (this.TakeSymbol(","));
          this.Symbol(")");
        } else {
          isArray = true;
        }
      }

      this.Word("As");
      return new(name, new(this.Name())) { IsArray = isArray, Bounds = bounds };
    }

    public List<VbVariable> Variables() {
      var variables = new List<VbVariable>();
      do
        variables.Add(this.Variable());
      while (this.TakeSymbol(","));
      return variables;
    }

    public VbBounds Bounds() {
      var lower = this.Expression();
      this.Word("To");
      return new(lower, this.Expression());
    }

    public VbParameter Parameter() {
      var byVal = this.TakeWord("ByVal");
      if (!byVal)
        this.TakeWord("ByRef");
      var name = this.Name();
      var isArray = false;
      if (this.TakeSymbol("(")) {
        this.Symbol(")");
        isArray = true;
      }

      this.Word("As");
      return new(byVal, name, new(this.Name())) { IsArray = isArray };
    }

    public VbLoopTest? Test() {
      if (this.TakeWord("While"))
        return new(false, this.Expression());

      return this.TakeWord("Until") ? new(true, this.Expression()) : null;
    }

    public VbCaseClause Clause() {
      if (this.TakeWord("Is")) {
        var comparison = this.Current is { Kind: Kind.Symbol, Text: "=" or "<>" or "<" or "<=" or ">" or ">=" } token ? token.Text : throw this.Error("expected a comparison after Is");
        ++this._at;
        return new(this.Expression()) { Is = comparison };
      }

      var value = this.Expression();
      return this.TakeWord("To") ? new(value) { Upper = this.Expression() } : new(value);
    }

    public VbExpression Expression() => this.Binary(1);

    /// <summary>The operator of the current token if it is a binary operator of exactly a strength.</summary>
    private string? Operator(int strength) {
      if (this.Current is not { } token || token.Kind is not (Kind.Symbol or Kind.Name))
        return null;

      var text = token.Kind == Kind.Name ? Canonical(token.Text) : token.Text;
      return text is not null && VbPrinter.IsBinaryOperator(text) && VbPrinter.Precedence(text) == strength ? text : null;
    }

    private static string? Canonical(string word) => new[] { "Mod", "And", "Or", "Xor", "Eqv", "Imp" }.FirstOrDefault(candidate => string.Equals(candidate, word, StringComparison.OrdinalIgnoreCase));

    private VbExpression Binary(int strength) {
      if (strength == 6)
        return this.Not();
      if (strength == 13)
        return this.Negation();
      if (strength > 14)
        return this.Postfix();

      var left = this.Binary(strength + 1);
      while (this.Operator(strength) is { } op) {
        ++this._at;
        left = new VbBinary(left, op, this.Binary(strength + 1));
      }

      return left;
    }

    private VbExpression Not() {
      if (this.TakeWord("Not"))
        return new VbUnary("Not", this.Not());

      return this.Binary(7);
    }

    private VbExpression Negation() {
      if (this.TakeSymbol("-")) {
        var operand = this.Negation();
        return operand switch {
          VbInteger { Hex: false, Value: >= 0 } integer => integer with { Value = -integer.Value },
          VbFloat { Value: >= 0 } number when !double.IsNegative(number.Value) => number with { Value = -number.Value },
          _ => new VbUnary("-", operand)
        };
      }

      return this.Binary(14);
    }

    public VbExpression Postfix() {
      var expression = this.Primary();
      while (true) {
        if (this.TakeSymbol(".")) {
          expression = new VbMember(expression, this.Member());
          continue;
        }

        if (this.TakeSymbol("(")) {
          var arguments = new List<VbExpression>();
          if (!this.TakeSymbol(")")) {
            do
              arguments.Add(this.Expression());
            while (this.TakeSymbol(","));
            this.Symbol(")");
          }

          expression = new VbCall(expression, arguments);
          continue;
        }

        return expression;
      }
    }

    /// <summary>A variable or a field of one, without indexes: what <c>ReDim</c> resizes.</summary>
    public VbExpression Target() {
      VbExpression expression = new VbName(this.Name());
      while (this.TakeSymbol("."))
        expression = new VbMember(expression, this.Member());
      return expression;
    }

    private string Member() {
      if (this.Current is { Kind: Kind.Name } token) {
        ++this._at;
        return token.Text;
      }

      throw this.Error("expected a member name");
    }

    private VbExpression Primary() {
      var token = this.Current ?? throw this.Error("expected an expression");
      switch (token.Kind) {
        case Kind.Integer:
          ++this._at;
          return new VbInteger(token.Integer) { Suffix = token.Suffix, Hex = token.Hex };
        case Kind.Float:
          ++this._at;
          return new VbFloat(token.Float, token.Single);
        case Kind.String:
          ++this._at;
          return new VbString(token.Text);
        case Kind.Name when token.Is("True") || token.Is("False"):
          ++this._at;
          return new VbBoolean(token.Is("True"));
        case Kind.Name when !_keywords.Contains(token.Text):
          ++this._at;
          return new VbName(token.Text);
        case Kind.Symbol when token.Text == "(":
          ++this._at;
          var inner = this.Expression();
          this.Symbol(")");
          return inner;
        default:
          throw this.Error($"unexpected '{token.Text}'");
      }
    }

  }

}
