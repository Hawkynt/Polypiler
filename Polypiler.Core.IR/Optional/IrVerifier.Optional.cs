namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  private sealed partial class FunctionCheck {

    /// <summary>
    /// The checks of the nodes of optional values: none is of an optional type, a value made optional
    /// is a scalar or a struct, only an optional value is tested, unwrapped or coalesced - with a
    /// fallback of its own type or of the type it holds -, only a reference is tested for <c>null</c>,
    /// and an assignment inside an expression is one to a local in scope of its type.
    /// </summary>
    private void Optional(IrExpr expression) {
      switch (expression) {
        case IrNone none:
          this.Require(none.Type.IsOptional && none.Type.IsStorable, $"none of the type {none.Type}, which is not optional");
          this.Declared(none.Type);
          break;
        case IrSome some:
          this.Expression(some.Value);
          this.Require(some.Value.Type.IsOptionalValue, $"a value of the type {some.Value.Type} is made optional");
          break;
        case IrIsNone test:
          this.Expression(test.Value);
          this.Require(test.Value.Type.IsOptional, $"a value of the type {test.Value.Type}, which is not optional, is tested for none");
          break;
        case IrUnwrap unwrap:
          this.Expression(unwrap.Value);
          this.Require(unwrap.Value.Type.IsOptional, $"a value of the type {unwrap.Value.Type}, which is not optional, is unwrapped");
          break;
        case IrCoalesce coalesce:
          this.Expression(coalesce.Value);
          this.Expression(coalesce.Fallback);
          var type = coalesce.Value.Type;
          this.Require(type.IsOptional, $"a value of the type {type}, which is not optional, is coalesced");
          this.Require(!type.IsOptional || coalesce.Fallback.Type == type || coalesce.Fallback.Type == type.Underlying,
            $"a value of the type {type} is coalesced with a fallback of the type {coalesce.Fallback.Type}");
          break;
        case IrIsNull test:
          this.Expression(test.Value);
          this.Require(test.Value.Type.IsNullable, $"a value of the type {test.Value.Type}, which is no reference, is tested for null");
          break;
        default:
          var bind = (IrBind)expression;
          this.Require(bind.Local != this.Receiver, "the receiver of a method is assigned");
          this.InScope(bind.Local);
          this.Value(bind.Value, bind.Local.Type, $"the value assigned to '{bind.Local.Name}'");
          break;
      }
    }

  }

}
