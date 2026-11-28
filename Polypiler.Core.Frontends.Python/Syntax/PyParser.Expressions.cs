using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;

namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>The expressions, the call arguments and the targets of the grammar.</summary>
internal sealed partial class PyParser {

  private sealed record KeyValue(PyNode? Key, PyNode Value);

  private sealed record KeywordOrStarred(PyNode Element, bool IsKeyword);

  private sealed record CompareOp(string Op, PyNode Operand);

  // ---------------------------------------------------------------------------------------------
  // Expressions

  private PyNode? Expressions() => this.Rule(() => {
    var start = this.Peek();
    var many = this.Try(() => {
      if (this.Expression() is not { } first)
        return null;

      var rest = this.Repeat(() => this.Try(() => this.Expect(",") is not null ? this.Expression() : null), 1);
      if (rest is null)
        return null;

      this.Expect(",");
      return PyAst.Make("Tuple", this.Extra(start), Nullable(Prepend(first, rest)), PyAst.Singleton("Load"));
    });
    if (many is not null)
      return many;

    var single = this.Try(() => this.Expression() is { } first && this.Expect(",") is not null
      ? PyAst.Make("Tuple", this.Extra(start), new List<PyNode?> { first }, PyAst.Singleton("Load"))
      : null);
    return single ?? this.Expression();
  });

  private PyNode? Expression() => this.Memo(MemoExpression, () => {
    if (this._invalid) {
      this.Try(this.InvalidExpression);
      this.Try(this.InvalidLegacyExpression);
    }

    var start = this.Peek();
    var conditional = this.Try(() => {
      if (this.Disjunction() is not { } body || this.Expect("if") is null || this.Disjunction() is not { } test || this.Expect("else") is null
        || this.Expression() is not { } orElse)
        return null;

      return PyAst.Make("IfExp", this.Extra(start), test, body, orElse);
    });
    if (conditional is not null)
      return conditional;

    return this.Try(this.Disjunction) ?? this.Lambdef();
  });

  private PyNode? ExpressionWithoutInvalid() => this.Rule(() => {
    var invalid = this._invalid;
    this._invalid = false;
    try {
      var start = this.Peek();
      var conditional = this.Try(() => {
        if (this.Disjunction() is not { } body || this.Expect("if") is null || this.Disjunction() is not { } test || this.Expect("else") is null
          || this.Expression() is not { } orElse)
          return null;

        return PyAst.Make("IfExp", this.Extra(start), test, body, orElse);
      });
      return conditional ?? this.Try(this.Disjunction) ?? this.Lambdef();
    } finally {
      this._invalid = invalid;
    }
  });

  private PyNode? YieldExpression() => this.Rule(() => {
    var start = this.Peek();
    var from = this.Try(() => this.Expect("yield") is not null && this.Expect("from") is not null && this.Expression() is { } value
      ? PyAst.Make("YieldFrom", this.Extra(start), value) : null);
    if (from is not null)
      return from;

    if (this.Expect("yield") is null)
      return null;

    var yielded = this.Try(this.StarExpressions);
    return PyAst.Make("Yield", this.Extra(start), yielded);
  });

  private PyNode? StarExpressions() => this.Rule(() => {
    var start = this.Peek();
    var many = this.Try(() => {
      if (this.StarExpression() is not { } first)
        return null;

      var rest = this.Repeat(() => this.Try(() => this.Expect(",") is not null ? this.StarExpression() : null), 1);
      if (rest is null)
        return null;

      this.Expect(",");
      return PyAst.Make("Tuple", this.Extra(start), Nullable(Prepend(first, rest)), PyAst.Singleton("Load"));
    });
    if (many is not null)
      return many;

    var single = this.Try(() => this.StarExpression() is { } first && this.Expect(",") is not null
      ? PyAst.Make("Tuple", this.Extra(start), new List<PyNode?> { first }, PyAst.Singleton("Load"))
      : null);
    return single ?? this.StarExpression();
  });

  private PyNode? StarExpression() => this.Memo(MemoStarExpression, () => {
    var start = this.Peek();
    var starred = this.Try(() => this.Expect("*") is not null && this.BitwiseOr() is { } value
      ? PyAst.Make("Starred", this.Extra(start), value, PyAst.Singleton("Load")) : null);
    return starred ?? this.Expression();
  });

  private List<PyNode>? StarNamedExpressions() => this.Rule(() => {
    var expressions = this.Gather(",", this.StarNamedExpression);
    if (expressions is null)
      return null;

    this.Expect(",");
    return expressions;
  });

  private PyNode? StarNamedExpression() => this.Rule(() => {
    var start = this.Peek();
    var starred = this.Try(() => this.Expect("*") is not null && this.BitwiseOr() is { } value
      ? PyAst.Make("Starred", this.Extra(start), value, PyAst.Singleton("Load")) : null);
    return starred ?? this.NamedExpression();
  });

  private PyNode? AssignmentExpression() => this.Rule(() => {
    var start = this.Peek();
    if (this.NameToken() is not { } name || this.Expect(":=") is null)
      return null;

    // ~: after ':=' the alternative is the only one.
    if (this.Expression() is not { } value)
      return null;

    return this.Version(8, "Assignment expressions are", PyAst.Make("NamedExpr", this.Extra(start), PyAst.SetContext(name, "Store"), value));
  });

  private PyNode? NamedExpression() => this.Rule(() => {
    if (this.Try(this.AssignmentExpression) is { } assignment)
      return assignment;

    if (this._invalid)
      this.Try(this.InvalidNamedExpression);

    return this.Try(() => this.Expression() is { } expression && !this.Check(":=") ? expression : null);
  });

  private PyNode? Disjunction() => this.Memo(MemoDisjunction, () => this.BooleanOperation("or", "Or", this.Conjunction));

  private PyNode? Conjunction() => this.Memo(MemoConjunction, () => this.BooleanOperation("and", "And", this.Inversion));

  private PyNode? BooleanOperation(string keyword, string op, System.Func<PyNode?> operand) {
    var start = this.Peek();
    var chain = this.Try(() => {
      if (operand() is not { } first)
        return null;

      var rest = this.Repeat(() => this.Try(() => this.Expect(keyword) is not null ? operand() : null), 1);
      return rest is null ? null : PyAst.Make("BoolOp", this.Extra(start), PyAst.Singleton(op), Nullable(Prepend(first, rest)));
    });
    return chain ?? operand();
  }

  private PyNode? Inversion() => this.Memo(MemoInversion, () => {
    var start = this.Peek();
    var not = this.Try(() => this.Expect("not") is not null && this.Inversion() is { } operand
      ? PyAst.Make("UnaryOp", this.Extra(start), PyAst.Singleton("Not"), operand) : null);
    return not ?? this.Comparison();
  });

  private PyNode? Comparison() => this.Rule(() => {
    var start = this.Peek();
    var compare = this.Try(() => {
      if (this.BitwiseOr() is not { } left || this.Repeat(this.CompareOpPair, 1) is not { } pairs)
        return null;

      return PyAst.Make("Compare", this.Extra(start), left, pairs.Select(pair => (PyNode?)PyAst.Singleton(pair.Op)).ToList(), pairs.Select(pair => (PyNode?)pair.Operand).ToList());
    });
    return compare ?? this.BitwiseOr();
  });

  private CompareOp? CompareOpPair() => this.Rule(() => {
    CompareOp? Pair(string op, params string[] tokens) => this.Try(() => {
      foreach (var token in tokens)
        if (this.Expect(token) is null)
          return null;

      return this.BitwiseOr() is { } operand ? new CompareOp(op, operand) : null;
    });

    if (Pair("Eq", "==") is { } eq)
      return eq;

    var notEqual = this.Try(() => {
      if (this.Expect("!=") is not { } token)
        return null;

      if (this._barry && token.Source != "<>")
        throw this.Error("with Barry as BDFL, use '<>' instead of '!='");

      if (!this._barry && token.Source != "!=")
        return null;

      return this.BitwiseOr() is { } operand ? new CompareOp("NotEq", operand) : null;
    });
    if (notEqual is not null)
      return notEqual;

    if (Pair("LtE", "<=") is { } lte)
      return lte;

    if (this._invalid)
      this.Try(this.InvalidNotEqual);

    return Pair("Lt", "<") ?? Pair("GtE", ">=") ?? Pair("Gt", ">") ?? Pair("NotIn", "not", "in") ?? Pair("In", "in") ?? Pair("IsNot", "is", "not") ?? Pair("Is", "is");
  });

  /// <summary>A left-recursive binary operator rule, grown as a loop: <c>rule: rule op next | next</c>.</summary>
  private PyNode? Binary(PyToken start, PyNode left, System.Func<PyNode?> next, params (string Token, string Op)[] operators) {
    while (true) {
      PyNode? grown = null;
      foreach (var (token, op) in operators) {
        grown = this.Try(() => {
          if (this.Expect(token) is null || next() is not { } right)
            return null;

          var node = PyAst.Make("BinOp", this.Extra(start), left, PyAst.Singleton(op), right);
          return op == "MatMult" ? this.Version(5, "The '@' operator is", node) : node;
        });
        if (grown is not null)
          break;
      }

      if (grown is null)
        return left;

      left = grown;
    }
  }

  private PyNode? BitwiseOr() => this.Memo(MemoBitwiseOr, () => {
    var start = this.Peek();
    return this.BitwiseXor() is { } left ? this.Binary(start, left, this.BitwiseXor, ("|", "BitOr")) : null;
  });

  private PyNode? BitwiseXor() => this.Memo(MemoBitwiseXor, () => {
    var start = this.Peek();
    return this.BitwiseAnd() is { } left ? this.Binary(start, left, this.BitwiseAnd, ("^", "BitXor")) : null;
  });

  private PyNode? BitwiseAnd() => this.Memo(MemoBitwiseAnd, () => {
    var start = this.Peek();
    return this.ShiftExpression() is { } left ? this.Binary(start, left, this.ShiftExpression, ("&", "BitAnd")) : null;
  });

  private PyNode? ShiftExpression() => this.Memo(MemoShiftExpr, () => {
    var start = this.Peek();
    if (this._invalid)
      this.Try(this.InvalidArithmetic);

    return this.Sum() is { } left ? this.Binary(start, left, this.Sum, ("<<", "LShift"), (">>", "RShift")) : null;
  });

  private PyNode? Sum() => this.Memo(MemoSum, () => {
    var start = this.Peek();
    return this.Term() is { } left ? this.Binary(start, left, this.Term, ("+", "Add"), ("-", "Sub")) : null;
  });

  private PyNode? Term() => this.Memo(MemoTerm, () => {
    var start = this.Peek();
    if (this._invalid)
      this.Try(this.InvalidFactor);

    return this.Factor() is { } left
      ? this.Binary(start, left, this.Factor, ("*", "Mult"), ("/", "Div"), ("//", "FloorDiv"), ("%", "Mod"), ("@", "MatMult"))
      : null;
  });

  private PyNode? Factor() => this.Memo(MemoFactor, () => {
    var start = this.Peek();
    foreach (var (token, op) in new[] { ("+", "UAdd"), ("-", "USub"), ("~", "Invert") }) {
      var unary = this.Try(() => this.Expect(token) is not null && this.Factor() is { } operand
        ? PyAst.Make("UnaryOp", this.Extra(start), PyAst.Singleton(op), operand) : null);
      if (unary is not null)
        return unary;
    }

    return this.Power();
  });

  private PyNode? Power() => this.Rule(() => {
    var start = this.Peek();
    var power = this.Try(() => this.AwaitPrimary() is { } left && this.Expect("**") is not null && this.Factor() is { } right
      ? PyAst.Make("BinOp", this.Extra(start), left, PyAst.Singleton("Pow"), right) : null);
    return power ?? this.AwaitPrimary();
  });

  private PyNode? AwaitPrimary() => this.Memo(MemoAwaitPrimary, () => {
    var start = this.Peek();
    var awaited = this.Try(() => this.Expect("await") is not null && this.Primary() is { } value
      ? this.Version(5, "Await expressions are", PyAst.Make("Await", this.Extra(start), value)) : null);
    return awaited ?? this.Primary();
  });

  private PyNode? Primary() => this.Memo(MemoPrimary, () => {
    var start = this.Peek();
    if (this.Atom() is not { } primary)
      return null;

    while (true) {
      var current = primary;
      var grown = this.Try(() => this.Expect(".") is not null && this.NameToken() is { } name
          ? PyAst.Make("Attribute", this.Extra(start), current, name.Text("id"), PyAst.Singleton("Load")) : null)
        ?? this.Try(() => this.Genexp() is { } generator
          ? PyAst.Make("Call", this.Extra(start), current, new List<PyNode?> { generator }, new List<PyNode?>()) : null)
        ?? this.Try(() => {
          if (this.Expect("(") is null)
            return null;

          var call = this.Try(this.Arguments);
          return this.Expect(")") is null ? null : PyAst.Make("Call", this.Extra(start), current, call?.Get("args") ?? new List<PyNode?>(), call?.Get("keywords") ?? new List<PyNode?>());
        })
        ?? this.Try(() => this.Expect("[") is not null && this.Slices() is { } slice && this.Expect("]") is not null
          ? PyAst.Make("Subscript", this.Extra(start), current, slice, PyAst.Singleton("Load")) : null);
      if (grown is null)
        return primary;

      primary = grown;
    }
  });

  private PyNode? Slices() => this.Rule(() => {
    var start = this.Peek();
    var single = this.Try(() => this.Slice() is { } slice && !this.Check(",") ? slice : null);
    if (single is not null)
      return single;

    var many = this.Gather(",", () => this.Try(this.Slice) ?? this.StarredExpression());
    if (many is null)
      return null;

    this.Expect(",");
    return PyAst.Make("Tuple", this.Extra(start), Nullable(many), PyAst.Singleton("Load"));
  });

  private PyNode? Slice() => this.Rule(() => {
    var start = this.Peek();
    var slice = this.Try(() => {
      var lower = this.Try(this.Expression);
      if (this.Expect(":") is null)
        return null;

      var upper = this.Try(this.Expression);
      var step = this.Try(() => this.Expect(":") is null ? null : new Box<PyNode?>(this.Try(this.Expression)));
      return PyAst.Make("Slice", this.Extra(start), lower, upper, step?.Value);
    });
    return slice ?? this.NamedExpression();
  });

  private PyNode? Atom() => this.Rule(() => {
    var token = this.Peek();
    switch (token.Kind) {
      case PyTokenKind.Name:
        return this.NameToken();
      case PyTokenKind.Keyword when token.Text is "True" or "False" or "None":
        this._mark++;
        return PyAst.Make("Constant", this.Extra(token), token.Text switch { "True" => true, "False" => false, _ => null }, null);
      case PyTokenKind.String or PyTokenKind.FStringStart or PyTokenKind.TStringStart:
        return this.Strings();
      case PyTokenKind.Number:
        return this.NumberToken();
      case PyTokenKind.Operator when token.Text == "(":
        return this.Try(this.Tuple) ?? this.Try(this.Group) ?? this.Genexp();
      case PyTokenKind.Operator when token.Text == "[":
        return this.Try(this.List) ?? this.Listcomp();
      case PyTokenKind.Operator when token.Text == "{":
        return this.Try(this.Dict) ?? this.Try(this.Set) ?? this.Try(this.Dictcomp) ?? this.Setcomp();
      case PyTokenKind.Operator when token.Text == "...":
        this._mark++;
        return PyAst.Make("Constant", this.Extra(token), PyEllipsis.Value, null);
      default:
        return null;
    }
  });

  private PyNode? Group() => this.Rule(() => {
    var inner = this.Try(() => this.Expect("(") is not null && (this.Try(this.YieldExpression) ?? this.NamedExpression()) is { } value && this.Expect(")") is not null
      ? value : null);
    if (inner is not null)
      return inner;

    return this._invalid ? this.InvalidGroup() : null;
  });

  // ---------------------------------------------------------------------------------------------
  // Lambdas

  private PyNode? Lambdef() => this.Rule(() => {
    var start = this.Expect("lambda");
    if (start is null)
      return null;

    var parameters = this.Try(this.LambdaParams);
    if (this.Expect(":") is null || this.Expression() is not { } body)
      return null;

    return PyAst.Make("Lambda", this.Extra(start), parameters ?? this.EmptyArguments(), body);
  });

  private PyNode? LambdaParams() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidLambdaParameters);

    return this.LambdaParameters();
  });

  private PyNode? LambdaParameters() => this.Rule(() => {
    var slashNoDefault = this.Try(() => {
      if (this.LambdaSlashNoDefault() is not { } slash)
        return null;

      var plain = this.Repeat(this.LambdaParamNoDefault, 0)!;
      var withDefault = this.Repeat(this.LambdaParamWithDefault, 0)!;
      var star = this.Try(this.LambdaStarEtc);
      return this.Version(8, "Positional-only parameters are", MakeArguments(slash, null, plain, withDefault, star));
    });
    if (slashNoDefault is not null)
      return slashNoDefault;

    var slashWithDefault = this.Try(() => {
      if (this.LambdaSlashWithDefault() is not { } slash)
        return null;

      var withDefault = this.Repeat(this.LambdaParamWithDefault, 0)!;
      var star = this.Try(this.LambdaStarEtc);
      return this.Version(8, "Positional-only parameters are", MakeArguments(null, slash, null, withDefault, star));
    });
    if (slashWithDefault is not null)
      return slashWithDefault;

    var plainFirst = this.Try(() => {
      if (this.Repeat(this.LambdaParamNoDefault, 1) is not { } plain)
        return null;

      var withDefault = this.Repeat(this.LambdaParamWithDefault, 0)!;
      var star = this.Try(this.LambdaStarEtc);
      return MakeArguments(null, null, plain, withDefault, star);
    });
    if (plainFirst is not null)
      return plainFirst;

    var defaultsFirst = this.Try(() => {
      if (this.Repeat(this.LambdaParamWithDefault, 1) is not { } withDefault)
        return null;

      var star = this.Try(this.LambdaStarEtc);
      return MakeArguments(null, null, null, withDefault, star);
    });
    if (defaultsFirst is not null)
      return defaultsFirst;

    return this.LambdaStarEtc() is { } onlyStar ? MakeArguments(null, null, null, null, onlyStar) : null;
  });

  private List<PyNode>? LambdaSlashNoDefault() => this.Rule(() =>
    this.Try(() => this.Repeat(this.LambdaParamNoDefault, 1) is { } names && this.Expect("/") is not null && this.Expect(",") is not null ? names : null)
    ?? this.Try(() => this.Repeat(this.LambdaParamNoDefault, 1) is { } names && this.Expect("/") is not null && this.Check(":") ? names : null));

  private SlashDefaults? LambdaSlashWithDefault() => this.Rule(() =>
    this.Try(() => this.Repeat(this.LambdaParamNoDefault, 0) is { } plain && this.Repeat(this.LambdaParamWithDefault, 1) is { } named && this.Expect("/") is not null
      && this.Expect(",") is not null ? new SlashDefaults(plain, named) : null)
    ?? this.Try(() => this.Repeat(this.LambdaParamNoDefault, 0) is { } plain && this.Repeat(this.LambdaParamWithDefault, 1) is { } named && this.Expect("/") is not null
      && this.Check(":") ? new SlashDefaults(plain, named) : null));

  private StarParameters? LambdaStarEtc() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidLambdaStarEtc);

    var named = this.Try(() => {
      if (this.Expect("*") is null || this.LambdaParamNoDefault() is not { } vararg)
        return null;

      var keywords = this.Repeat(this.LambdaParamMaybeDefault, 0)!;
      return new StarParameters(vararg, keywords, this.Try(this.LambdaKwds));
    });
    if (named is not null)
      return named;

    var bare = this.Try(() => {
      if (this.Expect("*") is null || this.Expect(",") is null || this.Repeat(this.LambdaParamMaybeDefault, 1) is not { } keywords)
        return null;

      return new StarParameters(null, keywords, this.Try(this.LambdaKwds));
    });
    if (bare is not null)
      return bare;

    return this.LambdaKwds() is { } kwarg ? new StarParameters(null, null, kwarg) : null;
  });

  private PyNode? LambdaKwds() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidLambdaKwds);

    return this.Expect("**") is null ? null : this.LambdaParamNoDefault();
  });

  private PyNode? LambdaParamNoDefault() => this.Rule(() =>
    this.Try(() => this.LambdaParam() is { } param && this.Expect(",") is not null ? param : null)
    ?? this.Try(() => this.LambdaParam() is { } param && this.Check(":") ? param : null));

  private NameDefault? LambdaParamWithDefault() => this.Rule(() =>
    this.Try(() => this.LambdaParam() is { } param && this.Default() is { } value && this.Expect(",") is not null ? new NameDefault(param, value) : null)
    ?? this.Try(() => this.LambdaParam() is { } param && this.Default() is { } value && this.Check(":") ? new NameDefault(param, value) : null));

  private NameDefault? LambdaParamMaybeDefault() => this.Rule(() =>
    this.Try(() => this.LambdaParam() is { } param && this.Try(this.Default) is var value && this.Expect(",") is not null ? new NameDefault(param, value) : null)
    ?? this.Try(() => this.LambdaParam() is { } param && this.Try(this.Default) is var value && this.Check(":") ? new NameDefault(param, value) : null));

  private PyNode? LambdaParam() => this.Rule(() => {
    var start = this.Peek();
    return this.NameToken() is { } name ? PyAst.Make("arg", this.Extra(start), name.Text("id"), null, null) : null;
  });

  // ---------------------------------------------------------------------------------------------
  // Displays and comprehensions

  private PyNode? List() => this.Rule(() => {
    var start = this.Expect("[");
    if (start is null)
      return null;

    var elements = this.Try(this.StarNamedExpressions);
    return this.Expect("]") is null ? null : PyAst.Make("List", this.Extra(start), Nullable(elements ?? []), PyAst.Singleton("Load"));
  });

  private PyNode? Tuple() => this.Rule(() => {
    var start = this.Expect("(");
    if (start is null)
      return null;

    var elements = this.Try(() => {
      if (this.StarNamedExpression() is not { } first || this.Expect(",") is null)
        return null;

      return Prepend(first, this.Try(this.StarNamedExpressions));
    });
    return this.Expect(")") is null ? null : PyAst.Make("Tuple", this.Extra(start), Nullable(elements ?? []), PyAst.Singleton("Load"));
  });

  private PyNode? Set() => this.Rule(() => {
    var start = this.Expect("{");
    if (start is null || this.StarNamedExpressions() is not { } elements || this.Expect("}") is null)
      return null;

    return PyAst.Make("Set", this.Extra(start), Nullable(elements));
  });

  private PyNode? Dict() => this.Rule(() => {
    var start = this.Peek();
    var dict = this.Try(() => {
      if (this.Expect("{") is null)
        return null;

      var pairs = this.Try(this.DoubleStarredKeyValues) ?? [];
      return this.Expect("}") is null ? null : PyAst.Make("Dict", this.Extra(start), pairs.Select(pair => pair.Key).ToList(), pairs.Select(pair => (PyNode?)pair.Value).ToList());
    });
    if (dict is not null)
      return dict;

    return this._invalid ? this.Try(() => this.Expect("{") is not null && this.InvalidDoubleStarredKeyValues() is { } invalid && this.Expect("}") is not null ? invalid : null) : null;
  });

  private List<KeyValue>? DoubleStarredKeyValues() => this.Rule(() => {
    var pairs = this.Gather(",", this.DoubleStarredKeyValue);
    if (pairs is null)
      return null;

    this.Expect(",");
    return pairs;
  });

  private KeyValue? DoubleStarredKeyValue() => this.Rule(() =>
    this.Try(() => this.Expect("**") is not null && this.BitwiseOr() is { } value ? new KeyValue(null, value) : null) ?? this.KeyValuePair());

  private KeyValue? KeyValuePair() => this.Rule(() =>
    this.Expression() is { } key && this.Expect(":") is not null && this.Expression() is { } value ? new KeyValue(key, value) : null);

  private List<PyNode>? ForIfClauses() => this.Rule(() => this.Repeat(this.ForIfClause, 1));

  private PyNode? ForIfClause() => this.Rule(() => {
    var cut = false;
    foreach (var async in new[] { true, false }) {
      var clause = this.Try(() => {
        if (async && this.Expect("async") is null)
          return null;

        if (this.Expect("for") is null || this.StarTargets() is not { } target || this.Expect("in") is null)
          return null;

        cut = true;
        if (this.Disjunction() is not { } iterable)
          return null;

        var conditions = this.Repeat(() => this.Try(() => this.Expect("if") is not null ? this.Disjunction() : null), 0)!;
        var node = PyAst.Make("comprehension", null, target, iterable, Nullable(conditions), async ? 1 : 0);
        return async ? this.Version(6, "Async comprehensions are", node) : node;
      });
      if (clause is not null || cut)
        return clause;
    }

    if (!this._invalid)
      return null;

    return this.Try(this.InvalidForIfClause) ?? this.InvalidForTarget();
  });

  private PyNode? Listcomp() => this.Rule(() => {
    var start = this.Peek();
    var comprehension = this.Try(() => this.Expect("[") is not null && this.NamedExpression() is { } element && this.ForIfClauses() is { } generators
      && this.Expect("]") is not null ? PyAst.Make("ListComp", this.Extra(start), element, Nullable(generators)) : null);
    if (comprehension is not null)
      return comprehension;

    return this._invalid ? this.InvalidComprehension() : null;
  });

  private PyNode? Setcomp() => this.Rule(() => {
    var start = this.Peek();
    var comprehension = this.Try(() => this.Expect("{") is not null && this.NamedExpression() is { } element && this.ForIfClauses() is { } generators
      && this.Expect("}") is not null ? PyAst.Make("SetComp", this.Extra(start), element, Nullable(generators)) : null);
    if (comprehension is not null)
      return comprehension;

    return this._invalid ? this.InvalidComprehension() : null;
  });

  private PyNode? Genexp() => this.Rule(() => {
    var start = this.Peek();
    var generator = this.Try(() => {
      if (this.Expect("(") is null)
        return null;

      var element = this.Try(this.AssignmentExpression) ?? this.Try(() => this.Expression() is { } expression && !this.Check(":=") ? expression : null);
      if (element is null || this.ForIfClauses() is not { } generators || this.Expect(")") is null)
        return null;

      return PyAst.Make("GeneratorExp", this.Extra(start), element, Nullable(generators));
    });
    if (generator is not null)
      return generator;

    return this._invalid ? this.InvalidComprehension() : null;
  });

  private PyNode? Dictcomp() => this.Rule(() => {
    var start = this.Peek();
    var comprehension = this.Try(() => this.Expect("{") is not null && this.KeyValuePair() is { } pair && this.ForIfClauses() is { } generators
      && this.Expect("}") is not null ? PyAst.Make("DictComp", this.Extra(start), pair.Key, pair.Value, Nullable(generators)) : null);
    if (comprehension is not null)
      return comprehension;

    return this._invalid ? this.InvalidDictComprehension() : null;
  });

  // ---------------------------------------------------------------------------------------------
  // Call arguments

  private PyNode? Arguments() => this.Memo(MemoArguments, () => {
    var arguments = this.Try(() => {
      if (this.Args() is not { } call)
        return null;

      this.Expect(",");
      return this.Check(")") ? call : null;
    });
    if (arguments is not null)
      return arguments;

    return this._invalid ? this.InvalidArguments() : null;
  });

  /// <summary>One positional argument of <c>args</c>: <c>starred_expression | (assignment_expression | expression !':=') !'='</c>.</summary>
  private PyNode? PositionalArgument() => this.Try(this.StarredExpression)
    ?? this.Try(() => (this.Try(this.AssignmentExpression) ?? this.Try(() => this.Expression() is { } expression && !this.Check(":=") ? expression : null)) is { } argument
      && !this.Check("=") ? argument : null);

  private PyNode? Args() => this.Rule(() => {
    var start = this.Peek();
    var positional = this.Try(() => {
      if (this.Gather(",", this.PositionalArgument) is not { } arguments)
        return null;

      var keywords = this.Try(() => this.Expect(",") is null ? null : this.Kwargs());
      return this.CollectCall(start, arguments, keywords);
    });
    if (positional is not null)
      return positional;

    return this.Kwargs() is { } only ? this.CollectCall(start, [], only) : null;
  });

  private PyNode CollectCall(PyToken start, List<PyNode> arguments, List<KeywordOrStarred>? keywords) {
    var args = Nullable(arguments);
    args.AddRange(keywords?.Where(item => !item.IsKeyword).Select(item => (PyNode?)item.Element) ?? []);
    var keywordNodes = keywords?.Where(item => item.IsKeyword).Select(item => (PyNode?)item.Element).ToList() ?? [];
    return PyAst.Make("Call", this.Extra(start), PyAst.Make("Name", null, "", PyAst.Singleton("Load")), args, keywordNodes);
  }

  private List<KeywordOrStarred>? Kwargs() => this.Rule(() => {
    var both = this.Try(() => {
      if (this.Gather(",", this.KwargOrStarred) is not { } starred || this.Expect(",") is null || this.Gather(",", this.KwargOrDoubleStarred) is not { } doubled)
        return null;

      return starred.Concat(doubled).ToList();
    });
    return both ?? this.Try(() => this.Gather(",", this.KwargOrStarred)) ?? this.Gather(",", this.KwargOrDoubleStarred);
  });

  private PyNode? StarredExpression() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidStarredExpressionUnpacking);

    var start = this.Peek();
    var starred = this.Try(() => this.Expect("*") is not null && this.Expression() is { } value
      ? PyAst.Make("Starred", this.Extra(start), value, PyAst.Singleton("Load")) : null);
    if (starred is not null)
      return starred;

    return this._invalid ? this.InvalidStarredExpression() : null;
  });

  private KeywordOrStarred? KwargOrStarred() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidKwarg);

    var start = this.Peek();
    var keyword = this.Try(() => this.NameToken() is { } name && this.Expect("=") is not null && this.Expression() is { } value
      ? new KeywordOrStarred(PyAst.Make("keyword", this.Extra(start), name.Text("id"), value), true) : null);
    if (keyword is not null)
      return keyword;

    return this.StarredExpression() is { } starred ? new KeywordOrStarred(starred, false) : null;
  });

  private KeywordOrStarred? KwargOrDoubleStarred() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidKwarg);

    var start = this.Peek();
    var keyword = this.Try(() => this.NameToken() is { } name && this.Expect("=") is not null && this.Expression() is { } value
      ? new KeywordOrStarred(PyAst.Make("keyword", this.Extra(start), name.Text("id"), value), true) : null);
    if (keyword is not null)
      return keyword;

    return this.Try(() => this.Expect("**") is not null && this.Expression() is { } value
      ? new KeywordOrStarred(PyAst.Make("keyword", this.Extra(start), null, value), true) : null);
  });

  // ---------------------------------------------------------------------------------------------
  // Targets

  private PyNode? StarTargets() => this.Rule(() => {
    var start = this.Peek();
    var single = this.Try(() => this.StarTarget() is { } target && !this.Check(",") ? target : null);
    if (single is not null)
      return single;

    return this.Try(() => {
      if (this.StarTarget() is not { } first)
        return null;

      var rest = this.Repeat(() => this.Try(() => this.Expect(",") is not null ? this.StarTarget() : null), 0)!;
      this.Expect(",");
      return PyAst.Make("Tuple", this.Extra(start), Nullable(Prepend(first, rest)), PyAst.Singleton("Store"));
    });
  });

  private List<PyNode>? StarTargetsListSeq() => this.Rule(() => {
    var targets = this.Gather(",", this.StarTarget);
    if (targets is null)
      return null;

    this.Expect(",");
    return targets;
  });

  private List<PyNode>? StarTargetsTupleSeq() => this.Rule(() => {
    var many = this.Try(() => {
      if (this.StarTarget() is not { } first || this.Repeat(() => this.Try(() => this.Expect(",") is not null ? this.StarTarget() : null), 1) is not { } rest)
        return null;

      this.Expect(",");
      return Prepend(first, rest);
    });
    return many ?? this.Try(() => this.StarTarget() is { } first && this.Expect(",") is not null ? new List<PyNode> { first } : null);
  });

  private PyNode? StarTarget() => this.Memo(MemoStarTarget, () => {
    var start = this.Peek();
    var starred = this.Try(() => {
      if (this.Expect("*") is null || this.Check("*") || this.StarTarget() is not { } target)
        return null;

      return PyAst.Make("Starred", this.Extra(start), PyAst.SetContext(target, "Store"), PyAst.Singleton("Store"));
    });
    return starred ?? this.TargetWithStarAtom();
  });

  private PyNode? TargetWithStarAtom() => this.Memo(MemoTargetWithStarAtom, () => this.Try(() => this.SubscriptAttributeTarget("Store")) ?? this.StarAtom());

  /// <summary><c>t_primary '.' NAME !t_lookahead</c> and <c>t_primary '[' slices ']' !t_lookahead</c> in a context.</summary>
  private PyNode? SubscriptAttributeTarget(string context) {
    var start = this.Peek();
    var attribute = this.Try(() => this.TPrimary() is { } value && this.Expect(".") is not null && this.NameToken() is { } name && !this.TLookahead()
      ? PyAst.Make("Attribute", this.Extra(start), value, name.Text("id"), PyAst.Singleton(context)) : null);
    if (attribute is not null)
      return attribute;

    return this.Try(() => this.TPrimary() is { } value && this.Expect("[") is not null && this.Slices() is { } slice && this.Expect("]") is not null && !this.TLookahead()
      ? PyAst.Make("Subscript", this.Extra(start), value, slice, PyAst.Singleton(context)) : null);
  }

  private PyNode? StarAtom() => this.Rule(() => {
    var start = this.Peek();
    if (this.Try(this.NameToken) is { } name)
      return PyAst.SetContext(name, "Store");

    var inner = this.Try(() => this.Expect("(") is not null && this.TargetWithStarAtom() is { } target && this.Expect(")") is not null ? PyAst.SetContext(target, "Store") : null);
    if (inner is not null)
      return inner;

    var tuple = this.Try(() => {
      if (this.Expect("(") is null)
        return null;

      var elements = this.Try(this.StarTargetsTupleSeq);
      return this.Expect(")") is null ? null : PyAst.Make("Tuple", this.Extra(start), Nullable(elements ?? []), PyAst.Singleton("Store"));
    });
    if (tuple is not null)
      return tuple;

    return this.Try(() => {
      if (this.Expect("[") is null)
        return null;

      var elements = this.Try(this.StarTargetsListSeq);
      return this.Expect("]") is null ? null : PyAst.Make("List", this.Extra(start), Nullable(elements ?? []), PyAst.Singleton("Store"));
    });
  });

  private PyNode? SingleTarget() => this.Rule(() => {
    if (this.Try(this.SingleSubscriptAttributeTarget) is { } target)
      return target;

    if (this.Try(this.NameToken) is { } name)
      return PyAst.SetContext(name, "Store");

    return this.Try(() => this.Expect("(") is not null && this.SingleTarget() is { } inner && this.Expect(")") is not null ? inner : null);
  });

  private PyNode? SingleSubscriptAttributeTarget() => this.Rule(() => this.SubscriptAttributeTarget("Store"));

  private bool TLookahead() => this.CheckAny("(", "[", ".");

  private PyNode? TPrimary() => this.Memo(MemoTPrimary, () => {
    var start = this.Peek();
    if (this.Atom() is not { } primary || !this.TLookahead())
      return null;

    while (true) {
      var current = primary;
      var grown = this.Try(() => this.Expect(".") is not null && this.NameToken() is { } name && this.TLookahead()
          ? PyAst.Make("Attribute", this.Extra(start), current, name.Text("id"), PyAst.Singleton("Load")) : null)
        ?? this.Try(() => this.Expect("[") is not null && this.Slices() is { } slice && this.Expect("]") is not null && this.TLookahead()
          ? PyAst.Make("Subscript", this.Extra(start), current, slice, PyAst.Singleton("Load")) : null)
        ?? this.Try(() => this.Genexp() is { } generator && this.TLookahead()
          ? PyAst.Make("Call", this.Extra(start), current, new List<PyNode?> { generator }, new List<PyNode?>()) : null)
        ?? this.Try(() => {
          if (this.Expect("(") is null)
            return null;

          var call = this.Try(this.Arguments);
          return this.Expect(")") is null || !this.TLookahead()
            ? null
            : PyAst.Make("Call", this.Extra(start), current, call?.Get("args") ?? new List<PyNode?>(), call?.Get("keywords") ?? new List<PyNode?>());
        });
      if (grown is null)
        return primary;

      primary = grown;
    }
  });

  private List<PyNode>? DelTargets() => this.Rule(() => {
    var targets = this.Gather(",", this.DelTarget);
    if (targets is null)
      return null;

    this.Expect(",");
    return targets;
  });

  private PyNode? DelTarget() => this.Memo(MemoDelTarget, () => this.Try(() => this.SubscriptAttributeTarget("Del")) ?? this.DelTAtom());

  private PyNode? DelTAtom() => this.Rule(() => {
    var start = this.Peek();
    if (this.Try(this.NameToken) is { } name)
      return PyAst.SetContext(name, "Del");

    var inner = this.Try(() => this.Expect("(") is not null && this.DelTarget() is { } target && this.Expect(")") is not null ? PyAst.SetContext(target, "Del") : null);
    if (inner is not null)
      return inner;

    var tuple = this.Try(() => {
      if (this.Expect("(") is null)
        return null;

      var elements = this.Try(this.DelTargets);
      return this.Expect(")") is null ? null : PyAst.Make("Tuple", this.Extra(start), Nullable(elements ?? []), PyAst.Singleton("Del"));
    });
    if (tuple is not null)
      return tuple;

    return this.Try(() => {
      if (this.Expect("[") is null)
        return null;

      var elements = this.Try(this.DelTargets);
      return this.Expect("]") is null ? null : PyAst.Make("List", this.Extra(start), Nullable(elements ?? []), PyAst.Singleton("Del"));
    });
  });

}
