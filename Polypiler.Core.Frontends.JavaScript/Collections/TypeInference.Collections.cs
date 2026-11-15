using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

/// <summary>
/// The arrays that change their length (#1328): a <c>push</c>, <c>pop</c> or <c>splice</c> of the array
/// a variable holds, a write of its <c>length</c> or at <c>a[a.length]</c>, makes it a list, and what is
/// pushed or inserted joins its elements. An array that becomes a list makes every variable it came
/// from a list too - a variable assigned to it, an argument passed for it, a returned variable - so
/// that one array is one list wherever it is held.
/// </summary>
internal sealed partial class TypeInference {

  /// <summary>Makes the array of a flow of <see cref="FlowKind.Grow"/> a list with what it adds; says whether its type changed.</summary>
  private bool Grow(Flow flow) {
    if (flow.Target!.Type is not { Kind: JsKind.Array, IsPlain: true } held)
      return false;

    var added = flow.Node switch {
      CallExpression { Callee: MemberExpression { Property: Identifier { Name: "push" } } } push => push.Arguments,
      CallExpression { Callee: MemberExpression { Property: Identifier { Name: "splice" } } } splice => splice.Arguments.Skip(2),
      _ => []
    };
    var element = added.Aggregate(held.Element!, (type, argument) => JsType.Join(type, this.Of(argument)));
    return this.Reach(flow.Target, JsType.ListOf(element), flow.Node);
  }

  /// <summary>
  /// Makes the variable a value is read from, or the result of the function of the file it is the
  /// result of, a list where the place it goes to is one: the array is the one that changes its length
  /// there. Says whether a type changed.
  /// </summary>
  private bool Alias(FunctionModel model, Node source, JsType place) {
    if (!place.IsGrowable)
      return false;

    // The array a function of the file returns is the one its return statements return.
    if (source is CallExpression call && this.Names.Callee(call.Callee) is { } callee && this.ModelOf(callee) is { Result: { Kind: JsKind.Array, IsPlain: true, IsGrowable: false } result } returning) {
      returning.Result = result.Grown;
      this._known.Clear();
      return true;
    }

    if (source is not Identifier name || model.Bindings.GetValueOrDefault(name) is not Variable { Type: { Kind: JsKind.Array, IsPlain: true, IsGrowable: false } array } variable)
      return false;

    return this.Reach(variable, array.Grown, source);
  }

  /// <summary>
  /// The type of what a method of an array yields - <c>pop</c> the element or <c>undefined</c>,
  /// <c>indexOf</c> an <c>int32</c>, <c>includes</c> a truth value, <c>push</c> the new length - or
  /// <see langword="null"/> for any other call.
  /// </summary>
  private JsType? ListCall(CallExpression call) {
    if (call.Callee is not MemberExpression { Computed: false, Property: Identifier { Name: var method } } member
        || this.Of(member.Object) is not { Kind: JsKind.Array, IsPlain: true, Element: { } element })
      return null;

    return method switch {
      "pop" => JsType.Join(element.Float == 32 ? JsType.F64 : element, JsType.UndefinedValue),
      "push" or "indexOf" => JsType.I32,
      "includes" => JsType.Boolean,
      _ => null
    };
  }

}
