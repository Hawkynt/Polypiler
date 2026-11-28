using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The nodes of the dynamic type, an <c>object</c>: a value is the payload boxed as its own type
    /// (cast to it first, so a literal boxes as the tag's type), <c>Empty</c> <c>null</c> and
    /// <c>Null</c> <c>System.DBNull.Value</c>; the tag is the type of the box; a payload is the unboxing
    /// cast, through <see cref="CSharpVariantHelpers.Payload"/> where the frontend has not tested the tag.
    /// </summary>
    private (CsExpression Node, int Binding) Variant(IrExpr expression) {
      switch (expression) {
        case IrVariantOf { Tag: IrVariantTag.Empty }:
          return (new CsCast(new CsType("object"), new CsNullLiteral()) { Type = IrType.Variant }, Cast);
        case IrVariantOf { Tag: IrVariantTag.Null }:
          return (CSharpFragments.Expression("global::System.DBNull.Value") with { Type = IrType.Variant }, Primary);
        case IrVariantOf { Tag: IrVariantTag.Currency } currency:
          var amount = new CsBinary(new CsCast(new CsType("decimal"), this.Operand(currency.Value!, Cast)), "/", new CsCast(new CsType("decimal"), new CsIntegerLiteral(10000)));
          return (new CsCast(new CsType("object"), new CsParenthesized(amount)) { Type = IrType.Variant }, Cast);
        case IrVariantOf { Tag: IrVariantTag.Date } date:
          return (new CsCast(new CsType("object"), new CsObjectCreation(new CsType(this.Nested(CSharpCollectionClasses.VariantDateName)), [this.Text(date.Value!)])) { Type = IrType.Variant }, Cast);
        case IrVariantOf made:
          var type = TypeNameOf(made.Value!.Type);
          var operand = this.Operand(made.Value, Cast);
          var payload = operand is CsCast cast && cast.TargetType.Name == type ? operand : new CsCast(new CsType(type), operand) { Type = made.Value.Type };
          return (new CsCast(new CsType("object"), payload) { Type = IrType.Variant }, Cast);
        case IrVariantTagOf tag:
          this.Nested(CSharpCollectionClasses.VariantDateName);
          return (new CsInvocation(CSharpFragments.Name(this.Helper(CSharpVariantHelpers.Tag)), [this.Text(tag.Value)]) { Type = IrType.I32 }, Primary);
        default:
          var value = (IrVariantValue)expression;
          CsExpression boxed = value.IsTested ? this.Operand(value.Value, Cast)
            : new CsInvocation(CSharpFragments.Name(this.Helper(CSharpVariantHelpers.Payload)), [this.Text(value.Value)]);
          if (value.Tag == IrVariantTag.Currency)
            return (new CsCast(new CsType("long"), new CsParenthesized(new CsBinary(new CsCast(new CsType("decimal"), boxed), "*", new CsCast(new CsType("decimal"), new CsIntegerLiteral(10000))))) { Type = value.Type }, Cast);
          if (value.Tag == IrVariantTag.Date)
            return (new CsMemberAccess(new CsParenthesized(new CsCast(new CsType(this.Nested(CSharpCollectionClasses.VariantDateName)), boxed)), "Value") { Type = value.Type }, Primary);
          return (new CsCast(new CsType(TypeNameOf(value.Type)), boxed) { Type = value.Type }, Cast);
      }
    }

  }

}
