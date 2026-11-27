using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Polypiler.Core.Backends.Go.Syntax;

/// <summary>
/// Reads Go source of the subset the backend prints back into the syntax tree: the package clause,
/// imports, functions, constants, variables and types, every statement and expression of
/// <see cref="GoSyntax"/>, with the semicolons Go's lexer inserts at the end of a line
/// (The Go Programming Language Specification, "Semicolons"). Comments in front of a declaration or a
/// statement become its leading trivia, those above the package clause the header. It is the reader
/// of the parse-back checks and reads the Go text of the backend's helpers into the tree once; it is
/// no general Go parser and refuses what the backend never writes.
/// </summary>
public static class GoSyntaxReader {

  /// <summary>Reads a file.</summary>
  /// <param name="source">The text.</param>
  /// <returns>The tree.</returns>
  /// <exception cref="FormatException">The text is not Go of the subset.</exception>
  public static GoFile ReadFile(string source) {
    ArgumentNullException.ThrowIfNull(source);
    return new Parser(Lexer.Tokens(source)).File();
  }

  /// <summary>Reads the declarations of a file without a package clause, such as the text of helpers.</summary>
  /// <param name="source">The text.</param>
  /// <returns>The declarations, in order.</returns>
  /// <exception cref="FormatException">The text is not Go of the subset.</exception>
  public static IReadOnlyList<GoDecl> ReadDeclarations(string source) {
    ArgumentNullException.ThrowIfNull(source);
    var parser = new Parser(Lexer.Tokens(source));
    return parser.Declarations();
  }

  /// <summary>Reads one expression.</summary>
  /// <param name="source">The text.</param>
  /// <returns>The tree.</returns>
  /// <exception cref="FormatException">The text is not one expression of the subset.</exception>
  public static GoExpr ReadExpression(string source) {
    ArgumentNullException.ThrowIfNull(source);
    var parser = new Parser(Lexer.Tokens(source));
    var expression = parser.Expression();
    parser.End();
    return expression;
  }

  private enum Kind { Ident, Int, Float, String, Char, Operator, Keyword, Semicolon, End }

  private sealed record Token(Kind Kind, string Text, int Line, int BlankLines, IReadOnlyList<string> Comments) {

    public bool Is(string text) => this.Kind is Kind.Operator or Kind.Keyword && this.Text == text;

  }

  private static class Lexer {

    private static readonly HashSet<string> _keywords = new(StringComparer.Ordinal) {
      "break", "case", "chan", "const", "continue", "default", "defer", "else", "fallthrough", "for", "func", "go", "goto", "if",
      "import", "interface", "map", "package", "range", "return", "select", "struct", "switch", "type", "var"
    };

    private static readonly string[] _operators = [
      "<<=", ">>=", "&^=", "...", "&&", "||", "<-", "++", "--", "==", "!=", "<=", ">=", ":=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=",
      "<<", ">>", "&^", "+", "-", "*", "/", "%", "&", "|", "^", "<", ">", "=", "!", "(", ")", "[", "]", "{", "}", ",", ";", ".", ":", "~"
    ];

    public static List<Token> Tokens(string source) {
      var tokens = new List<Token>();
      var (index, line, blank, newlines) = (0, 1, 0, 0);
      var comments = new List<string>();
      void Add(Kind kind, string text) {
        tokens.Add(new(kind, text, line, blank, [.. comments]));
        comments.Clear();
        blank = 0;
        newlines = 0;
      }

      bool InsertsSemicolon() => tokens.Count > 0 && tokens[^1] switch {
        { Kind: Kind.Ident or Kind.Int or Kind.Float or Kind.String or Kind.Char } => true,
        { Kind: Kind.Keyword, Text: "break" or "continue" or "fallthrough" or "return" } => true,
        { Kind: Kind.Operator, Text: "++" or "--" or ")" or "]" or "}" } => true,
        _ => false
      };

      while (index < source.Length) {
        var character = source[index];
        if (character == '\n') {
          if (InsertsSemicolon())
            Add(Kind.Semicolon, "\n");

          ++line;
          ++index;
          if (++newlines >= 2)
            blank = 1;

          continue;
        }

        if (character is ' ' or '\t' or '\r') {
          ++index;
          continue;
        }

        if (character == '/' && index + 1 < source.Length && source[index + 1] == '/') {
          var end = source.IndexOf('\n', index);
          end = end < 0 ? source.Length : end;
          var text = source[(index + 2)..end];
          comments.Add(text.StartsWith(' ') ? text[1..] : text);
          newlines = 0;
          index = end;
          continue;
        }

        if (character == '/' && index + 1 < source.Length && source[index + 1] == '*')
          throw new FormatException($"line {line}: block comments are outside the subset");

        if (char.IsAsciiLetter(character) || character == '_') {
          var start = index;
          while (index < source.Length && (char.IsAsciiLetterOrDigit(source[index]) || source[index] == '_'))
            ++index;

          var word = source[start..index];
          Add(_keywords.Contains(word) ? Kind.Keyword : Kind.Ident, word);
          continue;
        }

        if (char.IsAsciiDigit(character) || (character == '.' && index + 1 < source.Length && char.IsAsciiDigit(source[index + 1]))) {
          var start = index;
          var isFloat = false;
          if (character == '0' && index + 1 < source.Length && source[index + 1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O') {
            index += 2;
            while (index < source.Length && (char.IsAsciiHexDigit(source[index]) || source[index] == '_'))
              ++index;
          } else {
            while (index < source.Length && (char.IsAsciiDigit(source[index]) || source[index] is '_' or '.' or 'e' or 'E'
              || (source[index] is '+' or '-' && source[index - 1] is 'e' or 'E'))) {
              isFloat |= source[index] is '.' or 'e' or 'E';
              ++index;
            }
          }

          Add(isFloat ? Kind.Float : Kind.Int, source[start..index]);
          continue;
        }

        if (character == '"') {
          var text = new StringBuilder();
          ++index;
          while (index < source.Length && source[index] != '"') {
            if (source[index] == '\n')
              throw new FormatException($"line {line}: unterminated string");

            if (source[index] != '\\') {
              text.Append(source[index++]);
              continue;
            }

            var escape = source[index + 1];
            index += 2;
            switch (escape) {
              case 'a': text.Append('\a'); break;
              case 'b': text.Append('\b'); break;
              case 'f': text.Append('\f'); break;
              case 'n': text.Append('\n'); break;
              case 'r': text.Append('\r'); break;
              case 't': text.Append('\t'); break;
              case 'v': text.Append('\v'); break;
              case '\\': text.Append('\\'); break;
              case '"': text.Append('"'); break;
              case 'x':
                text.Append((char)int.Parse(source.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                index += 2;
                break;
              case 'u' or 'U': {
                var length = escape == 'u' ? 4 : 8;
                text.Append(char.ConvertFromUtf32(int.Parse(source.AsSpan(index, length), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                index += length;
                break;
              }
              default:
                throw new FormatException($"line {line}: the escape \\{escape} is outside the subset");
            }
          }

          if (index >= source.Length)
            throw new FormatException($"line {line}: unterminated string");

          ++index;
          Add(Kind.String, text.ToString());
          continue;
        }

        if (character == '`') {
          var end = source.IndexOf('`', index + 1);
          if (end < 0)
            throw new FormatException($"line {line}: unterminated raw string");

          var raw = source[(index + 1)..end];
          line += raw.Count(c => c == '\n');
          index = end + 1;
          Add(Kind.String, raw.Replace("\r", "", StringComparison.Ordinal));
          continue;
        }

        if (character == '\'') {
          var end = source.IndexOf('\'', index + 1);
          if (end < 0)
            throw new FormatException($"line {line}: unterminated rune");

          Add(Kind.Char, source[(index + 1)..end]);
          index = end + 1;
          continue;
        }

        var op = _operators.FirstOrDefault(candidate => string.CompareOrdinal(source, index, candidate, 0, candidate.Length) == 0)
          ?? throw new FormatException($"line {line}: unexpected character '{character}'");
        index += op.Length;
        Add(op == ";" ? Kind.Semicolon : Kind.Operator, op);
      }

      if (InsertsSemicolon())
        Add(Kind.Semicolon, "\n");

      Add(Kind.End, "");
      return tokens;
    }

  }

  private sealed class Parser(List<Token> tokens) {

    private int _position;

    /// <summary>Whether a composite literal of a type name may start here: not in the clause of <c>if</c>, <c>for</c> or <c>switch</c>.</summary>
    private int _level;

    private Token Peek => tokens[this._position];

    private Token PeekAt(int offset) => tokens[Math.Min(this._position + offset, tokens.Count - 1)];

    private Token Next() => tokens[this._position++];

    private FormatException Error(string expected) => new($"line {this.Peek.Line}: expected {expected}, found '{this.Peek.Text}'");

    private Token Expect(string text) {
      if (text == ";")
        return this.Peek is { Kind: Kind.Semicolon, Text: ";" } ? this.Next() : throw this.Error("';'");

      return this.Peek.Is(text) ? this.Next() : throw this.Error($"'{text}'");
    }

    private bool Accept(string text) {
      if (!this.Peek.Is(text))
        return false;

      ++this._position;
      return true;
    }

    private string Identifier() => this.Peek.Kind == Kind.Ident ? this.Next().Text : throw this.Error("an identifier");

    private void Semicolon() {
      if (this.Peek.Kind == Kind.Semicolon) {
        ++this._position;
        return;
      }

      if (!this.Peek.Is(")") && !this.Peek.Is("}") && this.Peek.Kind != Kind.End)
        throw this.Error("the end of the statement");
    }

    public void End() {
      while (this.Peek.Kind == Kind.Semicolon)
        ++this._position;

      if (this.Peek.Kind != Kind.End)
        throw this.Error("the end of the text");
    }

    private static GoTrivia TriviaOf(Token token) => new(token.BlankLines, token.Comments);

    #region Files and declarations

    public GoFile File() {
      var header = this.Peek.Comments;
      this.Expect("package");
      var name = this.Identifier();
      this.Semicolon();
      var imports = new List<string>();
      while (this.Accept("import")) {
        if (this.Accept("(")) {
          while (!this.Accept(")")) {
            imports.Add(this.Peek.Kind == Kind.String ? this.Next().Text : throw this.Error("an import path"));
            this.Semicolon();
          }
        } else {
          imports.Add(this.Peek.Kind == Kind.String ? this.Next().Text : throw this.Error("an import path"));
        }

        this.Semicolon();
      }

      return new GoFile(header, name, imports, this.Declarations());
    }

    public List<GoDecl> Declarations() {
      var declarations = new List<GoDecl>();
      while (this.Peek.Kind != Kind.End) {
        if (this.Peek.Kind == Kind.Semicolon) {
          ++this._position;
          continue;
        }

        var leading = TriviaOf(this.Peek);
        GoDecl declaration = this.Peek.Text switch {
          "func" => this.Function(),
          "const" => this.Value(true),
          "var" => this.Value(false),
          "type" => this.TypeDeclaration(),
          _ => throw this.Error("a declaration")
        };
        declarations.Add(declaration with { Leading = leading });
        this.Semicolon();
      }

      return declarations;
    }

    private GoFuncDecl Function() {
      this.Expect("func");
      GoField? receiver = null;
      if (this.Accept("(")) {
        var fields = this.Fields(")");
        receiver = fields.Single();
      }

      var name = this.Identifier();
      var signature = this.Signature();
      return new GoFuncDecl(name, signature, this.Block()) { Receiver = receiver };
    }

    private GoDecl Value(bool constant) {
      this.Next();
      var spec = this.Spec();
      return constant ? new GoConstDecl(spec) : new GoVarDecl(spec);
    }

    private GoValueSpec Spec() {
      var names = new List<string> { this.Identifier() };
      while (this.Accept(","))
        names.Add(this.Identifier());

      GoExpr? type = this.Peek.Is("=") ? null : this.Type();
      var values = this.Accept("=") ? this.ExpressionList() : [];
      return new GoValueSpec(names, type, values);
    }

    private GoTypeDecl TypeDeclaration() {
      this.Expect("type");
      var name = this.Identifier();
      return new GoTypeDecl(name, this.Type());
    }

    private GoFuncType Signature() {
      this.Expect("(");
      var parameters = this.Fields(")");
      List<GoField> results = [];
      if (this.Accept("("))
        results = this.Fields(")");
      else if (this.StartsType())
        results = [new GoField([], this.Type())];

      return new GoFuncType(parameters, results);
    }

    private bool StartsType() => this.Peek.Kind == Kind.Ident || this.Peek.Is("[") || this.Peek.Is("*") || this.Peek.Is("func") || this.Peek.Is("map") || this.Peek.Is("struct");

    /// <summary>
    /// The fields of a parameter list or struct up to the closing token: <c>a, b T</c> groups, or types
    /// alone, which a list of names without a type turns out to be.
    /// </summary>
    private List<GoField> Fields(string close) {
      var entries = new List<(string? Name, GoExpr? Type)>();
      while (!this.Accept(close)) {
        if (this.Peek.Kind == Kind.Semicolon) {
          ++this._position;
          continue;
        }

        if (this.Peek.Kind == Kind.Ident && this.PeekAt(1) is { } after && !after.Is(".") && (after.Is(",") || after.Is(close) || after.Kind == Kind.Semicolon)) {
          entries.Add((this.Identifier(), null));
        } else if (this.Peek.Kind == Kind.Ident && !this.PeekAt(1).Is(".")) {
          var name = this.Identifier();
          entries.Add((name, this.Type()));
        } else {
          entries.Add((null, this.Type()));
        }

        if (!this.Accept(",") && this.Peek.Kind == Kind.Semicolon)
          ++this._position;
      }

      // Without any type after a name, every entry is a type of its own.
      if (entries.All(entry => entry.Type is null || entry.Name is null))
        return [.. entries.Select(entry => new GoField([], entry.Type ?? new GoIdent(entry.Name!)))];

      var fields = new List<GoField>();
      var pending = new List<string>();
      foreach (var (name, type) in entries) {
        pending.Add(name ?? throw new FormatException("a parameter list mixes named and unnamed parameters"));
        if (type is null)
          continue;

        fields.Add(new GoField([.. pending], type));
        pending.Clear();
      }

      if (pending.Count > 0)
        throw new FormatException("a parameter list ends with a name without a type");

      return fields;
    }

    private GoExpr Type() {
      var token = this.Peek;
      switch (token.Text) {
        case "[" when token.Kind == Kind.Operator: {
          this.Next();
          GoExpr? length = this.Peek.Is("]") ? null : this.Expression();
          this.Expect("]");
          return new GoArrayType(length, this.Type());
        }
        case "*" when token.Kind == Kind.Operator:
          this.Next();
          return new GoStar(this.Type());
        case "map" when token.Kind == Kind.Keyword: {
          this.Next();
          this.Expect("[");
          var key = this.Type();
          this.Expect("]");
          return new GoMapType(key, this.Type());
        }
        case "func" when token.Kind == Kind.Keyword:
          this.Next();
          return this.Signature();
        case "struct" when token.Kind == Kind.Keyword:
          this.Next();
          this.Expect("{");
          return new GoStructType(this.Fields("}"));
        case "(" when token.Kind == Kind.Operator: {
          this.Next();
          var inner = this.Type();
          this.Expect(")");
          return inner;
        }
      }

      GoExpr name = new GoIdent(this.Identifier());
      if (this.Accept("."))
        name = new GoSelector(name, this.Identifier());

      return name;
    }

    #endregion

    #region Statements

    private GoBlock Block() {
      this.Expect("{");
      var statements = new List<GoStmt>();
      while (!this.Accept("}")) {
        if (this.Peek.Kind == Kind.Semicolon) {
          ++this._position;
          continue;
        }

        statements.Add(this.Statement());
        this.Semicolon();
      }

      return new GoBlock(statements);
    }

    private GoStmt Statement() {
      var leading = TriviaOf(this.Peek);
      var token = this.Peek;
      GoStmt statement;
      if (token.Kind == Kind.Ident && this.PeekAt(1).Is(":") && !this.PeekAt(1).Is(":=")) {
        this.Next();
        this.Next();
        while (this.Peek.Kind == Kind.Semicolon)
          ++this._position;

        statement = new GoLabeled(token.Text, this.Statement());
        return statement with { Leading = leading };
      }

      statement = token.Kind == Kind.Keyword ? token.Text switch {
        "var" => this.VarStatement(),
        "return" => this.Return(),
        "if" => this.If(),
        "for" => this.For(),
        "switch" => this.Switch(),
        "break" or "continue" or "goto" or "fallthrough" => this.Branch(),
        "defer" => this.Defer(),
        _ => this.Simple()
      } : token.Is("{") ? new GoBlockStmt(this.Block()) : this.Simple();
      return statement with { Leading = leading };
    }

    private GoVarStmt VarStatement() {
      this.Expect("var");
      return new GoVarStmt(this.Spec());
    }

    private GoReturn Return() {
      this.Expect("return");
      return new GoReturn(this.Peek.Kind == Kind.Semicolon || this.Peek.Is("}") ? [] : this.ExpressionList());
    }

    private GoBranch Branch() {
      var keyword = this.Next().Text;
      return new GoBranch(keyword, this.Peek.Kind == Kind.Ident ? this.Identifier() : null);
    }

    private GoDefer Defer() {
      this.Expect("defer");
      return new GoDefer(this.Expression());
    }

    private GoStmt Simple() {
      var left = this.ExpressionList();
      var token = this.Peek;
      if (token.Is("++") || token.Is("--")) {
        this.Next();
        return new GoIncDec(left.Single(), token.Text == "++");
      }

      if (token.Kind == Kind.Operator && token.Text is "=" or ":=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<=" or ">>=" or "&^=") {
        this.Next();
        return new GoAssign(left, token.Text, this.ExpressionList());
      }

      return new GoExprStmt(left.Single());
    }

    private GoIf If() {
      this.Expect("if");
      var outer = this._level;
      this._level = -1;
      GoStmt? init = null;
      var first = this.Simple();
      GoExpr condition;
      if (this.Peek.Kind == Kind.Semicolon && this.Peek.Text == ";") {
        this.Next();
        init = first;
        condition = this.Expression();
      } else {
        condition = first is GoExprStmt expression ? expression.Expression : throw this.Error("a condition");
      }

      this._level = outer;
      var then = this.Block();
      GoStmt? otherwise = null;
      if (this.Accept("else"))
        otherwise = this.Peek.Is("if") ? this.If() : new GoBlockStmt(this.Block());

      return new GoIf(init, condition, then, otherwise);
    }

    private GoStmt For() {
      this.Expect("for");
      var outer = this._level;
      this._level = -1;
      try {
        if (this.Peek.Is("{"))
          return this.ForBody(null, null, null);

        if (this.Accept("range")) {
          var over = this.Expression();
          this._level = outer;
          return new GoRange(null, null, false, over, this.Block());
        }

        GoStmt? init = null;
        if (!(this.Peek.Kind == Kind.Semicolon && this.Peek.Text == ";")) {
          var left = this.ExpressionList();
          if (this.Peek.Is(":=") || this.Peek.Is("=")) {
            var define = this.Next().Text == ":=";
            if (this.Accept("range")) {
              var over = this.Expression();
              this._level = outer;
              return new GoRange(left[0], left.Count > 1 ? left[1] : null, define, over, this.Block());
            }

            init = new GoAssign(left, define ? ":=" : "=", this.ExpressionList());
          } else if (this.Peek.Is("++") || this.Peek.Is("--")) {
            init = new GoIncDec(left.Single(), this.Next().Text == "++");
          } else if (this.Peek.Is("{")) {
            this._level = outer;
            return this.ForBody(null, left.Single(), null);
          } else {
            init = new GoExprStmt(left.Single());
          }
        }

        this.Expect(";");
        GoExpr? condition = this.Peek.Kind == Kind.Semicolon ? null : this.Expression();
        this.Expect(";");
        GoStmt? post = this.Peek.Is("{") ? null : this.Simple();
        this._level = outer;
        return this.ForBody(init, condition, post);
      } finally {
        this._level = outer;
      }
    }

    private GoFor ForBody(GoStmt? init, GoExpr? condition, GoStmt? post) => new(init, condition, post, this.Block());

    private GoSwitch Switch() {
      this.Expect("switch");
      var outer = this._level;
      this._level = -1;
      GoStmt? init = null;
      GoExpr? tag = null;
      if (!this.Peek.Is("{")) {
        var first = this.Simple();
        if (this.Peek.Kind == Kind.Semicolon && this.Peek.Text == ";") {
          this.Next();
          init = first;
          if (!this.Peek.Is("{"))
            tag = this.Expression();
        } else {
          tag = first is GoExprStmt expression ? expression.Expression : throw this.Error("a tag");
        }
      }

      this._level = outer;
      this.Expect("{");
      var clauses = new List<GoCaseClause>();
      while (!this.Accept("}")) {
        if (this.Peek.Kind == Kind.Semicolon) {
          ++this._position;
          continue;
        }

        IReadOnlyList<GoExpr>? values = null;
        if (this.Accept("case"))
          values = this.ExpressionList();
        else
          this.Expect("default");

        this.Expect(":");
        var body = new List<GoStmt>();
        while (!this.Peek.Is("case") && !this.Peek.Is("default") && !this.Peek.Is("}")) {
          if (this.Peek.Kind == Kind.Semicolon) {
            ++this._position;
            continue;
          }

          body.Add(this.Statement());
          this.Semicolon();
        }

        clauses.Add(new GoCaseClause(values, body));
      }

      return new GoSwitch(init, tag, clauses);
    }

    #endregion

    #region Expressions

    private List<GoExpr> ExpressionList() {
      var list = new List<GoExpr> { this.Expression() };
      while (this.Accept(","))
        list.Add(this.Expression());

      return list;
    }

    public GoExpr Expression() => this.Binary(1);

    private GoExpr Binary(int precedence) {
      var left = this.Unary();
      while (true) {
        var token = this.Peek;
        if (token.Kind != Kind.Operator || BinaryPrecedence(token.Text) is not { } level || level < precedence)
          return left;

        this.Next();
        left = new GoBinary(left, token.Text, this.Binary(level + 1));
      }
    }

    private static int? BinaryPrecedence(string op) => op switch {
      "*" or "/" or "%" or "<<" or ">>" or "&" or "&^" => 5,
      "+" or "-" or "|" or "^" => 4,
      "==" or "!=" or "<" or "<=" or ">" or ">=" => 3,
      "&&" => 2,
      "||" => 1,
      _ => null
    };

    private GoExpr Unary() {
      var token = this.Peek;
      if (token.Kind == Kind.Operator && token.Text is "-" or "+" or "!" or "^" or "&") {
        this.Next();
        return new GoUnary(token.Text, this.Unary());
      }

      if (token.Is("*")) {
        this.Next();
        return new GoStar(this.Unary());
      }

      return this.Primary();
    }

    private GoExpr Primary() {
      var expression = this.Operand();
      while (true) {
        if (this.Accept(".")) {
          expression = new GoSelector(expression, this.Identifier());
          continue;
        }

        if (this.Peek.Is("(")) {
          this.Next();
          var outer = this._level;
          this._level = 0;
          var arguments = new List<GoExpr>();
          var spread = false;
          while (!this.Accept(")")) {
            arguments.Add(this.Expression());
            spread = this.Accept("...");
            if (!this.Accept(",") && this.Peek.Kind == Kind.Semicolon)
              ++this._position;
          }

          this._level = outer;
          expression = new GoCall(expression, arguments) { Spread = spread };
          continue;
        }

        if (this.Peek.Is("[")) {
          this.Next();
          var outer = this._level;
          this._level = 0;
          GoExpr? low = this.Peek.Is(":") ? null : this.Expression();
          if (this.Accept(":")) {
            GoExpr? high = this.Peek.Is("]") || this.Peek.Is(":") ? null : this.Expression();
            GoExpr? max = this.Accept(":") ? this.Expression() : null;
            this.Expect("]");
            this._level = outer;
            expression = new GoSliceExpr(expression, low, high, max);
            continue;
          }

          this.Expect("]");
          this._level = outer;
          expression = new GoIndex(expression, low!);
          continue;
        }

        // In the clause of if, for and switch a brace after a type name opens the block, not a composite literal.
        if (this.Peek.Is("{") && IsLiteralType(expression) && (this._level >= 0 || expression is GoArrayType or GoMapType or GoStructType)) {
          expression = this.Composite(expression);
          continue;
        }

        return expression;
      }
    }

    private static bool IsLiteralType(GoExpr expression) => expression is GoIdent or GoSelector { Target: GoIdent } or GoArrayType or GoMapType or GoStructType;

    private GoComposite Composite(GoExpr? type) {
      var open = this.Expect("{");
      var outer = this._level;
      this._level = 0;
      var elements = new List<GoExpr>();
      var perLine = 0;
      var lines = new List<int>();
      var multiLine = this.Peek.Kind == Kind.Semicolon || this.Peek.Line != open.Line;
      while (!this.Accept("}")) {
        if (this.Peek.Kind == Kind.Semicolon) {
          ++this._position;
          continue;
        }

        lines.Add(this.Peek.Line);
        var element = this.Peek.Is("{") ? this.Composite(null) : this.Expression();
        if (this.Accept(":"))
          element = new GoKeyValue(element, this.Peek.Is("{") ? this.Composite(null) : this.Expression());

        elements.Add(element);
        if (!this.Accept(",") && this.Peek.Kind == Kind.Semicolon)
          ++this._position;
      }

      this._level = outer;
      if (multiLine && lines.Count > 0)
        perLine = lines.GroupBy(line => line).Max(group => group.Count());

      return new GoComposite(type, elements) { PerLine = perLine };
    }

    private GoExpr Operand() {
      var token = this.Peek;
      switch (token.Kind) {
        case Kind.Ident:
          this.Next();
          return new GoIdent(token.Text);
        case Kind.Int:
          this.Next();
          return Integer(token.Text);
        case Kind.Float:
          this.Next();
          return new GoFloat(token.Text);
        case Kind.String:
          this.Next();
          return new GoString(token.Text);
        case Kind.Char:
          throw this.Error("no rune literal, which the subset does not have");
      }

      if (this.Accept("(")) {
        var outer = this._level;
        this._level = 0;
        var inner = this.Expression();
        this.Expect(")");
        this._level = outer;
        return inner;
      }

      if (token.Is("func")) {
        this.Next();
        var signature = this.Signature();
        if (!this.Peek.Is("{"))
          return signature;

        var outer = this._level;
        this._level = 0;
        var body = this.Block();
        this._level = outer;
        return new GoFuncLit(signature, body);
      }

      if (token.Is("[") || token.Is("map") || token.Is("struct") || token.Is("*"))
        return this.Type();

      throw this.Error("an expression");
    }

    private static GoInt Integer(string text) {
      var digits = text.Replace("_", "", StringComparison.Ordinal);
      if (digits.Length > 2 && digits[0] == '0' && digits[1] is 'x' or 'X')
        return new GoInt(BigInteger.Parse("0" + digits[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture), true);

      if (digits.Length > 2 && digits[0] == '0' && digits[1] is 'b' or 'B' or 'o' or 'O') {
        var radix = digits[1] is 'b' or 'B' ? 2 : 8;
        return new GoInt(digits[2..].Aggregate(BigInteger.Zero, (value, digit) => value * radix + (digit - '0')));
      }

      return new GoInt(BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture));
    }

    #endregion

  }

}
