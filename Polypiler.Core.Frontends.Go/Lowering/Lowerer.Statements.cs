using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Frontends.Go.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Go.Lowering;

internal sealed partial class Lowerer {

  private readonly Dictionary<string, IrLocal> _locals = new(StringComparer.Ordinal);
  private readonly HashSet<string> _localNames = new(StringComparer.Ordinal);
  private readonly List<IrLocal> _allLocals = [];
  private readonly List<IrStmt> _statements = [];
  private readonly List<(string? Label, bool IsLoop)> _targets = [];
  private string? _pendingLabel;
  private IrFunction? _current;

  private void BeginFunction(IrFunction? function) {
    this._locals.Clear();
    this._localNames.Clear();
    this._allLocals.Clear();
    this._statements.Clear();
    this._targets.Clear();
    this._pendingLabel = null;
    this._current = function;
  }

  /// <summary>A local of the function with a name no other of its locals has: Go lets an inner scope reuse a name, the IR does not.</summary>
  private IrLocal NewLocal(string name, IrType type) {
    var unique = name;
    for (var suffix = 2; !this._localNames.Add(unique); ++suffix)
      unique = name + suffix.ToString(CultureInfo.InvariantCulture);

    var local = new IrLocal(unique, type);
    this._allLocals.Add(local);
    return local;
  }

  /// <summary>The local a defining identifier introduces.</summary>
  private IrLocal Define(GoNode identifier) {
    var local = this.NewLocal(identifier.Text("Name")!, this.TypeOf(identifier));
    this._locals[identifier.Declaration] = local;
    return local;
  }

  /// <summary>Whether an identifier of an assignment defines its object there.</summary>
  private static bool Defines(GoNode identifier) => identifier.Declaration == $"{identifier.Line}:{identifier.Column}";

  /// <summary>A temporary the lowering keeps a value in, declared with it.</summary>
  private IrLocal Temporary(IrExpr value, List<IrStmt> output, string name = "tmp") {
    var local = this.NewLocal(name, value.Type);
    output.Add(new IrDeclare(local, value));
    return local;
  }

  private IrFunction Function(GoNode node) {
    var name = node.Node("Name")!.Text("Name")!;
    var signature = this._signatures[name];
    this.BeginFunction(signature);
    var parameters = new List<IrLocal>();
    var index = 0;
    foreach (var field in node.Node("Type")?.Node("Params")?.Nodes("List") ?? []) {
      var names = field.Nodes("Names");
      if (names.Count == 0) {
        parameters.Add(this.NewLocal(signature.Parameters[index++].Name, signature.Parameters[parameters.Count].Type));
        continue;
      }

      foreach (var parameter in names) {
        var local = parameter.Text("Name") == "_" ? this.NewLocal("unused", this.TypeOf(parameter)) : this.Define(parameter);
        parameters.Add(local);
        ++index;
      }
    }

    this._allLocals.Clear();
    var body = new List<IrStmt>();
    foreach (var statement in node.Node("Body")?.Nodes("List") ?? [])
      this.Statement(statement, body);

    return signature with { Parameters = parameters, Locals = [.. this._allLocals], Body = new IrBlock(body) };
  }

  private IrBlock Block(GoNode? block) {
    var output = new List<IrStmt>();
    foreach (var statement in block?.Nodes("List") ?? [])
      this.Statement(statement, output);

    return new IrBlock(output);
  }

  private void Statement(GoNode node, List<IrStmt> output) {
    switch (node.Kind) {
      case "EmptyStmt":
        return;
      case "DeclStmt":
        this.Declaration(node.Node("Decl")!, output);
        return;
      case "AssignStmt":
        this.Assignment(node, output);
        return;
      case "IncDecStmt": {
        var target = node.Node("X")!;
        var type = this.TypeOf(target);
        var one = type.IsFloat ? new IrConst(type, IrOperators.Bits(type, 1.0)) : new IrConst(type, 1);
        this.Store(target, place => this.Arithmetic(node.Text("Tok") == "++" ? "+" : "-", place, one, type, node), output);
        return;
      }
      case "ExprStmt": {
        var expression = Strip(node.Node("X")!);
        if (expression.Is("CallExpr") && this.Panic(expression) is { } thrown) {
          output.Add(thrown);
          return;
        }

        output.Add(new IrExprStmt(this.Value(expression)));
        return;
      }
      case "ReturnStmt": {
        var results = node.Nodes("Results");
        output.Add(new IrReturn(results.Count == 0 ? null : this.Value(results[0], this._current!.ReturnType)));
        return;
      }
      case "BlockStmt":
        output.Add(this.Block(node));
        return;
      case "IfStmt":
        output.Add(this.If(node));
        return;
      case "ForStmt":
        output.Add(this.For(node));
        return;
      case "RangeStmt":
        output.Add(this.Range(node));
        return;
      case "SwitchStmt":
        output.Add(this.Switch(node));
        return;
      case "BranchStmt":
        output.Add(this.Branch(node));
        return;
      case "LabeledStmt":
        this._pendingLabel = node.Node("Label")!.Text("Name");
        this.Statement(node.Node("Stmt")!, output);
        this._pendingLabel = null;
        return;
      default:
        throw Unsupported(node, node.Kind switch {
          "GoStmt" => "go statement",
          "DeferStmt" => "defer",
          "SelectStmt" => "select",
          "SendStmt" => "channel send",
          "TypeSwitchStmt" => "type switch",
          _ => node.Kind
        });
    }
  }

  private static GoNode Strip(GoNode node) => node.Is("ParenExpr") ? Strip(node.Node("X")!) : node;

  /// <summary><c>panic(message)</c> as a throw of an exception with the message, or <see langword="null"/> for any other call.</summary>
  private IrThrow? Panic(GoNode call) {
    var callee = Strip(call.Node("Fun")!);
    if (!(callee.Is("Ident") && callee.Text("Name") == "panic" && callee.Object == "builtin"))
      return null;

    var argument = call.Nodes("Args")[0];
    if (this.TryType(argument.Type) != IrType.String)
      throw Unsupported(call, $"panic of a value of type {argument.Type}");

    return new IrThrow(Exception(IrExceptionClass.Exception, this.Value(argument)));
  }

  /// <summary>A new exception of a library class with a message.</summary>
  private static IrNew Exception(IrClassType type, IrExpr message) => new(IrType.Object(type), IrExceptionClass.Constructor(type, 1), [message]);

  private void Declaration(GoNode declaration, List<IrStmt> output) {
    switch (declaration.Text("Tok")) {
      case "const":
        // A constant is its value at every use, which go/types computed.
        return;
      case "var":
        foreach (var spec in declaration.Nodes("Specs")) {
          var names = spec.Nodes("Names");
          var values = spec.Nodes("Values");
          var lowered = values.Select((value, index) => this.Value(value, this.TypeOf(names[index]))).ToList();
          if (names.Count > 1 && values.Count > 1)
            lowered = [.. lowered.Select(value => (IrExpr)new IrLocalRef(this.Temporary(value, output)))];

          for (var index = 0; index < names.Count; ++index) {
            var type = this.TypeOf(names[index]);
            var value = values.Count == 0 ? this.ZeroOf(type, names[index]) : lowered[index];
            if (names[index].Text("Name") == "_") {
              output.Add(new IrExprStmt(value));
              continue;
            }

            output.Add(new IrDeclare(this.Define(names[index]), value));
          }
        }

        return;
      default:
        throw Unsupported(declaration, $"local {declaration.Text("Tok")} declaration");
    }
  }

  /// <summary>
  /// An assignment: <c>=</c> and <c>:=</c> evaluate every value - and the operands of the places on
  /// the left - before any is assigned, so that <c>a, b = b, a+b</c> swaps; <c>op=</c> evaluates its
  /// place once.
  /// </summary>
  private void Assignment(GoNode node, List<IrStmt> output) {
    var token = node.Text("Tok")!;
    var left = node.Nodes("Lhs");
    var right = node.Nodes("Rhs");
    if (token is not "=" and not ":=") {
      var op = token[..^1];
      var target = left[0];
      var type = this.TypeOf(target);
      var value = right[0];
      this.Store(target, place => op is "<<" or ">>" ? this.Shift(op, place, value, type, node) : this.Arithmetic(op, place, this.Value(value, type), type, node), output);
      return;
    }

    if (left.Count != right.Count)
      throw Unsupported(node, "an assignment of a call with several results");

    if (left.Count == 1) {
      this.AssignOne(left[0], token, this.Value(right[0], this.TargetType(left[0], right[0])), output);
      return;
    }

    // Several new variables: no value can read one of them, which are not in scope yet.
    if (token == ":=" && left.All(target => Strip(target) is { Kind: "Ident" } name && (name.Text("Name") == "_" || Defines(name)))) {
      for (var index = 0; index < left.Count; ++index)
        this.AssignOne(left[index], token, this.Value(right[index], this.TargetType(left[index], right[index])), output);

      return;
    }

    // Several: the places' operands first, then every value, each into a temporary, then the assignments in order.
    var places = left.Select(target => this.Place(target, output)).ToList();
    var values = right.Select((value, index) => (IrExpr)new IrLocalRef(this.Temporary(this.Value(value, this.TargetType(left[index], right[index])), output))).ToList();
    for (var index = 0; index < left.Count; ++index) {
      if (places[index] is { } place)
        output.Add(place(values[index]));
      else
        this.AssignOne(left[index], token, values[index], output);
    }
  }

  /// <summary>The type the value of an assignment to a target takes; the value's own for the blank identifier.</summary>
  private IrType TargetType(GoNode target, GoNode value) => Strip(target).Text("Name") == "_" ? this.TypeOf(value) : this.TypeOf(target);

  /// <summary>
  /// An element or field target with its operands evaluated into temporaries now, as the store that
  /// writes it later; <see langword="null"/> for a variable, which has no operands.
  /// </summary>
  private Func<IrExpr, IrStmt>? Place(GoNode target, List<IrStmt> output) {
    target = Strip(target);
    switch (target.Kind) {
      case "IndexExpr": {
        var array = this.Stable(this.Value(target.Node("X")!), output);
        var index = this.Stable(this.Index(target.Node("Index")!), output);
        return value => new IrStore(array, index, value);
      }
      case "SelectorExpr" when target.Text("selection") == "field":
        return null;
      default:
        return null;
    }
  }

  /// <summary>An expression that stays the same when read again: a constant or a local as it is, anything else in a temporary.</summary>
  private IrExpr Stable(IrExpr value, List<IrStmt> output) => value is IrConst or IrLocalRef ? value : new IrLocalRef(this.Temporary(value, output));

  private void AssignOne(GoNode target, string token, IrExpr value, List<IrStmt> output) {
    target = Strip(target);
    if (target.Is("Ident")) {
      if (target.Text("Name") == "_") {
        output.Add(new IrExprStmt(value));
        return;
      }

      if (token == ":=" && Defines(target)) {
        output.Add(new IrDeclare(this.Define(target), value));
        return;
      }
    }

    this.Store(target, _ => value, output, false);
  }

  /// <summary>
  /// Writes a place with a value computed from its old one: a variable, a global, an element of a
  /// slice or a field of a place, whose operands are evaluated once.
  /// </summary>
  private void Store(GoNode target, Func<IrExpr, IrExpr> value, List<IrStmt> output, bool reads = true) {
    target = Strip(target);
    switch (target.Kind) {
      case "Ident" when this._locals.TryGetValue(target.Declaration, out var local):
        output.Add(new IrAssign(local, value(new IrLocalRef(local))));
        return;
      case "Ident" when this._globals.TryGetValue(target.Declaration, out var global):
        output.Add(new IrGlobalAssign(global, value(new IrGlobalRef(global))));
        return;
      case "IndexExpr": {
        // A place read for its new value has its operands evaluated once, in temporaries unless they are constants or locals.
        var array = reads ? this.Stable(this.Value(target.Node("X")!), output) : this.Value(target.Node("X")!);
        var index = reads ? this.Stable(this.Index(target.Node("Index")!), output) : this.Index(target.Node("Index")!);
        output.Add(new IrStore(array, index, value(new IrArrayLoad(array, index))));
        return;
      }
      case "SelectorExpr" when target.Text("selection") == "field": {
        var owner = this.Value(target.Node("X")!);
        var field = this.FieldOf(owner.Type, target);
        output.Add(new IrFieldStore(owner, field, value(new IrFieldLoad(owner, field))));
        return;
      }
      default:
        throw Unsupported(target, $"assignment to {target.Kind}");
    }
  }

  private IrStmt If(GoNode node) {
    var initialization = new List<IrStmt>();
    if (node.Node("Init") is { } init)
      this.Statement(init, initialization);

    var condition = this.Value(node.Node("Cond")!, IrType.Bool);
    var then = this.Block(node.Node("Body"));
    IrBlock? otherwise = node.Node("Else") switch {
      null => null,
      { Kind: "IfStmt" } chained => new IrBlock([this.If(chained)]),
      var block => this.Block(block)
    };
    var conditional = new IrIf(condition, then, otherwise);
    return initialization.Count == 0 ? conditional : new IrBlock([.. initialization, conditional]);
  }

  /// <summary>Lowers the body of a loop or switch with its label, which a <c>break</c> or <c>continue</c> of it may name.</summary>
  private IrBlock Target(GoNode? body, bool isLoop, IReadOnlyList<IrStmt>? prefix = null) => this.Target(body?.Nodes("List") ?? [], isLoop, prefix);

  private IrBlock Target(IReadOnlyList<GoNode> statements, bool isLoop, IReadOnlyList<IrStmt>? prefix) {
    var label = this._pendingLabel;
    this._pendingLabel = null;
    this._targets.Add((label, isLoop));
    try {
      var output = new List<IrStmt>(prefix ?? []);
      foreach (var statement in statements)
        this.Statement(statement, output);

      return new IrBlock(output);
    } finally {
      this._targets.RemoveAt(this._targets.Count - 1);
    }
  }

  /// <summary>
  /// A <c>for</c>: the init statement in a block of its own before an <c>IrFor</c>, whose loop variables
  /// are a new variable in every pass from Go 1.22; a loop without init and post is an <c>IrWhile</c>.
  /// </summary>
  private IrStmt For(GoNode node) {
    var label = this._pendingLabel;
    var initialization = new List<IrStmt>();
    this._pendingLabel = null;
    if (node.Node("Init") is { } init)
      this.Statement(init, initialization);

    var condition = node.Node("Cond") is { } test ? this.Value(test, IrType.Bool) : new IrConst(IrType.Bool, 1);
    var step = new List<IrStmt>();
    if (node.Node("Post") is { } post)
      this.Statement(post, step);

    this._pendingLabel = label;
    var body = this.Target(node.Node("Body"), true);
    if (initialization.Count == 0 && step.Count == 0)
      return new IrWhile(condition, body);

    var declared = initialization.OfType<IrDeclare>().Select(declaration => declaration.Local).ToList();
    var loop = new IrFor(condition, body, new IrBlock(step)) { PerIteration = languageMinor >= 22 ? declared : [] };
    return new IrBlock([.. initialization, loop]);
  }

  /// <summary>
  /// A <c>for … range</c> over an integer (Go 1.22) or a slice: the operand evaluated once, a hidden
  /// counter from 0, and the key and value assigned from it at the start of every pass, so that a
  /// change of them in the body does not change the passes.
  /// </summary>
  private IrStmt Range(GoNode node) {
    var label = this._pendingLabel;
    this._pendingLabel = null;
    var over = node.Node("X")!;
    var overType = this.TypeOf(over);
    var output = new List<IrStmt>();
    var key = node.Node("Key") is { } k && k.Text("Name") != "_" ? k : null;
    var value = node.Node("Value") is { } v && v.Text("Name") != "_" ? v : null;
    var define = node.Text("Tok") == ":=";
    IrExpr bound;
    IrLocal? array = null;
    IrType counterType;
    if (overType.IsInteger) {
      if (value is not null)
        throw Unsupported(node, "range over an integer with two variables");

      counterType = overType;
      bound = new IrLocalRef(this.Temporary(this.Value(over), output, "n"));
    } else if (overType.IsArray) {
      array = this.Temporary(this.Value(over), output, "items");
      counterType = IrType.I32;
      bound = new IrArrayLength(new IrLocalRef(array));
    } else {
      throw Unsupported(node, $"range over {over.Type}");
    }

    var counter = this.NewLocal("i", counterType);
    output.Add(new IrDeclare(counter, new IrConst(counterType, 0)));
    var prefix = new List<IrStmt>();
    void Bind(GoNode target, IrExpr current) {
      if (define)
        prefix.Add(new IrDeclare(this.Define(target), current));
      else
        this.Store(target, _ => current, prefix);
    }

    if (key is not null)
      Bind(key, array is null || this.TypeOf(key) == IrType.I32 ? new IrLocalRef(counter) : new IrConvert(this.TypeOf(key), new IrLocalRef(counter)));

    if (value is not null)
      Bind(value, new IrArrayLoad(new IrLocalRef(array!), new IrLocalRef(counter)));

    this._pendingLabel = label;
    var body = this.Target(node.Node("Body"), true, prefix);
    var one = new IrConst(counterType, 1);
    output.Add(new IrFor(new IrBinary(IrBinaryOp.Less, Comparable(new IrLocalRef(counter)), Comparable(bound)), body,
      new IrBlock([new IrAssign(counter, Wrapped(new IrBinary(IrBinaryOp.Add, Comparable(new IrLocalRef(counter)), Comparable(one)), counterType))])));
    return new IrBlock(output);
  }

  /// <summary>
  /// A <c>switch</c>: on an integer with constant cases an <c>IrSwitch</c>; any other a chain of
  /// <c>if</c> statements over the tag evaluated once, inside a switch of its own where a <c>break</c>
  /// leaves it. Go tests the cases from the top, <c>default</c> last wherever it stands.
  /// </summary>
  private IrStmt Switch(GoNode node) {
    var label = this._pendingLabel;
    this._pendingLabel = null;
    var output = new List<IrStmt>();
    if (node.Node("Init") is { } init)
      this.Statement(init, output);

    var tagNode = node.Node("Tag");
    var clauses = node.Node("Body")?.Nodes("List") ?? [];
    if (clauses.SelectMany(clause => clause.Nodes("Body")).Any(statement => statement.Is("BranchStmt") && statement.Text("Tok") == "fallthrough"))
      throw Unsupported(node, "fallthrough");

    var tagType = tagNode is null ? IrType.Bool : this.TypeOf(tagNode);
    if (tagNode is not null && tagType.IsInteger && clauses.SelectMany(clause => clause.Nodes("List")).All(value => value.Mode == "constant")) {
      var tag = this.Value(tagNode);
      var sections = new List<IrSwitchSection>();
      foreach (var clause in clauses) {
        var labels = clause.Nodes("List").Select(value => ((IrConst)this.Value(value, tagType)).Value).ToList();
        this._pendingLabel = label;
        sections.Add(new IrSwitchSection(labels, clause.Nodes("List").Count == 0, this.Target(clause.Nodes("Body"), false, null)));
      }

      output.Add(new IrSwitch(tag, sections));
      return output.Count == 1 ? output[0] : new IrBlock(output);
    }

    IrExpr? subject = tagNode is null ? null : new IrLocalRef(this.Temporary(this.Value(tagNode), output, "tag"));
    this._pendingLabel = label;
    this._targets.Add((label, false));
    IrStmt? chain = null;
    var bodies = clauses.Select(clause => (Clause: clause, Body: this.ClauseBody(clause))).ToList();
    this._targets.RemoveAt(this._targets.Count - 1);
    this._pendingLabel = null;
    var fallback = bodies.FirstOrDefault(entry => entry.Clause.Nodes("List").Count == 0).Body;
    foreach (var (clause, body) in bodies.Where(entry => entry.Clause.Nodes("List").Count > 0).Reverse()) {
      IrExpr? condition = null;
      foreach (var value in clause.Nodes("List").Reverse()) {
        IrExpr test = subject is null ? this.Value(value, IrType.Bool) : this.Equality(subject, this.Value(value, subject.Type), value);
        condition = condition is null ? test : new IrBinary(IrBinaryOp.LogicalOr, test, condition);
      }

      chain = new IrIf(condition!, body, chain is null ? fallback : new IrBlock([chain]));
      fallback = null;
    }

    var whole = chain ?? (IrStmt?)fallback ?? new IrBlock([]);
    if (bodies.Any(entry => Breaks(entry.Body)))
      whole = new IrSwitch(new IrConst(IrType.I32, 0), [new IrSwitchSection([], true, new IrBlock([whole]))]);

    output.Add(whole);
    return new IrBlock(output);
  }

  private IrBlock ClauseBody(GoNode clause) {
    var output = new List<IrStmt>();
    foreach (var statement in clause.Nodes("Body"))
      this.Statement(statement, output);

    return new IrBlock(output);
  }

  /// <summary>Whether a block holds a <c>break</c> of the statement it is in, outside inner loops and switches.</summary>
  private static bool Breaks(IrStmt statement) => statement switch {
    IrBreak => true,
    IrWhile or IrDoWhile or IrFor or IrSwitch => false,
    IrBlock block => block.Statements.Any(Breaks),
    _ => IrTree.Blocks(statement).Any(Breaks)
  };

  /// <summary><c>break</c> and <c>continue</c>, of the innermost statement or naming it; any other target is refused, as the IR has no jump to an outer one.</summary>
  private IrStmt Branch(GoNode node) {
    var token = node.Text("Tok");
    var label = node.Node("Label")?.Text("Name");
    switch (token) {
      case "break":
        if (label is not null && (this._targets.Count == 0 || this._targets[^1].Label != label))
          throw Unsupported(node, $"break {label} of an outer statement");

        return new IrBreak();
      case "continue":
        var loop = this._targets.FindLastIndex(target => target.IsLoop);
        if (label is not null && (loop < 0 || this._targets[loop].Label != label))
          throw Unsupported(node, $"continue {label} of an outer loop");

        return new IrContinue();
      default:
        throw Unsupported(node, token ?? "branch");
    }
  }

}
