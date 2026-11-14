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

  private static readonly Regex _whiteSpace = new(@"\s+", RegexOptions.CultureInvariant);

  private readonly Dictionary<Candidate, FunctionModel> _models = [];
  private readonly Dictionary<FunctionModel, Binder> _binders = [];
  private readonly Dictionary<(Node, Coercion), JsType> _known = [];
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

    // A function without a return returns nothing, which is known before any type is.
    if (!model.Flows.Exists(flow => flow.Kind == FlowKind.Return))
      model.Result = JsType.Undefined;

    return model;
  }

  /// <summary>Reads the parameters and what JSDoc states; returns why the function cannot be imported whatever its body is.</summary>
  private Diagnostic? ReadSignature(FunctionModel model) {
    var candidate = model.Candidate;
    var function = candidate.Function;
    Diagnostic Unsupported(Node node, string what) => this.Diagnose(node, JavaScriptFrontend.UnsupportedDescriptor, $"'{what}' is not supported yet");
    if (candidate.Problem is not null)
      return Unsupported(candidate.Anchor, candidate.Problem);

    if (function.Generator || function.Async)
      return Unsupported(candidate.Anchor, function.Generator ? "a generator" : "an async function");

    if (candidate.Name.Contains('$'))
      return Unsupported(candidate.Anchor, $"the name {candidate.Name}");

    foreach (var parameter in function.Params) {
      if (parameter is not Identifier { Name: var name })
        return Unsupported(parameter, parameter is RestElement ? "a rest parameter" : parameter is AssignmentPattern ? "a default parameter value" : "a destructured parameter");

      if (name.Contains('$'))
        return Unsupported(parameter, $"the name {name}");

      if (model.Parameters.Exists(earlier => earlier.Name == name))
        return Unsupported(parameter, $"a second parameter named {name}");

      var (stated, type) = JsDoc.ParameterType(candidate.Documentation, name);
      if (stated && type is null)
        return this.Diagnose(parameter, JavaScriptFrontend.MissingTypeDescriptor, $"parameter '{name}' of '{candidate.Name}' has no supported JSDoc type");

      var variable = new Variable(name, parameter, true) { Declared = type is null ? null : JsType.Of(type) };
      model.Parameters.Add(variable);
      model.Bindings[parameter] = variable;
    }

    model.DeclaredResult = JsDoc.ResultType(candidate.Documentation);
    return null;
  }

  /// <summary>
  /// Infers the types of the given functions and of every function they call. Afterwards the types
  /// are final and <see cref="Of"/> answers from what it has computed once.
  /// </summary>
  /// <param name="roots">The functions to import.</param>
  /// <param name="isPublic">Whether a function is visible outside the module.</param>
  /// <returns>The functions, the called ones after their callers.</returns>
  public List<FunctionModel> Solve(IEnumerable<Candidate> roots, System.Func<Candidate, bool> isPublic) {
    var models = new List<FunctionModel>();
    // A function an initializer calls is imported as well.
    var pending = new Queue<Candidate>(roots.Concat(this.Initializers?.Callees ?? []));
    while (pending.TryDequeue(out var candidate)) {
      var model = this.ModelOf(candidate);
      if (models.Contains(model))
        continue;

      model.IsPublic = isPublic(candidate);
      models.Add(model);
      foreach (var callee in model.Callees)
        pending.Enqueue(callee);
    }

    var called = this.Flowing(models).SelectMany(model => model.Flows).Where(flow => flow.Kind == FlowKind.Argument).Select(flow => flow.Callee).ToHashSet();
    foreach (var model in models)
      foreach (var parameter in model.Parameters)
        this.Seed(model, parameter, model.IsPublic || !called.Contains(model.Candidate));

    do {
      for (var changed = true; changed;) {
        changed = false;
        foreach (var model in this.Flowing(models)) {
          this.Enter(model);
          foreach (var flow in model.Flows)
            changed |= this.Apply(model, flow);
        }
      }
    } while (this.Promote([.. this.Flowing(models)]));

    // Nothing in the file says what array a parameter is that no call passes one to.
    foreach (var model in models)
      foreach (var parameter in model.Parameters.Where(parameter => parameter.IsArrayByCall && parameter.Entry.Kind != JsKind.Array))
        model.Problem ??= this.Diagnose(parameter.Declaration, JavaScriptFrontend.MissingTypeDescriptor,
          $"parameter '{parameter.Name}' of '{model.Candidate.Name}' is used as an array and needs a JSDoc type");

    return models;
  }

  /// <summary>
  /// The type a parameter starts with. JSDoc states a contract. Without it, a parameter whose callers
  /// are not all known is a double, unless the function reads it under <c>ToInt32</c> or
  /// <c>ToUint32</c> only, or re-assigns it coerced before anything else: then it is that integer.
  /// A parameter whose callers are all known starts with nothing and takes what they pass, and so
  /// does one that is used as an array: a double it cannot be, and what array it is only a call says.
  /// </summary>
  private void Seed(FunctionModel model, Variable parameter, bool external) {
    if (parameter.Declared is { } declared) {
      parameter.Type = parameter.Entry = declared;
      parameter.Reason = $"@param {{{JsType.Spell(declared.Ir!)}}}";
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
  }

  /// <summary>Lets the value of one flow reach its place; says whether the type of the place changed.</summary>
  private bool Apply(FunctionModel model, Flow flow) {
    switch (flow.Kind) {
      case FlowKind.Assign:
        return this.Reach(flow.Target!, this.Of(flow.Node), flow.Node);
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
        var argument = ((ReturnStatement)flow.Node).Argument;
        var result = JsType.Join(model.Result, argument is null ? JsType.Undefined : this.Of(argument));
        if (result == model.Result)
          return false;

        if (Decides(model.Result, result))
          model.ResultEvidence = argument ?? flow.Node;

        model.Result = result;
        this._known.Clear();
        return true;
      case FlowKind.Field:
        var key = ((Identifier)((Property)flow.Node).Key).Name;
        return this.Of(flow.Source!).Shape?.TypeOf(key) is { } value && this.Reach(flow.Target!, value, flow.Node);
      default:
        return this.ModelOf(flow.Callee!).Parameters.ElementAtOrDefault(flow.Index) is { Declared: null } parameter && this.Pass(parameter, this.Of(flow.Node), flow.Node);
    }
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
    var promoted = false;
    foreach (var model in models) {
      foreach (var flow in model.Flows) {
        if (flow.Kind == FlowKind.Argument && this.ModelOf(flow.Callee!).Parameters.ElementAtOrDefault(flow.Index) is { Type.Kind: JsKind.Unknown } parameter)
          promoted |= this.Pass(parameter, JsType.F64, flow.Node);
        else if (flow.Kind is FlowKind.Assign or FlowKind.Compound or FlowKind.Step && flow.Target!.Type.Kind == JsKind.Unknown)
          promoted |= this.Reach(flow.Target, JsType.F64, flow.Node);
        else if (flow.Kind == FlowKind.Return && model.Result.Kind == JsKind.Unknown && ((ReturnStatement)flow.Node).Argument is { } argument) {
          model.Result = JsType.F64;
          model.ResultEvidence = argument;
          this._known.Clear();
          promoted = true;
        }
      }
    }

    return promoted;
  }

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
