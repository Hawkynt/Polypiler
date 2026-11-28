using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;

namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>
/// The <c>invalid_*</c> rules of the grammar, which only the second pass tries: each raises CPython's
/// message where its alternative matches, or fails.
/// </summary>
internal sealed partial class PyParser {

  private PyNode Dummy() => PyAst.Make("Name", null, "", PyAst.Singleton("Load"));

  private PyError? TargetError(PyAst.Targets targets, PyNode expression)
    => PyAst.InvalidTarget(expression, targets) is { } invalid
      ? this.ErrorAt(invalid, $"{(targets == PyAst.Targets.Delete ? "cannot delete" : "cannot assign to")} {PyAst.Name(invalid)}")
      : null;

  private T? RaiseInvalidTarget<T>(PyAst.Targets targets, PyNode expression) where T : class
    => this.TargetError(targets, expression) is { } error ? throw error : null;

  private static PyNode LastComprehensionItem(PyNode comprehension)
    => comprehension.Nodes("ifs") is { Count: > 0 } ifs ? ifs[^1] : comprehension.Node("iter")!;

  // ---------------------------------------------------------------------------------------------
  // Arguments

  private PyNode? InvalidArguments() => this.Rule(() => {
    this.Try<PyNode>(() => {
      var prefix = this.Try(() => this.Gather(",", this.PositionalArgument) is not null && this.Expect(",") is not null && this.Kwargs() is { } keywords ? keywords : null)
        ?? this.Kwargs();
      if (prefix is null || this.Expect(",") is not { } comma || this.Gather(",", () => this.Try(() => this.StarredExpression() is { } starred && !this.Check("=") ? starred : null)) is null)
        return null;

      throw this.ErrorFrom(comma, "iterable argument unpacking follows keyword argument unpacking");
    });

    this.Try<PyNode>(() => {
      if (this.Expression() is not { } first || this.ForIfClauses() is not { } clauses || this.Expect(",") is null)
        return null;

      this.Try(() => this.Try(this.Args) ?? (this.Expression() is not null && this.ForIfClauses() is not null ? this.Dummy() : null));
      throw this.ErrorAt(first, LastComprehensionItem(clauses[^1]), "Generator expression must be parenthesized");
    });

    // The grammar's alternative NAME '=' expression for_if_clauses comes next; invalid_kwarg raises its
    // message first, as the arguments are parsed, so it never matches here.
    this.Try<PyNode>(() => {
      this.Try(() => this.Args() is not null && this.Expect(",") is not null ? this.Dummy() : null);
      if (this.NameToken() is not { } name || this.Expect("=") is not { } sign || !this.CheckAny(",", ")"))
        return null;

      throw this.ErrorAt(name, sign, "expected argument value expression");
    });

    var nonParenthesized = this.Try(() => {
      if (this.Args() is not { } call || this.ForIfClauses() is not { } clauses)
        return null;

      var arguments = call.Nodes("args");
      if (arguments.Count <= 1)
        return new Box<bool>(false);

      throw this.ErrorAt(arguments[^1], LastComprehensionItem(clauses[^1]), "Generator expression must be parenthesized");
    });
    if (nonParenthesized is not null)
      return null;

    // The grammar's alternative args ',' expression for_if_clauses comes next; the arguments take that
    // expression in, so the alternative before has matched already.

    return this.Try<PyNode>(() => {
      if (this.Args() is not { } call || this.Expect(",") is null || this.Args() is null)
        return null;

      throw this.Error(call.Nodes("keywords").Any(keyword => keyword.Text("arg") is null)
        ? "positional argument follows keyword argument unpacking"
        : "positional argument follows keyword argument");
    });
  });

  private KeywordOrStarred? InvalidKwarg() => this.Rule(() => {
    this.Try<KeywordOrStarred>(() => {
      var token = this.Peek();
      if (token.Kind != PyTokenKind.Keyword || token.Text is not ("True" or "False" or "None"))
        return null;

      this._mark++;
      return this.Expect("=") is { } sign ? throw this.ErrorAt(token, sign, $"cannot assign to {token.Text}") : null;
    });

    this.Try<KeywordOrStarred>(() => {
      if (this.NameToken() is not { } name || this.Expect("=") is not { } sign || this.Expression() is null || this.ForIfClauses() is null)
        return null;

      throw this.ErrorAt(name, sign, "invalid syntax. Maybe you meant '==' or ':=' instead of '='?");
    });

    this.Try<KeywordOrStarred>(() => {
      if (this.Lookahead(() => this.NameToken() is not null && this.Expect("=") is not null ? this.Dummy() : null))
        return null;

      if (this.Expression() is not { } value || this.Expect("=") is not { } sign)
        return null;

      throw this.ErrorAt(value, sign, "expression cannot contain assignment, perhaps you meant \"==\"?");
    });

    return this.Try<KeywordOrStarred>(() => {
      if (this.Expect("**") is not { } stars || this.Expression() is null || this.Expect("=") is null || this.Expression() is not { } value)
        return null;

      throw this.ErrorAt(stars, value, "cannot assign to keyword argument unpacking");
    });
  });

  private PyNode? InvalidStarredExpressionUnpacking() => this.Rule<PyNode>(() => {
    if (this.Expect("*") is not { } star || this.Expression() is null || this.Expect("=") is null || this.Expression() is not { } value)
      return null;

    throw this.ErrorAt(star, value, "cannot assign to iterable argument unpacking");
  });

  private PyNode? InvalidStarredExpression() => this.Rule<PyNode>(() => this.Expect("*") is null ? null : throw this.Error("Invalid star expression"));

  // ---------------------------------------------------------------------------------------------
  // Expressions

  private PyNode? InvalidLegacyExpression() => this.Rule<PyNode>(() => {
    if (this.NameToken() is not { } name || this.Check("(") || this.StarExpressions() is not { } arguments)
      return null;

    var id = name.Text("id");
    return id is "print" or "exec" ? throw this.ErrorAt(name, arguments, $"Missing parentheses in call to '{id}'. Did you mean {id}(...)?") : null;
  });

  private PyNode? InvalidExpression() => this.Rule<PyNode>(() => {
    this.Try<PyNode>(() => {
      if (this.Expect(PyTokenKind.String) is null)
        return null;

      var inner = this.Repeat(() => this.Try(() => this.Check(PyTokenKind.String) ? null : this.ExpressionWithoutInvalid()), 1);
      if (inner is null || this.Expect(PyTokenKind.String) is null)
        return null;

      throw this.ErrorAt(inner[0], inner[^1], "invalid syntax. Is this intended to be part of the string?");
    });

    var comma = this.Try(() => {
      var mark = this._mark;
      var nameString = this.Expect(PyTokenKind.Name) is not null && this.Check(PyTokenKind.String);
      this._mark = mark;
      var excluded = nameString || this.CheckAnySoftKeyword();
      if (excluded || this.Disjunction() is not { } first || this.ExpressionWithoutInvalid() is not { } second)
        return null;

      if (first.Is("Name") && first.Text("id") is "print" or "exec")
        return new Box<bool>(false);

      if (this._tokens[this._mark - 1].Level == 0)
        return new Box<bool>(false);

      throw this.ErrorAt(first, second, "invalid syntax. Perhaps you forgot a comma?");
    });
    if (comma is not null)
      return null;

    this.Try<PyNode>(() => {
      if (this.Disjunction() is not { } body || this.Expect("if") is null || this.Disjunction() is not { } test || this.CheckAny("else", ":"))
        return null;

      throw this.ErrorAt(body, test, "expected 'else' after 'if' expression");
    });

    this.Try<PyNode>(() => {
      if (this.Disjunction() is null || this.Expect("if") is null || this.Disjunction() is null || this.Expect("else") is null || this.Lookahead(this.Expression))
        return null;

      throw this.Error("expected expression after 'else', but statement is given", next: true);
    });

    this.Try<PyNode>(() => {
      var start = this.Peek();
      if (start.Kind != PyTokenKind.Keyword || start.Text is not ("pass" or "break" or "continue"))
        return null;

      this._mark++;
      if (this.Expect("if") is null || this.Disjunction() is null || this.Expect("else") is null || this.SimpleStatement() is null)
        return null;

      throw this.ErrorAt(start, start, "expected expression before 'if', but statement is given");
    });

    foreach (var (kind, prefix) in new[] { (PyTokenKind.FStringMiddle, 'f'), (PyTokenKind.TStringMiddle, 't') }) {
      this.Try<PyNode>(() => {
        if (this.Expect("lambda") is not { } keyword)
          return null;

        this.Try(this.LambdaParams);
        if (this.Expect(":") is not { } colon || !this.Check(kind))
          return null;

        throw this.ErrorAt(keyword, colon, $"{prefix}-string: lambda expressions are not allowed without parentheses");
      });
    }

    return null;
  });

  private PyNode? InvalidNamedExpression() => this.Memo(MemoInvalidNamedExpression, () => {
    this.Try<PyNode>(() => {
      if (this.Expression() is not { } target || this.Expect(":=") is null || this.Expression() is null)
        return null;

      throw this.ErrorAt(target, $"cannot use assignment expressions with {PyAst.Name(target)}");
    });

    this.Try<PyNode>(() => {
      if (this.NameToken() is not { } target || this.Expect("=") is null || this.BitwiseOr() is not { } value || this.CheckAny("=", ":="))
        return null;

      throw this.ErrorAt(target, value, "invalid syntax. Maybe you meant '==' or ':=' instead of '='?");
    });

    return this.Try<PyNode>(() => {
      if (this.Lookahead(this.List) || this.Lookahead(this.Tuple) || this.Lookahead(this.Genexp) || this.CheckAny("True", "None", "False"))
        return null;

      if (this.BitwiseOr() is not { } target || this.Expect("=") is null || this.BitwiseOr() is null || this.CheckAny("=", ":="))
        return null;

      throw this.ErrorAt(target, $"cannot assign to {PyAst.Name(target)} here. Maybe you meant '==' instead of '='?");
    });
  });

  private CompareOp? InvalidNotEqual() => this.Rule<CompareOp>(() => {
    if (this.Expect("<") is not { } less || this.Expect(">") is not { } greater)
      return null;

    return less.EndLine == greater.Line && less.EndColumn == greater.Column
      ? throw this.ErrorAt(less, greater, "invalid syntax.  Maybe you meant '!=' instead of '<>'?")
      : null;
  });

  private PyNode? InvalidArithmetic() => this.Rule<PyNode>(() => {
    if (this.Sum() is null || !this.CheckAny("+", "-", "*", "/", "%", "//", "@"))
      return null;

    this._mark++;
    if (this.Expect("not") is not { } not || this.Inversion() is not { } operand)
      return null;

    throw this.ErrorAt(not, operand, "'not' after an operator must be parenthesized");
  });

  private PyNode? InvalidFactor() => this.Rule<PyNode>(() => {
    if (!this.CheckAny("+", "-", "~"))
      return null;

    this._mark++;
    if (this.Expect("not") is not { } not || this.Factor() is not { } operand)
      return null;

    throw this.ErrorAt(not, operand, "'not' after an operator must be parenthesized");
  });

  private PyNode? InvalidGroup() => this.Rule(() => {
    this.Try(() => this.Expect("(") is not null && this.StarredExpression() is { } value && this.Expect(")") is not null
      ? throw this.ErrorAt(value, "cannot use starred expression here") : (PyNode?)null);

    return this.Try(() => this.Expect("(") is not null && this.Expect("**") is { } stars && this.Expression() is not null && this.Expect(")") is not null
      ? throw this.ErrorAt(stars, stars, "cannot use double starred expression here") : (PyNode?)null);
  });

  private PyNode? InvalidComprehension() => this.Rule(() => {
    this.Try<PyNode>(() => {
      if (!this.CheckAny("[", "(", "{"))
        return null;

      this._mark++;
      return this.StarredExpression() is { } value && this.ForIfClauses() is not null ? throw this.ErrorAt(value, "iterable unpacking cannot be used in comprehension") : null;
    });

    this.Try<PyNode>(() => {
      if (!this.CheckAny("[", "{"))
        return null;

      this._mark++;
      if (this.StarNamedExpression() is not { } first || this.Expect(",") is null || this.StarNamedExpressions() is not { } rest || this.ForIfClauses() is null)
        return null;

      throw this.ErrorAt(first, rest[^1], "did you forget parentheses around the comprehension target?");
    });

    return this.Try<PyNode>(() => {
      if (!this.CheckAny("[", "{"))
        return null;

      this._mark++;
      if (this.StarNamedExpression() is not { } first || this.Expect(",") is not { } comma || this.ForIfClauses() is null)
        return null;

      throw this.ErrorAt(first, comma, "did you forget parentheses around the comprehension target?");
    });
  });

  private PyNode? InvalidDictComprehension() => this.Rule<PyNode>(() => {
    if (this.Expect("{") is null || this.Expect("**") is not { } stars || this.BitwiseOr() is null || this.ForIfClauses() is null || this.Expect("}") is null)
      return null;

    throw this.ErrorAt(stars, stars, "dict unpacking cannot be used in dict comprehension");
  });

  private PyNode? InvalidDoubleStarredKeyValues() => this.Rule(() => {
    this.Try(() => this.Gather(",", this.DoubleStarredKeyValue) is not null && this.Expect(",") is not null ? this.InvalidKeyValue() : null);

    this.Try(() => this.Expression() is not null && this.Expect(":") is not null && this.Expect("*") is { } star && this.BitwiseOr() is not null
      ? throw this.ErrorFrom(star, "cannot use a starred expression in a dictionary value") : (PyNode?)null);

    return this.Try(() => this.Expression() is not null && this.Expect(":") is { } colon && this.CheckAny("}", ",")
      ? throw this.ErrorAt(colon, colon, "expression expected after dictionary key and ':'") : (PyNode?)null);
  });

  private PyNode? InvalidKeyValue() => this.Rule(() => {
    this.Try(() => this.Expression() is { } key && !this.Check(":")
      ? throw this.ErrorAt(key.Range!.Line, key.Range.EndOffset - 1, key.Range.EndLine, -1, "':' expected after dictionary key") : (PyNode?)null);

    this.Try(() => this.Expression() is not null && this.Expect(":") is not null && this.Expect("*") is { } star && this.BitwiseOr() is not null
      ? throw this.ErrorFrom(star, "cannot use a starred expression in a dictionary value") : (PyNode?)null);

    return this.Try(() => this.Expression() is not null && this.Expect(":") is { } colon && this.CheckAny("}", ",")
      ? throw this.ErrorAt(colon, colon, "expression expected after dictionary key and ':'") : (PyNode?)null);
  });

  private PyNode? InvalidForIfClause() => this.Rule<PyNode>(() => {
    this.Expect("async");
    if (this.Expect("for") is null || this.BitwiseOr() is null)
      return null;

    this.Repeat(() => this.Try(() => this.Expect(",") is not null ? this.BitwiseOr() : null), 0);
    this.Expect(",");
    return this.Check("in") ? null : throw this.Error("'in' expected after for-loop variables");
  });

  private PyNode? InvalidForTarget() => this.Rule(() => {
    this.Expect("async");
    if (this.Expect("for") is null || this.StarExpressions() is not { } target)
      return null;

    return this.RaiseInvalidTarget<PyNode>(PyAst.Targets.For, target);
  });

  private PyNode? InvalidStringTStringConcat() => this.Rule(() => {
    this.Try(() => this.Repeat(() => this.Try(this.FString) ?? this.StringToken(), 1) is { } strings && this.TString() is { } template
      ? throw this.ErrorAt(strings[^1], template, "cannot mix t-string literals with string or bytes literals") : (PyNode?)null);

    return this.Try(() => this.Repeat(this.TString, 1) is { } templates && (this.Try(this.FString) ?? this.StringToken()) is { } text
      ? throw this.ErrorAt(templates[^1], text, "cannot mix t-string literals with string or bytes literals") : (PyNode?)null);
  });

  private PyNode? InvalidReplacementField(bool template) => this.Rule(() => {
    var prefix = template ? 't' : 'f';
    foreach (var punctuation in new[] { "=", "!", ":", "}" }) {
      this.Try(() => this.Expect("{") is not null && this.Expect(punctuation) is { } token
        ? throw this.ErrorAt(token, token, $"{prefix}-string: valid expression required before '{punctuation}'") : (PyNode?)null);
    }

    this.Try(() => this.Expect("{") is not null && !this.Lookahead(this.AnnotatedRhs)
      ? throw this.Error($"{prefix}-string: expecting a valid expression after '{{'", next: true) : (PyNode?)null);

    this.Try(() => this.Expect("{") is not null && this.AnnotatedRhs() is not null && !this.CheckAny("=", "!", ":", "}")
      ? throw this.Error($"{prefix}-string: expecting '=', or '!', or ':', or '}}'", next: true) : (PyNode?)null);

    this.Try(() => this.Expect("{") is not null && this.AnnotatedRhs() is not null && this.Expect("=") is not null && !this.CheckAny("!", ":", "}")
      ? throw this.Error($"{prefix}-string: expecting '!', or ':', or '}}'", next: true) : (PyNode?)null);

    this.Try<PyNode>(() => {
      if (this.Expect("{") is null || this.AnnotatedRhs() is null)
        return null;

      this.Expect("=");
      if (this.Expect("!") is null)
        return null;

      if (this.CheckAny(":", "}"))
        throw this.Error($"{prefix}-string: missing conversion character", next: true);

      return this.Check(PyTokenKind.Name) ? null : throw this.Error($"{prefix}-string: invalid conversion character", next: true);
    });

    PyNode? AfterConversion(System.Func<PyNode?> rest) => this.Try(() => {
      if (this.Expect("{") is null || this.AnnotatedRhs() is null)
        return null;

      this.Expect("=");
      this.Try(() => this.Expect("!") is not null && this.NameToken() is not null ? this.Dummy() : null);
      return rest();
    });

    AfterConversion(() => this.CheckAny(":", "}") ? null : throw this.Error($"{prefix}-string: expecting ':' or '}}'", next: true));

    AfterConversion(() => {
      if (this.Expect(":") is null)
        return null;

      this.Repeat(() => this.Try(() => this.FormatSpecPart(false)), 0);
      return this.Check("}") ? null : throw this.Error($"{prefix}-string: expecting '}}', or format specs", next: true);
    });

    return AfterConversion(() => this.Check("}") ? null : throw this.Error($"{prefix}-string: expecting '}}'", next: true));
  });

  // ---------------------------------------------------------------------------------------------
  // Simple statements

  private PyNode? InvalidAssignment() => this.Rule(() => {
    this.Try(() => this.InvalidAnnAssignTarget() is { } target && this.Expect(":") is not null && this.Expression() is not null
      ? throw this.ErrorAt(target, $"only single target (not {PyAst.Name(target)}) can be annotated") : (PyNode?)null);

    this.Try<PyNode>(() => {
      if (this.StarNamedExpression() is not { } first || this.Expect(",") is null)
        return null;

      this.Repeat(this.StarNamedExpressions, 0);
      return this.Expect(":") is not null && this.Expression() is not null ? throw this.ErrorAt(first, "only single target (not tuple) can be annotated") : null;
    });

    this.Try(() => this.Expression() is { } target && this.Expect(":") is not null && this.Expression() is not null
      ? throw this.ErrorAt(target, "illegal target for annotation") : (PyNode?)null);

    var target = this.Try(() => {
      this.Repeat(() => this.Try(() => this.StarTargets() is not null && this.Expect("=") is not null ? this.Dummy() : null), 0);
      if (this.StarExpressions() is not { } value || this.Expect("=") is null)
        return null;

      return this.TargetError(PyAst.Targets.Star, value) is { } error ? throw error : new Box<bool>(false);
    });
    if (target is not null)
      return null;

    this.Try<PyNode>(() => {
      this.Repeat(() => this.Try(() => this.StarTargets() is not null && this.Expect("=") is not null ? this.Dummy() : null), 0);
      return this.YieldExpression() is { } value && this.Expect("=") is not null ? throw this.ErrorAt(value, "assignment to yield expression not possible") : null;
    });

    return this.Try(() => this.StarExpressions() is { } value && this.AugAssign() is not null && this.AnnotatedRhs() is not null
      ? throw this.ErrorAt(value, $"'{PyAst.Name(value)}' is an illegal expression for augmented assignment") : (PyNode?)null);
  });

  private PyNode? InvalidAnnAssignTarget() => this.Rule(() =>
    this.Try(this.List) ?? this.Try(this.Tuple) ?? this.Try(() => this.Expect("(") is not null && this.InvalidAnnAssignTarget() is { } inner && this.Expect(")") is not null ? inner : null));

  private PyNode? InvalidDelStatement() => this.Rule(() =>
    this.Expect("del") is not null && this.StarExpressions() is { } target ? this.RaiseInvalidTarget<PyNode>(PyAst.Targets.Delete, target) : null);

  private PyNode? InvalidImport() => this.Rule(() => {
    this.Try(() => this.Expect("import") is { } keyword && this.Gather(",", this.DottedName) is not null && this.Expect("from") is not null && this.DottedName() is not null
      ? throw this.ErrorFrom(keyword, "Did you mean to use 'from ... import ...' instead?") : (PyNode?)null);

    return this.Try(() => this.Expect("import") is not null && this.Expect(PyTokenKind.Newline) is { } newline
      ? throw this.ErrorFrom(newline, "Expected one or more names after 'import'") : (PyNode?)null);
  });

  private bool ImportTargetFollows() => this.Lookahead(() => this.NameToken() is not null && (this.CheckAny(",", ")", ";") || this.Check(PyTokenKind.Newline)) ? this.Dummy() : null);

  private PyNode? InvalidDottedAsName() => this.Rule<PyNode>(() => {
    if (this.DottedName() is null || this.Expect("as") is null || this.ImportTargetFollows() || this.Expression() is not { } target)
      return null;

    throw this.ErrorAt(target, $"cannot use {PyAst.Name(target)} as import target");
  });

  private PyNode? InvalidImportFromAsName() => this.Rule<PyNode>(() => {
    if (this.NameToken() is null || this.Expect("as") is null || this.ImportTargetFollows() || this.Expression() is not { } target)
      return null;

    throw this.ErrorAt(target, $"cannot use {PyAst.Name(target)} as import target");
  });

  private List<PyNode>? InvalidImportFromTargets() => this.Rule<List<PyNode>>(() => {
    this.Try(() => this.ImportFromAsNames() is not null && this.Expect(",") is not null && this.Expect(PyTokenKind.Newline) is not null
      ? throw this.Error("trailing comma not allowed without surrounding parentheses") : (List<PyNode>?)null);

    return this.Expect(PyTokenKind.Newline) is { } newline ? throw this.ErrorFrom(newline, "Expected one or more names after 'import'") : null;
  });

  // ---------------------------------------------------------------------------------------------
  // Blocks and compound statements

  private List<PyNode>? InvalidBlock() => this.Rule(() =>
    this.Expect(PyTokenKind.Newline) is not null && !this.Check(PyTokenKind.Indent) ? throw this.IndentationError("expected an indented block") : (List<PyNode>?)null);

  /// <summary><c>a=keyword … ':' NEWLINE !INDENT</c>: the indentation error of a compound statement without a body.</summary>
  private T? MissingBody<T>(PyToken keyword, string what) where T : class
    => this.Expect(":") is not null && this.Expect(PyTokenKind.Newline) is not null && !this.Check(PyTokenKind.Indent)
      ? throw this.IndentationError($"expected an indented block after {what} on line {keyword.Line}")
      : null;

  private PyNode? InvalidClassDefRaw() => this.Rule(() => {
    PyToken? Header() {
      if (this.Expect("class") is not { } keyword || this.NameToken() is null)
        return null;

      this.Try(this.TypeParams);
      this.Try(() => {
        if (this.Expect("(") is null)
          return null;

        this.Try(this.Arguments);
        return this.Expect(")") is null ? null : this.Dummy();
      });
      return keyword;
    }

    this.Try(() => Header() is not null && this.Expect(PyTokenKind.Newline) is not null ? throw this.Error("expected ':'") : (PyNode?)null);

    return this.Try(() => Header() is { } keyword ? this.MissingBody<PyNode>(keyword, "class definition") : null);
  });

  private PyNode? InvalidDefRaw() => this.Rule(() => {
    this.Try(() => {
      this.Expect("async");
      if (this.Expect("def") is not { } keyword || this.NameToken() is null)
        return null;

      this.Try(this.TypeParams);
      if (this.Expect("(") is null)
        return null;

      this.Try(this.Params);
      if (this.Expect(")") is null)
        return null;

      this.Try(() => this.Expect("->") is not null ? this.Expression() : null);
      return this.MissingBody<PyNode>(keyword, "function definition");
    });

    return this.Try(() => {
      this.Expect("async");
      if (this.Expect("def") is null || this.NameToken() is null)
        return null;

      this.Try(this.TypeParams);
      this.Forced("(");
      this.Try(this.Params);
      if (this.Expect(")") is null)
        return null;

      this.Try(() => this.Expect("->") is not null ? this.Expression() : null);
      this.Forced(":");
      return this.Block() is not null ? this.Dummy() : null;
    });
  });

  private PyNode? InvalidIfStatement() => this.InvalidConditional("if");

  private PyNode? InvalidElifStatement() => this.InvalidConditional("elif");

  private PyNode? InvalidConditional(string keyword) => this.Rule(() => {
    this.Try(() => this.Expect(keyword) is not null && this.NamedExpression() is not null && this.Expect(PyTokenKind.Newline) is not null
      ? throw this.Error("expected ':'") : (PyNode?)null);

    return this.Try<PyNode>(() => {
      if (this.Expect(keyword) is not { } token || this.NamedExpression() is not { } condition)
        return null;

      // For 'if' CPython takes the line of the condition, which its grammar names like the keyword.
      return this.Expect(":") is not null && this.Expect(PyTokenKind.Newline) is not null && !this.Check(PyTokenKind.Indent)
        ? throw this.IndentationError($"expected an indented block after '{keyword}' statement on line {(keyword == "if" ? condition.Line : token.Line)}")
        : null;
    });
  });

  private List<PyNode>? InvalidElseStatement() => this.Rule(() => {
    this.Try(() => this.Expect("else") is { } keyword ? this.MissingBody<List<PyNode>>(keyword, "'else' statement") : null);

    return this.Try(() => this.Expect("else") is not null && this.Expect(":") is not null && this.Block() is not null && this.Expect("elif") is not null
      ? throw this.Error("'elif' block follows an 'else' block") : (List<PyNode>?)null);
  });

  private PyNode? InvalidWhileStatement() => this.Rule(() => {
    this.Try(() => this.Expect("while") is not null && this.NamedExpression() is not null && this.Expect(PyTokenKind.Newline) is not null
      ? throw this.Error("expected ':'") : (PyNode?)null);

    return this.Try(() => this.Expect("while") is { } keyword && this.NamedExpression() is not null ? this.MissingBody<PyNode>(keyword, "'while' statement") : null);
  });

  private PyNode? InvalidForStatement() => this.Rule(() => {
    this.Try(() => {
      this.Expect("async");
      return this.Expect("for") is not null && this.StarTargets() is not null && this.Expect("in") is not null && this.StarExpressions() is not null
        && this.Expect(PyTokenKind.Newline) is not null ? throw this.Error("expected ':'") : (PyNode?)null;
    });

    return this.Try(() => {
      this.Expect("async");
      return this.Expect("for") is { } keyword && this.StarTargets() is not null && this.Expect("in") is not null && this.StarExpressions() is not null
        ? this.MissingBody<PyNode>(keyword, "'for' statement") : null;
    });
  });

  private PyNode? WithItems(bool parenthesized) {
    if (parenthesized) {
      if (this.Expect("(") is null)
        return null;

      if (this.Gather(",", () => this.Expressions() is { } items ? this.Try(() => this.Expect("as") is not null ? this.StarTarget() : null) ?? items : null) is null)
        return null;

      this.Expect(",");
      return this.Expect(")") is null ? null : this.Dummy();
    }

    return this.Gather(",", () => this.Expression() is { } item ? this.Try(() => this.Expect("as") is not null ? this.StarTarget() : null) ?? item : null) is null ? null : this.Dummy();
  }

  private PyNode? InvalidWithStatement() => this.Rule<PyNode>(() => {
    foreach (var parenthesized in new[] { false, true }) {
      this.Try(() => {
        this.Expect("async");
        return this.Expect("with") is not null && this.WithItems(parenthesized) is not null && this.Expect(PyTokenKind.Newline) is not null
          ? throw this.Error("expected ':'") : (PyNode?)null;
      });
    }

    return null;
  });

  private PyNode? InvalidWithStatementIndent() => this.Rule<PyNode>(() => {
    foreach (var parenthesized in new[] { false, true }) {
      this.Try(() => {
        this.Expect("async");
        return this.Expect("with") is { } keyword && this.WithItems(parenthesized) is not null ? this.MissingBody<PyNode>(keyword, "'with' statement") : null;
      });
    }

    return null;
  });

  private PyNode? InvalidWithItem() => this.Rule(() => {
    if (this.Expression() is null || this.Expect("as") is null || this.Expression() is not { } target || !this.CheckAny(",", ")", ":"))
      return null;

    return this.RaiseInvalidTarget<PyNode>(PyAst.Targets.Star, target);
  });

  private PyNode? InvalidTryStatement() => this.Rule(() => {
    this.Try(() => this.Expect("try") is { } keyword ? this.MissingBody<PyNode>(keyword, "'try' statement") : null);

    this.Try(() => this.Expect("try") is not null && this.Expect(":") is not null && this.Block() is not null && !this.CheckAny("except", "finally")
      ? throw this.Error("expected 'except' or 'finally' block") : (PyNode?)null);

    this.Try<PyNode>(() => {
      if (this.Expect("try") is null || this.Expect(":") is null)
        return null;

      this.Repeat(this.Block, 0);
      if (this.Repeat(this.ExceptBlock, 1) is null || this.Expect("except") is not { } except || this.Expect("*") is not { } star || this.Expression() is null)
        return null;

      this.Try(() => this.Expect("as") is not null ? this.NameToken() : null);
      return this.Expect(":") is not null ? throw this.ErrorAt(except, star, "cannot have both 'except' and 'except*' on the same 'try'") : null;
    });

    return this.Try<PyNode>(() => {
      if (this.Expect("try") is null || this.Expect(":") is null)
        return null;

      this.Repeat(this.Block, 0);
      if (this.Repeat(this.ExceptStarBlock, 1) is null || this.Expect("except") is not { } except)
        return null;

      this.Try(() => this.Expression() is { } type ? this.Try(() => this.Expect("as") is not null ? this.NameToken() : null) ?? type : null);
      return this.Expect(":") is not null ? throw this.ErrorAt(except, except, "cannot have both 'except' and 'except*' on the same 'try'") : null;
    });
  });

  private PyNode? InvalidExceptStatement() => this.Rule(() => {
    this.Try(() => this.Expect("except") is not null && this.Expression() is { } type && this.Expect(",") is not null && this.Expressions() is not null
      && this.Expect("as") is not null && this.NameToken() is not null && this.Expect(":") is not null
      ? throw this.ErrorFrom(type, "multiple exception types must be parenthesized when using 'as'") : (PyNode?)null);

    this.Try<PyNode>(() => {
      if (this.Expect("except") is null || this.Expression() is null)
        return null;

      this.Try(() => this.Expect("as") is not null ? this.NameToken() : null);
      return this.Expect(PyTokenKind.Newline) is not null ? throw this.Error("expected ':'") : null;
    });

    this.Try(() => this.Expect("except") is not null && this.Expect(PyTokenKind.Newline) is not null ? throw this.Error("expected ':'") : (PyNode?)null);

    return this.Try(() => this.Expect("except") is not null && this.Expression() is not null && this.Expect("as") is not null && this.Expression() is { } target
      && this.Expect(":") is not null && this.Block() is not null
      ? throw this.ErrorAt(target, $"cannot use except statement with {PyAst.Name(target)}") : (PyNode?)null);
  });

  private PyNode? InvalidExceptStarStatement() => this.Rule(() => {
    this.Try(() => this.Expect("except") is not null && this.Expect("*") is not null && this.Expression() is { } type && this.Expect(",") is not null
      && this.Expressions() is not null && this.Expect("as") is not null && this.NameToken() is not null && this.Expect(":") is not null
      ? throw this.ErrorFrom(type, "multiple exception types must be parenthesized when using 'as'") : (PyNode?)null);

    this.Try<PyNode>(() => {
      if (this.Expect("except") is null || this.Expect("*") is null || this.Expression() is null)
        return null;

      this.Try(() => this.Expect("as") is not null ? this.NameToken() : null);
      return this.Expect(PyTokenKind.Newline) is not null ? throw this.Error("expected ':'") : null;
    });

    this.Try(() => this.Expect("except") is not null && this.Expect("*") is not null && (this.Expect(PyTokenKind.Newline) ?? this.Expect(":")) is not null
      ? throw this.Error("expected one or more exception types") : (PyNode?)null);

    return this.Try(() => this.Expect("except") is not null && this.Expect("*") is not null && this.Expression() is not null && this.Expect("as") is not null
      && this.Expression() is { } target && this.Expect(":") is not null && this.Block() is not null
      ? throw this.ErrorAt(target, $"cannot use except* statement with {PyAst.Name(target)}") : (PyNode?)null);
  });

  private List<PyNode>? InvalidFinallyStatement() => this.Rule(() => this.Expect("finally") is { } keyword ? this.MissingBody<List<PyNode>>(keyword, "'finally' statement") : null);

  private PyNode? InvalidExceptStatementIndent() => this.Rule(() => {
    this.Try(() => {
      if (this.Expect("except") is not { } keyword || this.Expression() is null)
        return null;

      this.Try(() => this.Expect("as") is not null ? this.NameToken() : null);
      return this.MissingBody<PyNode>(keyword, "'except' statement");
    });

    return this.Try(() => this.Expect("except") is { } keyword ? this.MissingBody<PyNode>(keyword, "'except' statement") : null);
  });

  private PyNode? InvalidExceptStarStatementIndent() => this.Rule(() => {
    if (this.Expect("except") is not { } keyword || this.Expect("*") is null || this.Expression() is null)
      return null;

    this.Try(() => this.Expect("as") is not null ? this.NameToken() : null);
    return this.MissingBody<PyNode>(keyword, "'except*' statement");
  });

  private PyNode? InvalidMatchStatement() => this.Rule(() => {
    this.Try(() => this.SoftKeyword("match") is not null && this.SubjectExpression() is not null && this.Expect(PyTokenKind.Newline) is not null
      ? throw this.Error("expected ':'") : (PyNode?)null);

    return this.Try(() => {
      var keyword = this.Peek();
      return this.SoftKeyword("match") is not null && this.SubjectExpression() is not null ? this.MissingBody<PyNode>(keyword, "'match' statement") : null;
    });
  });

  private PyNode? InvalidCaseBlock() => this.Rule(() => {
    this.Try(() => {
      if (this.SoftKeyword("case") is null || this.Patterns() is null)
        return null;

      this.Try(this.Guard);
      return this.Expect(PyTokenKind.Newline) is not null ? throw this.Error("expected ':'") : (PyNode?)null;
    });

    return this.Try(() => {
      var keyword = this.Peek();
      if (this.SoftKeyword("case") is null || this.Patterns() is null)
        return null;

      this.Try(this.Guard);
      return this.MissingBody<PyNode>(keyword, "'case' statement");
    });
  });

  private PyNode? InvalidAsPattern() => this.Rule(() => {
    this.Try(() => this.OrPattern() is not null && this.Expect("as") is not null && this.SoftKeyword("_") is { } underscore
      ? throw this.ErrorAt(underscore, "cannot use '_' as a target") : (PyNode?)null);

    return this.Try(() => this.OrPattern() is not null && this.Expect("as") is not null && this.Expression() is { } target
      ? throw this.ErrorAt(target, $"cannot use {PyAst.Name(target)} as pattern target") : (PyNode?)null);
  });

  private PyNode? InvalidClassPattern() => this.Rule<PyNode>(() => {
    if (this.NameOrAttr() is null || this.Expect("(") is null)
      return null;

    this.Try(() => this.PositionalPatterns() is not null && this.Expect(",") is not null ? this.Dummy() : null);
    if (this.KeywordPatterns() is null || this.Expect(",") is null || this.PositionalPatterns() is not { } positional)
      return null;

    throw this.ErrorAt(positional[0], positional[^1], "positional patterns follow keyword patterns");
  });

  private List<PyNode?>? InvalidTypeParams() => this.Rule(() =>
    this.Expect("[") is not null && this.Expect("]") is { } close ? throw this.ErrorFrom(close, "Type parameter list cannot be empty") : (List<PyNode?>?)null);

  private PyNode? InvalidTypeParam() => this.Rule<PyNode>(() => {
    foreach (var (stars, what) in new[] { ("*", "TypeVarTuple"), ("**", "ParamSpec") }) {
      this.Try(() => this.Expect(stars) is not null && this.NameToken() is not null && this.Expect(":") is { } colon && this.Expression() is { } expression
        ? throw this.ErrorFrom(colon, expression.Is("Tuple") ? $"cannot use constraints with {what}" : $"cannot use bound with {what}") : (PyNode?)null);
    }

    return null;
  });

  // ---------------------------------------------------------------------------------------------
  // Parameters

  private PyNode? InvalidParameters() => this.InvalidParametersOf(false);

  private PyNode? InvalidLambdaParameters() => this.InvalidParametersOf(true);

  private PyNode? InvalidParametersOf(bool lambda) => this.Rule(() => {
    System.Func<List<PyNode>?> slashNoDefault = lambda ? this.LambdaSlashNoDefault : this.SlashNoDefault;
    System.Func<SlashDefaults?> slashWithDefault = lambda ? this.LambdaSlashWithDefault : this.SlashWithDefault;
    System.Func<PyNode?> noDefault = lambda ? this.LambdaParamNoDefault : this.ParamNoDefault;
    System.Func<NameDefault?> withDefault = lambda ? this.LambdaParamWithDefault : this.ParamWithDefault;
    System.Func<NameDefault?> maybeDefault = lambda ? this.LambdaParamMaybeDefault : this.ParamMaybeDefault;
    bool Slash() => this.Try(() => this.Try(slashNoDefault) is not null || this.Try(slashWithDefault) is not null ? this.Dummy() : null) is not null;

    this.Try(() => this.Expect("/") is { } slash && this.Expect(",") is not null ? throw this.ErrorAt(slash, slash, "at least one argument must precede /") : (PyNode?)null);

    this.Try(() => {
      if (!Slash())
        return null;

      this.Repeat(maybeDefault, 0);
      return this.Expect("/") is { } slash ? throw this.ErrorAt(slash, slash, "/ may appear only once") : (PyNode?)null;
    });

    this.Try<PyNode>(() => {
      this.Try(slashNoDefault);
      this.Repeat(noDefault, 0);
      var helper = this.Try(() => this.Try(slashWithDefault) is not null ? this.Dummy() : this.Repeat(withDefault, 1) is not null ? this.Dummy() : null);
      if (helper is null || noDefault() is not { } parameter)
        return null;

      throw this.ErrorAt(parameter, "parameter without a default follows parameter with a default");
    });

    this.Try(() => {
      this.Repeat(noDefault, 0);
      if (this.Expect("(") is not { } open)
        return null;

      var inner = lambda ? (object?)this.Gather(",", this.LambdaParam) : this.Repeat(this.ParamNoDefault, 1);
      if (inner is null)
        return null;

      this.Expect(",");
      return this.Expect(")") is { } close
        ? throw this.ErrorAt(open, close, lambda ? "Lambda expression parameters cannot be parenthesized" : "Function parameters cannot be parenthesized")
        : (PyNode?)null;
    });

    this.Try(() => {
      Slash();
      this.Repeat(maybeDefault, 0);
      if (this.Expect("*") is null || (this.Expect(",") is null && noDefault() is null))
        return null;

      this.Repeat(maybeDefault, 0);
      return this.Expect("/") is { } slash ? throw this.ErrorAt(slash, slash, "/ must be ahead of *") : (PyNode?)null;
    });

    return this.Try(() => this.Repeat(maybeDefault, 1) is not null && this.Expect("/") is not null && this.Expect("*") is { } star
      ? throw this.ErrorAt(star, star, "expected comma between / and *") : (PyNode?)null);
  });

  private StarParameters? InvalidStarEtc() => this.Rule(() => {
    this.Try(() => {
      if (this.Expect("*") is not { } star)
        return null;

      var named = this.Expect(")") is not null || (this.Expect(",") is not null && (this.Expect(")") ?? this.Expect("**")) is not null);
      return named ? throw this.ErrorAt(star, star, "named arguments must follow bare *") : (StarParameters?)null;
    });

    this.Try(() => this.Expect("*") is not null && this.Param() is not null && this.Expect("=") is { } sign
      ? throw this.ErrorAt(sign, sign, "var-positional argument cannot have default value") : (StarParameters?)null);

    return this.Try<StarParameters>(() => {
      if (this.Expect("*") is null || (this.Try(this.ParamNoDefault) is null && this.Expect(",") is null))
        return null;

      this.Repeat(this.ParamMaybeDefault, 0);
      if (this.Expect("*") is not { } again || (this.Try(this.ParamNoDefault) is null && this.Expect(",") is null))
        return null;

      throw this.ErrorAt(again, again, "* argument may appear only once");
    });
  });

  private StarParameters? InvalidLambdaStarEtc() => this.Rule(() => {
    this.Try(() => {
      if (this.Expect("*") is null)
        return null;

      var named = this.Expect(":") is not null || (this.Expect(",") is not null && (this.Expect(":") ?? this.Expect("**")) is not null);
      return named ? throw this.Error("named arguments must follow bare *") : (StarParameters?)null;
    });

    this.Try(() => this.Expect("*") is not null && this.LambdaParam() is not null && this.Expect("=") is { } sign
      ? throw this.ErrorAt(sign, sign, "var-positional argument cannot have default value") : (StarParameters?)null);

    return this.Try<StarParameters>(() => {
      if (this.Expect("*") is null || (this.Try(this.LambdaParamNoDefault) is null && this.Expect(",") is null))
        return null;

      this.Repeat(this.LambdaParamMaybeDefault, 0);
      if (this.Expect("*") is not { } again || (this.Try(this.LambdaParamNoDefault) is null && this.Expect(",") is null))
        return null;

      throw this.ErrorAt(again, again, "* argument may appear only once");
    });
  });

  private PyNode? InvalidKwds() => this.InvalidKwdsOf(this.Param);

  private PyNode? InvalidLambdaKwds() => this.InvalidKwdsOf(this.LambdaParam);

  private PyNode? InvalidKwdsOf(System.Func<PyNode?> param) => this.Rule(() => {
    this.Try(() => this.Expect("**") is not null && param() is not null && this.Expect("=") is { } sign
      ? throw this.ErrorAt(sign, sign, "var-keyword argument cannot have default value") : (PyNode?)null);

    this.Try(() => this.Expect("**") is not null && param() is not null && this.Expect(",") is not null && param() is { } next
      ? throw this.ErrorAt(next, "arguments cannot follow var-keyword argument") : (PyNode?)null);

    return this.Try<PyNode>(() => {
      if (this.Expect("**") is null || param() is null || this.Expect(",") is null)
        return null;

      var token = this.Expect("*") ?? this.Expect("**") ?? this.Expect("/");
      return token is null ? null : throw this.ErrorAt(token, token, "arguments cannot follow var-keyword argument");
    });
  });

  private PyNode? InvalidDefault() => this.Rule(() =>
    this.Expect("=") is { } sign && this.CheckAny(")", ",") ? throw this.ErrorAt(sign, sign, "expected default value expression") : (PyNode?)null);

}
