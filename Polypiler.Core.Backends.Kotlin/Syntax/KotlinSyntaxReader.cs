using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.Backends.Kotlin.Printing;

namespace Polypiler.Core.Backends.Kotlin.Syntax;

/// <summary>
/// Reads the Kotlin <see cref="KotlinPrinter"/> writes back into the syntax tree, for the parse-back
/// sweep: printing what it reads gives the text it read. It knows the subset the printer writes - one
/// statement per line, newlines insignificant inside parentheses and brackets, the precedence of the
/// Kotlin grammar (kotlinlang.org/spec, "Expressions") - and rejects anything else with a
/// <see cref="FormatException"/> that names the line.
/// </summary>
public static class KotlinSyntaxReader {

  /// <summary>Reads a file.</summary>
  /// <param name="text">The text.</param>
  /// <returns>The tree.</returns>
  /// <exception cref="FormatException">The text is not Kotlin of the printed subset.</exception>
  public static KtFile Read(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var header = new List<string>();
    var lines = text.Split('\n');
    var index = 0;
    while (index < lines.Length && lines[index].StartsWith("//", StringComparison.Ordinal)) {
      header.Add(lines[index].Length > 3 ? lines[index][3..] : "");
      ++index;
    }

    var parser = new Parser(Lexer.Tokens(string.Join('\n', lines.Skip(index))), index);
    return parser.File(header);
  }

  /// <summary>Reads one expression.</summary>
  /// <param name="text">The text.</param>
  /// <returns>The tree.</returns>
  /// <exception cref="FormatException">The text is not one expression of the printed subset.</exception>
  public static KtExpression ReadExpression(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var parser = new Parser(Lexer.Tokens(text), 0);
    var expression = parser.Expression();
    parser.End();
    return expression;
  }

  private enum Kind { Name, Number, String, Character, Symbol, Newline, Documentation, End }

  private sealed record Token(Kind Kind, string Text, int Line);

  private static FormatException Error(int line, string message) => new($"line {line.ToString(CultureInfo.InvariantCulture)}: {message}");

  /// <summary>Cuts the text into tokens; a newline is a token only where it separates statements: outside every parenthesis and bracket, or inside a brace.</summary>
  private static class Lexer {

    private static readonly string[] _symbols = [
      "..<", "===", "!==", "?.", "?:", "!!", "++", "--", "->", "::", "==", "!=", "<=", ">=", "&&", "||", "+=", "-=", "*=", "/=", "%=", "..",
      "{", "}", "(", ")", "[", "]", ",", ".", ":", ";", "@", "!", "+", "-", "*", "/", "%", "=", "<", ">", "?"
    ];

    public static List<Token> Tokens(string text) {
      var tokens = new List<Token>();
      var brackets = new Stack<char>();
      var line = 1;
      var at = 0;
      while (at < text.Length) {
        var character = text[at];
        if (character == '\n') {
          if ((brackets.Count == 0 || brackets.Peek() == '{') && tokens.Count > 0 && tokens[^1].Kind != Kind.Newline)
            tokens.Add(new(Kind.Newline, "\n", line));
          ++line;
          ++at;
        } else if (character is ' ' or '\t' or '\r') {
          ++at;
        } else if (text.AsSpan(at).StartsWith("/**")) {
          var end = text.IndexOf("*/", at, StringComparison.Ordinal);
          if (end < 0)
            throw Error(line, "an unterminated comment");

          var body = text[(at + 3)..end];
          var documentation = body.Split('\n').Select(part => part.Trim()).Where(part => part.StartsWith('*')).Select(part => part.Length > 2 ? part[2..] : "");
          tokens.Add(new(Kind.Documentation, string.Join('\n', documentation), line));
          line += body.Count(unit => unit == '\n');
          at = end + 2;
        } else if (char.IsAsciiLetter(character) || character == '_') {
          var start = at;
          while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] == '_'))
            ++at;

          tokens.Add(new(Kind.Name, text[start..at], line));
        } else if (char.IsAsciiDigit(character)) {
          tokens.Add(new(Kind.Number, Number(text, ref at), line));
        } else if (character == '"') {
          tokens.Add(new(Kind.String, Quoted(text, ref at, line), line));
        } else if (character == '\'') {
          var end = text.IndexOf('\'', at + 1);
          if (end < 0)
            throw Error(line, "an unterminated character");

          tokens.Add(new(Kind.Character, text[at..(end + 1)], line));
          at = end + 1;
        } else {
          var symbol = _symbols.FirstOrDefault(candidate => text.AsSpan(at).StartsWith(candidate)) ?? throw Error(line, $"an unexpected character '{character}'");
          if (symbol is "(" or "[" or "{")
            brackets.Push(symbol[0]);
          else if (symbol is ")" or "]" or "}" && brackets.Count > 0)
            brackets.Pop();

          tokens.Add(new(Kind.Symbol, symbol, line));
          at += symbol.Length;
        }
      }

      tokens.Add(new(Kind.End, "", line));
      return tokens;
    }

    /// <summary>A number: digits, a fraction and an exponent where it has them, and Kotlin's suffix.</summary>
    private static string Number(string text, ref int at) {
      var start = at;
      while (at < text.Length && char.IsAsciiDigit(text[at]))
        ++at;

      if (at + 1 < text.Length && text[at] == '.' && char.IsAsciiDigit(text[at + 1])) {
        ++at;
        while (at < text.Length && char.IsAsciiDigit(text[at]))
          ++at;
      }

      if (at < text.Length && text[at] is 'E' or 'e') {
        ++at;
        if (at < text.Length && text[at] is '+' or '-')
          ++at;

        while (at < text.Length && char.IsAsciiDigit(text[at]))
          ++at;
      }

      while (at < text.Length && text[at] is 'u' or 'L' or 'f')
        ++at;

      return text[start..at];
    }

    /// <summary>The characters of a string literal, its escapes read.</summary>
    private static string Quoted(string text, ref int at, int line) {
      var value = new StringBuilder();
      ++at;
      while (true) {
        if (at >= text.Length || text[at] == '\n')
          throw Error(line, "an unterminated string");

        var character = text[at++];
        if (character == '"')
          return value.ToString();

        if (character != '\\') {
          value.Append(character);
          continue;
        }

        var escape = text[at++];
        value.Append(escape switch {
          'n' => '\n',
          'r' => '\r',
          't' => '\t',
          'b' => '\b',
          'u' => (char)int.Parse(text.AsSpan(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
          _ => escape
        });
        if (escape == 'u')
          at += 4;
      }
    }

  }

  private sealed class Parser(List<Token> tokens, int offset) {

    private int _at;

    private Token Peek => tokens[this._at];

    private Token PeekAt(int ahead) => tokens[Math.Min(this._at + ahead, tokens.Count - 1)];

    private Token Next() => tokens[this._at++];

    private bool Is(string symbol) => this.Peek.Kind is Kind.Symbol or Kind.Name && this.Peek.Text == symbol;

    private bool Accept(string symbol) {
      if (!this.Is(symbol))
        return false;

      ++this._at;
      return true;
    }

    private void Expect(string symbol) {
      if (!this.Accept(symbol))
        throw this.Unexpected($"'{symbol}'");
    }

    private FormatException Unexpected(string wanted)
      => Error(this.Peek.Line + offset, $"{wanted} expected, found '{(this.Peek.Kind == Kind.Newline ? "a newline" : this.Peek.Text)}'");

    private string Identifier() => this.Peek.Kind == Kind.Name ? this.Next().Text : throw this.Unexpected("a name");

    private void Newlines() {
      while (this.Peek.Kind == Kind.Newline)
        ++this._at;
    }

    public void End() {
      this.Newlines();
      if (this.Peek.Kind != Kind.End)
        throw this.Unexpected("the end");
    }

    #region Declarations

    public KtFile File(List<string> header) {
      this.Newlines();
      var suppressions = new List<string>();
      if (this.Is("@") && this.PeekAt(1).Text == "file") {
        this._at += 2;
        this.Expect(":");
        this.Expect("Suppress");
        this.Expect("(");
        do
          suppressions.Add(this.Peek.Kind == Kind.String ? this.Next().Text : throw this.Unexpected("a string"));
        while (this.Accept(","));

        this.Expect(")");
        this.Newlines();
      }

      string? package = null;
      if (this.Accept("package")) {
        package = this.QualifiedName();
        this.Newlines();
      }

      var imports = new List<string>();
      while (this.Accept("import")) {
        imports.Add(this.QualifiedName());
        this.Newlines();
      }

      var declarations = new List<KtDeclaration>();
      while (this.Peek.Kind != Kind.End) {
        declarations.Add(this.Declaration());
        this.Newlines();
      }

      return new KtFile(header, package, imports, declarations) { Suppressions = suppressions };
    }

    private string QualifiedName() {
      var name = new StringBuilder(this.Identifier());
      while (this.Accept("."))
        name.Append('.').Append(this.Identifier());

      return name.ToString();
    }

    private static readonly HashSet<string> _modifiers = new(StringComparer.Ordinal) {
      "private", "public", "internal", "protected", "const", "data", "open", "abstract", "override", "final", "inline", "operator", "infix", "sealed", "lateinit", "tailrec"
    };

    private KtDeclaration Declaration() {
      IReadOnlyList<string> documentation = this.Peek.Kind == Kind.Documentation ? this.Next().Text.Split('\n') : [];
      this.Newlines();
      var modifiers = new List<string>();
      while (this.Peek.Kind == Kind.Name && _modifiers.Contains(this.Peek.Text) && !(this.PeekAt(1).Kind == Kind.Symbol && this.PeekAt(1).Text is "(" or ":" or "="))
        modifiers.Add(this.Next().Text);

      KtDeclaration declaration = this.Peek.Text switch {
        "fun" => this.Function(),
        "val" or "var" => this.Property(),
        "class" or "interface" or "object" or "enum" => this.Class(),
        "constructor" => this.Constructor(),
        "init" => this.Initializer(),
        _ => throw this.Unexpected("a declaration")
      };
      return declaration with { Modifiers = modifiers, Documentation = documentation };
    }

    private KtFunction Function() {
      this.Expect("fun");
      var name = this.Identifier();
      this.Expect("(");
      var parameters = this.Parameters(")");
      KtType? result = this.Accept(":") ? this.Type() : null;
      if (this.Accept("="))
        return new KtFunction(name, parameters, result, null) { Expression = this.Expression() };

      return new KtFunction(name, parameters, result, this.Is("{") ? this.Block() : null);
    }

    private List<KtParameter> Parameters(string close) {
      var parameters = new List<KtParameter>();
      if (this.Accept(close))
        return parameters;

      do {
        string? property = this.Is("val") || this.Is("var") ? this.Next().Text : null;
        var name = this.Identifier();
        KtType? type = this.Accept(":") ? this.Type() : null;
        var value = this.Accept("=") ? this.Expression() : null;
        parameters.Add(new KtParameter(name, type) { Property = property, Default = value });
      } while (this.Accept(","));

      this.Expect(close);
      return parameters;
    }

    private KtProperty Property() {
      var mutable = this.Next().Text == "var";
      var name = this.Identifier();
      KtType? type = this.Accept(":") ? this.Type() : null;
      var value = this.Accept("=") ? this.Expression() : null;
      return new KtProperty(mutable, name, type, value);
    }

    private KtClass Class() {
      var kind = this.Next().Text;
      if (kind == "enum") {
        this.Expect("class");
        kind = "enum class";
      }

      var name = this.Identifier();
      List<KtParameter>? parameters = null;
      if (this.Accept("("))
        parameters = this.Parameters(")");

      var supers = new List<KtSuperType>();
      if (this.Accept(":")) {
        do {
          var type = this.Type();
          supers.Add(new KtSuperType(type, this.Accept("(") ? this.Arguments(")") : null));
        } while (this.Accept(","));
      }

      var members = new List<KtDeclaration>();
      var entries = new List<KtEnumEntry>();
      if (!this.Accept("{"))
        return new KtClass(kind, name, parameters, supers, members);

      this.Newlines();
      if (kind == "enum class" && !this.Is("}")) {
        while (true) {
          var entry = this.Identifier();
          entries.Add(new KtEnumEntry(entry, this.Accept("(") ? this.Arguments(")") : []));
          if (this.Accept(";") || !this.Accept(","))
            break;

          this.Newlines();
        }

        this.Newlines();
      }

      while (!this.Accept("}")) {
        members.Add(this.Declaration());
        this.Newlines();
      }

      return new KtClass(kind, name, parameters, supers, members) { Entries = entries };
    }

    private KtConstructor Constructor() {
      this.Expect("constructor");
      this.Expect("(");
      var parameters = this.Parameters(")");
      string? delegation = null;
      List<KtExpression> arguments = [];
      if (this.Accept(":")) {
        delegation = this.Next().Text;
        this.Expect("(");
        arguments = this.Arguments(")");
      }

      return new KtConstructor(parameters, delegation, arguments, this.Block());
    }

    private KtInitializer Initializer() {
      this.Expect("init");
      return new KtInitializer(this.Block());
    }

    private KtType Type() {
      KtType type;
      if (this.Accept("(")) {
        if (this.Is("(") || !this.LooksLikeFunctionType()) {
          type = this.Type();
          this.Expect(")");
        } else {
          var parameters = new List<KtType>();
          if (!this.Accept(")")) {
            do
              parameters.Add(this.Type());
            while (this.Accept(","));

            this.Expect(")");
          }

          this.Expect("->");
          type = new KtFunctionType(parameters, this.Type());
        }
      } else {
        var name = this.QualifiedName();
        var arguments = new List<KtType>();
        if (this.Accept("<")) {
          do
            arguments.Add(this.Type());
          while (this.Accept(","));

          this.Expect(">");
        }

        type = new KtNamedType(name, arguments);
      }

      return this.Accept("?") ? type with { Nullable = true } : type;
    }

    /// <summary>Whether the parenthesis just read opens the parameters of a function type: its closing one is followed by an arrow.</summary>
    private bool LooksLikeFunctionType() {
      var depth = 1;
      for (var ahead = 0; ; ++ahead) {
        var token = this.PeekAt(ahead);
        if (token.Kind == Kind.End)
          return false;

        if (token.Text == "(")
          ++depth;
        else if (token.Text == ")" && --depth == 0)
          return this.PeekAt(ahead + 1).Text == "->";
      }
    }

    #endregion

    #region Statements

    private KtBlock Block() {
      this.Expect("{");
      var statements = new List<KtStatement>();
      this.Newlines();
      while (!this.Accept("}")) {
        statements.Add(this.Statement());
        this.Newlines();
      }

      return new KtBlock(statements);
    }

    private KtStatement Statement() {
      string? label = null;
      if (this.Peek.Kind == Kind.Name && this.PeekAt(1).Text == "@" && this.PeekAt(2).Text is "while" or "do" or "for") {
        label = this.Next().Text;
        this.Next();
      }

      switch (this.Peek.Text) {
        case "val" or "var" when this.Peek.Kind == Kind.Name: {
          var property = this.Property();
          return new KtVariable(property.Mutable, property.Name, property.Type, property.Initializer);
        }
        case "fun" when this.Peek.Kind == Kind.Name:
          return new KtLocalFunction(this.Function());
        case "private" when this.PeekAt(1).Text == "fun":
          this.Next();
          return new KtLocalFunction(this.Function() with { Modifiers = ["private"] });
        case "if" when this.Peek.Kind == Kind.Name:
          return this.If();
        case "while" when this.Peek.Kind == Kind.Name: {
          this.Next();
          var condition = this.Condition();
          return new KtWhile(label, condition, this.Block());
        }
        case "do" when this.Peek.Kind == Kind.Name: {
          this.Next();
          var body = this.Block();
          this.Expect("while");
          return new KtDoWhile(label, body, this.Condition());
        }
        case "for" when this.Peek.Kind == Kind.Name: {
          this.Next();
          this.Expect("(");
          var variable = this.Identifier();
          this.Expect("in");
          var range = this.Expression();
          this.Expect(")");
          return new KtFor(label, variable, range, this.Block());
        }
        case "when" when this.Peek.Kind == Kind.Name:
          return this.When();
        case "break" or "continue" when this.Peek.Kind == Kind.Name: {
          var keyword = this.Next().Text;
          string? target = this.Accept("@") ? this.Identifier() : null;
          return keyword == "break" ? new KtBreak(target) : new KtContinue(target);
        }
        case "return" when this.Peek.Kind == Kind.Name:
          this.Next();
          return new KtReturn(this.Peek.Kind is Kind.Newline or Kind.End || this.Is("}") ? null : this.Expression());
        case "throw" when this.Peek.Kind == Kind.Name:
          this.Next();
          return new KtThrow(this.Expression());
        case "try" when this.Peek.Kind == Kind.Name:
          return this.Try();
      }

      var expression = this.Expression();
      if (this.Peek.Kind == Kind.Symbol && this.Peek.Text is "=" or "+=" or "-=" or "*=" or "/=" or "%=") {
        var op = this.Next().Text;
        return new KtAssignment(expression, op, this.Expression());
      }

      return new KtExpressionStatement(expression);
    }

    private KtExpression Condition() {
      this.Expect("(");
      var condition = this.Expression();
      this.Expect(")");
      return condition;
    }

    private KtIf If() {
      this.Expect("if");
      var condition = this.Condition();
      var then = this.Block();
      if (!this.Accept("else"))
        return new KtIf(condition, then, null);

      return new KtIf(condition, then, this.Is("if") ? this.If() : this.Block());
    }

    private KtWhen When() {
      this.Expect("when");
      KtExpression? subject = this.Is("(") ? this.Condition() : null;
      this.Expect("{");
      this.Newlines();
      var entries = new List<KtWhenEntry>();
      while (!this.Accept("}")) {
        var conditions = new List<KtExpression>();
        if (!this.Accept("else")) {
          do
            conditions.Add(this.Expression());
          while (this.Accept(","));
        }

        this.Expect("->");
        entries.Add(new KtWhenEntry(conditions, this.Block()));
        this.Newlines();
      }

      return new KtWhen(subject, entries);
    }

    private KtTry Try() {
      this.Expect("try");
      var body = this.Block();
      var catches = new List<KtCatch>();
      while (this.Accept("catch")) {
        this.Expect("(");
        var name = this.Identifier();
        this.Expect(":");
        var type = this.Type();
        this.Expect(")");
        catches.Add(new KtCatch(name, type, this.Block()));
      }

      return new KtTry(body, catches, this.Accept("finally") ? this.Block() : null);
    }

    #endregion

    #region Expressions

    public KtExpression Expression() => this.Binary(3);

    /// <summary>The level of a binary operator at the current token, or 0 where none stands.</summary>
    private int Level() {
      var token = this.Peek;
      if (token.Kind == Kind.Name)
        return token.Text switch {
          "in" or "is" => 7,
          "as" => 13,
          _ when KotlinPrinter.InfixFunctions.Contains(token.Text) => 9,
          _ => 0
        };

      if (token.Kind != Kind.Symbol)
        return 0;

      if (token.Text == "!" && this.PeekAt(1).Kind == Kind.Name && this.PeekAt(1).Text is "in" or "is")
        return 7;

      return token.Text switch {
        "*" or "/" or "%" or "+" or "-" or ".." or "..<" or "?:" or "<" or ">" or "<=" or ">=" or "==" or "!=" or "===" or "!==" or "&&" or "||" => KotlinPrinter.Precedence(token.Text),
        _ => 0
      };
    }

    private KtExpression Binary(int minimum) {
      var left = this.Prefix();
      while (true) {
        var level = this.Level();
        if (level == 0 || level < minimum)
          return left;

        var token = this.Next();
        if (token.Text == "!") {
          var negated = this.Next().Text;
          left = negated == "is" ? new KtIs(left, this.Type(), true) : new KtBinary(left, "!in", this.Binary(level + 1));
        } else if (token.Text == "is") {
          left = new KtIs(left, this.Type(), false);
        } else if (token.Text == "as") {
          var safe = this.Accept("?");
          left = new KtAs(left, this.Type(), safe);
        } else {
          left = new KtBinary(left, token.Text, this.Binary(level + 1));
        }
      }
    }

    private KtExpression Prefix() {
      if (this.Peek.Kind == Kind.Symbol && this.Peek.Text is "-" or "+" or "!" or "++" or "--")
        return new KtPrefix(this.Next().Text, this.Prefix());

      return this.Postfix(this.Primary());
    }

    private KtExpression Postfix(KtExpression expression) {
      while (true) {
        if (this.Accept(".")) {
          expression = new KtMember(expression, this.Identifier());
        } else if (this.Accept("?.")) {
          expression = new KtMember(expression, this.Identifier()) { Safe = true };
        } else if (this.Is("(")) {
          this.Next();
          expression = this.Trailing(new KtCall(expression, this.Arguments(")")));
        } else if (this.Is("<") && expression is KtName or KtMember && this.TypeArguments() is { } types) {
          this.Expect("(");
          expression = this.Trailing(new KtCall(expression, this.Arguments(")")) { TypeArguments = types });
        } else if (this.Is("{") && expression is KtName or KtMember) {
          expression = new KtCall(expression, []) { Trailing = this.Lambda() };
        } else if (this.Accept("[")) {
          var index = this.Expression();
          this.Expect("]");
          expression = new KtIndex(expression, index);
        } else if (this.Peek.Kind == Kind.Symbol && this.Peek.Text is "!!" or "++" or "--") {
          expression = new KtPostfix(expression, this.Next().Text);
        } else {
          return expression;
        }
      }
    }

    /// <summary>A lambda after the parentheses of a call, where one stands.</summary>
    private KtCall Trailing(KtCall call) => this.Is("{") ? call with { Trailing = this.Lambda() } : call;

    /// <summary>The type arguments of a call, where <c>&lt;</c> opens them and a parenthesis follows their end; otherwise nothing is read.</summary>
    private List<KtType>? TypeArguments() {
      var start = this._at;
      try {
        this.Expect("<");
        var types = new List<KtType>();
        do
          types.Add(this.Type());
        while (this.Accept(","));

        this.Expect(">");
        if (this.Is("("))
          return types;
      } catch (FormatException) {
      }

      this._at = start;
      return null;
    }

    private List<KtExpression> Arguments(string close) {
      var arguments = new List<KtExpression>();
      if (this.Accept(close))
        return arguments;

      do
        arguments.Add(this.Expression());
      while (this.Accept(","));

      this.Expect(close);
      return arguments;
    }

    private KtExpression Primary() {
      var token = this.Peek;
      switch (token.Kind) {
        case Kind.Number or Kind.Character:
          this.Next();
          return new KtLiteral(token.Text);
        case Kind.String:
          this.Next();
          return new KtString(token.Text);
        case Kind.Name when token.Text is "true" or "false" or "null":
          this.Next();
          return new KtLiteral(token.Text);
        case Kind.Name when token.Text == "this":
          this.Next();
          return new KtThis();
        case Kind.Name when token.Text == "if": {
          this.Next();
          var condition = this.Condition();
          var then = this.Expression();
          this.Expect("else");
          return new KtIfExpression(condition, then, this.Expression());
        }
        case Kind.Name when token.Text == "throw":
          this.Next();
          return new KtThrowExpression(this.Expression());
        case Kind.Name:
          this.Next();
          return new KtName(token.Text);
        case Kind.Symbol when token.Text == "(": {
          this.Next();
          var inner = this.Expression();
          this.Expect(")");
          return inner;
        }
        case Kind.Symbol when token.Text == "{":
          return this.Lambda();
        case Kind.Symbol when token.Text == "::":
          this.Next();
          return new KtReference(this.Identifier());
        default:
          throw this.Unexpected("an expression");
      }
    }

    private KtLambda Lambda() {
      this.Expect("{");
      var parameters = new List<KtParameter>();
      var start = this._at;
      // The parameters stand before an arrow; without one the lambda has none.
      if (this.Peek.Kind == Kind.Name) {
        try {
          do {
            var name = this.Identifier();
            parameters.Add(new KtParameter(name, this.Accept(":") ? this.Type() : null));
          } while (this.Accept(","));

          if (!this.Accept("->"))
            throw this.Unexpected("'->'");
        } catch (FormatException) {
          parameters.Clear();
          this._at = start;
        }
      }

      var body = new List<KtStatement>();
      this.Newlines();
      while (!this.Accept("}")) {
        body.Add(this.Statement());
        this.Newlines();
      }

      return new KtLambda(parameters, body);
    }

    #endregion

  }

}
