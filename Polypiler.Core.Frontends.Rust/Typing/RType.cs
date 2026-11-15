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

  /// <summary>A tuple of two or more elements.</summary>
  Tuple,

  /// <summary><c>Option&lt;T&gt;</c>.</summary>
  Option,

  /// <summary>A type the checker has not decided yet: an inference variable.</summary>
  Variable,

  /// <summary><c>[T; N]</c>.</summary>
  Array,

  /// <summary><c>[T]</c>, only ever behind a reference.</summary>
  Slice,

  /// <summary><c>Vec&lt;T&gt;</c>.</summary>
  Vec,

  /// <summary><c>&amp;T</c> or <c>&amp;mut T</c>.</summary>
  Reference,

  /// <summary>A struct of the crate.</summary>
  Struct,

  /// <summary>A field-less enum of the crate.</summary>
  Enum,

  /// <summary><c>Result&lt;T, E&gt;</c>.</summary>
  Result,

  /// <summary>A function pointer <c>fn(A) -&gt; R</c>.</summary>
  Function,

  /// <summary>The type of one closure expression, which only that closure has.</summary>
  Closure,

  /// <summary>An iterator of the subset: a source and its adapters, consumed where it is made.</summary>
  Iterator,

  /// <summary><c>char</c>, a Unicode scalar value; a <c>u32</c> in the IR.</summary>
  Char,

  /// <summary><c>str</c>, only ever behind a reference; an IR string.</summary>
  Str,

  /// <summary><c>String</c>, owned text; an IR string, which is immutable, so the value is the text.</summary>
  String,

  /// <summary>A type parameter of a generic function's template, which only its instances replace.</summary>
  Param,

  /// <summary><c>HashMap&lt;K, V&gt;</c> or <c>BTreeMap&lt;K, V&gt;</c>: an IR map; <see cref="RType.Element"/> is the value, the key the first of <see cref="RType.Elements"/>.</summary>
  Map,

  /// <summary><c>HashSet&lt;T&gt;</c> or <c>BTreeSet&lt;T&gt;</c>: an IR set of <see cref="RType.Element"/>.</summary>
  Set

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
/// A type of the Rust subset: a primitive type, unit, never, a tuple, an array, a slice, a
/// <c>Vec</c>, a reference, a struct or an enum of the crate, <c>Option</c>, <c>Result</c>, a function
/// pointer, a closure, or an inference variable that unification binds. The primitive types are
/// singletons, compared by reference; the other types are compared by their parts.
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

  /// <summary>The element of an array, a slice or a <c>Vec</c>, the value of an <c>Option</c> and of an <c>Ok</c>, the target of a reference, the result of a function.</summary>
  public RType? Element { get; private init; }

  /// <summary>The elements of a tuple; the error of a <c>Result</c> as its only element; the parameters of a function.</summary>
  public IReadOnlyList<RType> Elements { get; private init; } = [];

  /// <summary>The length of an array.</summary>
  public long Length { get; private init; }

  /// <summary>Whether a reference is <c>&amp;mut</c>.</summary>
  public bool Mutable { get; private init; }

  /// <summary>The declaration of a struct, an enum or a closure.</summary>
  public object? Declaration { get; private init; }

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

  /// <summary><c>char</c>.</summary>
  public static RType Char { get; } = Primitive(RKind.Char, "char", IrType.U32, 32);

  /// <summary><c>str</c>.</summary>
  public static RType Str { get; } = Primitive(RKind.Str, "str", IrType.String, 0);

  /// <summary><c>String</c>.</summary>
  public static RType String { get; } = Primitive(RKind.String, "String", IrType.String, 0);

  /// <summary><c>&amp;str</c>, the type of a string literal.</summary>
  public static RType StrRef { get; } = Reference(Str, false);

  /// <summary>A map of the standard library: <c>HashMap</c> or <c>BTreeMap</c>.</summary>
  public static RType Map(string name, RType key, RType value) => new(RKind.Map, name) { Element = value, Elements = [key] };

  /// <summary>A set of the standard library: <c>HashSet</c> or <c>BTreeSet</c>.</summary>
  public static RType Set(string name, RType element) => new(RKind.Set, name) { Element = element };

  /// <summary>The key type of a map.</summary>
  public RType Key => this.Resolved.Elements[0];

  /// <summary>A type parameter of a template.</summary>
  public static RType Param(string name) => new(RKind.Param, name);

  /// <summary>Whether a type is text: <c>String</c> or <c>str</c>, behind any number of references.</summary>
  public bool IsText => this.Dereferenced.Kind is RKind.String or RKind.Str;

  /// <summary>The numeric types the language has and the subset leaves out, which a diagnostic names.</summary>
  public static IReadOnlySet<string> Unsupported { get; } = new HashSet<string> { "i128", "u128", "f16", "f128" };

  private static RType Primitive(RKind kind, string name, IrType ir, int bits) {
    var type = new RType(kind, name) { _ir = ir, _bits = bits };
    _primitives.Add(name, type);
    return type;
  }

  /// <summary>The primitive type of a name, or <see langword="null"/>.</summary>
  public static RType? Named(string name) => _primitives.GetValueOrDefault(name);

  /// <summary>A new inference variable.</summary>
  public static RType Variable(VariableKind kind) => new(RKind.Variable, "_") { VariableKind = kind };

  /// <summary>A tuple of two or more elements.</summary>
  public static RType Tuple(IReadOnlyList<RType> elements) => new(RKind.Tuple, "tuple") { Elements = [.. elements] };

  /// <summary>The pair of a value and an overflow flag, of the <c>overflowing_*</c> methods.</summary>
  public static RType Pair(RType value) => Tuple([value, Bool]);

  /// <summary>An optional value.</summary>
  public static RType Option(RType value) => new(RKind.Option, "Option") { Element = value };

  /// <summary>A result: a value or an error.</summary>
  public static RType Result(RType value, RType error) => new(RKind.Result, "Result") { Element = value, Elements = [error] };

  /// <summary>An array of a fixed length.</summary>
  public static RType Array(RType element, long length) => new(RKind.Array, "array") { Element = element, Length = length };

  /// <summary>A slice, the target of a reference.</summary>
  public static RType Slice(RType element) => new(RKind.Slice, "slice") { Element = element };

  /// <summary>A growable vector.</summary>
  public static RType Vec(RType element) => new(RKind.Vec, "Vec") { Element = element };

  /// <summary>A reference.</summary>
  public static RType Reference(RType target, bool mutable) => new(RKind.Reference, "&") { Element = target, Mutable = mutable };

  /// <summary>A struct of the crate.</summary>
  public static RType StructOf(StructInfo declaration) => new(RKind.Struct, declaration.Name) { Declaration = declaration };

  /// <summary>A field-less enum of the crate.</summary>
  public static RType EnumOf(EnumInfo declaration) => new(RKind.Enum, declaration.Name) { Declaration = declaration };

  /// <summary>A function pointer.</summary>
  public static RType Function(IReadOnlyList<RType> parameters, RType result) => new(RKind.Function, "fn") { Elements = [.. parameters], Element = result };

  /// <summary>The type of one closure.</summary>
  public static RType Closure(ClosureInfo declaration) => new(RKind.Closure, "closure") { Declaration = declaration, Elements = declaration.Parameters, Element = declaration.Result };

  /// <summary>An iterator of a source and its adapters so far.</summary>
  public static RType Iterator(IteratorInfo declaration) => new(RKind.Iterator, "iterator") { Declaration = declaration, Element = declaration.Item };

  /// <summary>The iterator of an iterator type.</summary>
  public IteratorInfo? IteratorInfo => this.Resolved.Declaration as IteratorInfo;

  /// <summary>The type a variable stands for, following its bindings; any other type itself.</summary>
  public RType Resolved {
    get {
      var type = this;
      while (type.Kind == RKind.Variable && type.Bound is not null)
        type = type.Bound;

      return type;
    }
  }

  /// <summary>The struct of a struct type.</summary>
  public StructInfo? Struct => this.Resolved.Declaration as StructInfo;

  /// <summary>The enum of an enum type.</summary>
  public EnumInfo? Enum => this.Resolved.Declaration as EnumInfo;

  /// <summary>The error type of a <c>Result</c>.</summary>
  public RType Error => this.Resolved.Elements[0];

  public bool IsSigned => this.Resolved.Name[0] == 'i';

  /// <summary>The width in bits of a primitive type; <c>isize</c> and <c>usize</c> have 64 (the profile's pointer width).</summary>
  public int Bits => this.Resolved._bits;

  /// <summary>The smallest value of an integer type.</summary>
  public BigInteger Minimum => this.IsSigned ? -(BigInteger.One << (this.Bits - 1)) : BigInteger.Zero;

  /// <summary>The largest value of an integer type.</summary>
  public BigInteger Maximum => this.Resolved.Kind == RKind.Char ? 0x10FFFF : this.IsSigned ? (BigInteger.One << (this.Bits - 1)) - 1 : (BigInteger.One << this.Bits) - 1;

  /// <summary>The unsigned integer type of the same width: <c>abs_diff</c>'s result.</summary>
  public RType Unsigned => this.Resolved.Name switch {
    "i8" => U8,
    "i16" => U16,
    "i32" => U32,
    "i64" => U64,
    "isize" => Usize,
    _ => this.Resolved
  };

  /// <summary>Whether the type is a primitive number or <c>bool</c>.</summary>
  public bool IsScalar => this.Resolved.Kind is RKind.Integer or RKind.Float or RKind.Bool;

  /// <summary>Whether the type is a number.</summary>
  public bool IsNumber => this.Resolved.Kind is RKind.Integer or RKind.Float;

  /// <summary>The type of the IR a resolved primitive type is represented by; <c>void</c> for unit, never and every composite type.</summary>
  public IrType Ir => this.Resolved._ir;

  /// <summary>The type a reference points to, through every reference; any other type itself.</summary>
  public RType Dereferenced {
    get {
      var type = this.Resolved;
      while (type.Kind == RKind.Reference)
        type = type.Element!.Resolved;

      return type;
    }
  }

  /// <summary>
  /// Whether values of the type are <c>Copy</c>: the primitives, shared references, function
  /// pointers, arrays, tuples and options of copyable types, and the structs and enums that derive it.
  /// </summary>
  public bool IsCopy {
    get {
      var type = this.Resolved;
      return type.Kind switch {
        RKind.Integer or RKind.Float or RKind.Bool or RKind.Unit or RKind.Never or RKind.Function or RKind.Char => true,
        RKind.Reference => !type.Mutable,
        RKind.Array or RKind.Option => type.Element!.IsCopy,
        RKind.Tuple => type.Elements.All(element => element.IsCopy),
        RKind.Result => type.Element!.IsCopy && type.Error.IsCopy,
        RKind.Struct => type.Struct!.Derives.Contains("Copy"),
        RKind.Enum => type.Enum!.Derives.Contains("Copy"),
        _ => false
      };
    }
  }

  /// <inheritdoc/>
  public override string ToString() {
    var type = this.Resolved;
    return type.Kind switch {
      RKind.Variable => type.VariableKind switch { VariableKind.Integer => "{integer}", VariableKind.Float => "{float}", _ => "_" },
      RKind.Tuple => type.Elements.Count == 1 ? $"({type.Elements[0]},)" : $"({string.Join(", ", type.Elements)})",
      RKind.Option => $"Option<{type.Element}>",
      RKind.Result => $"Result<{type.Element}, {type.Error}>",
      RKind.Array => $"[{type.Element}; {type.Length}]",
      RKind.Slice => $"[{type.Element}]",
      RKind.Vec => $"Vec<{type.Element}>",
      RKind.Map => $"{type.Name}<{type.Key}, {type.Element}>",
      RKind.Set => $"{type.Name}<{type.Element}>",
      RKind.Reference => $"&{(type.Mutable ? "mut " : "")}{type.Element}",
      RKind.Function => $"fn({string.Join(", ", type.Elements)}) -> {type.Element}",
      RKind.Closure => "{closure}",
      RKind.Iterator => $"impl Iterator<Item = {type.Element}>",
      _ => type.Name
    };
  }

  /// <summary>Every integer type, in the order of the reference.</summary>
  public static IReadOnlyList<RType> Integers { get; } = [I8, I16, I32, I64, Isize, U8, U16, U32, U64, Usize];

}

/// <summary>A struct of the crate: its name, path, fields and derives.</summary>
internal sealed class StructInfo(string name, string path, Syntax.StructItem item) {

  public string Name { get; } = name;

  /// <summary>The name of the IR type: the crate path with its separators replaced.</summary>
  public string Path { get; } = path;

  public Syntax.StructItem Item { get; } = item;

  /// <summary>The fields in declaration order, with their types once resolved.</summary>
  public List<(string Name, RType Type, bool IsVisible)> Fields { get; } = [];

  /// <summary>The traits the struct derives.</summary>
  public HashSet<string> Derives { get; } = [];

  /// <summary>Whether the struct is <c>#[repr(C)]</c>, whose layout is observable.</summary>
  public bool ReprC { get; set; }

  /// <summary>The module the struct is declared in.</summary>
  public ModuleScope? Module { get; set; }

  /// <summary>The inherent methods and associated functions, by name.</summary>
  public Dictionary<string, FunctionSignature> Methods { get; } = [];

  /// <summary>The associated constants, by name.</summary>
  public Dictionary<string, Binding> Constants { get; } = [];

}

/// <summary>A field-less enum of the crate: its variants with the discriminants the compiler assigns.</summary>
internal sealed class EnumInfo(string name, string path, Syntax.EnumItem item) {

  public string Name { get; } = name;

  public string Path { get; } = path;

  public Syntax.EnumItem Item { get; } = item;

  /// <summary>The variants in declaration order with their discriminants.</summary>
  public List<(string Name, BigInteger Value)> Variants { get; } = [];

  /// <summary>The integer type of <c>#[repr]</c>, or <c>isize</c>.</summary>
  public RType Representation { get; set; } = RType.Isize;

  /// <summary>Whether the enum has a <c>#[repr]</c>.</summary>
  public bool HasRepr { get; set; }

  /// <summary>The traits the enum derives.</summary>
  public HashSet<string> Derives { get; } = [];

  /// <summary>The variant <c>#[default]</c> marks, or <see langword="null"/>.</summary>
  public string? Default { get; set; }

  /// <summary>The module the enum is declared in.</summary>
  public ModuleScope? Module { get; set; }

  /// <summary>The inherent methods and associated functions, by name.</summary>
  public Dictionary<string, FunctionSignature> Methods { get; } = [];

  /// <summary>The associated constants, by name.</summary>
  public Dictionary<string, Binding> Constants { get; } = [];

}

/// <summary>A closure: its expression, its parameter and result types, and the variables it captures.</summary>
internal sealed class ClosureInfo(Syntax.ClosureExpr expression, IReadOnlyList<RType> parameters, RType result) {

  public Syntax.ClosureExpr Expression { get; } = expression;

  public IReadOnlyList<RType> Parameters { get; } = parameters;

  public RType Result { get; } = result;

  /// <summary>The variables of enclosing scopes the body names, each once.</summary>
  public List<Binding> Captures { get; } = [];

}

/// <summary>An iterator under construction: its source and the adapters so far, and the type of its items.</summary>
internal sealed record IteratorInfo(SourceKind Kind, RangeLoop? Range, ElementSource? Source, IReadOnlyList<Adapter> Adapters, RType Item) {

  /// <summary>The element type of a range source.</summary>
  public RType? RangeElement { get; init; }

  /// <summary>Whether only adapters that work position by position follow the source: no <c>map</c> and no <c>filter</c> yet.</summary>
  public bool Positional => this.Adapters.All(adapter => adapter.Name is "rev" or "step_by" or "zip" or "enumerate" or "copied" or "cloned");

}
