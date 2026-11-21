using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

/// <summary>
/// The types of function values (#1326). A nested function - an arrow, a function expression, a
/// function declaration inside a function - is a function of its own whose parameters take what the
/// calls through its values pass, as the parameters of a function of the file take what its calls
/// pass; the functions a value may be share one signature (<see cref="FunctionShape"/>): a
/// parameter of a nested function is a double unless the program proves an integer (#1137).
/// </summary>
internal sealed partial class TypeInference {

  /// <summary>Makes the candidate of a nested function, with the variables of the functions around it, and binds its body.</summary>
  private Candidate Nest(FunctionModel parent, IFunction function, string name, IReadOnlyDictionary<string, Variable> scope) {
    var candidate = new Candidate(name, (Node)function, function) { Parent = parent, Scope = scope };
    this.ModelOf(candidate);
    return candidate;
  }

  /// <summary>The value a <c>return</c> yields - the concise body of an arrow is one - or <see langword="null"/> for none.</summary>
  private static Node? Returned(Flow flow) => flow.Node is ReturnStatement result ? result.Argument : flow.Node;

  /// <summary>Lets the arguments of a call of a function value reach the parameters of every function the value may be; says whether a type changed.</summary>
  private bool Invoke(CallExpression call) {
    if (this.Of(call.Callee) is not { Kind: JsKind.Function } callee)
      return false;

    var changed = false;
    for (var index = 0; index < call.Arguments.Count; ++index)
      changed |= callee.Callable!.Pass(index, this.Of(call.Arguments[index]));

    foreach (var member in callee.Callable!.Members.ToList()) {
      var parameters = this.ModelOf(member).Parameters;
      for (var index = 0; index < call.Arguments.Count && index < parameters.Count; ++index)
        if (parameters[index] is { Declared: null } parameter)
          changed |= this.Pass(parameter, this.Of(call.Arguments[index]), call.Arguments[index]);
    }

    return changed;
  }

  /// <summary>The functions whose values have been made, each once.</summary>
  private IEnumerable<Candidate> Valued() => this._models.Keys.Where(candidate => candidate.HasShape);

  /// <summary>Infers the functions of one shape jointly: one result, and each parameter what any of them is called with.</summary>
  private bool UnifyShapes() {
    var changed = false;
    foreach (var shape in this.Valued().Select(candidate => candidate.Shape.Root).Distinct().ToList()) {
      var models = shape.Members.Select(this.ModelOf).ToList();
      if (models.Count < 2)
        continue;

      var result = models.Select(model => model.Result).Aggregate(JsType.Join);
      foreach (var model in models.Where(model => model.Result != result)) {
        model.ResultEvidence ??= models.Select(other => other.ResultEvidence).FirstOrDefault(evidence => evidence is not null);
        model.Result = result;
        this._known.Clear();
        changed = true;
      }

      for (var index = 0; index < models.Min(model => model.Parameters.Count); ++index) {
        var parameters = models.Select(model => model.Parameters[index]).ToList();
        var entry = parameters.Select(parameter => parameter.Entry).Aggregate(JsType.Join);
        foreach (var parameter in parameters.Where(parameter => parameter.Declared is null && parameter.Entry != entry))
          changed |= this.Pass(parameter, entry, parameter.Declaration);
      }
    }

    return changed;
  }

  /// <summary>
  /// Gives every shape the function type of its functions, once their types are final: the
  /// parameters as they are called, the result as it is returned - a shape a signature names first.
  /// </summary>
  /// <remarks>
  /// A signature is an IR type, which a record among its parameters or its result makes a struct: it is
  /// made once the shapes are named, after <see cref="Solve"/> (#1377).
  /// </remarks>
  public void Sign(List<FunctionModel> models) {
    var visiting = new HashSet<FunctionShape>(ReferenceEqualityComparer.Instance);
    foreach (var candidate in this.Valued().ToList())
      this.Sign(candidate.Shape.Root, visiting);

    this._known.Clear();
  }

  private void Sign(FunctionShape shape, HashSet<FunctionShape> visiting) {
    if (shape.Signature is not null || !visiting.Add(shape))
      return;

    var model = this.ModelOf(shape.Members[0]);
    foreach (var type in model.Parameters.Select(parameter => parameter.Entry).Append(model.Result))
      for (var part = type; part is not null; part = part.Element)
        if (part.Callable is { } named)
          this.Sign(named.Root, visiting);

    // A position the calls pass an argument at that the function has no parameter for is one it ignores (#1408).
    var parameters = model.Parameters.Select(parameter => parameter.Entry).Concat(shape.Arguments.Skip(model.Parameters.Count))
      .Select(entry => entry.Ir is { IsStorable: true } type ? type : IrType.F64).ToList();
    var result = model.ReturnType;
    shape.Signature = IrType.Function(new IrFunctionType(parameters, result.IsStorable || result == IrType.Void ? result : IrType.F64));
  }

}
