using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Acornima.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

/// <summary>
/// Identifies the types of the variables, parameters and results of the functions of one file. A
/// number is a double; a place is an integer exactly when every value that reaches it is produced by
/// an operation whose result is that integer - the least fixed point over the assignments of the
/// functions and over the calls between them. Nothing is taken from a name or from how a value is
/// used, with the one exception the import announces: a parameter without a JSDoc type that a
/// function only ever reads under a coercion.
/// </summary>
internal sealed partial class TypeInference(string path, string source, List<Candidate> candidates, IReadOnlyDictionary<string, ModuleConstant> constants) {

  /// <summary>The shapes of the object literals of the file, which become its structs.</summary>
  public Shapes Shapes { get; } = new();

  /// <summary>The enum objects of the file by name, which every function sees wherever no local, constant or variable of the module hides them.</summary>
  public IReadOnlyDictionary<string, EnumObject> Enums { get; init; } = new Dictionary<string, EnumObject>();

  private static readonly Regex _whiteSpace = new(@"\s+", RegexOptions.CultureInvariant);

  private readonly Dictionary<Candidate, FunctionModel> _models = [];
  private readonly Dictionary<FunctionModel, Binder> _binders = [];
  private readonly Dictionary<(FunctionModel?, Node, Coercion), JsType> _known = [];
  private readonly List<(Variable Place, JsType Stated, bool IsParameter)> _statedLongs = [];

  /// <summary>Whether a stated type is a 64-bit integer, which the values decide to be a Number or a <c>BigInt</c> (#1401).</summary>
  private static bool IsStatedLong(JsType stated) => stated is { Kind: JsKind.BigInt, IsStated: true };

  /// <summary>The copy of a function each call that the import gave one calls instead of the function, by the callee of the call.</summary>
  public IReadOnlyDictionary<Node, Candidate> Redirects { get; init; } = new Dictionary<Node, Candidate>();
  private FunctionModel? _scope;

  /// <summary>Makes a function the one whose expressions are typed.</summary>
  public void Enter(FunctionModel model) => this._scope = model;

  /// <summary>The names of the function whose expressions are typed.</summary>
  public Binder Names => this._binders[this._scope!];

  /// <summary>The source text of a node on one line.</summary>
  public string Text(Node node) => _whiteSpace.Replace(source[node.Start..node.End], " ");

  /// <summary>A diagnostic at the start of a node.</summary>
  public Diagnostic Diagnose(Node node, DiagnosticDescriptor descriptor, string message) {
    var start = node.Location.Start;
    return descriptor.At(message, path, start.Line, start.Column + 1);
  }

  /// <summary>What is known about a function; its signature is read and its body bound the first time it is asked for.</summary>
  public FunctionModel ModelOf(Candidate candidate) {
    if (this._models.TryGetValue(candidate, out var known))
      return known;

    var model = new FunctionModel(candidate);
    this._models[candidate] = model;
    this._binders[model] = this.BinderOf(model);
    model.Problem = this.ReadSignature(model);
    if (model.Problem is null)
      this._binders[model].Bind();

    // A generator returns its generator object, of the values it yields; a function without a return
    // returns nothing, which is known before any type is; one that returns a value and can also end
    // without one returns undefined there.
    if (candidate.Function.Generator)
      (model.Result, model.ResultEvidence) = (JsType.SequenceOf(JsType.Unknown), candidate.Anchor);
    else if (!model.Flows.Exists(flow => flow.Kind == FlowKind.Return))
      // A body that never ends but by throwing - the method a base class leaves to its derived ones - says nothing about the result (#1365).
      model.Result = candidate.Function.Body is FunctionBody never && !Completes.MayComplete(never.Body) ? JsType.Unknown : JsType.Undefined;
    else if (model.Problem is null && candidate.Function.Body is FunctionBody block && Completes.MayComplete(block.Body))
      model.Result = JsType.UndefinedValue;

    return model;
  }

  /// <summary>Reads the parameters and what JSDoc states; returns why the function cannot be imported whatever its body is.</summary>
  private Diagnostic? ReadSignature(FunctionModel model) {
    var candidate = model.Candidate;
    var function = candidate.Function;
    Diagnostic Unsupported(Node node, string what) => this.Diagnose(node, JavaScriptFrontend.UnsupportedDescriptor, $"'{what}' is not supported yet");
    if (candidate.Problem is not null)
      return Unsupported(candidate.Anchor, candidate.Problem);

    if (this.AsyncProblem(candidate) is { } asynchronous)
      return asynchronous;

    if (function.Generator && this.GeneratorProblem(candidate) is { } generator)
      return generator;

    if (candidate.Name.Contains('$'))
      return Unsupported(candidate.Anchor, $"the name {candidate.Name}");

    foreach (var declared in function.Params) {
      // A default that is a literal is what a call that leaves the parameter out passes (#1365).
      var (parameter, defaultValue) = declared is AssignmentPattern { Left: Identifier left, Right: var right } && IsLiteral(right) ? ((Node)left, (Expression?)right) : (declared, null);
      if (parameter is not Identifier { Name: var name })
        return Unsupported(declared, declared is RestElement ? "a rest parameter" : declared is AssignmentPattern ? "a default parameter value that is no literal" : "a destructured parameter");

      if (name.Contains('$'))
        return Unsupported(parameter, $"the name {name}");

      if (model.Parameters.Exists(earlier => earlier.Name == name))
        return Unsupported(parameter, $"a second parameter named {name}");

      var (stated, type) = JsDoc.ParameterType(candidate.Documentation, name, this.Named);
      if (stated && type is null)
        return this.Diagnose(parameter, JavaScriptFrontend.MissingTypeDescriptor, $"parameter '{name}' of '{candidate.Name}' has no supported JSDoc type");

      var variable = new Variable(name, parameter, true) { Declared = type, Default = defaultValue };
      model.Parameters.Add(variable);
      model.Bindings[parameter] = variable;
    }

    model.DeclaredResult = JsDoc.ResultType(candidate.Documentation);
    model.StatedResult = JsDoc.StatedResult(candidate.Documentation, this.Named);
    return null;
  }

  /// <summary>Whether an expression is a literal: a number, a string, a truth value or <c>null</c>, negated or not.</summary>
  private static bool IsLiteral(Expression expression)
    => expression is NumericLiteral or StringLiteral or BooleanLiteral or NullLiteral or UnaryExpression { Operator: Acornima.Operator.UnaryNegation, Argument: NumericLiteral };

  /// <summary>
  /// Infers the types of the given functions and of every function they call. Afterwards the types
  /// are final and <see cref="Of"/> answers from what it has computed once.
  /// </summary>
  /// <param name="roots">The functions to import.</param>
  /// <param name="isPublic">Whether a function is visible outside the module.</param>
  /// <returns>The functions, the called ones after their callers.</returns>
  public List<FunctionModel> Solve(IEnumerable<Candidate> roots, System.Func<Candidate, bool> isPublic) {
    this.Shapes.Classes.IsTaken = this.Classes.ContainsKey;
    // A field JSDoc states the type of holds values of it, whatever else reaches it: null alone is then that reference absent.
    // A stated 64-bit integer is left to the values that reach it, and a BigInt only where none does (#1401).
    foreach (var field in this.Classes.Values.Distinct().SelectMany(owner => owner.Fields).Where(field => field.Stated is not null))
      if (JsDoc.Stated(field.Stated!, this.Named) is { } stated && stated.Ir is not null) {
        if (IsStatedLong(stated))
          this._statedLongs.Add((field.Variable, stated, false));
        else
          field.Variable.Type = JsType.Join(field.Variable.Type, stated);

        field.Variable.Reason ??= $"@type {{{field.Stated}}}";
      }

    var models = new List<FunctionModel>();
    // A function an initializer calls is imported as well.
    var pending = new Queue<Candidate>(roots.Concat(this.Initializers.Callees));
    while (pending.TryDequeue(out var candidate)) {
      var model = this.ModelOf(candidate);
      if (models.Contains(model))
        continue;

      model.IsPublic = !candidate.IsNested && isPublic(candidate);
      models.Add(model);
      foreach (var callee in model.Callees.Concat(model.Nested.Values))
        pending.Enqueue(callee);
    }

    var called = this.Flowing(models).SelectMany(model => model.Flows).Where(flow => flow.Kind == FlowKind.Argument).Select(flow => flow.Callee).ToHashSet();
    // A function no call of the file can pass anything: not called, no method, no function value, no nested function.
    var values = this.Flowing(models).SelectMany(model => model.FunctionValues.Values).ToHashSet();
    // A nested function is called through its values only: its parameters take what those calls pass.
    foreach (var model in models)
      foreach (var parameter in model.Parameters)
        this.Seed(model, parameter, !model.Candidate.IsNested && (model.IsPublic || !called.Contains(model.Candidate)),
          model.Candidate.IsNested || model.Candidate.Class is not null || called.Contains(model.Candidate) || values.Contains(model.Candidate));

    do {
      for (var changed = true; changed;) {
        changed = false;
        foreach (var model in this.Flowing(models)) {
          this.Enter(model);
          foreach (var flow in model.Flows)
            changed |= this.Apply(model, flow);
        }

        changed |= this.Unify();
        changed |= this.RecordFields();
      }
    } while (this.Promote([.. this.Flowing(models)]));

    // Nothing in the file says what array a parameter is that no call passes one to; calls that pass arrays of two kinds say two.
    foreach (var model in models)
      foreach (var parameter in model.Parameters.Where(parameter => parameter.IsArrayByCall && parameter.Entry.Kind != JsKind.Array))
        model.Problem ??= parameter.Entry.Kind == JsKind.Conflict
          ? this.Diagnose(parameter.EntryEvidence ?? parameter.Declaration, JavaScriptFrontend.MixedKindsDescriptor,
            $"'{parameter.Name}' holds both {parameter.Entry.Kinds.First} and {parameter.Entry.Kinds.Second}")
          : this.Diagnose(parameter.Declaration, JavaScriptFrontend.MissingTypeDescriptor,
            $"parameter '{parameter.Name}' of '{model.Candidate.Name}' is used as an array and needs a JSDoc type");

    return models;
  }

  /// <summary>
  /// The type a parameter starts with. JSDoc states a contract. Without it, a parameter whose callers
  /// are not all known is a double, unless the function reads it under <c>ToInt32</c> or
  /// <c>ToUint32</c> only, or re-assigns it coerced before anything else: then it is that integer.
  /// A parameter whose callers are all known starts with nothing and takes what they pass, and so
  /// does one that is used as an array: a double it cannot be, and what array it is only a call says.
  /// A stated <c>int64</c> or <c>uint64</c> that a call of the file may pass (<paramref name="passed"/>)
  /// starts with nothing as well: a Number where the calls pass Numbers, a <c>BigInt</c> where they pass
  /// BigInts or nothing (#1401); one no call of the file can pass is the <c>BigInt</c> it states.
  /// </summary>
  private void Seed(FunctionModel model, Variable parameter, bool external, bool passed) {
    // A parameter with a default holds that literal too; without JSDoc an exported one is of its kind (a number a double).
    if (parameter.Default is { } given) {
      var literal = given switch {
        NumericLiteral number => JsType.Literal(number.Value),
        UnaryExpression { Argument: NumericLiteral negated } => JsType.Literal(-negated.Value),
        StringLiteral => JsType.String,
        BooleanLiteral => JsType.Boolean,
        _ => JsType.Null
      };
      parameter.Type = parameter.Entry = parameter.Declared is { } stated ? stated : external ? (literal.Kind == JsKind.Number ? JsType.F64 : literal) : literal;
      parameter.Evidence = parameter.EntryEvidence = given;
      return;
    }

    if (parameter.Declared is { } declared) {
      parameter.Reason = $"@param {{{JsType.Spell(declared.Ir!)}}}";
      // A stated 64-bit integer that a call of the file may pass starts with nothing: those calls decide what it is (#1401).
      if (IsStatedLong(declared) && passed) {
        this._statedLongs.Add((parameter, declared, true));
        return;
      }

      // A stated T[] that the function tests with Array.isArray is an Array, unless a call of the file passes another (#1374).
      parameter.Type = parameter.Entry = declared is { IsStated: true } && parameter.TestedAsArray is not null ? declared.ShapedAs(JsType.ArrayOf(declared.Element!, true))! : declared;
      // An integer the function only coerces to 32 bits sees the low 32 bits of any integer. A 32-bit one takes the other
      // 32-bit integer wrapped; a narrower one of a function only the file calls is widened to the 32 bits the coercion sees,
      // while one called from outside keeps the contract it states.
      if (declared is { IsInteger: true, IsAbsent: false, Enum: null } && declared.Ir is { IsInteger: true, IsLong: false } stated
          && (parameter.Prologue is { Coercion: Coercion.Int32 or Coercion.Uint32 } || parameter.Prologue is null && parameter.Uses.Count > 0 && parameter.Uses.TrueForAll(use => use.Coercion is Coercion.Int32 or Coercion.Uint32))) {
        parameter.IsCoercedOnly = true;
        if (!external)
          parameter.Type = parameter.Entry = stated.IsSigned ? JsType.I32 : JsType.U32;
      }

      return;
    }

    // A parameter only a string can be is one: it calls a method only strings have, or a for...of goes over it.
    if (external && parameter.UsedAsString is { } text) {
      parameter.Type = parameter.Entry = JsType.String;
      parameter.Evidence = parameter.EntryEvidence = text;
      return;
    }

    // An array has no type of its own to start with: it takes the type of the arrays the file passes.
    parameter.IsArrayByCall = external && parameter.UsedAsArray is not null;
    if (!external || parameter.IsArrayByCall)
      return;

    var coerced = parameter.Prologue ?? (parameter.Uses.Count > 0 && parameter.Uses.TrueForAll(use => use.Coercion != Coercion.None)
      ? (parameter.Uses.TrueForAll(use => use.Coercion == Coercion.Uint32) ? Coercion.Uint32 : Coercion.Int32, parameter.Uses[0].Use)
      : null);
    if (coerced is { } integer) {
      parameter.Type = parameter.Entry = integer.Coercion == Coercion.Uint32 ? JsType.U32 : JsType.I32;
      parameter.Evidence = parameter.EntryEvidence = integer.Use;
      parameter.IsIntegerByUse = true;
    } else {
      parameter.Type = parameter.Entry = JsType.F64;
      parameter.Reason = "no JSDoc type";
    }

    // A parameter the function compares with null or undefined is one a caller may pass them as (#1322).
    if (parameter.ComparedWithNull)
      parameter.Type = parameter.Entry = JsType.Join(parameter.Type, JsType.Null);

    if (parameter.ComparedWithUndefined)
      parameter.Type = parameter.Entry = JsType.Join(parameter.Type, JsType.UndefinedValue);
  }

  /// <summary>Lets the value of one flow reach its place; says whether the type of the place changed.</summary>
  private bool Apply(FunctionModel model, Flow flow) {
    switch (flow.Kind) {
      case FlowKind.Assign:
        return this.Reach(flow.Target!, this.Of(flow.Node), flow.Node) | this.Alias(model, flow.Node, flow.Target!.Type);
      case FlowKind.Grow:
        return this.Grow(flow);
      case FlowKind.FieldGrow:
        var member = (MemberExpression)flow.Source!;
        return this.Of(member.Object).Class?.Field(Binder.KeyOf(member)!) is { } grown && this.Grow(flow with { Kind = FlowKind.Grow, Target = grown.Variable });
      case FlowKind.Entry:
        return this.Entry(flow);
      case FlowKind.Compound:
        return this.Reach(flow.Target!, this.Combined((AssignmentExpression)flow.Node), flow.Node);
      case FlowKind.Step:
        return this.Reach(flow.Target!, Stepped(flow.Target!.Type), flow.Node);
      case FlowKind.Store:
        var store = (AssignmentExpression)flow.Node;
        return flow.Target!.Type is { Kind: JsKind.Array, IsPlain: true }
          && this.Reach(flow.Target, JsType.ArrayOf(Binder.Compound(store.Operator) is null ? this.Of(store.Right) : this.Combined(store), true), flow.Node);
      case FlowKind.StepElement:
        return flow.Target!.Type is { Kind: JsKind.Array, IsPlain: true } stepped
          && this.Reach(flow.Target, JsType.ArrayOf(Stepped(stepped.Element!), true), flow.Node);
      case FlowKind.Return:
        var argument = Returned(flow);
        var result = JsType.Join(model.Result, argument is null ? JsType.UndefinedValue : this.Of(argument));
        // A returned variable is the list the result is (#1328), of the integers JSDoc states where its whole doubles fit them (#1413).
        var aliased = argument is not null && (this.Alias(model, argument, result) | this.Fit(model, argument, model.StatedResult));
        if (result == model.Result)
          return aliased;

        if (Decides(model.Result, result))
          model.ResultEvidence = argument ?? flow.Node;

        model.Result = result;
        this._known.Clear();
        return true;
      case FlowKind.Field:
        var key = ((Identifier)((Property)flow.Node).Key).Name;
        return this.Of(flow.Source!).Shape?.TypeOf(key) is { } value && this.Reach(flow.Target!, value, flow.Node);
      case FlowKind.FieldStore or FlowKind.FieldStep or FlowKind.MethodArgument:
        return this.ApplyMember(model, flow);
      case FlowKind.Invoke:
        return this.Invoke((CallExpression)flow.Node);
      case FlowKind.Yield:
        return this.Yielded(model, flow);
      case FlowKind.KeyedStore:
        return this.KeyedStore(flow);
      default:
        var called = this.ModelOf(flow.Callee!).Parameters.ElementAtOrDefault(flow.Index);
        return (called is { Declared: null } parameter && this.Pass(parameter, flow.Missing ? JsType.UndefinedValue : this.Of(flow.Node), flow.Node))
          | (called is { Declared.IsStated: true } stated && !flow.Missing && this.Shape(stated, this.Of(flow.Node), flow.Node))
          | (called is not null && !flow.Missing && this.Alias(model, flow.Node, called.Type));
    }
  }

  /// <summary>
  /// Lets an argument decide which array a parameter JSDoc states as <c>T[]</c> is (#1374): an
  /// <c>Array</c>, a list or a typed array of the stated elements, as the calls of the file pass it;
  /// calls of two kinds make a conflict, which the lowering refuses. The elements stay the stated ones.
  /// </summary>
  private bool Shape(Variable parameter, JsType argument, Node node) {
    if (parameter.Declared!.ShapedAs(argument) is not { } shaped)
      return false;

    // A list passed is the list the parameter is: where its elements fit the stated ones, it holds the stated elements.
    var stated = parameter.Declared.Element!;
    if (argument.IsGrowable && node is Identifier name && this._scope?.Bindings.GetValueOrDefault(name) is Variable { Type.IsGrowable: true } list
        && JsType.Join(argument.Element!, stated) is var element && element.Ir == stated.Ir && element != argument.Element
        && this.Reach(list, JsType.ListOf(element), node))
      return true;

    var entry = parameter.IsShaped ? JsType.Join(parameter.Entry, shaped) : shaped;
    parameter.IsShaped = true;
    if (entry == parameter.Entry)
      return false;

    parameter.Type = parameter.Entry = entry;
    this._known.Clear();
    return true;
  }

  /// <summary>
  /// Lets the value of an argument reach a parameter. A parameter with a JSDoc type is not reached:
  /// its type is a contract, which a call either keeps or is refused for.
  /// </summary>
  private bool Pass(Variable parameter, JsType type, Node argument) {
    var entry = JsType.Join(parameter.Entry, type);
    if (Decides(parameter.Entry, entry))
      parameter.EntryEvidence = argument;

    parameter.Entry = entry;

    return this.Reach(parameter, type, argument);
  }

  /// <summary>The type of the value a compound assignment stores.</summary>
  private JsType Combined(AssignmentExpression assignment)
    => this.Decide(Binder.Compound(assignment.Operator)!.Value, assignment.Left, assignment.Right, Coercion.None).Result;

  /// <summary>What <c>++</c> leaves in a place: the old value plus one, in double arithmetic or as a <c>BigInt</c> without a bound.</summary>
  private static JsType Stepped(JsType type) => type.Kind == JsKind.BigInt ? JsType.UnboundedBigInt : JsType.F64;

  /// <summary>Joins a type into a variable. What was computed from the old type of the variable is forgotten.</summary>
  private bool Reach(Variable variable, JsType type, Node evidence) {
    var joined = JsType.Join(variable.Type, type);
    if (joined == variable.Type)
      return false;

    if (Decides(variable.Type, joined))
      variable.Evidence = evidence;

    variable.Type = joined;
    this._known.Clear();
    return true;
  }

  /// <summary>
  /// Whether a value changed what a place is in the IR, so that its expression is the evidence for
  /// the type: a second literal that the type of the first one holds as well decides nothing.
  /// </summary>
  private static bool Decides(JsType before, JsType after)
    // A record is compared by its shape: its struct is made only once the shapes are named.
    => before.Kind == JsKind.Unknown || (after.HasRecord || before.HasRecord ? before != after : before.Ir != after.Ir);

  /// <summary>
  /// Gives a place that values reach, but none with a type, the type of a number: it is fed only by
  /// itself, as the result of a recursion without an end is. Says whether there was such a place.
  /// </summary>
  private bool Promote(List<FunctionModel> models) {
    // A stated 64-bit integer no Number and no BigInt reached is the BigInt it states, before anything it feeds is promoted.
    var promoted = false;
    // What enters a parameter decides it, what a field holds decides the field.
    foreach (var (place, stated, isParameter) in this._statedLongs.Where(item => (item.IsParameter ? item.Place.Entry : item.Place.Type).Kind is JsKind.Unknown or JsKind.Null)) {
      if (isParameter)
        place.Entry = JsType.Join(place.Entry, stated.Decided);

      place.Type = JsType.Join(place.Type, stated.Decided);
      promoted = true;
    }

    if (promoted) {
      this._known.Clear();
      return true;
    }

    foreach (var model in models) {
      this.Enter(model);
      foreach (var flow in model.Flows) {
        if (flow.Kind == FlowKind.Argument && this.ModelOf(flow.Callee!).Parameters.ElementAtOrDefault(flow.Index) is { Type.Kind: JsKind.Unknown } parameter)
          promoted |= this.Pass(parameter, JsType.F64, flow.Node);
        else if (flow.Kind is FlowKind.Assign or FlowKind.Compound or FlowKind.Step && flow.Target!.Type.Kind == JsKind.Unknown)
          promoted |= this.Reach(flow.Target, (flow.Kind == FlowKind.Assign ? this.StatedCall(flow.Node) : null) ?? JsType.F64, flow.Node);
        else if (flow.Kind == FlowKind.Return && model.Result.Kind == JsKind.Unknown && Returned(flow) is { } argument) {
          model.Result = JsType.F64;
          model.ResultEvidence = argument;
          this._known.Clear();
          promoted = true;
        }
      }
    }

    return promoted;
  }

  /// <summary>
  /// The type JSDoc states for the result of a call no value comes out of - of a method whose every
  /// implementation only throws (#1389), alone or as a branch of a <c>?:</c> - or <see langword="null"/>:
  /// a local that takes it is of that type rather than a double, which a stated place of the code
  /// after it, never run, would refuse (#1413).
  /// </summary>
  private JsType? StatedCall(Node node) => node switch {
    ConditionalExpression choice => this.StatedCall(choice.Consequent) ?? this.StatedCall(choice.Alternate),
    CallExpression { Callee: MemberExpression { Computed: false } invoked } when this.Of(invoked.Object).Class is { } owner && Binder.KeyOf(invoked) is { } key
      && owner.Member(MemberKind.Method, key) is { } called && this.ModelOf(called) is { Result.Kind: JsKind.Unknown, StatedResult: { Ir: not null } stated }
      => stated.Decided,
    _ => null
  };

  /// <summary>Why every parameter, local and result of a function has its type.</summary>
  public IEnumerable<TypeExplanation> Explain(FunctionModel model) {
    TypeExplanation Explanation(string role, string name, IrType type, Node at, string evidence) {
      var start = at.Location.Start;
      return new TypeExplanation(model.Candidate.Name, role, name, type, evidence, start.Line, start.Column + 1);
    }

    foreach (var parameter in model.Parameters) {
      var at = parameter.EntryEvidence ?? parameter.Declaration;
      yield return Explanation("parameter", parameter.Name, parameter.Parameter!.Type, at, parameter.EntryEvidence is null ? parameter.Reason! : this.Text(at));
    }

    // A parameter the body assigns another type has a variable of its own there, which is explained like a local.
    foreach (var variable in model.Parameters.Concat(model.Locals).Where(variable => variable.Local is not null && variable.Local != variable.Parameter)) {
      var at = variable.Evidence ?? variable.Declaration;
      yield return Explanation("local", variable.Name, variable.Local!.Type, at, variable.Evidence is null ? "no value" : this.Text(at));
    }

    if (model.ReturnType != IrType.Void)
      yield return Explanation("result", "", model.ReturnType, model.ResultEvidence!, this.Text(model.ResultEvidence!));
  }

}
