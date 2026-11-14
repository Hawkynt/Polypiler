using System;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

/// <summary>The kinds of value the importer tells apart.</summary>
internal enum JsKind {

  /// <summary>Nothing is known yet: no value has reached the place.</summary>
  Unknown,

  /// <summary>No value: what a function returns that returns nothing.</summary>
  Undefined,

  /// <summary>A truth value.</summary>
  Boolean,

  /// <summary>A number: a double, or an integer the program proves.</summary>
  Number,

  /// <summary>A <c>BigInt</c>.</summary>
  BigInt,

  /// <summary>An array.</summary>
  Array,

  /// <summary>An object literal of fixed shape that is never written, compared or extended: a value record.</summary>
  Record,

  /// <summary>A string: a sequence of UTF-16 code units.</summary>
  String,

  /// <summary>Values of two kinds reach the place, which no type of the IR holds.</summary>
  Conflict

}

/// <summary>
/// What is proven about the values of an expression or a variable. A number is a double unless every
/// value is an integer of a known range; the range of an integer is the range of the IR type that
/// produced it, never a guess from the values a program happens to use. A literal alone commits to
/// no width: it takes the type of whatever it is joined with, when that type holds it.
/// </summary>
internal sealed record JsType {

  private static readonly IrType[] _words = [IrType.U8, IrType.I8, IrType.U16, IrType.I16, IrType.I32, IrType.U32];
  private static readonly IrType[] _longs = [IrType.I64, IrType.U64];

  private JsType(JsKind kind) => this.Kind = kind;

  /// <summary>The kind of the values.</summary>
  public JsKind Kind { get; }

  /// <summary>Whether a number is not a proven integer: 32 for a binary32 number, 64 for a double, 0 for an integer.</summary>
  public int Float { get; private init; }

  /// <summary>The smallest value of an integer or a bounded <c>BigInt</c>.</summary>
  public Int128 Low { get; private init; }

  /// <summary>The largest value of an integer or a bounded <c>BigInt</c>.</summary>
  public Int128 High { get; private init; }

  /// <summary>Whether the range comes from literals only, which fit any type that holds them.</summary>
  public bool IsLiteral { get; private init; }

  /// <summary>Whether a <c>BigInt</c> has no bound, so that no type of the IR holds it.</summary>
  public bool IsUnbounded { get; private init; }

  /// <summary>The type of the elements of an array.</summary>
  public JsType? Element { get; private init; }

  /// <summary>Whether an array is a JavaScript <c>Array</c>, which stores a value as it is, rather than a typed array, which converts it.</summary>
  public bool IsPlain { get; private init; }

  /// <summary>The keys and value types of a record.</summary>
  public RecordShape? Shape { get; private init; }

  /// <summary>The two kinds of a conflict, as a diagnostic names them.</summary>
  public (string First, string Second) Kinds { get; private init; }

  /// <summary>Nothing known yet.</summary>
  public static JsType Unknown { get; } = new(JsKind.Unknown);

  /// <summary>No value.</summary>
  public static JsType Undefined { get; } = new(JsKind.Undefined);

  /// <summary>A truth value.</summary>
  public static JsType Boolean { get; } = new(JsKind.Boolean);

  /// <summary>A double.</summary>
  public static JsType F64 { get; } = new(JsKind.Number) { Float = 64 };

  /// <summary>A binary32 number.</summary>
  public static JsType F32 { get; } = new(JsKind.Number) { Float = 32 };

  /// <summary>A signed 32-bit integer.</summary>
  public static JsType I32 { get; } = Of(IrType.I32);

  /// <summary>An unsigned 32-bit integer.</summary>
  public static JsType U32 { get; } = Of(IrType.U32);

  /// <summary>A string.</summary>
  public static JsType String { get; } = new(JsKind.String);

  /// <summary>A <c>BigInt</c> without a bound.</summary>
  public static JsType UnboundedBigInt { get; } = new(JsKind.BigInt) { IsUnbounded = true };

  /// <summary>The type of the values an IR type holds; an array of it is a typed array.</summary>
  public static JsType Of(IrType type) => type.Kind switch {
    IrTypeKind.Void => Undefined,
    IrTypeKind.Bool => Boolean,
    IrTypeKind.F32 => F32,
    IrTypeKind.F64 => F64,
    IrTypeKind.String => String,
    // An array of strings is a JavaScript Array: no typed array holds a string.
    IrTypeKind.Array => ArrayOf(Of(type.Element!), type.Element!.IsString),
    _ => new(type.IsLong ? JsKind.BigInt : JsKind.Number) { Low = IrOperators.Minimum(type), High = IrOperators.Maximum(type) }
  };

  /// <summary>The type of a numeric literal: an integer when it is one of 32 bits, otherwise a double. The negative zero is a double.</summary>
  public static JsType Literal(double value)
    => value == Math.Floor(value) && value >= int.MinValue && value <= uint.MaxValue && !(value == 0 && double.IsNegative(value))
      ? new(JsKind.Number) { Low = (Int128)value, High = (Int128)value, IsLiteral = true }
      : F64;

  /// <summary>The type of a <c>BigInt</c> literal: bounded when 64 bits hold it.</summary>
  public static JsType BigLiteral(System.Numerics.BigInteger value)
    => value >= long.MinValue && value <= ulong.MaxValue
      ? new(JsKind.BigInt) { Low = (Int128)value, High = (Int128)value, IsLiteral = true }
      : UnboundedBigInt;

  /// <summary>An array type.</summary>
  public static JsType ArrayOf(JsType element, bool plain) => new(JsKind.Array) { Element = element, IsPlain = plain };

  /// <summary>The type of a record of a shape.</summary>
  public static JsType Record(RecordShape shape) => new(JsKind.Record) { Shape = shape };

  /// <summary>Whether the type is a record or holds one, as an element or a value: such a type has its struct only once the shapes are named.</summary>
  public bool HasRecord => this.Kind == JsKind.Record || this.Element is { HasRecord: true };

  /// <summary>Whether the values are proven integers.</summary>
  public bool IsInteger => this.Kind == JsKind.Number && this.Float == 0;

  /// <summary>Whether the values are integers that one of the two 32-bit types holds.</summary>
  public bool IsWord => this.IsInteger && (this.Within(IrType.I32) || this.Within(IrType.U32));

  /// <summary>Whether every value is a value of an integer type.</summary>
  public bool Within(IrType type) => this.Low >= IrOperators.Minimum(type) && this.High <= IrOperators.Maximum(type);

  /// <summary>
  /// The IR type that holds the values, or <see langword="null"/> when there is none: for an
  /// unbounded <c>BigInt</c>, a conflict, and a place nothing has reached.
  /// </summary>
  public IrType? Ir => this.Kind switch {
    JsKind.Undefined => IrType.Void,
    JsKind.Boolean => IrType.Bool,
    JsKind.Number when this.Float == 32 => IrType.F32,
    JsKind.Number when this.Float == 64 => IrType.F64,
    JsKind.Number => (this.IsLiteral ? _words.Skip(4) : _words).First(this.Within),
    JsKind.BigInt when !this.IsUnbounded => _longs.FirstOrDefault(this.Within),
    JsKind.String => IrType.String,
    JsKind.Array => IrType.Array(this.Element!.Ir is { } element && (element.IsNumeric || element.IsStruct || element.IsString) ? element : IrType.F64),
    JsKind.Record => IrType.Struct(this.Shape!.Registry.Declare(this.Shape)),
    _ => null
  };

  /// <summary>The type as a diagnostic names it: the kind, and for an array its elements.</summary>
  public string Describe() => this.Kind switch {
    JsKind.Undefined => "undefined",
    JsKind.Boolean => "boolean",
    JsKind.Number => "number",
    JsKind.BigInt => "bigint",
    JsKind.String => "string",
    JsKind.Array => this.Element!.HasRecord ? $"{this.Element.Describe()}[]" : Spell(this.Ir!),
    JsKind.Record => this.Shape!.ToString(),
    _ => "nothing"
  };

  /// <summary>The JSDoc spelling of an IR type.</summary>
  public static string Spell(IrType type) => type.Kind switch {
    IrTypeKind.Void => "void",
    IrTypeKind.Bool => "boolean",
    IrTypeKind.I8 => "int8",
    IrTypeKind.U8 => "uint8",
    IrTypeKind.I16 => "int16",
    IrTypeKind.U16 => "uint16",
    IrTypeKind.I32 => "int32",
    IrTypeKind.U32 => "uint32",
    IrTypeKind.I64 => "int64",
    IrTypeKind.U64 => "uint64",
    IrTypeKind.F32 => "float32",
    IrTypeKind.F64 => "float64",
    IrTypeKind.Struct => type.Declaration!.Name,
    IrTypeKind.String => "string",
    _ => Spell(type.Element!) + "[]"
  };

  /// <summary>
  /// The type of a place that values of both types reach. Integers join to the smallest integer type
  /// that holds both and, when 32 bits do not, to a double, which holds every 32-bit integer exactly.
  /// Two kinds join to a conflict, which stays one whatever else is joined.
  /// </summary>
  public static JsType Join(JsType first, JsType second) {
    if (first.Kind == JsKind.Conflict || second.Kind == JsKind.Unknown)
      return first;

    if (second.Kind == JsKind.Conflict || first.Kind == JsKind.Unknown)
      return second;

    if (first.Kind != second.Kind)
      return Conflict(first, second);

    switch (first.Kind) {
      case JsKind.Number:
        if (first.Float != 0 || second.Float != 0)
          return first.Float == 32 && second.Float == 32 ? F32 : F64;

        return Hull(first, second, _words) ?? F64;
      case JsKind.BigInt:
        return first.IsUnbounded || second.IsUnbounded ? UnboundedBigInt : Hull(first, second, _longs) ?? UnboundedBigInt;
      case JsKind.Array:
        if (first.IsPlain && second.IsPlain)
          return ArrayOf(Join(first.Element!, second.Element!), true);

        return first.IsPlain == second.IsPlain && first.Ir == second.Ir ? first : Conflict(first, second);
      case JsKind.Record:
        // Two shapes are one record only with the same keys in the same order, each value of one kind.
        var (a, b) = (first.Shape!, second.Shape!);
        if (!a.Keys.SequenceEqual(b.Keys))
          return Conflict(first, second);

        var joined = a.Types.Zip(b.Types, Join).ToList();
        return joined.Exists(type => type.Kind == JsKind.Conflict) ? Conflict(first, second) : Record(new RecordShape(a.Registry, a.Keys, joined));
      default:
        return first;
    }
  }

  private static JsType Conflict(JsType first, JsType second)
    => new(JsKind.Conflict) { Kinds = (first.Describe(), second.Describe()) };

  /// <summary>The range that holds both ranges: of literals as it is, otherwise widened to the smallest type that holds it; nothing when no type does.</summary>
  private static JsType? Hull(JsType first, JsType second, IrType[] types) {
    var low = Int128.Min(first.Low, second.Low);
    var high = Int128.Max(first.High, second.High);
    var holder = types.FirstOrDefault(type => low >= IrOperators.Minimum(type) && high <= IrOperators.Maximum(type));
    if (holder is null)
      return null;

    return first.IsLiteral && second.IsLiteral ? new(first.Kind) { Low = low, High = high, IsLiteral = true } : Of(holder);
  }

}
