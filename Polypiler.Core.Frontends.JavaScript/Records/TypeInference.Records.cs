using System.Collections.Generic;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

internal sealed partial class TypeInference {

  /// <summary>
  /// The type of an object literal: a record of its keys in source order and the types of their
  /// values. A literal the subset does not take - a computed or repeated key, a key that is no name,
  /// a spread, a method, a getter or a setter - has no type, and the lowering names what it is.
  /// </summary>
  private JsType Record(ObjectExpression literal) {
    var keys = new List<string>();
    var types = new List<JsType>();
    foreach (var node in literal.Properties) {
      if (Plain(node) is not { } property || keys.Contains(property.Key))
        return JsType.Unknown;

      keys.Add(property.Key);
      types.Add(this.Of(property.Value));
    }

    return JsType.Record(new RecordShape(this.Shapes, keys, types));
  }

  /// <summary>A property of an object literal or a pattern that is a name and a value, or <see langword="null"/>.</summary>
  public static (string Key, Node Value)? Plain(Node node) => node is Property { Kind: PropertyKind.Init, Computed: false, Method: false, Key: Identifier { Name: var key }, Value: var value }
    ? (key, value)
    : null;

}
