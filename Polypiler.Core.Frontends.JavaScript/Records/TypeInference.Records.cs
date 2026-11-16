using System.Collections.Generic;
using Acornima;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

internal sealed partial class TypeInference {

  /// <summary>The <c>@typedef {Object}</c> records of the file (#1409).</summary>
  public required Typedefs Typedefs { get; init; }

  /// <summary>The class a JSDoc type names: a class of the file, else the class of a typedef; <see langword="null"/> for neither.</summary>
  public ClassModel? Named(string key) => this.Classes.GetValueOrDefault(key) is { IsLibrary: false } owner ? owner : this.Typedefs.Resolve(key, this.Named);

  /// <summary>
  /// The branch of <c>x === undefined ? d : x</c> - or of <c>x !== undefined ? x : d</c>, <c>==</c>,
  /// <c>!=</c> and <c>null</c> alike - that reads the value its test proves present, where <c>x</c> is a
  /// variable or a chain of keys of one that nothing between the test and the branch changes, and the
  /// test rules out every absent value that reaches it: of that branch the value is present (#1409).
  /// <see langword="null"/> for any other conditional.
  /// </summary>
  public Node? PresentBranch(ConditionalExpression choice) {
    if (choice.Test is not BinaryExpression { Operator: Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality } compared)
      return null;

    var names = this.Names;
    var (literal, tested) = names.IsAbsentLiteral(compared.Right) ? (compared.Right, compared.Left) : names.IsAbsentLiteral(compared.Left) ? (compared.Left, compared.Right) : (null, null);
    if (literal is null || !IsPlace(tested!))
      return null;

    var type = this.Of(tested!);
    var strict = compared.Operator is Operator.StrictEquality or Operator.StrictInequality;
    if (!type.IsAbsent || strict && (literal is NullLiteral ? type.MayBeUndefined : type.MayBeNull))
      return null;

    var branch = compared.Operator is Operator.Equality or Operator.StrictEquality ? choice.Alternate : choice.Consequent;
    return IsPlace(branch) && this.Text(branch) == this.Text(tested!) ? branch : null;
  }

  /// <summary>Whether reading an expression twice reads one value: a name, <c>this</c>, or a key of such an expression.</summary>
  private static bool IsPlace(Node node) => node switch {
    Identifier or ThisExpression => true,
    MemberExpression { Computed: false, Property: Identifier } member => IsPlace(member.Object),
    _ => false
  };

  /// <summary>
  /// The type of <c>a || b</c> whose operands join to no type, where JavaScript yields the left one:
  /// an object that is never absent counts as true, so the right operand is never evaluated; of an
  /// object of a typedef that may be absent, an object literal the typedef takes on the right (#1409).
  /// <see langword="null"/> for any other.
  /// </summary>
  public JsType? Leftmost(LogicalExpression logical) {
    var left = this.Of(logical.Left);
    if (logical.Operator != Operator.LogicalOr || left.Kind != JsKind.Object || JsType.Join(left.Present, this.Of(logical.Right)).Kind != JsKind.Conflict)
      return null;

    return !left.IsAbsent ? left : left.Class!.IsTypedef && logical.Right is ObjectExpression ? left.Present : null;
  }

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
