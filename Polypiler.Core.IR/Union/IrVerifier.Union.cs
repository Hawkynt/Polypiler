namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  private sealed partial class FunctionCheck {

    /// <summary>The rules of a union type wherever one is declared: two to four distinct members of the allowed kinds.</summary>
    private void UnionType(IrType type) {
      foreach (var problem in IrUnions.Problems(type))
        this.Require(false, problem);
    }

    /// <summary>
    /// The checks of the nodes of the closed tagged unions: a value is made from a member of a valid
    /// union with a value of exactly the member's type; only a union has a tag, and a payload is read
    /// only of a member the union has.
    /// </summary>
    private void Union(IrExpr expression) {
      switch (expression) {
        case IrUnionOf made:
          this.Expression(made.Value);
          this.Require(made.Union.IsUnion, $"a union value of the type {made.Union}, which is no union");
          this.UnionType(made.Union);
          var member = IrUnions.MemberType(made.Union, made.Member);
          this.Require(made.Union.IsUnion is false || member is not null, $"a value of the member {made.Member} of {made.Union}, which has none");
          this.Require(member is null || made.Value.Type == member, $"a value of the type {made.Value.Type} for the member {made.Member} of {made.Union}, which is {member}");
          break;
        case IrUnionTagOf tag:
          this.Expression(tag.Value);
          this.Require(tag.Value.Type.IsUnion, $"the tag of a value of the type {tag.Value.Type}, which is no union");
          break;
        default:
          var value = (IrUnionValue)expression;
          this.Expression(value.Value);
          this.Require(value.Value.Type.IsUnion, $"the payload of a value of the type {value.Value.Type}, which is no union");
          this.Require(value.Value.Type.IsUnion is false || IrUnions.MemberType(value.Value.Type, value.Member) is not null,
            $"the payload of the member {value.Member} of {value.Value.Type}, which has none");
          break;
      }
    }

  }

}
