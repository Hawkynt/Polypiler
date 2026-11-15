using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;
using Polypiler.Core.Frontends.Python.Typing;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>
/// Lowers one function of the module into the IR: statements in order, every value with the static
/// type the inference gave it, every operation with the meaning Python gives it. A function nested in
/// another is lowered by a lowering of its own inside the one of the function it stands in, as a
/// lambda that captures the variables it shares with it: one storage location for both, as Python's
/// cells are.
/// </summary>
internal sealed partial class FunctionLowering {

  private readonly LoweringContext _context;
  private readonly ModuleModel _module;
  private readonly FunctionModel? _function;
  private readonly FunctionLowering? _parent;
  private readonly Dictionary<Variable, IrLocal> _variables = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<FunctionModel, IrLocal> _nested = new(ReferenceEqualityComparer.Instance);
  private List<IrLocal> _locals = [];
  private readonly HashSet<string> _names;
  private readonly Stack<LoopContext> _loops = new();

  /// <summary>The statements that have to run before the statement being lowered: temporaries its expressions needed.</summary>
  private List<IrStmt> _prefix = [];

  /// <summary>
  /// How many operations that can raise or call were built so far, how many when the current statement
  /// began, and how many when the last value was held in a temporary the statement declares first.
  /// </summary>
  private int _effects, _statementEffects, _heldEffects;

  /// <summary>How deep the expression being lowered is inside operands that are evaluated only under a condition.</summary>
  private int _conditional;

  private sealed record LoopContext(IrLocal? Broke);

  /// <summary>Prepares the lowering of a function, or of the module-level expressions where <paramref name="function"/> is <see langword="null"/>.</summary>
  public FunctionLowering(LoweringContext context, ModuleModel module, FunctionModel? function, FunctionLowering? parent = null) {
    this._context = context;
    this._module = module;
    this._function = function;
    this._parent = parent;
    this._names = parent?._names ?? [.. module.Functions.Keys, .. PythonHelpers.Keys, .. module.Classes.Keys];
    this.Scope = new Scope(module, function, context.Bound);
  }

  /// <summary>Where the expressions being lowered stand.</summary>
  private Scope Scope { get; }

  private PythonHelpers Helpers => this._context.Helpers;

  private LoweringNotes Notes => this._context.Notes;

  private SourceFile File => this._module.File;

  private FunctionModel Function => this._function!;

  /// <summary>The function in the IR.</summary>
  public IrFunction Lower() {
    var (parameters, body) = this.Body();
    return new IrFunction(this.Function.IrName, this._module.Exports(this.Function), this.ResultType(), parameters, this._locals, body);
  }

  /// <summary>The IR type of the function's result: the sequence of a generator, nothing for <c>None</c>.</summary>
  private IrType ResultType() => PyTypes.Ir(this.Function.Returns);

  /// <summary>The parameters and the body of the function.</summary>
  private (List<IrLocal> Parameters, IrBlock Body) Body() {
    var parameters = new List<IrLocal>();
    foreach (var parameter in this.Function.Parameters) {
      var local = new IrLocal(this.Unique(parameter.Name), PyTypes.Ir(parameter.Type));
      this._variables[parameter] = local;
      parameters.Add(local);
    }

    var declarations = new List<IrStmt>();
    foreach (var variable in this.Function.Locals.Values) {
      if (variable.Type.Kind == PyKind.None)
        throw this.Unsupported(variable.Node, $"the variable {variable.Name}, which only ever holds None,");

      var local = this.Fresh(variable.Name, PyTypes.Ir(variable.Type));
      this._variables[variable] = local;
      // Every read is definitely assigned (DefiniteAssignment); the zero only gives the declaration a value no path reads.
      declarations.Add(new IrDeclare(local, this.Zero(variable.Type)));
    }

    foreach (var nested in this.Function.Nested.Values) {
      var type = this.Signature(nested);
      var local = this.Fresh(nested.Name, type);
      this._nested[nested] = local;
      declarations.Add(new IrDeclare(local, new IrNull(type)));
    }

    var body = this.Statements(this.Function.Body);
    if (this.Function.ReturnWidened)
      this.Notes.Widens = true;

    if (this.Function.EndReachable && this.Function.Returns.Kind == PyKind.Optional)
      body.Add(new IrReturn(this.None(this.Function.Returns)));

    return (parameters, Block([.. declarations, .. body]));
  }

  /// <summary>The function type of the value a nested function is.</summary>
  private IrType Signature(FunctionModel nested)
    => IrType.Function(new IrFunctionType([.. nested.Parameters.Select(parameter => PyTypes.Ir(parameter.Type))], PyTypes.Ir(nested.Returns)));

  /// <summary>A value of a type no path reads: zero, false, the empty string, none, or no reference.</summary>
  private IrExpr? Zero(PyType type) => type.Kind switch {
    PyKind.Bool => Bool(false),
    PyKind.Float => Float(0),
    PyKind.Int => Big(0),
    PyKind.Str => new IrStrConst(""),
    PyKind.Enum => new IrConst(PyTypes.Ir(type), type.Class!.Members[0].Value),
    PyKind.Optional => this.None(type),
    PyKind.Object or PyKind.Bytes or PyKind.ByteArray or PyKind.Table => new IrNull(PyTypes.Ir(type)),
    PyKind.Tuple => new IrStructDefault(PyTypes.Ir(type)),
    PyKind.List => new IrCollection(IrCollectionOp.ListNew, PyTypes.Ir(type), []),
    _ => null
  };

  /// <summary><c>None</c> as a value of an optional type.</summary>
  private IrExpr None(PyType optional)
    => PyTypes.IsReference(optional.Element!) ? new IrNull(PyTypes.Ir(optional)) : new IrNone(PyTypes.Ir(optional));

  /// <summary>A name for a local unlike every other name of the function and of the functions it stands in.</summary>
  private string Unique(string name) {
    var candidate = name;
    for (var suffix = 1; !this._names.Add(candidate); ++suffix)
      candidate = name + suffix;

    return candidate;
  }

  /// <summary>A new local of the function for a temporary, named after what it holds and unlike every other name.</summary>
  private IrLocal Fresh(string name, IrType type) {
    var local = new IrLocal(this.Unique(name), type);
    this._locals.Add(local);
    return local;
  }

  /// <summary>The IR local of a variable: of this function, of a comprehension or lambda in it, or of a function it stands in.</summary>
  private IrLocal Local(Variable variable) {
    for (var lowering = this; lowering is not null; lowering = lowering._parent)
      if (lowering._variables.TryGetValue(variable, out var local))
        return local;

    // A variable of a comprehension or a lambda gets its local where it is first met.
    var made = this.Fresh(variable.Name, PyTypes.Ir(variable.Type));
    this._variables[variable] = made;
    return made;
  }

  /// <summary>The IR local that holds the value of a nested function.</summary>
  private IrLocal NestedLocal(FunctionModel nested) {
    for (var lowering = this; lowering is not null; lowering = lowering._parent)
      if (lowering._nested.TryGetValue(nested, out var local))
        return local;

    throw new System.InvalidOperationException($"'{nested.Qualified}' is not in scope.");
  }

  private Refusal Refuse(PyNode node, DiagnosticDescriptor descriptor, string message) => this.File.Refuse(node, descriptor, message);

  private Refusal Unsupported(PyNode node, string what) => this.Refuse(node, PythonFrontend.UnsupportedDescriptor, $"'{what}' is not supported by the Python frontend");

  private string Quote(PyNode node) => this.File.Quote(node);

  private PyType TypeOf(PyNode expression) => ExpressionTypes.Of(expression, this.Scope);

  private List<IrStmt> Statements(IReadOnlyList<PyNode> statements) {
    var result = new List<IrStmt>();
    foreach (var statement in statements) {
      var saved = (this._prefix, this._statementEffects, this._heldEffects);
      this._prefix = [];
      this._statementEffects = this._effects;
      this._heldEffects = this._effects;
      var lowered = this.Statement(statement).ToList();
      result.AddRange(this._prefix);
      result.AddRange(lowered);
      (this._prefix, this._statementEffects, this._heldEffects) = saved;
    }

    return result;
  }

  private IEnumerable<IrStmt> Statement(PyNode statement) {
    switch (statement.Kind) {
      case "Expr":
        return this.ExpressionStatement(statement.Node("value")!);
      case "Assign":
        return this.Assignment(statement);
      case "AnnAssign":
        var target = statement.Node("target")!;
        if (this._context.Options.RecordAnnotations && target.Is("Name"))
          this.Notes.Contracts.Add($"{this.Function.Qualified}.{target.Text("id")}: {this.Quote(statement.Node("annotation")!)}");

        return statement.Node("value") is { } value ? this.Store(target, this.Lower(value, this.TargetType(target))) : [];
      case "AugAssign":
        return this.Augmented(statement);
      case "Return":
        return [this.ReturnStatement(statement)];
      case "If":
        var test = this.Truth(this.Value(statement.Node("test")!));
        var then = this.Statements(statement.Nodes("body"));
        var otherwise = statement.Nodes("orelse");
        return [new IrIf(test, new(then), otherwise.Count == 0 ? null : new(this.Statements(otherwise)))];
      case "While":
        return this.While(statement);
      case "For":
        return this.For(statement);
      case "Match":
        return this.Match(statement);
      case "Assert":
        return this.Assert(statement);
      case "Break":
        return this._loops.Peek().Broke is { } broke ? [Assign(broke, Bool(true)), new IrBreak()] : [new IrBreak()];
      case "Continue":
        return [new IrContinue()];
      case "Raise":
        return this.RaiseStatement(statement);
      case "Try":
        return this.Try(statement);
      case "FunctionDef":
        return this.NestedDefinition(statement);
      default:
        // Pass, global and nonlocal; SubsetCheck refused everything else.
        return [];
    }
  }

  private IEnumerable<IrStmt> ExpressionStatement(PyNode expression) {
    switch (expression.Kind) {
      // A docstring or another constant does nothing.
      case "Constant":
        return [];
      case "Yield":
        var element = this.Function.Returns.Element!;
        return [new IrYield(expression.Node("value") is { } produced ? this.Coerce(this.Lower(produced, element), element, produced) : this.Zero(element)!)];
      case "YieldFrom":
        return [new IrYieldFrom(this.Iterable(expression.Node("value")!).Value)];
    }

    var (value, _) = this.Lower(expression);
    return [new IrExprStmt(value)];
  }

  private IrStmt ReturnStatement(PyNode statement) {
    var value = statement.Node("value");
    if (this.Function.IsGenerator)
      return new IrYieldBreak();

    if (value is null || (value.Is("Constant") && value.Text("kind") == "none"))
      return new IrReturn(this.Function.Returns.Kind == PyKind.Optional ? this.None(this.Function.Returns) : null);

    var returned = this.Lower(value, this.Function.Returns);
    if (returned.Type.Kind != PyKind.None)
      return new IrReturn(this.Coerce(returned, this.Function.Returns, value));

    // The None of a call that returns nothing: the call runs, then None is returned.
    IrStmt result = new IrReturn(this.Function.Returns.Kind == PyKind.Optional ? this.None(this.Function.Returns) : null);
    return returned.Value is IrConst ? result : Block(new IrExprStmt(returned.Value), result);
  }

  /// <summary>The type a target holds, for the value assigned to it: a variable's, an attribute's, an element's.</summary>
  private PyType TargetType(PyNode target) => target.Kind switch {
    "Name" when this.Scope.Variable(target) is { } variable => variable.Type,
    "Name" when this.Scope.Resolve(target) == Binding.Global => this._module.GlobalVariables[target.Text("id")!].Type,
    _ => this.TypeOf(target)
  };

  /// <summary>An assignment of a value to a target: a name, an attribute of an object, or an element of a sequence.</summary>
  private List<IrStmt> Store(PyNode target, (IrExpr Value, PyType Type) value) {
    // The None of a call that returns nothing: the call runs, then None is stored.
    if (value.Type.Kind == PyKind.None && value.Value is not IrConst) {
      var none = (new IrConst(IrType.Void, 0), PyType.None);
      return [new IrExprStmt(value.Value), .. this.Store(target, none)];
    }

    switch (target.Kind) {
      case "Name" when this.Scope.Variable(target) is { } variable:
        return [Assign(this.Local(variable), this.Coerce(value, variable.Type, target))];
      case "Name":
        var global = this._module.GlobalVariables[target.Text("id")!];
        return [new IrGlobalAssign(global.Ir!, Fit(this.Coerce(value, global.Type, target), global.Ir!.Type))];
      case "Attribute":
        return this.StoreAttribute(target, value);
      default:
        return this.StoreElement(target, value);
    }
  }

  private IEnumerable<IrStmt> Assignment(PyNode statement) {
    var targets = statement.Nodes("targets");
    var value = statement.Node("value")!;
    if (targets is [{ Kind: "Tuple" or "List" } tuple]) {
      if (value.Kind is "Tuple" or "List" && value.Nodes("elts").Count == tuple.Nodes("elts").Count)
        return this.Unpack(tuple.Nodes("elts"), value.Nodes("elts"));

      return value.Is("Call") && value.Node("func") is { Kind: "Name" } callee && callee.Text("id") == "divmod" && this.Scope.Resolve(callee) == Binding.BuiltIn
        ? this.DivMod(tuple.Nodes("elts"), value)
        : this.UnpackValue(tuple, value);
    }

    // a = b = e: e once, then the targets from left to right.
    var lowered = this.Lower(value, this.TargetType(targets[0]));
    var result = new List<IrStmt>();
    if (targets.Count > 1)
      lowered = this.Once(lowered, result);

    foreach (var target in targets)
      result.AddRange(this.Store(target, lowered));

    return result;
  }

  /// <summary>
  /// <c>a, b = e1, e2</c>: every value is evaluated, left to right, before the first name is assigned.
  /// A value is used where it is assigned only when that cannot tell: a constant, or a variable no
  /// name before it is assigned.
  /// </summary>
  private IEnumerable<IrStmt> Unpack(IReadOnlyList<PyNode> names, IReadOnlyList<PyNode> values) {
    var lowered = new List<(IrExpr Value, PyType Type)>();
    var result = new List<IrStmt>();
    for (var index = 0; index < values.Count; ++index) {
      var value = this.Value(values[index], this.TargetType(names[index]));
      var earlier = names.Take(index).Select(name => name.Text("id")).ToHashSet();
      var stable = value.Value is IrConst or IrBigConst || (value.Value is IrLocalRef local && !earlier.Contains(local.Local.Name));
      if (!stable) {
        var temporary = this.Fresh(names[index].Text("id") ?? "value", value.Value.Type);
        result.Add(new IrDeclare(temporary, value.Value));
        value = (Ref(temporary), value.Type);
      }

      lowered.Add(value);
    }

    foreach (var (name, value) in names.Zip(lowered))
      result.AddRange(this.Store(name, value));

    return result;
  }

  /// <summary><c>a, b = t</c> of a tuple value: the tuple evaluated once, then its items assigned in order; a length that differs is refused, as python raises.</summary>
  private IEnumerable<IrStmt> UnpackValue(PyNode targets, PyNode value) {
    var names = targets.Nodes("elts");
    var tuple = this.Value(value);
    if (tuple.Type.Kind != PyKind.Tuple || tuple.Type.Items.Count != names.Count)
      throw this.Unsupported(value, $"unpacking {this.Quote(value)} of the type {tuple.Type} into {names.Count} names");

    var result = new List<IrStmt>();
    var held = this.Fresh("unpacked", tuple.Value.Type);
    result.Add(new IrDeclare(held, tuple.Value));
    var fields = PyTypes.Ir(tuple.Type).StructType!.Fields;
    for (var index = 0; index < names.Count; ++index)
      result.AddRange(this.Store(names[index], (new IrFieldLoad(Ref(held), fields[index]), tuple.Type.Items[index])));

    return result;
  }

  /// <summary><c>q, r = divmod(a, b)</c>: both results computed before either name is assigned.</summary>
  private IEnumerable<IrStmt> DivMod(IReadOnlyList<PyNode> names, PyNode call) {
    var arguments = call.Nodes("args");
    var result = new List<IrStmt>();
    var operands = new List<(IrExpr Value, PyType Type)>();
    foreach (var argument in arguments) {
      var value = this.Value(argument);
      if (value.Value is not (IrConst or IrBigConst or IrLocalRef)) {
        var temporary = this.Fresh("operand", value.Value.Type);
        result.Add(new IrDeclare(temporary, value.Value));
        value = (Ref(temporary), value.Type);
      }

      operands.Add(value);
    }

    var quotient = this.Operate(call, "FloorDiv", operands[0], operands[1]);
    var remainder = this.Operate(call, "Mod", operands[0], operands[1]);
    var q = this.Fresh("quotient", quotient.Value.Type);
    var r = this.Fresh("remainder", remainder.Value.Type);
    result.Add(new IrDeclare(q, quotient.Value));
    result.Add(new IrDeclare(r, remainder.Value));
    result.AddRange(this.Store(names[0], (Ref(q), quotient.Type)));
    result.AddRange(this.Store(names[1], (Ref(r), remainder.Type)));
    return result;
  }

  /// <summary><c>x op= v</c>: the target's container and index evaluated once, the operation, the store.</summary>
  private IEnumerable<IrStmt> Augmented(PyNode statement) {
    var target = statement.Node("target")!;
    var op = statement.Operator()!;
    if (target.Is("Name")) {
      var current = this.Value(target);
      var result = this.Operate(statement, op, current, this.Value(statement.Node("value")!));
      return this.Store(target, result);
    }

    var setup = new List<IrStmt>();
    if (target.Is("Attribute")) {
      var owner = this.Once(this.Value(target.Node("value")!), setup);
      var read = Exactly(this.ReadAttribute(target, owner));
      var combined = this.Operate(statement, op, read, this.Value(statement.Node("value")!));
      return [.. setup, .. this.StoreAttribute(target, combined, owner)];
    }

    var container = this.Once(this.Value(target.Node("value")!), setup);
    var index = this.Once(this.Value(target.Node("slice")!), setup);
    var element = Exactly(this.Index(target, container, index));
    var updated = this.Operate(statement, op, element, this.Value(statement.Node("value")!));
    return [.. setup, .. this.StoreElement(target, updated, container, index)];
  }

  /// <summary>A value used more than once: a constant or a variable as it is, otherwise in a temporary declared first.</summary>
  private (IrExpr Value, PyType Type) Once((IrExpr Value, PyType Type) value, List<IrStmt> setup) {
    if (value.Value is IrConst or IrBigConst or IrLocalRef or IrStrConst)
      return value;

    var temporary = this.Fresh("held", value.Value.Type);
    setup.Add(new IrDeclare(temporary, value.Value));
    return (Ref(temporary), value.Type);
  }

  private IEnumerable<IrStmt> Assert(PyNode statement) {
    if (this._context.Options.StripAssertions)
      return [];

    var test = this.Truth(this.Value(statement.Node("test")!));
    var message = statement.Node("msg")?.Text("value") ?? "";
    return [If(Not(test), Raise(IrExceptionClass.AssertionException, message))];
  }

  /// <summary>Whether a loop's body leaves the loop with <c>break</c>, not counting the loops inside it.</summary>
  private static bool Breaks(IReadOnlyList<PyNode> statements)
    => statements.Any(statement => statement.Kind switch {
      "Break" => true,
      "If" => Breaks(statement.Nodes("body")) || Breaks(statement.Nodes("orelse")),
      "Match" => statement.Nodes("cases").Any(matchCase => Breaks(matchCase.Nodes("body"))),
      "Try" => Breaks(statement.Nodes("body")) || Breaks(statement.Nodes("orelse")) || Breaks(statement.Nodes("finalbody"))
        || statement.Nodes("handlers").Any(handler => Breaks(handler.Nodes("body"))),
      _ => false
    });

  /// <summary>
  /// A loop with its <c>else</c> clause, which runs when the loop ends without <c>break</c>: after the
  /// loop where nothing breaks, otherwise under a flag every <c>break</c> sets.
  /// </summary>
  private List<IrStmt> WithElse(PyNode statement, System.Func<List<IrStmt>> loop) {
    var orelse = statement.Nodes("orelse");
    var broke = orelse.Count > 0 && Breaks(statement.Nodes("body")) ? this.Fresh("broke", IrType.Bool) : null;
    this._loops.Push(new LoopContext(broke));
    var result = new List<IrStmt>();
    if (broke is not null)
      result.Add(new IrDeclare(broke, Bool(false)));

    try {
      result.AddRange(loop());
    } finally {
      this._loops.Pop();
    }

    if (orelse.Count > 0) {
      var otherwise = this.Statements(orelse);
      result.AddRange(broke is null ? otherwise : [new IrIf(Not(Ref(broke)), new(otherwise), null)]);
    }

    return result;
  }

  private IEnumerable<IrStmt> While(PyNode statement) {
    return this.WithElse(statement, () => {
      var test = this.Truth(this.Value(statement.Node("test")!));
      // A test that needed temporaries evaluates them at the top of every pass.
      var spills = this._prefix;
      this._prefix = [];
      var body = this.Statements(statement.Nodes("body"));
      return spills.Count == 0
        ? [new IrWhile(test, new(body))]
        : [new IrWhile(Bool(true), new([.. spills, If(Not(test), new IrBreak()), .. body]))];
    });
  }

}
