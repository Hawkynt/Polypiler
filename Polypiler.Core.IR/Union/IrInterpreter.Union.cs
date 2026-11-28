namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>A value of a union type as the interpreter carries it: the index of its member and the member's value as the interpreter carries a value of that type.</summary>
  private sealed record Member(int Index, object Value);

  /// <summary>A value of a union type taken across the boundary: an <see cref="IrUnion"/> of a member of the type with a value of the member's type.</summary>
  private static object? AcceptedUnion(IrType type, object? value)
    => value is IrUnion union && IrUnions.MemberType(type, union.Member) is { Kind: not IrTypeKind.Void } member && Accepted(member, union.Value) is { } accepted
      ? new Member(union.Member, accepted)
      : null;

  /// <summary>A value of a union type given back across the boundary, its member's value as a .NET value of the member's type.</summary>
  private static IrUnion UnionToHost(IrType type, Member value) => new(value.Index, ToHost(IrUnions.MemberType(type, value.Index), value.Value));

  private sealed partial class Machine {

    /// <summary>The nodes of a union type: a value made of a member, the index of the member, and the member's value, which traps on another member.</summary>
    private object Union(IrExpr expression, Frame frame) {
      switch (expression) {
        case IrUnionOf made:
          return new Member(made.Member, this.Evaluate(made.Value, frame));
        case IrUnionTagOf tag:
          return (long)((Member)this.Evaluate(tag.Value, frame)).Index;
        default:
          var value = (IrUnionValue)expression;
          var held = (Member)this.Evaluate(value.Value, frame);
          return held.Index == value.Member ? held.Value : throw new IrTrapException(IrTrap.InvalidCast);
      }
    }

  }

}
