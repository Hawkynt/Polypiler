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
    // m.set(k, v) and s.add(x) join what they insert into the keys, values and elements (#1328).
    if (flow.Node is CallExpression { Callee: MemberExpression { Property: Identifier { Name: var inserting } } } insertion) {
      var arguments = insertion.Arguments;
      if (flow.Target!.Type is { Kind: JsKind.Map } map && inserting == "set" && arguments.Count == 2)
        return this.Reach(flow.Target, JsType.MapOf(JsType.Join(map.Key!, this.Of(arguments[0])), JsType.Join(map.Element!, this.Of(arguments[1]))), flow.Node);

      if (flow.Target!.Type is { Kind: JsKind.Set } set && inserting == "add" && arguments.Count == 1)
        return this.Reach(flow.Target, JsType.SetOf(JsType.Join(set.Element!, this.Of(arguments[0]))), flow.Node);
    }

    if (flow.Target!.Type is not { Kind: JsKind.Array, IsPlain: true } held || flow.Node is CallExpression { Callee: MemberExpression { Property: Identifier { Name: "set" or "add" } } })
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

    // The new array of map, filter or flatMap is the list it is assigned to (#1329).
    if (this.GrowCall(source))
      return true;

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
      // A copy of a list is a new Array of its elements, which may grow as the list does (#1385).
      "slice" when this.Of(member.Object).IsGrowable => JsType.ListOf(element),
      _ => null
    };
  }


  /// <summary>The type of the key (index 0) or the value (1) of the entries of the <c>Map</c> a <c>for…of</c> destructures, joined into its variable.</summary>
  private bool Entry(Flow flow) {
    var loop = (ForOfStatement)flow.Node;
    var source = loop.Right is CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: "entries" } } entries, Arguments.Count: 0 } ? entries.Object : loop.Right;
    return this.Of(source) is { Kind: JsKind.Map } map && this.Reach(flow.Target!, flow.Index == 0 ? map.Key! : map.Element!, loop);
  }

  /// <summary>What a <c>for…of</c> yields over a <c>Set</c>, <c>keys()</c> or <c>values()</c> of a <c>Map</c> or of a <c>Set</c>, or <see langword="null"/>.</summary>
  private JsType? Iterated(Node right) {
    if (right is CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: "keys" or "values" } method } member, Arguments.Count: 0 }) {
      var source = this.Of(member.Object);
      return source.Kind switch {
        JsKind.Map => method.Name == "keys" ? source.Key : source.Element,
        JsKind.Set => source.Element,
        _ => null
      };
    }

    return this.Of(right) is { Kind: JsKind.Set } set ? set.Element : null;
  }

  /// <summary>
  /// The type of an expression on a <c>Map</c> or a <c>Set</c>: a new one, with the entries or the
  /// elements of an array literal; its <c>size</c>; what <c>get</c>, <c>has</c> and <c>delete</c> yield.
  /// <see langword="null"/> for any other expression.
  /// </summary>
  private JsType? KeyedType(Node node) {
    switch (node) {
      case NewExpression { Callee: Identifier { Name: var name } constructor } creation when name is "Map" or "Set" && !this._scope!.Bindings.ContainsKey(constructor):
        var listed = creation.Arguments is [ArrayExpression literal] ? literal.Elements : [];
        if (name == "Set") {
          var element = creation.Arguments is [Node source] && source is not ArrayExpression && this.Of(source) is { Kind: JsKind.Array, Element: { } from } ? from : JsType.Unknown;
          return JsType.SetOf(listed.Aggregate(element, (type, item) => item is null or SpreadElement ? type : JsType.Join(type, this.Of(item))));
        }

        var (key, value) = (JsType.Unknown, JsType.Unknown);
        foreach (var entry in listed)
          if (entry is ArrayExpression { Elements: [Node first, Node second] } && first is not SpreadElement && second is not SpreadElement)
            (key, value) = (JsType.Join(key, this.Of(first)), JsType.Join(value, this.Of(second)));

        return JsType.MapOf(key, value);
      case MemberExpression { Computed: false, Property: Identifier { Name: "size" } } size when this.Of(size.Object).Kind is JsKind.Map or JsKind.Set:
        return JsType.I32;
      case CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: var method } } member } when this.Of(member.Object) is { Kind: JsKind.Map or JsKind.Set } keyed:
        return method switch {
          "get" when keyed.Kind == JsKind.Map => JsType.Join(keyed.Element!.Float == 32 ? JsType.F64 : keyed.Element, JsType.UndefinedValue),
          "has" or "delete" => JsType.Boolean,
          _ => JsType.Unknown
        };
      default:
        return null;
    }
  }

}
