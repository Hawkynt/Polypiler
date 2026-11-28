using System;

namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>A value of a closed tagged union as the interpreter carries it: the member's index and the payload as the interpreter carries a value of the member's type.</summary>
  private sealed record Held(int Member, object Payload);

  /// <summary>A value of a union taken across the boundary: an <see cref="IrUnion"/> of a member the type has, whose payload is a value of that member's type.</summary>
  private static object? AcceptedUnion(IrType type, object? value)
    => value is IrUnion union && IrUnions.MemberType(type, union.Member) is { } member && Accepted(member, union.Value) is { } payload ? new Held(union.Member, payload) : null;

  /// <summary>A value of a union given back across the boundary, its payload as a .NET value of the member's type.</summary>
  private static IrUnion UnionToHost(IrType type, Held value) => new(value.Member, ToHost(IrUnions.MemberType(type, value.Member)!, value.Payload));

  /// <summary>
  /// Whether two values of one union are equal: they hold the same member, and the payloads are equal
  /// as <c>==</c> compares that member's type - a float as IEEE 754 does (a NaN equals nothing), a
  /// string by its code units, an object by its identity, a struct field by field, an optional when
  /// both are none or both hold equal values.
  /// </summary>
  private static bool UnionEquals(IrType type, Held left, Held right)
    => left.Member == right.Member && PayloadEquals(IrUnions.MemberType(type, left.Member)!, left.Payload, right.Payload);

  private static bool PayloadEquals(IrType type, object left, object right) {
    if (type.IsOptional)
      return left is Absent || right is Absent ? left is Absent && right is Absent : PayloadEquals(type.Underlying!, left, right);
    if (type == IrType.F32)
      return (float)left == (float)right;
    if (type == IrType.F64)
      return (double)left == (double)right;
    if (type.StructType is { } declared) {
      var (first, second) = ((Record)left, (Record)right);
      for (var index = 0; index < declared.Fields.Count; ++index)
        if (!PayloadEquals(declared.Fields[index].Type, first.Fields[index], second.Fields[index]))
          return false;

      return true;
    }

    return type.IsObject ? ReferenceEquals(left, right) : left.Equals(right);
  }

  private sealed partial class Machine {

    /// <summary>The nodes of the closed tagged unions: a value made from a member, its tag, and its payload, which traps on another member.</summary>
    private object Union(IrExpr expression, Frame frame) {
      switch (expression) {
        case IrUnionOf made:
          return new Held(made.Member, this.Evaluate(made.Value, frame));
        case IrUnionTagOf tag:
          return (long)((Held)this.Evaluate(tag.Value, frame)).Member;
        default:
          var value = (IrUnionValue)expression;
          var held = (Held)this.Evaluate(value.Value, frame);
          return held.Member == value.Member ? held.Payload : throw new IrTrapException(IrTrap.InvalidCast);
      }
    }

  }

}
