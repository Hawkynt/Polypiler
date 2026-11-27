namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  private sealed partial class FunctionCheck {

    /// <summary>
    /// The checks of the nodes of the dynamic type: a value is made from a tag of the enum with a
    /// payload of the tag's type, or with none for <c>Empty</c> and <c>Null</c>; only a variant has a
    /// tag and a payload, and a payload is read only of a tag that has one.
    /// </summary>
    private void Variant(IrExpr expression) {
      switch (expression) {
        case IrVariantOf made:
          var payload = System.Enum.IsDefined(made.Tag) ? IrVariants.PayloadType(made.Tag) : null;
          this.Require(System.Enum.IsDefined(made.Tag), $"a variant of the tag {(int)made.Tag}, which is none");
          if (made.Value is null) {
            this.Require(payload is null, $"a variant of the tag {made.Tag} without its payload");
            break;
          }

          this.Expression(made.Value);
          this.Require(payload is not null, $"a variant of the tag {made.Tag}, which has no payload, with one of the type {made.Value.Type}");
          this.Require(payload is null || made.Value.Type == payload, $"a variant of the tag {made.Tag} with a payload of the type {made.Value.Type}, not {payload}");
          break;
        case IrVariantTagOf tag:
          this.Expression(tag.Value);
          this.Require(tag.Value.Type.IsVariant, $"the tag of a value of the type {tag.Value.Type}, which is no variant");
          break;
        default:
          var value = (IrVariantValue)expression;
          this.Expression(value.Value);
          this.Require(value.Value.Type.IsVariant, $"the payload of a value of the type {value.Value.Type}, which is no variant");
          this.Require(System.Enum.IsDefined(value.Tag) && IrVariants.PayloadType(value.Tag) is not null, $"the payload of the tag {value.Tag}, which has none");
          break;
      }
    }

  }

}
