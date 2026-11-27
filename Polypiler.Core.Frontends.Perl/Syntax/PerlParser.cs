using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Perl.Ast;

namespace Polypiler.Core.Frontends.Perl.Syntax;

/// <summary>
/// A recursive-descent parser of Perl 5 (perlsyn, perlop, perlsub), written from the documentation
/// and checked against perl itself. It resolves lexical variables while it parses, as perl does - a
/// <c>my</c> variable becomes visible after its statement or after the condition of a compound
/// statement - follows the lexical pragmas that change the grammar (<c>use feature</c>, feature
/// bundles of <c>use VERSION</c>, <c>use utf8</c>, <c>use integer</c>, <c>use constant</c>,
/// <c>use subs</c>) and knows the subs declared before a call, with their prototypes. What it cannot
/// decide without running the file - a source filter, a keyword plugin, a call whose parse depends on
/// a sub a module may import - it refuses by name.
/// </summary>
internal sealed partial class PerlParser : IPerlLexerHost {

  private sealed class Scope {

    public Scope(Scope? parent) {
      this.Parent = parent;
      if (parent is null) {
        this.Features = new HashSet<string>(PerlPragmas.DefaultFeatures, StringComparer.Ordinal);
        return;
      }
      this.Features = parent.Features;
      this.Integer = parent.Integer;
      this.Utf8 = parent.Utf8;
      this.Package = parent.Package;
    }

    public Scope? Parent { get; }

    public Dictionary<string, PerlLexical> Names { get; } = new(StringComparer.Ordinal);

    /// <summary>The lexicals declared in this scope that are not visible yet.</summary>
    public List<PerlLexical> Pending { get; } = [];

    public HashSet<string> Features { get; set; }

    public bool Integer { get; set; }

    public bool Utf8 { get; set; }

    public string Package { get; set; } = "main";

  }

  private sealed record KnownSub(string? Prototype, bool Imported);

  private readonly string _file;
  private PerlLexer _lexer;
  private Scope _scope = new(null);
  private PerlUnit _unit;
  private readonly Dictionary<string, KnownSub> _subs = new(StringComparer.Ordinal);
  private readonly List<PerlSubDeclaration> _defined = [];
  private readonly HashSet<string> _unknownImporters = new(StringComparer.Ordinal);
  private bool _beginCode;
  private readonly HashSet<string> _usedModules = new(StringComparer.Ordinal);

  private PerlParser(string file, string source) {
    this._file = file;
    this._unit = new PerlUnit("main", null);
    this._lexer = new PerlLexer(new PerlText(source, 1), 0, source.Length, this, nested: false);
    foreach (var (name, prototype) in PerlPragmas.CoreFunctions)
      this._subs[name] = new KnownSub(prototype, false);
  }

  /// <summary>Parses a file.</summary>
  /// <exception cref="PerlSyntaxException">The file is not valid Perl.</exception>
  /// <exception cref="PerlUnsupportedException">The file uses a construct the parser does not decide.</exception>
  public static PerlDocument Parse(string file, string source) => ParseWithConstants(file, source).Document;

  /// <summary>Parses a file and gives the constants its <c>use constant</c> and constant subs declare as well.</summary>
  public static (PerlDocument Document, IReadOnlyDictionary<string, PerlExpr?> Constants) ParseWithConstants(string file, string source) {
    if (source.StartsWith("#!", StringComparison.Ordinal)) {
      var end = source.IndexOf('\n');
      var shebang = end < 0 ? source : source[..end];
      if (shebang.Contains("perl", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(shebang, @"\s-[A-Za-z0-9]*[npaFl0xiE]"))
        throw new PerlUnsupportedException("command-line switches on the #! line that change the program", 1);
    }
    var parser = new PerlParser(file, source);
    var statements = parser.ParseStatements(closing: false);
    return (new PerlDocument(statements, parser._unit) { Line = 1, Subs = parser._defined }, parser.Constants);
  }

  /// <summary>The constants <c>use constant</c> declared, by qualified name, with their values.</summary>
  public Dictionary<string, PerlExpr?> Constants { get; } = new(StringComparer.Ordinal);

  bool IPerlLexerHost.ApostropheSeparates => this._scope.Features.Contains("apostrophe_as_package_separator");

  bool IPerlLexerHost.Utf8 => this._scope.Utf8;

  private string Package => this._scope.Package;

  private bool Feature(string name) => this._scope.Features.Contains(name);

  /// <summary>Whether a module of unknown exports, or BEGIN-time code, may have defined subs this parse cannot see.</summary>
  private bool MayImport => this._unknownImporters.Count > 0 || this._beginCode;

  // ---- tokens ----

  private PerlToken PeekTerm() => this._lexer.Peek(term: true);

  private PerlToken PeekOperator() => this._lexer.Peek(term: false);

  private PerlToken NextTerm() => this._lexer.Next(term: true);

  private PerlToken NextOperator() => this._lexer.Next(term: false);

  private bool AcceptOperator(string text) {
    if (!this.PeekOperator().Is(text))
      return false;
    this.NextOperator();
    return true;
  }

  private bool AcceptTerm(string text) {
    if (!this.PeekTerm().Is(text))
      return false;
    this.NextTerm();
    return true;
  }

  private void ExpectOperator(string text) {
    var token = this.PeekOperator();
    if (!token.Is(text))
      throw this.Unexpected(token, $"'{text}'");
    this.NextOperator();
  }

  private void ExpectTerm(string text) {
    var token = this.PeekTerm();
    if (!token.Is(text))
      throw this.Unexpected(token, $"'{text}'");
    this.NextTerm();
  }

  private PerlSyntaxException Unexpected(PerlToken token, string wanted)
    => new(token.Kind == PerlTokenKind.End
      ? $"syntax error: missing {wanted} at end of file"
      : $"syntax error near \"{Near(token)}\", expected {wanted}", token.Line);

  private static string Near(PerlToken token) => token.Text.Length > 20 ? token.Text[..20] : token.Text;

  private PerlSyntaxException Error(string message, int line) => new($"syntax error: {message}", line);

  private int Line => this._lexer.Line;

  // ---- scopes and lexicals ----

  private T InScope<T>(Func<T> body) {
    var saved = this._scope;
    this._scope = new Scope(saved);
    try {
      return body();
    } finally {
      this._scope = saved;
    }
  }

  private PerlLexical Declare(char sigil, string name, PerlDeclarator kind) {
    var lexical = new PerlLexical(sigil, name, kind, this._unit, this.Package);
    this._unit.Lexicals.Add(lexical);
    this._unit.Pad.Add(lexical);
    this._scope.Pending.Add(lexical);
    return lexical;
  }

  /// <summary>Makes the lexicals declared so far visible, as perl does at the end of a statement or after a condition.</summary>
  private void Introduce() {
    foreach (var lexical in this._scope.Pending)
      this._scope.Names[lexical.PadName] = lexical;
    this._scope.Pending.Clear();
  }

  private PerlLexical? Lookup(char sigil, string name) {
    if (!IsLexicalName(name))
      return null;
    for (var scope = this._scope; scope is not null; scope = scope.Parent)
      if (scope.Names.TryGetValue(sigil + name, out var lexical))
        return lexical;
    return null;
  }

  private static bool IsLexicalName(string name)
    => name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_' && name.Length > 1) && !name.Contains("::", StringComparison.Ordinal);

  private PerlVariable Variable(char sigil, string name, int line)
    => new(sigil, name, this.Lookup(sigil, name)) { Line = line };

  // ---- statements ----

  private List<PerlStatement> ParseStatements(bool closing) {
    var statements = new List<PerlStatement>();
    while (true) {
      var token = this.PeekTerm();
      if (token.Kind == PerlTokenKind.End) {
        if (closing)
          throw this.Error("missing right curly bracket", token.Line);
        return statements;
      }
      if (token.Is("}")) {
        if (!closing)
          throw this.Error("unmatched right curly bracket", token.Line);
        return statements;
      }
      if (this.ParseStatement() is { } statement)
        statements.Add(statement);
    }
  }

  private PerlBlock ParseBlock() {
    var line = this.Line;
    this.ExpectTerm("{");
    var statements = this.InScope(() => {
      this.Introduce();
      return this.ParseStatements(closing: true);
    });
    this.ExpectTerm("}");
    return new PerlBlock(statements) { Line = line };
  }

  private PerlStatement? ParseStatement() {
    var token = this.PeekTerm();
    var line = token.Line;
    string? label = null;
    if (token.Kind == PerlTokenKind.Word && !token.AutoQuoted && this.IsLabel(token)) {
      label = token.Text;
      this.NextTerm();
      this.ExpectOperator(":");
      token = this.PeekTerm();
      if (token.Is("}") || token.Kind == PerlTokenKind.End)
        return new PerlEmpty { Line = line, Label = label, Package = this.Package, Integer = this._scope.Integer };
    }
    var package = this.Package;
    var integer = this._scope.Integer;
    var statement = this.ParseStatementBody(token);
    return statement is null ? null : statement with { Label = label ?? statement.Label, Line = line, Package = package, Integer = integer };
  }

  private static readonly HashSet<string> _statementKeywords = new(StringComparer.Ordinal) {
    "if", "unless", "while", "until", "for", "foreach", "sub", "package", "use", "no", "format", "do", "eval", "my", "our",
    "local", "return", "print", "else", "elsif", "continue", "BEGIN", "END", "INIT", "CHECK", "UNITCHECK", "and", "or", "not",
    "xor", "x", "lt", "gt", "le", "ge", "eq", "ne", "cmp", "q", "qq", "qw", "qr", "qx", "m", "s", "tr", "y", "default"
  };

  private bool IsLabel(PerlToken token) {
    if (_statementKeywords.Contains(token.Text) || token.Text.Contains("::", StringComparison.Ordinal))
      return false;
    var after = this._lexer.SkipSpace(token.End);
    return this._lexer.At(after) == ':' && this._lexer.At(after + 1) != ':';
  }

  private PerlStatement? ParseStatementBody(PerlToken token) {
    if (token.Is(";")) {
      this.NextTerm();
      return new PerlEmpty();
    }
    if (token.Is("{") && !this.LooksLikeAnonHash(token.End, statement: true))
      return this.ParseBareBlock();
    if (token.Is("...")) {
      this.NextTerm();
      this.EndStatement();
      return new PerlExpressionStatement(new PerlCall("...", []) { Line = token.Line, Builtin = true });
    }
    if (token.Kind == PerlTokenKind.Word && !token.AutoQuoted && this.ParseKeywordStatement(token) is { } keyword)
      return keyword;
    return this.ParseExpressionStatement();
  }

  private PerlStatement? ParseKeywordStatement(PerlToken token) {
    switch (token.Text) {
      case "if" or "unless":
        return this.ParseIf(token.Text == "unless");
      case "while" or "until":
        return this.ParseWhile(token.Text == "until");
      case "for" or "foreach":
        return this.ParseFor();
      case "sub" when this.NextIsIdentifier(token.End):
        return this.ParseNamedSub();
      case "package":
        return this.ParsePackage();
      case "use" or "no":
        return this.ParseUse(token.Text == "no");
      case "BEGIN" or "END" or "INIT" or "CHECK" or "UNITCHECK" when this.NextIs(token.End, '{'):
        this.NextTerm();
        return this.ParsePhaseBlock(token.Text);
      case "AUTOLOAD" or "DESTROY" when this.NextIs(token.End, '{'):
        this.NextTerm();
        return this.ParseNamedSubAfterName(token.Text, token.Line);
      case "format":
        throw new PerlUnsupportedException("format", token.Line);
      case "try" when this.Feature("try"):
        return this.ParseTry();
      case "defer" when this.Feature("defer"):
        this.NextTerm();
        return new PerlDefer(this.ParseBlock());
      case "given" or "when" or "default" when this.Feature("switch"):
        throw new PerlUnsupportedException($"the switch feature ({token.Text})", token.Line);
      case "class" or "field" or "method" or "ADJUST" when this.Feature("class"):
        throw new PerlUnsupportedException($"the class feature ({token.Text})", token.Line);
      default:
        return null;
    }
  }

  private bool NextIsIdentifier(int position) {
    var at = this._lexer.SkipSpace(position);
    var c = this._lexer.At(at);
    return PerlLexer.IsIdentifierStart(c, this._scope.Utf8) || c == ':' && this._lexer.At(at + 1) == ':';
  }

  private bool NextIs(int position, char c) => this._lexer.At(this._lexer.SkipSpace(position)) == c;

  private PerlStatement ParseExpressionStatement() {
    var expression = this.ParseExpression();
    var statement = new PerlExpressionStatement(expression);
    var token = this.PeekOperator();
    if (token.Kind == PerlTokenKind.Word && token.Text is "if" or "unless" or "while" or "until" or "for" or "foreach" && !token.AutoQuoted) {
      this.NextOperator();
      var modified = this.ParseExpression();
      statement = statement with { Modifier = token.Text == "foreach" ? "for" : token.Text, ModifierExpression = modified };
    }
    this.Introduce();
    this.EndStatement();
    return statement;
  }

  private void EndStatement() {
    var token = this.PeekOperator();
    if (token.Is(";")) {
      this.NextOperator();
      return;
    }
    if (token.Is("}") || token.Kind == PerlTokenKind.End)
      return;
    throw this.Unexpected(token, "an operator or ';'");
  }

  private PerlExpr ParseCondition() {
    this.ExpectTerm("(");
    var condition = this.ParseExpression();
    this.ExpectOperator(")");
    this.Introduce();
    return condition;
  }

  private PerlStatement ParseIf(bool unless) {
    this.NextTerm();
    return this.InScope<PerlStatement>(() => {
      var clauses = new List<(PerlExpr, PerlBlock)> { (this.ParseCondition(), this.ParseBlock()) };
      PerlBlock? otherwise = null;
      while (true) {
        var token = this.PeekOperator();
        if (token.IsWord("elsif")) {
          this.NextOperator();
          clauses.Add((this.ParseCondition(), this.ParseBlock()));
          continue;
        }
        if (token.IsWord("else")) {
          this.NextOperator();
          otherwise = this.ParseBlock();
        }
        break;
      }
      return new PerlIf(clauses, otherwise, unless);
    });
  }

  private PerlStatement ParseWhile(bool until) {
    this.NextTerm();
    return this.InScope<PerlStatement>(() => {
      this.ExpectTerm("(");
      PerlExpr? condition = null;
      if (!this.PeekTerm().Is(")"))
        condition = this.ParseExpression();
      this.ExpectOperator(")");
      this.Introduce();
      var body = this.ParseBlock();
      var next = this.ParseContinue();
      return new PerlWhile(condition, body, next, until);
    });
  }

  private PerlBlock? ParseContinue() {
    if (!this.PeekOperator().IsWord("continue"))
      return null;
    this.NextOperator();
    return this.ParseBlock();
  }

  private PerlStatement ParseFor() {
    this.NextTerm();
    return this.InScope<PerlStatement>(() => {
      var token = this.PeekTerm();
      PerlDeclarator? declarator = null;
      var variables = new List<PerlVariable>();
      if (token.Kind == PerlTokenKind.Word && token.Text is "my" or "our" or "state" or "CORE::my" or "CORE::our" or "CORE::state" && !token.AutoQuoted) {
        this.NextTerm();
        declarator = token.Text.Replace("CORE::", "", StringComparison.Ordinal) switch { "my" => PerlDeclarator.My, "our" => PerlDeclarator.Our, _ => PerlDeclarator.State };
        if (this.PeekTerm().Is("(")) {
          this.NextTerm();
          do {
            while (this.AcceptTerm(","))
              ;
            if (this.PeekTerm().Is(")"))
              break;
            variables.Add(this.ParseLoopVariable(declarator));
          } while (this.AcceptOperator(","));
          this.ExpectOperator(")");
        } else {
          variables.Add(this.ParseLoopVariable(declarator));
        }
      } else if (token.Kind == PerlTokenKind.Variable && token.Sigil == '$') {
        this.NextTerm();
        variables.Add(this.Variable('$', token.Text, token.Line));
      } else if (token.Kind == PerlTokenKind.Cast && token.Text == "$") {
        this.NextTerm();
        this.ParseCast(token);
        variables.Add(new PerlVariable('$', "", null) { Line = token.Line });
      } else if (token.Is("\\")) {
        throw new PerlUnsupportedException("foreach with a reference alias (refaliasing)", token.Line);
      }

      this.ExpectTerm("(");
      if (variables.Count == 0) {
        PerlExpr? init = null;
        if (!this.PeekTerm().Is(";"))
          init = this.PeekTerm().Is(")") ? null : this.ParseExpression();
        if (this.PeekOperator().Is(";")) {
          this.NextOperator();
          this.Introduce();
          var condition = this.PeekTerm().Is(";") ? null : this.ParseExpression();
          this.ExpectOperator(";");
          this.Introduce();
          var step = this.PeekTerm().Is(")") ? null : this.ParseExpression();
          this.ExpectOperator(")");
          this.Introduce();
          return new PerlCFor(init, condition, step, this.ParseBlock());
        }
        this.ExpectOperator(")");
        this.Introduce();
        var implicitBody = this.ParseBlock();
        return new PerlForeach([], null, init ?? new PerlList([]) { Parenthesized = true, Line = token.Line }, implicitBody, this.ParseContinue());
      }

      var list = this.PeekTerm().Is(")") ? new PerlList([]) { Parenthesized = true, Line = token.Line } : this.ParseExpression();
      this.ExpectOperator(")");
      this.Introduce();
      var body = this.ParseBlock();
      return new PerlForeach(variables, declarator, list, body, this.ParseContinue());
    });
  }

  private PerlVariable ParseLoopVariable(PerlDeclarator? declarator) {
    var token = this.NextTerm();
    if (token.Kind != PerlTokenKind.Variable || token.Sigil != '$' || !IsLexicalName(token.Text))
      throw this.Unexpected(token, "a scalar loop variable");
    var lexical = this.Declare('$', token.Text, declarator!.Value);
    return new PerlVariable('$', token.Text, lexical) { Line = token.Line };
  }

  private PerlStatement ParseBareBlock() {
    var body = this.ParseBlock();
    return new PerlBareBlock(body, this.ParseContinue());
  }

  private PerlStatement ParsePhaseBlock(string phase) {
    var saved = this._unit;
    this._unit = new PerlUnit(phase, saved);
    try {
      var body = this.ParseBlock();
      if (phase is "BEGIN" or "UNITCHECK" or "CHECK")
        this.FollowBegin(body);
      return new PerlPhaseBlock(phase, body);
    } finally {
      this._unit = saved;
    }
  }

  /// <summary>
  /// What a BEGIN block may define for the rest of the parse: the subs of a file of perl's test harness
  /// it requires; anything it loads, evaluates or assigns to a glob may define subs the parse cannot see.
  /// </summary>
  private void FollowBegin(PerlBlock body) {
    foreach (var node in PerlWalk.Descendants(body)) {
      switch (node) {
        case PerlCall { Name: "require" or "do", Arguments: [PerlString { Bareword: false } file] } when PerlPragmas.Harness(file.Value) is { } subs:
          foreach (var (name, prototype) in subs)
            this._subs[this.Qualify(name)] = new KnownSub(prototype, true);
          break;
        case PerlCall { Name: "require" or "do" or "eval" or "evalbytes" }:
        case PerlCall { Builtin: false } call when !_harmlessAtBegin.Contains(call.Name):
        case PerlMethodCall:
        case PerlAssign { Target: PerlVariable { Sigil: '*' } or PerlDeref { Sigil: '*' } }:
        case PerlUse:
          this._beginCode = true;
          break;
      }
    }
  }

  /// <summary>Subs of the test harness a BEGIN block calls that define nothing (plans, skips, the include path, test output).</summary>
  private static readonly HashSet<string> _harmlessAtBegin = new(StringComparer.Ordinal) {
    "plan", "skip_all", "skip_all_if_miniperl", "skip_all_without_dynamic_extension", "skip_all_without_perlio", "skip_all_without_config",
    "skip_all_without_unicode_tables", "set_up_inc", "is_miniperl", "diag", "note", "ok", "is", "pass", "fail", "BAIL_OUT", "watchdog"
  };

  private PerlStatement ParseTry() {
    this.NextTerm();
    var body = this.ParseBlock();
    PerlVariable? variable = null;
    PerlBlock? handler = null;
    PerlBlock? final = null;
    if (this.PeekOperator().IsWord("catch")) {
      this.NextOperator();
      (variable, handler) = this.InScope(() => {
        this.ExpectTerm("(");
        var token = this.NextTerm();
        if (token.Kind != PerlTokenKind.Variable || token.Sigil != '$' || !IsLexicalName(token.Text))
          throw this.Unexpected(token, "a scalar variable");
        var lexical = this.Declare('$', token.Text, PerlDeclarator.My);
        this.ExpectOperator(")");
        this.Introduce();
        return (new PerlVariable('$', token.Text, lexical) { Line = token.Line }, this.ParseBlock());
      });
    }
    if (this.PeekOperator().IsWord("finally")) {
      this.NextOperator();
      final = this.ParseBlock();
    }
    if (handler is null && final is null)
      throw this.Error("try requires catch or finally", this.Line);
    return new PerlTry(body, variable, handler, final);
  }

  private PerlStatement ParsePackage() {
    this.NextTerm();
    var name = this.ReadIdentifier() ?? throw this.Error("package name expected", this.Line);
    string? version = null;
    var token = this.PeekTerm();
    if (token.Kind is PerlTokenKind.Number or PerlTokenKind.Version) {
      this.NextTerm();
      version = token.Text;
    }
    if (this.PeekTerm().Is("{")) {
      var body = this.InScope(() => {
        this._scope.Package = name;
        return this.ParseBlock();
      });
      return new PerlPackage(name, version, body);
    }
    this.EndStatement();
    this._scope.Package = name;
    return new PerlPackage(name, version, null);
  }

  /// <summary>Reads an identifier at the position directly, without the term/operator lexing; <see langword="null"/> if none.</summary>
  private string? ReadIdentifier() {
    var start = this._lexer.SkipSpace(this._lexer.Position);
    var c = this._lexer.At(start);
    if (c == '\'' && this.Feature("apostrophe_as_package_separator") && PerlLexer.IsIdentifierStart(this._lexer.At(start + 1), this._scope.Utf8)) {
      var rest = this._lexer.ScanIdentifier(start + 1);
      this._lexer.Position = rest;
      return "main::" + this._lexer.IdentifierText(start + 1, rest);
    }
    if (!(PerlLexer.IsIdentifierStart(c, this._scope.Utf8) || c == ':' && this._lexer.At(start + 1) == ':'))
      return null;
    var end = this._lexer.ScanIdentifier(start);
    this._lexer.Position = end;
    var text = this._lexer.IdentifierText(start, end);
    return text.StartsWith("::", StringComparison.Ordinal) ? "main" + text : text;
  }

  // ---- subs ----

  private PerlStatement ParseNamedSub() {
    this.NextTerm();
    var line = this.Line;
    var raw = this.ReadIdentifier()!;
    if (raw is "BEGIN" or "END" or "INIT" or "CHECK" or "UNITCHECK" && this.NextIs(this._lexer.Position, '{'))
      return this.ParsePhaseBlock(raw);
    return this.ParseNamedSubAfterName(raw, line);
  }

  private PerlStatement ParseNamedSubAfterName(string raw, int line) {
    var qualified = raw.Contains("::", StringComparison.Ordinal) ? raw : this.Package + "::" + raw;
    var sub = this.ParseSubRest(qualified, named: true);
    if (sub.Body is not null) {
      var declaration = new PerlSubDeclaration(qualified, sub) { Line = line, Package = this.Package };
      this._defined.RemoveAll(d => d.Name == qualified);
      if (!IsConstantSub(sub))
        this._defined.Add(declaration);
      else
        this.Constants[qualified] = ((PerlExpressionStatement)sub.Body.Statements[0]).Expression;
    }
    return new PerlSubDeclaration(qualified, sub);
  }

  /// <summary>Whether perl makes a sub a constant (perlsub, "Constant Functions"): an empty prototype and a body of one constant expression.</summary>
  private static bool IsConstantSub(PerlSub sub)
    => sub.Prototype == "" && sub.Body!.Statements is [PerlExpressionStatement { Modifier: null, Expression: var value }] && IsConstantExpression(value);

  private static bool IsConstantExpression(PerlExpr expression) => expression switch {
    PerlNumber or PerlString => true,
    PerlCall { Name: "undef", Arguments.Count: 0 } => true,
    PerlUnary { Operator: "-" or "!" or "~", Operand: { } operand } => IsConstantExpression(operand),
    PerlBinary { Operator: not ("=~" or "!~" or "&&" or "||" or "//" or "and" or "or" or "xor" or ".." or "..."), Left: var left, Right: var right } => IsConstantExpression(left) && IsConstantExpression(right),
    PerlList { Items: [var only], Parenthesized: true } => IsConstantExpression(only),
    _ => false
  };

  private PerlSub ParseSubRest(string name, bool named) {
    var line = this.Line;
    var attributes = new List<string>();
    string? prototype = null;
    List<PerlParameter>? signature = null;
    var saved = this._unit;
    var unit = new PerlUnit(name, saved);
    this._unit = unit;
    try {
      return this.InScope(() => {
        this.ParseAttributes(attributes);
        if (this.PeekTerm().Is("(")) {
          if (this.Feature("signatures"))
            signature = this.ParseSignature();
          else
            prototype = this.ReadPrototype();
        }
        this.ParseAttributes(attributes);
        if (attributes.FirstOrDefault(a => a.StartsWith("prototype(", StringComparison.Ordinal)) is { } declared)
          prototype = declared[10..^1].Replace(" ", "", StringComparison.Ordinal);
        if (named)
          this._subs[name] = new KnownSub(prototype, false);
        PerlBlock? body = null;
        if (this.PeekTerm().Is("{")) {
          this.Introduce();
          body = this.ParseBlock();
        } else if (named) {
          this.EndStatement();
        } else {
          throw this.Unexpected(this.PeekTerm(), "a sub body");
        }
        return new PerlSub(prototype, signature, attributes, body, unit) { Line = line };
      });
    } finally {
      this._unit = saved;
    }
  }

  private void ParseAttributes(List<string> attributes) {
    while (this._lexer.At(this._lexer.SkipSpace(this._lexer.Position)) == ':' && this._lexer.At(this._lexer.SkipSpace(this._lexer.Position) + 1) != ':') {
      this._lexer.Position = this._lexer.SkipSpace(this._lexer.Position) + 1;
      while (true) {
        var name = this.ReadIdentifier();
        if (name is null)
          break;
        var at = this._lexer.Position;
        if (this._lexer.At(at) == '(') {
          var (body, after) = this._lexer.ScanDelimited(at);
          this._lexer.Position = after;
          name += "(" + body.Value + ")";
        }
        attributes.Add(name);
        var next = this._lexer.SkipSpace(this._lexer.Position);
        if (this._lexer.At(next) == ':' && this._lexer.At(next + 1) != ':')
          this._lexer.Position = next + 1;
      }
    }
  }

  private string ReadPrototype() {
    var at = this._lexer.SkipSpace(this._lexer.Position);
    var (body, after) = this._lexer.ScanDelimited(at);
    this._lexer.Position = after;
    return new string([.. body.Value.Where(c => !char.IsWhiteSpace(c))]);
  }

  private List<PerlParameter> ParseSignature() {
    this.ExpectTerm("(");
    var parameters = new List<PerlParameter>();
    while (this._lexer.At(this._lexer.SkipSpace(this._lexer.Position)) != ')') {
      var at = this._lexer.SkipSpace(this._lexer.Position);
      var sigil = this._lexer.At(at);
      var follow = this._lexer.At(this._lexer.SkipSpace(at + 1));
      if (sigil is '$' or '@' or '%' && follow is ',' or ')' or '=') {
        var placeholderLine = this._lexer.LineAt(at);
        this._lexer.Position = at + 1;
        var placeholder = new PerlParameter(null, sigil, null, null) { Line = placeholderLine };
        if (follow == '=') {
          this._lexer.Position = this._lexer.SkipSpace(at + 1) + 1;
          if (!this.PeekTerm().Is(",") && !this.PeekTerm().Is(")"))
            placeholder = placeholder with { DefaultOperator = "=", Default = this.ParseAssignment() };
        }
        parameters.Add(placeholder);
        if (!this.AcceptOperator(","))
          break;
        this.SkipRawCommas();
        continue;
      }
      var token = this.NextTerm();
      var line = token.Line;
      PerlParameter parameter;
      if (token.Kind == PerlTokenKind.Variable && token.Sigil is '$' or '@' or '%' && IsLexicalName(token.Text)) {
        var lexical = this.Declare(token.Sigil, token.Text, PerlDeclarator.My);
        var variable = new PerlVariable(token.Sigil, token.Text, lexical) { Line = line };
        parameter = new PerlParameter(variable, token.Sigil, null, null) { Line = line };
      } else if (token.Is("$") || token.Is("@") || token.Is("%") || token.Kind == PerlTokenKind.Cast && token.Text is "$" or "@" or "%") {
        parameter = new PerlParameter(null, token.Text[0], null, null) { Line = line };
      } else if (token.Kind == PerlTokenKind.Variable && token.Sigil == '$' && token.Text == "$") {
        parameter = new PerlParameter(null, '$', null, null) { Line = line };
      } else {
        throw this.Unexpected(token, "a signature parameter");
      }
      var next = this.PeekOperator();
      if (parameter.Sigil == '$' && next.Text is "=" or "//=" or "||=") {
        this.NextOperator();
        var op = next.Text;
        PerlExpr? value = null;
        if (!this.PeekTerm().Is(",") && !this.PeekTerm().Is(")"))
          value = this.ParseAssignment();
        parameter = parameter with { DefaultOperator = op, Default = value };
      }
      parameters.Add(parameter);
      this.Introduce();
      if (!this.AcceptOperator(","))
        break;
      this.SkipRawCommas();
    }
    this.ExpectOperator(")");
    return parameters;
  }

  private void SkipRawCommas() {
    while (this._lexer.At(this._lexer.SkipSpace(this._lexer.Position)) == ',')
      this._lexer.Position = this._lexer.SkipSpace(this._lexer.Position) + 1;
  }

  // ---- use / no ----

  private PerlStatement ParseUse(bool no) {
    var keyword = this.NextTerm();
    var line = keyword.Line;
    var token = this.PeekTerm();
    if (token.Kind is PerlTokenKind.Number or PerlTokenKind.Version) {
      this.NextTerm();
      this.EndStatement();
      if (!no)
        this.UseVersion(token.Text);
      return new PerlUse(no, null, token.Text, null);
    }
    var module = this.ReadIdentifier() ?? throw this.Unexpected(token, "a module name or version");
    if (PerlPragmas.ChangesGrammar(module))
      throw new PerlUnsupportedException($"the module {module}, which changes the grammar of the file", line);
    string? version = null;
    var next = this.PeekTerm();
    if (next.Kind is PerlTokenKind.Number or PerlTokenKind.Version && !this.FollowedByComma(next.End)) {
      this.NextTerm();
      version = next.Text;
    }
    PerlExpr? imports = null;
    var empty = false;
    if (!this.PeekTerm().Is(";") && !this.PeekTerm().Is("}") && this.PeekTerm().Kind != PerlTokenKind.End) {
      var saved = this._unit;
      this._unit = new PerlUnit("BEGIN", saved);
      try {
        imports = this.ParseExpression();
      } finally {
        this._unit = saved;
      }
      empty = imports is PerlList { Items.Count: 0, Parenthesized: true };
    }
    this.EndStatement();
    this.ApplyPragma(no, module, imports, empty, line);
    return new PerlUse(no, module, version, imports) { EmptyImports = empty };
  }

  private bool FollowedByComma(int position) {
    var at = this._lexer.SkipSpace(position);
    return this._lexer.At(at) == ',' || this._lexer.At(at) == '=' && this._lexer.At(at + 1) == '>';
  }

  private void UseVersion(string version) {
    var minor = PerlPragmas.MinorVersion(version);
    if (minor < 0)
      throw this.Error($"Perl {version} required--this is only v5.42", this.Line);
    if (minor > 42)
      throw this.Error($"Perl {version} required--this is only v5.42", this.Line);
    if (minor >= 10)
      this._scope.Features = new HashSet<string>(PerlPragmas.Bundle(minor), StringComparer.Ordinal);
    if (minor >= 39)
      foreach (var name in PerlPragmas.BuiltinBundle)
        this.ImportBuiltin(name);
  }

  private void ApplyPragma(bool no, string module, PerlExpr? imports, bool empty, int line) {
    var words = ConstantWords(imports);
    switch (module) {
      case "feature" or "experimental":
        this.ApplyFeatures(no, words ?? [], module == "experimental");
        return;
      case "integer":
        this._scope.Integer = !no;
        return;
      case "utf8":
        this._scope.Utf8 = !no;
        return;
      case "constant" when !no:
        this.DeclareConstants(imports, line);
        return;
      case "builtin" when !no:
        foreach (var name in words ?? []) {
          if (name.StartsWith(':') && PerlPragmas.MinorVersion(name[1..]) >= 39) {
            foreach (var bundled in PerlPragmas.BuiltinBundle)
              this.ImportBuiltin(bundled);
            continue;
          }
          if (name.Length == 0 || name[0] == ':' || char.IsAsciiDigit(name[0]))
            continue;
          this.ImportBuiltin(name);
        }
        return;
      case "subs" when !no:
        foreach (var name in words ?? [])
          this._subs[this.Qualify(name)] = new KnownSub(null, false);
        return;
    }
    this._usedModules.Add(module);
    if (no || PerlPragmas.IsQuiet(module))
      return;
    if (empty)
      return;
    var defaults = PerlPragmas.DefaultExports(module);
    if (module is "Test::More" or "Test::Simple" && defaults is not null) {
      foreach (var name in defaults)
        this._subs[this.Qualify(name)] = new KnownSub(PerlPragmas.ExportedPrototype(module, name), true);
      return;
    }
    if (imports is null) {
      if (defaults is null) {
        this._unknownImporters.Add(module);
        return;
      }
      foreach (var name in defaults)
        this._subs[this.Qualify(name)] = new KnownSub(PerlPragmas.ExportedPrototype(module, name), true);
      return;
    }
    if (words is null) {
      this._unknownImporters.Add(module);
      return;
    }
    foreach (var word in words) {
      if (word.Length == 0)
        continue;
      if (word[0] is ':' or '-' or '!' or '/' || char.IsAsciiDigit(word[0])) {
        this._unknownImporters.Add(module);
        continue;
      }
      if (word[0] is '$' or '@' or '%' or '*')
        continue;
      var name = word[0] == '&' ? word[1..] : word;
      this._subs[this.Qualify(name)] = new KnownSub(PerlPragmas.ExportedPrototype(module, name), true);
    }
  }

  /// <summary>A function of <c>builtin</c> imported lexically, as <c>use builtin</c> and <c>use v5.40</c> do: a <c>state &amp;name</c> in the pad.</summary>
  private void ImportBuiltin(string name) {
    var lexical = this.Declare('&', name, PerlDeclarator.State);
    this._scope.Names[lexical.PadName] = lexical;
    this._scope.Pending.Remove(lexical);
    this._subs[this.Qualify(name)] = new KnownSub(name is "true" or "false" or "inf" or "nan" ? "" : "$", false);
  }

  private string Qualify(string name) => name.Contains("::", StringComparison.Ordinal) ? name : this.Package + "::" + name;

  private void ApplyFeatures(bool no, IReadOnlyList<string> names, bool experimental) {
    var features = new HashSet<string>(this._scope.Features, StringComparer.Ordinal);
    foreach (var raw in names) {
      var name = raw;
      if (name.StartsWith(':')) {
        var bundle = PerlPragmas.NamedBundle(name[1..]) ?? throw this.Error($"Feature bundle \"{name[1..]}\" is not supported by Perl 5.42", this.Line);
        if (no)
          features.ExceptWith(bundle);
        else
          features.UnionWith(bundle);
        continue;
      }
      if (!PerlPragmas.AllFeatures.Contains(name)) {
        if (experimental || name is "postderef" or "lexical_subs" or "array_base")
          continue;
        throw this.Error($"Feature \"{name}\" is not supported by Perl 5.42", this.Line);
      }
      if (no)
        features.Remove(name);
      else
        features.Add(name);
    }
    if (no && names.Count == 0)
      features = new HashSet<string>(PerlPragmas.DefaultFeatures, StringComparer.Ordinal);
    this._scope.Features = features;
  }

  private void DeclareConstants(PerlExpr? imports, int line) {
    if (imports is PerlAnonHash hash) {
      for (var i = 0; i + 1 < hash.Items.Count; i += 2)
        if (ConstantString(hash.Items[i]) is { } name)
          this.DeclareConstant(name, hash.Items[i + 1]);
      return;
    }
    var items = imports is PerlList list ? list.Items : imports is null ? [] : [imports];
    if (items.Count == 0)
      return;
    if (ConstantString(items[0]) is not { } first)
      return;
    this.DeclareConstant(first, items.Count == 2 ? items[1] : items.Count > 2 ? new PerlList([.. items.Skip(1)]) { Line = line } : null);
  }

  private void DeclareConstant(string name, PerlExpr? value) {
    var qualified = this.Qualify(name);
    this._subs[qualified] = new KnownSub("", false);
    this.Constants[qualified] = value;
  }

  private static string? ConstantString(PerlExpr expression) => expression switch {
    PerlString s => s.Value,
    _ => null
  };

  /// <summary>The words of a constant import list (<c>qw(...)</c>, strings, lists of them); <see langword="null"/> if it is not constant.</summary>
  private static List<string>? ConstantWords(PerlExpr? expression) {
    var words = new List<string>();
    bool Collect(PerlExpr? e) {
      switch (e) {
        case null:
          return true;
        case PerlWords w:
          words.AddRange(w.Words);
          return true;
        case PerlString s:
          words.Add(s.Value);
          return true;
        case PerlNumber n:
          words.Add(n.Value.ToString());
          return true;
        case PerlList l:
          return l.Items.All(Collect);
        default:
          return false;
      }
    }
    return Collect(expression) ? words : null;
  }

}
