using System.Linq;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

internal sealed partial class Lowering {

  /// <summary>
  /// <c>X.A</c> of an enum object: the member, a constant of the enum. An object of numbers that is no
  /// enum, a computed key and a name that is no member are refused, naming the object.
  /// </summary>
  private IrExpr EnumMember(MemberExpression member, EnumObject named) {
    if (named.Record is { Frozen: { } members } record) {
      if (member.Computed || member.Property is not Identifier { Name: var label })
        throw this.Unsupported(member, $"a computed key into the frozen object {named.Name}");

      // A member is never written nor compared by identity: a new object of its values is the member (#1371).
      return members.TryGetValue(label, out var values)
        ? new IrNew(IrType.Object(record.Ir), record.Creator!, [.. values.Select((value, index) => this.Value(value, record.Fields[index].Ir!.Type))])
        : throw this.Unsupported(member, $"{named.Name}.{label}, which is no member of the frozen object {named.Name}");
    }

    if (named.Type is null)
      throw this.Unsupported(member, named.Problem!);

    if (member.Computed || member.Property is not Identifier { Name: var key })
      throw this.Unsupported(member, $"a computed key into the enum object {named.Name}");

    return named.Type.Member(key) is { } found
      ? new IrConst(named.Type.Type, found.Value)
      : throw this.Unsupported(member, $"{named.Name}.{key}, which is no member of the enum object {named.Name}");
  }

  /// <summary>
  /// The name of the enum a statement declares as TypeScript writes one,
  /// <c>(function (E) { E[E["A"] = 0] = "A"; })(E || (E = {}));</c>, or <see langword="null"/>.
  /// </summary>
  private static string? TypeScriptEnum(Node statement)
    => statement is ExpressionStatement { Expression: CallExpression { Callee: FunctionExpression { Params.Count: 1 }, Arguments: [LogicalExpression { Operator: Acornima.Operator.LogicalOr, Left: Identifier { Name: var name } }] } }
      ? name
      : null;

}
