using System;
using System.Collections.Generic;
using Polypiler.Core.Frontends.Perl.Ast;

namespace Polypiler.Core.Frontends.Perl.Syntax;

internal sealed partial class PerlParser {

  // The binary operators of perlop by precedence, lowest first, from the range operator up.
  private const int RangeLevel = 7;
  private const int OrOrLevel = 8;
  private const int ShiftLevel = 16;
  private const int BindLevel = 19;

  private static readonly Dictionary<string, int> _binaryLevels = new(StringComparer.Ordinal) {
    ["||"] = 8, ["//"] = 8, ["^^"] = 8,
    ["&&"] = 9,
    ["|"] = 10, ["^"] = 10, ["|."] = 10, ["^."] = 10,
    ["&"] = 11, ["&."] = 11,
    ["=="] = 12, ["!="] = 12, ["<=>"] = 12, ["eq"] = 12, ["ne"] = 12, ["cmp"] = 12, ["~~"] = 12,
    ["<"] = 13, [">"] = 13, ["<="] = 13, [">="] = 13, ["lt"] = 13, ["gt"] = 13, ["le"] = 13, ["ge"] = 13,
    ["isa"] = 14,
    ["<<"] = 16, [">>"] = 16,
    ["+"] = 17, ["-"] = 17, ["."] = 17,
    ["*"] = 18, ["/"] = 18, ["%"] = 18, ["x"] = 18,
    ["=~"] = 19, ["!~"] = 19
  };

  private static readonly HashSet<string> _assignments = new(StringComparer.Ordinal) {
    "=", "**=", "+=", "-=", "*=", "/=", ".=", "%=", "x=", "&=", "|=", "^=", "<<=", ">>=", "&&=", "||=", "//=", "&.=", "|.=", "^.=", "^^="
  };

  /// <summary>An expression, down to the lowest precedence: <c>or</c>, <c>xor</c>, <c>and</c>, <c>not</c>.</summary>
  private PerlExpr ParseExpression() {
    var left = this.ParseLowAnd();
    while (true) {
      var token = this.PeekOperator();
      if (!(token.IsWord("or") || token.IsWord("xor")))
        return left;
      this.NextOperator();
      var right = this.ParseLowAnd();
      left = new PerlBinary(token.Text, left, right) { Line = token.Line };
    }
  }

  private PerlExpr ParseLowAnd() {
    var left = this.ParseLowNot();
    while (this.PeekOperator().IsWord("and")) {
      var token = this.NextOperator();
      var right = this.ParseLowNot();
      left = new PerlBinary("and", left, right) { Line = token.Line };
    }
    return left;
  }

  private PerlExpr ParseLowNot() {
    var token = this.PeekTerm();
    if (token.IsWord("not")) {
      this.NextTerm();
      if (!this.CanStartTerm(this.PeekTerm()))
        throw this.Unexpected(this.PeekTerm(), "an operand of not");
      return new PerlUnary("not", this.ParseLowNot()) { Line = token.Line };
    }
    return this.ParseCommaList();
  }

  /// <summary>A comma list (or a single assignment expression).</summary>
  private PerlExpr ParseCommaList() {
    var line = this.Line;
    var first = this.ParseAssignment();
    if (!this.PeekOperator().Is(",") && !this.PeekOperator().Is("=>"))
      return first;
    var items = new List<PerlExpr> { first };
    while (this.PeekOperator().Is(",") || this.PeekOperator().Is("=>")) {
      this.NextOperator();
      while (this.PeekTerm().Is(",") || this.PeekTerm().Is("=>"))
        this.NextTerm();
      if (!this.CanStartTerm(this.PeekTerm()))
        break;
      items.Add(this.ParseAssignment());
    }
    return new PerlList(items) { Line = line };
  }

  private PerlExpr ParseAssignment() {
    var left = this.ParseTernary();
    var token = this.PeekOperator();
    if (token.Kind == PerlTokenKind.Operator && _assignments.Contains(token.Text)) {
      this.NextOperator();
      var right = this.ParseAssignment();
      return new PerlAssign(token.Text, left, right) { Line = token.Line };
    }
    return left;
  }

  private PerlExpr ParseTernary() {
    var condition = this.ParseRange();
    var token = this.PeekOperator();
    if (!token.Is("?"))
      return condition;
    this.NextOperator();
    var then = this.ParseAssignment();
    this.ExpectOperator(":");
    var otherwise = this.ParseTernaryElse();
    return new PerlTernary(condition, then, otherwise) { Line = token.Line };
  }

  private PerlExpr ParseTernaryElse() {
    // The branch after ':' is a ternary itself; an assignment there assigns to the whole conditional.
    var start = this.ParseRange();
    var token = this.PeekOperator();
    if (!token.Is("?"))
      return start;
    this.NextOperator();
    var then = this.ParseAssignment();
    this.ExpectOperator(":");
    return new PerlTernary(start, then, this.ParseTernaryElse()) { Line = token.Line };
  }

  private PerlExpr ParseRange() {
    var left = this.ParseBinary(OrOrLevel);
    var token = this.PeekOperator();
    if (token.Is("..") || token.Is("...")) {
      this.NextOperator();
      var right = this.ParseBinary(OrOrLevel);
      left = new PerlBinary(token.Text, left, right) { Line = token.Line };
      var again = this.PeekOperator();
      if (again.Is("..") || again.Is("..."))
        throw this.Unexpected(again, "an operator");
    }
    return left;
  }

  private int BinaryLevel(PerlToken token) {
    if (token.AutoQuoted)
      return -1;
    if (token.Kind == PerlTokenKind.Operator && _binaryLevels.TryGetValue(token.Text, out var level))
      return level;
    if (token.Kind == PerlTokenKind.Word && token.Text is "eq" or "ne" or "cmp" or "lt" or "gt" or "le" or "ge" && _binaryLevels.TryGetValue(token.Text, out var word))
      return word;
    if (token.Kind == PerlTokenKind.Word && token.Text == "isa" && this.Feature("isa"))
      return 14;
    return -1;
  }

  private PerlExpr ParseBinary(int minimum) {
    var left = this.ParseUnary();
    while (true) {
      var token = this.PeekOperator();
      var level = this.BinaryLevel(token);
      if (level < minimum)
        return left;
      this.NextOperator();
      if (token.Text is "=~" or "!~") {
        var pattern = this.ParseUnary();
        left = pattern is PerlPattern { Target: null } bound
          ? bound with { Target = left, Negated = token.Text == "!~", Line = token.Line }
          : new PerlBinary(token.Text, left, pattern) { Line = token.Line };
        continue;
      }
      if (token.Text == "isa") {
        var classToken = this.PeekTerm();
        PerlExpr type;
        if (classToken.Kind == PerlTokenKind.Word && !classToken.AutoQuoted) {
          this.NextTerm();
          type = new PerlString(classToken.Text.TrimEnd(':')) { Line = classToken.Line, Bareword = true };
        } else {
          type = this.ParseUnary();
        }
        left = new PerlBinary("isa", left, type) { Line = token.Line };
        continue;
      }
      var right = this.ParseBinary(level + 1);
      if (level is 12 or 13) {
        left = this.ParseComparisons(level, token, left, right);
        continue;
      }
      left = new PerlBinary(token.Text, left, right) { Line = token.Line };
    }
  }

  /// <summary>
  /// The comparisons of one level after the first (perlop, "Operator Precedence and Associativity"):
  /// the relational and the equality operators chain, <c>&lt;=&gt;</c>, <c>cmp</c> and <c>~~</c> do not
  /// combine with any operator of their level.
  /// </summary>
  private PerlExpr ParseComparisons(int level, PerlToken first, PerlExpr left, PerlExpr right) {
    static bool Chains(string op) => op is not ("<=>" or "cmp" or "~~");
    var operators = new List<string> { first.Text };
    var operands = new List<PerlExpr> { left, right };
    while (this.BinaryLevel(this.PeekOperator()) == level) {
      var token = this.PeekOperator();
      if (!Chains(token.Text) || !Chains(operators[^1]))
        throw this.Unexpected(token, "an operator of another precedence level");
      this.NextOperator();
      operators.Add(token.Text);
      operands.Add(this.ParseBinary(level + 1));
    }
    return operators.Count == 1
      ? new PerlBinary(first.Text, left, right) { Line = first.Line }
      : new PerlComparisonChain(operators, operands) { Line = first.Line };
  }

  /// <summary>The operand of a named unary operator: everything binding tighter than the comparisons.</summary>
  private PerlExpr ParseUnaryOperand() => this.ParseBinary(ShiftLevel);

  private PerlExpr ParseUnary() {
    var token = this.PeekTerm();
    if (token.Kind == PerlTokenKind.Operator) {
      switch (token.Text) {
        case "!":
          this.NextTerm();
          return new PerlUnary("!", this.ParseUnary()) { Line = token.Line };
        case "~" or "~.":
          this.NextTerm();
          return new PerlUnary(token.Text, this.ParseUnary()) { Line = token.Line };
        case "\\":
          this.NextTerm();
          return new PerlUnary("\\", this.ParseUnary()) { Line = token.Line };
        case "-": {
          this.NextTerm();
          var next = this.PeekTerm();
          if (next.Kind == PerlTokenKind.Word && !next.SpaceBefore && !this.IsKnownSub(next.Text) && !PerlBuiltins.IsBuiltin(next.Text) && !next.Text.StartsWith("CORE::", StringComparison.Ordinal) && !this.FollowedByCallParen(next)) {
            this.NextTerm();
            return new PerlString("-" + next.Text) { Line = token.Line, Bareword = true };
          }
          return new PerlUnary("-", this.ParseUnary()) { Line = token.Line };
        }
        case "+":
          this.NextTerm();
          return new PerlUnary("+", this.ParseUnary()) { Line = token.Line };
      }
    }
    return this.ParsePower();
  }

  private bool FollowedByCallParen(PerlToken word) => this._lexer.At(this._lexer.SkipSpace(word.End)) == '(';

  private PerlExpr ParsePower() {
    var left = this.ParseIncrement();
    var token = this.PeekOperator();
    if (token.Is("**")) {
      this.NextOperator();
      var right = this.ParseUnary();
      return new PerlBinary("**", left, right) { Line = token.Line };
    }
    return left;
  }

  private PerlExpr ParseIncrement() {
    var token = this.PeekTerm();
    if (token.Is("++") || token.Is("--")) {
      this.NextTerm();
      var target = this.ParseIncrement();
      return new PerlStep(token.Text == "++", true, target) { Line = token.Line };
    }
    var expression = this.ParsePostfix(this.ParsePrimary());
    var after = this.PeekOperator();
    if (after.Is("++") || after.Is("--")) {
      this.NextOperator();
      return new PerlStep(after.Text == "++", false, expression) { Line = after.Line };
    }
    return expression;
  }

  /// <summary>Whether a token read as a term can start one.</summary>
  private bool CanStartTerm(PerlToken token) {
    switch (token.Kind) {
      case PerlTokenKind.End:
        return false;
      case PerlTokenKind.Word:
        if (token.AutoQuoted)
          return true;
        return token.Text == "x" || !PerlWordSets.IsOperatorWord(token.Text, this.Feature("isa"));
      case PerlTokenKind.Operator:
        return PerlWordSets.TermOperators.Contains(token.Text);
      default:
        return true;
    }
  }

  // ---- postfix: subscripts, arrows, method calls ----

  private PerlExpr ParsePostfix(PerlExpr expression) {
    var subscripted = expression is PerlElement or PerlSlice or PerlCallReference;
    while (true) {
      var token = this.PeekOperator();
      if (token.Is("->")) {
        this.NextOperator();
        expression = this.ParseArrow(expression, token.Line, out subscripted);
        continue;
      }
      if (subscripted && token.Is("[") || subscripted && token.Is("{") && expression is not PerlSlice) {
        expression = this.ParseSubscript(new PerlDeref(token.Is("[") ? '@' : '%', expression) { Line = token.Line }, token.Is("{"));
        continue;
      }
      if (subscripted && token.Is("(") && expression is PerlCallReference or PerlElement) {
        this.NextOperator();
        expression = new PerlCallReference(expression, this.ParseParenArguments()) { Line = token.Line };
        continue;
      }
      return expression;
    }
  }

  private PerlExpr ParseArrow(PerlExpr expression, int line, out bool subscripted) {
    subscripted = true;
    var at = this._lexer.SkipSpace(this._lexer.Position);
    var c = this._lexer.At(at);
    var c1 = this._lexer.At(at + 1);
    switch (c) {
      case '[':
        return this.ParseSubscript(new PerlDeref('@', expression) { Line = line }, hash: false);
      case '{':
        return this.ParseSubscript(new PerlDeref('%', expression) { Line = line }, hash: true);
      case '(':
        this._lexer.Position = at + 1;
        return new PerlCallReference(expression, this.ParseParenArguments()) { Line = line };
      case '@' or '%' or '$' or '&' or '*' when c1 == '*':
        this._lexer.Position = at + 2;
        subscripted = false;
        return new PerlDeref(c, expression) { Line = line };
      case '$' when c1 == '#' && this._lexer.At(at + 2) == '*':
        this._lexer.Position = at + 3;
        subscripted = false;
        return new PerlLastIndex(new PerlDeref('@', expression) { Line = line }) { Line = line };
      case '@' or '%' when c1 is '[' or '{':
        this._lexer.Position = at + 1;
        return this.ParseSliceSubscript(new PerlDeref(c1 == '[' ? '@' : '%', expression) { Line = line }, c1 == '{', keyValue: c == '%');
      case '$': {
        subscripted = false;
        var nameToken = this.NextTerm();
        PerlExpr dynamic = nameToken.Kind == PerlTokenKind.Variable && nameToken.Sigil == '$'
          ? this.Variable('$', nameToken.Text, nameToken.Line)
          : nameToken.Kind == PerlTokenKind.Cast && nameToken.Text == "$"
            ? this.ParseCast(nameToken)
            : throw this.Unexpected(nameToken, "a method name");
        var (arguments, parenthesized) = this.ParseMethodArguments();
        return new PerlMethodCall(expression, null, dynamic, arguments) { Line = line, Parenthesized = parenthesized };
      }
      default: {
        subscripted = false;
        var name = this.ReadIdentifier() ?? throw this.Unexpected(this.PeekTerm(), "a method name or subscript");
        var (arguments, parenthesized) = this.ParseMethodArguments();
        return new PerlMethodCall(expression, name, null, arguments) { Line = line, Parenthesized = parenthesized };
      }
    }
  }

  private (List<PerlExpr> Arguments, bool Parenthesized) ParseMethodArguments() {
    if (this._lexer.At(this._lexer.SkipSpace(this._lexer.Position)) != '(')
      return ([], false);
    this.NextOperator();
    return (this.ParseParenArguments(), true);
  }

  /// <summary>The arguments after a <c>(</c> already taken, up to and with the <c>)</c>.</summary>
  private List<PerlExpr> ParseParenArguments() {
    var items = new List<PerlExpr>();
    if (this.AcceptTerm(")"))
      return items;
    var expression = this.ParseExpression();
    this.ExpectOperator(")");
    if (expression is PerlList { Parenthesized: false } list)
      items.AddRange(list.Items);
    else
      items.Add(expression);
    return items;
  }

  /// <summary>An element subscript <c>[...]</c> or <c>{...}</c> of an aggregate, at the bracket.</summary>
  private PerlExpr ParseSubscript(PerlExpr aggregate, bool hash) {
    var open = this.NextOperator();
    var index = hash ? this.ParseHashKey() : this.ParseExpression();
    this.ExpectOperator(hash ? "}" : "]");
    return new PerlElement(aggregate, index, hash) { Line = open.Line };
  }

  private PerlExpr ParseSliceSubscript(PerlExpr aggregate, bool hash, bool keyValue) {
    var open = this.NextOperator();
    PerlExpr indexes = this.PeekTerm().Is(hash ? "}" : "]")
      ? new PerlList([]) { Line = open.Line }
      : hash ? this.ParseHashKey() : this.ParseExpression();
    this.ExpectOperator(hash ? "}" : "]");
    return new PerlSlice(aggregate, indexes, hash, keyValue) { Line = open.Line };
  }

  /// <summary>The key of a hash subscript, at the position after <c>{</c>: a bareword (also <c>-word</c>) is a string.</summary>
  private PerlExpr ParseHashKey() {
    var at = this._lexer.SkipSpace(this._lexer.Position);
    var start = at;
    var minus = this._lexer.At(at) == '-';
    if (minus)
      ++at;
    if (PerlLexer.IsIdentifierStart(this._lexer.At(at), this._scope.Utf8)) {
      var end = at;
      while (PerlLexer.IsWordChar(this._lexer.At(end), this._scope.Utf8))
        ++end;
      var close = this._lexer.SkipSpace(end);
      if (this._lexer.At(close) == '}') {
        this._lexer.Position = close;
        return new PerlString(this._lexer.Text.Value[start..end]) { Line = this._lexer.LineAt(start), Bareword = true };
      }
    }
    return this.ParseExpression();
  }

  // ---- primaries ----

  private PerlExpr ParsePrimary() {
    var token = this.PeekTerm();
    var line = token.Line;
    switch (token.Kind) {
      case PerlTokenKind.Variable:
        this.NextTerm();
        return this.ParseVariable(token);
      case PerlTokenKind.Cast:
        this.NextTerm();
        return this.ParseCast(token);
      case PerlTokenKind.Number:
        this.NextTerm();
        return new PerlNumber(token.Number) { Line = line };
      case PerlTokenKind.Version:
        this.NextTerm();
        return new PerlString(VersionString(token.Text)) { Line = line };
      case PerlTokenKind.Quote or PerlTokenKind.Match or PerlTokenKind.Substitute or PerlTokenKind.Transliterate or PerlTokenKind.Heredoc or PerlTokenKind.Glob: {
        var quoted = this.ParseQuoted(this.NextTerm());
        if (quoted is PerlWords && this.PeekOperator().Is("["))
          return this.ParseSliceSubscript(quoted, hash: false, keyValue: false);
        return quoted;
      }
      case PerlTokenKind.ReadLine:
        this.NextTerm();
        return new PerlReadLine(token.Text, false) { Line = line };
      case PerlTokenKind.FileTest:
        return this.ParseFileTest();
      case PerlTokenKind.Word:
        this.NextTerm();
        return this.ParseWord(token);
      case PerlTokenKind.Operator:
        switch (token.Text) {
          case "(":
            this.NextTerm();
            return this.ParseParenthesized(line);
          case "[":
            this.NextTerm();
            return new PerlAnonArray(this.ParseItemsUntil("]")) { Line = line };
          case "{":
            this.NextTerm();
            return new PerlAnonHash(this.ParseItemsUntil("}")) { Line = line };
          case "<<>>":
            this.NextTerm();
            return new PerlReadLine("<<>>", false) { Line = line };
        }
        break;
    }
    throw this.Unexpected(token, "a term");
  }

  private static string VersionString(string text) {
    var parts = text.TrimStart('v').Replace("_", "", StringComparison.Ordinal).Split('.');
    var chars = new System.Text.StringBuilder();
    foreach (var part in parts)
      chars.Append(char.ConvertFromUtf32(Math.Min(int.Parse(part, System.Globalization.CultureInfo.InvariantCulture), 0x10FFFF)));
    return chars.ToString();
  }

  private List<PerlExpr> ParseItemsUntil(string close) {
    var items = new List<PerlExpr>();
    if (this.AcceptTerm(close))
      return items;
    var expression = this.ParseExpression();
    this.ExpectOperator(close);
    if (expression is PerlList { Parenthesized: false } list)
      items.AddRange(list.Items);
    else
      items.Add(expression);
    return items;
  }

  private PerlExpr ParseParenthesized(int line) {
    PerlExpr list;
    if (this.AcceptTerm(")")) {
      list = new PerlList([]) { Line = line, Parenthesized = true };
    } else {
      var inner = this.ParseExpression();
      this.ExpectOperator(")");
      list = inner is PerlList { Parenthesized: false } items
        ? items with { Parenthesized = true }
        : new PerlList([inner]) { Line = line, Parenthesized = true };
    }
    if (this.PeekOperator().Is("["))
      return this.ParseSliceSubscript(list, hash: false, keyValue: false);
    return list;
  }

  private PerlExpr ParseVariable(PerlToken token) {
    var line = token.Line;
    switch (token.Sigil) {
      case '$': {
        var next = this.PeekOperator();
        if (next.Is("["))
          return this.ParseSubscript(this.Variable('@', token.Text, line), hash: false);
        if (next.Is("{"))
          return this.ParseSubscript(this.Variable('%', token.Text, line), hash: true);
        return this.Variable('$', token.Text, line);
      }
      case '@': {
        var next = this.PeekOperator();
        if (next.Is("["))
          return this.ParseSliceSubscript(this.Variable('@', token.Text, line), hash: false, keyValue: false);
        if (next.Is("{"))
          return this.ParseSliceSubscript(this.Variable('%', token.Text, line), hash: true, keyValue: false);
        return this.Variable('@', token.Text, line);
      }
      case '%': {
        var next = this.PeekOperator();
        if (next.Is("[") && !next.SpaceBefore)
          return this.ParseSliceSubscript(this.Variable('@', token.Text, line), hash: false, keyValue: true);
        if (next.Is("{") && !next.SpaceBefore)
          return this.ParseSliceSubscript(this.Variable('%', token.Text, line), hash: true, keyValue: true);
        return this.Variable('%', token.Text, line);
      }
      case '#':
        return new PerlLastIndex(this.Variable('@', token.Text, line)) { Line = line };
      case '&': {
        var name = token.Text.StartsWith("::", StringComparison.Ordinal) ? "main" + token.Text : token.Text;
        if (this.PeekOperator().Is("(")) {
          this.NextOperator();
          return new PerlCall(name, this.ParseParenArguments()) { Line = line, Ampersand = true, Parenthesized = true };
        }
        return new PerlCall(name, null) { Line = line, Ampersand = true };
      }
      default: {
        var glob = this.Variable('*', token.Text, line);
        if (this.PeekOperator().Is("{") && !this.PeekOperator().SpaceBefore)
          return this.ParseSubscript(glob, hash: true);
        return glob;
      }
    }
  }

  /// <summary>A dereference after its sigil: <c>$$x</c>, <c>@{...}</c>, <c>%$h</c>, <c>&amp;$f(...)</c>, <c>$#{...}</c>, and the subscripts it takes.</summary>
  private PerlExpr ParseCast(PerlToken cast) {
    var line = cast.Line;
    PerlExpr inner;
    var token = this.PeekTerm();
    if (token.Is("{")) {
      inner = this.ParseDerefBlock();
    } else if (token.Kind == PerlTokenKind.Variable && token.Sigil == '$') {
      this.NextTerm();
      inner = this.Variable('$', token.Text, token.Line);
    } else if (token.Kind == PerlTokenKind.Cast && token.Text == "$") {
      this.NextTerm();
      inner = this.ParseCast(token);
      if (inner is not PerlDeref { Sigil: '$' })
        throw this.Unexpected(token, "a scalar");
    } else {
      throw this.Unexpected(token, "a variable or block after the sigil");
    }
    var next = this.PeekOperator();
    switch (cast.Text) {
      case "$":
        if (next.Is("["))
          return this.ParseSubscript(new PerlDeref('@', inner) { Line = line }, hash: false);
        if (next.Is("{"))
          return this.ParseSubscript(new PerlDeref('%', inner) { Line = line }, hash: true);
        return new PerlDeref('$', inner) { Line = line };
      case "@":
        if (next.Is("[") && !next.SpaceBefore)
          return this.ParseSliceSubscript(new PerlDeref('@', inner) { Line = line }, hash: false, keyValue: false);
        if (next.Is("{") && !next.SpaceBefore)
          return this.ParseSliceSubscript(new PerlDeref('%', inner) { Line = line }, hash: true, keyValue: false);
        return new PerlDeref('@', inner) { Line = line };
      case "%":
        if (next.Is("[") && !next.SpaceBefore)
          return this.ParseSliceSubscript(new PerlDeref('@', inner) { Line = line }, hash: false, keyValue: true);
        if (next.Is("{") && !next.SpaceBefore)
          return this.ParseSliceSubscript(new PerlDeref('%', inner) { Line = line }, hash: true, keyValue: true);
        return new PerlDeref('%', inner) { Line = line };
      case "&":
        if (next.Is("(")) {
          this.NextOperator();
          return new PerlCallReference(new PerlDeref('&', inner) { Line = line }, this.ParseParenArguments()) { Line = line };
        }
        return new PerlCallReference(new PerlDeref('&', inner) { Line = line }, null) { Line = line };
      case "*":
        if (next.Is("{") && !next.SpaceBefore)
          return this.ParseSubscript(new PerlDeref('*', inner) { Line = line }, hash: true);
        return new PerlDeref('*', inner) { Line = line };
      default:
        return new PerlLastIndex(new PerlDeref('@', inner) { Line = line }) { Line = line };
    }
  }

  /// <summary>The block of <c>${ ... }</c>, <c>@{ ... }</c>: an expression, or statements whose last value is the reference.</summary>
  private PerlExpr ParseDerefBlock() {
    var line = this.Line;
    var block = this.ParseBlock();
    if (block.Statements is [PerlExpressionStatement { Modifier: null } only])
      return only.Expression;
    return new PerlDoBlock(block) { Line = line };
  }

  private PerlExpr ParseFileTest() {
    var token = this.NextTerm();
    var line = token.Line;
    var next = this.PeekTerm();
    if (next.Kind == PerlTokenKind.Word && next.Text == "_" && !next.AutoQuoted) {
      this.NextTerm();
      return new PerlUnary(token.Text, new PerlString("_") { Line = next.Line, Bareword = true }) { Line = line };
    }
    if (!this.CanStartTerm(next))
      return new PerlUnary(token.Text, null) { Line = line };
    return new PerlUnary(token.Text, this.ParseUnaryOperand()) { Line = line };
  }

  private PerlExpr? ParseParenthesizedOperand() {
    this.ExpectTerm("(");
    if (this.AcceptTerm(")"))
      return null;
    var operand = this.ParseExpression();
    this.ExpectOperator(")");
    return operand;
  }

}
