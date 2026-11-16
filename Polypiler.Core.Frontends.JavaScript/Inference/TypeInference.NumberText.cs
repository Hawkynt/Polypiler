using Acornima;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

internal sealed partial class TypeInference {

  /// <summary>Whether a call is of a global function of a name that no local, constant or function of the file hides.</summary>
  private bool IsGlobal(CallExpression call, string name)
    => call.Callee is Identifier identifier && identifier.Name == name && !this._scope!.Bindings.ContainsKey(identifier) && this.Names.Callee(identifier) is null;

  /// <summary>
  /// The type of a conversion between numbers and text, or <see langword="null"/> for an expression
  /// that is none: <c>String(x)</c>, <c>x.toString()</c> and <c>x.toFixed(d)</c> of a number, a
  /// <c>BigInt</c> or a truth value are strings; <c>Number(s)</c>, <c>+s</c>, <c>parseFloat</c> and
  /// <c>parseInt</c> of a string are doubles.
  /// </summary>
  private JsType? NumberTextType(Node node) {
    switch (node) {
      case UnaryExpression { Operator: Operator.UnaryPlus } plus when this.Of(plus.Argument).Kind == JsKind.String:
        return JsType.F64;
      case CallExpression call when this.IsGlobal(call, "String"):
        return JsType.String;
      // parseInt of a short text of proven digits is a whole double (#1413).
      case CallExpression call when this.IsGlobal(call, "parseInt") && this.ParsedRange(call) is { } range:
        return range;
      case CallExpression call when this.IsGlobal(call, "Number") || this.IsGlobal(call, "parseFloat") || this.IsGlobal(call, "parseInt"):
        return call.Arguments.Count > 0 && this.Of(call.Arguments[0]).Kind == JsKind.String ? JsType.F64 : null;
      case CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: "toString" or "toFixed" or "toPrecision" or "toExponential" or "toLocaleString" } } callee }
        when this.Of(callee.Object).Kind is JsKind.Number or JsKind.BigInt or JsKind.Boolean:
        return JsType.String;
      default:
        return null;
    }
  }

}
