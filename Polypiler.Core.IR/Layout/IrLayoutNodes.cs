namespace Polypiler.Core.IR;

/// <summary>
/// The size or the alignment of a type, or the offset of a field of a record, in bytes, as an <c>i64</c> (#110):
/// <c>SizeOf</c>, <c>AlignOf</c> and <c>OffsetOf</c>. The answer is <see cref="RecordLayout.Answer"/> under the
/// profile of the target; it reads nothing and never traps. A query whose answer depends on the profile
/// (<see cref="RecordLayout.IsProfileIndependent"/>) is answered once the profile is known:
/// <see cref="IrLayoutLowering"/> replaces it by its value, and the interpreter answers it only when it is called
/// with a profile.
/// </summary>
/// <param name="Query">What is asked.</param>
/// <param name="Of">The type asked about: a truth value, a number, an enum, a pointer or a record; a record for an offset.</param>
/// <param name="Field">The field of <paramref name="Of"/> whose offset is asked; <see langword="null"/> for a size or an alignment.</param>
public sealed record IrLayoutOf(IrLayoutQuery Query, IrType Of, IrField? Field = null) : IrExpr(IrType.I64);

/// <summary>
/// The address of a field of a record that lies in memory (#110): <c>FieldAddr</c>. The base points at the first
/// byte of the record; the result points at the field, <see cref="RecordLayout.OffsetOf(IrField)"/> bytes further,
/// in the same region. It is a pointer to the field's number when the field is a number of the profile's byte
/// order, and a pointer to its bytes - <c>u8*</c> - for a truth value, a record, and a number of a stated
/// <see cref="IrField.ByteOrder"/>, which the ordered loads and stores read and write. Forming the address checks
/// nothing; the load or store through it checks the region, as for every pointer.
/// </summary>
/// <param name="Base">A <c>u8*</c> at the first byte of the record.</param>
/// <param name="Record">The record that lies there.</param>
/// <param name="Field">The field, one of the record's own.</param>
public sealed record IrFieldAddress(IrExpr Base, IrStructType Record, IrField Field) : IrExpr(AddressType(Field)) {

  /// <summary>The type of the address of a field: a pointer to the number for a number of the profile's order, <c>u8*</c> otherwise.</summary>
  /// <param name="field">The field.</param>
  /// <returns>The pointer type.</returns>
  public static IrType AddressType(IrField field)
    => IrType.Pointer(field.Type.IsNumeric && field.ByteOrder is null ? field.Type.Integral : IrType.U8);

}
