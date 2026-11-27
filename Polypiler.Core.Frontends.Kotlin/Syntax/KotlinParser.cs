using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.Kotlin.Syntax;

/// <summary>
/// A recursive-descent parser for the Kotlin the frontend imports, after the grammar of the Kotlin
/// language specification (kotlinlang.org/spec, "Syntax and grammar"): its precedence from postfix
/// down to disjunction, and its newline rules - a line break ends a statement unless the statement
/// cannot end there, inside parentheses and brackets it is ignored, and it may stand before
/// <c>.</c>, <c>?.</c>, <c>?:</c>, <c>&amp;&amp;</c>, <c>||</c> and <c>else</c>. Classes,
/// interfaces, objects and type aliases are parsed only to be refused by name.
/// </summary>
public sealed class KotlinParser {

  private readonly IReadOnlyList<KToken> _tokens;
  private int _at;

  /// <summary>How deep the parser is inside parentheses and brackets, where a line break means nothing; a brace starts counting again.</summary>
  private readonly Stack<int> _parentheses = new([0]);

  private KotlinParser(IReadOnlyList<KToken> tokens) => this._tokens = tokens;

  /// <summary>Parses a file.</summary>
  /// <param name="text">The source.</param>
  /// <returns>The tree.</returns>
  /// <exception cref="KotlinSyntaxException">The source is not Kotlin the parser reads.</exception>
  public static KFile Parse(string text) => new KotlinParser(KotlinLexer.Tokens(text)).File();

  /// <summary>Parses one expression, as the inside of <c>${…}</c> of a string template is.</summary>
  /// <param name="text">The expression.</param>
  /// <param name="at">Where it stands in the file.</param>
  /// <returns>The tree.</returns>
  /// <exception cref="KotlinSyntaxException">The text is not one expression.</exception>
  public static KExpression ParseExpression(string text, KPosition at) {
    KotlinParser parser;
    try {
      parser = new KotlinParser(KotlinLexer.Tokens(text));
    } catch (KotlinSyntaxException exception) {
      throw new KotlinSyntaxException(exception.Reason, at);
    }

    parser._parentheses.Push(1);
    var expression = parser.Expression();
    if (parser.Peek.Kind != KTokenKind.End)
      throw new KotlinSyntaxException("a template holds one expression", at);

    return expression;
  }

  private KToken Peek => this._tokens[this._at];

  private KToken PeekAt(int ahead) => this._tokens[Math.Min(this._at + ahead, this._tokens.Count - 1)];

  private KToken Next() => this._tokens[this._at++];

  /// <summary>Whether line breaks are significant where the parser stands: not inside parentheses or brackets.</summary>
  private bool LinesMatter => this._parentheses.Peek() == 0;

  /// <summary>Whether the current token starts a new line where that ends what stands before it.</summary>
  private bool OnNewLine => this.LinesMatter && this.Peek.NewlineBefore;

  private bool Is(string text) => this.Peek.Kind is KTokenKind.Symbol or KTokenKind.Name && this.Peek.Text == text;

  private bool Accept(string text) {
    if (!this.Is(text))
      return false;

    this.Track(this.Next().Text);
    return true;
  }

  /// <summary>Counts an opening or closing parenthesis, bracket or brace the parser consumed.</summary>
  private void Track(string symbol) {
    switch (symbol) {
      case "(" or "[":
        this._parentheses.Push(this._parentheses.Pop() + 1);
        break;
      case ")" or "]":
        this._parentheses.Push(Math.Max(0, this._parentheses.Pop() - 1));
        break;
      case "{":
        this._parentheses.Push(0);
        break;
      case "}" when this._parentheses.Count > 1:
        this._parentheses.Pop();
        break;
    }
  }

  private void Expect(string text) {
    if (!this.Accept(text))
      throw this.Unexpected($"'{text}'");
  }

  private KotlinSyntaxException Unexpected(string wanted)
    => new($"{wanted} expected, found {(this.Peek.Kind == KTokenKind.End ? "the end" : $"'{this.Peek.Text}'")}", this.Peek.At);

  private string Identifier() => this.Peek.Kind == KTokenKind.Name ? this.Next().Text : throw this.Unexpected("a name");

  /// <summary>Skips the semicolons that end a statement.</summary>
  private void Semicolons() {
    while (this.Accept(";")) {
    }
  }

  #region Declarations

  private KFile File() {
    string? package = null;
    var imports = new List<string>();
    this.Annotations();
    if (this.Accept("package"))
      package = this.QualifiedName();

    this.Semicolons();
    while (this.Accept("import")) {
      var name = this.QualifiedName();
      if (this.Accept(".")) {
        this.Expect("*");
        name += ".*";
      }

      if (this.Accept("as"))
        name += " as " + this.Identifier();

      imports.Add(name);
      this.Semicolons();
    }

    var declarations = new List<KDeclaration>();
    while (this.Peek.Kind != KTokenKind.End) {
      declarations.Add(this.Declaration());
      this.Semicolons();
    }

    return new KFile(package, imports, declarations);
  }

  private string QualifiedName() {
    var name = this.Identifier();
    while (this.Is(".") && this.PeekAt(1).Kind == KTokenKind.Name) {
      this.Next();
      name += "." + this.Identifier();
    }

    return name;
  }

  /// <summary>Skips annotations, <c>@Name</c>, <c>@file:Name(…)</c>, which change nothing the frontend imports.</summary>
  private void Annotations() {
    while (this.Is("@")) {
      this.Next();
      this.Identifier();
      if (this.Accept(":"))
        this.QualifiedName();

      while (this.Accept("."))
        this.Identifier();

      if (this.Is("(") && !this.Peek.NewlineBefore)
        this.Skip("(", ")");
    }
  }

  /// <summary>Skips a balanced pair of brackets and everything between.</summary>
  private void Skip(string open, string close) {
    this.Expect(open);
    var depth = 1;
    while (depth > 0) {
      if (this.Peek.Kind == KTokenKind.End)
        throw this.Unexpected($"'{close}'");

      var token = this.Next();
      this.Track(token.Text);
      if (token.Kind == KTokenKind.Symbol && token.Text == open)
        ++depth;
      else if (token.Kind == KTokenKind.Symbol && token.Text == close)
        --depth;
    }
  }

  private static readonly HashSet<string> _modifiers = new(StringComparer.Ordinal) {
    "private", "public", "internal", "protected", "const", "inline", "tailrec", "operator", "infix", "external", "suspend", "data", "open",
    "abstract", "sealed", "final", "override", "lateinit", "annotation", "inner", "value", "enum", "expect", "actual", "noinline", "crossinline"
  };

  private KDeclaration Declaration() {
    this.Annotations();
    var at = this.Peek.At;
    var modifiers = new List<string>();
    while (this.Peek.Kind == KTokenKind.Name && _modifiers.Contains(this.Peek.Text) && this.PeekAt(1).Kind == KTokenKind.Name) {
      modifiers.Add(this.Next().Text);
      this.Annotations();
    }

    KDeclaration declaration;
    switch (this.Peek.Text) {
      case "fun" when this.Peek.Kind == KTokenKind.Name:
        declaration = this.Function();
        break;
      case "val" or "var" when this.Peek.Kind == KTokenKind.Name:
        declaration = this.Property();
        break;
      case "class" or "interface" or "object" or "typealias" when this.Peek.Kind == KTokenKind.Name: {
        var kind = this.Next().Text;
        var name = this.Peek.Kind == KTokenKind.Name ? this.Identifier() : "";
        // The rest of the declaration up to its body, then the body.
        while (this.Peek.Kind != KTokenKind.End && !this.Is("{") && !(this.Peek.NewlineBefore && this.Peek.Kind == KTokenKind.Name && (_modifiers.Contains(this.Peek.Text) || this.Peek.Text is "fun" or "val" or "var" or "class" or "object" or "interface" or "typealias"))) {
          if (this.Is("("))
            this.Skip("(", ")");
          else
            this.Next();
        }

        if (this.Is("{"))
          this.Skip("{", "}");

        declaration = new KUnsupportedDeclaration(modifiers.Contains("enum") ? "enum class" : modifiers.Contains("data") ? "data class" : kind, name);
        break;
      }
      default:
        throw this.Unexpected("a declaration");
    }

    return declaration with { Modifiers = modifiers, At = at };
  }

  private KFunction Function() {
    var at = this.Peek.At;
    this.Expect("fun");
    if (this.Is("<"))
      throw new KotlinSyntaxException("a generic function is outside the imported subset", this.Peek.At);

    var name = this.Identifier();
    if (this.Is("."))
      throw new KotlinSyntaxException($"the extension function '{name}.{this.PeekAt(1).Text}' is outside the imported subset", this.Peek.At);

    this.Expect("(");
    var parameters = new List<KParameter>();
    if (!this.Accept(")")) {
      do {
        if (this.Is(")"))
          break;

        this.Annotations();
        var parameterAt = this.Peek.At;
        if (this.Is("vararg"))
          throw new KotlinSyntaxException("a vararg parameter is outside the imported subset", parameterAt);

        var parameter = this.Identifier();
        this.Expect(":");
        var type = this.Type();
        parameters.Add(new KParameter(parameter, type, this.Accept("=") ? this.Expression() : null) { At = parameterAt });
      } while (this.Accept(","));

      this.Expect(")");
    }

    KTypeRef? result = this.Accept(":") ? this.Type() : null;
    if (this.Accept("="))
      return new KFunction(name, parameters, result, null, this.Expression()) { At = at };

    return new KFunction(name, parameters, result, this.Block(), null) { At = at };
  }

  private KProperty Property() {
    var at = this.Peek.At;
    var mutable = this.Next().Text == "var";
    var name = this.Identifier();
    KTypeRef? type = this.Accept(":") ? this.Type() : null;
    return new KProperty(mutable, name, type, this.Accept("=") ? this.Expression() : null) { At = at };
  }

  private KTypeRef Type() {
    var at = this.Peek.At;
    if (this.Is("("))
      throw new KotlinSyntaxException("a function type is outside the imported subset", at);

    var name = this.QualifiedName();
    var arguments = new List<KTypeRef>();
    if (this.Is("<")) {
      this.Next();
      do {
        if (this.Accept("*"))
          arguments.Add(new KTypeRef("*", [], true) { At = at });
        else
          arguments.Add(this.Type());
      } while (this.Accept(","));

      this.Expect(">");
    }

    var nullable = !this.OnNewLine && this.Accept("?");
    return new KTypeRef(name, arguments, nullable) { At = at };
  }

  #endregion

  #region Statements

  private KBlock Block() {
    var at = this.Peek.At;
    this.Expect("{");
    var statements = new List<KStatement>();
    this.Semicolons();
    while (!this.Accept("}")) {
      if (this.Peek.Kind == KTokenKind.End)
        throw this.Unexpected("'}'");

      statements.Add(this.Statement());
      if (!this.Is("}") && !this.Peek.NewlineBefore && !this.Is(";"))
        throw this.Unexpected("the end of the statement");

      this.Semicolons();
    }

    return new KBlock(statements) { At = at };
  }

  /// <summary>The body of a loop or a branch: a block, or one statement.</summary>
  private KNode Body() => this.Is("{") ? this.Block() : this.Statement();

  private KStatement Statement() {
    this.Annotations();
    var at = this.Peek.At;
    string? label = null;
    if (this.Peek.Kind == KTokenKind.Name && this.PeekAt(1).Text == "@" && !this.PeekAt(1).NewlineBefore && this.PeekAt(2).Text is "while" or "do" or "for") {
      label = this.Next().Text;
      this.Next();
    }

    KStatement statement;
    switch (this.Peek.Kind == KTokenKind.Name ? this.Peek.Text : "") {
      case "val" or "var": {
        var property = this.Property();
        statement = new KVariable(property.Mutable, property.Name, property.Type, property.Initializer);
        break;
      }
      case "fun":
        statement = new KLocalFunction(this.Function());
        break;
      case "while": {
        this.Next();
        var condition = this.Parenthesized();
        statement = new KWhile(label, condition, this.LoopBody());
        break;
      }
      case "do": {
        this.Next();
        var body = this.Is("{") ? this.Block() : (KNode)this.Statement();
        this.Expect("while");
        statement = new KDoWhile(label, body, this.Parenthesized());
        break;
      }
      case "for": {
        this.Next();
        this.Expect("(");
        if (this.Is("("))
          throw new KotlinSyntaxException("a destructuring for loop is outside the imported subset", this.Peek.At);

        var variable = this.Identifier();
        if (this.Accept(":"))
          this.Type();

        this.Expect("in");
        var range = this.Expression();
        this.Expect(")");
        statement = new KFor(label, variable, range, this.LoopBody());
        break;
      }
      default: {
        var expression = this.Expression();
        if (this.Peek.Kind == KTokenKind.Symbol && this.Peek.Text is "=" or "+=" or "-=" or "*=" or "/=" or "%=" && !this.OnNewLine) {
          var op = this.Next().Text;
          statement = new KAssignment(expression, op, this.Expression());
        } else {
          statement = expression is KJumpExpression jump ? jump.Jump : new KExpressionStatement(expression);
        }

        break;
      }
    }

    return statement with { At = at };
  }

  /// <summary>The body of a loop: a block, one statement, or nothing (<c>;</c>).</summary>
  private KNode LoopBody() => this.Is(";") ? new KBlock([]) { At = this.Peek.At } : this.Body();

  private KExpression Parenthesized() {
    this.Expect("(");
    var expression = this.Expression();
    this.Expect(")");
    return expression;
  }

  #endregion

  #region Expressions

  /// <summary>An expression: a disjunction, the lowest level of the grammar.</summary>
  public KExpression Expression() => this.Disjunction();

  private KExpression Disjunction() {
    var left = this.Conjunction();
    while (this.Is("||")) {
      var at = this.Next().At;
      left = new KBinary(left, "||", this.Conjunction()) { At = at };
    }

    return left;
  }

  private KExpression Conjunction() {
    var left = this.Equality();
    while (this.Is("&&")) {
      var at = this.Next().At;
      left = new KBinary(left, "&&", this.Equality()) { At = at };
    }

    return left;
  }

  /// <summary>A level of operators that a line break before the operator ends: equality, comparison and the levels below.</summary>
  private KExpression Level(Func<KExpression> operand, params string[] operators) {
    var left = operand();
    while (this.Peek.Kind == KTokenKind.Symbol && operators.Contains(this.Peek.Text) && !this.OnNewLine) {
      var token = this.Next();
      left = new KBinary(left, token.Text, operand()) { At = token.At };
    }

    return left;
  }

  private KExpression Equality() => this.Level(this.Comparison, "==", "!=", "===", "!==");

  private KExpression Comparison() => this.Level(this.NamedChecks, "<", ">", "<=", ">=");

  private KExpression NamedChecks() {
    var left = this.Elvis();
    while (!this.OnNewLine) {
      var at = this.Peek.At;
      if (this.Is("in")) {
        this.Next();
        left = new KBinary(left, "in", this.Elvis()) { At = at };
      } else if (this.Is("!") && this.PeekAt(1).Text is "in" or "is" && !this.PeekAt(1).NewlineBefore) {
        this.Next();
        left = this.Next().Text == "in" ? new KBinary(left, "!in", this.Elvis()) { At = at } : new KIs(left, this.Type(), true) { At = at };
      } else if (this.Is("is")) {
        this.Next();
        left = new KIs(left, this.Type(), false) { At = at };
      } else {
        return left;
      }
    }

    return left;
  }

  private KExpression Elvis() {
    var left = this.Infix();
    while (this.Is("?:")) {
      var at = this.Next().At;
      left = new KBinary(left, "?:", this.Infix()) { At = at };
    }

    return left;
  }

  /// <summary>The hard keywords of Kotlin and the soft ones that end an expression, none of which names an infix function.</summary>
  private static readonly HashSet<string> _keywords = new(StringComparer.Ordinal) {
    "as", "break", "class", "continue", "do", "else", "false", "for", "fun", "if", "in", "interface", "is", "null", "object", "package",
    "return", "super", "this", "throw", "true", "try", "typealias", "typeof", "val", "var", "when", "while", "catch", "finally"
  };

  /// <summary>A call of an infix function, <c>a shl b</c>: a name between two operands on one line, every name alike and from the left.</summary>
  private KExpression Infix() {
    var left = this.Range();
    while (this.Peek.Kind == KTokenKind.Name && !this.OnNewLine && !_keywords.Contains(this.Peek.Text)
      && this.PeekAt(1).Kind != KTokenKind.End && !(this.PeekAt(1).Kind == KTokenKind.Symbol && this.PeekAt(1).Text is ")" or "]" or "}" or "," or "=" or ";" or "->")) {
      var token = this.Next();
      left = new KBinary(left, token.Text, this.Range()) { At = token.At };
    }

    return left;
  }

  private KExpression Range() => this.Level(this.Additive, "..", "..<");

  private KExpression Additive() => this.Level(this.Multiplicative, "+", "-");

  private KExpression Multiplicative() => this.Level(this.TypeOperator, "*", "/", "%");

  private KExpression TypeOperator() {
    var left = this.Prefix();
    while (this.Is("as") && !this.OnNewLine) {
      var at = this.Next().At;
      var safe = this.Accept("?");
      left = new KAs(left, this.Type(), safe) { At = at };
    }

    return left;
  }

  private KExpression Prefix() {
    var token = this.Peek;
    if (token.Kind == KTokenKind.Symbol && token.Text is "-" or "+" or "!" or "++" or "--") {
      this.Next();
      return new KPrefix(token.Text, this.Prefix()) { At = token.At };
    }

    // A label of a lambda, label@ { … }, changes nothing the frontend imports.
    if (token.Kind == KTokenKind.Name && this.PeekAt(1).Text == "@" && this.PeekAt(2).Text == "{") {
      this.Next();
      this.Next();
    }

    return this.Postfix(this.Primary());
  }

  private KExpression Postfix(KExpression expression) {
    while (true) {
      var token = this.Peek;
      if (token.Kind == KTokenKind.Symbol && token.Text is "." or "?.") {
        this.Next();
        expression = new KMember(expression, this.Identifier(), token.Text == "?.") { At = token.At };
      } else if (this.OnNewLine) {
        return expression;
      } else if (this.Is("(")) {
        this.Next();
        this.Track("(");
        var arguments = this.Arguments();
        expression = new KCall(expression, arguments, this.Is("{") ? (KLambda)this.Lambda() : null) { At = token.At };
      } else if (this.Is("<") && expression is KName or KMember && this.TypeArguments()) {
        // The type arguments of a call: the frontend infers them from the arguments and the result.
        continue;
      } else if (this.Is("{") && expression is KName or KMember) {
        expression = new KCall(expression, [], (KLambda)this.Lambda()) { At = token.At };
      } else if (this.Is("[")) {
        this.Next();
        this.Track("[");
        var index = this.Expression();
        this.Expect("]");
        expression = new KIndex(expression, index) { At = token.At };
      } else if (token.Kind == KTokenKind.Symbol && token.Text is "++" or "--" or "!!") {
        this.Next();
        expression = new KPostfix(expression, token.Text) { At = token.At };
      } else {
        return expression;
      }
    }
  }

  /// <summary>Reads type arguments when a parenthesis follows them, which makes them those of a call; otherwise nothing is read.</summary>
  private bool TypeArguments() {
    var start = this._at;
    try {
      this.Next();
      do
        this.Type();
      while (this.Accept(","));

      if (this.Accept(">") && this.Is("("))
        return true;
    } catch (KotlinSyntaxException) {
    }

    this._at = start;
    return false;
  }

  /// <summary>The arguments of a call after its opening parenthesis, the closing one read.</summary>
  private List<KExpression> Arguments() {
    var arguments = new List<KExpression>();
    while (!this.Accept(")")) {
      if (this.Peek.Kind == KTokenKind.Name && this.PeekAt(1).Text == "=" && !this.Is("this"))
        throw new KotlinSyntaxException($"the named argument '{this.Peek.Text}' is outside the imported subset", this.Peek.At);

      if (this.Is("*"))
        throw new KotlinSyntaxException("a spread argument is outside the imported subset", this.Peek.At);

      arguments.Add(this.Expression());
      if (!this.Accept(",") && !this.Is(")"))
        throw this.Unexpected("',' or ')'");
    }

    return arguments;
  }

  private KExpression Primary() {
    var token = this.Peek;
    var at = token.At;
    switch (token.Kind) {
      case KTokenKind.Integer: {
        this.Next();
        var value = KotlinLexer.Integer(token.Text) ?? throw new KotlinSyntaxException($"the literal {token.Text} does not fit 64 bits", at);
        return new KInteger(value.Value, value.Unsigned, value.Long) { At = at };
      }
      case KTokenKind.Float: {
        this.Next();
        var (value, single) = KotlinLexer.Floating(token.Text);
        return new KFloat(value, single) { At = at };
      }
      case KTokenKind.Character:
        this.Next();
        return new KChar(token.Text[0]) { At = at };
      case KTokenKind.String:
        this.Next();
        return this.Template(token.Text, at, raw: false);
      case KTokenKind.RawString:
        this.Next();
        return this.Template(token.Text, at, raw: true);
      case KTokenKind.Symbol when token.Text == "(": {
        this.Next();
        this.Track("(");
        var inner = this.Expression();
        this.Expect(")");
        return inner;
      }
      case KTokenKind.Symbol when token.Text == "{":
        return this.Lambda();
      case KTokenKind.Name:
        return this.Keyword(token, at);
      default:
        throw this.Unexpected("an expression");
    }
  }

  private KExpression Keyword(KToken token, KPosition at) {
    switch (token.Text) {
      case "true" or "false":
        this.Next();
        return new KBoolean(token.Text == "true") { At = at };
      case "null":
        this.Next();
        return new KNull { At = at };
      case "if": {
        this.Next();
        var condition = this.Parenthesized();
        var then = this.Body();
        // A semicolon may stand before the else; without an else it ends the statement.
        var end = this._at;
        this.Semicolons();
        KNode? otherwise = this.Accept("else") ? this.Body() : null;
        if (otherwise is null)
          this._at = end;
        return new KIf(condition, then, otherwise) { At = at };
      }
      case "when":
        return this.When();
      case "throw":
        this.Next();
        return new KThrowExpression(this.Expression()) { At = at };
      case "return": {
        this.Next();
        KExpression? value = this.OnNewLine || this.Is("}") || this.Is(";") || this.Is(")") || this.Peek.Kind == KTokenKind.End ? null : this.Expression();
        return new KJumpExpression(new KReturn(value) { At = at }) { At = at };
      }
      case "break" or "continue": {
        this.Next();
        string? label = null;
        if (this.Is("@") && !this.Peek.NewlineBefore) {
          this.Next();
          label = this.Identifier();
        }

        KStatement jump = token.Text == "break" ? new KBreak(label) { At = at } : new KContinue(label) { At = at };
        return new KJumpExpression(jump) { At = at };
      }
      case "object" or "super" or "try":
        throw new KotlinSyntaxException($"'{token.Text}' is outside the imported subset", at);
      default:
        this.Next();
        return new KName(token.Text) { At = at };
    }
  }

  private KWhen When() {
    var at = this.Next().At;
    KExpression? subject = null;
    if (this.Is("(")) {
      this.Next();
      this.Track("(");
      if (this.Is("val"))
        throw new KotlinSyntaxException("a when subject that declares a variable is outside the imported subset", this.Peek.At);

      subject = this.Expression();
      this.Expect(")");
    }

    this.Expect("{");
    var entries = new List<KWhenEntry>();
    this.Semicolons();
    while (!this.Accept("}")) {
      var entryAt = this.Peek.At;
      var conditions = new List<KExpression>();
      if (!this.Accept("else")) {
        do {
          if (this.Is("->"))
            break;

          var conditionAt = this.Peek.At;
          if (subject is not null && this.Is("in"))
            conditions.Add(new KWhenRange(this.Skipped(this.Expression), false) { At = conditionAt });
          else if (subject is not null && this.Is("!") && this.PeekAt(1).Text == "in")
            conditions.Add(new KWhenRange(this.Skipped(() => this.Skipped(this.Expression)), true) { At = conditionAt });
          else if (subject is not null && (this.Is("is") || this.Is("!") && this.PeekAt(1).Text == "is"))
            throw new KotlinSyntaxException("a type test in when is outside the imported subset", conditionAt);
          else
            conditions.Add(this.Expression());
        } while (this.Accept(","));
      }

      this.Expect("->");
      entries.Add(new KWhenEntry(conditions, this.Body()) { At = entryAt });
      this.Semicolons();
    }

    return new KWhen(subject, entries) { At = at };
  }

  /// <summary>Skips one token, then reads what follows.</summary>
  private T Skipped<T>(Func<T> read) {
    this.Next();
    return read();
  }

  private KExpression Lambda() {
    var at = this.Peek.At;
    this.Expect("{");
    var parameters = new List<KParameter>();
    var start = this._at;
    var depth = this._parentheses.Count;
    if (this.Peek.Kind == KTokenKind.Name) {
      try {
        do {
          var parameterAt = this.Peek.At;
          var name = this.Identifier();
          parameters.Add(new KParameter(name, this.Accept(":") ? this.Type() : null, null) { At = parameterAt });
        } while (this.Accept(","));

        if (!this.Accept("->"))
          throw this.Unexpected("'->'");
      } catch (KotlinSyntaxException) {
        parameters.Clear();
        this._at = start;
        while (this._parentheses.Count > depth)
          this._parentheses.Pop();
      }
    }

    var body = new List<KStatement>();
    this.Semicolons();
    while (!this.Accept("}")) {
      if (this.Peek.Kind == KTokenKind.End)
        throw this.Unexpected("'}'");

      body.Add(this.Statement());
      this.Semicolons();
    }

    return new KLambda(parameters, body) { At = at };
  }

  /// <summary>
  /// A string literal: its literal parts with their escapes read, and its templates - <c>$name</c>,
  /// <c>${expression}</c> - parsed; a raw string has no escapes.
  /// </summary>
  private KExpression Template(string text, KPosition at, bool raw) {
    var parts = new List<KExpression>();
    var literal = new System.Text.StringBuilder();
    for (var index = 0; index < text.Length; ++index) {
      var character = text[index];
      if (!raw && character == '\\') {
        var (unit, length) = KotlinLexer.Escape(text, index, at);
        literal.Append(unit);
        index += length - 1;
      } else if (character == '$' && index + 1 < text.Length && (text[index + 1] == '{' || char.IsLetter(text[index + 1]) || text[index + 1] == '_')) {
        if (literal.Length > 0)
          parts.Add(new KString(literal.ToString()) { At = at });

        literal.Clear();
        if (text[index + 1] == '{') {
          var end = Close(text, index + 1, at);
          parts.Add(ParseExpression(text[(index + 2)..end], at));
          index = end;
        } else {
          var end = index + 1;
          while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
            ++end;

          parts.Add(new KName(text[(index + 1)..end]) { At = at });
          index = end - 1;
        }
      } else {
        literal.Append(character);
      }
    }

    if (parts.Count == 0)
      return new KString(literal.ToString()) { At = at };

    if (literal.Length > 0)
      parts.Add(new KString(literal.ToString()) { At = at });

    return new KTemplate(parts) { At = at };
  }

  /// <summary>The index of the brace that closes the one at <paramref name="open"/>.</summary>
  private static int Close(string text, int open, KPosition at) {
    var depth = 0;
    for (var index = open; index < text.Length; ++index) {
      if (text[index] == '{')
        ++depth;
      else if (text[index] == '}' && --depth == 0)
        return index;
    }

    throw new KotlinSyntaxException("an unterminated template", at);
  }

  #endregion

}
