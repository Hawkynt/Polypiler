using System;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VisualBasic.Lowering;

/// <summary>The kinds of value of the subset.</summary>
internal enum VbaKind { Boolean, Byte, Integer, Long, LongLong, Single, Double, String, Record, Array }

/// <summary>
/// A type of the subset: a scalar, a user-defined type, or an array of one with its lower bound,
/// which Visual Basic carries with the array and the IR's arrays, all starting at 0, do not.
/// </summary>
internal sealed record VbaType(VbaKind Kind, IrStructType? Record = null, VbaType? Element = null, int LowerBound = 0) {

  public static VbaType Boolean { get; } = new(VbaKind.Boolean);

  public static VbaType Byte { get; } = new(VbaKind.Byte);

  public static VbaType Integer { get; } = new(VbaKind.Integer);

  public static VbaType Long { get; } = new(VbaKind.Long);

  public static VbaType LongLong { get; } = new(VbaKind.LongLong);

  public static VbaType Single { get; } = new(VbaKind.Single);

  public static VbaType Double { get; } = new(VbaKind.Double);

  public static VbaType String { get; } = new(VbaKind.String);

  public static VbaType ArrayOf(VbaType element, int lowerBound) => new(VbaKind.Array, null, element, lowerBound);

  public bool IsInteger => this.Kind is VbaKind.Byte or VbaKind.Integer or VbaKind.Long or VbaKind.LongLong;

  public bool IsFloat => this.Kind is VbaKind.Single or VbaKind.Double;

  /// <summary>Whether arithmetic takes the type: the numbers and <c>Boolean</c>, which counts as an <c>Integer</c>.</summary>
  public bool IsNumeric => this.IsInteger || this.IsFloat || this.Kind == VbaKind.Boolean;

  public bool IsArray => this.Kind == VbaKind.Array;

  /// <summary>The IR type that carries a value.</summary>
  public IrType Ir => this.Kind switch {
    VbaKind.Boolean => IrType.Bool,
    VbaKind.Byte => IrType.U8,
    VbaKind.Integer => IrType.I16,
    VbaKind.Long => IrType.I32,
    VbaKind.LongLong => IrType.I64,
    VbaKind.Single => IrType.F32,
    VbaKind.Double => IrType.F64,
    VbaKind.String => IrType.String,
    VbaKind.Record => IrType.Struct(this.Record!),
    _ => IrType.Array(this.Element!.Ir)
  };

  /// <summary>The name Visual Basic gives the type.</summary>
  public override string ToString() => this.Kind switch {
    VbaKind.Record => this.Record!.Name,
    VbaKind.Array => this.Element + "()",
    _ => this.Kind.ToString()
  };

  /// <summary>The rank of a number in the widening order Byte, Integer, Long, LongLong, Single, Double.</summary>
  public int Rank => this.Kind switch {
    VbaKind.Byte => 1,
    VbaKind.Boolean or VbaKind.Integer => 2,
    VbaKind.Long => 3,
    VbaKind.LongLong => 4,
    VbaKind.Single => 5,
    VbaKind.Double => 6,
    _ => throw new InvalidOperationException($"'{this}' is no number")
  };

}

/// <summary>A lowered expression with its Visual Basic type.</summary>
internal readonly record struct VbaValue(IrExpr Expr, VbaType Type);
