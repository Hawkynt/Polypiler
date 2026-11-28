using System;
using System.Linq;
using Polypiler.Core.Analysis.Dynamic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VisualBasic.Lowering;

internal sealed partial class VbaLowerer {

  /// <summary>
  /// The semantics table of classic Visual Basic (#1584, ADR 0300 decision 4): the kinds a
  /// <c>Variant</c> operation gives - the very rules the lowering computes them by, so the table and the
  /// frontend cannot disagree - Visual Basic's errors, and the typed IR of the pairs that have one.
  /// Kinds map to the lattice as <c>Integer</c> <c>i16</c>, <c>Long</c> <c>i32</c>, <c>LongLong</c>
  /// <c>i64</c>, <c>Single</c> <c>f32</c>, <c>Double</c> <c>f64</c>, <c>Byte</c> <c>u8</c>,
  /// <c>Boolean</c> <c>bool</c>, <c>String</c> <c>str</c>, <c>Currency</c> an <c>i64</c> and <c>Date</c>
  /// an <c>f64</c> with their kind, <c>Empty</c> and <c>Null</c> the missing values. Every two kinds are
  /// told apart (<c>VarType</c> does), so a join of two is a union.
  /// </summary>
  internal sealed class Semantics : IDynamicSemantics {

    private Semantics() { }

    /// <summary>The table.</summary>
    public static Semantics Instance { get; } = new();

    /// <inheritdoc/>
    public string Language => "visualbasic";

    /// <inheritdoc/>
    public IJoinPolicy Joins => DistinctJoins.Instance;

    /// <summary>The kinds of a value from outside, of which the table knows nothing narrower.</summary>
    private const VbaTags Unknown = VbaTags.Any | VbaTags.Currency | VbaTags.LongLong;

    #region Elements and kinds

    /// <summary>The kinds an element allows; <see cref="Unknown"/> for one the table does not know, such as a shape or an array.</summary>
    internal static VbaTags Tags(DynamicType type) => type switch {
      DynamicNothing => VbaTags.None,
      DynamicExact exact => exact.Tag switch {
        IrVariantTag.Currency => VbaTags.Currency,
        IrVariantTag.Date => VbaTags.Date,
        _ => exact.Type.Kind switch {
          IrTypeKind.Bool => VbaTags.Boolean,
          IrTypeKind.U8 => VbaTags.Byte,
          IrTypeKind.I16 when !exact.Type.IsEnum => VbaTags.Integer,
          IrTypeKind.I32 when !exact.Type.IsEnum => VbaTags.Long,
          IrTypeKind.I64 => VbaTags.LongLong,
          IrTypeKind.F32 => VbaTags.Single,
          IrTypeKind.F64 => VbaTags.Double,
          IrTypeKind.String => VbaTags.String,
          _ => Unknown
        }
      },
      DynamicOptional optional => Tags(optional.Value)
        | ((optional.Missing & DynamicMissing.Empty) != 0 ? VbaTags.Empty : VbaTags.None)
        | ((optional.Missing & DynamicMissing.Null) != 0 ? VbaTags.Null : VbaTags.None)
        | ((optional.Missing & DynamicMissing.Undefined) != 0 ? Unknown : VbaTags.None),
      DynamicUnion union => union.Members.Aggregate(VbaTags.None, (tags, member) => tags | Tags(member)),
      _ => Unknown
    };

    /// <summary>The element of a set of kinds: each kind exact, <c>Empty</c> and <c>Null</c> missing, more than four kinds a variant.</summary>
    internal static DynamicType Element(VbaTags tags) {
      if ((tags & (VbaTags.ArrayFrom0 | VbaTags.ArrayFrom1)) != VbaTags.None)
        return DynamicType.Variant;
      var missing = ((tags & VbaTags.Empty) != 0 ? DynamicMissing.Empty : DynamicMissing.None) | ((tags & VbaTags.Null) != 0 ? DynamicMissing.Null : DynamicMissing.None);
      var members = Each(tags & ~(VbaTags.Empty | VbaTags.Null)).Select(Kind).ToArray();
      return DynamicType.Optional(DynamicType.Union(DistinctJoins.Instance, members), missing);
    }

    /// <summary>The element of one kind of value.</summary>
    private static DynamicType Kind(VbaTags tag) => tag switch {
      VbaTags.Boolean => DynamicType.Exact(IrType.Bool),
      VbaTags.Byte => DynamicType.Exact(IrType.U8),
      VbaTags.Integer => DynamicType.Exact(IrType.I16),
      VbaTags.Long => DynamicType.Exact(IrType.I32),
      VbaTags.LongLong => DynamicType.Exact(IrType.I64),
      VbaTags.Single => DynamicType.Exact(IrType.F32),
      VbaTags.Double => DynamicType.Exact(IrType.F64),
      VbaTags.String => DynamicType.Exact(IrType.String),
      VbaTags.Currency => DynamicType.Exact(IrType.I64, IrVariantTag.Currency),
      _ => DynamicType.Exact(IrType.F64, IrVariantTag.Date)
    };

    #endregion

    /// <inheritdoc/>
    public DynamicType Result(DynamicOperation operation, DynamicType left, DynamicType right) {
      ArgumentNullException.ThrowIfNull(left);
      ArgumentNullException.ThrowIfNull(right);
      return Results(operation, Tags(left), Tags(right)) is { } tags ? Element(tags) : operation switch {
        DynamicOperation.Truthiness => DynamicType.Exact(IrType.Bool),
        DynamicOperation.ToNumber => DynamicType.Exact(IrType.F64),
        DynamicOperation.ToString => DynamicType.Exact(IrType.String),
        _ => DynamicType.Variant
      };
    }

    /// <summary>
    /// The kinds an operation of operands of two sets of kinds gives, as the lowering computes them;
    /// <see langword="null"/> for an operation that gives no <c>Variant</c>, and <see cref="Unknown"/>
    /// for one the lowering refuses (a <c>LongLong</c> in arithmetic, a <c>Currency</c> in <c>^</c>,
    /// <c>\</c>, <c>Mod</c> and the bitwise operators).
    /// </summary>
    internal static VbaTags? Results(DynamicOperation operation, VbaTags a, VbaTags b) {
      var both = a | b;
      var nulls = both & VbaTags.Null;
      var refused = (both & (VbaTags.LongLong | VbaTags.ArrayFrom0 | VbaTags.ArrayFrom1)) != VbaTags.None;
      var currency = (both & VbaTags.Currency) != VbaTags.None;
      switch (operation) {
        case DynamicOperation.Add or DynamicOperation.Subtract or DynamicOperation.Multiply:
          return refused ? Unknown : ArithmeticTags(operation == DynamicOperation.Add ? "+" : operation == DynamicOperation.Subtract ? "-" : "*", a, b);
        case DynamicOperation.Divide:
          return refused ? Unknown : VbaTags.Double | ((both & VbaTags.Single) != VbaTags.None ? VbaTags.Single : VbaTags.None) | nulls;
        case DynamicOperation.Power:
          return refused || currency ? Unknown : VbaTags.Double | nulls;
        case DynamicOperation.IntegerDivide or DynamicOperation.Modulo:
          return refused || currency ? Unknown : IntegerDivisionTags(a, b);
        case DynamicOperation.Concatenate:
          return currency && (a & b & VbaTags.Null) != VbaTags.None ? Unknown : VbaTags.String | (a & b & VbaTags.Null);
        case DynamicOperation.Equal or DynamicOperation.NotEqual or DynamicOperation.Less or DynamicOperation.LessOrEqual or DynamicOperation.Greater or DynamicOperation.GreaterOrEqual:
          return VbaTags.Boolean | nulls;
        case DynamicOperation.Negate:
          return refused ? Unknown : NegationTags(a);
        case DynamicOperation.Not:
          return refused || currency ? Unknown : (a & ~(VbaTags.Boolean | VbaTags.Null)) == VbaTags.None ? a : Each(a).Aggregate(VbaTags.None, (kinds, tag) => kinds | BitwiseKind(tag));
        case DynamicOperation.And or DynamicOperation.Or or DynamicOperation.Xor or DynamicOperation.Eqv or DynamicOperation.Imp:
          if (refused || currency)
            return Unknown;
          return (both & ~(VbaTags.Boolean | VbaTags.Null)) == VbaTags.None && operation is DynamicOperation.And or DynamicOperation.Or or DynamicOperation.Xor
            ? VbaTags.Boolean | nulls
            : BitwiseTags(a, b);
        case DynamicOperation.Index or DynamicOperation.MemberGet or DynamicOperation.MemberSet or DynamicOperation.Call:
          return Unknown;
        default:
          return null;
      }
    }

    /// <inheritdoc/>
    public DynamicTrap Trap(DynamicOperation operation, DynamicType left, DynamicType right) {
      ArgumentNullException.ThrowIfNull(left);
      ArgumentNullException.ThrowIfNull(right);
      var a = Tags(left);
      var b = Tags(right);
      var both = a | b;
      var text = (both & VbaTags.String) != VbaTags.None;
      // A string in arithmetic is read as a number: one that is none raises error 13 (Type mismatch).
      var mismatch = text && !(operation == DynamicOperation.Add && (a & ~VbaTags.String) == VbaTags.None && (b & ~VbaTags.String) == VbaTags.None);
      return operation switch {
        DynamicOperation.Concatenate or DynamicOperation.ToString
          or DynamicOperation.Equal or DynamicOperation.NotEqual or DynamicOperation.Less or DynamicOperation.LessOrEqual or DynamicOperation.Greater or DynamicOperation.GreaterOrEqual => DynamicTrap.None,
        _ when mismatch => DynamicTrap.Error(13),
        // Error 11: division by zero.
        DynamicOperation.Divide or DynamicOperation.IntegerDivide or DynamicOperation.Modulo => DynamicTrap.Error(11),
        // Error 5: a negative number to a power that is no integer.
        DynamicOperation.Power => DynamicTrap.Error(5),
        // Error 6: an overflow, of a Double, a Single, a Currency or a Date; the integers promote instead.
        DynamicOperation.Add or DynamicOperation.Subtract or DynamicOperation.Multiply
          when (Results(operation, a, b)!.Value & (VbaTags.Float | VbaTags.Currency | VbaTags.Date)) != VbaTags.None => DynamicTrap.Error(6),
        // Error 6 too: a number beyond a Long in a bitwise operator.
        DynamicOperation.Not or DynamicOperation.And or DynamicOperation.Or or DynamicOperation.Xor or DynamicOperation.Eqv or DynamicOperation.Imp
          when (both & (VbaTags.Float | VbaTags.Date)) != VbaTags.None => DynamicTrap.Error(6),
        DynamicOperation.Index or DynamicOperation.MemberGet or DynamicOperation.MemberSet or DynamicOperation.Call => DynamicTrap.Error(13),
        _ => DynamicTrap.None
      };
    }

    /// <inheritdoc/>
    public IrExpr? Expand(DynamicOperation operation, IrExpr left, DynamicType leftType, IrExpr? right, DynamicType rightType) {
      ArgumentNullException.ThrowIfNull(left);
      ArgumentNullException.ThrowIfNull(leftType);
      ArgumentNullException.ThrowIfNull(rightType);
      if (this.Result(operation, leftType, rightType) is not DynamicExact result)
        return null;

      var a = leftType as DynamicExact;
      var b = rightType as DynamicExact;
      switch (operation) {
        case DynamicOperation.Add or DynamicOperation.Subtract or DynamicOperation.Multiply when result.Type == IrType.F64 && result.Tag is null && Real(left, a) is { } x && Real(right, b) is { } y:
          // A Double result raises error 6 where it overflows, as the typed lowering does.
          var op = operation == DynamicOperation.Add ? IrBinaryOp.Add : operation == DynamicOperation.Subtract ? IrBinaryOp.Subtract : IrBinaryOp.Multiply;
          return new IrCall("PlyCheckD", IrType.F64, [new IrBinary(op, x, y)]);
        case DynamicOperation.Add when result.Type.IsString && a is { Type.IsString: true } && b is { Type.IsString: true }:
          return new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [left, right!]);
        case DynamicOperation.Divide when result.Type == IrType.F64 && Real(left, a) is { } x && Real(right, b) is { } y:
          return new IrCall("PlyDivD", IrType.F64, [x, y]);
        case DynamicOperation.Power when Real(left, a) is { } x && Real(right, b) is { } y:
          return new IrCall("PlyPow", IrType.F64, [x, y]);
        case DynamicOperation.Concatenate when a is { Type.IsString: true } && b is { Type.IsString: true }:
          return new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [left, right!]);
        case DynamicOperation.Equal or DynamicOperation.NotEqual or DynamicOperation.Less or DynamicOperation.LessOrEqual or DynamicOperation.Greater or DynamicOperation.GreaterOrEqual
          when Real(left, a) is { } x && Real(right, b) is { } y:
          return new IrBinary(Comparison(operation), x, y);
        case DynamicOperation.Negate when a is { Type.Kind: IrTypeKind.F64, Tag: null }:
          return new IrUnary(IrUnaryOp.Negate, left);
        case DynamicOperation.Truthiness when a is { Type.Kind: IrTypeKind.Bool }:
          return left;
        case DynamicOperation.Truthiness when Real(left, a) is { } x:
          return new IrBinary(IrBinaryOp.NotEqual, x, new IrConst(IrType.F64, BitConverter.DoubleToInt64Bits(0.0)));
        case DynamicOperation.ToNumber when Real(left, a) is { } x:
          return x;
        case DynamicOperation.ToString when a is { Type.IsString: true }:
          return left;
        default:
          return null;
      }
    }

    /// <summary>
    /// An operand as a <c>Double</c> where that is exact and is what Visual Basic computes with: a
    /// <c>Double</c> itself, and a <c>Byte</c>, an <c>Integer</c>, a <c>Long</c> or a <c>Single</c>
    /// converted; <see langword="null"/> for every other element, a <c>Boolean</c>, a <c>Currency</c>
    /// and a <c>Date</c> among them.
    /// </summary>
    private static IrExpr? Real(IrExpr? operand, DynamicExact? element) => element switch {
      { Tag: not null } or null => null,
      { Type.Kind: IrTypeKind.F64 } => operand,
      { Type.Kind: IrTypeKind.U8 or IrTypeKind.I16 or IrTypeKind.I32 or IrTypeKind.F32 } when !element.Type.IsEnum => new IrConvert(IrType.F64, operand!, IrConversion.Wrap),
      _ => null
    };

    private static IrBinaryOp Comparison(DynamicOperation operation) => operation switch {
      DynamicOperation.Equal => IrBinaryOp.Equal,
      DynamicOperation.NotEqual => IrBinaryOp.NotEqual,
      DynamicOperation.Less => IrBinaryOp.Less,
      DynamicOperation.LessOrEqual => IrBinaryOp.LessOrEqual,
      DynamicOperation.Greater => IrBinaryOp.Greater,
      _ => IrBinaryOp.GreaterOrEqual
    };

    /// <inheritdoc/>
    public DynamicCall? Classify(IrExpr expression) {
      ArgumentNullException.ThrowIfNull(expression);
      switch (expression) {
        case IrBinary { Op: var op, Left: IrCall { Function: "PlyVarCmp" or "PlyVarCmpT" or "PlyVarCmpC", Arguments: [var a, var b] }, Right: IrConst { Value: 0 } }
          when op is IrBinaryOp.Equal or IrBinaryOp.NotEqual or IrBinaryOp.Less or IrBinaryOp.LessOrEqual or IrBinaryOp.Greater or IrBinaryOp.GreaterOrEqual:
          return new(Compared(op), a, b);
        case IrCall { Function: "PlyVarCmpOp", Arguments: [IrCall { Function: "PlyVarCmp" or "PlyVarCmpT" or "PlyVarCmpC", Arguments: [var a, var b] }, IrConst code] } when code.Value is >= 0 and < 6:
          return new((DynamicOperation)((int)DynamicOperation.Equal + (int)code.Value), a, b);
        case IrCall { Function: "PlyVarArith" or "PlyVarArithC", Arguments: [IrConst code, var a, var b] } when code.Value is >= 0 and <= 2:
          return new(code.Value == 0 ? DynamicOperation.Add : code.Value == 1 ? DynamicOperation.Subtract : DynamicOperation.Multiply, a, b);
        case IrCall { Function: "PlyVarDiv" or "PlyVarDivC", Arguments: [var a, var b] }:
          return new(DynamicOperation.Divide, a, b);
        case IrCall { Function: "PlyVarPow", Arguments: [var a, var b] }:
          return new(DynamicOperation.Power, a, b);
        case IrCall { Function: "PlyVarIntDiv", Arguments: [IrConst code, var a, var b] }:
          return new(code.Value == 1 ? DynamicOperation.Modulo : DynamicOperation.IntegerDivide, a, b);
        case IrCall { Function: "PlyVarConcat", Arguments: [var a, var b] }:
          return new(DynamicOperation.Concatenate, a, b);
        case IrCall { Function: "PlyVarNeg" or "PlyVarNegC", Arguments: [var a] }:
          return new(DynamicOperation.Negate, a, null);
        case IrCall { Function: "PlyVarNot" or "PlyVarBitNot", Arguments: [var a] }:
          return new(DynamicOperation.Not, a, null);
        case IrCall { Function: "PlyVarLogic", Arguments: [IrConst code, var a, var b] } when code.Value is >= 0 and <= 2:
          return new((DynamicOperation)((int)DynamicOperation.And + code.Value), a, b);
        case IrCall { Function: "PlyVarBits", Arguments: [IrConst code, var a, var b] } when code.Value is >= 0 and <= 4:
          return new((DynamicOperation)((int)DynamicOperation.And + code.Value), a, b);
        case IrCall { Function: "PlyVarIsTrue", Arguments: [var a] }:
          return new(DynamicOperation.Truthiness, a, null);
        case IrCall { Function: "PlyVarDbl" or "PlyVarDblC", Arguments: [var a] }:
          return new(DynamicOperation.ToNumber, a, null);
        case IrCall { Function: "PlyVarCStr" or "PlyVarCStrC", Arguments: [var a] }:
          return new(DynamicOperation.ToString, a, null);
        default:
          return null;
      }
    }

    /// <inheritdoc/>
    /// <remarks>The helpers of the lowering are named <c>Ply</c> and a capital letter, as <c>PlyVarArith</c> and <c>PlyCheckD</c>.</remarks>
    public bool IsHelper(string function) {
      ArgumentNullException.ThrowIfNull(function);
      return function.Length > 3 && function.StartsWith("Ply", StringComparison.Ordinal) && char.IsUpper(function[3]);
    }

    /// <inheritdoc/>
    public string Name(DynamicExact element) {
      ArgumentNullException.ThrowIfNull(element);
      return Tags(element) is var tags && IsSingle(tags) ? Each(tags).Single().ToString() : element.Type.ToString();
    }

    private static DynamicOperation Compared(IrBinaryOp op) => (DynamicOperation)((int)DynamicOperation.Equal + Array.IndexOf(_comparisonOps, op));

    private static readonly IrBinaryOp[] _comparisonOps = [IrBinaryOp.Equal, IrBinaryOp.NotEqual, IrBinaryOp.Less, IrBinaryOp.LessOrEqual, IrBinaryOp.Greater, IrBinaryOp.GreaterOrEqual];

  }

}
