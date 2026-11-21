using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// Function values of JavaScript (#1326): an arrow, a function expression and a function declaration
/// inside a function are an <see cref="IrLambda"/> that captures the variables it uses; a function of
/// the file named as a value is an <see cref="IrFunctionRef"/>; a call of a function value is an
/// <see cref="IrInvoke"/>. A function declaration is hoisted (ECMA-262 §10.2.11): its variable is
/// declared where its block starts and holds the closure from the earliest point every variable of
/// the block it captures is declared. A <c>for (let …)</c> whose variables a closure captures gives
/// every pass variables of its own (§14.7.4.4); a <c>for (var …)</c> shares one.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>
  /// A nested function as a lambda: lowered as a function of its own - its parameters, its locals and
  /// its body - in the middle of the function around it, whose variables it captures. A function
  /// expression has a <c>this</c> of its own, which is no object of the subset; an arrow has that of
  /// the function around it.
  /// </summary>
  private IrLambda Lambda(Candidate candidate) {
    var model = types.ModelOf(candidate);
    var (outerModel, locals, returnType, receiver, assigned) = (this._model, this._locals.ToList(), this._returnType, this._receiver, this._assigned);
    this._model = model;
    this._locals.Clear();
    if (candidate.Function is not ArrowFunctionExpression)
      this._receiver = null;

    types.Enter(model);
    try {
      if (model.Problem is { } problem)
        throw new Refusal(problem);

      foreach (var variable in model.Parameters.Concat(model.Locals))
        this.CheckType(variable.Type, variable.Name, variable.Evidence ?? variable.Declaration, true);

      this.CheckType(model.Result, candidate.Name, model.ResultEvidence ?? candidate.Anchor);
      var entry = new List<IrStmt>();
      foreach (var parameter in model.Parameters) {
        parameter.Parameter = parameter.Local = new IrLocal(parameter.Name, this.Storable(parameter.Entry, parameter.Declaration));
        var type = this.Storable(parameter.Type, parameter.Declaration);
        if (type == parameter.Parameter.Type)
          continue;

        parameter.Local = new IrLocal(parameter.Name + "$0", type);
        this._locals.Add(parameter.Local);
        entry.Add(new IrDeclare(parameter.Local, this.To(new Typed(new IrLocalRef(parameter.Parameter), parameter.Entry), type, parameter.Declaration)));
      }

      this._returnType = model.ReturnType;
      var statements = new List<IrStmt>(entry);
      if (candidate.Function.Body is Expression concise) {
        if (this._returnType == IrType.Void) {
          this.Effect(concise, statements);
        } else if (concise is AssignmentExpression { Left: var place } && this.Rereadable(place)) {
          // v => this.total += v yields what it stores, which the place it stores into then holds (#1408).
          this.Effect(concise, statements);
          statements.Add(new IrReturn(this.Value(place, this._returnType)));
        } else {
          statements.Add(new IrReturn(this.Value(concise, this._returnType)));
        }
      } else {
        statements.AddRange(this.Block(((FunctionBody)candidate.Function.Body).Body).Statements);
      }

      var body = new IrBlock(statements);
      if (this._returnType != IrType.Void && !AlwaysReturns(body))
        throw this.Unsupported(candidate.Anchor, "a function that can end without returning its value");

      // A call through the value may pass more arguments than the function has parameters: it ignores them (#1408).
      var parameters = model.Parameters.Select(parameter => parameter.Parameter!).ToList();
      var signature = candidate.Shape.Signature!.Signature!;
      for (var index = parameters.Count; index < signature.Parameters.Count; ++index)
        parameters.Add(new IrLocal($"ignored${index}", signature.Parameters[index]));

      return IrLambda.Of(parameters, this._returnType, [.. this._locals], body);
    } finally {
      this._model = outerModel;
      this._locals.Clear();
      this._locals.AddRange(locals);
      (this._returnType, this._receiver, this._assigned) = (returnType, receiver, assigned);
      types.Enter(outerModel);
    }
  }

  /// <summary>
  /// Whether a place holds exactly the value an assignment to it yields, so that reading it again is that
  /// value: a variable, or a field of an object - not an element, which a typed array converts, nor a
  /// setter, whose getter may answer anything.
  /// </summary>
  private bool Rereadable(Node place) => place switch {
    Identifier name => this._model.Bindings.GetValueOrDefault(name) is Variable,
    MemberExpression { Computed: false, Object: ThisExpression or Identifier } member => Binder.KeyOf(member) is { } key && types.Of(member.Object).Class?.Field(key) is not null,
    _ => false
  };

  /// <summary>
  /// The function declarations of a block: each a variable declared where the block starts, which
  /// counts as assigned there, as a hoisted function is. Returns them with their variables.
  /// </summary>
  private List<(Candidate Function, IrLocal Variable)> Hoisted(List<Statement> statements, List<IrStmt> into) {
    var hoisted = new List<(Candidate, IrLocal)>();
    foreach (var declaration in statements.OfType<FunctionDeclaration>()) {
      if (!this._model.Nested.TryGetValue(declaration, out var nested) || this._model.Bindings.GetValueOrDefault(declaration.Id!) is not Variable variable)
        throw this.Unsupported(declaration, declaration.Id is { } id && this._model.Refused.TryGetValue(id, out var refused) ? refused : "a function inside a function");

      // What refuses the function refuses the block it is hoisted to, before anything of the block is read.
      if (types.ModelOf(nested).Problem is { } problem)
        throw new Refusal(problem);

      var local = new IrLocal(declaration.Id!.Name, this.Storable(variable.Type, declaration));
      variable.Local = local;
      this._locals.Add(local);
      this._assigned = [.. this._assigned, variable];
      into.Add(new IrDeclare(local, null));
      hoisted.Add((nested, local));
    }

    return hoisted;
  }

  /// <summary>Gives the hoisted functions of a block their closures once the block is lowered: each right behind the last declaration of the block it captures.</summary>
  private void Assign(List<(Candidate Function, IrLocal Variable)> hoisted, List<IrStmt> into, int start) {
    foreach (var (function, variable) in hoisted) {
      var lambda = this.Lambda(function);
      var position = start;
      for (var index = start; index < into.Count; ++index)
        if (into[index] is IrDeclare declare && lambda.Captures.Contains(declare.Local))
          position = index + 1;

      into.Insert(position, new IrAssign(variable, lambda));
    }
  }

  /// <summary>A function of the file named as a value, <c>f</c> or <c>O.f</c> of an object of functions (#1408); <see langword="null"/> for any other name.</summary>
  private IrExpr? FunctionValue(Node identifier) {
    if (!this._model.FunctionValues.TryGetValue(identifier, out var function))
      return null;

    // typeof f asks for no signature: a value that nothing calls through has none yet.
    var signature = function.Shape.Signature;
    return signature?.Signature is not { } called || called.Parameters.Count == function.Function.Params.Count ? new IrFunctionRef(signature!, function.Name)
      : throw this.Unsupported(identifier, $"the function {function.Name} as a value that calls pass {called.Parameters.Count} arguments, of which it takes {function.Function.Params.Count}");
  }

  /// <summary>A call of a function value: the callee, then the arguments, each in the type of its parameter in the value's signature.</summary>
  private IrExpr Invocation(CallExpression call) {
    if (types.Of(call.Callee) is not { Kind: JsKind.Function } callee || callee.Ir?.Signature is not { } signature)
      throw this.Unsupported(call, $"a call of {types.Text(call.Callee)}");

    if (call.Arguments.Count != signature.Parameters.Count)
      throw this.Unsupported(call, $"a call of {types.Text(call.Callee)} with {call.Arguments.Count} arguments for {signature.Parameters.Count} parameters");

    var function = this.Expression(call.Callee);
    var arguments = call.Arguments.Select((argument, index) => this.Value(argument, signature.Parameters[index])).ToList();
    return new IrInvoke(signature.Result, function, arguments);
  }

  /// <summary>
  /// The variables of a <c>for (let …)</c> or <c>for (const …)</c> that a closure in the loop
  /// captures, which every pass has variables of its own of; none for a <c>for (var …)</c>.
  /// </summary>
  private static IReadOnlyList<IrLocal> PerIteration(IReadOnlyList<IrLocal> declared, IrExpr condition, IrBlock body, List<IrStmt> step) {
    if (declared.Count == 0)
      return [];

    var captured = IrClosures.Captured(new IrBlock([new IrExprStmt(condition), body, .. step]));
    return [.. declared.Where(captured.Contains)];
  }

}
