using Acornima;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

internal sealed partial class TypeInference {

  /// <summary>
  /// The type of an expression on strings, or <see langword="null"/> for one that is not: a string
  /// literal or template, the code point a <c>for…of</c> over a string yields - or the element one over a
  /// typed array yields -, an element and the
  /// length of a string, and the result of a method of a string or of <c>String.fromCharCode</c>.
  /// </summary>
  private JsType? StringType(Node node) {
    switch (node) {
      case StringLiteral or TemplateLiteral:
        return JsType.String;
      case ForOfStatement loop:
        // A for...of over an array, a typed array or a view yields its elements, a binary32 one as the double it is (#1328, #1325).
        var iterated = this.Of(loop.Right);
        return iterated.Kind == JsKind.String ? JsType.String
          : iterated is { Kind: JsKind.Array, Element: { } element } ? (element.Float == 32 ? JsType.F64 : element)
          : iterated is { Kind: JsKind.Sequence, Element: { } produced } ? produced
          : this.Iterated(loop.Right) ?? JsType.Unknown;
      case MemberExpression member when this.Of(member.Object).Kind == JsKind.String:
        return member.Computed ? JsType.String : member.Property is Identifier { Name: "length" } ? JsType.I32 : JsType.Unknown;
      case CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: var method } } callee }:
        var receiver = this.Of(callee.Object);
        if (receiver is { Kind: JsKind.Array, Element.Kind: JsKind.String } && method == "join")
          return JsType.String;

        if (receiver.Kind == JsKind.String)
          return StringResult(method);

        // String.fromCharCode.apply(null, codes) (#1400).
        if (method == "apply" && callee.Object is MemberExpression applied && this.Names.Library(applied) == "String.fromCharCode")
          return JsType.String;

        return this.Names.Library(callee) == "String.fromCharCode" ? JsType.String : null;
      default:
        return null;
    }
  }

  /// <summary>The type of what a method of a string returns; nothing for a method the lowering refuses.</summary>
  private static JsType StringResult(string method) => method switch {
    "charCodeAt" => JsType.F64,
    "codePointAt" or "indexOf" or "lastIndexOf" => JsType.I32,
    "includes" or "startsWith" or "endsWith" => JsType.Boolean,
    "split" => JsType.ArrayOf(JsType.String, true),
    "charAt" or "at" or "substring" or "slice" or "concat" or "replace" or "replaceAll" or "trim" or "trimStart" or "trimEnd"
      or "padStart" or "padEnd" or "repeat" => JsType.String,
    _ => JsType.Unknown
  };

  /// <summary>
  /// What an operator does when a string stands on either side, or <see langword="null"/> when none
  /// does: <c>+</c> of two strings concatenates, a comparison of two strings is by code unit, and
  /// anything else with a string - a number joined to text among it - has no type.
  /// </summary>
  private Operation? DecideString(Operator op, Node left, Node right) {
    var (first, second) = (this.Of(left), this.Of(right));
    if (first.Kind != JsKind.String && second.Kind != JsKind.String)
      return null;

    var both = first.Kind == JsKind.String && second.Kind == JsKind.String;
    // A number, a BigInt or a truth value joined to text is written as JavaScript writes it (#1315).
    var written = first.Kind is JsKind.String or JsKind.Number or JsKind.BigInt or JsKind.Boolean && second.Kind is JsKind.String or JsKind.Number or JsKind.BigInt or JsKind.Boolean;
    return op switch {
      Operator.Addition => written ? new(Plan.Concat, JsType.String, Polypiler.Core.IR.IrType.String) : new(Plan.Invalid, JsType.Unknown),
      Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality
        or Operator.LessThan or Operator.LessThanOrEqual or Operator.GreaterThan or Operator.GreaterThanOrEqual
        => both ? new(Plan.CompareString, JsType.Boolean, Polypiler.Core.IR.IrType.String) : new(Plan.Invalid, JsType.Boolean),
      _ => new(Plan.Invalid, JsType.Unknown)
    };
  }

}
