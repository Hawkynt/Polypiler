using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.Backends.Python.Printing;

namespace Polypiler.Core.Backends.Python.Syntax;

/// <summary>
/// Reads Python source into the syntax tree: the subset the backend writes, which is how the
/// parse-back check of every generated file and the source of the backend's own helpers come into
/// the tree. The grammar is that of The Python Language Reference (§2 "Lexical analysis", §6
/// "Expressions", §7 "Simple statements", §8 "Compound statements") restricted to the nodes of
/// <see cref="PyNode"/>: indentation by INDENT and DEDENT tokens, implicit line joining inside
/// brackets, comments, and the precedence of §6.17. Anything else is a <see cref="FormatException"/>
/// that names the line.
/// </summary>
public static class PythonSyntaxReader {

  /// <summary>Reads a module; the comment lines in front of its first statement become its header.</summary>
  /// <param name="source">The source text.</param>
  /// <returns>The module.</returns>
  /// <exception cref="FormatException">The text is not of the subset.</exception>
  public static PyModule ReadModule(string source) {
    ArgumentNullException.ThrowIfNull(source);
    var header = new List<string>();
    foreach (var line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) {
      if (!line.StartsWith('#'))
        break;

      header.Add(line.Length > 1 && line[1] == ' ' ? line[2..] : line[1..]);
    }

    var parser = new Parser(Tokenize(source));
    return new PyModule(header, parser.Module());
  }

  /// <summary>Reads one expression.</summary>
  /// <param name="source">The source text of the expression.</param>
  /// <returns>The expression.</returns>
  /// <exception cref="FormatException">The text is not one expression of the subset.</exception>
  public static PyExpression ReadExpression(string source) {
    ArgumentNullException.ThrowIfNull(source);
    var parser = new Parser(Tokenize(source));
    var expression = parser.Expression();
    parser.SkipNewlines();
    parser.Expect(TokenKind.End);
    return expression;
  }

  private enum TokenKind { Name, Number, String, Operator, Newline, Indent, Dedent, End }

  private sealed record Token(TokenKind Kind, string Text, int Line);

  private static readonly string[] _operators = [
    "**", "//", "<<", ">>", "<=", ">=", "==", "!=", ":=", "->",
    "+", "-", "*", "/", "%", "&", "|", "^", "~", "<", ">", "(", ")", "[", "]", "{", "}", ",", ":", ".", "=", "@"
  ];

  private static List<Token> Tokenize(string source) {
    var tokens = new List<Token>();
    var indents = new Stack<int>();
    indents.Push(0);
    var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    var depth = 0;
    for (var number = 1; number <= lines.Length; ++number) {
      var line = lines[number - 1];
      var position = 0;
      if (depth == 0) {
        while (position < line.Length && line[position] == ' ')
          ++position;

        if (position == line.Length || line[position] == '#')
          continue;

        if (line[position] == '\t')
          throw new FormatException($"line {number}: tabs are not used for indentation");

        if (position > indents.Peek()) {
          indents.Push(position);
          tokens.Add(new(TokenKind.Indent, "", number));
        }

        while (position < indents.Peek()) {
          indents.Pop();
          tokens.Add(new(TokenKind.Dedent, "", number));
        }

        if (position != indents.Peek())
          throw new FormatException($"line {number}: the indentation matches no enclosing level");
      }

      while (position < line.Length) {
        var character = line[position];
        if (character == ' ') {
          ++position;
          continue;
        }

        if (character == '#')
          break;

        if (char.IsAsciiLetter(character) || character == '_') {
          var start = position;
          while (position < line.Length && (char.IsAsciiLetterOrDigit(line[position]) || line[position] == '_'))
            ++position;

          tokens.Add(new(TokenKind.Name, line[start..position], number));
          continue;
        }

        if (char.IsAsciiDigit(character) || (character == '.' && position + 1 < line.Length && char.IsAsciiDigit(line[position + 1]))) {
          var start = position;
          while (position < line.Length && (char.IsAsciiLetterOrDigit(line[position]) || line[position] is '_' or '.'
            || (line[position] is '+' or '-' && char.ToLowerInvariant(line[position - 1]) == 'e' && !line[start..position].StartsWith("0x", StringComparison.OrdinalIgnoreCase))))
            ++position;

          tokens.Add(new(TokenKind.Number, line[start..position], number));
          continue;
        }

        if (character == '"') {
          var (value, end) = StringLiteral(line, position, number);
          tokens.Add(new(TokenKind.String, value, number));
          position = end;
          continue;
        }

        var op = _operators.FirstOrDefault(candidate => string.CompareOrdinal(line, position, candidate, 0, candidate.Length) == 0)
          ?? throw new FormatException($"line {number}: unexpected character '{character}'");
        depth += op switch { "(" or "[" or "{" => 1, ")" or "]" or "}" => -1, _ => 0 };
        if (depth < 0)
          throw new FormatException($"line {number}: '{op}' closes no bracket");

        tokens.Add(new(TokenKind.Operator, op, number));
        position += op.Length;
      }

      if (depth == 0 && tokens.Count > 0 && tokens[^1].Kind != TokenKind.Newline)
        tokens.Add(new(TokenKind.Newline, "", number));
    }

    if (depth != 0)
      throw new FormatException("the source ends inside brackets");

    while (indents.Pop() > 0)
      tokens.Add(new(TokenKind.Dedent, "", lines.Length));

    tokens.Add(new(TokenKind.End, "", lines.Length));
    return tokens;
  }

  /// <summary>A double-quoted string literal on one line with the escapes the printer writes: its value and the position after it.</summary>
  private static (string Value, int End) StringLiteral(string line, int start, int number) {
    var value = new StringBuilder();
    var position = start + 1;
    while (position < line.Length && line[position] != '"') {
      if (line[position] != '\\') {
        value.Append(line[position++]);
        continue;
      }

      if (position + 1 >= line.Length)
        throw new FormatException($"line {number}: the string is not closed");

      var escape = line[position + 1];
      position += 2;
      switch (escape) {
        case '\\' or '"' or '\'':
          value.Append(escape);
          break;
        case 'n':
          value.Append('\n');
          break;
        case 'r':
          value.Append('\r');
          break;
        case 't':
          value.Append('\t');
          break;
        case 'x' or 'u':
          var length = escape == 'x' ? 2 : 4;
          if (position + length > line.Length || !int.TryParse(line.AsSpan(position, length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var unit))
            throw new FormatException($"line {number}: a broken \\{escape} escape");

          value.Append((char)unit);
          position += length;
          break;
        default:
          throw new FormatException($"line {number}: the escape \\{escape} is not one the backend writes");
      }
    }

    if (position >= line.Length)
      throw new FormatException($"line {number}: the string is not closed");

    return (value.ToString(), position + 1);
  }

  private sealed class Parser(List<Token> tokens) {

    private int _index;

    private Token Current => tokens[this._index];

    private Token Next() => tokens[this._index++];

    private bool Is(TokenKind kind, string? text = null) => this.Current.Kind == kind && (text is null || this.Current.Text == text);

    private bool IsOperator(string text) => this.Is(TokenKind.Operator, text);

    private bool IsKeyword(string text) => this.Is(TokenKind.Name, text);

    private bool Accept(TokenKind kind, string? text = null) {
      if (!this.Is(kind, text))
        return false;

      ++this._index;
      return true;
    }

    public Token Expect(TokenKind kind, string? text = null) {
      if (!this.Is(kind, text))
        throw this.Error($"expected {(text is null ? kind.ToString() : $"'{text}'")}");

      return this.Next();
    }

    private FormatException Error(string message) => new($"line {this.Current.Line}: {message}, found {this.Current.Kind} '{this.Current.Text}'");

    public void SkipNewlines() {
      while (this.Accept(TokenKind.Newline)) {
      }
    }

    public List<PyStatement> Module() {
      var body = new List<PyStatement>();
      this.SkipNewlines();
      while (!this.Is(TokenKind.End))
        body.Add(this.Statement());

      return body;
    }

    private List<PyStatement> Block() {
      this.Expect(TokenKind.Operator, ":");
      this.Expect(TokenKind.Newline);
      this.Expect(TokenKind.Indent);
      var body = new List<PyStatement>();
      while (!this.Accept(TokenKind.Dedent))
        body.Add(this.Statement());

      return body.Count == 1 && body[0] is PyPass ? [] : body;
    }

    private PyStatement Statement() {
      if (this.IsOperator("@")) {
        var decorators = new List<PyExpression>();
        while (this.Accept(TokenKind.Operator, "@")) {
          decorators.Add(this.Primary());
          this.Expect(TokenKind.Newline);
        }

        return this.IsKeyword("class") ? this.Class() with { Decorators = decorators } : this.Function() with { Decorators = decorators };
      }

      if (this.IsKeyword("def") || this.IsKeyword("async"))
        return this.Function();

      if (this.IsKeyword("class"))
        return this.Class();

      if (this.Accept(TokenKind.Name, "try"))
        return this.Try();

      if (this.IsKeyword("if"))
        return this.If();

      if (this.Accept(TokenKind.Name, "while")) {
        var test = this.Expression();
        return new PyWhile(test, this.Block());
      }

      if (this.Accept(TokenKind.Name, "for")) {
        var variable = this.Expect(TokenKind.Name).Text;
        while (this.Accept(TokenKind.Operator, ","))
          variable += ", " + this.Expect(TokenKind.Name).Text;

        this.Expect(TokenKind.Name, "in");
        var iterable = this.Expression();
        return new PyFor(variable, iterable, this.Block());
      }

      // "match" is a soft keyword: a name where an assignment, an attribute or a call follows it.
      if (this.IsKeyword("match") && tokens[this._index + 1] is not { Kind: TokenKind.Operator, Text: "=" or "." or ":" or "[" or "(" } and not { Kind: TokenKind.Newline })
        return this.Match();

      var simple = this.Simple();
      this.Expect(TokenKind.Newline);
      return simple;
    }

    private PyStatement Simple() {
      if (this.Accept(TokenKind.Name, "pass"))
        return new PyPass();

      if (this.Accept(TokenKind.Name, "break"))
        return new PyBreak();

      if (this.Accept(TokenKind.Name, "continue"))
        return new PyContinue();

      if (this.Accept(TokenKind.Name, "return"))
        return new PyReturn(this.Is(TokenKind.Newline) ? null : this.Expression());

      if (this.Accept(TokenKind.Name, "raise"))
        return new PyRaise(this.Is(TokenKind.Newline) ? null : this.Expression());

      if (this.Accept(TokenKind.Name, "global"))
        return new PyGlobal(this.Names());

      if (this.Accept(TokenKind.Name, "nonlocal"))
        return new PyNonlocal(this.Names());

      if (this.Accept(TokenKind.Name, "import"))
        return new PyImport(this.Dotted());

      if (this.Accept(TokenKind.Name, "from")) {
        var module = this.Dotted();
        this.Expect(TokenKind.Name, "import");
        return new PyImportFrom(module, this.Names());
      }

      var target = this.Expression();
      if (this.Accept(TokenKind.Operator, ":")) {
        var annotation = this.Expression();
        if (!this.Accept(TokenKind.Operator, "="))
          return target is PyName name ? new PyAnnotation(name.Name, annotation) : throw this.Error("only a name is declared with a type");

        return new PyAssign(target, this.Expression()) { Annotation = annotation };
      }

      return this.Accept(TokenKind.Operator, "=") ? new PyAssign(target, this.Expression()) : new PyExpressionStatement(target);
    }

    private List<string> Names() {
      var names = new List<string> { this.Expect(TokenKind.Name).Text };
      while (this.Accept(TokenKind.Operator, ","))
        names.Add(this.Expect(TokenKind.Name).Text);

      return names;
    }

    private string Dotted() {
      var name = this.Expect(TokenKind.Name).Text;
      while (this.Accept(TokenKind.Operator, "."))
        name += "." + this.Expect(TokenKind.Name).Text;

      return name;
    }

    private PyClassDef Class() {
      this.Expect(TokenKind.Name, "class");
      var name = this.Expect(TokenKind.Name).Text;
      var bases = this.Accept(TokenKind.Operator, "(") ? this.Items(")") : [];
      return new PyClassDef(name, bases, this.Block());
    }

    private PyTry Try() {
      var body = this.Block();
      var handlers = new List<PyExceptHandler>();
      while (this.Accept(TokenKind.Name, "except")) {
        var type = this.IsOperator(":") ? null : this.Expression();
        var bound = this.Accept(TokenKind.Name, "as") ? this.Expect(TokenKind.Name).Text : null;
        handlers.Add(new PyExceptHandler(type, bound, this.Block()));
      }

      var hasFinally = this.Accept(TokenKind.Name, "finally");
      if (handlers.Count == 0 && !hasFinally)
        throw this.Error("expected 'except' or 'finally'");

      var @finally = hasFinally ? this.Block() : [];

      return new PyTry(body, handlers, @finally);
    }

    private PyFunctionDef Function() {
      var isAsync = this.Accept(TokenKind.Name, "async");
      this.Expect(TokenKind.Name, "def");
      var name = this.Expect(TokenKind.Name).Text;
      this.Expect(TokenKind.Operator, "(");
      var parameters = new List<PyParameter>();
      while (!this.Accept(TokenKind.Operator, ")")) {
        var parameter = (this.Accept(TokenKind.Operator, "*") ? "*" : "") + this.Expect(TokenKind.Name).Text;
        var annotation = this.Accept(TokenKind.Operator, ":") ? this.Expression() : null;
        var value = this.Accept(TokenKind.Operator, "=") ? this.Expression() : null;
        parameters.Add(new PyParameter(parameter, annotation) { Default = value });
        if (!this.Accept(TokenKind.Operator, ",") && !this.IsOperator(")"))
          throw this.Error("expected ',' or ')'");
      }

      this.Expect(TokenKind.Operator, "->");
      var returns = this.Expression();
      return new PyFunctionDef(name, parameters, returns, this.Block()) { IsAsync = isAsync };
    }

    private PyIf If() {
      this.Next();
      var test = this.Expression();
      var body = this.Block();
      if (this.IsKeyword("elif"))
        return new PyIf(test, body, [this.If()]);

      return this.Accept(TokenKind.Name, "else") ? new PyIf(test, body, this.Block()) : new PyIf(test, body, []);
    }

    private PyMatchStatement Match() {
      this.Expect(TokenKind.Name, "match");
      var subject = this.Expression();
      this.Expect(TokenKind.Operator, ":");
      this.Expect(TokenKind.Newline);
      this.Expect(TokenKind.Indent);
      var cases = new List<PyMatchCase>();
      while (!this.Accept(TokenKind.Dedent)) {
        this.Expect(TokenKind.Name, "case");
        var patterns = new List<PyExpression>();
        if (!this.Accept(TokenKind.Name, "_")) {
          patterns.Add(this.Unary());
          while (this.Accept(TokenKind.Operator, "|"))
            patterns.Add(this.Unary());
        }

        var guard = this.Accept(TokenKind.Name, "if") ? this.Expression() : null;
        cases.Add(new PyMatchCase(patterns, guard, this.Block()));
      }

      return new PyMatchStatement(subject, cases);
    }

    public PyExpression Expression() {
      if (this.Accept(TokenKind.Name, "lambda")) {
        var parameters = new List<string>();
        while (!this.Accept(TokenKind.Operator, ":")) {
          parameters.Add(this.Expect(TokenKind.Name).Text);
          if (!this.Accept(TokenKind.Operator, ",") && !this.IsOperator(":"))
            throw this.Error("expected ',' or ':'");
        }

        return new PyLambda(parameters, this.Expression());
      }

      if (this.Accept(TokenKind.Name, "yield")) {
        if (this.Accept(TokenKind.Name, "from"))
          return new PyYield(this.Expression(), true);

        return new PyYield(this.Is(TokenKind.Newline) || this.IsOperator(")") ? null : this.Expression(), false);
      }

      var whenTrue = this.Or();
      if (!this.Accept(TokenKind.Name, "if"))
        return whenTrue;

      var test = this.Or();
      this.Expect(TokenKind.Name, "else");
      return new PyConditional(test, whenTrue, this.Expression());
    }

    private PyExpression Or() {
      var left = this.And();
      while (this.Accept(TokenKind.Name, "or"))
        left = new PyBoolOp(left, "or", this.And());

      return left;
    }

    private PyExpression And() {
      var left = this.Not();
      while (this.Accept(TokenKind.Name, "and"))
        left = new PyBoolOp(left, "and", this.Not());

      return left;
    }

    private PyExpression Not() => this.Accept(TokenKind.Name, "not") ? new PyUnary("not", this.Not()) : this.Comparison();

    private static readonly string[] _comparisons = ["<", "<=", ">", ">=", "==", "!="];

    private PyExpression Comparison() {
      var first = this.Binary(0);
      var operands = new List<PyExpression> { first };
      var operators = new List<string>();
      while (true) {
        if (this.Current.Kind == TokenKind.Operator && _comparisons.Contains(this.Current.Text))
          operators.Add(this.Next().Text);
        else if (this.Accept(TokenKind.Name, "in"))
          operators.Add("in");
        else if (this.IsKeyword("not") && tokens[this._index + 1] is { Kind: TokenKind.Name, Text: "in" }) {
          this._index += 2;
          operators.Add("not in");
        } else if (this.Accept(TokenKind.Name, "is"))
          operators.Add(this.Accept(TokenKind.Name, "not") ? "is not" : "is");
        else
          break;

        operands.Add(this.Binary(0));
      }

      return operators.Count == 0 ? first : new PyCompare(operands, operators);
    }

    /// <summary>The binary operators from the loosest to the tightest, each level left-associative.</summary>
    private static readonly string[][] _levels = [["|"], ["^"], ["&"], ["<<", ">>"], ["+", "-"], ["*", "/", "//", "%"]];

    private PyExpression Binary(int level) {
      if (level == _levels.Length)
        return this.Unary();

      var left = this.Binary(level + 1);
      while (this.Current.Kind == TokenKind.Operator && _levels[level].Contains(this.Current.Text)) {
        var op = this.Next().Text;
        left = new PyBinary(left, op, this.Binary(level + 1));
      }

      return left;
    }

    private PyExpression Unary() {
      if (this.Current.Kind == TokenKind.Operator && this.Current.Text is "-" or "+" or "~") {
        var op = this.Next().Text;
        var operand = this.Unary();
        // A minus in front of a literal that is not negative is the negative literal the printer
        // writes; the printer puts a negative one in parentheses where a minus stands in front.
        return (op, operand) switch {
          ("-", PyInt { Value.Sign: >= 0 } integer) => new PyInt(-integer.Value),
          ("-", PyFloat { Value: var value }) when !double.IsNegative(value) => new PyFloat(-value),
          _ => new PyUnary(op, operand)
        };
      }

      return this.Power();
    }

    private PyExpression Power() {
      var left = this.Accept(TokenKind.Name, "await") ? new PyAwait(this.Primary()) : this.Primary();
      return this.Accept(TokenKind.Operator, "**") ? new PyBinary(left, "**", this.Unary()) : left;
    }

    public PyExpression Primary() {
      var expression = this.Atom();
      while (true) {
        if (this.Accept(TokenKind.Operator, ".")) {
          expression = new PyAttribute(expression, this.Expect(TokenKind.Name).Text);
        } else if (this.Accept(TokenKind.Operator, "(")) {
          var arguments = new List<PyExpression>();
          var keywords = new List<PyKeyword>();
          while (!this.Accept(TokenKind.Operator, ")")) {
            if (this.Is(TokenKind.Name) && tokens[this._index + 1] is { Kind: TokenKind.Operator, Text: "=" }) {
              var keyword = this.Next().Text;
              this.Next();
              keywords.Add(new PyKeyword(keyword, this.Expression()));
            } else {
              arguments.Add(this.Accept(TokenKind.Operator, "*") ? new PyStarred(this.Binary(0)) : this.Expression());
            }

            if (!this.Accept(TokenKind.Operator, ",") && !this.IsOperator(")"))
              throw this.Error("expected ',' or ')'");
          }

          expression = new PyCall(expression, arguments) { Keywords = keywords };
        } else if (this.Accept(TokenKind.Operator, "[")) {
          var lower = this.IsOperator(":") ? null : this.Expression();
          PyExpression index = lower!;
          if (this.Accept(TokenKind.Operator, ":")) {
            index = new PySlice(lower, this.IsOperator("]") ? null : this.Expression());
          } else if (this.IsOperator(",")) {
            var indexes = new List<PyExpression> { index };
            while (this.Accept(TokenKind.Operator, ","))
              indexes.Add(this.Expression());

            index = new PyTuple(indexes);
          }

          this.Expect(TokenKind.Operator, "]");
          expression = new PySubscript(expression, index);
        } else {
          return expression;
        }
      }
    }

    private PyExpression Atom() {
      var token = this.Next();
      switch (token.Kind) {
        case TokenKind.Number:
          return Number(token);
        case TokenKind.String:
          return new PyString(token.Text);
        case TokenKind.Name when token.Text == "True":
          return new PyBool(true);
        case TokenKind.Name when token.Text == "False":
          return new PyBool(false);
        case TokenKind.Name when token.Text == "None":
          return new PyNone();
        case TokenKind.Name when token.Text is "if" or "else" or "and" or "or" or "not" or "in" or "is" or "def" or "class" or "return" or "while" or "for" or "try" or "except" or "finally" or "lambda" or "yield" or "raise" or "async" or "await":
          throw new FormatException($"line {token.Line}: the keyword '{token.Text}' stands where an expression is expected");
        case TokenKind.Name:
          return new PyName(token.Text);
        case TokenKind.Operator when token.Text == "[":
          var items = this.Items("]");
          return new PyList(items) { Tabulated = this.Spans(token) };
        case TokenKind.Operator when token.Text == "{":
          var keys = new List<PyExpression>();
          var values = new List<PyExpression>();
          while (!this.Accept(TokenKind.Operator, "}")) {
            keys.Add(this.Expression());
            this.Expect(TokenKind.Operator, ":");
            values.Add(this.Expression());
            if (!this.Accept(TokenKind.Operator, ",") && !this.IsOperator("}"))
              throw this.Error("expected ',' or '}'");
          }

          return new PyDict(keys, values);
        case TokenKind.Operator when token.Text == "(":
          if (this.Is(TokenKind.Name) && tokens[this._index + 1] is { Kind: TokenKind.Operator, Text: ":=" }) {
            var name = this.Next().Text;
            this.Next();
            var value = this.Expression();
            this.Expect(TokenKind.Operator, ")");
            return new PyNamedExpression(name, value);
          }

          if (this.Accept(TokenKind.Operator, ")"))
            return new PyTuple([]);

          var first = this.Expression();
          if (this.Accept(TokenKind.Operator, ")"))
            return first;

          this.Expect(TokenKind.Operator, ",");
          var elements = new List<PyExpression> { first };
          elements.AddRange(this.Items(")"));
          return new PyTuple(elements) { Tabulated = this.Spans(token) };
        default:
          throw new FormatException($"line {token.Line}: unexpected {token.Kind} '{token.Text}'");
      }
    }

    /// <summary>The elements of a display up to its closing bracket, a trailing comma allowed.</summary>
    /// <summary>Whether a display that opened at a token ended on another line: the printer writes a table that way.</summary>
    private bool Spans(Token open) => tokens[this._index - 1].Line != open.Line;

    private List<PyExpression> Items(string close) {
      var items = new List<PyExpression>();
      while (!this.Accept(TokenKind.Operator, close)) {
        items.Add(this.Expression());
        if (!this.Accept(TokenKind.Operator, ",") && !this.IsOperator(close))
          throw this.Error($"expected ',' or '{close}'");
      }

      return items;
    }

    private static PyExpression Number(Token token) {
      if (PythonLiterals.ParseInteger(token.Text) is { } integer)
        return new PyInt(integer) { Raw = token.Text };

      if (double.TryParse(token.Text.Replace("_", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
        return new PyFloat(value);

      throw new FormatException($"line {token.Line}: '{token.Text}' is no number");
    }

  }

}
