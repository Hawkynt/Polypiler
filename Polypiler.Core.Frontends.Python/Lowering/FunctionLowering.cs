using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>What the lowering of the module found that the import has to announce as a known difference.</summary>
internal sealed class LoweringNotes {

  /// <summary>A narrower number is stored where a wider one is held, and converted.</summary>
  public bool Widens { get; set; }

  /// <summary>An <c>int</c> is raised to an <c>int</c> power whose sign is not known.</summary>
  public bool PowersBySignUnknown { get; set; }

  /// <summary>A <c>float</c> is raised to a power, which CPython can answer with a complex.</summary>
  public bool PowersFloats { get; set; }

  /// <summary>The contracts the annotations of locals state, as <c>f.x: T</c>.</summary>
  public List<string> Contracts { get; } = [];

}

/// <summary>
/// Lowers one function of the module into the IR: statements in order, every value with the static
/// type the inference gave it, every operation with the meaning Python gives it.
/// </summary>
internal sealed partial class FunctionLowering {

  private readonly SourceFile _file;
  private readonly FunctionModel _function;
  private readonly IReadOnlyDictionary<string, FunctionModel> _functions;
  private readonly PythonHelpers _helpers;
  private readonly LoweringNotes _notes;
  private readonly PythonImportOptions _options;
  private readonly Dictionary<string, IrLocal> _variables = [];
  private readonly List<IrLocal> _locals = [];
  private readonly HashSet<string> _names = [];
  private readonly Stack<LoopContext> _loops = new();

  /// <summary>The statements that have to run before the statement being lowered: temporaries its expressions needed.</summary>
  private List<IrStmt> _prefix = [];

  /// <summary>How many operations that can raise or call were built so far, and how many when the current statement began.</summary>
  private int _effects, _statementEffects;

  /// <summary>How deep the expression being lowered is inside operands that are evaluated only under a condition.</summary>
  private int _conditional;

  private sealed record LoopContext(IrLocal? Broke);

  public FunctionLowering(SourceFile file, FunctionModel function, IReadOnlyDictionary<string, FunctionModel> functions, PythonHelpers helpers, LoweringNotes notes, PythonImportOptions options) {
    this._file = file;
    this._function = function;
    this._functions = functions;
    this._helpers = helpers;
    this._notes = notes;
    this._options = options;
    foreach (var name in functions.Keys.Concat(PythonHelpers.Keys))
      this._names.Add(name);
  }

  /// <summary>The function in the IR.</summary>
  public IrFunction Lower() {
    var parameters = new List<IrLocal>();
    foreach (var parameter in this._function.Parameters) {
      var local = new IrLocal(parameter.Name, PyTypes.Ir(parameter.Type));
      this._variables[parameter.Name] = local;
      this._names.Add(parameter.Name);
      parameters.Add(local);
    }

    var declarations = new List<IrStmt>();
    foreach (var variable in this._function.Locals.Values) {
      var local = new IrLocal(variable.Name, PyTypes.Ir(variable.Type));
      this._variables[variable.Name] = local;
      this._names.Add(variable.Name);
      this._locals.Add(local);
      // Every read is definitely assigned (DefiniteAssignment); the zero only gives the declaration a value no path reads.
      declarations.Add(new IrDeclare(local, Zero(variable.Type)));
    }

    var body = this.Statements(this._function.Body);
    if (this._function.ReturnWidened)
      this._notes.Widens = true;

    return new IrFunction(this._function.Name, this._function.IsPublic, PyTypes.Ir(this._function.Returns), parameters, this._locals, Block([.. declarations, .. body]));
  }

  private static IrExpr Zero(PyType type) => type switch {
    PyType.Bool => Bool(false),
    PyType.Float => Float(0),
    _ => Big(0)
  };

  /// <summary>A new local of the function for a temporary, named after what it holds and unlike every other name.</summary>
  private IrLocal Fresh(string name, IrType type) {
    var candidate = name;
    for (var suffix = 1; !this._names.Add(candidate); ++suffix)
      candidate = name + suffix;

    var local = new IrLocal(candidate, type);
    this._locals.Add(local);
    return local;
  }

  private Refusal Refuse(PyNode node, DiagnosticDescriptor descriptor, string message) => this._file.Refuse(node, descriptor, message);

  private List<IrStmt> Statements(IReadOnlyList<PyNode> statements) {
    var result = new List<IrStmt>();
    foreach (var statement in statements) {
      var saved = (this._prefix, this._statementEffects);
      this._prefix = [];
      this._statementEffects = this._effects;
      var lowered = this.Statement(statement);
      result.AddRange(this._prefix);
      result.AddRange(lowered);
      (this._prefix, this._statementEffects) = saved;
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
        if (this._options.RecordAnnotations)
          this._notes.Contracts.Add($"{this._function.Name}.{target.Text("id")}: {this._file.Quote(statement.Node("annotation")!)}");

        return statement.Node("value") is { } value ? [this.Store(target, this.Value(value))] : [];
      case "AugAssign":
        var augmented = statement.Node("target")!;
        var variable = this._function.Find(augmented.Text("id")!)!;
        var result = this.Operate(statement, statement.Operator()!, (Ref(this._variables[variable.Name]), variable.Type), this.Value(statement.Node("value")!));
        return [this.Store(augmented, result)];
      case "Return":
        return [this.Return(statement)];
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
      default:
        // Pass; SubsetCheck refused everything else.
        return [];
    }
  }

  private IEnumerable<IrStmt> ExpressionStatement(PyNode expression) {
    // A docstring or another constant does nothing.
    if (expression.Is("Constant"))
      return [];

    var (value, _) = this.Lower(expression);
    return [new IrExprStmt(value)];
  }

  private IrStmt Return(PyNode statement) {
    var value = statement.Node("value");
    if (value is null || (value.Is("Constant") && value.Text("kind") == "none"))
      return new IrReturn(null);

    return new IrReturn(this.Coerce(this.Value(value), this._function.Returns));
  }

  /// <summary>An assignment of a value to a name, converted to the type of the variable.</summary>
  private IrStmt Store(PyNode target, (IrExpr Value, PyType Type) value) {
    var variable = this._function.Find(target.Text("id")!)!;
    return Assign(this._variables[variable.Name], this.Coerce(value, variable.Type));
  }

  private IEnumerable<IrStmt> Assignment(PyNode statement) {
    var targets = statement.Nodes("targets");
    var value = statement.Node("value")!;
    if (targets is [{ Kind: "Tuple" or "List" } tuple])
      return value.Is("Call") ? this.DivMod(tuple.Nodes("elts"), value) : this.Unpack(tuple.Nodes("elts"), value.Nodes("elts"));

    // a = b = e: e once, then the names from left to right, each later one from the one before.
    var lowered = this.Value(value);
    var result = new List<IrStmt>();
    foreach (var target in targets) {
      result.Add(this.Store(target, lowered));
      var variable = this._function.Find(target.Text("id")!)!;
      lowered = (Ref(this._variables[variable.Name]), variable.Type);
    }

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
      var value = this.Value(values[index]);
      var earlier = names.Take(index).Select(name => name.Text("id")).ToHashSet();
      var stable = value.Value is IrConst or IrBigConst || (value.Value is IrLocalRef local && !earlier.Contains(local.Local.Name));
      if (!stable) {
        var temporary = this.Fresh(names[index].Text("id")!, value.Value.Type);
        result.Add(new IrDeclare(temporary, value.Value));
        value = (Ref(temporary), value.Type);
      }

      lowered.Add(value);
    }

    result.AddRange(names.Zip(lowered).Select(pair => this.Store(pair.First, pair.Second)));
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
    result.Add(this.Store(names[0], (Ref(q), quotient.Type)));
    result.Add(this.Store(names[1], (Ref(r), remainder.Type)));
    return result;
  }

  private IEnumerable<IrStmt> Assert(PyNode statement) {
    if (this._options.StripAssertions)
      return [];

    var test = this.Truth(this.Value(statement.Node("test")!));
    var message = statement.Node("msg")?.Text("value") ?? "";
    return [If(Not(test), Raise(IrExceptionClass.InvalidOperationException, message))];
  }

  /// <summary>Whether a loop's body leaves the loop with <c>break</c>, not counting the loops inside it.</summary>
  private static bool Breaks(IReadOnlyList<PyNode> statements)
    => statements.Any(statement => statement.Kind switch {
      "Break" => true,
      "If" => Breaks(statement.Nodes("body")) || Breaks(statement.Nodes("orelse")),
      "Match" => statement.Nodes("cases").Any(matchCase => Breaks(matchCase.Nodes("body"))),
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
