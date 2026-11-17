namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  private sealed partial class FunctionCheck {

    /// <summary>
    /// A layout query or a field address (#110): the type asked about has bytes, an offset names a field of its
    /// record, a size or an alignment names none, and a field address takes a <c>u8*</c> and names a field of a
    /// record the module declares. Whether the layout itself has a problem is the
    /// <see cref="RecordLayoutRule"/>'s to check.
    /// </summary>
    private void Layout(IrExpr expression) {
      switch (expression) {
        case IrLayoutOf query:
          this.Require(RecordLayout.HasBytes(query.Of) || (query.Of.IsPointer && query.Query != IrLayoutQuery.Offset), $"the {query.Query} of the type {query.Of}, which has no byte layout");
          if (query.Query == IrLayoutQuery.Offset) {
            if (this.Struct(query.Of) is { } owner)
              this.Require(query.Field is not null && owner.Owns(query.Field), $"the offset of '{query.Field?.Name}', which is not a field of {owner.Name}");
          } else {
            this.Require(query.Field is null, $"the {query.Query} of {query.Of} names the field '{query.Field?.Name}'");
            if (query.Of.IsStruct)
              this.Struct(query.Of);
          }

          break;
        case IrFieldAddress address:
          this.Expression(address.Base);
          this.Require(address.Base.Type == IrType.Pointer(IrType.U8), $"the address of a field is taken from a {address.Base.Type}, not a u8*");
          if (this.Struct(IrType.Struct(address.Record)) is { } record)
            this.Require(record.Owns(address.Field), $"the field '{address.Field.Name}' is not a field of {record.Name}");

          this.Require(address.Type == IrFieldAddress.AddressType(address.Field), $"the address of '{address.Field.Name}' has the type {address.Type}, expected {IrFieldAddress.AddressType(address.Field)}");
          break;
      }
    }

  }

}
