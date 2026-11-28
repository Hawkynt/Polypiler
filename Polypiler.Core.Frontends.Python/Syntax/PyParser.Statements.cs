using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;

namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>The statements of the grammar: <c>file</c> to <c>finally_block</c>, and the parameters.</summary>
internal sealed partial class PyParser {

  private sealed record NameDefault(PyNode Arg, PyNode? Value);

  private sealed record SlashDefaults(List<PyNode> PlainNames, List<NameDefault> NamesWithDefaults);

  private sealed record StarParameters(PyNode? VarArg, List<NameDefault>? KwOnly, PyNode? KwArg);

  private sealed class Box<T>(T value) {
    public T Value { get; } = value;
  }

  // Rule numbers of the memoized rules.
  private const int MemoSimpleStmt = 1, MemoBlock = 2, MemoExpression = 3, MemoStarExpression = 4, MemoDisjunction = 5, MemoConjunction = 6,
    MemoInversion = 7, MemoFactor = 8, MemoAwaitPrimary = 9, MemoClosedPattern = 10, MemoStarPattern = 11, MemoTypeParam = 12, MemoStarTarget = 13,
    MemoTargetWithStarAtom = 14, MemoDelTarget = 15, MemoArguments = 16, MemoStrings = 17, MemoTString = 18, MemoInvalidNamedExpression = 19,
    MemoBitwiseOr = 20, MemoBitwiseXor = 21, MemoBitwiseAnd = 22, MemoShiftExpr = 23, MemoSum = 24, MemoTerm = 25, MemoPrimary = 26,
    MemoTPrimary = 27, MemoDottedName = 28;

  private PyNode? File() => this.Rule(() => {
    var statements = this.Statements() ?? [];
    if (this.Expect(PyTokenKind.EndMarker) is null)
      return null;

    return PyAst.Make("Module", null, Nullable(statements), new List<PyNode?>());
  });

  private List<PyNode>? Statements() => this.Rule(() => {
    var lists = this.Repeat(this.Statement, 1);
    return lists?.SelectMany(list => list).ToList();
  });

  private List<PyNode>? Statement() => this.Rule(() => {
    if (this.Try(this.CompoundStatement) is { } compound)
      return [compound];

    return this.SimpleStatements();
  });

  private List<PyNode>? SimpleStatements() => this.Rule(() => {
    var single = this.Try(() => this.SimpleStatement() is { } statement && !this.Check(";") && this.Expect(PyTokenKind.Newline) is not null ? new List<PyNode> { statement } : null);
    if (single is not null)
      return single;

    return this.Try(() => {
      var statements = this.Gather(";", this.SimpleStatement);
      if (statements is null)
        return null;

      this.Expect(";");
      return this.Expect(PyTokenKind.Newline) is null ? null : statements;
    });
  });

  private PyNode? SimpleStatement() => this.Memo(MemoSimpleStmt, () => {
    var start = this.Peek();
    if (this.Try(this.Assignment) is { } assignment)
      return assignment;

    if (this.CheckSoftKeyword("type") && this.Try(this.TypeAlias) is { } alias)
      return alias;

    if (this.Try(this.StarExpressions) is { } expression)
      return PyAst.Make("Expr", this.Extra(start), expression);

    var token = this.Peek();
    if (token.Kind != PyTokenKind.Keyword)
      return null;

    return token.Text switch {
      "return" => this.Try(this.ReturnStatement),
      "import" or "from" => this.Try(this.ImportStatement),
      "raise" => this.Try(this.RaiseStatement),
      "pass" => this.Try(() => this.Expect("pass") is { } pass ? PyAst.Make("Pass", this.Extra(pass)) : null),
      "del" => this.Try(this.DeleteStatement),
      "yield" => this.Try(() => this.YieldExpression() is { } value ? PyAst.Make("Expr", this.Extra(start), value) : null),
      "assert" => this.Try(this.AssertStatement),
      "break" => this.Try(() => this.Expect("break") is { } brk ? PyAst.Make("Break", this.Extra(brk)) : null),
      "continue" => this.Try(() => this.Expect("continue") is { } cont ? PyAst.Make("Continue", this.Extra(cont)) : null),
      "global" or "nonlocal" => this.Try(() => this.GlobalStatement(token.Text)),
      _ => null
    };
  });

  private PyNode? CompoundStatement() => this.Rule(() => {
    var token = this.Peek();
    if (token.Kind == PyTokenKind.Keyword || (token.Kind == PyTokenKind.Operator && token.Text == "@")) {
      if (token.Text is "def" or "@" or "async" && this.Try(this.FunctionDef) is { } function)
        return function;

      if (token.Text == "if" && this.Try(this.IfStatement) is { } ifStatement)
        return ifStatement;

      if (token.Text is "class" or "@" && this.Try(this.ClassDef) is { } classDef)
        return classDef;

      if (token.Text is "with" or "async" && this.Try(this.WithStatement) is { } with)
        return with;

      if (token.Text is "for" or "async" && this.Try(this.ForStatement) is { } forStatement)
        return forStatement;

      if (token.Text == "try" && this.Try(this.TryStatement) is { } tryStatement)
        return tryStatement;

      if (token.Text == "while" && this.Try(this.WhileStatement) is { } whileStatement)
        return whileStatement;
    }

    return this.MatchStatement();
  });

  // ---------------------------------------------------------------------------------------------
  // Simple statements

  private PyNode? Assignment() => this.Rule(() => {
    var start = this.Peek();
    var annotated = this.Try(() => {
      var name = this.NameToken();
      if (name is null || this.Expect(":") is null || this.Expression() is not { } annotation)
        return null;

      var value = this.Try(() => this.Expect("=") is null ? null : this.AnnotatedRhs());
      return this.Version(6, "Variable annotation syntax is", PyAst.Make("AnnAssign", this.Extra(start), PyAst.SetContext(name, "Store"), annotation, value, 1));
    });
    if (annotated is not null)
      return annotated;

    var complex = this.Try(() => {
      var target = this.Try(() => this.Expect("(") is not null && this.SingleTarget() is { } inner && this.Expect(")") is not null ? inner : null)
        ?? this.SingleSubscriptAttributeTarget();
      if (target is null || this.Expect(":") is null || this.Expression() is not { } annotation)
        return null;

      var value = this.Try(() => this.Expect("=") is null ? null : this.AnnotatedRhs());
      return this.Version(6, "Variable annotations syntax is", PyAst.Make("AnnAssign", this.Extra(start), target, annotation, value, 0));
    });
    if (complex is not null)
      return complex;

    var assign = this.Try(() => {
      var targets = this.Repeat(() => this.Try(() => this.StarTargets() is { } target && this.Expect("=") is not null ? target : null), 1);
      if (targets is null || this.AnnotatedRhs() is not { } value || this.Check("="))
        return null;

      return PyAst.Make("Assign", this.Extra(start), Nullable(targets), value, null);
    });
    if (assign is not null)
      return assign;

    var cut = false;
    var augmented = this.Try(() => {
      if (this.SingleTarget() is not { } target || this.AugAssign() is not { } op)
        return null;

      cut = true;
      return this.AnnotatedRhs() is { } value ? PyAst.Make("AugAssign", this.Extra(start), target, op, value) : null;
    });
    if (augmented is not null || cut)
      return augmented;

    return this._invalid ? this.InvalidAssignment() : null;
  });

  private PyNode? AnnotatedRhs() => this.Rule(() => this.Try(this.YieldExpression) ?? this.StarExpressions());

  private PyNode? AugAssign() {
    var token = this.Peek();
    if (token.Kind != PyTokenKind.Operator)
      return null;

    var op = token.Text switch {
      "+=" => "Add",
      "-=" => "Sub",
      "*=" => "Mult",
      "@=" => "MatMult",
      "/=" => "Div",
      "%=" => "Mod",
      "&=" => "BitAnd",
      "|=" => "BitOr",
      "^=" => "BitXor",
      "<<=" => "LShift",
      ">>=" => "RShift",
      "**=" => "Pow",
      "//=" => "FloorDiv",
      _ => null
    };
    if (op is null)
      return null;

    this._mark++;
    return op == "MatMult" ? this.Version(5, "The '@' operator is", PyAst.Singleton(op)) : PyAst.Singleton(op);
  }

  private PyNode? ReturnStatement() => this.Rule(() => {
    var start = this.Expect("return");
    if (start is null)
      return null;

    var value = this.Try(this.StarExpressions);
    return PyAst.Make("Return", this.Extra(start), value);
  });

  private PyNode? RaiseStatement() => this.Rule(() => {
    var start = this.Peek();
    var full = this.Try(() => {
      if (this.Expect("raise") is null || this.Expression() is not { } exception)
        return null;

      var cause = this.Try(() => this.Expect("from") is null ? null : this.Expression());
      return PyAst.Make("Raise", this.Extra(start), exception, cause);
    });
    if (full is not null)
      return full;

    return this.Expect("raise") is null ? null : PyAst.Make("Raise", this.Extra(start), null, null);
  });

  private PyNode? GlobalStatement(string keyword) => this.Rule(() => {
    var start = this.Expect(keyword);
    if (start is null || this.Gather(",", this.NameToken) is not { } names)
      return null;

    return PyAst.Make(keyword == "global" ? "Global" : "Nonlocal", this.Extra(start), names.Select(name => name.Text("id")!).ToList());
  });

  private PyNode? DeleteStatement() => this.Rule(() => {
    var start = this.Peek();
    var delete = this.Try(() => {
      if (this.Expect("del") is null || this.DelTargets() is not { } targets)
        return null;

      return this.Check(";") || this.Check(PyTokenKind.Newline) ? PyAst.Make("Delete", this.Extra(start), Nullable(targets)) : null;
    });
    if (delete is not null)
      return delete;

    return this._invalid ? this.InvalidDelStatement() : null;
  });

  private PyNode? AssertStatement() => this.Rule(() => {
    var start = this.Expect("assert");
    if (start is null || this.Expression() is not { } test)
      return null;

    var message = this.Try(() => this.Expect(",") is null ? null : this.Expression());
    return PyAst.Make("Assert", this.Extra(start), test, message);
  });

  private PyNode? ImportStatement() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidImport);

    return this.Try(this.ImportName) ?? this.ImportFrom();
  });

  private PyNode? ImportName() => this.Rule(() => {
    var start = this.Expect("import");
    return start is not null && this.DottedAsNames() is { } names ? PyAst.Make("Import", this.Extra(start), Nullable(names)) : null;
  });

  private int? Dots(int minimum) {
    var dots = 0;
    var count = 0;
    while (true) {
      if (this.Expect(".") is not null) {
        dots++;
      } else if (this.Expect("...") is not null) {
        dots += 3;
      } else {
        break;
      }

      count++;
    }

    return count >= minimum ? dots : null;
  }

  private PyNode? ImportFrom() => this.Rule(() => {
    var start = this.Peek();
    var named = this.Try(() => {
      if (this.Expect("from") is null || this.Dots(0) is not { } dots || this.DottedName() is not { } module || this.Expect("import") is null
        || this.ImportFromTargets() is not { } names)
        return null;

      var id = module.Text("id")!;
      if (dots == 0 && id == "__future__" && names.Any(alias => alias.Text("name") == "barry_as_FLUFL")) {
        this._barry = true;
        this._lexer.BarryAsBdfl = true;
      }

      return PyAst.Make("ImportFrom", this.Extra(start), id, Nullable(names), dots);
    });
    if (named is not null)
      return named;

    return this.Try(() => {
      if (this.Expect("from") is null || this.Dots(1) is not { } dots || this.Expect("import") is null || this.ImportFromTargets() is not { } names)
        return null;

      return PyAst.Make("ImportFrom", this.Extra(start), null, Nullable(names), dots);
    });
  });

  private List<PyNode>? ImportFromTargets() => this.Rule(() => {
    var start = this.Peek();
    var parenthesized = this.Try(() => {
      if (this.Expect("(") is null || this.ImportFromAsNames() is not { } names)
        return null;

      this.Expect(",");
      return this.Expect(")") is null ? null : names;
    });
    if (parenthesized is not null)
      return parenthesized;

    var plain = this.Try(() => this.ImportFromAsNames() is { } names && !this.Check(",") ? names : null);
    if (plain is not null)
      return plain;

    var star = this.Try(() => this.Expect("*") is null ? null : new List<PyNode> { PyAst.Make("alias", this.Extra(start), "*", null) });
    if (star is not null)
      return star;

    return this._invalid ? this.InvalidImportFromTargets() : null;
  });

  private List<PyNode>? ImportFromAsNames() => this.Rule(() => this.Gather(",", this.ImportFromAsName));

  private PyNode? ImportFromAsName() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidImportFromAsName);

    var start = this.Peek();
    if (this.NameToken() is not { } name)
      return null;

    var alias = this.Try(() => this.Expect("as") is null ? null : this.NameToken());
    return PyAst.Make("alias", this.Extra(start), name.Text("id"), alias?.Text("id"));
  });

  private List<PyNode>? DottedAsNames() => this.Rule(() => this.Gather(",", this.DottedAsName));

  private PyNode? DottedAsName() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidDottedAsName);

    var start = this.Peek();
    if (this.DottedName() is not { } name)
      return null;

    var alias = this.Try(() => this.Expect("as") is null ? null : this.NameToken());
    return PyAst.Make("alias", this.Extra(start), name.Text("id"), alias?.Text("id"));
  });

  private PyNode? DottedName() => this.Memo(MemoDottedName, () => {
    if (this.NameToken() is not { } name)
      return null;

    while (true) {
      var next = this.Try(() => this.Expect(".") is not null && this.NameToken() is { } part
        ? PyAst.Make("Name", this.Span(name, part), name.Text("id") + "." + part.Text("id"), PyAst.Singleton("Load"))
        : null);
      if (next is null)
        return name;

      name = next;
    }
  });

  // ---------------------------------------------------------------------------------------------
  // Blocks, classes and functions

  private List<PyNode>? Block() => this.Memo(MemoBlock, () => {
    var indented = this.Try(() => {
      if (this.Expect(PyTokenKind.Newline) is null || this.Expect(PyTokenKind.Indent) is null || this.Statements() is not { } statements)
        return null;

      return this.Expect(PyTokenKind.Dedent) is null ? null : statements;
    });
    if (indented is not null)
      return indented;

    if (this.Try(this.SimpleStatements) is { } simple)
      return simple;

    return this._invalid ? this.InvalidBlock() : null;
  });

  private List<PyNode>? Decorators() => this.Rule(() => this.Repeat(() => this.Try(() =>
    this.Expect("@") is not null && this.NamedExpression() is { } decorator && this.Expect(PyTokenKind.Newline) is not null ? decorator : null), 1));

  private PyNode? ClassDef() => this.Rule(() => {
    var decorated = this.Try(() => this.Decorators() is { } decorators && this.ClassDefRaw() is { } classDef
      ? classDef.With("decorator_list", Nullable(decorators))
      : null);
    return decorated ?? this.ClassDefRaw();
  });

  private PyNode? ClassDefRaw() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidClassDefRaw);

    var start = this.Expect("class");
    if (start is null || this.NameToken() is not { } name)
      return null;

    var typeParams = this.Try(this.TypeParams);
    var arguments = this.Try(() => {
      if (this.Expect("(") is null)
        return null;

      var call = this.Try(this.Arguments);
      return this.Expect(")") is null ? null : new Box<PyNode?>(call);
    });
    if (this.Expect(":") is null || this.Block() is not { } body)
      return null;

    var call = arguments?.Value;
    return PyAst.Make("ClassDef", this.Extra(start), name.Text("id"), call?.Get("args") ?? new List<PyNode?>(), call?.Get("keywords") ?? new List<PyNode?>(),
      Nullable(body), new List<PyNode?>(), typeParams ?? new List<PyNode?>());
  });

  private PyNode? FunctionDef() => this.Rule(() => {
    var decorated = this.Try(() => this.Decorators() is { } decorators && this.FunctionDefRaw() is { } function
      ? function.With("decorator_list", Nullable(decorators))
      : null);
    return decorated ?? this.FunctionDefRaw();
  });

  private PyNode? FunctionDefRaw() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidDefRaw);

    return this.Try(() => this.FunctionDefinition(false)) ?? this.Try(() => this.FunctionDefinition(true));
  });

  private PyNode? FunctionDefinition(bool async) {
    var start = this.Peek();
    if (async && this.Expect("async") is null)
      return null;

    if (this.Expect("def") is null || this.NameToken() is not { } name)
      return null;

    var typeParams = this.Try(this.TypeParams);
    if (this.Expect("(") is null)
      return null;

    var parameters = this.Try(this.Params);
    if (this.Expect(")") is null)
      return null;

    var returns = this.Try(() => this.Expect("->") is null ? null : this.Expression());
    if (this.Expect(":") is null || this.Block() is not { } body)
      return null;

    var function = PyAst.Make(async ? "AsyncFunctionDef" : "FunctionDef", this.Extra(start), name.Text("id"), parameters ?? this.EmptyArguments(), Nullable(body),
      new List<PyNode?>(), returns, null, typeParams ?? new List<PyNode?>());
    return async ? this.Version(5, "Async functions are", function) : function;
  }

  private PyNode EmptyArguments()
    => PyAst.Make("arguments", null, new List<PyNode?>(), new List<PyNode?>(), null, new List<PyNode?>(), new List<PyNode?>(), null, new List<PyNode?>());

  private static PyNode MakeArguments(List<PyNode>? slashWithout, SlashDefaults? slashWith, List<PyNode>? plain, List<NameDefault>? withDefault, StarParameters? star) {
    var positionalOnly = slashWithout ?? (slashWith is null ? [] : [.. slashWith.PlainNames, .. slashWith.NamesWithDefaults.Select(pair => pair.Arg)]);
    List<PyNode> positional = [.. plain ?? [], .. (withDefault ?? []).Select(pair => pair.Arg)];
    List<PyNode?> defaults = [.. (slashWith?.NamesWithDefaults ?? []).Select(pair => pair.Value), .. (withDefault ?? []).Select(pair => pair.Value)];
    var keywordOnly = star?.KwOnly ?? [];
    return PyAst.Make("arguments", null, Nullable(positionalOnly), Nullable(positional), star?.VarArg, keywordOnly.Select(pair => (PyNode?)pair.Arg).ToList(),
      keywordOnly.Select(pair => pair.Value).ToList(), star?.KwArg, defaults);
  }

  private PyNode? Params() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidParameters);

    return this.Parameters();
  });

  private PyNode? Parameters() => this.Rule(() => {
    var slashNoDefault = this.Try(() => {
      if (this.SlashNoDefault() is not { } slash)
        return null;

      var plain = this.Repeat(this.ParamNoDefault, 0)!;
      var withDefault = this.Repeat(this.ParamWithDefault, 0)!;
      var star = this.Try(this.StarEtc);
      return this.Version(8, "Positional-only parameters are", MakeArguments(slash, null, plain, withDefault, star));
    });
    if (slashNoDefault is not null)
      return slashNoDefault;

    var slashWithDefault = this.Try(() => {
      if (this.SlashWithDefault() is not { } slash)
        return null;

      var withDefault = this.Repeat(this.ParamWithDefault, 0)!;
      var star = this.Try(this.StarEtc);
      return this.Version(8, "Positional-only parameters are", MakeArguments(null, slash, null, withDefault, star));
    });
    if (slashWithDefault is not null)
      return slashWithDefault;

    var plainFirst = this.Try(() => {
      if (this.Repeat(this.ParamNoDefault, 1) is not { } plain)
        return null;

      var withDefault = this.Repeat(this.ParamWithDefault, 0)!;
      var star = this.Try(this.StarEtc);
      return MakeArguments(null, null, plain, withDefault, star);
    });
    if (plainFirst is not null)
      return plainFirst;

    var defaultsFirst = this.Try(() => {
      if (this.Repeat(this.ParamWithDefault, 1) is not { } withDefault)
        return null;

      var star = this.Try(this.StarEtc);
      return MakeArguments(null, null, null, withDefault, star);
    });
    if (defaultsFirst is not null)
      return defaultsFirst;

    return this.StarEtc() is { } onlyStar ? MakeArguments(null, null, null, null, onlyStar) : null;
  });

  private List<PyNode>? SlashNoDefault() => this.Rule(() =>
    this.Try(() => this.Repeat(this.ParamNoDefault, 1) is { } names && this.Expect("/") is not null && this.Expect(",") is not null ? names : null)
    ?? this.Try(() => this.Repeat(this.ParamNoDefault, 1) is { } names && this.Expect("/") is not null && this.Check(")") ? names : null));

  private SlashDefaults? SlashWithDefault() => this.Rule(() =>
    this.Try(() => this.Repeat(this.ParamNoDefault, 0) is { } plain && this.Repeat(this.ParamWithDefault, 1) is { } named && this.Expect("/") is not null
      && this.Expect(",") is not null ? new SlashDefaults(plain, named) : null)
    ?? this.Try(() => this.Repeat(this.ParamNoDefault, 0) is { } plain && this.Repeat(this.ParamWithDefault, 1) is { } named && this.Expect("/") is not null
      && this.Check(")") ? new SlashDefaults(plain, named) : null));

  private StarParameters? StarEtc() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidStarEtc);

    var named = this.Try(() => {
      if (this.Expect("*") is null || this.ParamNoDefault() is not { } vararg)
        return null;

      var keywords = this.Repeat(this.ParamMaybeDefault, 0)!;
      return new StarParameters(vararg, keywords, this.Try(this.Kwds));
    });
    if (named is not null)
      return named;

    var starAnnotated = this.Try(() => {
      if (this.Expect("*") is null || this.ParamNoDefaultStarAnnotation() is not { } vararg)
        return null;

      var keywords = this.Repeat(this.ParamMaybeDefault, 0)!;
      return new StarParameters(vararg, keywords, this.Try(this.Kwds));
    });
    if (starAnnotated is not null)
      return starAnnotated;

    var bare = this.Try(() => {
      if (this.Expect("*") is null || this.Expect(",") is null || this.Repeat(this.ParamMaybeDefault, 1) is not { } keywords)
        return null;

      return new StarParameters(null, keywords, this.Try(this.Kwds));
    });
    if (bare is not null)
      return bare;

    return this.Kwds() is { } kwarg ? new StarParameters(null, null, kwarg) : null;
  });

  private PyNode? Kwds() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidKwds);

    return this.Expect("**") is null ? null : this.ParamNoDefault();
  });

  private PyNode? ParamNoDefault() => this.Rule(() =>
    this.Try(() => this.Param() is { } param && this.Expect(",") is not null ? param : null)
    ?? this.Try(() => this.Param() is { } param && this.Check(")") ? param : null));

  private PyNode? ParamNoDefaultStarAnnotation() => this.Rule(() =>
    this.Try(() => this.ParamStarAnnotation() is { } param && this.Expect(",") is not null ? param : null)
    ?? this.Try(() => this.ParamStarAnnotation() is { } param && this.Check(")") ? param : null));

  private NameDefault? ParamWithDefault() => this.Rule(() =>
    this.Try(() => this.Param() is { } param && this.Default() is { } value && this.Expect(",") is not null ? new NameDefault(param, value) : null)
    ?? this.Try(() => this.Param() is { } param && this.Default() is { } value && this.Check(")") ? new NameDefault(param, value) : null));

  private NameDefault? ParamMaybeDefault() => this.Rule(() =>
    this.Try(() => this.Param() is { } param && this.Try(this.Default) is var value && this.Expect(",") is not null ? new NameDefault(param, value) : null)
    ?? this.Try(() => this.Param() is { } param && this.Try(this.Default) is var value && this.Check(")") ? new NameDefault(param, value) : null));

  private PyNode? Param() => this.Rule(() => {
    var start = this.Peek();
    if (this.NameToken() is not { } name)
      return null;

    var annotation = this.Try(() => this.Expect(":") is null ? null : this.Expression());
    return PyAst.Make("arg", this.Extra(start), name.Text("id"), annotation, null);
  });

  private PyNode? ParamStarAnnotation() => this.Rule(() => {
    var start = this.Peek();
    if (this.NameToken() is not { } name || this.Expect(":") is null || this.StarExpression() is not { } annotation)
      return null;

    return PyAst.Make("arg", this.Extra(start), name.Text("id"), annotation, null);
  });

  private PyNode? Default() => this.Rule(() => {
    var value = this.Try(() => this.Expect("=") is null ? null : this.Expression());
    if (value is not null)
      return value;

    return this._invalid ? this.InvalidDefault() : null;
  });

  // ---------------------------------------------------------------------------------------------
  // if, while, for, with, try

  private PyNode? IfStatement() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidIfStatement);

    return this.Conditional("if");
  });

  private PyNode? Conditional(string keyword) {
    var start = this.Peek();
    var chained = this.Try(() => {
      if (this.Expect(keyword) is null || this.NamedExpression() is not { } test || this.Expect(":") is null || this.Block() is not { } body
        || this.ElifStatement() is not { } elif)
        return null;

      return PyAst.Make("If", this.Extra(start), test, Nullable(body), new List<PyNode?> { elif });
    });
    if (chained is not null)
      return chained;

    return this.Try(() => {
      if (this.Expect(keyword) is null || this.NamedExpression() is not { } test || this.Expect(":") is null || this.Block() is not { } body)
        return null;

      var orElse = this.Try(this.ElseBlock);
      return PyAst.Make("If", this.Extra(start), test, Nullable(body), Nullable(orElse ?? []));
    });
  }

  private PyNode? ElifStatement() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidElifStatement);

    return this.Conditional("elif");
  });

  private List<PyNode>? ElseBlock() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidElseStatement);

    if (this.Expect("else") is null)
      return null;

    this.Forced(":");
    return this.Block();
  });

  private PyNode? WhileStatement() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidWhileStatement);

    var start = this.Expect("while");
    if (start is null || this.NamedExpression() is not { } test || this.Expect(":") is null || this.Block() is not { } body)
      return null;

    var orElse = this.Try(this.ElseBlock);
    return PyAst.Make("While", this.Extra(start), test, Nullable(body), Nullable(orElse ?? []));
  });

  private PyNode? ForStatement() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidForStatement);

    var cut = false;
    foreach (var async in new[] { false, true }) {
      var start = this.Peek();
      var loop = this.Try(() => {
        if (async && this.Expect("async") is null)
          return null;

        if (this.Expect("for") is null || this.StarTargets() is not { } target || this.Expect("in") is null)
          return null;

        cut = true;
        if (this.StarExpressions() is not { } iterable || this.Expect(":") is null || this.Block() is not { } body)
          return null;

        var orElse = this.Try(this.ElseBlock);
        var node = PyAst.Make(async ? "AsyncFor" : "For", this.Extra(start), target, iterable, Nullable(body), Nullable(orElse ?? []), null);
        return async ? this.Version(5, "Async for loops are", node) : node;
      });
      if (loop is not null || cut)
        return loop;
    }

    return this._invalid ? this.InvalidForTarget() : null;
  });

  private PyNode? WithStatement() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidWithStatementIndent);

    foreach (var async in new[] { false, true }) {
      var start = this.Peek();
      var parenthesized = this.Try(() => {
        if (async && this.Expect("async") is null)
          return null;

        if (this.Expect("with") is null || this.Expect("(") is null || this.Gather(",", this.WithItem) is not { } items)
          return null;

        this.Expect(",");
        if (this.Expect(")") is null || this.Expect(":") is null || this.Block() is not { } body)
          return null;

        var node = PyAst.Make(async ? "AsyncWith" : "With", this.Extra(start), Nullable(items), Nullable(body), null);
        return async ? this.Version(5, "Async with statements are", node) : node;
      });
      if (parenthesized is not null)
        return parenthesized;

      var plain = this.Try(() => {
        if (async && this.Expect("async") is null)
          return null;

        if (this.Expect("with") is null || this.Gather(",", this.WithItem) is not { } items || this.Expect(":") is null || this.Block() is not { } body)
          return null;

        var node = PyAst.Make(async ? "AsyncWith" : "With", this.Extra(start), Nullable(items), Nullable(body), null);
        return async ? this.Version(5, "Async with statements are", node) : node;
      });
      if (plain is not null)
        return plain;
    }

    return this._invalid ? this.InvalidWithStatement() : null;
  });

  private PyNode? WithItem() => this.Rule(() => {
    var named = this.Try(() => {
      if (this.Expression() is not { } context || this.Expect("as") is null || this.StarTarget() is not { } target)
        return null;

      return this.CheckAny(",", ")", ":") ? PyAst.Make("withitem", null, context, target) : null;
    });
    if (named is not null)
      return named;

    if (this._invalid)
      this.Try(this.InvalidWithItem);

    return this.Expression() is { } expression ? PyAst.Make("withitem", null, expression, null) : null;
  });

  private PyNode? TryStatement() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidTryStatement);

    var start = this.Peek();
    var finallyOnly = this.Try(() => {
      if (this.Expect("try") is null)
        return null;

      this.Forced(":");
      if (this.Block() is not { } body || this.FinallyBlock() is not { } final)
        return null;

      return PyAst.Make("Try", this.Extra(start), Nullable(body), new List<PyNode?>(), new List<PyNode?>(), Nullable(final));
    });
    if (finallyOnly is not null)
      return finallyOnly;

    foreach (var star in new[] { false, true }) {
      var handled = this.Try(() => {
        if (this.Expect("try") is null)
          return null;

        this.Forced(":");
        if (this.Block() is not { } body || this.Repeat<PyNode>(star ? this.ExceptStarBlock : this.ExceptBlock, 1) is not { } handlers)
          return null;

        var orElse = this.Try(this.ElseBlock);
        var final = this.Try(this.FinallyBlock);
        var node = PyAst.Make(star ? "TryStar" : "Try", this.Extra(start), Nullable(body), Nullable(handlers), Nullable(orElse ?? []), Nullable(final ?? []));
        return star ? this.Version(11, "Exception groups are", node) : node;
      });
      if (handled is not null)
        return handled;
    }

    return null;
  });

  private PyNode? ExceptBlock() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidExceptStatementIndent);

    var start = this.Peek();
    var typed = this.Try(() => this.Expect("except") is not null && this.Expression() is { } type && this.Expect(":") is not null && this.Block() is { } body
      ? PyAst.Make("ExceptHandler", this.Extra(start), type, null, Nullable(body)) : null);
    if (typed is not null)
      return typed;

    var named = this.Try(() => this.Expect("except") is not null && this.Expression() is { } type && this.Expect("as") is not null && this.NameToken() is { } name
      && this.Expect(":") is not null && this.Block() is { } body
      ? PyAst.Make("ExceptHandler", this.Extra(start), type, name.Text("id"), Nullable(body)) : null);
    if (named is not null)
      return named;

    var several = this.Try(() => this.Expect("except") is not null && this.Expressions() is { } types && this.Expect(":") is not null && this.Block() is { } body
      ? this.Version(14, "except expressions without parentheses are", PyAst.Make("ExceptHandler", this.Extra(start), types, null, Nullable(body))) : null);
    if (several is not null)
      return several;

    var bare = this.Try(() => this.Expect("except") is not null && this.Expect(":") is not null && this.Block() is { } body
      ? PyAst.Make("ExceptHandler", this.Extra(start), null, null, Nullable(body)) : null);
    if (bare is not null)
      return bare;

    return this._invalid ? this.InvalidExceptStatement() : null;
  });

  private PyNode? ExceptStarBlock() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidExceptStarStatementIndent);

    var start = this.Peek();
    var typed = this.Try(() => this.Expect("except") is not null && this.Expect("*") is not null && this.Expression() is { } type && this.Expect(":") is not null
      && this.Block() is { } body
      ? PyAst.Make("ExceptHandler", this.Extra(start), type, null, Nullable(body)) : null);
    if (typed is not null)
      return typed;

    var named = this.Try(() => this.Expect("except") is not null && this.Expect("*") is not null && this.Expression() is { } type && this.Expect("as") is not null
      && this.NameToken() is { } name && this.Expect(":") is not null && this.Block() is { } body
      ? PyAst.Make("ExceptHandler", this.Extra(start), type, name.Text("id"), Nullable(body)) : null);
    if (named is not null)
      return named;

    var several = this.Try(() => this.Expect("except") is not null && this.Expect("*") is not null && this.Expressions() is { } types && this.Expect(":") is not null
      && this.Block() is { } body
      ? this.Version(14, "except expressions without parentheses are", PyAst.Make("ExceptHandler", this.Extra(start), types, null, Nullable(body))) : null);
    if (several is not null)
      return several;

    return this._invalid ? this.InvalidExceptStarStatement() : null;
  });

  private List<PyNode>? FinallyBlock() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidFinallyStatement);

    if (this.Expect("finally") is null)
      return null;

    this.Forced(":");
    return this.Block();
  });

  // ---------------------------------------------------------------------------------------------
  // Type aliases and type parameters

  private PyNode? TypeAlias() => this.Rule(() => {
    var start = this.Peek();
    if (this.SoftKeyword("type") is null || this.NameToken() is not { } name)
      return null;

    var typeParams = this.Try(this.TypeParams);
    if (this.Expect("=") is null || this.Expression() is not { } value)
      return null;

    return this.Version(12, "Type statement is", PyAst.Make("TypeAlias", this.Extra(start), PyAst.SetContext(name, "Store"), typeParams ?? new List<PyNode?>(), value));
  });

  private List<PyNode?>? TypeParams() => this.Rule(() => {
    if (this._invalid)
      this.Try(this.InvalidTypeParams);

    if (this.Expect("[") is null || this.Gather(",", this.TypeParam) is not { } parameters)
      return null;

    this.Expect(",");
    return this.Expect("]") is null ? null : this.Version(12, "Type parameter lists are", Nullable(parameters));
  });

  private PyNode? TypeParam() => this.Memo(MemoTypeParam, () => {
    var start = this.Peek();
    var plain = this.Try(() => {
      if (this.NameToken() is not { } name)
        return null;

      var bound = this.Try(() => this.Expect(":") is null ? null : this.Expression());
      var value = this.Try(this.TypeParamDefault);
      return PyAst.Make("TypeVar", this.Extra(start), name.Text("id"), bound, value);
    });
    if (plain is not null)
      return plain;

    if (this._invalid)
      this.Try(this.InvalidTypeParam);

    var tuple = this.Try(() => {
      if (this.Expect("*") is null || this.NameToken() is not { } name)
        return null;

      var value = this.Try(() => this.Expect("=") is null ? null : this.StarExpression() is { } star ? this.Version(13, "Type parameter defaults are", star) : null);
      return PyAst.Make("TypeVarTuple", this.Extra(start), name.Text("id"), value);
    });
    if (tuple is not null)
      return tuple;

    return this.Try(() => {
      if (this.Expect("**") is null || this.NameToken() is not { } name)
        return null;

      var value = this.Try(this.TypeParamDefault);
      return PyAst.Make("ParamSpec", this.Extra(start), name.Text("id"), value);
    });
  });

  private PyNode? TypeParamDefault() => this.Rule(() =>
    this.Expect("=") is not null && this.Expression() is { } value ? this.Version(13, "Type parameter defaults are", value) : null);

}
