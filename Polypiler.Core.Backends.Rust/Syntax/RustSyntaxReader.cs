using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Backends.Rust.Syntax;

/// <summary>
/// Reads Rust source back into the syntax tree: the subset the printer writes, by The Rust Reference's
/// grammar of items, statements and expressions and its table of operator precedence. The parse-back
/// sweep reads every generated file with it and prints the tree again; the text must come back byte
/// for byte, which shows that the tree the printer was given is the tree the text means. The reader
/// keeps no IR types; parentheses are nodes, as the printer writes them.
/// </summary>
public static class RustSyntaxReader {

  /// <summary>Reads a source file.</summary>
  /// <param name="text">The source.</param>
  /// <returns>The tree.</returns>
  /// <exception cref="FormatException">The text is not in the subset.</exception>
  public static RsFile Read(string text) {
    ArgumentNullException.ThrowIfNull(text);
    return new Parser(Tokenize(text)).File();
  }

  private enum Kind { Identifier, Number, String, Lifetime, Punctuation, InnerDoc, OuterDoc, InnerAttribute, OuterAttribute, Comment, End }

  private sealed record Token(Kind Kind, string Text);

  private static readonly string[] _punctuation = [
    "<<=", ">>=", "..=", "::", "->", "=>", "==", "!=", "<=", ">=", "&&", "||", "<<", ">>", "+=", "-=", "*=", "/=", "%=", "^=", "&=", "|=", "..",
    "+", "-", "*", "/", "%", "^", "!", "&", "|", "=", "<", ">", "(", ")", "[", "]", "{", "}", ",", ";", ":", ".", "_", "#", "?"
  ];

  private static List<Token> Tokenize(string text) {
    var tokens = new List<Token>();
    var index = 0;
    while (index < text.Length) {
      var c = text[index];
      if (char.IsWhiteSpace(c)) {
        ++index;
        continue;
      }

      if (text.AsSpan(index).StartsWith("//")) {
        var end = text.IndexOf('\n', index);
        end = end < 0 ? text.Length : end;
        var line = text[index..end];
        if (line.StartsWith("//!", StringComparison.Ordinal))
          tokens.Add(new(Kind.InnerDoc, line[3..].TrimStart(' ')));
        else if (line.StartsWith("///", StringComparison.Ordinal))
          tokens.Add(new(Kind.OuterDoc, line[3..].TrimStart(' ')));
        else
          tokens.Add(new(Kind.Comment, line[2..].TrimStart(' ')));
        index = end;
        continue;
      }

      if (c == '#') {
        var inner = index + 1 < text.Length && text[index + 1] == '!';
        var open = text.IndexOf('[', index);
        var depth = 0;
        var close = open;
        for (; close < text.Length; ++close) {
          depth += text[close] switch { '[' => 1, ']' => -1, _ => 0 };
          if (depth == 0)
            break;
        }

        // An attribute broken over lines reads as the one line it stands for.
        var content = string.Join(" ", text[(open + 1)..close].Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0))
          .Replace("( ", "(", StringComparison.Ordinal).Replace(" )", ")", StringComparison.Ordinal);
        tokens.Add(new(inner ? Kind.InnerAttribute : Kind.OuterAttribute, content));
        index = close + 1;
        continue;
      }

      if (c == '"') {
        var value = new StringBuilder();
        ++index;
        while (text[index] != '"') {
          if (text[index] != '\\') {
            value.Append(text[index++]);
            continue;
          }

          var escape = text[++index];
          if (escape == 'u') {
            var close = text.IndexOf('}', index);
            value.Append((char)Convert.ToInt32(text[(index + 2)..close], 16));
            index = close + 1;
            continue;
          }

          value.Append(escape switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', _ => escape });
          ++index;
        }

        ++index;
        tokens.Add(new(Kind.String, value.ToString()));
        continue;
      }

      // A character literal, 'x' or '\x', reads as the literal it is; anything else after a quote is a lifetime.
      var quote = index + 1 < text.Length && text[index + 1] == '\\' ? index + 3 : index + 2;
      if (c == '\'' && quote < text.Length && text[quote] == '\'') {
        tokens.Add(new(Kind.Number, text[index..(quote + 1)]));
        index = quote + 1;
        continue;
      }

      if (c == '\'') {
        var start = ++index;
        while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] == '_'))
          ++index;
        tokens.Add(new(Kind.Lifetime, text[start..index]));
        continue;
      }

      if (char.IsAsciiDigit(c)) {
        var start = index;
        while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] == '_'
          || (text[index] == '.' && index + 1 < text.Length && char.IsAsciiDigit(text[index + 1]))
          || ((text[index] is '-' or '+') && (text[index - 1] is 'e' or 'E') && !text.AsSpan(start, index - start).StartsWith("0x"))))
          ++index;
        tokens.Add(new(Kind.Number, text[start..index]));
        continue;
      }

      if (char.IsAsciiLetter(c) || (c == '_' && index + 1 < text.Length && (char.IsAsciiLetterOrDigit(text[index + 1]) || text[index + 1] == '_'))) {
        var start = index;
        if (text.AsSpan(index).StartsWith("r#"))
          index += 2;
        while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] == '_'))
          ++index;
        tokens.Add(new(Kind.Identifier, text[start..index]));
        continue;
      }

      var symbol = _punctuation.FirstOrDefault(candidate => text.AsSpan(index).StartsWith(candidate))
        ?? throw new FormatException($"Unexpected character '{c}' at offset {index}.");
      tokens.Add(new(Kind.Punctuation, symbol));
      index += symbol.Length;
    }

    tokens.Add(new(Kind.End, ""));
    return tokens;
  }

  private sealed class Parser(List<Token> tokens) {

    private int _index;

    private Token Peek(int offset = 0) => tokens[Math.Min(this._index + offset, tokens.Count - 1)];

    private Token Next() => tokens[this._index++];

    private bool Is(string text, int offset = 0) => this.Peek(offset) is { Kind: Kind.Punctuation or Kind.Identifier } token && token.Text == text;

    private bool Accept(string text) {
      if (!this.Is(text))
        return false;

      ++this._index;
      return true;
    }

    private void Expect(string text) {
      if (!this.Accept(text))
        throw new FormatException($"Expected '{text}', found '{this.Peek().Text}'.");
    }

    private string Identifier() {
      var token = this.Next();
      return token.Kind == Kind.Identifier || token.Text == "_" ? token.Text : throw new FormatException($"Expected a name, found '{token.Text}'.");
    }

    /// <summary>
    /// A pattern of names in parentheses, <c>(l, r)</c>, as a <c>let</c> that takes a pair apart has it, or
    /// <c>(&amp;s, d)</c> as a loop over two zipped slices.
    /// </summary>
    private string TuplePattern() {
      this.Expect("(");
      var names = new List<string> { this.Bound() };
      while (this.Accept(","))
        names.Add(this.Bound());
      this.Expect(")");
      return $"({string.Join(", ", names)})";
    }

    /// <summary>A name a pattern binds, <c>&amp;x</c> where it copies the element a reference points to.</summary>
    private string Bound() => (this.Accept("&") ? "&" : "") + this.Identifier();

    public RsFile File() {
      var docs = new List<string>();
      var attributes = new List<string>();
      while (this.Peek().Kind is Kind.InnerDoc or Kind.InnerAttribute) {
        var token = this.Next();
        (token.Kind == Kind.InnerDoc ? docs : attributes).Add(token.Text);
      }

      var items = new List<RsItem>();
      while (this.Peek().Kind != Kind.End)
        items.Add(this.Item());

      return new RsFile(docs, attributes, items);
    }

    private RsItem Item() {
      var docs = new List<string>();
      var attributes = new List<string>();
      while (this.Peek().Kind is Kind.OuterDoc or Kind.OuterAttribute) {
        var token = this.Next();
        (token.Kind == Kind.OuterDoc ? docs : attributes).Add(token.Text);
      }

      var visibility = "";
      if (this.Accept("pub")) {
        visibility = "pub";
        if (this.Is("(")) {
          this.Expect("(");
          visibility = $"pub({this.Identifier()})";
          this.Expect(")");
        }
      }

      RsItem item;
      if (this.Accept("fn"))
        item = this.Function(visibility);
      else if (this.Is("const") || this.Is("static")) {
        var isStatic = this.Next().Text == "static";
        var name = this.Identifier();
        this.Expect(":");
        var type = this.TypeText();
        this.Expect("=");
        var value = this.Expression();
        this.Expect(";");
        item = new RsConst(name, visibility, type, value) { IsStatic = isStatic };
      } else if (this.Accept("mod")) {
        var name = this.Identifier();
        if (this.Accept(";"))
          item = new RsModuleDeclaration(name, visibility);
        else {
          this.Expect("{");
          var items = new List<RsItem>();
          while (!this.Accept("}"))
            items.Add(this.Item());
          item = new RsInlineModule(name, items);
        }
      } else if (this.Accept("struct")) {
        item = this.Struct(visibility);
      } else if (this.Accept("trait")) {
        var name = this.Identifier();
        var supertraits = new List<string>();
        if (this.Accept(":")) {
          supertraits.Add(this.Identifier());
          while (this.Accept("+"))
            supertraits.Add(this.Identifier());
        }

        this.Expect("{");
        var items = new List<RsItem>();
        while (!this.Accept("}"))
          items.Add(this.Item());
        item = new RsTrait(name, visibility, supertraits, items);
      } else if (this.Accept("impl")) {
        var generics = this.Is("<") ? this.TypeText() : "";
        var first = this.TypeText(true);
        string? trait = null;
        if (this.Accept("for")) {
          trait = first;
          first = this.TypeText();
        }

        this.Expect("{");
        var items = new List<RsItem>();
        while (!this.Accept("}"))
          items.Add(this.Item());
        item = new RsImpl(trait, first, items) { Generics = generics };
      } else if (this.Accept("type")) {
        var name = this.Identifier();
        this.Expect("=");
        var type = this.TypeText();
        this.Expect(";");
        item = new RsTypeAlias(name, type);
      } else if (this.Accept("use")) {
        var path = new StringBuilder();
        while (!this.Is(";"))
          path.Append(this.Next().Text);
        this.Expect(";");
        item = new RsUse(path.ToString()) { Visibility = visibility };
      } else if (this.Accept("enum")) {
        var name = this.Identifier();
        this.Expect("{");
        var variants = new List<RsVariant>();
        while (!this.Accept("}")) {
          var variant = this.Identifier();
          variants.Add(new RsVariant(variant, this.Accept("=") ? this.Next().Text : null));
          this.Accept(",");
        }

        item = new RsEnum(name, visibility, variants);
      } else {
        throw new FormatException($"Expected an item, found '{this.Peek().Text}'.");
      }

      return item with { Docs = docs, Attributes = attributes };
    }

    /// <summary><c>struct Name { fields }</c>, <c>struct Name(fields);</c> or <c>struct Name;</c>.</summary>
    private RsStruct Struct(string visibility) {
      var name = this.Identifier();
      var generics = this.Is("<") ? this.TypeText() : "";
      var fields = new List<RsFieldDeclaration>();
      if (this.Accept(";"))
        return new RsStruct(name, visibility, fields);

      var tuple = this.Accept("(");
      if (!tuple)
        this.Expect("{");

      while (!this.Accept(tuple ? ")" : "}")) {
        var fieldVisibility = this.Accept("pub") ? "pub" : "";
        var fieldName = "";
        if (!tuple) {
          fieldName = this.Identifier();
          this.Expect(":");
        }

        fields.Add(new RsFieldDeclaration(fieldName, fieldVisibility, this.TypeText()));
        this.Accept(",");
      }

      if (tuple)
        this.Expect(";");
      return new RsStruct(name, visibility, fields) { Generics = generics };
    }

    private RsFunction Function(string visibility) {
      var name = this.Identifier();
      var generics = this.Is("<") ? this.TypeText() : "";
      this.Expect("(");
      var parameters = new List<RsParameter>();
      while (!this.Accept(")")) {
        if (this.Is("&") || this.Is("self") || (this.Is("mut") && this.Is("self", 1))) {
          var receiver = new StringBuilder();
          if (this.Accept("&"))
            receiver.Append('&');
          if (this.Accept("mut"))
            receiver.Append("mut ");
          this.Expect("self");
          // self: &Rc<Self>, a receiver with its type.
          parameters.Add(receiver.Length == 0 && this.Accept(":") ? new RsParameter("self", this.TypeText()) : new RsParameter(receiver.Append("self").ToString(), ""));
          this.Accept(",");
          continue;
        }

        var parameter = this.Identifier();
        this.Expect(":");
        parameters.Add(new RsParameter(parameter, this.TypeText()));
        this.Accept(",");
      }

      string? returns = null;
      if (this.Accept("->"))
        returns = this.TypeText();

      // The bounds of the generic parameters after the signature, T: Ord + Copy, each ended by its comma.
      var clauses = new List<string>();
      if (this.Accept("where"))
        while (!this.Is("{")) {
          var parameter = this.Identifier();
          this.Expect(":");
          clauses.Add($"{parameter}: {this.TypeText()}");
          this.Expect(",");
        }

      // A required method of a trait has no body: a declaration block.
      var body = this.Accept(";") ? new RsBlock([], null) { IsDeclaration = true } : this.Block();
      return new RsFunction(name, visibility, parameters, returns, body) { Generics = generics, Where = clauses };
    }

    /// <summary>
    /// A type as the printer writes it: the tokens up to a separator outside brackets, with a space
    /// after <c>;</c>, <c>,</c>, <c>mut</c>, <c>dyn</c>, <c>impl</c>, a lifetime and around <c>-&gt;</c>;
    /// a list of generic parameters, <c>&lt;'a&gt;</c>, ends at its closing bracket.
    /// </summary>
    private string TypeText(bool beforeFor = false) {
      var text = new StringBuilder();
      var depth = 0;
      var generics = this.Is("<");
      while (true) {
        var token = this.Peek();
        if (depth == 0 && (token.Text is "," or ")" or "=" or "{" or ";" or ">" or "|" && token.Kind == Kind.Punctuation || beforeFor && token.Text == "for" || token.Text == "where"))
          return text.ToString();

        depth += token.Text switch { "[" or "(" or "<" => 1, "]" or ")" or ">" => -1, ">>" => -2, _ => 0 };
        this.Next();
        if (token.Kind == Kind.Lifetime)
          text.Append('\'').Append(token.Text).Append(this.Is(">") || this.Is(",") ? "" : " ");
        else if (token.Text == "->")
          text.Append(" -> ");
        else
          text.Append(token.Text is "+" or "=" ? $" {token.Text} " : token.Text).Append(token.Text is ";" or "," or ":" or "mut" or "dyn" or "impl" ? " " : "");

        if (generics && depth == 0)
          return text.ToString();
      }
    }

    private RsBlock Block() {
      this.Expect("{");
      var statements = new List<RsStatement>();
      RsExpression? tail = null;
      while (!this.Accept("}")) {
        if (this.Accept("let")) {
          var mutable = this.Accept("mut");
          var name = this.Is("(") ? this.TuplePattern() : this.Identifier();
          string? type = null;
          if (this.Accept(":"))
            type = this.TypeText();

          RsExpression? value = null;
          if (this.Accept("="))
            value = this.Expression();
          this.Expect(";");
          statements.Add(new RsLet(name, mutable, type, value));
          continue;
        }

        if (this.Accept("fn")) {
          statements.Add(new RsItemStatement(this.Function("")));
          continue;
        }

        if (this.Peek().Kind == Kind.Comment) {
          statements.Add(new RsComment(this.Next().Text));
          continue;
        }

        // An expression statement that starts with a block ends with it (The Rust Reference, "Expression statements").
        var expression = this.StartsBlock() ? this.Primary() : this.Expression();
        if (this.StartsBlock(expression) && !this.Is("}") && !this.Is(";") && !this.Is("."))
          statements.Add(new RsExpressionStatement(expression));
        else if (this.Accept(";"))
          statements.Add(new RsExpressionStatement(expression));
        else if (this.Is("}"))
          tail = expression;
        else if (Printing.RustPrinter.IsBlockLike(expression))
          statements.Add(new RsExpressionStatement(expression));
        else
          throw new FormatException($"Expected ';' after an expression, found '{this.Peek().Text}'.");
      }

      return new RsBlock(statements, tail);
    }

    /// <summary>Whether the next token starts an expression that ends in a block: a control-flow expression, a block or a label.</summary>
    private bool StartsBlock() => this.Is("if") || this.Is("while") || this.Is("loop") || this.Is("for") || this.Is("match") || this.Is("{") || this.Peek().Kind == Kind.Lifetime;

    /// <summary>Whether an expression read at the start of a statement is one that ends in a block.</summary>
    private bool StartsBlock(RsExpression expression) => Printing.RustPrinter.IsBlockLike(expression);

    private RsExpression Expression(int minimum = 0) {
      var left = this.Prefix();
      while (true) {
        var token = this.Peek();
        if (token.Kind != Kind.Punctuation && token.Text != "as")
          return left;

        if (token.Text == "as") {
          if (12 < minimum)
            return left;
          this.Next();
          left = new RsCast(left, this.Identifier());
          continue;
        }

        if (token.Text is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "^=" or "&=" or "|=" or "<<=" or ">>=") {
          if (minimum > 1)
            return left;
          this.Next();
          left = new RsAssign(left, token.Text, this.Expression(1));
          continue;
        }

        if (token.Text is ".." or "..=") {
          if (minimum > 2)
            return left;
          this.Next();
          left = new RsRange(left, this.RangeEnd() ? null : this.Expression(3), token.Text == "..=");
          continue;
        }

        var precedence = token.Text switch {
          "*" or "/" or "%" => 11,
          "+" or "-" => 10,
          "<<" or ">>" => 9,
          "&" => 8,
          "^" => 7,
          "|" => 6,
          "==" or "!=" or "<" or ">" or "<=" or ">=" => 5,
          "&&" => 4,
          "||" => 3,
          _ => -1
        };
        if (precedence < 0 || precedence < minimum)
          return left;

        this.Next();
        left = new RsBinary(left, token.Text, this.Expression(precedence + 1));
      }
    }

    /// <summary>Whether a range has no end: what follows it closes an index, a group or an argument.</summary>
    private bool RangeEnd() => this.Is("]") || this.Is(")") || this.Is(",") || this.Is(";") || this.Is("{");

    private RsExpression Prefix() {
      if (this.Is("..") || this.Is("..=")) {
        var inclusive = this.Next().Text == "..=";
        return new RsRange(null, this.RangeEnd() ? null : this.Expression(3), inclusive);
      }

      if (this.Is("-") || this.Is("!") || this.Is("&") || this.Is("*")) {
        var op = this.Next().Text;
        var mutable = op == "&" && this.Accept("mut");
        var operand = this.Postfix(this.Prefix13());
        return op == "&" ? new RsReference(mutable, operand) : new RsUnary(op, operand);
      }

      return this.Postfix(this.Primary());
    }

    /// <summary>The operand of a prefix operator: another prefix operator, or a primary with its postfix forms.</summary>
    private RsExpression Prefix13() {
      if (this.Is("-") || this.Is("!") || this.Is("&") || this.Is("*")) {
        var op = this.Next().Text;
        var mutable = op == "&" && this.Accept("mut");
        var operand = this.Postfix(this.Prefix13());
        return op == "&" ? new RsReference(mutable, operand) : new RsUnary(op, operand);
      }

      return this.Primary();
    }

    private RsExpression Postfix(RsExpression expression) {
      while (true) {
        if (this.Accept(".")) {
          var name = this.Peek().Kind == Kind.Number ? this.Next().Text : this.Identifier();
          // A method with its type arguments, `cast::<u8>`, keeps them in its name.
          if (this.Is("::") && this.Is("<", 1)) {
            this.Expect("::");
            name += "::" + this.TypeText();
          }

          expression = this.Is("(") ? new RsMethodCall(expression, name, this.Arguments(")")) : new RsField(expression, name);
        } else if (this.Accept("?")) {
          expression = new RsTry(expression);
        } else if (this.Is("(") && expression is RsPath or RsCall or RsIndex or RsParenthesized) {
          expression = new RsCall(expression, this.Arguments(")"));
        } else if (this.Is("[")) {
          this.Expect("[");
          var index = this.Expression();
          this.Expect("]");
          expression = new RsIndex(expression, index);
        } else {
          return expression;
        }
      }
    }

    private List<RsExpression> Arguments(string close) {
      this.Expect(close == ")" ? "(" : "[");
      var arguments = new List<RsExpression>();
      while (!this.Accept(close)) {
        arguments.Add(this.Expression());
        this.Accept(",");
      }

      return arguments;
    }

    private RsExpression Primary() {
      var token = this.Peek();
      switch (token.Kind) {
        case Kind.Number:
          this.Next();
          return new RsLiteral(token.Text);
        case Kind.String:
          this.Next();
          return new RsString(token.Text);
        case Kind.Lifetime: {
          this.Next();
          this.Expect(":");
          return this.Is("{") ? new RsLabeledBlock(token.Text, this.Block()) : this.Loop(token.Text);
        }
      }

      if (this.Accept("(")) {
        var inner = this.Expression();
        if (this.Is(",")) {
          var elements = new List<RsExpression> { inner };
          while (this.Accept(",") && !this.Is(")"))
            elements.Add(this.Expression());
          this.Expect(")");
          return new RsTuple(elements);
        }

        this.Expect(")");
        return new RsParenthesized(inner);
      }

      if (this.Accept("unsafe"))
        return new RsUnsafe(this.Block());

      if (this.Is("move") || this.Is("|") || this.Is("||"))
        return this.Closure();
      if (this.Is("<")) {
        // A qualified path, <[i32]>::to_vec.
        var qualified = new StringBuilder(this.TypeText());
        while (this.Accept("::"))
          qualified.Append("::").Append(this.Identifier());
        return new RsPath(qualified.ToString());
      }
      if (this.Is("["))
        return this.Array();
      if (this.Is("{"))
        return new RsBlockExpression(this.Block());
      if (this.Is("if"))
        return this.If();
      if (this.Is("while") || this.Is("loop") || this.Is("for"))
        return this.Loop(null);
      if (this.Accept("match")) {
        var scrutinee = this.Header();
        this.Expect("{");
        var arms = new List<RsMatchArm>();
        while (!this.Accept("}")) {
          var patterns = new List<string> { "" };
          while (!this.Is("=>")) {
            var part = this.Next().Text;
            if (part == "|")
              patterns.Add("");
            else
              patterns[^1] += part;
          }

          this.Expect("=>");
          var body = this.Is("{") ? new RsBlockExpression(this.Block()) : this.Expression();
          this.Accept(",");
          arms.Add(new RsMatchArm(patterns, body));
        }

        return new RsMatch(scrutinee, arms);
      }

      if (this.Accept("break")) {
        string? label = null;
        if (this.Peek().Kind == Kind.Lifetime)
          label = this.Next().Text;
        return new RsBreak(label, this.Is(";") || this.Is("}") || this.Is(",") ? null : this.Expression());
      }

      if (this.Accept("continue"))
        return new RsContinue(this.Peek().Kind == Kind.Lifetime ? this.Next().Text : null);
      if (this.Accept("return"))
        return new RsReturn(this.Is(";") || this.Is("}") || this.Is(",") ? null : this.Expression());
      if (token.Text is "true" or "false") {
        this.Next();
        return new RsLiteral(token.Text);
      }

      if (token.Kind == Kind.Identifier) {
        var path = new StringBuilder(this.Next().Text);
        // A segment is a name or the type arguments of a call, def::<i32>.
        while (this.Accept("::"))
          path.Append("::").Append(this.Is("<") ? this.TypeText() : this.Identifier());
        if (this.Is("{") && !this._noStruct) {
          this.Expect("{");
          var fields = new List<RsFieldValue>();
          while (!this.Accept("}")) {
            var field = this.Identifier();
            fields.Add(new RsFieldValue(field, this.Accept(":") ? this.Expression() : new RsPath(field)));
            this.Accept(",");
          }

          return new RsStructLiteral(path.ToString(), fields);
        }

        if (this.Is("!") && this.Is("(", 1)) {
          this.Next();
          return new RsMacro(path.ToString(), this.Arguments(")"));
        }

        if (this.Is("!") && this.Is("[", 1)) {
          this.Next();
          return new RsMacro(path.ToString(), [this.Array()]) { Bracketed = true };
        }

        return new RsPath(path.ToString());
      }

      throw new FormatException($"Unexpected '{token.Text}'.");
    }

    /// <summary><c>move |a: T, b: U| body</c>: a closure with typed parameters, its body an expression or a block.</summary>
    private RsClosure Closure() {
      var move = this.Accept("move");
      var parameters = new List<RsParameter>();
      if (!this.Accept("||")) {
        this.Expect("|");
        while (!this.Accept("|")) {
          var name = (this.Accept("&") ? "&" : "") + this.Identifier();
          parameters.Add(new RsParameter(name, this.Accept(":") ? this.TypeText() : ""));
          this.Accept(",");
        }
      }

      return new RsClosure(move, parameters, this.Expression());
    }

    /// <summary><c>[a, b, c]</c>, or <c>[value; count]</c>.</summary>
    private RsArray Array() {
      this.Expect("[");
      if (this.Accept("]"))
        return new RsArray([]);

      var first = this.Expression();
      if (this.Accept(";")) {
        var count = this.Expression();
        this.Expect("]");
        return new RsArray([first]) { Count = count };
      }

      var elements = new List<RsExpression> { first };
      this.Accept(",");
      while (!this.Accept("]")) {
        elements.Add(this.Expression());
        this.Accept(",");
      }

      return new RsArray(elements);
    }

    /// <summary>Whether a struct literal cannot stand here: in the condition of an <c>if</c> or a <c>while</c>, a scrutinee, an iterator.</summary>
    private bool _noStruct;

    /// <summary>An expression where a struct literal cannot stand, as The Rust Reference forbids one before a block.</summary>
    private RsExpression Header() {
      var outer = this._noStruct;
      this._noStruct = true;
      try {
        if (this.Accept("let")) {
          var pattern = new StringBuilder();
          while (!this.Is("="))
            pattern.Append(this.Next().Text);
          this.Expect("=");
          return new RsLetCondition(pattern.ToString(), this.Expression());
        }

        return this.Expression();
      } finally {
        this._noStruct = outer;
      }
    }

    private RsIf If() {
      this.Expect("if");
      var condition = this.Header();
      var then = this.Block();
      RsExpression? otherwise = null;
      if (this.Accept("else"))
        otherwise = this.Is("if") ? this.If() : new RsBlockExpression(this.Block());
      return new RsIf(condition, then, otherwise);
    }

    private RsExpression Loop(string? label) {
      if (this.Accept("loop"))
        return new RsLoop(label, this.Block());
      if (this.Accept("while")) {
        var condition = this.Header();
        return new RsWhile(label, condition, this.Block());
      }

      this.Expect("for");
      var pattern = new StringBuilder();
      while (!this.Is("in")) {
        // A pattern of names, references and tuples: (place, &digit), with rustfmt's space after a comma and a mut.
        var part = this.Next().Text;
        pattern.Append(part).Append(part is "," or "mut" ? " " : "");
      }

      this.Expect("in");
      var iterator = this.Header();
      return new RsFor(label, pattern.ToString(), iterator, this.Block());
    }

  }

}
