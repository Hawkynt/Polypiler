using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Frontends.Rust.Syntax;

/// <summary>
/// A recursive-descent parser for the subset of Rust the frontend imports, following the grammar of
/// The Rust Reference ("Items", "Statements", "Expressions", "Patterns"): free functions and
/// constants, statements, the operators with their precedence, blocks and the control-flow
/// expressions, and patterns. A construct of the language outside the subset is recognised as far as
/// is needed to skip it and becomes a <see cref="Refusal"/> naming it; text that is no Rust at all is
/// a <see cref="RustSyntaxException"/>.
/// </summary>
internal sealed class Parser {

  /// <summary>The attributes a function may carry: they change nothing the import observes.</summary>
  private static readonly HashSet<string> _accepted = ["inline", "must_use", "allow", "warn", "deny", "forbid", "expect", "doc", "cold", "track_caller"];

  /// <summary>The keywords of the items the frontend refuses.</summary>
  private static readonly HashSet<string> _refusedItems = ["use", "mod", "struct", "enum", "impl", "trait", "type", "union", "static", "extern", "macro_rules"];

  private readonly List<Token> _tokens;
  private int _position;

  private Parser(List<Token> tokens) => this._tokens = tokens;

  /// <summary>Parses a source file.</summary>
  /// <exception cref="RustSyntaxException">The text is not valid Rust.</exception>
  public static SourceFile Parse(string source) => new Parser(Lexer.Tokens(source)).File();

  private Token Current => this._tokens[this._position];

  private Token PeekAt(int offset) => this._tokens[System.Math.Min(this._position + offset, this._tokens.Count - 1)];

  private Token Next() => this._tokens[this._position++ < this._tokens.Count - 1 ? this._position - 1 : this._tokens.Count - 1];

  private bool At(string text) => this.Current.Is(text);

  private bool Accept(string text) {
    if (!this.At(text))
      return false;

    ++this._position;
    return true;
  }

  private Token Expect(string text) => this.At(text) ? this.Next() : throw this.Error($"expected '{text}', found {this.Current}");

  private string Identifier() {
    if (this.Current.Kind != TokenKind.Identifier)
      throw this.Error($"expected identifier, found {this.Current}");

    return this.Next().Text;
  }

  private RustSyntaxException Error(string message) => new(message, this.Current.At);

  // ---------------------------------------------------------------- items

  private SourceFile File() {
    var items = new List<Item>();
    // Inner attributes of the crate: the lint levels are accepted, anything else refused.
    while (this.At("#") && this.PeekAt(1).Is("!") && this.PeekAt(2).Is("[")) {
      var at = this.Current.At;
      this.Next();
      this.Next();
      var name = this.AttributeBody();
      if (!_accepted.Contains(name))
        items.Add(new RefusedItem(new Refusal(RustFrontend.UnsupportedAttribute, name, at)));
    }

    while (this.Current.Kind != TokenKind.End)
      items.Add(this.Item());

    return new SourceFile(items);
  }

  /// <summary>The name of an attribute whose <c>[</c> is next; the rest of it is skipped.</summary>
  private string AttributeBody() {
    this.Expect("[");
    var name = this.Current.Kind == TokenKind.Identifier ? this.Current.Text : "?";
    var depth = 1;
    while (depth > 0) {
      var token = this.Next();
      if (token.Kind == TokenKind.End)
        throw new RustSyntaxException("unterminated attribute", token.At);

      depth += token.Is("[") ? 1 : token.Is("]") ? -1 : 0;
    }

    return name;
  }

  private Item Item() {
    var at = this.Current.At;
    Refusal? refused = null;
    while (this.At("#")) {
      var attributeAt = this.Current.At;
      this.Next();
      var name = this.AttributeBody();
      if (!_accepted.Contains(name))
        refused ??= new Refusal(RustFrontend.UnsupportedAttribute, name, attributeAt);
    }

    var isPublic = false;
    if (this.Accept("pub")) {
      isPublic = true;
      if (this.At("(")) {
        isPublic = false;
        this.SkipTree();
      }
    }

    var item = this.ItemAfterVisibility(at, isPublic);
    return refused is null || item is RefusedItem ? item : new RefusedItem(refused);
  }

  private Item ItemAfterVisibility(Span at, bool isPublic) {
    var qualifiers = new List<string>();
    while (this.Current.Kind == TokenKind.Identifier && this.Current.Text is "const" or "unsafe" or "async" or "extern" && this.IsFunctionAhead()) {
      qualifiers.Add(this.Next().Text);
      if (qualifiers[^1] == "extern" && this.Current.Kind == TokenKind.String)
        this.Next();
    }

    if (this.At("fn"))
      return this.Function(at, isPublic, qualifiers);

    if (this.At("const")) {
      this.Next();
      var name = this.Identifier();
      this.Expect(":");
      var type = this.Type();
      this.Expect("=");
      var value = this.Expression();
      this.Expect(";");
      return new ConstItem(at, name, type, value);
    }

    if (this.Current is { Kind: TokenKind.Identifier, Raw: false } && _refusedItems.Contains(this.Current.Text)) {
      var keyword = this.Current.Text;
      this.SkipItem();
      return new RefusedItem(new Refusal(RustFrontend.Unsupported, $"{keyword} item", at));
    }

    throw this.Error($"expected item, found {this.Current}");
  }

  /// <summary>Whether a qualifier at the current token belongs to a function: <c>fn</c> follows the qualifiers.</summary>
  private bool IsFunctionAhead() {
    for (var offset = 0; offset < 4; ++offset) {
      var token = this.PeekAt(offset);
      if (token.Is("fn"))
        return true;

      if (!(token.Kind == TokenKind.String || token.Text is "const" or "unsafe" or "async" or "extern"))
        return false;
    }

    return false;
  }

  /// <summary>Skips an item: up to its <c>;</c> or through its braced body.</summary>
  private void SkipItem() {
    while (this.Current.Kind != TokenKind.End) {
      if (this.Accept(";"))
        return;

      if (this.At("{")) {
        this.SkipTree();
        return;
      }

      this.SkipTree();
    }
  }

  /// <summary>Skips one token, or a delimited group of them with everything inside.</summary>
  private void SkipTree() {
    var open = this.Next();
    if (open.Kind == TokenKind.End)
      throw new RustSyntaxException("unexpected end of file", open.At);

    var close = open.Text switch { "(" => ")", "[" => "]", "{" => "}", _ => null };
    if (open.Kind != TokenKind.Punctuation || close is null)
      return;

    while (!this.At(close)) {
      if (this.Current.Kind == TokenKind.End)
        throw new RustSyntaxException($"unclosed delimiter '{open.Text}'", open.At);

      this.SkipTree();
    }

    this.Next();
  }

  private Item Function(Span at, bool isPublic, List<string> qualifiers) {
    this.Expect("fn");
    var name = this.Identifier();
    Refusal? refused = null;
    if (qualifiers.Contains("async"))
      refused = new Refusal(RustFrontend.Coroutine, "async fn", at);
    else if (qualifiers.FirstOrDefault(qualifier => qualifier is "unsafe" or "extern") is { } qualifier)
      refused = new Refusal(RustFrontend.UnsupportedFunction, $"{qualifier} fn", at);

    if (this.At("<")) {
      refused ??= new Refusal(RustFrontend.UnsupportedFunction, "generic fn", this.Current.At);
      this.SkipAngles();
    }

    this.Expect("(");
    var parameters = new List<Parameter>();
    while (!this.At(")")) {
      var parameterAt = this.Current.At;
      var mutable = this.Accept("mut");
      if (this.Current.Kind == TokenKind.Identifier && this.PeekAt(1).Is(":") && this.Current.Text != "self") {
        var parameterName = this.Next().Text;
        this.Expect(":");
        parameters.Add(new Parameter(parameterName, mutable, this.Type(), parameterAt));
      } else {
        refused ??= new Refusal(RustFrontend.UnsupportedFunction, "parameter pattern", parameterAt);
        while (!this.At(",") && !this.At(")"))
          this.SkipTree();
      }

      if (!this.Accept(","))
        break;
    }

    this.Expect(")");
    TypeRef? returnType = null;
    if (this.Accept("->"))
      returnType = this.Type();

    if (this.At("where")) {
      refused ??= new Refusal(RustFrontend.UnsupportedFunction, "where clause", this.Current.At);
      while (!this.At("{") && this.Current.Kind != TokenKind.End)
        this.SkipTree();
    }

    var body = this.Block();
    return refused is null ? new FunctionItem(at, name, isPublic, parameters, returnType, body) : new RefusedItem(refused);
  }

  private void SkipAngles() {
    var depth = 0;
    do {
      var token = this.Next();
      if (token.Kind == TokenKind.End)
        throw new RustSyntaxException("unclosed '<'", token.At);

      depth += token.Kind != TokenKind.Punctuation ? 0 : token.Text switch { "<" => 1, ">" => -1, ">>" => -2, _ => 0 };
    } while (depth > 0);
  }

  /// <summary>A type, as the text of its tokens: a primitive type is its name, unit <c>()</c> and never <c>!</c>.</summary>
  private TypeRef Type() {
    var at = this.Current.At;
    return new TypeRef(this.TypeText(), at);
  }

  private string TypeText() {
    var token = this.Current;
    if (this.Accept("(")) {
      var elements = new List<string>();
      while (!this.At(")")) {
        elements.Add(this.TypeText());
        if (!this.Accept(","))
          break;
      }

      this.Expect(")");
      return "(" + string.Join(", ", elements) + ")";
    }

    if (this.Accept("[")) {
      var text = "[" + this.TypeText();
      if (this.Accept(";")) {
        while (!this.At("]")) {
          if (this.Current.Kind == TokenKind.End)
            throw this.Error("unclosed '['");

          this.SkipTree();
        }

        text += "; _";
      }

      this.Expect("]");
      return text + "]";
    }

    if (this.Accept("!"))
      return "!";

    if (token.Is("&") || token.Is("&&") || token.Is("*")) {
      this.Next();
      if (this.Current.Kind == TokenKind.Lifetime)
        this.Next();

      var qualifier = this.Accept("mut") ? "mut " : this.Accept("const") ? "const " : "";
      return token.Text + qualifier + this.TypeText();
    }

    if (token.Is("fn") || token.Is("unsafe") || token.Is("extern")) {
      while (!this.At("(")) {
        if (this.Current.Kind == TokenKind.End)
          throw this.Error("expected '('");

        this.Next();
      }

      this.SkipTree();
      if (this.Accept("->"))
        this.TypeText();

      return "fn(...)";
    }

    if (token.Is("dyn") || token.Is("impl")) {
      this.Next();
      var bounds = new List<string> { this.TypeText() };
      while (this.Accept("+"))
        bounds.Add(this.TypeText());

      return token.Text + " " + string.Join(" + ", bounds);
    }

    var path = new StringBuilder(this.Identifier());
    while (true) {
      if (this.At("<")) {
        var start = this._position;
        this.SkipAngles();
        path.Append(string.Concat(this._tokens.Skip(start).Take(this._position - start).Select(skipped => skipped.Text)));
        continue;
      }

      // The parenthesised form of the closure traits: Fn(u32) -> u32.
      if (this.At("(")) {
        this.SkipTree();
        if (this.Accept("->"))
          this.TypeText();

        return path.Append("(...)").ToString();
      }

      if (!this.Accept("::"))
        return path.ToString();

      path.Append("::").Append(this.Identifier());
    }
  }

  // ---------------------------------------------------------------- statements and blocks

  private Block Block() {
    var at = this.Expect("{").At;
    var statements = new List<Statement>();
    Expr? tail = null;
    while (!this.At("}")) {
      if (this.Current.Kind == TokenKind.End)
        throw this.Error("this file contains an unclosed delimiter");

      if (this.Accept(";"))
        continue;

      var statementAt = this.Current.At;
      // Attributes of a statement: the accepted ones change nothing, any other refuses the statement.
      if (this.At("#") && !this.PeekAt(1).Is("!")) {
        var attributeAt = this.Current.At;
        this.Next();
        var name = this.AttributeBody();
        if (!_accepted.Contains(name))
          statements.Add(new RefusedStatement(new Refusal(RustFrontend.UnsupportedAttribute, name, attributeAt)));

        continue;
      }

      if (this.At("let")) {
        statements.Add(this.Let());
        continue;
      }

      if (this.At("const") && this.PeekAt(1).Kind == TokenKind.Identifier && this.PeekAt(2).Is(":")) {
        this.Next();
        var name = this.Identifier();
        this.Expect(":");
        var type = this.Type();
        this.Expect("=");
        var value = this.Expression();
        this.Expect(";");
        statements.Add(new ConstStatement(new ConstItem(statementAt, name, type, value)));
        continue;
      }

      if (this.At("fn") || this.At("pub") || (this.Current is { Kind: TokenKind.Identifier, Raw: false } && _refusedItems.Contains(this.Current.Text) && !this.PeekAt(1).Is("!") && !this.PeekAt(1).Is("::"))) {
        var item = this.Item();
        statements.Add(new RefusedStatement(item is RefusedItem refusedItem ? refusedItem.Refusal : new Refusal(RustFrontend.UnsupportedFunction, "nested item", statementAt)));
        continue;
      }

      // A block-like expression ends the statement: `if c { a } else { b } - 1` is two of them.
      var blockLike = this.IsBlockLike();
      var expression = blockLike ? this.Primary() : this.Expression();
      if (blockLike && (this.At(".") || this.At("?"))) {
        expression = this.Postfix(expression);
        blockLike = false;
      }

      if (this.At("}")) {
        tail = expression;
        break;
      }

      if (!this.Accept(";") && !blockLike)
        throw this.Error($"expected ';', found {this.Current}");

      statements.Add(new ExpressionStatement(statementAt, expression));
    }

    this.Expect("}");
    return new Block(at, statements, tail);
  }

  /// <summary>Whether the expression at the current token is block-like, which ends a statement without a semicolon.</summary>
  private bool IsBlockLike() {
    var token = this.Current;
    if (token.Is("{") || (token.Kind == TokenKind.Lifetime && this.PeekAt(1).Is(":")))
      return true;

    return token is { Kind: TokenKind.Identifier, Raw: false } && token.Text is "if" or "match" or "loop" or "while" or "for" or "unsafe";
  }

  private Statement Let() {
    var at = this.Expect("let").At;
    var pattern = this.PatternNoTopAlternatives();
    TypeRef? type = null;
    if (this.Accept(":"))
      type = this.Type();

    Expr? initializer = null;
    if (this.Accept("="))
      initializer = this.Expression();

    if (this.At("else")) {
      var elseAt = this.Current.At;
      this.Next();
      this.Block();
      this.Expect(";");
      return new RefusedStatement(new Refusal(RustFrontend.ConditionalBinding, "let ... else", elseAt));
    }

    this.Expect(";");
    return new LetStatement(at, pattern, type, initializer);
  }

  // ---------------------------------------------------------------- expressions

  /// <summary>An expression, assignment included.</summary>
  private Expr Expression() => this.Assignment();

  private static readonly string[] _assignments = ["=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>="];

  private Expr Assignment() {
    var left = this.Range();
    var op = this.Current;
    if (op.Kind == TokenKind.Punctuation && _assignments.Contains(op.Text)) {
      this.Next();
      var right = this.Assignment();
      return new AssignExpr(op.At, op.Text == "=" ? "=" : op.Text[..^1], left, right);
    }

    return left;
  }

  private Expr Range() {
    var at = this.Current.At;
    if (this.At("..") || this.At("..=")) {
      var inclusive = this.Next().Text == "..=";
      return new RangeExpr(at, null, this.StartsExpression() ? this.Binary(0) : null, inclusive);
    }

    var left = this.Binary(0);
    if (this.At("..") || this.At("..=")) {
      var inclusive = this.Next().Text == "..=";
      return new RangeExpr(left.At, left, this.StartsExpression() ? this.Binary(0) : null, inclusive);
    }

    return left;
  }

  private bool StartsExpression() {
    var token = this.Current;
    return token.Kind switch {
      TokenKind.Punctuation => token.Text is "(" or "-" or "!" or "*" or "&" or "&&" or "[" or "|" or "||",
      TokenKind.Identifier => token.Text is not ("as" or "else"),
      TokenKind.End => false,
      _ => true
    };
  }

  /// <summary>The binary operators by precedence, loosest first.</summary>
  private static readonly string[][] _levels = [
    ["||"], ["&&"], ["==", "!=", "<", ">", "<=", ">="], ["|"], ["^"], ["&"], ["<<", ">>"], ["+", "-"], ["*", "/", "%"]
  ];

  private Expr Binary(int level) {
    if (level == _levels.Length)
      return this.Cast();

    var left = this.Binary(level + 1);
    while (this.Current.Kind == TokenKind.Punctuation && _levels[level].Contains(this.Current.Text)) {
      var op = this.Next();
      var right = this.Binary(level + 1);
      left = new BinaryExpr(op.At, op.Text, left, right);
      // Comparisons do not associate: `a < b < c` needs parentheses.
      if (level == 2 && this.Current.Kind == TokenKind.Punctuation && _levels[2].Contains(this.Current.Text))
        throw this.Error("comparison operators cannot be chained");
    }

    return left;
  }

  private Expr Cast() {
    var operand = this.Unary();
    while (this.At("as")) {
      var at = this.Next().At;
      operand = new CastExpr(at, operand, this.Type());
    }

    return operand;
  }

  private Expr Unary() {
    var token = this.Current;
    if (token.Is("-") || token.Is("!")) {
      this.Next();
      return new UnaryExpr(token.At, token.Text, this.Unary());
    }

    if (token.Is("*") || token.Is("&") || token.Is("&&")) {
      this.Next();
      this.Accept("mut");
      this.Unary();
      return new RefusedExpr(new Refusal(RustFrontend.Unsupported, token.Is("*") ? "dereference" : "reference", token.At));
    }

    return this.Postfix(this.Primary());
  }

  private Expr Postfix(Expr expression) {
    while (true) {
      var token = this.Current;
      if (token.Is(".")) {
        this.Next();
        var name = this.Current;
        if (name.Kind == TokenKind.Integer) {
          this.Next();
          expression = new FieldExpr(name.At, expression, name.Text);
          continue;
        }

        var member = this.Identifier();
        if (member == "await") {
          expression = new RefusedExpr(new Refusal(RustFrontend.Coroutine, ".await", name.At));
          continue;
        }

        if (this.At("::")) {
          this.Next();
          this.SkipAngles();
          expression = new RefusedExpr(new Refusal(RustFrontend.Unsupported, "turbofish", name.At));
        }

        if (this.At("(")) {
          expression = new MethodCallExpr(name.At, expression, member, this.Arguments());
          continue;
        }

        expression = new FieldExpr(name.At, expression, member);
        continue;
      }

      if (token.Is("(")) {
        expression = new CallExpr(token.At, expression, this.Arguments());
        continue;
      }

      if (token.Is("[")) {
        this.SkipTree();
        expression = new RefusedExpr(new Refusal(RustFrontend.Unsupported, "index expression", token.At));
        continue;
      }

      if (token.Is("?")) {
        this.Next();
        expression = new RefusedExpr(new Refusal(RustFrontend.Unsupported, "? operator", token.At));
        continue;
      }

      return expression;
    }
  }

  private List<Expr> Arguments() {
    this.Expect("(");
    var arguments = new List<Expr>();
    while (!this.At(")")) {
      arguments.Add(this.Expression());
      if (!this.Accept(","))
        break;
    }

    this.Expect(")");
    return arguments;
  }

  private Expr Primary() {
    var token = this.Current;
    switch (token.Kind) {
      case TokenKind.Integer:
        this.Next();
        return new IntegerLiteral(token.At, token.Value, token.Suffix);
      case TokenKind.Float:
        this.Next();
        return new FloatLiteral(token.At, token.Text, token.Suffix);
      case TokenKind.String:
        this.Next();
        return new StringLiteral(token.At, token.Text);
      case TokenKind.Character:
        this.Next();
        return new RefusedExpr(new Refusal(RustFrontend.UnsupportedType, "char", token.At));
      case TokenKind.Lifetime:
        return this.Labelled();
      case TokenKind.Punctuation:
        return this.Punctuated(token);
      case TokenKind.End:
        throw this.Error("expected expression, found end of file");
    }

    return token.Raw ? this.Path() : token.Text switch {
      "true" or "false" => this.Literal(token),
      "if" => this.If(),
      "match" => this.Match(),
      "loop" => this.Loop(null, token.At),
      "while" => this.While(null, token.At),
      "for" => this.For(null, token.At),
      "return" => this.Return(),
      "break" => this.Break(),
      "continue" => this.Continue(),
      "unsafe" => this.Skipped(RustFrontend.Unsupported, "unsafe block"),
      "async" or "gen" when this.PeekAt(1).Is("{") || this.PeekAt(1).Is("move") => this.Skipped(RustFrontend.Coroutine, token.Text + " block"),
      "try" when this.PeekAt(1).Is("{") => this.Skipped(RustFrontend.Coroutine, "try block"),
      "yield" => this.Yield(),
      "move" => this.Closure(),
      "let" => throw this.Error("expected expression, found `let` statement"),
      _ => this.Path()
    };
  }

  private Expr Literal(Token token) {
    this.Next();
    return new BoolLiteral(token.At, token.Text == "true");
  }

  /// <summary>A keyword followed by a block, which is skipped and refused.</summary>
  private RefusedExpr Skipped(string code, string construct) {
    var at = this.Next().At;
    this.Accept("move");
    this.SkipTree();
    return new RefusedExpr(new Refusal(code, construct, at));
  }

  private RefusedExpr Yield() {
    var at = this.Next().At;
    if (this.StartsExpression() && !this.At("}") && !this.At(";"))
      this.Expression();

    return new RefusedExpr(new Refusal(RustFrontend.Coroutine, "yield", at));
  }

  private Expr Punctuated(Token token) {
    switch (token.Text) {
      case "(": {
        this.Next();
        if (this.Accept(")"))
          return new RefusedExpr(new Refusal(RustFrontend.Unsupported, "unit value '()'", token.At));

        var first = this.Expression();
        if (this.Accept(")"))
          return first;

        var elements = new List<Expr> { first };
        while (this.Accept(",")) {
          if (this.At(")"))
            break;

          elements.Add(this.Expression());
        }

        this.Expect(")");
        return new TupleExpr(token.At, elements);
      }
      case "{":
        return new BlockExpr(token.At, this.Block(), null);
      case "[":
        this.SkipTree();
        return new RefusedExpr(new Refusal(RustFrontend.Unsupported, "array expression", token.At));
      case "|" or "||":
        return this.Closure();
      default:
        throw this.Error($"expected expression, found {token}");
    }
  }

  private RefusedExpr Closure() {
    var at = this.Current.At;
    this.Accept("move");
    if (this.Accept("|")) {
      while (!this.At("|")) {
        if (this.Current.Kind == TokenKind.End)
          throw this.Error("unterminated closure parameters");

        this.SkipTree();
      }

      this.Next();
    } else {
      this.Expect("||");
    }

    if (this.Accept("->")) {
      this.Type();
      this.Block();
    } else {
      this.Expression();
    }

    return new RefusedExpr(new Refusal(RustFrontend.Unsupported, "closure", at));
  }

  private Expr Path() {
    var at = this.Current.At;
    var segments = new List<string> { this.Identifier() };
    var generic = false;
    while (this.At("::")) {
      this.Next();
      if (this.At("<")) {
        this.SkipAngles();
        generic = true;
        continue;
      }

      segments.Add(this.Identifier());
    }

    if (generic)
      return new RefusedExpr(new Refusal(RustFrontend.Unsupported, "generic path " + string.Join("::", segments), at));

    if (this.At("!") && !this.PeekAt(1).Is("=")) {
      this.Next();
      if (this.At("(") || this.At("[") || this.At("{"))
        this.SkipTree();

      return new RefusedExpr(new Refusal(RustFrontend.UnsupportedMacro, string.Join("::", segments) + "!", at));
    }

    return new PathExpr(at, segments);
  }

  private Expr Labelled() {
    var label = this.Next();
    this.Expect(":");
    var token = this.Current;
    return token.Text switch {
      "loop" => this.Loop(label.Text, label.At),
      "while" => this.While(label.Text, label.At),
      "for" => this.For(label.Text, label.At),
      "{" => new BlockExpr(label.At, this.Block(), label.Text),
      _ => throw this.Error($"expected a loop or a block after the label '{label.Text}")
    };
  }

  private Expr If() {
    var at = this.Expect("if").At;
    if (this.At("let")) {
      var letAt = this.Current.At;
      this.SkipCondition();
      this.Block();
      if (this.Accept("else")) {
        if (this.At("if"))
          this.If();
        else
          this.Block();
      }

      return new RefusedExpr(new Refusal(RustFrontend.ConditionalBinding, "if let", letAt));
    }

    var condition = this.Expression();
    var then = this.Block();
    Expr? otherwise = null;
    if (this.Accept("else")) {
      var elseAt = this.Current.At;
      otherwise = this.At("if") ? this.If() : new BlockExpr(elseAt, this.Block(), null);
    }

    return new IfExpr(at, condition, then, otherwise);
  }

  /// <summary>Skips the condition of an <c>if let</c> or a <c>while let</c>, up to the block it guards.</summary>
  private void SkipCondition() {
    while (!this.At("{")) {
      if (this.Current.Kind == TokenKind.End)
        throw this.Error("expected '{'");

      this.SkipTree();
    }
  }

  private Expr Match() {
    var at = this.Expect("match").At;
    var scrutinee = this.Expression();
    this.Expect("{");
    var arms = new List<MatchArm>();
    while (!this.At("}")) {
      var armAt = this.Current.At;
      this.Accept("|");
      var pattern = this.Pattern();
      Expr? guard = null;
      if (this.Accept("if"))
        guard = this.Expression();

      this.Expect("=>");
      var blockBody = this.At("{");
      var body = this.Expression();
      arms.Add(new MatchArm(armAt, pattern, guard, body));
      if (!this.Accept(",") && !blockBody && !this.At("}"))
        throw this.Error($"expected ',' after a match arm, found {this.Current}");
    }

    this.Expect("}");
    return new MatchExpr(at, scrutinee, arms);
  }

  private Expr Loop(string? label, Span at) {
    this.Expect("loop");
    return new LoopExpr(at, label, this.Block());
  }

  private Expr While(string? label, Span at) {
    this.Expect("while");
    if (this.At("let")) {
      var letAt = this.Current.At;
      this.SkipCondition();
      this.Block();
      return new RefusedExpr(new Refusal(RustFrontend.ConditionalBinding, "while let", letAt));
    }

    var condition = this.Expression();
    return new WhileExpr(at, label, condition, this.Block());
  }

  private Expr For(string? label, Span at) {
    this.Expect("for");
    var pattern = this.Pattern();
    this.Expect("in");
    var iterable = this.Expression();
    return new ForExpr(at, label, pattern, iterable, this.Block());
  }

  private Expr Return() {
    var at = this.Next().At;
    return new ReturnExpr(at, this.EndsValue() ? null : this.Expression());
  }

  private Expr Break() {
    var at = this.Next().At;
    string? label = null;
    if (this.Current.Kind == TokenKind.Lifetime)
      label = this.Next().Text;

    return new BreakExpr(at, label, this.EndsValue() ? null : this.Expression());
  }

  private Expr Continue() {
    var at = this.Next().At;
    string? label = null;
    if (this.Current.Kind == TokenKind.Lifetime)
      label = this.Next().Text;

    return new ContinueExpr(at, label);
  }

  /// <summary>Whether no value follows a <c>return</c> or <c>break</c>.</summary>
  private bool EndsValue() => this.At(";") || this.At("}") || this.At(",") || this.At(")") || !this.StartsExpression();

  // ---------------------------------------------------------------- patterns

  /// <summary>A pattern of a match arm or a <c>for</c>, alternatives allowed.</summary>
  private Pattern Pattern() {
    var at = this.Current.At;
    var first = this.PatternNoTopAlternatives();
    if (!this.At("|"))
      return first;

    var alternatives = new List<Pattern> { first };
    while (this.Accept("|"))
      alternatives.Add(this.PatternNoTopAlternatives());

    return new OrPattern(at, alternatives);
  }

  private Pattern PatternNoTopAlternatives() {
    var token = this.Current;
    var at = token.At;
    if (token.Is("_")) {
      this.Next();
      return new WildcardPattern(at);
    }

    if (token.Is("(")) {
      this.Next();
      var elements = new List<Pattern>();
      while (!this.At(")")) {
        elements.Add(this.Pattern());
        if (!this.Accept(","))
          break;
      }

      this.Expect(")");
      return elements.Count == 1 ? elements[0] : new TuplePattern(at, elements);
    }

    if (token.Is("&") || token.Is("&&") || token.Is("[") || token.Is("..") && !this.PeekAt(1).Is("=")) {
      if (!token.Is(".."))
        this.Next();

      if (token.Is("["))
        this.SkipSlicePattern();
      else if (token.Is(".."))
        this.Next();
      else
        this.PatternNoTopAlternatives();

      return new RefusedPattern(new Refusal(RustFrontend.UnsupportedPattern, token.Is("[") ? "slice pattern" : token.Is("..") ? "rest pattern" : "reference pattern", at));
    }

    if (token.Is("..=")) {
      this.Next();
      return new RangePattern(at, null, this.RangeBound(), true);
    }

    if (token.Is("const")) {
      this.Next();
      this.SkipTree();
      return new RefusedPattern(new Refusal(RustFrontend.UnsupportedPattern, "const block", at));
    }

    if ((token.Is("ref") || token.Is("mut") || (token.Kind == TokenKind.Identifier && token.Text is not ("true" or "false"))) && !this.PeekAt(1).Is("::") && !this.PeekAt(1).Is("(") && !this.PeekAt(1).Is("{")) {
      var isRef = this.Accept("ref");
      var mutable = this.Accept("mut");
      var name = this.Identifier();
      if (this.At("@")) {
        this.Next();
        this.PatternNoTopAlternatives();
        return new RefusedPattern(new Refusal(RustFrontend.UnsupportedPattern, $"{name} @ ...", at));
      }

      if (isRef)
        return new RefusedPattern(new Refusal(RustFrontend.UnsupportedPattern, "ref binding", at));

      var pattern = new IdentifierPattern(at, name, mutable);
      return mutable || !(this.At("..=") || this.At("..")) ? pattern : this.RangeFrom(new PathExpr(at, [name]), at);
    }

    if (token.Kind == TokenKind.Identifier && token.Text is not ("true" or "false") && (this.PeekAt(1).Is("(") || this.PeekAt(1).Is("{") || this.PeekAt(1).Is("::"))) {
      var path = this.PathText();
      if (this.At("(") || this.At("{")) {
        this.SkipTree();
        return new RefusedPattern(new Refusal(RustFrontend.UnsupportedPattern, path.Text + (this._tokens[this._position - 1].Is(")") ? "(...)" : " { ... }"), at));
      }

      return this.At("..=") || this.At("..") ? this.RangeFrom(path, at) : new LiteralPattern(at, path);
    }

    var literal = this.LiteralBound();
    return this.At("..=") || this.At("..") ? this.RangeFrom(literal, at) : new LiteralPattern(at, literal);
  }

  private void SkipSlicePattern() {
    var depth = 1;
    while (depth > 0) {
      var token = this.Next();
      if (token.Kind == TokenKind.End)
        throw new RustSyntaxException("unclosed '['", token.At);

      depth += token.Is("[") ? 1 : token.Is("]") ? -1 : 0;
    }
  }

  private PathExpr PathText() {
    var at = this.Current.At;
    var segments = new List<string> { this.Identifier() };
    while (this.Accept("::"))
      segments.Add(this.Identifier());

    return new PathExpr(at, segments);
  }

  private Pattern RangeFrom(Expr low, Span at) {
    var inclusive = this.Next().Text == "..=";
    var bounded = this.Current.Kind is TokenKind.Integer or TokenKind.Float or TokenKind.Character || this.At("-")
      || (this.Current.Kind == TokenKind.Identifier && this.Current.Text is not ("if" or "true" or "false"));
    if (!bounded && inclusive)
      throw this.Error("inclusive range with no end");

    return new RangePattern(at, low, bounded ? this.RangeBound() : null, inclusive);
  }

  private Expr RangeBound() => this.Current.Kind == TokenKind.Identifier && this.Current.Text is not ("true" or "false") ? this.PathText() : this.LiteralBound();

  /// <summary>A literal of a pattern: a number, possibly negative, a truth value; a character or a string is refused.</summary>
  private Expr LiteralBound() {
    var token = this.Current;
    if (token.Is("-")) {
      this.Next();
      var operand = this.LiteralBound();
      return new UnaryExpr(token.At, "-", operand);
    }

    switch (token.Kind) {
      case TokenKind.Integer:
        this.Next();
        return new IntegerLiteral(token.At, token.Value, token.Suffix);
      case TokenKind.Float:
        this.Next();
        return new FloatLiteral(token.At, token.Text, token.Suffix);
      case TokenKind.Character or TokenKind.String:
        this.Next();
        return new RefusedExpr(new Refusal(RustFrontend.UnsupportedType, token.Kind == TokenKind.Character ? "char" : "&str", token.At));
      default:
        if (token.Is("true") || token.Is("false"))
          return this.Literal(token);

        throw this.Error($"expected pattern, found {token}");
    }
  }

}
