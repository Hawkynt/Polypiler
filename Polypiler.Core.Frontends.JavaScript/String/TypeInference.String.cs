using Acornima;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

internal sealed partial class TypeInference {

  /// <summary>
  /// The type of an expression on strings, or <see langword="null"/> for one that is not: a string
  /// literal or template, the code point a <c>for…of</c> over a string yields, an element and the
  /// length of a string, and the result of a method of a string or of <c>String.fromCharCode</c>.
  /// </summary>
  private JsType? StringType(Node node) {
    switch (node) {
      case StringLiteral or TemplateLiteral:
        return JsType.String;
      case ForOfStatement loop:
        return this.Of(loop.Right).Kind == JsKind.String ? JsType.String : JsType.Unknown;
      case MemberExpression member when this.Of(member.Object).Kind == JsKind.String:
        return member.Computed ? JsType.String : member.Property is Identifier { Name: "length" } ? JsType.I32 : JsType.Unknown;
      case CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: var method } } callee }:
        var receiver = this.Of(callee.Object);
        if (receiver is { Kind: JsKind.Array, Element.Kind: JsKind.String } && method == "join")
          return JsType.String;

        if (receiver.Kind == JsKind.String)
          return StringResult(method);

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
    return op switch {
      Operator.Addition => both ? new(Plan.Concat, JsType.String, Polypiler.Core.IR.IrType.String) : new(Plan.Invalid, JsType.Unknown),
      Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality
        or Operator.LessThan or Operator.LessThanOrEqual or Operator.GreaterThan or Operator.GreaterThanOrEqual
        => both ? new(Plan.CompareString, JsType.Boolean, Polypiler.Core.IR.IrType.String) : new(Plan.Invalid, JsType.Boolean),
      _ => new(Plan.Invalid, JsType.Unknown)
    };
  }

}
