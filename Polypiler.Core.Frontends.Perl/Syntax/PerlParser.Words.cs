using System;
using System.Collections.Generic;
using Polypiler.Core.Frontends.Perl.Ast;

namespace Polypiler.Core.Frontends.Perl.Syntax;

internal sealed partial class PerlParser {

  private bool IsKnownSub(string name) => this._subs.ContainsKey(this.Qualify(name.StartsWith("::", StringComparison.Ordinal) ? "main" + name : name));

  private KnownSub? FindSub(string name) {
    var qualified = this.Qualify(name.StartsWith("::", StringComparison.Ordinal) ? "main" + name : name);
    if (this._subs.TryGetValue(qualified, out var known))
      return known;
    if (!name.Contains("::", StringComparison.Ordinal))
      return null;
    var split = qualified.LastIndexOf("::", StringComparison.Ordinal);
    var module = qualified[..split];
    return this._usedModules.Contains(module) && PerlPragmas.ExportedPrototype(module, qualified[(split + 2)..]) is { } prototype ? new KnownSub(prototype, true) : null;
  }

  /// <summary>A term that starts with a word: a keyword, a builtin, a call, a class name, a bareword.</summary>
  private PerlToken? _lastWord;

  private PerlExpr ParseWord(PerlToken token) {
    this._lastWord = token;
    var line = token.Line;
    var name = token.Text;
    if (token.AutoQuoted)
      return new PerlString(name) { Line = line, Bareword = true };

    var core = false;
    if (name.StartsWith("CORE::", StringComparison.Ordinal) && !name.StartsWith("CORE::GLOBAL::", StringComparison.Ordinal) && !name[6..].Contains("::", StringComparison.Ordinal)) {
      name = name[6..];
      core = true;
      if (!PerlBuiltins.IsBuiltin(name))
        throw this.Error($"CORE::{name} is not a keyword", line);
      if (name is "given" or "when" or "default" or "break")
        throw new PerlUnsupportedException($"the switch feature (CORE::{name})", line);
      if (name is "my" or "our" or "state" or "local")
        return this.ParseDeclaration(name, line);
    }

    if (!core && this.ParseKeywordTerm(name, line) is { } keyword)
      return keyword;

    var known = core ? null : this.FindSub(name);
    if (PerlBuiltins.IsBuiltin(name) && (core || known is null || PerlBuiltins.IsStrong(name)) && this.IsActiveBuiltin(name, core))
      return this.ParseBuiltin(name, line);

    if (known is not null)
      return this.ParseSubCall(name, known, line);

    return this.ParseBareword(token, name, line);
  }

  /// <summary>Whether a builtin is a keyword where it stands: feature keywords only with their feature.</summary>
  private bool IsActiveBuiltin(string name, bool core) {
    if (core)
      return true;
    return name switch {
      "say" => this.Feature("say"),
      "state" => this.Feature("state"),
      "fc" => this.Feature("fc"),
      "evalbytes" => this.Feature("evalbytes"),
      "__SUB__" => this.Feature("current_sub"),
      "any" => this.Feature("keyword_any"),
      "all" => this.Feature("keyword_all"),
      "try" or "catch" or "finally" => this.Feature("try"),
      "defer" => this.Feature("defer"),
      "given" or "when" or "default" or "break" => this.Feature("switch"),
      "isa" => this.Feature("isa"),
      "class" or "field" or "method" or "ADJUST" or "__CLASS__" => this.Feature("class"),
      _ => true
    };
  }

  private PerlExpr? ParseKeywordTerm(string name, int line) {
    switch (name) {
      case "__PACKAGE__":
        return new PerlString(this.Package) { Line = line };
      case "__FILE__":
        return new PerlString(this._file) { Line = line };
      case "__LINE__":
        return new PerlNumber(PerlNumeric.Of((long)line)) { Line = line };
      case "my" or "our" or "local":
        return this.ParseDeclaration(name, line);
      case "state" when this.Feature("state"):
        return this.ParseDeclaration(name, line);
      case "sub":
        return new PerlAnonSub(this.ParseAnonSub()) { Line = line };
      case "do":
        return this.ParseDo(line);
      case "eval":
        return this.ParseEval(line);
      case "return":
        return new PerlJump("return", null, this.CanStartTerm(this.PeekTerm()) ? this.ParseCommaList() : null) { Line = line };
      case "last" or "next" or "redo" or "dump":
        return this.ParseLoopJump(name, line);
      case "goto":
        return new PerlJump("goto", null, this.ParseGotoTarget()) { Line = line };
      case "not":
        return new PerlUnary("not", this.CanStartTerm(this.PeekTerm()) ? this.ParseCommaList() : null) { Line = line };
      case "if" or "unless" or "while" or "until" or "foreach" or "for" or "else" or "elsif" or "continue" or "package" or "use" or "no" or "format":
        throw this.Error($"unexpected '{name}'", line);
    }
    return null;
  }

  private PerlSub ParseAnonSub() => this.ParseSubRest("", named: false) is var sub && sub.Body is not null && !(System.Linq.Enumerable.Contains(sub.Attributes, "const") && sub.Body.Statements is [PerlExpressionStatement { Modifier: null, Expression: var value }] && IsConstantExpression(value)) ? this.RegisterAnon(sub) : sub;

  private PerlSub RegisterAnon(PerlSub sub) {
    this._unit.Pad.Add(sub.Unit);
    return sub;
  }

  private PerlExpr ParseDo(int line) {
    if (this.PeekTerm().Is("{")) {
      var block = this.ParseBlock();
      return new PerlDoBlock(block) { Line = line };
    }
    return new PerlCall("do", this.ParseUnaryArguments()) { Line = line, Builtin = true };
  }

  private PerlExpr ParseEval(int line) {
    if (this.PeekTerm().Is("{")) {
      var block = this.ParseBlock();
      return new PerlEvalBlock(block) { Line = line };
    }
    return new PerlCall("eval", this.ParseUnaryArguments()) { Line = line, Builtin = true };
  }

  private PerlExpr ParseLoopJump(string keyword, int line) {
    var token = this.PeekTerm();
    if (token.Kind == PerlTokenKind.Word && !token.AutoQuoted && this.CanStartTerm(token) && !PerlBuiltins.IsBuiltin(token.Text) && !this.IsKnownSub(token.Text) && !this.FollowedByCallParen(token)) {
      this.NextTerm();
      return new PerlJump(keyword, token.Text, null) { Line = line };
    }
    if (this.CanStartTerm(token) && !token.Is("{"))
      return new PerlJump(keyword, null, this.ParseUnaryOperand()) { Line = line };
    return new PerlJump(keyword, null, null) { Line = line };
  }

  private PerlExpr? ParseGotoTarget() {
    var token = this.PeekTerm();
    if (token.Kind == PerlTokenKind.Word && !token.AutoQuoted && !PerlBuiltins.IsBuiltin(token.Text) && !this.IsKnownSub(token.Text) && !this.FollowedByCallParen(token)) {
      this.NextTerm();
      return new PerlString(token.Text) { Line = token.Line, Bareword = true };
    }
    return this.CanStartTerm(token) ? this.ParseUnaryOperand() : null;
  }

  // ---- declarations ----

  private PerlExpr ParseDeclaration(string keyword, int line) {
    if (keyword == "local") {
      var target = this.ParseUnary();
      return new PerlLocal(target) { Line = line };
    }
    var kind = keyword switch { "my" => PerlDeclarator.My, "our" => PerlDeclarator.Our, _ => PerlDeclarator.State };
    var token = this.PeekTerm();
    if (token.Kind == PerlTokenKind.Word && token.Text == "sub")
      throw new PerlUnsupportedException($"lexical subs ({keyword} sub)", line);
    if (token.Is("\\"))
      throw new PerlUnsupportedException("declared references", line);
    if (token.Kind == PerlTokenKind.Word && !token.AutoQuoted) {
      this.NextTerm();
      token = this.PeekTerm();
    }
    var variables = new List<PerlVariable>();
    var parenthesized = false;
    if (token.Is("(")) {
      this.NextTerm();
      parenthesized = true;
      while (!this.PeekTerm().Is(")")) {
        if (this.AcceptTerm(","))
          continue;
        var item = this.NextTerm();
        if (item.Kind == PerlTokenKind.Word && item.Text == "undef") {
          variables.Add(new PerlVariable('$', "", null) { Line = item.Line });
        } else {
          variables.Add(this.DeclareVariable(item, kind));
        }
        if (!this.AcceptOperator(",") && !this.AcceptOperator("=>"))
          break;
      }
      this.ExpectOperator(")");
    } else {
      variables.Add(this.DeclareVariable(this.NextTerm(), kind));
    }
    var attributes = new List<string>();
    this.ParseAttributes(attributes);
    return new PerlDeclaration(kind, variables, parenthesized) { Line = line };
  }

  private PerlVariable DeclareVariable(PerlToken token, PerlDeclarator kind) {
    if (token.Kind == PerlTokenKind.Cast && token.Text == "\\")
      throw new PerlUnsupportedException("declared references", token.Line);
    if (token.Kind != PerlTokenKind.Variable || token.Sigil is not ('$' or '@' or '%'))
      throw this.Unexpected(token, "a variable to declare");
    if (!IsLexicalName(token.Text) && !(kind == PerlDeclarator.Our && token.Text.Length > 0))
      throw this.Error($"Can't use global {token.Sigil}{token.Text} in \"{kind.ToString().ToLowerInvariant()}\"", token.Line);
    var lexical = this.Declare(token.Sigil, token.Text, kind);
    return new PerlVariable(token.Sigil, token.Text, lexical) { Line = token.Line };
  }

  // ---- builtins ----

  private PerlExpr ParseBuiltin(string name, int line) {
    switch (name) {
      case "print" or "printf" or "say" or "exec" or "system":
        return this.ParsePrintLike(name, line);
      case "sort" or "map" or "grep" or "any" or "all":
        return this.ParseListWithBlock(name, line);
      case "require":
        return this.ParseRequire(line);
      case "defined" or "undef" or "exists" or "delete" or "scalar" or "pos" or "glob" or "study" or "prototype" or "lock":
        return new PerlCall(name, this.ParseUnaryArguments(name)) { Line = line, Builtin = true, Parenthesized = this._lastParenthesized };
      case "split" or "chomp" or "chop" or "select" or "return" or "local":
        return new PerlCall(name, this.ParseListArguments()) { Line = line, Builtin = true, Parenthesized = this._lastParenthesized };
      case "__SUB__" or "__CLASS__" or "break" or "continue" or "wait" or "time" or "times" or "wantarray" or "fork" or "getppid" or "getlogin":
        this.SkipEmptyParens();
        return new PerlCall(name, []) { Line = line, Builtin = true };
      case "given" or "when" or "default" or "try" or "catch" or "finally" or "defer" or "class" or "field" or "method" or "ADJUST" or "isa"
        or "__DATA__" or "__END__" or "format":
        throw this.Error($"unexpected '{name}'", line);
      case "BEGIN" or "END" or "INIT" or "CHECK" or "UNITCHECK" or "AUTOLOAD" or "DESTROY":
        return this.ParseBareword(this._lastWord!, name, line);
    }
    var shape = PerlBuiltins.Shape(name);
    var arguments = shape switch {
      PerlBuiltinShape.Term => this.ParseTermArguments(),
      PerlBuiltinShape.Unary => this.ParseUnaryArguments(name),
      _ => this.ParseListArguments()
    };
    return new PerlCall(name, arguments) { Line = line, Builtin = true, Parenthesized = this._lastParenthesized };
  }

  private bool _lastParenthesized;

  private void SkipEmptyParens() {
    if (this._lexer.At(this._lexer.SkipSpace(this._lexer.Position)) == '(' && this._lexer.At(this._lexer.SkipSpace(this._lexer.SkipSpace(this._lexer.Position) + 1)) == ')') {
      this.NextTerm();
      this.NextTerm();
    }
  }

  private List<PerlExpr> ParseTermArguments() {
    this._lastParenthesized = false;
    if (this._lexer.At(this._lexer.SkipSpace(this._lexer.Position)) == '(') {
      this.NextTerm();
      this._lastParenthesized = true;
      return this.ParseParenArguments();
    }
    return [];
  }

  private List<PerlExpr> ParseUnaryArguments(string? name = null) {
    this._lastParenthesized = false;
    if (name is "shift" or "pop" or "getc" or "umask" or "undef") {
      var at = this._lexer.SkipSpace(this._lexer.Position);
      if (this._lexer.At(at) == '/' && this._lexer.At(at + 1) == '/')
        return [];
    }
    var token = this.PeekTerm();
    if (token.Is("(")) {
      this.NextTerm();
      this._lastParenthesized = true;
      return this.ParseParenArguments();
    }
    if (!this.CanStartTerm(token))
      return [];
    return [this.ParseUnaryOperand()];
  }

  private List<PerlExpr> ParseListArguments() {
    this._lastParenthesized = false;
    var token = this.PeekTerm();
    if (token.Is("(")) {
      this.NextTerm();
      this._lastParenthesized = true;
      return this.ParseParenArguments();
    }
    if (!this.CanStartTerm(token))
      return [];
    return Items(this.ParseCommaList());
  }

  private static List<PerlExpr> Items(PerlExpr expression)
    => expression is PerlList { Parenthesized: false } list ? [.. list.Items] : [expression];

  private PerlExpr ParseRequire(int line) {
    var token = this.PeekTerm();
    if (token.Kind == PerlTokenKind.Word && !token.AutoQuoted && !PerlBuiltins.IsBuiltin(token.Text) && !this.IsKnownSub(token.Text)) {
      this.NextTerm();
      return new PerlCall("require", [new PerlString(token.Text) { Line = token.Line, Bareword = true }]) { Line = line, Builtin = true };
    }
    return new PerlCall("require", this.ParseUnaryArguments()) { Line = line, Builtin = true };
  }

  /// <summary><c>print</c>, <c>printf</c>, <c>say</c>, <c>exec</c>, <c>system</c>: an optional file handle, then a list.</summary>
  private PerlExpr ParsePrintLike(string name, int line) {
    var parenthesized = false;
    if (this.PeekTerm().Is("(")) {
      this.NextTerm();
      parenthesized = true;
    }
    PerlExpr? handle = null;
    var token = this.PeekTerm();
    if (token.Is("{")) {
      var block = this.ParseBlock();
      handle = new PerlDoBlock(block) { Line = token.Line };
    } else if (token.Kind == PerlTokenKind.Word && !token.AutoQuoted && this.IsHandleWord(token)) {
      this.NextTerm();
      handle = new PerlString(token.Text) { Line = token.Line, Bareword = true };
    } else if (token.Kind == PerlTokenKind.Variable && token.Sigil == '$' && this.IsHandleVariable(token)) {
      this.NextTerm();
      handle = this.Variable('$', token.Text, token.Line);
    }
    List<PerlExpr> arguments;
    if (parenthesized) {
      arguments = this.ParseParenArguments();
    } else {
      arguments = this.CanStartTerm(this.PeekTerm()) ? Items(this.ParseCommaList()) : [];
    }
    return new PerlCall(name, arguments) { Line = line, Builtin = true, Handle = handle, Parenthesized = parenthesized };
  }

  private bool IsHandleWord(PerlToken token) {
    if (PerlBuiltins.IsBuiltin(token.Text) && this.IsActiveBuiltin(token.Text, false) || this.IsKnownSub(token.Text) || token.Text.StartsWith("CORE::", StringComparison.Ordinal))
      return false;
    var after = this._lexer.SkipSpace(token.End);
    var c = this._lexer.At(after);
    if (c is ';' or ')' or '\0' or '}')
      return IsUpperIdentifier(token.Text);
    if (c is '(' or ',' or '=' or '.' or '?' or ':' or '[' or '{' || c == '-' && this._lexer.At(after + 1) == '>')
      return false;
    if (after == token.End)
      return false;
    return c is '"' or '\'' or '`' or '$' or '@' or '\\' or '<' or '-' or '+' or '%' or '&' || char.IsAsciiLetterOrDigit(c) || c == '_';
  }

  private static bool IsUpperIdentifier(string text) {
    foreach (var c in text)
      if (!(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '_' or ':'))
        return false;
    return true;
  }

  private bool IsHandleVariable(PerlToken token) {
    if (!IsLexicalName(token.Text) && !char.IsAsciiLetter(token.Text[0]) && token.Text[0] != '_')
      return false;
    var end = token.End;
    var after = this._lexer.SkipSpace(end);
    if (after == end)
      return false;
    var c = this._lexer.At(after);
    var c1 = this._lexer.At(after + 1);
    if (c is '"' or '\'' or '`' or '$' or '@' or '\\' or '(')
      return true;
    if (char.IsAsciiDigit(c))
      return true;
    if (c == '<')
      return c1 == '<' ? PerlLexer.IsIdentifierStart(this._lexer.At(after + 2), false) || this._lexer.At(after + 2) is '"' or '\'' or '~' : c1 is '$' or '>' || PerlLexer.IsIdentifierStart(c1, false);
    if (PerlLexer.IsIdentifierStart(c, this._scope.Utf8)) {
      var wordEnd = this._lexer.ScanIdentifier(after);
      var word = this._lexer.Text.Value[after..wordEnd];
      return word is not ("x" or "lt" or "gt" or "le" or "ge" or "eq" or "ne" or "cmp" or "and" or "or" or "xor" or "if" or "unless"
        or "while" or "until" or "for" or "foreach" or "isa");
    }
    return false;
  }

  /// <summary><c>sort</c>, <c>map</c>, <c>grep</c> (and <c>any</c>/<c>all</c>): a block or a sub (sort), or a first expression and a comma.</summary>
  private PerlExpr ParseListWithBlock(string name, int line) {
    var parenthesized = false;
    if (this.PeekTerm().Is("(")) {
      this.NextTerm();
      parenthesized = true;
    }
    PerlBlock? block = null;
    PerlExpr? handle = null;
    var arguments = new List<PerlExpr>();
    var token = this.PeekTerm();
    if (token.Is("{") && (name is "sort" or "any" or "all" || !this.LooksLikeAnonHash(token.End, statement: false))) {
      block = this.InScope(() => this.ParseBlock());
      this.AcceptOperator(",");
    } else if (name == "sort" && token.Kind == PerlTokenKind.Word && !token.AutoQuoted && !PerlBuiltins.IsBuiltin(token.Text) && this.StartsTermAfter(token.End)) {
      this.NextTerm();
      handle = new PerlString(token.Text) { Line = token.Line, Bareword = true };
    } else if (name == "sort" && token.Kind == PerlTokenKind.Variable && token.Sigil == '$' && this.StartsTermAfter(token.End)) {
      this.NextTerm();
      handle = this.Variable('$', token.Text, token.Line);
    } else if (name is "map" or "grep" && this.CanStartTerm(token) && !token.Is(")")) {
      arguments.Add(this.ParseAssignment());
      if (!this.AcceptOperator(",") && !this.AcceptOperator("=>")) {
        if (!(parenthesized && this.PeekOperator().Is(")")))
          throw this.Unexpected(this.PeekOperator(), "',' after the first argument of " + name);
      }
    }
    if (parenthesized) {
      if (!this.PeekTerm().Is(")")) {
        var rest = this.ParseExpression();
        arguments.AddRange(Items(rest));
      }
      this.ExpectOperator(")");
    } else if (this.CanStartTerm(this.PeekTerm())) {
      arguments.AddRange(Items(this.ParseCommaList()));
    }
    return new PerlCall(name, arguments) { Line = line, Builtin = true, Block = block, Handle = handle, Parenthesized = parenthesized };
  }

  private bool StartsTermAfter(int position) {
    var after = this._lexer.SkipSpace(position);
    if (after == position)
      return false;
    var c = this._lexer.At(after);
    return c is '$' or '@' or '(' or '%' or '"' or '\'' || char.IsAsciiDigit(c) || PerlLexer.IsIdentifierStart(c, this._scope.Utf8) && !(c == 'x' && !PerlLexer.IsWordChar(this._lexer.At(after + 1)));
  }

  // ---- user subs and barewords ----

  private PerlExpr ParseSubCall(string name, KnownSub known, int line) {
    var qualified = name.StartsWith("::", StringComparison.Ordinal) ? "main" + name : name;
    if (this.PeekOperator().Is("->") && !this.PeekOperator().SpaceBefore && known.Prototype != "")
      return new PerlString(qualified) { Line = line, Bareword = true };
    var prototype = known.Prototype;
    var token = this.PeekTerm();
    if (token.Is("(")) {
      this.NextTerm();
      return new PerlCall(qualified, this.ParseParenArguments()) { Line = line, Parenthesized = true };
    }
    if (prototype == "")
      return new PerlCall(qualified, []) { Line = line };
    if (prototype is not null && prototype.StartsWith('&') && token.Is("{")) {
      var block = this.ParseAnonBlockArgument();
      var arguments = new List<PerlExpr> { block };
      if (prototype.Length > 1 && this.CanStartTerm(this.PeekTerm()))
        arguments.AddRange(Items(this.ParseCommaList()));
      return new PerlCall(qualified, arguments) { Line = line };
    }
    if (prototype is null && known.Imported && token.Is("{"))
      throw new PerlUnsupportedException($"a call of {name} with a block, whose prototype is not known", line);
    if (prototype is not null && PerlBuiltins.ShapeOf(prototype) == PerlBuiltinShape.Unary && !prototype.StartsWith('&')) {
      if (!this.CanStartTerm(token))
        return new PerlCall(qualified, []) { Line = line };
      return new PerlCall(qualified, [this.ParseUnaryOperand()]) { Line = line };
    }
    if (!this.CanStartTerm(token))
      return new PerlCall(qualified, []) { Line = line };
    return new PerlCall(qualified, Items(this.ParseCommaList())) { Line = line };
  }

  private PerlExpr ParseAnonBlockArgument() {
    var line = this.Line;
    var saved = this._unit;
    var unit = new PerlUnit("", saved);
    this._unit = unit;
    PerlBlock body;
    try {
      body = this.InScope(() => this.ParseBlock());
    } finally {
      this._unit = saved;
    }
    var sub = new PerlSub(null, null, [], body, unit) { Line = line };
    this._unit.Pad.Add(unit);
    return new PerlAnonSub(sub) { Line = line };
  }

  private PerlExpr ParseBareword(PerlToken token, string name, int line) {
    var qualified = name.StartsWith("::", StringComparison.Ordinal) ? "main" + name : name;
    if (name.EndsWith("::", StringComparison.Ordinal))
      return new PerlString(name[..^2]) { Line = line, Bareword = true };
    var next = this.PeekOperator();
    if (next.Is("->"))
      return new PerlString(qualified) { Line = line, Bareword = true };
    if (next.Is("(") && !next.SpaceBefore || next.Is("(")) {
      this.NextOperator();
      return new PerlCall(qualified, this.ParseParenArguments()) { Line = line, Parenthesized = true };
    }
    if (next.Is("=>") || next.Is(","))
      return new PerlString(qualified) { Line = line, Bareword = true };

    var after = this._lexer.SkipSpace(token.End);
    var c = this._lexer.At(after);
    var spaced = after > token.End;
    var isQualified = name.Contains("::", StringComparison.Ordinal);

    // What follows is an operator however the word is defined.
    if (!spaced || this.IsOperatorAfterBareword(c, after))
      return new PerlString(qualified) { Line = line, Bareword = true };

    // An operator after a word perl does not know as a sub, the start of a term after one it knows.
    var ambiguous = c is '-' or '+' or '/' or '<' or '*' or '%' or '&' || c == '.' && char.IsAsciiDigit(this._lexer.At(after + 1));
    if (ambiguous) {
      if (this.MayImport || isQualified)
        throw new PerlUnsupportedException($"'{token.Text} {c}', whose parse depends on whether a module or BEGIN code defines {token.Text}", line);
      return new PerlString(qualified) { Line = line, Bareword = true };
    }

    // An indirect object: `new Foo(...)`, `new Foo`.
    var termToken = this.PeekTerm();
    if (termToken.Kind == PerlTokenKind.Word && !termToken.AutoQuoted && this.IndirectClass(termToken) && !this.MayImport && !isQualified) {
      this.NextTerm();
      var className = termToken.Text.EndsWith("::", StringComparison.Ordinal) ? termToken.Text[..^2] : termToken.Text;
      var invocant = new PerlString(className) { Line = termToken.Line, Bareword = true };
      var (arguments, parenthesized) = this.ParseMethodArguments();
      if (!parenthesized && this.CanStartTerm(this.PeekTerm()))
        arguments = Items(this.ParseCommaList());
      return new PerlMethodCall(invocant, qualified, null, arguments) { Line = line, Parenthesized = parenthesized };
    }

    // An indirect object held in a variable: `new $class(...)`, `method $obj LIST`.
    if (termToken.Kind == PerlTokenKind.Variable && termToken.Sigil == '$' && !isQualified && this.IndirectInvocant(termToken) is var indirect && (indirect || !this.MayImport)) {
      this.NextTerm();
      var invocant = this.Variable('$', termToken.Text, termToken.Line);
      var (arguments, parenthesized) = this.ParseMethodArguments();
      if (!parenthesized && this.CanStartTerm(this.PeekTerm()) && indirect)
        arguments = Items(this.ParseCommaList());
      return new PerlMethodCall(invocant, qualified, null, arguments) { Line = line, Parenthesized = parenthesized };
    }

    if (!this.CanStartTerm(termToken))
      return new PerlString(qualified) { Line = line, Bareword = true };
    if (isQualified && this._usedModules.Contains(qualified[..qualified.LastIndexOf("::", StringComparison.Ordinal)]))
      return new PerlCall(qualified, Items(this.ParseCommaList())) { Line = line };
    if (this.MayImport || isQualified)
      throw new PerlUnsupportedException($"'{name}' followed by a term: a call only if a module or BEGIN code defines {name}", line);
    if (termToken.Is("{") || termToken.Is("[") || termToken.Is("("))
      throw new PerlUnsupportedException($"the bareword {name} followed by '{termToken.Text}'", line);
    throw this.Error($"bareword \"{name}\" followed by a term (\"{Near(termToken)}\")", line);
  }

  /// <summary>
  /// Whether <c>word $var</c> can only be an indirect method call: the variable is followed by a
  /// parenthesized list or a term, which no call of a sub <c>word</c> could take.
  /// </summary>
  private bool IndirectInvocant(PerlToken variable) {
    var after = this._lexer.SkipSpace(variable.End);
    if (after == variable.End)
      return false;
    var c = this._lexer.At(after);
    if (c is '(' or '"' or '\'' or '$' or '@' || char.IsAsciiDigit(c))
      return true;
    if (PerlLexer.IsIdentifierStart(c, this._scope.Utf8)) {
      var end = this._lexer.ScanIdentifier(after);
      return this._lexer.Text.Value[after..end] is not ("x" or "lt" or "gt" or "le" or "ge" or "eq" or "ne" or "cmp" or "and" or "or" or "xor" or "if"
        or "unless" or "while" or "until" or "for" or "foreach" or "isa");
    }
    return false;
  }

  private bool IndirectClass(PerlToken token) {
    var text = token.Text;
    if (PerlBuiltins.IsBuiltin(text) || this.IsKnownSub(text) || _statementKeywords.Contains(text) || text is "isa")
      return false;
    var after = this._lexer.SkipSpace(token.End);
    var c = this._lexer.At(after);
    if (c is '(' or ';' or ')' or ',' or '}' or '\0')
      return true;
    return after > token.End && (c is '$' or '@' or '"' or '\'' || char.IsAsciiDigit(c) || PerlLexer.IsIdentifierStart(c, this._scope.Utf8) && !this.IsOperatorAfterBareword(c, after));
  }

  /// <summary>Whether what follows a bareword is an operator perl would read as one after a word it does not know as a sub.</summary>
  private bool IsOperatorAfterBareword(char c, int at) {
    var c1 = this._lexer.At(at + 1);
    switch (c) {
      case '\0' or ';' or ')' or ']' or '}' or ',' or ':' or '=' or '.' or '|' or '^' or '>' or '?':
        return true;
      case '!' when c1 is '=' or '~':
        return true;
      case '~' when c1 == '~':
        return true;
      case '<' when c1 is '=' or '<' && this._lexer.At(at + 2) is not ('"' or '\'' or '~') && !PerlLexer.IsIdentifierStart(this._lexer.At(at + 2), false):
        return true;
      case '&' when c1 == '&':
        return true;
      case '*' when c1 == '*':
        return true;
      case '|' or '/' when c1 == c:
        return true;
    }
    if (PerlLexer.IsIdentifierStart(c, false)) {
      var end = this._lexer.ScanIdentifier(at);
      var word = this._lexer.Text.Value[at..end];
      return word is "x" or "lt" or "gt" or "le" or "ge" or "eq" or "ne" or "cmp" or "and" or "or" or "xor" or "if" or "unless"
        or "while" or "until" or "for" or "foreach" || word == "isa" && this.Feature("isa");
    }
    return false;
  }

  /// <summary>
  /// Whether the <c>{</c> before <paramref name="after"/> opens an anonymous hash rather than a block,
  /// guessed as perl guesses (perlfunc, map; perlref): an empty pair of braces is a hash after
  /// <c>map</c> and a block at a statement; a first word or string followed by <c>=&gt;</c>, or by a
  /// comma unless the word is lower case, makes a hash.
  /// </summary>
  private bool LooksLikeAnonHash(int after, bool statement) {
    var at = this._lexer.SkipSpace(after);
    var c = this._lexer.At(at);
    if (c == '}')
      return !statement;
    var first = c;
    var t = at;
    var quoted = false;
    if (c is '\'' or '"' or '`') {
      ++t;
      while (t < this._lexer.End && this._lexer.At(t) != c) {
        if (this._lexer.At(t) == '\\')
          ++t;
        ++t;
      }
      ++t;
      quoted = true;
    } else if (c == 'q' && !PerlLexer.IsWordChar(this._lexer.At(t + 1)) || c == 'q' && this._lexer.At(t + 1) is 'q' && !PerlLexer.IsWordChar(this._lexer.At(t + 2))) {
      var open = this._lexer.SkipSpace(t + (this._lexer.At(t + 1) == 'q' ? 2 : 1));
      if (this._lexer.At(open) is not ('=' or ',' or '\0' or '}')) {
        try {
          var (_, end) = this._lexer.ScanDelimited(open);
          t = end;
          quoted = true;
        } catch (PerlSyntaxException) {
          return false;
        }
      }
    }
    if (!quoted) {
      if (!PerlLexer.IsWordChar(c, this._scope.Utf8))
        return false;
      while (PerlLexer.IsWordChar(this._lexer.At(t), this._scope.Utf8))
        ++t;
    }
    t = this._lexer.SkipSpace(t);
    var next = this._lexer.At(t);
    if (next == '=' && this._lexer.At(t + 1) == '>')
      return true;
    return next == ',' && (quoted || !char.IsAsciiLetterLower(first));
  }

}
