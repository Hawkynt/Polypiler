using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

internal sealed partial class TypeInference {

  /// <summary>
  /// Why a generator cannot be imported, or <see langword="null"/> (#1327): a yield that is no
  /// statement of its own - it would receive the value of <c>next(v)</c> - a yield without a value, a
  /// return of a value, and a generator nested in a function.
  /// </summary>
  private Diagnostic? GeneratorProblem(Candidate candidate) {
    Diagnostic Unsupported(Node node, string what) => this.Diagnose(node, JavaScriptFrontend.UnsupportedDescriptor, $"'{what}' is not supported yet");
    var function = candidate.Function;
    Diagnostic? Search(Node node, Node? parent) {
      switch (node) {
        case YieldExpression produced when parent is not ExpressionStatement:
          return Unsupported(produced, "yield as a value");
        case YieldExpression { Argument: null } produced:
          return Unsupported(produced, "yield without a value");
        case ReturnStatement { Argument: not null } returned:
          return Unsupported(returned, "a generator that returns a value");
        case IFunction nested when !ReferenceEquals(nested, function):
          // A function inside the generator is a body of its own, whose yields and returns are its own.
          return null;
      }

      foreach (var child in node.ChildNodes)
        if (Search(child, node) is { } problem)
          return problem;

      return null;
    }

    return Search((Node)function, null) ?? (candidate.IsNested ? Unsupported(candidate.Anchor, "a nested generator") : null);
  }

  /// <summary>
  /// Lets a yielded value reach the values of the generator object: the value itself, or every value
  /// of the generator object or the array of a <c>yield*</c>. Says whether the type of the result changed.
  /// </summary>
  private bool Yielded(FunctionModel model, Flow flow) {
    var produced = this.Of(flow.Node);
    if (flow.Index == 1)
      produced = produced switch {
        { Kind: JsKind.Sequence, Element: { } element } => element,
        { Kind: JsKind.Array, IsView: false, Element: { } element } => element.Float == 32 ? JsType.F64 : element,
        _ => JsType.Unknown
      };

    var before = model.Result.Element!;
    var joined = JsType.Join(before, produced);
    if (joined == before)
      return false;

    if (Decides(before, joined))
      model.ResultEvidence = flow.Node;

    model.Result = JsType.SequenceOf(joined);
    this._known.Clear();
    return true;
  }

}
