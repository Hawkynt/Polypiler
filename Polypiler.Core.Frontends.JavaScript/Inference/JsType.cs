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

  /// <summary>A reference to an object of a class of the file.</summary>
  Object,

  /// <summary>
  /// An absent value only - <c>null</c>, <c>undefined</c> or both, which <see cref="JsType.MayBeNull"/>
  /// and <see cref="JsType.MayBeUndefined"/> say. Joined with a value it is that value, which may then
  /// be absent: an optional value of the IR for a scalar, <c>null</c> for a reference.
  /// </summary>
  Null,

  /// <summary>Values of two kinds reach the place, which no type of the IR holds.</summary>
  Conflict,

  /// <summary>A <c>Map</c>: keys of <see cref="JsType.Key"/>, values of <see cref="JsType.Element"/> (#1328).</summary>
  Map,

  /// <summary>A <c>Set</c> of elements of <see cref="JsType.Element"/> (#1328).</summary>
  Set,

  /// <summary>A function value: one of the functions of a <see cref="FunctionShape"/>.</summary>
  Function,

  /// <summary>A generator object, which yields values of <see cref="JsType.Element"/> once (#1327).</summary>
  Sequence

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

  /// <summary>
  /// Whether JSDoc states the array as <c>T[]</c> of numbers, which says what its elements are but not
  /// whether it is an <c>Array</c> or a typed array (#1374): the calls of the file say that, and where
  /// none passes one, whether the function itself requires an <c>Array</c>. Of a <c>BigInt</c>, whether
  /// JSDoc states <c>int64</c> or <c>uint64</c>, which says the range but not whether the place holds
  /// Numbers or BigInts (#1401): the values that reach it say that, and where none does, it is a <c>BigInt</c>.
  /// </summary>
  public bool IsStated { get; private init; }

  /// <summary>The array of the elements JSDoc states, of no kind decided yet: a typed array until something decides; the 64-bit integer JSDoc states, a <c>BigInt</c> until something decides.</summary>
  public JsType AsStated() => this with { IsStated = true };

  /// <summary>The stated type as the one it is where nothing else decides: a typed array, a <c>BigInt</c> of the stated range.</summary>
  public JsType Decided => this with { IsStated = false };

  /// <summary>
  /// The stated array as the kind of array an argument is: an <c>Array</c> - a list where the argument
  /// is one - or a typed array, of the stated elements, decided; <see langword="null"/> for an argument that is no array.
  /// The stated 64-bit integer as the kind of integer an argument is: the Number it is, or the stated <c>BigInt</c>.
  /// </summary>
  public JsType? ShapedAs(JsType argument)
    // A stated 64-bit integer is a Number where a Number is passed, the stated BigInt where a BigInt is (#1401), which the call then keeps or is refused for.
    => this.Kind == JsKind.BigInt ? (argument.Kind == JsKind.Number ? argument : argument.Kind == JsKind.BigInt ? this.Decided : null)
    // An argument that is a stated array nothing has decided either decides nothing.
    : argument.Kind != JsKind.Array || argument.IsView || argument.IsStated ? null
      : argument.IsGrowable ? ListOf(this.Element!)
      : argument.IsPlain ? ArrayOf(this.Element!, true) : this.Decided;

  /// <summary>The keys and value types of a record.</summary>
  public RecordShape? Shape { get; private init; }

  /// <summary>The class of a reference to an object.</summary>
  public ClassModel? Class { get; private init; }

  /// <summary>Whether <c>null</c> reaches the place as well.</summary>
  public bool MayBeNull { get; private init; }

  /// <summary>Whether <c>undefined</c> reaches the place as well: <c>undefined</c>, <c>void 0</c>, <c>return;</c>, the end of a function, a missing argument.</summary>
  public bool MayBeUndefined { get; private init; }

  /// <summary>Whether an absent value reaches the place.</summary>
  public bool IsAbsent => this.MayBeNull || this.MayBeUndefined;

  /// <summary>The type without its absent values: what ToNumber or a test leaves of a value that is present.</summary>
  public JsType Present => this with { MayBeNull = false, MayBeUndefined = false };

  /// <summary>
  /// The type of an optional key of a typedef (#1409): its stated type, or <c>undefined</c> where a value
  /// leaves the key out. An integer stays one: an operator that computes with it unchecked sees a NaN
  /// where it is undefined, a test that proves it present the integer.
  /// </summary>
  public JsType OrUndefined() => this with { MayBeUndefined = true };

  /// <summary>The type with the absent values that reach another place.</summary>
  public JsType AbsentAs(JsType other) => this with { MayBeNull = other.MayBeNull, MayBeUndefined = other.MayBeUndefined };

  /// <summary>The functions a function value may be.</summary>
  public FunctionShape? Callable { get; private init; }

  /// <summary>The type of the values of a shape of functions.</summary>
  public static JsType Function(FunctionShape shape) => new(JsKind.Function) { Callable = shape.Root };

  /// <summary>
  /// The enum every value is a member of, for an integer that only members of one enum object reach
  /// (and literals equal to a member); otherwise <see langword="null"/>.
  /// </summary>
  public IrEnumType? Enum { get; private init; }

  /// <summary>The type of a member of an enum object: the literal of its value, which stays the enum's while only members join it.</summary>
  /// <param name="type">The enum.</param>
  /// <param name="value">The value of the member.</param>
  /// <returns>The type.</returns>
  public static JsType Member(IrEnumType type, long value) => new(JsKind.Number) { Low = value, High = value, IsLiteral = true, Enum = type };

  /// <summary>The type without the enum: the integers it holds.</summary>
  public JsType Plain => this.Enum is null ? this : this with { Enum = null };

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

  /// <summary><c>null</c>.</summary>
  public static JsType Null { get; } = new(JsKind.Null) { MayBeNull = true };

  /// <summary><c>undefined</c> as a value: the literal, <c>void 0</c>, a <c>return;</c> or the end of a function that returns a value elsewhere.</summary>
  public static JsType UndefinedValue { get; } = new(JsKind.Null) { MayBeUndefined = true };

  /// <summary>A reference to an object of a class.</summary>
  public static JsType Object(ClassModel type) => new(JsKind.Object) { Class = type };

  /// <summary>A <c>BigInt</c> without a bound.</summary>
  public static JsType UnboundedBigInt { get; } = new(JsKind.BigInt) { IsUnbounded = true };

  /// <summary>The type of the values an IR type holds; an array of it is a typed array, and a value of an enum is one of its members' type.</summary>
  public static JsType Of(IrType type) => type.EnumType is { } named ? new(JsKind.Number) { Low = IrOperators.Minimum(type), High = IrOperators.Maximum(type), Enum = named } : type.Kind switch {
    IrTypeKind.Optional => Of(type.Underlying!) with { MayBeNull = true },
    IrTypeKind.Void => Undefined,
    IrTypeKind.Bool => Boolean,
    IrTypeKind.F32 => F32,
    IrTypeKind.F64 => F64,
    IrTypeKind.String => String,
    // An array of strings is a JavaScript Array: no typed array holds a string.
    IrTypeKind.Array => ArrayOf(Of(type.Element!), type.Element!.IsString),
    IrTypeKind.Span or IrTypeKind.ReadOnlySpan => ViewOf(Of(type.Element!)),
    IrTypeKind.Sequence => SequenceOf(Of(type.Element!)),
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

  /// <summary>A view of the elements of a typed array: what <c>subarray</c> makes, a <c>Span</c> of the IR (#1325).</summary>
  /// <param name="element">The type of every element.</param>
  /// <returns>The view type.</returns>
  public static JsType ViewOf(JsType element) => new(JsKind.Array) { Element = element, IsView = true };

  /// <summary>Whether the value is a view of a typed array, made by <c>subarray</c>, rather than an array of its own.</summary>
  public bool IsView { get; private init; }

  /// <summary>
  /// Whether a view escapes the stack of the function that holds it: it is stored into a field, or reaches a
  /// place a stored view reaches. Such a view is a <c>Memory</c> of the IR (#1325), every other one a <c>Span</c>.
  /// </summary>
  public bool IsMemory { get; private init; }

  /// <summary>The type of a value stored into a field: a view that escapes, which is a memory; any other type as it is.</summary>
  public JsType Stored() => this.IsView ? this with { IsMemory = true } : this;

  /// <summary>An array type.</summary>
  public static JsType ArrayOf(JsType element, bool plain)
    // An Array of objects, of arrays or of collections is a list (#1365): no array of the IR holds a reference.
    => plain && element.Kind is JsKind.Object or JsKind.Array or JsKind.Map or JsKind.Set ? ListOf(element) : new(JsKind.Array) { Element = element, IsPlain = plain };

  /// <summary>
  /// Whether a JavaScript <c>Array</c> changes its length - <c>push</c>, <c>pop</c>, <c>splice</c>, a
  /// write of <c>length</c> or at <c>a[a.length]</c> - so that it is a list of the IR (#1328); an array
  /// whose length never changes stays an array.
  /// </summary>
  public bool IsGrowable { get; private init; }

  /// <summary>The type of the keys of a <c>Map</c>.</summary>
  public JsType? Key { get; private init; }

  /// <summary>A <c>Map</c> type.</summary>
  public static JsType MapOf(JsType key, JsType value) => new(JsKind.Map) { Key = key, Element = value };

  /// <summary>A <c>Set</c> type.</summary>
  public static JsType SetOf(JsType element) => new(JsKind.Set) { Element = element };

  /// <summary>The type of a generator object that yields values of a type.</summary>
  public static JsType SequenceOf(JsType element) => new(JsKind.Sequence) { Element = element };

  /// <summary>The IR type of a key, a value or an element of a map or a set: what the values reaching it are, a double where nothing reached it.</summary>
  private static IrType Held(JsType type) => type.Ir is { } held && (held.IsNumeric || held == IrType.Bool || held.IsString || held.IsObject || held.IsStruct || held.IsCollection) ? held : IrType.F64;

  /// <summary>The type of a JavaScript <c>Array</c> that changes its length.</summary>
  public static JsType ListOf(JsType element) => new(JsKind.Array) { Element = element, IsPlain = true, IsGrowable = true };

  /// <summary>The same array as one that changes its length.</summary>
  public JsType Grown => ListOf(this.Element!) with { MayBeNull = this.MayBeNull, MayBeUndefined = this.MayBeUndefined };

  /// <summary>The type of a record of a shape.</summary>
  public static JsType Record(RecordShape shape) => new(JsKind.Record) { Shape = shape };

  /// <summary>Whether the type is a record or holds one, as an element or a value: such a type has its struct only once the shapes are named.</summary>
  public bool HasRecord => this.Kind == JsKind.Record || this.Element is { HasRecord: true } || this.Key is { HasRecord: true };

  /// <summary>Whether the values are proven integers. An absent value is no integer, but null is 0 to every operator that computes with it.</summary>
  public bool IsInteger => this.Kind == JsKind.Number && this.Float == 0 && !this.MayBeUndefined;

  /// <summary>Whether the values are integers that one of the two 32-bit types holds.</summary>
  public bool IsWord => this.IsInteger && (this.Within(IrType.I32) || this.Within(IrType.U32));

  /// <summary>Whether every value is a value of an integer type.</summary>
  public bool Within(IrType type) => this.Low >= IrOperators.Minimum(type) && this.High <= IrOperators.Maximum(type);

  /// <summary>
  /// The IR type that holds the values, or <see langword="null"/> when there is none: for an
  /// unbounded <c>BigInt</c>, a conflict, and a place nothing has reached.
  /// </summary>
  public IrType? Ir => this.IsAbsent && this.Kind is JsKind.Number or JsKind.Boolean or JsKind.BigInt
    ? (this.Present.Ir is { } present ? IrType.Optional(present) : null)
    : this.Kind switch {
      JsKind.Undefined => IrType.Void,
      JsKind.Boolean => IrType.Bool,
      JsKind.Number when this.Float == 32 => IrType.F32,
      JsKind.Number when this.Float == 64 => IrType.F64,
      JsKind.Number when this.Enum is { } named => named.Type,
      JsKind.Number => (this.IsLiteral ? _words.Skip(4) : _words).First(this.Within),
      JsKind.BigInt when !this.IsUnbounded => _longs.FirstOrDefault(this.Within),
      JsKind.String => IrType.String,
      JsKind.Array when this.IsView => this.IsMemory ? IrType.Memory(this.ViewedElement) : IrType.Span(this.ViewedElement),
      JsKind.Array when this.IsGrowable => IrType.List(this.Element!.Ir is { } listed && (listed.IsNumeric || listed.IsStruct || listed.IsString || listed.IsObject || listed.IsCollection || listed.IsArray || listed.IsFunction) ? listed : IrType.F64),
      JsKind.Array => IrType.Array(this.Element!.Ir is { } element && (element.IsNumeric || element.IsStruct || element.IsString || element.IsFunction) ? element : IrType.F64),
      JsKind.Function => this.Callable!.Signature,
      JsKind.Record => IrType.Struct(this.Shape!.Registry.Declare(this.Shape)),
      JsKind.Map => IrType.Map(Held(this.Key!), Held(this.Element!), MapOrder.Insertion),
      JsKind.Set => IrType.Set(Held(this.Element!), MapOrder.Insertion),
      JsKind.Object => IrType.Object(this.Class!.Ir),
      JsKind.Sequence => this.Element!.Ir is { } produced ? (IrSequences.IsElement(produced) ? IrType.Sequence(produced, false) : null) : this.Element.Kind == JsKind.Unknown ? IrType.Sequence(IrType.F64, false) : null,
      _ => null
    };

  /// <summary>The IR type of the elements of a view: a number type, a double where nothing better is known.</summary>
  private IrType ViewedElement => this.Element!.Ir is { IsNumeric: true } viewed ? viewed : IrType.F64;

  /// <summary>The type as a diagnostic names it: the kind, and for an array its elements.</summary>
  public string Describe() => this.Kind != JsKind.Null && this.IsAbsent ? $"{this.Present.Describe()} or {Absence(this)}" : this.Kind switch {
    JsKind.Undefined => "undefined",
    JsKind.Boolean => "boolean",
    JsKind.Number => "number",
    JsKind.BigInt => "bigint",
    JsKind.String => "string",
    JsKind.Array when this.IsGrowable => $"a list of {this.Element!.Describe()}",
    JsKind.Array => this.Element!.HasRecord ? $"{this.Element.Describe()}[]" : Spell(this.Ir!),
    JsKind.Record => this.Shape!.ToString(),
    JsKind.Map => $"a Map of {this.Key!.Describe()} to {this.Element!.Describe()}",
    JsKind.Set => $"a Set of {this.Element!.Describe()}",
    // An object of the class made for a literal is named as the literal is (#1388).
    JsKind.Object => this.Class!.RecordKeys is { } keys ? $"{{ {string.Join(", ", keys)} }}" : this.Class.Name,
    JsKind.Null => Absence(this),
    JsKind.Function => "a function",
    JsKind.Sequence => $"a generator of {this.Element!.Describe()}",
    _ => "nothing"
  };

  /// <summary>The absent values that reach a place, as a diagnostic names them.</summary>
  private static string Absence(JsType type) => type.MayBeNull && type.MayBeUndefined ? "null or undefined" : type.MayBeUndefined ? "undefined" : "null";

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
    IrTypeKind.Span or IrTypeKind.ReadOnlySpan or IrTypeKind.Memory or IrTypeKind.ReadOnlyMemory => Spell(type.Element!) + " view",
    IrTypeKind.String => "string",
    IrTypeKind.List => Spell(type.Element!) + " list",
    IrTypeKind.Map => $"Map<{Spell(type.Key!)}, {Spell(type.Element!)}>",
    IrTypeKind.Set => $"Set<{Spell(type.Element!)}>",
    IrTypeKind.Object => type.Declaration!.Name,
    IrTypeKind.Sequence => $"Generator<{Spell(type.Element!)}>",
    // A stated int|null is an optional value (#1377): its spelling, which a JSDoc reason names, is the one JSDoc writes.
    IrTypeKind.Optional => Spell(type.Underlying!) + "|null",
    IrTypeKind.Function => type.Signature!.ToString(),
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

    // An absent value joins any value to that value, which may then be absent as well (#1322).
    if (first.IsAbsent || second.IsAbsent) {
      var present = first.Kind == JsKind.Null ? second.Present : second.Kind == JsKind.Null ? first.Present : Join(first.Present, second.Present);
      return Absent(present, first.MayBeNull || second.MayBeNull, first.MayBeUndefined || second.MayBeUndefined);
    }

    if (first.Kind != second.Kind)
      return first.Kind == JsKind.Record && second.Kind == JsKind.Object ? Meet(first, second)
        : second.Kind == JsKind.Record && first.Kind == JsKind.Object ? Meet(second, first)
        : Conflict(first, second);

    switch (first.Kind) {
      case JsKind.Number when (first.Enum ?? second.Enum) is { } named:
        // Members of one enum, and literals that are members, stay the enum's; anything else joins as the numbers they are.
        bool Fits(JsType type) => type.Enum == named || (type.Enum is null && type.IsLiteral && type.Low == type.High && named.MemberOf((long)type.Low) is not null);
        return Fits(first) && Fits(second)
          ? new(JsKind.Number) { Low = Int128.Min(first.Low, second.Low), High = Int128.Max(first.High, second.High), IsLiteral = first.IsLiteral && second.IsLiteral, Enum = named }
          : Join(first.Plain, second.Plain);
      case JsKind.Number:
        if (first.Float != 0 || second.Float != 0)
          return first.Float == 32 && second.Float == 32 ? F32 : F64;

        return Hull(first, second, _words) ?? F64;
      case JsKind.BigInt:
        return first.IsUnbounded || second.IsUnbounded ? UnboundedBigInt : Hull(first, second, _longs) ?? UnboundedBigInt;
      case JsKind.Array:
        // An array JSDoc states and nothing has decided yet is whatever array the program gives it (#1374);
        // one of elements no value has reached yet - new Array(n), [] - takes the stated elements (#1406).
        if (first.IsStated != second.IsStated && !first.IsView && !second.IsView) {
          var (stated, given) = first.IsStated ? (first, second) : (second, first);
          return given.Element!.Kind != JsKind.Unknown ? given
            : (given.IsGrowable ? ListOf(stated.Element!) : ArrayOf(stated.Element!, true)) with { MayBeNull = given.MayBeNull, MayBeUndefined = given.MayBeUndefined };
        }

        if (first.IsPlain && second.IsPlain)
          return first.IsGrowable || second.IsGrowable ? ListOf(Join(first.Element!, second.Element!)) : ArrayOf(Join(first.Element!, second.Element!), true);

        // A list and a typed array: no type of the IR is both.
        if (first.IsGrowable || second.IsGrowable)
          return Conflict(first, second);

        // A view that escapes makes every view that reaches the same place one that escapes.
        if (first.IsView && second.IsView && first.ViewedElement == second.ViewedElement)
          return first.IsMemory ? first : second;

        return first.IsPlain == second.IsPlain && first.Ir == second.Ir ? first : Conflict(first, second);
      case JsKind.Record:
        // Two shapes are one record only with the same keys in the same order, each value of one kind.
        var (a, b) = (first.Shape!, second.Shape!);
        if (!a.Keys.SequenceEqual(b.Keys))
          return Conflict(first, second);

        var joined = a.Types.Zip(b.Types, Join).ToList();
        return joined.Exists(type => type.Kind == JsKind.Conflict) ? Conflict(first, second) : Record(new RecordShape(a.Registry, a.Keys, joined));
      case JsKind.Map:
        return MapOf(Join(first.Key!, second.Key!), Join(first.Element!, second.Element!));
      case JsKind.Set:
        return SetOf(Join(first.Element!, second.Element!));
      case JsKind.Sequence:
        return SequenceOf(Join(first.Element!, second.Element!));
      case JsKind.Function:
        // A value that reaches both places is a member of either shape: they are one.
        return Function(FunctionShape.Merge(first.Callable!, second.Callable!));
      case JsKind.Object:
        // Objects of several classes are objects of the nearest class of the file they all derive from.
        return first.Class!.Lineage.FirstOrDefault(second.Class!.Lineage.Contains) is { } common ? Object(common) : Conflict(first, second);
      default:
        return first;
    }
  }

  /// <summary>
  /// A type that absent values reach as well. A number that <c>undefined</c> reaches is a double:
  /// <c>ToNumber(undefined)</c> is a NaN, which no integer holds. A conflict stays one.
  /// </summary>
  private static JsType Absent(JsType present, bool isNull, bool isUndefined) {
    if (present.Kind is JsKind.Conflict or JsKind.Unknown && present.Kind != JsKind.Null)
      return present.Kind == JsKind.Unknown ? new(JsKind.Null) { MayBeNull = isNull, MayBeUndefined = isUndefined } : present;

    var widened = isUndefined && present.Kind == JsKind.Number && present.Float == 0 ? F64 : present;
    return widened with { MayBeNull = isNull, MayBeUndefined = isUndefined };
  }

  /// <summary>
  /// A record where objects of a class are kept (#1388): the literals of its shape are objects of the
  /// class synthesized for it, and the place holds objects of the nearest class both derive from.
  /// </summary>
  private static JsType Meet(JsType record, JsType instance)
    => record.Shape!.Registry.Classes.Meet(record.Shape, instance.Class!) is { } made && made.Lineage.FirstOrDefault(instance.Class!.Lineage.Contains) is { } common
      ? Object(common)
      : Conflict(instance, record);

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
