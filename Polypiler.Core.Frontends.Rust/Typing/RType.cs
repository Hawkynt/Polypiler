using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>What a type of the checker is.</summary>
internal enum RKind {

  /// <summary>One of the integer types.</summary>
  Integer,

  /// <summary><c>f32</c> or <c>f64</c>.</summary>
  Float,

  /// <summary><c>bool</c>.</summary>
  Bool,

  /// <summary><c>()</c>.</summary>
  Unit,

  /// <summary><c>!</c>, the type of an expression that never yields a value.</summary>
  Never,

  /// <summary>The pair of an <c>overflowing_*</c> method: the value and the overflow flag.</summary>
  Tuple,

  /// <summary>The optional value of a <c>checked_*</c> method.</summary>
  Option,

  /// <summary>A type the checker has not decided yet: an inference variable.</summary>
  Variable

}

/// <summary>What an inference variable may become.</summary>
internal enum VariableKind {

  /// <summary>Anything: the type of a <c>let</c> without type and initializer.</summary>
  Any,

  /// <summary>An integer type: the type of an integer literal without suffix, <c>i32</c> if nothing decides it.</summary>
  Integer,

  /// <summary>A floating-point type: the type of a float literal without suffix, <c>f64</c> if nothing decides it.</summary>
  Float

}

/// <summary>
/// A type of the Rust subset: a primitive type, unit, never, the results of the overflow methods, or
/// an inference variable that unification binds. The primitive types are singletons, compared by
/// reference.
/// </summary>
internal sealed class RType {

  private static readonly Dictionary<string, RType> _primitives = [];

  private RType(RKind kind, string name) {
    this.Kind = kind;
    this.Name = name;
  }

  /// <summary>What the type is; of a bound variable, still <see cref="RKind.Variable"/> - use <see cref="Resolved"/>.</summary>
  public RKind Kind { get; }

  /// <summary>The name of a primitive type as Rust spells it.</summary>
  public string Name { get; }

  /// <summary>The value type of a tuple or an option.</summary>
  public RType? Element { get; private init; }

  /// <summary>What a variable may become.</summary>
  public VariableKind VariableKind { get; set; }

  /// <summary>What a variable is bound to, or <see langword="null"/>.</summary>
  public RType? Bound { get; set; }

  public static RType Unit { get; } = new(RKind.Unit, "()");

  public static RType Never { get; } = new(RKind.Never, "!");

  private IrType _ir = IrType.Void;
  private int _bits;

  public static RType Bool { get; } = Primitive(RKind.Bool, "bool", IrType.Bool, 1);

  public static RType I8 { get; } = Primitive(RKind.Integer, "i8", IrType.I8, 8);

  public static RType I16 { get; } = Primitive(RKind.Integer, "i16", IrType.I16, 16);

  public static RType I32 { get; } = Primitive(RKind.Integer, "i32", IrType.I32, 32);

  public static RType I64 { get; } = Primitive(RKind.Integer, "i64", IrType.I64, 64);

  public static RType Isize { get; } = Primitive(RKind.Integer, "isize", IrType.I64, 64);

  public static RType U8 { get; } = Primitive(RKind.Integer, "u8", IrType.U8, 8);

  public static RType U16 { get; } = Primitive(RKind.Integer, "u16", IrType.U16, 16);

  public static RType U32 { get; } = Primitive(RKind.Integer, "u32", IrType.U32, 32);

  public static RType U64 { get; } = Primitive(RKind.Integer, "u64", IrType.U64, 64);

  public static RType Usize { get; } = Primitive(RKind.Integer, "usize", IrType.U64, 64);

  public static RType F32 { get; } = Primitive(RKind.Float, "f32", IrType.F32, 32);

  public static RType F64 { get; } = Primitive(RKind.Float, "f64", IrType.F64, 64);

  /// <summary>The types the language has and the subset leaves out, which a diagnostic names.</summary>
  public static IReadOnlySet<string> Unsupported { get; } = new HashSet<string> { "i128", "u128", "f16", "f128", "char", "str" };

  private static RType Primitive(RKind kind, string name, IrType ir, int bits) {
    var type = new RType(kind, name) { _ir = ir, _bits = bits };
    _primitives.Add(name, type);
    return type;
  }

  /// <summary>The primitive type of a name, or <see langword="null"/>.</summary>
  public static RType? Named(string name) => _primitives.GetValueOrDefault(name);

  /// <summary>A new inference variable.</summary>
  public static RType Variable(VariableKind kind) => new(RKind.Variable, "_") { VariableKind = kind };

  /// <summary>The pair of a value and an overflow flag.</summary>
  public static RType Tuple(RType value) => new(RKind.Tuple, "tuple") { Element = value };

  /// <summary>An optional value.</summary>
  public static RType Option(RType value) => new(RKind.Option, "Option") { Element = value };

  /// <summary>The type a variable stands for, following its bindings; any other type itself.</summary>
  public RType Resolved {
    get {
      var type = this;
      while (type.Kind == RKind.Variable && type.Bound is not null)
        type = type.Bound;

      return type;
    }
  }

  public bool IsSigned => this.Resolved.Name[0] == 'i';

  /// <summary>The width in bits of a primitive type; <c>isize</c> and <c>usize</c> have 64 (the profile's pointer width).</summary>
  public int Bits => this.Resolved._bits;

  /// <summary>The smallest value of an integer type.</summary>
  public BigInteger Minimum => this.IsSigned ? -(BigInteger.One << (this.Bits - 1)) : BigInteger.Zero;

  /// <summary>The largest value of an integer type.</summary>
  public BigInteger Maximum => this.IsSigned ? (BigInteger.One << (this.Bits - 1)) - 1 : (BigInteger.One << this.Bits) - 1;

  /// <summary>The unsigned integer type of the same width: <c>abs_diff</c>'s result.</summary>
  public RType Unsigned => this.Resolved.Name switch {
    "i8" => U8,
    "i16" => U16,
    "i32" => U32,
    "i64" => U64,
    "isize" => Usize,
    _ => this.Resolved
  };

  /// <summary>The type of the IR a resolved type is represented by; <c>void</c> for unit and never.</summary>
  public IrType Ir => this.Resolved._ir;

  /// <inheritdoc/>
  public override string ToString() {
    var type = this.Resolved;
    return type.Kind switch {
      RKind.Variable => type.VariableKind switch { VariableKind.Integer => "{integer}", VariableKind.Float => "{float}", _ => "_" },
      RKind.Tuple => $"({type.Element}, bool)",
      _ => type.Name
    };
  }

  /// <summary>Every integer type, in the order of the reference.</summary>
  public static IReadOnlyList<RType> Integers { get; } = [I8, I16, I32, I64, Isize, U8, U16, U32, U64, Usize];

}
