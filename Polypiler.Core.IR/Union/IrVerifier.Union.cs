namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  private sealed partial class FunctionCheck {

    /// <summary>
    /// The checks of the nodes of a union type: a value is made of a valid union from a value of exactly
    /// the type of one of its members; only a union has a tag and a member, and a member is read by an
    /// index the union has.
    /// </summary>
    private void Union(IrExpr expression) {
      switch (expression) {
        case IrUnionOf made:
          this.Expression(made.Value);
          if (!made.Union.IsUnion) {
            this.Require(false, $"a union value of the type {made.Union}, which is no union");
            break;
          }

          this.Declared(made.Union);
          var member = IrUnions.MemberType(made.Union, made.Member);
          this.Require(member != IrType.Void, $"a value of member {made.Member} of {made.Union}, which has {made.Union.UnionType!.Members.Count}");
          this.Require(member == IrType.Void || made.Value.Type == member, $"a value of member {made.Member} of {made.Union} of the type {made.Value.Type}, not {member}");
          break;
        case IrUnionTagOf tag:
          this.Expression(tag.Value);
          this.Require(tag.Value.Type.IsUnion, $"the tag of a value of the type {tag.Value.Type}, which is no union");
          break;
        default:
          var value = (IrUnionValue)expression;
          this.Expression(value.Value);
          this.Require(value.Value.Type.IsUnion, $"the member of a value of the type {value.Value.Type}, which is no union");
          this.Require(!value.Value.Type.IsUnion || value.Type != IrType.Void, $"member {value.Member} of {value.Value.Type}, which it does not have");
          break;
      }
    }

  }

}
