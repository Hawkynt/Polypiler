using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Python;

public static partial class PythonEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The nodes of the dynamic type, a tuple <c>(tag, payload)</c> (<c>(0,)</c> for <c>Empty</c>,
    /// <c>(1,)</c> for <c>Null</c>): Python's <c>int</c> cannot tell an <c>i16</c> from an <c>i32</c>,
    /// which the tag does. The tag is the first element, a tested payload the second, an untested one
    /// the helper <c>_ply_variant</c>, which raises the cast trap on another tag. A currency or a date has no form here.
    /// </summary>
    private PyExpression? VariantExpression(IrExpr expression) {
      switch (expression) {
        case IrVariantOf { Tag: IrVariantTag.Currency or IrVariantTag.Date } or IrVariantValue { Tag: IrVariantTag.Currency or IrVariantTag.Date }:
          return this.Inexpressible($"a variant of the tag {(expression as IrVariantOf)?.Tag ?? ((IrVariantValue)expression).Tag}");
        case IrVariantOf made:
          var tag = Integer((int)made.Tag, IrType.I32);
          return new PyTuple(made.Value is null ? [tag] : [tag, this.Expression(made.Value)]);
        case IrVariantTagOf read:
          return new PySubscript(this.Expression(read.Value), Integer(0, IrType.I32));
        case IrVariantValue { IsTested: true } value:
          return new PySubscript(this.Expression(value.Value), Integer(1, IrType.I32));
        case IrVariantValue value:
          return this.Helper(PythonHelpers.Variant, this.Expression(value.Value), Integer((int)value.Tag, IrType.I32));
        default:
          return null;
      }
    }

  }

}
