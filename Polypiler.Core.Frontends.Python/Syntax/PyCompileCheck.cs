using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Polypiler.Core.Frontends.Python.Ast;

namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>
/// The errors <c>compile(source, path, "exec")</c> raises after the parser: those of the symbol table
/// (<c>Python/symtable.c</c>, converted: scopes, <c>global</c> and <c>nonlocal</c>, parameters,
/// comprehensions, annotation and type-parameter scopes, <c>await</c>, <c>async</c> statements) and of
/// the code generator (<c>Python/codegen.c</c>: <c>return</c>, <c>yield</c>, loops and <c>except*</c>,
/// starred expressions, repeated keywords, patterns, static nesting). The first error in CPython's order
/// is the one given.
/// </summary>
internal sealed class PyCompileCheck {

  private const int DefGlobal = 1, DefLocal = 2, DefParam = 4, DefNonlocal = 8, Use = 16, DefImport = 128, DefAnnot = 256, DefCompIter = 512,
    DefTypeParam = 1024;

  private const int DefBound = DefLocal | DefParam | DefImport;

  private enum Block { Module, Function, Class, Annotation, TypeVariable, TypeAlias, TypeParameters }

  private enum Comprehension { None, List, Set, Dict, Generator }

  private sealed class Scope(Block type, PyNode? node) {
    public Block Type { get; } = type;
    public PyNode? Node { get; } = node;
    public readonly List<string> Order = [];
    public readonly Dictionary<string, int> Symbols = [];
    public readonly List<Scope> Children = [];
    public readonly Dictionary<string, PyNode> Directives = [];
    public bool Coroutine;
    public bool Generator;
    public Comprehension Comprehension;
    public bool CompIterTarget;
    public int CompIterExpr;
    public bool InUnevaluatedAnnotation;
    public bool InConditionalBlock;
    public bool CanSeeClassScope;
    public bool HasConditionalAnnotations;
    public Scope? AnnotationBlock;
    public string? ScopeInfo;
    public HashSet<string>? MangledNames;

    public bool IsFunctionLike => this.Type is Block.Function or Block.Annotation or Block.TypeVariable or Block.TypeAlias or Block.TypeParameters;

    public int Flags(string name) => this.Symbols.TryGetValue(name, out var flags) ? flags : 0;
  }

  private readonly List<Scope> _stack = [];
  private readonly Dictionary<PyNode, Scope> _scopes = new(ReferenceEqualityComparer.Instance);
  private Scope _current = null!;
  private string? _private;
  private PyRange? _future;
  private bool _futureAnnotations;

  /// <summary>The error <c>compile</c> raises for a parsed module, or <see langword="null"/>.</summary>
  public static PyError? Check(PyNode module) {
    var check = new PyCompileCheck();
    try {
      check.Build(module);
      check.Analyse(check._current, null, [], [], null);
      new Codegen(check._scopes, check._futureAnnotations).Module(module);
    } catch (PyError error) {
      return error;
    }

    return null;
  }

  private static PyError Error(PyNode node, string message) {
    var range = node.Range;
    return range is null
      ? new PyError("SyntaxError", message, 1, 1, 1, 1)
      : new PyError("SyntaxError", message, range.Line, range.Column, range.EndLine, range.EndColumn);
  }

  // ---------------------------------------------------------------------------------------------
  // Building the symbol table

  private void Build(PyNode module) {
    var body = module.Nodes("body");
    var i = body.Count > 0 && PyFuture.IsDocstring(body[0]) ? 1 : 0;
    for (; i < body.Count && body[i] is { Kind: "ImportFrom" } statement && statement.Number("level") == 0 && statement.Text("module") == "__future__"; i++)
      this._future = statement.Range;

    this._futureAnnotations = PyFuture.Annotations(module);
    this.Enter(Block.Module, module);
    foreach (var statement in body)
      this.Statement(statement);
  }

  private Scope Enter(Block type, PyNode? node) {
    var scope = new Scope(type, node);
    if (node is not null && type is Block.Function or Block.Class)
      this._scopes.TryAdd(node, scope);

    this.EnterExisting(scope, true);
    if (type is Block.Annotation or Block.TypeVariable or Block.TypeAlias) {
      // The annotation and type scopes read the format they are asked for as a parameter.
      this.AddDef(".format", DefParam, node ?? this._stack[0].Node!);
      this.AddDef(".format", Use, node ?? this._stack[0].Node!);
    }

    return scope;
  }

  private void EnterExisting(Scope scope, bool addToChildren) {
    var previous = this._stack.Count > 0 ? this._current : null;
    this._stack.Add(scope);
    if (previous is not null) {
      // bpo-37757: no assignment expression anywhere in the outermost iterable of a comprehension.
      scope.CompIterExpr = previous.CompIterExpr;
      if (previous.MangledNames is not null && scope.Type != Block.Class)
        scope.MangledNames = previous.MangledNames;
    }

    this._current = scope;
    if (this._futureAnnotations && scope.Type == Block.Annotation)
      return;

    if (addToChildren && previous is not null)
      previous.Children.Add(scope);
  }

  private void Exit() {
    this._stack.RemoveAt(this._stack.Count - 1);
    this._current = this._stack.Count > 0 ? this._stack[^1] : this._current;
  }

  private string Mangle(Scope scope, string name) {
    if (scope.MangledNames is { } mangled && !mangled.Contains(name))
      return name;

    var owner = this._private;
    if (owner is null || !name.StartsWith("__", System.StringComparison.Ordinal) || name.EndsWith("__", System.StringComparison.Ordinal) || name.Contains('.', System.StringComparison.Ordinal))
      return name;

    var stripped = owner.TrimStart('_');
    return stripped.Length == 0 ? name : "_" + stripped + name;
  }

  private static void CheckName(string name, PyNode at, string context) {
    if (name != "__debug__")
      return;

    if (context == "Store")
      throw Error(at, "cannot assign to __debug__");

    if (context == "Del")
      throw Error(at, "cannot delete __debug__");
  }

  private void AddDef(string name, int flag, PyNode at, string? context = null) => this.AddDef(name, flag, at, this._current, context ?? (flag == Use ? "Load" : "Store"));

  private void AddDef(string name, int flag, PyNode at, Scope scope, string context) {
    if ((flag & (DefParam | DefLocal | DefImport)) != 0)
      CheckName(name, at, context);

    if ((flag & DefTypeParam) != 0)
      scope.MangledNames?.Add(name);

    var mangled = this.Mangle(scope, name);
    int value;
    if (scope.Symbols.TryGetValue(mangled, out var existing)) {
      if ((flag & DefParam) != 0 && (existing & DefParam) != 0)
        throw Error(at, $"duplicate argument '{name}' in function definition");

      if ((flag & DefTypeParam) != 0 && (existing & DefTypeParam) != 0)
        throw Error(at, $"duplicate type parameter '{name}'");

      value = existing | flag;
    } else {
      value = flag;
      scope.Order.Add(mangled);
    }

    if (scope.CompIterTarget) {
      if ((value & (DefGlobal | DefNonlocal)) != 0)
        throw Error(at, $"comprehension inner loop cannot rebind assignment expression target '{name}'");

      value |= DefCompIter;
    }

    scope.Symbols[mangled] = value;
  }

  private int Lookup(Scope scope, string name) => scope.Flags(this.Mangle(scope, name));

  private void Directive(string name, PyNode at) => this._current.Directives.TryAdd(this.Mangle(this._current, name), at);

  private void Conditional(System.Action body) {
    var previous = this._current.InConditionalBlock;
    this._current.InConditionalBlock = true;
    body();
    this._current.InConditionalBlock = previous;
  }

  private void Statements(IEnumerable<PyNode> statements) {
    foreach (var statement in statements)
      this.Statement(statement);
  }

  private void Expressions(IEnumerable<PyNode?> expressions) {
    foreach (var expression in expressions)
      if (expression is not null)
        this.Expression(expression);
  }

  private void EnterTypeParameters(string name, PyNode node, bool defaults, bool kwDefaults, string kind) {
    var classBlock = this._current.Type == Block.Class;
    this.Enter(Block.TypeParameters, node);
    if (classBlock) {
      this._current.CanSeeClassScope = true;
      this.AddDef("__classdict__", Use, node);
    }

    if (kind == "ClassDef") {
      this.AddDef(".type_params", DefLocal, node);
      this.AddDef(".type_params", Use, node);
      this.AddDef(".generic_base", DefLocal, node);
      this.AddDef(".generic_base", Use, node);
    }

    if (defaults)
      this.AddDef(".defaults", DefParam, node);

    if (kwDefaults)
      this.AddDef(".kwdefaults", DefParam, node);

    _ = name;
  }

  private void Function(PyNode statement, bool async) {
    var name = statement.Text("name")!;
    var arguments = statement.Node("args")!;
    this.AddDef(name, DefLocal, statement);
    this.Expressions(arguments.Nodes("defaults"));
    this.Expressions(ListOf(arguments, "kw_defaults"));
    this.Expressions(statement.Nodes("decorator_list"));
    var typeParams = statement.Nodes("type_params");
    if (typeParams.Count > 0) {
      this.EnterTypeParameters(name, statement, arguments.Nodes("defaults").Count > 0, ListOf(arguments, "kw_defaults").Any(value => value is not null), statement.Kind);
      foreach (var parameter in typeParams)
        this.TypeParam(parameter);
    }

    var function = new Scope(Block.Function, statement);
    this._scopes.TryAdd(statement, function);
    this.Annotations(statement, arguments, statement.Node("returns"));
    this.EnterExisting(function, true);
    if (async)
      this._current.Coroutine = true;

    this.Arguments(arguments);
    this.Statements(statement.Nodes("body"));
    this.Exit();
    if (typeParams.Count > 0)
      this.Exit();
  }

  private static IReadOnlyList<PyNode?> ListOf(PyNode node, string field) => node.Get(field) as IReadOnlyList<PyNode?> ?? [];

  private void Statement(PyNode statement) {
    switch (statement.Kind) {
      case "FunctionDef":
        this.Function(statement, false);
        break;
      case "AsyncFunctionDef":
        this.Function(statement, true);
        break;
      case "ClassDef": {
        var name = statement.Text("name")!;
        this.AddDef(name, DefLocal, statement);
        this.Expressions(statement.Nodes("decorator_list"));
        var previous = this._private;
        var typeParams = statement.Nodes("type_params");
        if (typeParams.Count > 0) {
          this.EnterTypeParameters(name, statement, false, false, "ClassDef");
          this._private = name;
          this._current.MangledNames = [];
          foreach (var parameter in typeParams)
            this.TypeParam(parameter);
        }

        this.Expressions(statement.Nodes("bases"));
        foreach (var keyword in statement.Nodes("keywords"))
          if (keyword.Text("arg") is { } arg)
            CheckName(arg, keyword, "Store");

        foreach (var keyword in statement.Nodes("keywords"))
          this.Expression(keyword.Node("value")!);

        this.Enter(Block.Class, statement);
        this._private = name;
        if (typeParams.Count > 0) {
          this.AddDef("__type_params__", DefLocal, statement);
          this.AddDef(".type_params", Use, statement);
        }

        this.Statements(statement.Nodes("body"));
        this.Exit();
        if (typeParams.Count > 0)
          this.Exit();

        this._private = previous;
        break;
      }
      case "TypeAlias": {
        var target = statement.Node("name")!;
        this.Expression(target);
        var name = target.Text("id")!;
        var inClass = this._current.Type == Block.Class;
        var typeParams = statement.Nodes("type_params");
        if (typeParams.Count > 0) {
          this.EnterTypeParameters(name, statement, false, false, "TypeAlias");
          foreach (var parameter in typeParams)
            this.TypeParam(parameter);
        }

        this.Enter(Block.TypeAlias, statement);
        this._current.CanSeeClassScope = inClass;
        if (inClass)
          this.AddDef("__classdict__", Use, statement.Node("value")!);

        this.Expression(statement.Node("value")!);
        this.Exit();
        if (typeParams.Count > 0)
          this.Exit();

        break;
      }
      case "Return":
        if (statement.Node("value") is { } value)
          this.Expression(value);

        break;
      case "Delete":
        this.Expressions(statement.Nodes("targets"));
        break;
      case "Assign":
        this.Expressions(statement.Nodes("targets"));
        this.Expression(statement.Node("value")!);
        break;
      case "AnnAssign": {
        var target = statement.Node("target")!;
        var simple = statement.Number("simple") == 1;
        if (target.Is("Name")) {
          var id = target.Text("id")!;
          var current = this.Lookup(this._current, id);
          if ((current & (DefGlobal | DefNonlocal)) != 0 && this._current.Type != Block.Module && simple)
            throw Error(statement, (current & DefGlobal) != 0 ? $"annotated name '{id}' can't be global" : $"annotated name '{id}' can't be nonlocal");

          if (simple)
            this.AddDef(id, DefAnnot | DefLocal, target);
          else if (statement.Node("value") is not null)
            this.AddDef(id, DefLocal, target);
        } else {
          this.Expression(target);
        }

        this.Annotation(statement.Node("annotation")!, statement);
        if (statement.Node("value") is { } assigned)
          this.Expression(assigned);

        break;
      }
      case "AugAssign":
        this.Expression(statement.Node("target")!);
        this.Expression(statement.Node("value")!);
        break;
      case "For" or "AsyncFor":
        if (statement.Is("AsyncFor") && !this._current.Coroutine)
          throw Error(statement, "'async for' outside async function");

        this.Expression(statement.Node("target")!);
        this.Expression(statement.Node("iter")!);
        this.Conditional(() => {
          this.Statements(statement.Nodes("body"));
          this.Statements(statement.Nodes("orelse"));
        });
        break;
      case "While" or "If":
        this.Expression(statement.Node("test")!);
        this.Conditional(() => {
          this.Statements(statement.Nodes("body"));
          this.Statements(statement.Nodes("orelse"));
        });
        break;
      case "Match":
        this.Expression(statement.Node("subject")!);
        this.Conditional(() => {
          foreach (var @case in statement.Nodes("cases")) {
            this.Pattern(@case.Node("pattern")!);
            if (@case.Node("guard") is { } guard)
              this.Expression(guard);

            this.Statements(@case.Nodes("body"));
          }
        });
        break;
      case "Raise":
        if (statement.Node("exc") is { } exception) {
          this.Expression(exception);
          if (statement.Node("cause") is { } cause)
            this.Expression(cause);
        }

        break;
      case "Try" or "TryStar":
        this.Conditional(() => {
          this.Statements(statement.Nodes("body"));
          foreach (var handler in statement.Nodes("handlers")) {
            if (handler.Node("type") is { } type)
              this.Expression(type);

            if (handler.Text("name") is { } name)
              this.AddDef(name, DefLocal, handler);

            this.Statements(handler.Nodes("body"));
          }

          this.Statements(statement.Nodes("orelse"));
          this.Statements(statement.Nodes("finalbody"));
        });
        break;
      case "Assert":
        this.Expression(statement.Node("test")!);
        if (statement.Node("msg") is { } message)
          this.Expression(message);

        break;
      case "Import":
        foreach (var alias in statement.Nodes("names"))
          this.Alias(alias);

        break;
      case "ImportFrom":
        foreach (var alias in statement.Nodes("names"))
          this.Alias(alias);

        if (statement.Number("level") == 0 && statement.Text("module") == "__future__" && statement.Range is { } range
          && (this._future is not { } future || range.Line > future.Line || (range.Line == future.EndLine && range.Offset > future.EndOffset)))
          throw Error(statement, "from __future__ imports must occur at the beginning of the file");

        break;
      case "Global" or "Nonlocal": {
        var global = statement.Is("Global");
        foreach (var name in statement.Texts("names")) {
          var current = this.Lookup(this._current, name);
          if ((current & (DefParam | DefLocal | Use | DefAnnot)) != 0) {
            var what = global ? "global" : "nonlocal";
            throw Error(statement, (current & DefParam) != 0 ? $"name '{name}' is parameter and {what}"
              : (current & Use) != 0 ? $"name '{name}' is used prior to {what} declaration"
              : (current & DefAnnot) != 0 ? $"annotated name '{name}' can't be {what}"
              : $"name '{name}' is assigned to before {what} declaration");
          }

          this.AddDef(name, global ? DefGlobal : DefNonlocal, statement);
          this.Directive(name, statement);
        }

        break;
      }
      case "Expr":
        this.Expression(statement.Node("value")!);
        break;
      case "With" or "AsyncWith":
        if (statement.Is("AsyncWith") && !this._current.Coroutine)
          throw Error(statement, "'async with' outside async function");

        this.Conditional(() => {
          foreach (var item in statement.Nodes("items")) {
            this.Expression(item.Node("context_expr")!);
            if (item.Node("optional_vars") is { } target)
              this.Expression(target);
          }

          this.Statements(statement.Nodes("body"));
        });
        break;
    }
  }

  private void Alias(PyNode alias) {
    var name = alias.Text("asname") ?? alias.Text("name")!;
    if (name == "*") {
      if (this._current.Type != Block.Module)
        throw Error(alias, "import * only allowed at module level");

      return;
    }

    var dot = name.IndexOf('.', System.StringComparison.Ordinal);
    this.AddDef(dot >= 0 ? name[..dot] : name, DefImport, alias);
  }

  private void Annotation(PyNode annotation, PyNode owner) {
    var unevaluated = this._current.Type == Block.Function;
    if (((this._current.Type == Block.Class && this._current.InConditionalBlock) || this._current.Type == Block.Module) && !this._current.HasConditionalAnnotations) {
      this._current.HasConditionalAnnotations = true;
      this.AddDef("__conditional_annotations__", Use, annotation);
    }

    var parent = this._current;
    if (parent.AnnotationBlock is null) {
      var block = this.Enter(Block.Annotation, null);
      parent.AnnotationBlock = block;
      if (parent.Type == Block.Class) {
        block.CanSeeClassScope = true;
        this.AddDef("__classdict__", Use, annotation);
      }
    } else {
      this.EnterExisting(parent.AnnotationBlock, false);
    }

    if (unevaluated)
      this._current.InUnevaluatedAnnotation = true;

    this.Expression(annotation);
    if (unevaluated)
      this._current.InUnevaluatedAnnotation = false;

    this.Exit();
    _ = owner;
  }

  private void Annotations(PyNode statement, PyNode arguments, PyNode? returns) {
    var inClass = this._current.CanSeeClassScope;
    var type = this._current.Type;
    this.Enter(Block.Annotation, null);
    if (inClass || type == Block.Class) {
      this._current.CanSeeClassScope = true;
      this.AddDef("__classdict__", Use, statement);
    }

    foreach (var parameter in arguments.Nodes("posonlyargs").Concat(arguments.Nodes("args")))
      if (parameter.Node("annotation") is { } annotation)
        this.Expression(annotation);

    if (arguments.Node("vararg")?.Node("annotation") is { } varargs)
      this.Expression(varargs);

    if (arguments.Node("kwarg")?.Node("annotation") is { } kwargs)
      this.Expression(kwargs);

    foreach (var parameter in arguments.Nodes("kwonlyargs"))
      if (parameter.Node("annotation") is { } annotation)
        this.Expression(annotation);

    if (returns is not null)
      this.Expression(returns);

    this.Exit();
  }

  private void Arguments(PyNode arguments) {
    foreach (var parameter in arguments.Nodes("posonlyargs").Concat(arguments.Nodes("args")).Concat(arguments.Nodes("kwonlyargs")))
      this.AddDef(parameter.Text("arg")!, DefParam, parameter);

    if (arguments.Node("vararg") is { } varargs)
      this.AddDef(varargs.Text("arg")!, DefParam, varargs);

    if (arguments.Node("kwarg") is { } kwargs)
      this.AddDef(kwargs.Text("arg")!, DefParam, kwargs);
  }

  private void TypeParam(PyNode parameter) {
    var name = parameter.Text("name")!;
    this.AddDef(name, DefTypeParam | DefLocal, parameter);
    switch (parameter.Kind) {
      case "TypeVar":
        var bound = parameter.Node("bound");
        this.BoundOrDefault(bound, name, parameter, bound is null ? null : bound.Is("Tuple") ? "a TypeVar constraint" : "a TypeVar bound");
        this.BoundOrDefault(parameter.Node("default_value"), name, parameter, "a TypeVar default");
        break;
      case "TypeVarTuple":
        this.BoundOrDefault(parameter.Node("default_value"), name, parameter, "a TypeVarTuple default");
        break;
      default:
        this.BoundOrDefault(parameter.Node("default_value"), name, parameter, "a ParamSpec default");
        break;
    }
  }

  private void BoundOrDefault(PyNode? expression, string name, PyNode parameter, string? info) {
    if (name == "__classdict__")
      throw Error(parameter, $"reserved name '{name}' cannot be used for type parameter");

    if (expression is null)
      return;

    var inClass = this._current.CanSeeClassScope;
    this.Enter(Block.TypeVariable, null);
    this._current.CanSeeClassScope = inClass;
    if (inClass)
      this.AddDef("__classdict__", Use, expression);

    this._current.ScopeInfo = info;
    this.Expression(expression);
    this.Exit();
  }

  private void Pattern(PyNode pattern) {
    switch (pattern.Kind) {
      case "MatchValue":
        this.Expression(pattern.Node("value")!);
        break;
      case "MatchSequence" or "MatchOr":
        foreach (var inner in pattern.Nodes("patterns"))
          this.Pattern(inner);

        break;
      case "MatchStar":
        if (pattern.Text("name") is { } star)
          this.AddDef(star, DefLocal, pattern);

        break;
      case "MatchMapping":
        this.Expressions(pattern.Nodes("keys"));
        foreach (var inner in pattern.Nodes("patterns"))
          this.Pattern(inner);

        if (pattern.Text("rest") is { } rest)
          this.AddDef(rest, DefLocal, pattern);

        break;
      case "MatchClass":
        this.Expression(pattern.Node("cls")!);
        foreach (var inner in pattern.Nodes("patterns"))
          this.Pattern(inner);

        var attributes = pattern.Texts("kwd_attrs");
        var patterns = pattern.Nodes("kwd_patterns");
        for (var i = 0; i < attributes.Count; i++)
          CheckName(attributes[i], patterns[i], "Store");

        foreach (var inner in patterns)
          this.Pattern(inner);

        break;
      case "MatchAs":
        if (pattern.Node("pattern") is { } subpattern)
          this.Pattern(subpattern);

        if (pattern.Text("name") is { } name)
          this.AddDef(name, DefLocal, pattern);

        break;
    }
  }

  private void RaiseIfAnnotationBlock(string what, PyNode at) {
    var message = this._current.Type switch {
      Block.Annotation => $"{what} cannot be used within an annotation",
      Block.TypeVariable => $"{what} cannot be used within {this._current.ScopeInfo}",
      Block.TypeAlias => $"{what} cannot be used within a type alias",
      Block.TypeParameters => $"{what} cannot be used within the definition of a generic",
      _ => null
    };
    if (message is not null)
      throw Error(at, message);
  }

  private void Expression(PyNode expression) {
    switch (expression.Kind) {
      case "NamedExpr":
        this.RaiseIfAnnotationBlock("named expression", expression);
        if (this._current.CompIterExpr > 0)
          throw Error(expression, "assignment expression cannot be used in a comprehension iterable expression");

        if (this._current.Comprehension != Comprehension.None)
          this.ExtendNamedExpressionScope(expression.Node("target")!);

        this.Expression(expression.Node("value")!);
        this.Expression(expression.Node("target")!);
        break;
      case "Lambda": {
        var arguments = expression.Node("args")!;
        this.Expressions(arguments.Nodes("defaults"));
        this.Expressions(ListOf(arguments, "kw_defaults"));
        this.Enter(Block.Function, expression);
        this._scopes.TryAdd(expression, this._current);
        this.Arguments(arguments);
        this.Expression(expression.Node("body")!);
        this.Exit();
        break;
      }
      case "GeneratorExp" or "ListComp" or "SetComp" or "DictComp":
        this.Comprehend(expression);
        break;
      case "Yield" or "YieldFrom":
        this.RaiseIfAnnotationBlock("yield expression", expression);
        if (expression.Node("value") is { } yielded)
          this.Expression(yielded);

        this._current.Generator = true;
        if (this._current.Comprehension != Comprehension.None)
          throw Error(expression, this._current.Comprehension switch {
            Comprehension.List => "'yield' inside list comprehension",
            Comprehension.Set => "'yield' inside set comprehension",
            Comprehension.Dict => "'yield' inside dict comprehension",
            _ => "'yield' inside generator expression"
          });

        break;
      case "Await":
        this.RaiseIfAnnotationBlock("await expression", expression);
        if (!this._current.IsFunctionLike)
          throw Error(expression, "'await' outside function");

        if (!(this._current.Type == Block.Function && this._current.Coroutine) && this._current.Comprehension == Comprehension.None)
          throw Error(expression, "'await' outside async function");

        this.Expression(expression.Node("value")!);
        this._current.Coroutine = true;
        break;
      case "Call":
        this.Expression(expression.Node("func")!);
        this.Expressions(expression.Nodes("args"));
        foreach (var keyword in expression.Nodes("keywords"))
          if (keyword.Text("arg") is { } arg)
            CheckName(arg, keyword, "Store");

        foreach (var keyword in expression.Nodes("keywords"))
          this.Expression(keyword.Node("value")!);

        break;
      case "Attribute":
        CheckName(expression.Text("attr")!, expression, expression.Operator("ctx"));
        this.Expression(expression.Node("value")!);
        break;
      case "Name":
        if (!this._current.InUnevaluatedAnnotation) {
          var context = expression.Operator("ctx");
          var id = expression.Text("id")!;
          this.AddDef(id, context == "Load" ? Use : DefLocal, expression, context);
          if (context == "Load" && this._current.IsFunctionLike && id == "super")
            this.AddDef("__class__", Use, expression);
        }

        break;
      case "Constant":
        break;
      default:
        foreach (var (name, value) in expression.Fields) {
          if (name.StartsWith('#'))
            continue;

          if (value is PyNode { Range: not null } child)
            this.Expression(child);
          else if (value is IReadOnlyList<PyNode?> children)
            this.Expressions(children);
        }

        break;
    }
  }

  private void ExtendNamedExpressionScope(PyNode target) {
    var name = target.Text("id")!;
    for (var i = this._stack.Count - 1; i >= 0; i--) {
      var scope = this._stack[i];
      if (scope.Comprehension != Comprehension.None) {
        var flags = this.Lookup(scope, name);
        if ((flags & DefCompIter) != 0 && (flags & DefLocal) != 0)
          throw Error(target, $"assignment expression cannot rebind comprehension iteration variable '{name}'");

        continue;
      }

      switch (scope.Type) {
        case Block.Function:
          this.AddDef(name, (this.Lookup(scope, name) & DefGlobal) != 0 ? DefGlobal : DefNonlocal, target);
          this.Directive(name, target);
          this.AddDef(name, DefLocal, target, scope, "Store");
          return;
        case Block.Module:
          this.AddDef(name, DefGlobal, target);
          this.Directive(name, target);
          this.AddDef(name, DefGlobal, target, scope, "Store");
          return;
        case Block.Class:
          throw Error(target, "assignment expression within a comprehension cannot be used in a class body");
        case Block.TypeParameters:
          throw Error(target, "assignment expression within a comprehension cannot be used within the definition of a generic");
        case Block.TypeAlias:
          throw Error(target, "assignment expression within a comprehension cannot be used in a type alias");
        case Block.TypeVariable:
          throw Error(target, "assignment expression within a comprehension cannot be used in a TypeVar bound");
      }
    }
  }

  private void Comprehend(PyNode expression) {
    var generators = expression.Nodes("generators");
    var outermost = generators[0];
    var generator = expression.Is("GeneratorExp");
    this._current.CompIterExpr++;
    this.Expression(outermost.Node("iter")!);
    this._current.CompIterExpr--;

    this.Enter(Block.Function, expression);
    this._scopes.TryAdd(expression, this._current);
    this._current.Comprehension = expression.Kind switch {
      "ListComp" => Comprehension.List,
      "SetComp" => Comprehension.Set,
      "DictComp" => Comprehension.Dict,
      _ => Comprehension.Generator
    };
    if (outermost.Number("is_async") == 1)
      this._current.Coroutine = true;

    this.AddDef(".0", DefParam, expression);
    this._current.CompIterTarget = true;
    this.Expression(outermost.Node("target")!);
    this._current.CompIterTarget = false;
    this.Expressions(outermost.Nodes("ifs"));
    foreach (var clause in generators.Skip(1)) {
      this._current.CompIterTarget = true;
      this.Expression(clause.Node("target")!);
      this._current.CompIterTarget = false;
      this._current.CompIterExpr++;
      this.Expression(clause.Node("iter")!);
      this._current.CompIterExpr--;
      this.Expressions(clause.Nodes("ifs"));
      if (clause.Number("is_async") == 1)
        this._current.Coroutine = true;
    }

    if (expression.Node("value") is { } value)
      this.Expression(value);

    this.Expression(expression.Node(expression.Is("DictComp") ? "key" : "elt")!);
    this._current.Generator = generator;
    var async = this._current.Coroutine && !generator;
    this.Exit();
    if (async && !(this._current.Type == Block.Function && this._current.Coroutine) && this._current.Comprehension == Comprehension.None)
      throw Error(expression, "asynchronous comprehension outside of an asynchronous function");

    if (async)
      this._current.Coroutine = true;
  }

  // ---------------------------------------------------------------------------------------------
  // Analysing the scopes: what global and nonlocal refer to (analyze_block, analyze_name)

  private void Analyse(Scope scope, HashSet<string>? bound, HashSet<string> global, HashSet<string> typeParams, Scope? classEntry) {
    var local = new HashSet<string>();
    var newGlobal = new HashSet<string>();
    var newBound = new HashSet<string>();
    typeParams = [.. typeParams];
    global = [.. global];
    if (scope.Type == Block.Class) {
      newGlobal.UnionWith(global);
      if (bound is not null)
        newBound.UnionWith(bound);
    }

    foreach (var name in scope.Order) {
      var flags = scope.Symbols[name];
      PyError AtDirective(string message) => Error(scope.Directives.TryGetValue(name, out var at) ? at : scope.Node!, message);
      if ((flags & DefGlobal) != 0) {
        if ((flags & DefNonlocal) != 0)
          throw AtDirective($"name '{name}' is nonlocal and global");

        global.Add(name);
        bound?.Remove(name);
        continue;
      }

      if ((flags & DefNonlocal) != 0) {
        if (bound is null)
          throw AtDirective("nonlocal declaration not allowed at module level");

        if (!bound.Contains(name))
          throw AtDirective($"no binding for nonlocal '{name}' found");

        if (typeParams.Contains(name))
          throw AtDirective($"nonlocal binding not allowed for type parameter '{name}'");

        continue;
      }

      if ((flags & DefBound) != 0) {
        local.Add(name);
        global.Remove(name);
        if ((flags & DefTypeParam) != 0)
          typeParams.Add(name);
        else
          typeParams.Remove(name);
      }
    }

    if (scope.Type != Block.Class) {
      if (scope.IsFunctionLike)
        newBound.UnionWith(local);

      if (bound is not null)
        newBound.UnionWith(bound);

      newGlobal.UnionWith(global);
    } else {
      newBound.Add("__class__");
      newBound.Add("__classdict__");
      newBound.Add("__conditional_annotations__");
    }

    foreach (var child in scope.Children) {
      var childClass = child.CanSeeClassScope ? scope.Type == Block.Class ? scope : classEntry : null;
      this.Analyse(child, [.. newBound], newGlobal, typeParams, childClass);
    }
  }

  // ---------------------------------------------------------------------------------------------
  // The checks of the code generator

  private sealed class Codegen(Dictionary<PyNode, Scope> scopes, bool futureAnnotations) {

    private const int MaxBlocks = 21;

    private enum Frame { Loop, ExceptStar, Other }

    private sealed class Unit(Scope? scope, bool async) {
      public Scope? Scope { get; } = scope;
      public bool Async { get; } = async;
      public readonly List<Frame> Frames = [];
      public bool FunctionLike => this.Scope?.IsFunctionLike ?? false;
    }

    private Unit _unit = new(null, false);

    public void Module(PyNode module) {
      this._unit = new Unit(null, false);
      this.Body(module);
    }

    /// <summary>
    /// The statements of a module, class or function; its docstring is cleaned as UTF-8
    /// (<c>_PyCompile_CleanDoc</c>), which a lone surrogate in it cannot be.
    /// </summary>
    private void Body(PyNode owner) {
      var body = owner.Nodes("body");
      if (body.Count > 0 && PyFuture.IsDocstring(body[0]) && body[0].Node("value")!.CodePoints is { Count: > 0 } points) {
        // The docstring is cleaned after str.expandtabs(), so the positions are of the expanded text.
        var expanded = new List<int>();
        foreach (var point in points) {
          if (point != '\t') {
            expanded.Add(point);
            continue;
          }

          var column = expanded.Count - (expanded.FindLastIndex(c => c is '\n' or '\r') + 1);
          expanded.AddRange(Enumerable.Repeat((int)' ', 8 - column % 8));
        }

        throw new PyError("UnicodeEncodeError", PyUnicode.EncodeError(expanded)!, 1, 1, 1, 1);
      }

      this.Statements(body);
    }

    private void Push(Frame frame, PyNode at) {
      if (this._unit.Frames.Count >= MaxBlocks)
        throw Error(at, "too many statically nested blocks");

      this._unit.Frames.Add(frame);
    }

    private void Pop() => this._unit.Frames.RemoveAt(this._unit.Frames.Count - 1);

    private static readonly Scope _functionLike = new(Block.Annotation, null);

    /// <summary>Compiles in a scope of its own: a function, class or lambda, or with <see langword="null"/> an annotation or type scope.</summary>
    private void InUnit(PyNode? node, bool async, System.Action body) {
      var previous = this._unit;
      this._unit = new Unit(node is null ? _functionLike : scopes.GetValueOrDefault(node), async);
      try {
        body();
      } finally {
        this._unit = previous;
      }
    }

    private void Statements(IEnumerable<PyNode> statements) {
      foreach (var statement in statements)
        this.Statement(statement);
    }

    private void Expressions(IEnumerable<PyNode?> expressions) {
      foreach (var expression in expressions)
        if (expression is not null)
          this.Expression(expression);
    }

    private void Unwind(PyNode at, bool toLoop, string message) {
      for (var i = this._unit.Frames.Count - 1; i >= 0; i--) {
        if (this._unit.Frames[i] == Frame.ExceptStar)
          throw Error(at, "'break', 'continue' and 'return' cannot appear in an except* block");

        if (toLoop && this._unit.Frames[i] == Frame.Loop)
          return;
      }

      if (toLoop)
        throw Error(at, message);
    }

    private void Function(PyNode statement, bool async) {
      var arguments = statement.Node("args")!;
      this.Expressions(statement.Nodes("decorator_list"));
      this.TypeParams(statement.Nodes("type_params"));
      this.Expressions(arguments.Nodes("defaults"));
      this.Expressions(ListOf(arguments, "kw_defaults"));
      if (!futureAnnotations)
        this.InUnit(null, false, () => {
          foreach (var parameter in arguments.Nodes("args").Concat(arguments.Nodes("posonlyargs")).Append(arguments.Node("vararg")).Concat(arguments.Nodes("kwonlyargs"))
            .Append(arguments.Node("kwarg")))
            if (parameter?.Node("annotation") is { } annotation)
              this.Expression(annotation.Is("Starred") ? annotation.Node("value")! : annotation);

          if (statement.Node("returns") is { } returns)
            this.Expression(returns);
        });

      this.InUnit(statement, async, () => {
        var scope = this._unit.Scope;
        if (scope is not null && (scope.Generator || scope.Coroutine))
          this.Push(Frame.Other, statement);

        this.Body(statement);
      });
    }

    private void TypeParams(IReadOnlyList<PyNode> parameters) {
      var seenDefault = false;
      foreach (var parameter in parameters) {
        if (parameter.Node("bound") is { } bound)
          this.InUnit(null, false, () => this.Expression(bound));

        if (parameter.Node("default_value") is { } value) {
          this.InUnit(null, false, () => this.Expression(value.Is("Starred") ? value.Node("value")! : value));
          seenDefault = true;
          continue;
        }

        if (seenDefault && parameter.Kind is "TypeVar" or "TypeVarTuple")
          throw Error(parameter, $"non-default type parameter '{parameter.Text("name")}' follows default type parameter");
      }
    }

    private void Statement(PyNode statement) {
      switch (statement.Kind) {
        case "FunctionDef":
          this.Function(statement, false);
          break;
        case "AsyncFunctionDef":
          this.Function(statement, true);
          break;
        case "ClassDef":
          this.Expressions(statement.Nodes("decorator_list"));
          this.TypeParams(statement.Nodes("type_params"));
          this.Keywords(statement.Nodes("keywords"));
          this.Arguments(statement.Nodes("bases"));
          foreach (var keyword in statement.Nodes("keywords"))
            this.Expression(keyword.Node("value")!);

          this.InUnit(statement, false, () => this.Body(statement));
          break;
        case "TypeAlias":
          this.TypeParams(statement.Nodes("type_params"));
          this.InUnit(null, false, () => this.Expression(statement.Node("value")!));
          break;
        case "Return":
          if (!this._unit.FunctionLike)
            throw Error(statement, "'return' outside function");

          if (statement.Node("value") is { } value) {
            if (this._unit.Scope is { Coroutine: true, Generator: true })
              throw Error(statement, "'return' with value in async generator");

            this.Expression(value);
          }

          this.Unwind(statement, false, "");
          break;
        case "Delete":
          this.Expressions(statement.Nodes("targets"));
          break;
        case "Assign":
          this.Expression(statement.Node("value")!);
          foreach (var target in statement.Nodes("targets"))
            this.Target(target);

          break;
        case "AugAssign":
          this.Expression(statement.Node("target")!);
          this.Expression(statement.Node("value")!);
          break;
        case "AnnAssign":
          if (!futureAnnotations && this._unit.Scope?.Type is null or Block.Class)
            this.InUnit(null, false, () => this.Expression(statement.Node("annotation")!));

          if (statement.Node("value") is { } annotated) {
            this.Expression(annotated);
            this.Target(statement.Node("target")!);
          } else if (!statement.Node("target")!.Is("Name")) {
            this.Expression(statement.Node("target")!.Node("value")!);
            if (statement.Node("target")!.Node("slice") is { } slice)
              this.Expression(slice);
          }

          break;
        case "For" or "AsyncFor":
          this.Expression(statement.Node("iter")!);
          this.Push(Frame.Loop, statement);
          this.Target(statement.Node("target")!);
          this.Statements(statement.Nodes("body"));
          this.Pop();
          this.Statements(statement.Nodes("orelse"));
          break;
        case "While":
          this.Expression(statement.Node("test")!);
          this.Push(Frame.Loop, statement);
          this.Statements(statement.Nodes("body"));
          this.Pop();
          this.Statements(statement.Nodes("orelse"));
          break;
        case "If":
          this.Expression(statement.Node("test")!);
          this.Statements(statement.Nodes("body"));
          this.Statements(statement.Nodes("orelse"));
          break;
        case "With" or "AsyncWith":
          this.With(statement, statement.Nodes("items"), 0);
          break;
        case "Match":
          this.Match(statement);
          break;
        case "Raise":
          if (statement.Node("exc") is { } exception)
            this.Expression(exception);

          if (statement.Node("cause") is { } cause)
            this.Expression(cause);

          break;
        case "Try" or "TryStar":
          this.Try(statement);
          break;
        case "Assert":
          this.Expression(statement.Node("test")!);
          if (statement.Node("msg") is { } message)
            this.Expression(message);

          break;
        case "Expr":
          this.Expression(statement.Node("value")!);
          break;
        case "Break":
          this.Unwind(statement, true, "'break' outside loop");
          break;
        case "Continue":
          this.Unwind(statement, true, "'continue' not properly in loop");
          break;
      }
    }

    private void With(PyNode statement, IReadOnlyList<PyNode> items, int index) {
      if (index == items.Count) {
        this.Statements(statement.Nodes("body"));
        return;
      }

      var item = items[index];
      this.Expression(item.Node("context_expr")!);
      this.Push(Frame.Other, statement);
      if (item.Node("optional_vars") is { } target)
        this.Target(target);

      this.With(statement, items, index + 1);
      this.Pop();
    }

    private void Try(PyNode statement) {
      var handlers = statement.Nodes("handlers");
      var final = statement.Nodes("finalbody");
      if (final.Count > 0) {
        this.Push(Frame.Other, statement);
        if (handlers.Count > 0)
          this.TryExcept(statement, handlers);
        else
          this.Statements(statement.Nodes("body"));

        this.Pop();
        this.Push(Frame.Other, statement);
        this.Statements(final);
        this.Pop();
        return;
      }

      this.TryExcept(statement, handlers);
    }

    private void TryExcept(PyNode statement, IReadOnlyList<PyNode> handlers) {
      this.Push(Frame.Other, statement);
      this.Statements(statement.Nodes("body"));
      this.Pop();
      var star = statement.Is("TryStar");
      this.Push(star ? Frame.ExceptStar : Frame.Other, statement);
      for (var i = 0; i < handlers.Count; i++) {
        var handler = handlers[i];
        if (handler.Node("type") is not { } type) {
          if (i < handlers.Count - 1)
            throw Error(handler, "default 'except:' must be last");
        } else {
          this.Expression(type);
        }

        this.Push(Frame.Other, handler);
        this.Statements(handler.Nodes("body"));
        this.Pop();
      }

      this.Pop();
      this.Statements(statement.Nodes("orelse"));
    }

    private void Keywords(IReadOnlyList<PyNode> keywords) {
      for (var i = 0; i < keywords.Count; i++) {
        if (keywords[i].Text("arg") is not { } name)
          continue;

        for (var j = i + 1; j < keywords.Count; j++)
          if (keywords[j].Text("arg") == name)
            throw Error(keywords[j], $"keyword argument repeated: {name}");
      }
    }

    /// <summary>Positional arguments and displays, where a starred expression is allowed.</summary>
    private void Arguments(IEnumerable<PyNode> elements) {
      foreach (var element in elements)
        this.Expression(element.Is("Starred") ? element.Node("value")! : element);
    }

    private void Target(PyNode target) {
      switch (target.Kind) {
        case "Tuple" or "List": {
          var elements = target.Nodes("elts");
          var seen = false;
          for (var i = 0; i < elements.Count; i++) {
            if (!elements[i].Is("Starred"))
              continue;

            if (seen)
              throw Error(target, "multiple starred expressions in assignment");

            if (i >= 1 << 8 || elements.Count - i - 1 >= int.MaxValue >> 8)
              throw Error(target, "too many expressions in star-unpacking assignment");

            seen = true;
          }

          foreach (var element in elements)
            this.Target(element.Is("Starred") ? element.Node("value")! : element);

          break;
        }
        case "Starred":
          throw Error(target, "starred assignment target must be in a list or tuple");
        case "Attribute":
          this.Expression(target.Node("value")!);
          break;
        case "Subscript":
          this.Expression(target.Node("value")!);
          this.Expression(target.Node("slice")!);
          break;
      }
    }

    private void Expression(PyNode expression) {
      switch (expression.Kind) {
        case "Lambda":
          this.Expressions(expression.Node("args")!.Nodes("defaults"));
          this.Expressions(ListOf(expression.Node("args")!, "kw_defaults"));
          this.InUnit(expression, false, () => this.Expression(expression.Node("body")!));
          break;
        case "Yield":
          if (!this._unit.FunctionLike)
            throw Error(expression, "'yield' outside function");

          if (expression.Node("value") is { } yielded)
            this.Expression(yielded);

          break;
        case "YieldFrom":
          if (!this._unit.FunctionLike)
            throw Error(expression, "'yield from' outside function");

          if (this._unit.Async)
            throw Error(expression, "'yield from' inside async function");

          this.Expression(expression.Node("value")!);
          break;
        case "Call":
          this.Keywords(expression.Nodes("keywords"));
          this.Expression(expression.Node("func")!);
          this.Arguments(expression.Nodes("args"));
          foreach (var keyword in expression.Nodes("keywords"))
            this.Expression(keyword.Node("value")!);

          break;
        case "List" or "Tuple" or "Set":
          if (expression.Node("ctx")?.Kind is "Store")
            this.Target(expression);
          else
            this.Arguments(expression.Nodes("elts"));

          break;
        case "Starred":
          throw Error(expression, expression.Operator("ctx") == "Store" ? "starred assignment target must be in a list or tuple" : "can't use starred expression here");
        case "GeneratorExp" or "ListComp" or "SetComp" or "DictComp":
          foreach (var clause in expression.Nodes("generators")) {
            this.Expression(clause.Node("iter")!);
            this.Target(clause.Node("target")!);
            this.Expressions(clause.Nodes("ifs"));
          }

          if (expression.Node("key") is { } key)
            this.Expression(key);

          if (expression.Node("value") is { } entry && expression.Is("DictComp"))
            this.Expression(entry);

          if (expression.Node("elt") is { } element)
            this.Expression(element);

          break;
        case "Dict":
          this.Expressions(ListOf(expression, "keys"));
          this.Expressions(expression.Nodes("values"));
          break;
        case "Constant" or "Name":
          break;
        default:
          foreach (var (name, value) in expression.Fields) {
            if (name.StartsWith('#'))
              continue;

            if (value is PyNode { Range: not null } child)
              this.Expression(child);
            else if (value is IReadOnlyList<PyNode?> children)
              this.Expressions(children);
          }

          break;
      }
    }

    // Patterns

    private sealed class PatternContext {
      public List<string> Stores = [];
      public bool AllowIrrefutable;
    }

    private void Match(PyNode statement) {
      this.Expression(statement.Node("subject")!);
      var cases = statement.Nodes("cases");
      var last = cases[^1].Node("pattern")!;
      var hasDefault = Wildcard(last) && cases.Count > 1;
      for (var i = 0; i < cases.Count - (hasDefault ? 1 : 0); i++) {
        var @case = cases[i];
        var context = new PatternContext { AllowIrrefutable = @case.Node("guard") is not null || i == cases.Count - 1 };
        this.Pattern(@case.Node("pattern")!, context);
        if (@case.Node("guard") is { } guard)
          this.Expression(guard);

        this.Statements(@case.Nodes("body"));
      }

      if (hasDefault) {
        if (cases[^1].Node("guard") is { } guard)
          this.Expression(guard);

        this.Statements(cases[^1].Nodes("body"));
      }
    }

    private static bool Wildcard(PyNode pattern) => pattern.Is("MatchAs") && pattern.Text("name") is null && pattern.Node("pattern") is null;

    private static bool WildcardStar(PyNode pattern) => pattern.Is("MatchStar") && pattern.Text("name") is null;

    private static void Store(PyNode at, string? name, PatternContext context) {
      if (name is null)
        return;

      if (context.Stores.Contains(name))
        throw Error(at, $"multiple assignments to name {Repr(name)} in pattern");

      context.Stores.Add(name);
    }

    /// <summary>A text as Python's <c>repr</c> of a <c>str</c> writes it (<c>unicode_repr</c>).</summary>
    private static string Repr(string text) => QuotedRepr(PyUnicode.CodePoints(text), point => point switch {
      < 0x20 or 0x7F => $"\\x{point:x2}",
      < 0x7F => char.ConvertFromUtf32(point),
      _ when PyUnicode.IsPrintable(point) => char.ConvertFromUtf32(point),
      <= 0xFF => $"\\x{point:x2}",
      <= 0xFFFF => $"\\u{point:x4}",
      _ => $"\\U{point:x8}"
    });

    /// <summary>
    /// The quotes and escapes both reprs share: single quotes unless the text holds one and no double quote,
    /// the quote and the backslash escaped, tab, newline and carriage return by name, any other point as given.
    /// </summary>
    private static string QuotedRepr(IReadOnlyList<int> points, Func<int, string> other) {
      var quote = points.Contains('\'') && !points.Contains('"') ? '"' : '\'';
      var text = new StringBuilder().Append(quote);
      foreach (var point in points)
        text.Append(point switch {
          '\\' => "\\\\",
          '\t' => "\\t",
          '\n' => "\\n",
          '\r' => "\\r",
          _ when point == quote => "\\" + quote,
          _ => other(point)
        });

      return text.Append(quote).ToString();
    }

    private void Subpattern(PyNode pattern, PatternContext context) {
      var allow = context.AllowIrrefutable;
      context.AllowIrrefutable = true;
      this.Pattern(pattern, context);
      context.AllowIrrefutable = allow;
    }

    private void Pattern(PyNode pattern, PatternContext context) {
      switch (pattern.Kind) {
        case "MatchValue": {
          var value = pattern.Node("value")!;
          if (!(value.Is("Constant") || value.Is("Attribute") || Folded(value) is not null))
            throw Error(pattern, "patterns may only match literals and attribute lookups");

          this.Expression(value);
          break;
        }
        case "MatchSingleton":
          break;
        case "MatchSequence": {
          var patterns = pattern.Nodes("patterns");
          var star = -1;
          var onlyWildcard = true;
          var starWildcard = false;
          for (var i = 0; i < patterns.Count; i++) {
            var item = patterns[i];
            if (item.Is("MatchStar")) {
              if (star >= 0)
                throw Error(pattern, "multiple starred names in sequence pattern");

              starWildcard = WildcardStar(item);
              onlyWildcard &= starWildcard;
              star = i;
              continue;
            }

            onlyWildcard &= Wildcard(item);
          }

          if (onlyWildcard)
            break;

          for (var i = 0; i < patterns.Count; i++) {
            if (starWildcard && (Wildcard(patterns[i]) || i == star))
              continue;

            this.Subpattern(patterns[i], context);
          }

          break;
        }
        case "MatchStar":
          Store(pattern, pattern.Text("name"), context);
          break;
        case "MatchMapping": {
          var keys = pattern.Nodes("keys");
          var seen = new List<object?>();
          foreach (var key in keys) {
            var folded = Folded(key);
            if (key.Is("Constant") || folded is not null) {
              var value = key.Is("Constant") ? key.Value : folded!.Value;
              if (seen.Any(other => PyEquals(other, value)))
                throw Error(pattern, $"mapping pattern checks duplicate key ({PyRepr(value)})");

              seen.Add(value);
            } else if (!key.Is("Attribute")) {
              throw Error(pattern, "mapping pattern keys may only match literals and attribute lookups");
            }

            this.Expression(key);
          }

          foreach (var inner in pattern.Nodes("patterns"))
            this.Subpattern(inner, context);

          Store(pattern, pattern.Text("rest"), context);
          break;
        }
        case "MatchClass": {
          var attributes = pattern.Texts("kwd_attrs");
          var keywordPatterns = pattern.Nodes("kwd_patterns");
          for (var i = 0; i < attributes.Count; i++)
            for (var j = i + 1; j < attributes.Count; j++)
              if (attributes[i] == attributes[j])
                throw Error(keywordPatterns[j], $"attribute name repeated in class pattern: {attributes[i]}");

          this.Expression(pattern.Node("cls")!);
          foreach (var inner in pattern.Nodes("patterns").Concat(keywordPatterns))
            if (!Wildcard(inner))
              this.Subpattern(inner, context);

          break;
        }
        case "MatchAs":
          if (pattern.Node("pattern") is not { } subpattern) {
            if (!context.AllowIrrefutable)
              throw Error(pattern, pattern.Text("name") is { } capture
                ? $"name capture {Repr(capture)} makes remaining patterns unreachable"
                : "wildcard makes remaining patterns unreachable");

            Store(pattern, pattern.Text("name"), context);
            break;
          }

          this.Pattern(subpattern, context);
          Store(pattern, pattern.Text("name"), context);
          break;
        case "MatchOr": {
          var alternatives = pattern.Nodes("patterns");
          var outer = context.Stores;
          var allow = context.AllowIrrefutable;
          List<string>? control = null;
          for (var i = 0; i < alternatives.Count; i++) {
            context.Stores = [];
            context.AllowIrrefutable = i == alternatives.Count - 1 && allow;
            this.Pattern(alternatives[i], context);
            if (control is null)
              control = context.Stores;
            else if (control.Count != context.Stores.Count || control.Any(name => !context.Stores.Contains(name)))
              throw Error(pattern, "alternative patterns bind different names");
          }

          context.Stores = outer;
          context.AllowIrrefutable = allow;
          foreach (var name in control!) {
            if (context.Stores.Contains(name))
              throw Error(pattern, $"multiple assignments to name {Repr(name)} in pattern");

            context.Stores.Add(name);
          }

          break;
        }
      }
    }

    /// <summary>The constant a negated number or a complex literal folds to before code generation, or <see langword="null"/>.</summary>
    private static PyNode? Folded(PyNode expression) {
      object? Value(PyNode node) => node.Is("Constant") ? node.Value : Folded(node)?.Value;
      switch (expression.Kind) {
        case "UnaryOp" when expression.Operator() == "USub" && Value(expression.Node("operand")!) is { } operand:
          // The grammar negates only number literals.
          return operand switch {
            BigInteger integer => PyAst.Make("Constant", expression.Range, -integer, null),
            double number => PyAst.Make("Constant", expression.Range, -number, null),
            _ => PyAst.Make("Constant", expression.Range, new PyComplex(-((PyComplex)operand).Real, -((PyComplex)operand).Imaginary), null)
          };
        case "BinOp" when expression.Operator() is "Add" or "Sub" && Value(expression.Node("left")!) is { } left && Value(expression.Node("right")!) is PyComplex right: {
          // The grammar adds an imaginary literal only to a real one, signed or not.
          var real = left is BigInteger integer ? (double)integer : (double)left;
          var sign = expression.Operator() == "Add" ? 1 : -1;
          return PyAst.Make("Constant", expression.Range, new PyComplex(real + sign * right.Real, sign * right.Imaginary), null);
        }
        default:
          return null;
      }
    }

    /// <summary>Python's <c>==</c> of two constants, as a set compares its elements.</summary>
    private static bool PyEquals(object? left, object? right) {
      static (double Real, double Imaginary, BigInteger? Integer)? Number(object? value) => value switch {
        bool flag => (flag ? 1 : 0, 0, flag ? BigInteger.One : BigInteger.Zero),
        BigInteger integer => ((double)integer, 0, integer),
        double number => (number, 0, null),
        PyComplex complex => (complex.Real, complex.Imaginary, null),
        _ => null
      };

      if (Number(left) is { } a && Number(right) is { } b)
        return a.Integer is { } x && b.Integer is { } y ? x == y : a.Real == b.Real && a.Imaginary == b.Imaginary;

      return (left, right) switch {
        (null, null) => true,
        (string s, string t) => s == t,
        (byte[] s, byte[] t) => s.AsSpan().SequenceEqual(t),
        _ => false
      };
    }

    private static string PyRepr(object? value) => value switch {
      null => "None",
      true => "True",
      false => "False",
      BigInteger integer => integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
      double number => FloatRepr(number, true),
      PyComplex { Real: 0 } complex when !double.IsNegative(complex.Real) => FloatRepr(complex.Imaginary, false) + "j",
      PyComplex complex => $"({FloatRepr(complex.Real, false)}{(double.IsNegative(complex.Imaginary) ? "" : "+")}{FloatRepr(complex.Imaginary, false)}j)",
      string text => Repr(text),
      _ => "b" + QuotedRepr([.. ((byte[])value!).Select(b => (int)b)], point => point is >= 0x20 and < 0x7F ? ((char)point).ToString() : $"\\x{point:x2}")
    };

    /// <summary>
    /// A float as Python's <c>repr</c> writes it: the shortest digits that read back, positional from
    /// 1e-4 to 1e16 and exponential outside; a whole number ends in <c>.0</c> unless it is a part of a complex.
    /// A literal is never NaN.
    /// </summary>
    private static string FloatRepr(double number, bool point) {
      if (double.IsInfinity(number))
        return number > 0 ? "inf" : "-inf";

      var sign = double.IsNegative(number) ? "-" : "";
      if (number == 0)
        return sign + (point ? "0.0" : "0");

      // The shortest round-trip digits and the decimal exponent of the first of them.
      var shortest = Math.Abs(number).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
      var exponentAt = shortest.IndexOf('E', StringComparison.Ordinal);
      var mantissa = exponentAt < 0 ? shortest : shortest[..exponentAt];
      var exponent = exponentAt < 0 ? 0 : int.Parse(shortest[(exponentAt + 1)..], System.Globalization.CultureInfo.InvariantCulture);
      var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
      var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
      exponent += (dot < 0 ? mantissa.Length : dot) - 1;
      var leading = digits.TrimStart('0');
      exponent -= digits.Length - leading.Length;
      digits = leading.TrimEnd('0');

      string body;
      if (exponent is >= -4 and < 16) {
        if (exponent < 0)
          body = "0." + new string('0', -exponent - 1) + digits;
        else if (digits.Length <= exponent + 1)
          body = digits + new string('0', exponent + 1 - digits.Length) + (point ? ".0" : "");
        else
          body = digits[..(exponent + 1)] + "." + digits[(exponent + 1)..];
      } else {
        body = (digits.Length == 1 ? digits : digits[..1] + "." + digits[1..]) + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
      }

      return sign + body;
    }

  }

}
