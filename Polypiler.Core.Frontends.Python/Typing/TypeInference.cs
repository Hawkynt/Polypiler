using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;

namespace Polypiler.Core.Frontends.Python.Typing;

/// <summary>
/// Gives every parameter, local, result, attribute and module variable of the import a static type. A
/// parameter has the type of its hint; one without a hint has the type of its default and of the
/// arguments the import's own calls pass. A local, a result and an attribute of a plain class have the
/// type of the values that reach them, whatever a hint says - a hint is a recorded contract, never
/// trusted; a dataclass field has the type of its hint. Two number types join to the wider one (a
/// <c>bool</c> is an <c>int</c>, an <c>int</c> converts to a <c>float</c>); <c>None</c> and a type
/// join to an optional of it; an empty list takes its element type from what is appended or stored.
/// The rules run to a fixed point, so recursion and calls in any order are typed.
/// </summary>
internal sealed class TypeInference(IReadOnlyList<ModuleModel> modules, IReadOnlyDictionary<PyNode, Variable> bound, List<Diagnostic> diagnostics) {

  private readonly HashSet<string> _conflicts = [];
  private bool _changed;
  private ModuleModel _module = null!;

  private Scope ScopeOf(FunctionModel? function) => new(this._module, function, bound);

  private SourceFile File => this._module.File;

  /// <summary>Types the import; every failure adds a diagnostic.</summary>
  public void Run() {
    foreach (var module in modules) {
      this._module = module;
      this.Hints(module);
    }

    if (diagnostics.Count > 0)
      return;

    for (var round = 0; round < 64; ++round) {
      this._changed = false;
      foreach (var module in modules) {
        this._module = module;
        this.ModuleStatements(module);
        foreach (var function in module.AllFunctions)
          this.Infer(function);
      }

      // A result only recursion reaches takes its hint, from which the rest follows.
      if (!this._changed)
        foreach (var module in modules) {
          this._module = module;
          foreach (var function in module.AllFunctions.Where(function => function.Returns.Kind == PyKind.Unknown && function.ReturnHint is not null && TypeHints.Of(function.ReturnHint!, module) is not null))
            this.Result(function, Hinted(function, module)!, function.ReturnHint!);
        }

      if (!this._changed)
        break;
    }

    foreach (var module in modules) {
      this._module = module;
      this.Unknowns(module);
    }
  }

  private static PyType? Hinted(FunctionModel function, ModuleModel module) => TypeHints.Of(function.ReturnHint!, module);

  private void Unknowns(ModuleModel module) {
    foreach (var function in module.AllFunctions) {
      foreach (var variable in function.Parameters.Concat(function.Locals.Values).Where(variable => variable.Type.IsIncomplete)) {
        var parameter = function.Parameters.Contains(variable);
        var uncalled = parameter && !this.Called(function);
        diagnostics.Add(this.File.At(variable.Node, PythonFrontend.MissingTypeDescriptor,
          $"the type of '{variable.Name}' in '{function.Qualified}' is not known: "
          + (variable.Type.Kind == PyKind.List ? "nothing tells the type of its elements; add a type hint such as 'list[int]'"
            : variable.Type.Kind is PyKind.Dict or PyKind.Set ? $"nothing tells the type of its {(variable.Type.Kind == PyKind.Dict ? "keys and values; add a type hint such as 'dict[str, int]'" : "elements; add a type hint such as 'set[int]'")}"
            : !parameter ? "no value of a known type reaches it"
            : uncalled ? $"it has no type hint and nothing in the import calls '{function.Qualified}'; add a type hint such as '{variable.Name}: int'"
            : "it has no type hint and no call of the import passes it a value of a known type")));
      }

      if (function.Returns.IsIncomplete && !(function.Returns.Kind == PyKind.Unknown && function.Owner is not null && function.Name == "__init__"))
        diagnostics.Add(this.File.At(function.Node, PythonFrontend.MissingTypeDescriptor, $"the result type of '{function.Qualified}' is not known: it has no return type hint and no return value of a known type"));
    }

    foreach (var type in module.Classes.Values)
      foreach (var field in type.Fields.Where(field => field.Type.IsIncomplete))
        diagnostics.Add(this.File.At(field.Node, PythonFrontend.MissingTypeDescriptor, $"the type of the attribute '{field.Name}' of '{type.Name}' is not known: no value of a known type is assigned to it"));

    foreach (var global in module.GlobalVariables.Values.Where(global => global.Type.IsIncomplete))
      diagnostics.Add(this.File.At(global.Node, PythonFrontend.MissingTypeDescriptor, $"the type of the module variable '{global.Name}' is not known"));
  }

  /// <summary>Whether anything in the import calls a function, which would pass its parameters a type.</summary>
  private bool Called(FunctionModel function)
    => modules.Any(module => module.Node.Nodes("body").Any(statement => ScopeResolver.Walk(statement, true).Concat(Descendants(statement)).Any(node => node.Is("Call") && node.Node("func") is { } callee
      && (callee.Text("id") == function.Name || callee.Text("attr") == function.Name))));

  private static IEnumerable<PyNode> Descendants(PyNode statement) {
    foreach (var field in new[] { "body", "orelse", "finalbody" })
      foreach (var inner in statement.Nodes(field))
        foreach (var node in ScopeResolver.Expressions(inner).SelectMany(expression => ScopeResolver.Walk(expression, true)).Concat(Descendants(inner)))
          yield return node;

    foreach (var handler in statement.Nodes("handlers"))
      foreach (var inner in handler.Nodes("body"))
        foreach (var node in ScopeResolver.Expressions(inner).SelectMany(expression => ScopeResolver.Walk(expression, true)).Concat(Descendants(inner)))
          yield return node;

    foreach (var matchCase in statement.Nodes("cases"))
      foreach (var inner in matchCase.Nodes("body"))
        foreach (var node in ScopeResolver.Expressions(inner).SelectMany(expression => ScopeResolver.Walk(expression, true)).Concat(Descendants(inner)))
          yield return node;
  }

  /// <summary>
  /// Types the variables of the comprehensions and lambdas of a module-level expression, which is
  /// evaluated before the functions are typed: from the iterables they take their elements from.
  /// </summary>
  public void ModuleExpression(ModuleModel module, PyNode expression) {
    this._module = module;
    for (var round = 0; round < 16; ++round) {
      this._changed = false;
      this.Expression(null, expression);
      if (!this._changed)
        break;
    }
  }

  /// <summary>The types of the hints: of parameters, of results and of dataclass fields; a hint the import does not know adds a diagnostic.</summary>
  private void Hints(ModuleModel module) {
    foreach (var function in module.AllFunctions) {
      for (var index = 0; index < function.Parameters.Count; ++index) {
        var parameter = function.Parameters[index];
        if (index == 0 && function.Owner is { } owner && !function.IsStatic) {
          parameter.Type = owner.IsEnum ? PyType.Enum(owner) : PyType.Object(owner);
          continue;
        }

        if (parameter.Hint is null)
          continue;

        if (TypeHints.Of(parameter.Hint, module) is { } type && type.Kind is not (PyKind.None or PyKind.Iterator))
          parameter.Type = type;
        else
          diagnostics.Add(module.File.At(parameter.Hint, PythonFrontend.MissingTypeDescriptor, $"the type hint '{module.File.Quote(parameter.Hint)}' of '{parameter.Name}' is not supported; supported are {TypeHints.Supported}"));
      }

      if (function.ReturnHint is { } returns && TypeHints.Of(returns, module) is null)
        diagnostics.Add(module.File.At(returns, PythonFrontend.MissingTypeDescriptor, $"the return type hint '{module.File.Quote(returns)}' of '{function.Qualified}' is not supported; supported are {TypeHints.Supported} and None"));
    }

    foreach (var type in module.Classes.Values.Where(type => type.Kind is ClassKind.Dataclass or ClassKind.NamedTuple))
      foreach (var field in type.Fields) {
        var hint = field.Node.Node("annotation")!;
        if (TypeHints.Of(hint, module) is { } hinted && hinted.Kind is not (PyKind.None or PyKind.Iterator))
          field.Type = hinted;
        else
          diagnostics.Add(module.File.At(hint, PythonFrontend.MissingTypeDescriptor, $"the type hint '{module.File.Quote(hint)}' of the field '{field.Name}' of '{type.Name}' is not supported; supported are {TypeHints.Supported}"));
      }

    // The constructor of a dataclass takes its fields, of their types.
    foreach (var type in module.Classes.Values.Where(type => type.Kind is ClassKind.Dataclass or ClassKind.NamedTuple && type.Method("__init__") is null))
      foreach (var (field, parameter) in type.Fields.Zip(type.InitParameters))
        parameter.Type = field.Type;
  }

  /// <summary>The module's statements: the types of what its names and its globals are assigned, and of the defaults of its functions.</summary>
  private void ModuleStatements(ModuleModel module) {
    var scope = this.ScopeOf(null);
    foreach (var statement in module.Statements) {
      if (statement.Is("FunctionDef")) {
        var function = module.Functions[statement.Text("name")!];
        foreach (var parameter in function.Parameters.Where(parameter => parameter.Default is not null && parameter.Hint is null))
          this.Store(function, parameter, ExpressionTypes.Of(parameter.Default!, scope), parameter.Default!);

        continue;
      }

      if (statement.Kind is "AugAssign" or "ClassDef")
        continue;

      var value = statement.Node("value")!;
      foreach (var target in Collector.Assigned(statement)) {
        var type = statement.Is("Assign") && statement.Nodes("targets")[0].Is("Tuple")
          ? ExpressionTypes.Of(value, scope) is { Kind: PyKind.Tuple } tuple && tuple.Items.Count > Index(statement, target) ? tuple.Items[Index(statement, target)] : PyType.Unknown
          : ExpressionTypes.Of(value, scope);
        if (module.GlobalVariables.TryGetValue(target.Text("id")!, out var global))
          this.Merge($"{module.Name}.{global.Name}", global.Type, type, target, joined => global.Type = joined, () => { });
      }
    }

    foreach (var function in module.AllFunctions.Where(function => function.Parent is not null || function.Owner is not null))
      foreach (var parameter in function.Parameters.Where(parameter => parameter.Default is not null && parameter.Hint is null))
        this.Store(function, parameter, ExpressionTypes.Of(parameter.Default!, this.ScopeOf(function.Parent)), parameter.Default!);
  }

  private static int Index(PyNode statement, PyNode target) {
    var elements = statement.Nodes("targets")[0].Nodes("elts");
    for (var index = 0; index < elements.Count; ++index)
      if (ReferenceEquals(elements[index], target))
        return index;

    return 0;
  }

  private void Infer(FunctionModel function) {
    foreach (var statement in function.Body)
      this.Statement(function, statement);

    if (function.EndReachable && !function.IsGenerator)
      this.Result(function, PyType.None, function.Node);

    if (function.IsGenerator && function.Returns.Kind == PyKind.Unknown && function.ReturnHint is { } hint && TypeHints.Of(hint, this._module) is { Kind: PyKind.Iterator } hinted)
      this.Result(function, hinted, hint);
  }

  private void Statement(FunctionModel function, PyNode statement) {
    // A nested function is typed as a function of its own.
    if (statement.Is("FunctionDef"))
      return;

    var scope = this.ScopeOf(function);
    foreach (var expression in ScopeResolver.Expressions(statement))
      this.Expression(function, expression);

    switch (statement.Kind) {
      case "Assign":
        this.Assign(function, statement.Nodes("targets"), statement.Node("value")!);
        break;
      case "AnnAssign" when statement.Node("value") is { } value:
        // A hint completes a value whose own type is incomplete, an empty list: it is the one thing that tells its elements.
        var own = ExpressionTypes.Of(value, scope);
        if ((own.IsIncomplete || own.Kind == PyKind.None) && TypeHints.Of(statement.Node("annotation")!, this._module) is { } hinted && PyTypes.Join(own, hinted) is { IsIncomplete: false } completed)
          this.Target(function, statement.Node("target")!, completed);
        else
          this.Assign(function, [statement.Node("target")!], value);

        break;
      case "AugAssign":
        var target = statement.Node("target")!;
        var current = ExpressionTypes.Of(target, scope);
        this.Target(function, target, ExpressionTypes.Binary(statement.Operator()!, current, ExpressionTypes.Of(statement.Node("value")!, scope), statement.Node("value")!));
        break;
      case "For":
        this.Iteration(function, statement.Node("target")!, statement.Node("iter")!);
        break;
      case "Match":
        var subject = ExpressionTypes.Of(statement.Node("subject")!, scope);
        foreach (var matchCase in statement.Nodes("cases"))
          foreach (var capture in ScopeResolver.Captures(matchCase.Node("pattern")!))
            this.Store(function, function.Find(capture.Text("name")!)!, subject, capture);

        break;
      case "Return":
        var returned = statement.Node("value");
        if (function.IsGenerator)
          break;

        if (returned is null)
          this.Result(function, PyType.None, statement);
        else
          this.Result(function, ExpressionTypes.Of(returned, scope), statement);

        break;
      case "Expr" when statement.Node("value") is { Kind: "Yield" } yielded:
        function.IsGenerator = true;
        this.Yielded(function, yielded.Node("value") is { } produced ? ExpressionTypes.Of(produced, scope) : PyType.None, statement);
        break;
      case "Expr" when statement.Node("value") is { Kind: "YieldFrom" } delegated:
        function.IsGenerator = true;
        if (ExpressionTypes.Element(ExpressionTypes.Of(delegated.Node("value")!, scope)) is { } inner)
          this.Yielded(function, inner, statement);

        break;
      case "Try":
        foreach (var handler in statement.Nodes("handlers").Where(handler => handler.Text("name") is not null))
          this.Store(function, function.Find(handler.Text("name")!)!, PyType.Object(this.Handled(handler)), handler);

        break;
    }

    foreach (var field in new[] { "body", "orelse", "finalbody" })
      foreach (var inner in statement.Nodes(field))
        this.Statement(function, inner);

    foreach (var handler in statement.Nodes("handlers"))
      foreach (var inner in handler.Nodes("body"))
        this.Statement(function, inner);

    foreach (var matchCase in statement.Nodes("cases"))
      foreach (var inner in matchCase.Nodes("body"))
        this.Statement(function, inner);
  }

  /// <summary>The class of the exceptions an <c>except</c> clause catches: its class, the nearest common base of a tuple of them, <c>Exception</c> for a bare one.</summary>
  public ClassModel Handled(PyNode handler) {
    var type = handler.Node("type");
    if (type is null)
      return Classes.Builtin("Exception");

    var named = (type.Is("Tuple") ? type.Nodes("elts") : [type]).Select(name => Classes.Of(this._module, name.Text("id")!)!).ToList();
    var common = named[0];
    while (!named.All(candidate => candidate.DerivesFrom(common)))
      common = common.Base ?? Classes.Builtin("Exception");

    return common;
  }

  /// <summary>The loop variables of a <c>for</c> or a comprehension: the elements of what it iterates.</summary>
  private void Iteration(FunctionModel? function, PyNode target, PyNode iterable) {
    var scope = this.ScopeOf(function);
    var type = this.Elements(iterable, scope);
    if (type.Kind == PyKind.Unknown && ExpressionTypes.Of(iterable, scope) is { IsIncomplete: false } whole && this._conflicts.Add($"iteration {iterable.Line},{iterable.Column}")) {
      diagnostics.Add(this.File.At(iterable, PythonFrontend.UnsupportedIterationDescriptor, $"iteration over '{this.File.Quote(iterable)}' of the type {whole} is not supported"));
      return;
    }

    if (target.Is("Tuple") || target.Is("List")) {
      var names = target.Nodes("elts");
      if (type.Kind == PyKind.Tuple && type.Items.Count == names.Count)
        foreach (var (name, item) in names.Zip(type.Items))
          this.Bind(function, name, item);
      else if (!type.IsIncomplete && this._conflicts.Add($"unpacking {target.Line},{target.Column}"))
        diagnostics.Add(this.File.At(target, PythonFrontend.UnsupportedStatementDescriptor,
          $"statement 'unpacking a value of the type {type} into {names.Count} names, which raises TypeError or ValueError,' is not supported by the Python frontend"));

      return;
    }

    this.Bind(function, target, type);
  }

  /// <summary>The type of the elements a <c>for</c> takes from what it iterates: also the members of an enum.</summary>
  public static PyType ElementsOf(PyNode iterable, Scope scope) {
    if (iterable.Is("Name") && scope.Resolve(iterable) == Binding.Class && scope.Class(iterable) is { IsEnum: true } enumeration)
      return PyType.Enum(enumeration);

    return ExpressionTypes.Element(ExpressionTypes.Of(iterable, scope)) ?? PyType.Unknown;
  }

  private PyType Elements(PyNode iterable, Scope scope) => ElementsOf(iterable, scope);

  private void Bind(FunctionModel? function, PyNode name, PyType type) {
    var scope = this.ScopeOf(function);
    if (scope.Variable(name) is { } variable)
      this.Store(function, variable, type, name);
  }

  private void Assign(FunctionModel function, IReadOnlyList<PyNode> targets, PyNode value) {
    var scope = this.ScopeOf(function);
    var assigned = ExpressionTypes.Of(value, scope);
    foreach (var target in targets) {
      if (target.Is("Tuple") || target.Is("List")) {
        var names = target.Nodes("elts");
        var items = value.Kind is "Tuple" or "List" && value.Nodes("elts").Count == names.Count
          ? value.Nodes("elts").Select(element => ExpressionTypes.Of(element, scope)).ToList()
          : assigned.Kind == PyKind.Tuple && assigned.Items.Count == names.Count ? [.. assigned.Items] : null;
        if (items is not null)
          foreach (var (name, item) in names.Zip(items))
            this.Target(function, name, item);

        continue;
      }

      this.Target(function, target, assigned);
    }
  }

  /// <summary>A value stored in a target: a variable, an attribute of an object, or an element of a list.</summary>
  private void Target(FunctionModel function, PyNode target, PyType type) {
    var scope = this.ScopeOf(function);
    switch (target.Kind) {
      case "Name":
        if (scope.Variable(target) is { } variable)
          this.Store(function, variable, type, target);
        else if (scope.Resolve(target) == Binding.Global)
          this.Global(target.Text("id")!, type, target);

        break;
      case "Attribute":
        this.Field(target, type, scope);
        break;
      case "Subscript" when !target.Node("slice")!.Is("Slice"):
        // a[i] = v: a list takes the type of its elements from what is stored, a dict the types of its keys and values.
        if (ExpressionTypes.Of(target.Node("value")!, scope) is { Kind: PyKind.Dict, IsIncomplete: true } dict
            && PyTypes.Join(dict, PyType.Dict(ExpressionTypes.Of(target.Node("slice")!, scope), type)) is { } completed)
          this.Target(function, target.Node("value")!, completed);
        else
          this.Container(target.Node("value")!, type, scope, function);

        break;
    }
  }

  private void Global(string name, PyType type, PyNode at) {
    var global = this._module.GlobalVariables[name];
    this.Merge($"{this._module.Name}.{name}", global.Type, type, at, joined => global.Type = joined, () => { });
  }

  private void Field(PyNode target, PyType type, Scope scope) {
    var owner = ExpressionTypes.Of(target.Node("value")!, scope);
    if (owner.Kind == PyKind.Optional)
      owner = owner.Element!;

    if (owner.Kind == PyKind.Object && owner.Class!.Kind == ClassKind.Plain && owner.Class.Field(target.Text("attr")!) is { } field)
      this.Merge($"{owner.Class.Name}.{field.Name}", field.Type, type, target, joined => field.Type = joined, () => { });
  }

  /// <summary>A container that has an element of a type stored, appended or added: a list or set variable or attribute takes the element type.</summary>
  private void Container(PyNode container, PyType element, Scope scope, FunctionModel function) {
    if (element.Kind == PyKind.Unknown)
      return;

    var current = ExpressionTypes.Of(container, scope);
    if (current.Kind is not (PyKind.List or PyKind.Set) || !current.Element!.IsIncomplete)
      return;

    this.Target(function, container, current.Kind == PyKind.List ? PyType.List(element) : PyType.Set(element));
  }

  /// <summary>
  /// The types an expression passes on: the arguments of calls to the parameters of the import's
  /// functions, the elements appended to lists, the elements of the iterables of comprehensions and
  /// lambdas to their variables, and the targets of <c>:=</c>.
  /// </summary>
  private void Expression(FunctionModel? function, PyNode expression) {
    var scope = this.ScopeOf(function);
    foreach (var node in ScopeResolver.Walk(expression, true)) {
      switch (node.Kind) {
        case "Call":
          this.Call(function, node, scope);
          break;
        case "NamedExpr" when function is not null:
          this.Target(function, node.Node("target")!, ExpressionTypes.Of(node.Node("value")!, scope));
          break;
        case "ListComp" or "GeneratorExp" or "DictComp" or "SetComp":
          foreach (var generator in node.Nodes("generators"))
            this.Iteration(function, generator.Node("target")!, generator.Node("iter")!);

          break;
        case "Attribute":
          this.Attribute(node, scope);
          break;
      }
    }
  }

  /// <summary>An attribute read of an object or a class that has no such attribute: refused where it stands, before its unknown type would hide why.</summary>
  private void Attribute(PyNode attribute, Scope scope) {
    var owner = attribute.Node("value")!;
    var name = attribute.Text("attr")!;
    string? problem = null;
    var descriptor = PythonFrontend.UnsupportedClassDescriptor;
    if (owner.Is("Name") && scope.Resolve(owner) == Binding.Class && scope.Class(owner) is { } type) {
      if (!(type.IsEnum && type.Members.Any(member => member.Name == name)) && type.Method(name) is not { IsStatic: true })
        problem = $"'{this.File.Quote(attribute)}, a class attribute,' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #481";
    } else if (ExpressionTypes.Of(owner, scope) is var value && (value.Kind == PyKind.Optional ? value.Element! : value) is var target) {
      if (target.Kind == PyKind.Enum && name is not ("value" or "name"))
        problem = $"'{name} of a member of an enum' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #481";

      if (target.Kind == PyKind.Object && target.Class!.Field(name) is null && target.Class.Method(name) is null && !(target.Class.IsException && name == "args")) {
        descriptor = PythonFrontend.DynamicAttributeDescriptor;
        problem = $"attribute '{name}' of class '{target.Class.Name}' is created dynamically; only attributes assigned in __init__ or declared as dataclass fields are imported";
      }
    }

    if (problem is not null && this._conflicts.Add($"attribute {attribute.Line},{attribute.Column}"))
      diagnostics.Add(this.File.At(attribute, descriptor, problem));
  }

  private void Call(FunctionModel? function, PyNode call, Scope scope) {
    var callee = call.Node("func")!;
    var arguments = call.Nodes("args");
    var keywords = call.Nodes("keywords");
    if (callee.Is("Attribute") && function is not null) {
      var method = callee.Text("attr")!;
      var receiver = ExpressionTypes.Of(callee.Node("value")!, scope);
      if (receiver.Kind == PyKind.List && method == "append" && arguments.Count == 1)
        this.Container(callee.Node("value")!, ExpressionTypes.Of(arguments[0], scope), scope, function);
      else if (receiver.Kind == PyKind.Set && method == "add" && arguments.Count == 1)
        this.Container(callee.Node("value")!, ExpressionTypes.Of(arguments[0], scope), scope, function);
      else if (receiver.Kind == PyKind.List && method == "extend" && arguments.Count == 1 && ExpressionTypes.Element(ExpressionTypes.Of(arguments[0], scope)) is { } extended)
        this.Container(callee.Node("value")!, extended, scope, function);
    }

    // Lambdas and functions passed to the built-ins take the elements of the sequence they are applied to.
    if (callee.Is("Name") && scope.Resolve(callee) == Binding.BuiltIn) {
      var name = callee.Text("id")!;
      PyNode? applied = name switch {
        "map" or "filter" when arguments.Count == 2 => arguments[0],
        "sorted" or "min" or "max" => keywords.FirstOrDefault(keyword => keyword.Text("arg") == "key")?.Node("value"),
        _ => null
      };
      if (applied is not null && !applied.Is("Lambda") && !applied.Is("Name"))
        applied = null;

      var source = name is "map" or "filter" ? arguments.ElementAtOrDefault(1) : arguments.ElementAtOrDefault(0);
      if (applied is not null && source is not null) {
        var element = name is "min" or "max" && arguments.Count > 1
          ? ExpressionTypes.Joined(arguments.Select(argument => ExpressionTypes.Of(argument, scope)))
          : ExpressionTypes.Element(ExpressionTypes.Of(source, scope)) ?? PyType.Unknown;
        if (applied.Is("Lambda"))
          foreach (var parameter in applied.Node("args")!.Nodes("args"))
            this.Store(function, bound[parameter], element, parameter);
        else if (scope.FunctionNamed(applied) is { } named && named.Arguments.FirstOrDefault() is { Hint: null } first)
          this.Store(named, first, element, applied);
      }
    }

    var target = CallTarget(call, scope);
    if (target is null)
      return;

    var (called, calledModule) = target.Value;
    var parameters = called.Arguments.ToList();
    for (var index = 0; index < arguments.Count && index < parameters.Count; ++index)
      if (parameters[index].Hint is null)
        this.Store(called, parameters[index], ExpressionTypes.Of(arguments[index], scope), arguments[index]);

    foreach (var keyword in keywords)
      if (parameters.FirstOrDefault(parameter => parameter.Name == keyword.Text("arg")) is { Hint: null } parameter)
        this.Store(called, parameter, ExpressionTypes.Of(keyword.Node("value")!, scope), keyword);

    _ = calledModule;
  }

  /// <summary>The function of the import a call calls, with its module: a function, a nested one, a method, a constructor's <c>__init__</c>.</summary>
  public static (FunctionModel Function, ModuleModel Module)? CallTarget(PyNode call, Scope scope) {
    var callee = call.Node("func")!;
    if (callee.Is("Name")) {
      if (scope.FunctionNamed(callee) is { } function)
        return (function, scope.Module);

      if (scope.Resolve(callee) is Binding.Class or Binding.Import && scope.Class(callee)?.Method("__init__") is { } init)
        return (init, scope.Module);

      return null;
    }

    if (!callee.Is("Attribute"))
      return null;

    var owner = callee.Node("value")!;
    var name = callee.Text("attr")!;
    if (scope.UserModule(owner) is { } user)
      return user.Functions.TryGetValue(name, out var imported) ? (imported, user)
        : user.Classes.TryGetValue(name, out var type) && type.Method("__init__") is { } constructor ? (constructor, user) : null;

    if (owner.Is("Name") && scope.Resolve(owner) == Binding.Class)
      return scope.Class(owner)?.Method(name) is { IsStatic: true } method ? (method, scope.Module) : null;

    var receiver = ExpressionTypes.Of(owner, scope);
    if (receiver.Kind == PyKind.Optional)
      receiver = receiver.Element!;

    return receiver.Kind == PyKind.Object && receiver.Class!.Method(name) is { IsProperty: false } called ? (called, scope.Module) : null;
  }

  private void Store(FunctionModel? function, Variable variable, PyType type, PyNode at)
    => this.Merge($"{function?.Qualified ?? this._module.Name}.{variable.Name}", variable.Type, type, at, joined => variable.Type = joined, () => variable.Widened = true);

  /// <summary>A value a generator yields: its element type is the join of every one, a narrower number converted where it is yielded.</summary>
  private void Yielded(FunctionModel function, PyType element, PyNode at)
    => this.Merge(function.Qualified + " result", function.Returns.Kind == PyKind.Iterator ? function.Returns.Element! : function.Returns, element, at,
      joined => function.Returns = PyType.Iterator(joined), () => function.ReturnWidened = true);

  private void Result(FunctionModel function, PyType type, PyNode at)
    => this.Merge(function.Qualified + " result", function.Returns, type, at, joined => function.Returns = joined, () => function.ReturnWidened = true);

  private void Merge(string what, PyType current, PyType type, PyNode at, System.Action<PyType> set, System.Action widened) {
    if (type.Kind == PyKind.Unknown)
      return;

    if (PyTypes.Join(current, type) is not { } joined) {
      if (this._conflicts.Add(what))
        diagnostics.Add(this.File.At(at, PythonFrontend.MixedKindsDescriptor,
          $"'{what}' holds values of the types {current} and {type}, which no type of the import holds both of"));

      return;
    }

    if (joined != type && type.IsNumber && joined.IsNumber)
      widened();

    if (joined != current) {
      set(joined);
      this._changed = true;
    }
  }

}
