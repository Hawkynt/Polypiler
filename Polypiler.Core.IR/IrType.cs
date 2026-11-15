using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>The shapes a value of the prototype IR can have.</summary>
public enum IrTypeKind {

  /// <summary>No value; only the return type of a function.</summary>
  Void,

  /// <summary>A truth value.</summary>
  Bool,

  /// <summary>A signed 8-bit integer. It is storage only: arithmetic happens on 32-bit values.</summary>
  I8,

  /// <summary>An unsigned 8-bit integer. It is storage only: arithmetic happens on 32-bit values.</summary>
  U8,

  /// <summary>A signed 16-bit integer. It is storage only: arithmetic happens on 32-bit values.</summary>
  I16,

  /// <summary>An unsigned 16-bit integer; also a UTF-16 code unit. It is storage only, like the other narrow types.</summary>
  U16,

  /// <summary>A signed 32-bit integer in two's complement that wraps around.</summary>
  I32,

  /// <summary>An unsigned 32-bit integer that wraps around.</summary>
  U32,

  /// <summary>A signed 64-bit integer in two's complement that wraps around.</summary>
  I64,

  /// <summary>An unsigned 64-bit integer that wraps around.</summary>
  U64,

  /// <summary>An IEEE 754 binary32 number; every operation rounds its result to binary32.</summary>
  F32,

  /// <summary>An IEEE 754 binary64 number.</summary>
  F64,

  /// <summary>A one-dimensional, zero-based array of numbers or of structs with a fixed length.</summary>
  Array,

  /// <summary>A record of named fields that is copied by value: an <see cref="IrStructType"/>.</summary>
  Struct,

  /// <summary>
  /// A writable view of a run of elements of numbers: an array, the first element and a length. It lives on
  /// the stack only: it is a local, a parameter or a result, never stored in an array, a field or a constant.
  /// </summary>
  Span,

  /// <summary>A view like <see cref="Span"/> whose elements are only read.</summary>
  ReadOnlySpan,

  /// <summary>An immutable sequence of UTF-16 code units, never null; compared and searched by code unit.</summary>
  String,

  /// <summary>
  /// A reference to an object of a class, an <see cref="IrClassType"/>, or of a class that implements an
  /// <see cref="IrInterfaceType"/>, or <c>null</c>: the object has an identity, and every copy of the
  /// reference names the same object.
  /// </summary>
  Object,

  /// <summary>
  /// An optional value <c>T?</c>: a value of its <see cref="IrType.Underlying"/> type, or none. Lifted
  /// operations are no part of the type: the IR spells them out with <see cref="IrIsNone"/>,
  /// <see cref="IrUnwrap"/> and <see cref="IrSome"/>.
  /// </summary>
  Optional,

  /// <summary>
  /// A growable list of elements with an identity: <c>List&lt;T&gt;</c> of C#, an <c>Array</c> of
  /// JavaScript that changes its length. What its operations mean is stated at <see cref="IrCollectionOp"/>.
  /// </summary>
  List,

  /// <summary>
  /// A map from keys to values with an identity and an order of enumeration (<see cref="IrType.Order"/>):
  /// <c>Dictionary&lt;TKey, TValue&gt;</c> of C#, <c>Map</c> of JavaScript.
  /// </summary>
  Map,

  /// <summary>A set of keys with an identity and an order of enumeration: <c>HashSet&lt;T&gt;</c> of C#, <c>Set</c> of JavaScript.</summary>
  Set

}

/// <summary>
/// A type of the prototype IR. Two types are equal when kind and element type are, and for a
/// declared type when the declaration is the same one: declared types are nominal.
/// </summary>
public sealed record IrType {

  private IrType(IrTypeKind kind, IrType? element) : this(kind, element, null) { }

  private IrType(IrTypeKind kind, IrType? element, IrTypeDeclaration? declaration) {
    this.Kind = kind;
    this.Element = element;
    this.Declaration = declaration;
  }

  private IrType(IrType underlying, IrTypeKind kind) : this(kind, null, null) => this.Underlying = underlying;

  private IrType(IrTypeKind kind, IrType? key, IrType element, MapOrder order) : this(kind, element, null) {
    this.Key = key;
    this.Order = order;
  }

  /// <summary>The shape of the type.</summary>
  public IrTypeKind Kind { get; }

  /// <summary>The element type of an array, otherwise <see langword="null"/>.</summary>
  public IrType? Element { get; }

  /// <summary>The declaration of a declared type, such as a struct; otherwise <see langword="null"/>.</summary>
  public IrTypeDeclaration? Declaration { get; }

  /// <summary>No value.</summary>
  public static IrType Void { get; } = new(IrTypeKind.Void, null);

  /// <summary>A truth value.</summary>
  public static IrType Bool { get; } = new(IrTypeKind.Bool, null);

  /// <summary>A signed 8-bit integer.</summary>
  public static IrType I8 { get; } = new(IrTypeKind.I8, null);

  /// <summary>An unsigned 8-bit integer.</summary>
  public static IrType U8 { get; } = new(IrTypeKind.U8, null);

  /// <summary>A signed 16-bit integer.</summary>
  public static IrType I16 { get; } = new(IrTypeKind.I16, null);

  /// <summary>An unsigned 16-bit integer.</summary>
  public static IrType U16 { get; } = new(IrTypeKind.U16, null);

  /// <summary>A signed 32-bit integer.</summary>
  public static IrType I32 { get; } = new(IrTypeKind.I32, null);

  /// <summary>An unsigned 32-bit integer.</summary>
  public static IrType U32 { get; } = new(IrTypeKind.U32, null);

  /// <summary>A signed 64-bit integer.</summary>
  public static IrType I64 { get; } = new(IrTypeKind.I64, null);

  /// <summary>An unsigned 64-bit integer.</summary>
  public static IrType U64 { get; } = new(IrTypeKind.U64, null);

  /// <summary>An IEEE 754 binary32 number.</summary>
  public static IrType F32 { get; } = new(IrTypeKind.F32, null);

  /// <summary>An IEEE 754 binary64 number.</summary>
  public static IrType F64 { get; } = new(IrTypeKind.F64, null);

  /// <summary>An immutable, non-null sequence of UTF-16 code units, written <c>str</c>.</summary>
  public static IrType String { get; } = new(IrTypeKind.String, null);

  /// <summary>The array type with the given element type.</summary>
  /// <param name="element">The type of every element.</param>
  /// <returns>The array type.</returns>
  public static IrType Array(IrType element) => new(IrTypeKind.Array, element);

  /// <summary>The type of a writable view of elements of a number type.</summary>
  /// <param name="element">The type of every element.</param>
  /// <returns>The view type.</returns>
  public static IrType Span(IrType element) => new(IrTypeKind.Span, element);

  /// <summary>The type of a read-only view of elements of a number type.</summary>
  /// <param name="element">The type of every element.</param>
  /// <returns>The view type.</returns>
  public static IrType ReadOnlySpan(IrType element) => new(IrTypeKind.ReadOnlySpan, element);

  /// <summary>Whether the type is a view of elements, writable or read-only.</summary>
  public bool IsView => this.Kind is IrTypeKind.Span or IrTypeKind.ReadOnlySpan;

  /// <summary>Whether the elements of a value of the type can be written: an array or a writable view.</summary>
  public bool IsWritableSequence => this.Kind is IrTypeKind.Array or IrTypeKind.Span;

  /// <summary>Whether the type is an array or a view, whose elements are read by index.</summary>
  public bool IsSequence => this.IsArray || this.IsView;

  /// <summary>The type of the values of a struct.</summary>
  /// <param name="declaration">The struct.</param>
  /// <returns>The struct type.</returns>
  public static IrType Struct(IrStructType declaration) => new(IrTypeKind.Struct, null, declaration);

  /// <summary>The struct of a struct type, otherwise <see langword="null"/>.</summary>
  public IrStructType? StructType => this.Declaration as IrStructType;

  /// <summary>The type of the references to objects of a class.</summary>
  /// <param name="declaration">The class.</param>
  /// <returns>The reference type.</returns>
  public static IrType Object(IrClassType declaration) => new(IrTypeKind.Object, null, declaration);

  /// <summary>The class of a reference type, otherwise <see langword="null"/>.</summary>
  public IrClassType? ClassType => this.Declaration as IrClassType;

  /// <summary>The type of the references to objects of the classes that implement an interface.</summary>
  /// <param name="declaration">The interface.</param>
  /// <returns>The reference type.</returns>
  public static IrType Object(IrInterfaceType declaration) => new(IrTypeKind.Object, null, declaration);

  /// <summary>The interface of a reference type, otherwise <see langword="null"/>.</summary>
  public IrInterfaceType? InterfaceType => this.Declaration as IrInterfaceType;

  /// <summary>
  /// Whether a value of this type is a value of another without a conversion: the same type, or a
  /// reference to a class that converts to its base class or an interface it implements (an upcast,
  /// which has no effect).
  /// </summary>
  /// <param name="target">The type the value goes to.</param>
  /// <returns><see langword="true"/> when the value can stand where the target type is expected.</returns>
  public bool IsAssignableTo(IrType? target) {
    if (this == target)
      return true;

    if (!this.IsObject || target is not { IsObject: true })
      return false;

    if (this.ClassType is { } type)
      return target.ClassType is { } ancestor ? type.DerivesFrom(ancestor) : type.AllInterfaces.Contains(target.InterfaceType!);

    return target.InterfaceType is { } other && this.InterfaceType!.Closure.Contains(other);
  }

  /// <summary>Whether the type is a reference to an object of a class, which may be <c>null</c>.</summary>
  public bool IsObject => this.Kind == IrTypeKind.Object;

  /// <summary>The type of the value an optional type holds when it holds one, otherwise <see langword="null"/>.</summary>
  public IrType? Underlying { get; }

  /// <summary>
  /// The optional type over a type: a scalar - a truth value or a number - or a struct. References
  /// have no optional type: they are <c>null</c> already (<see cref="IrNull"/>).
  /// </summary>
  /// <param name="underlying">The type of the value it holds when it holds one.</param>
  /// <returns>The optional type <c>T?</c>.</returns>
  public static IrType Optional(IrType underlying) => new(underlying, IrTypeKind.Optional);

  /// <summary>Whether the type is an optional value <c>T?</c>.</summary>
  public bool IsOptional => this.Kind == IrTypeKind.Optional;

  /// <summary>Whether a type may be the underlying type of an optional one: a truth value, a number or a struct.</summary>
  public bool IsOptionalValue => this == Bool || this.IsNumeric || this.IsStruct;

  /// <summary>The list type with the given element type.</summary>
  /// <param name="element">The type of every element.</param>
  /// <returns>The list type.</returns>
  public static IrType List(IrType element) => new(IrTypeKind.List, null, element, MapOrder.Insertion);

  /// <summary>The map type with the given key and value types and order of enumeration.</summary>
  /// <param name="key">The type of the keys.</param>
  /// <param name="value">The type of the values, which <see cref="Element"/> gives.</param>
  /// <param name="order">The order the entries are enumerated in.</param>
  /// <returns>The map type.</returns>
  public static IrType Map(IrType key, IrType value, MapOrder order) => new(IrTypeKind.Map, key, value, order);

  /// <summary>The set type with the given element type and order of enumeration.</summary>
  /// <param name="element">The type of every element.</param>
  /// <param name="order">The order the elements are enumerated in.</param>
  /// <returns>The set type.</returns>
  public static IrType Set(IrType element, MapOrder order) => new(IrTypeKind.Set, null, element, order);

  /// <summary>The type of the keys of a map, otherwise <see langword="null"/>.</summary>
  public IrType? Key { get; }

  /// <summary>The order a map or a set enumerates in; <see cref="MapOrder.Insertion"/> for every other type.</summary>
  public MapOrder Order { get; }

  /// <summary>Whether the type is a list, a map or a set.</summary>
  public bool IsCollection => this.Kind is IrTypeKind.List or IrTypeKind.Map or IrTypeKind.Set;

  /// <summary>Whether the type is a list.</summary>
  public bool IsList => this.Kind == IrTypeKind.List;

  /// <summary>Whether a value of the type is a reference that may be <c>null</c>: an object or an array.</summary>
  public bool IsNullable => this.IsObject || this.IsArray;

  /// <summary>Whether the type is the string type <c>str</c>.</summary>
  public bool IsString => this.Kind == IrTypeKind.String;

  /// <summary>Whether the type is a struct, which is copied by value.</summary>
  public bool IsStruct => this.Kind == IrTypeKind.Struct;

  /// <summary>Whether values of the type are integers of any width.</summary>
  public bool IsInteger => this.Kind is >= IrTypeKind.I8 and <= IrTypeKind.U64;

  /// <summary>Whether the type is one of the two 32-bit integer types.</summary>
  public bool IsWord => this.Kind is IrTypeKind.I32 or IrTypeKind.U32;

  /// <summary>Whether the type is one of the two 64-bit integer types.</summary>
  public bool IsLong => this.Kind is IrTypeKind.I64 or IrTypeKind.U64;

  /// <summary>Whether integer arithmetic is defined on the type: it is an integer of 32 or 64 bits.</summary>
  public bool IsWideInteger => this.IsWord || this.IsLong;

  /// <summary>Whether the type is an integer that has negative values.</summary>
  public bool IsSigned => this.Kind is IrTypeKind.I8 or IrTypeKind.I16 or IrTypeKind.I32 or IrTypeKind.I64;

  /// <summary>The width of an integer type in bits; 0 for every other type.</summary>
  public int Bits => this.Kind switch {
    IrTypeKind.I8 or IrTypeKind.U8 => 8,
    IrTypeKind.I16 or IrTypeKind.U16 => 16,
    IrTypeKind.I32 or IrTypeKind.U32 => 32,
    IrTypeKind.I64 or IrTypeKind.U64 => 64,
    _ => 0
  };

  /// <summary>The number of bytes a value of a number type takes in memory: 1, 2, 4 or 8; 0 for every other type.</summary>
  public int Size => this.Kind switch {
    IrTypeKind.F32 => 4,
    IrTypeKind.F64 => 8,
    _ => this.Bits / 8
  };

  /// <summary>Whether values of the type are floating-point numbers.</summary>
  public bool IsFloat => this.Kind is IrTypeKind.F32 or IrTypeKind.F64;

  /// <summary>Whether values of the type are numbers.</summary>
  public bool IsNumeric => this.IsInteger || this.IsFloat;

  /// <summary>Whether arithmetic is defined on the type; the narrow integers are widened first.</summary>
  public bool IsArithmetic => this.IsWideInteger || this.IsFloat;

  /// <summary>Whether the type is an array.</summary>
  public bool IsArray => this.Kind == IrTypeKind.Array;

  /// <summary>
  /// Whether the type can be the type of a local: not void, an array only of numbers, of structs or of
  /// strings, and a view only of numbers.
  /// </summary>
  public bool IsStorable => this.IsOptional ? this.Underlying!.IsOptionalValue
    : this.IsCollection ? IrCollections.IsElement(this)
    : this.Kind != IrTypeKind.Void && (this.Element is null || this.Element.IsNumeric || (!this.IsView && (this.Element.IsStruct || this.Element.IsString)));

  /// <summary>The name of the type as the IR writes it, for example <c>i32</c>, <c>u8[]</c>, <c>str</c>, <c>i32?</c> or the name of a struct.</summary>
  public override string ToString()
    => this.Underlying is { } underlying ? $"{underlying}?"
      : this.Declaration is { } declaration ? declaration.Name
      : this.IsString ? "str"
      : this.Element is null ? this.Kind.ToString().ToLowerInvariant()
      : this.Kind == IrTypeKind.Span ? $"span<{this.Element}>"
      : this.Kind == IrTypeKind.ReadOnlySpan ? $"readonly span<{this.Element}>"
      : this.Kind == IrTypeKind.List ? $"list<{this.Element}>"
      : this.Kind == IrTypeKind.Map ? $"map<{this.Key}, {this.Element}, {this.Order.ToString().ToLowerInvariant()}>"
      : this.Kind == IrTypeKind.Set ? $"set<{this.Element}, {this.Order.ToString().ToLowerInvariant()}>"
      : $"{this.Element}[]";

}
