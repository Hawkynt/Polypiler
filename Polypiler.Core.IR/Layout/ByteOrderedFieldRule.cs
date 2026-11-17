namespace Polypiler.Core.IR;

/// <summary>
/// A field of a stated byte order (#113, <see cref="IrField.ByteOrder"/>) is read and written in that order: an
/// ordered load or store at its address is of its order and of its type, and its address is not cast to a pointer
/// of another type, through which a plain load would read it in the profile's order.
/// </summary>
public sealed class ByteOrderedFieldRule : IrNodeRule<IrIntrinsic> {

  /// <inheritdoc/>
  public override string Id => "ir.verifier.records.field-byte-order";

  /// <inheritdoc/>
  public override string Family => "records";

  /// <inheritdoc/>
  public override string Description => "a field of a stated byte order is read and written in that order and as its type";

  /// <inheritdoc/>
  protected override void Check(VerifierRuleContext context, IrIntrinsic node) {
    if (node.Arguments is not [IrFieldAddress { Field: { ByteOrder: { } order } field }, ..])
      return;

    if (node.Op == IrIntrinsicOp.PointerCast) {
      context.Report(node, $"the address of '{field.Name}', stored {order}-endian, is cast to {node.Type}");
      return;
    }

    if (!IrOrderedAccess.IsOrdered(node.Op))
      return;

    var type = IrOrderedAccess.IsStore(node.Op) ? node.Arguments[^1].Type : node.Type;
    if (IrOrderedAccess.OrderOf(node.Op) != order || type != field.Type)
      context.Report(node, $"'{field.Name}', a {field.Type} stored {order}-endian, is accessed as a {type} by {node.Op}");
  }

}
