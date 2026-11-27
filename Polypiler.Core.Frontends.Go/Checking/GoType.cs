using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Frontends.Go.Checking;

/// <summary>
/// A type of Go (The Go Programming Language Specification, "Types"): the predeclared types, defined
/// types and the type literals, written as <c>types.TypeString</c> writes them with the names of the
/// checked package unqualified.
/// </summary>
internal abstract class GoType {

  /// <summary>The underlying type: the type itself for every type but a defined one.</summary>
  public virtual GoType Underlying => this;

  /// <inheritdoc/>
  public abstract override string ToString();

}

/// <summary>The predeclared types and the types of untyped constants.</summary>
internal enum GoBasicKind {
  Bool,
  Int,
  Int8,
  Int16,
  Int32,
  Int64,
  Uint,
  Uint8,
  Uint16,
  Uint32,
  Uint64,
  Uintptr,
  Float32,
  Float64,
  Complex64,
  Complex128,
  String,
  UntypedBool,
  UntypedInt,
  UntypedRune,
  UntypedFloat,
  UntypedComplex,
  UntypedString,
  UntypedNil
}

/// <summary>A predeclared type, or the type of an untyped constant; <c>byte</c> and <c>rune</c> are <c>uint8</c> and <c>int32</c> under their own names.</summary>
internal sealed class GoBasic(GoBasicKind kind, string name) : GoType {

  /// <summary>The kind.</summary>
  public GoBasicKind Kind { get; } = kind;

  /// <summary>The name.</summary>
  public string Name { get; } = name;

  /// <summary>Whether the type is an integer type, an untyped integer or rune among them.</summary>
  public bool IsInteger => this.Kind is >= GoBasicKind.Int and <= GoBasicKind.Uintptr or GoBasicKind.UntypedInt or GoBasicKind.UntypedRune;

  /// <summary>Whether the type is an unsigned integer type.</summary>
  public bool IsUnsigned => this.Kind is >= GoBasicKind.Uint and <= GoBasicKind.Uintptr;

  /// <summary>Whether the type is a floating-point type.</summary>
  public bool IsFloat => this.Kind is GoBasicKind.Float32 or GoBasicKind.Float64 or GoBasicKind.UntypedFloat;

  /// <summary>Whether the type is a complex type.</summary>
  public bool IsComplex => this.Kind is GoBasicKind.Complex64 or GoBasicKind.Complex128 or GoBasicKind.UntypedComplex;

  /// <summary>Whether the type is numeric.</summary>
  public bool IsNumeric => this.IsInteger || this.IsFloat || this.IsComplex;

  /// <summary>Whether the type is a string type.</summary>
  public bool IsString => this.Kind is GoBasicKind.String or GoBasicKind.UntypedString;

  /// <summary>Whether the type is a boolean type.</summary>
  public bool IsBoolean => this.Kind is GoBasicKind.Bool or GoBasicKind.UntypedBool;

  /// <summary>Whether the type is the type of an untyped value.</summary>
  public bool IsUntyped => this.Kind >= GoBasicKind.UntypedBool;

  /// <summary>Whether the values of the type are ordered.</summary>
  public bool IsOrdered => this.IsInteger || this.IsFloat || this.IsString;

  /// <summary>The size in bits of a sized type: 64 for <c>int</c>, <c>uint</c> and <c>uintptr</c>; 0 for any other.</summary>
  public int Bits => this.Kind switch {
    GoBasicKind.Int8 or GoBasicKind.Uint8 => 8,
    GoBasicKind.Int16 or GoBasicKind.Uint16 => 16,
    GoBasicKind.Int32 or GoBasicKind.Uint32 or GoBasicKind.Float32 => 32,
    GoBasicKind.Int or GoBasicKind.Int64 or GoBasicKind.Uint or GoBasicKind.Uint64 or GoBasicKind.Uintptr or GoBasicKind.Float64 or GoBasicKind.Complex64 => 64,
    GoBasicKind.Complex128 => 128,
    _ => 0
  };

  /// <inheritdoc/>
  public override string ToString() => this.Name;

}

/// <summary>A defined type: a name declared with a type declaration, its underlying type set once it is resolved, and its methods.</summary>
internal sealed class GoNamed(string name, string? package) : GoType {

  private GoType? _underlying;

  /// <summary>The name.</summary>
  public string Name { get; } = name;

  /// <summary>The import path of the package that declares the type; <see langword="null"/> for the checked one and the universe.</summary>
  public string? Package { get; } = package;

  /// <summary>Whether the underlying type is resolved.</summary>
  public bool IsResolved => this._underlying is not null;

  /// <summary>The methods declared with the type as receiver base type.</summary>
  public List<GoObject> Methods { get; } = [];

  /// <inheritdoc/>
  public override GoType Underlying => this._underlying ?? GoTypes.Invalid;

  /// <summary>Sets the underlying type: the underlying type of <paramref name="type"/>.</summary>
  public void Resolve(GoType type) => this._underlying = type.Underlying;

  /// <inheritdoc/>
  public override string ToString() => this.Package is null ? this.Name : this.Package + "." + this.Name;

}

/// <summary>A pointer type <c>*T</c>.</summary>
internal sealed class GoPointer(GoType element) : GoType {

  /// <summary>The base type.</summary>
  public GoType Element { get; } = element;

  /// <inheritdoc/>
  public override string ToString() => "*" + this.Element;

}

/// <summary>A slice type <c>[]T</c>.</summary>
internal sealed class GoSlice(GoType element) : GoType {

  /// <summary>The element type.</summary>
  public GoType Element { get; } = element;

  /// <inheritdoc/>
  public override string ToString() => "[]" + this.Element;

}

/// <summary>An array type <c>[N]T</c>.</summary>
internal sealed class GoArray(long length, GoType element) : GoType {

  /// <summary>The length.</summary>
  public long Length { get; } = length;

  /// <summary>The element type.</summary>
  public GoType Element { get; } = element;

  /// <inheritdoc/>
  public override string ToString() => $"[{this.Length.ToString(CultureInfo.InvariantCulture)}]{this.Element}";

}

/// <summary>A map type <c>map[K]V</c>.</summary>
internal sealed class GoMap(GoType key, GoType element) : GoType {

  /// <summary>The key type.</summary>
  public GoType Key { get; } = key;

  /// <summary>The element type.</summary>
  public GoType Element { get; } = element;

  /// <inheritdoc/>
  public override string ToString() => $"map[{this.Key}]{this.Element}";

}

/// <summary>A channel type: <c>chan T</c>, <c>chan&lt;- T</c> (send) or <c>&lt;-chan T</c> (receive).</summary>
internal sealed class GoChan(string direction, GoType element) : GoType {

  /// <summary>The direction: <c>both</c>, <c>send</c> or <c>recv</c>.</summary>
  public string Direction { get; } = direction;

  /// <summary>The element type.</summary>
  public GoType Element { get; } = element;

  /// <inheritdoc/>
  public override string ToString() => this.Direction switch {
    "send" => "chan<- " + this.Element,
    "recv" => "<-chan " + this.Element,
    _ => this.Element is GoChan { Direction: "recv" } ? $"chan ({this.Element})" : "chan " + this.Element
  };

}

/// <summary>A field of a struct type.</summary>
/// <param name="Name">The name; the type name of an embedded field.</param>
/// <param name="Type">The type.</param>
/// <param name="Embedded">Whether the field is embedded.</param>
/// <param name="Tag">The tag, or <see langword="null"/>.</param>
internal sealed record GoField(string Name, GoType Type, bool Embedded, string? Tag);

/// <summary>A struct type.</summary>
internal sealed class GoStruct(IReadOnlyList<GoField> fields) : GoType {

  /// <summary>The fields, in order.</summary>
  public IReadOnlyList<GoField> Fields { get; } = fields;

  /// <summary>The field of a name, or <see langword="null"/>.</summary>
  public GoField? Field(string name) => this.Fields.FirstOrDefault(field => field.Name == name);

  /// <inheritdoc/>
  public override string ToString() {
    var builder = new StringBuilder("struct{");
    for (var index = 0; index < this.Fields.Count; ++index) {
      var field = this.Fields[index];
      if (index > 0)
        builder.Append("; ");

      if (!field.Embedded)
        builder.Append(field.Name).Append(' ');

      builder.Append(field.Type);
      if (field.Tag is not null)
        builder.Append(' ').Append(GoConstant.Quote(Encoding.UTF8.GetBytes(field.Tag)));
    }

    return builder.Append('}').ToString();
  }

}

/// <summary>A parameter or result of a signature.</summary>
/// <param name="Name">The name, or empty.</param>
/// <param name="Type">The type; of a variadic parameter the slice type.</param>
internal sealed record GoParameter(string Name, GoType Type);

/// <summary>A function type: parameters, results and whether the last parameter is variadic.</summary>
internal sealed class GoSignature(IReadOnlyList<GoParameter> parameters, IReadOnlyList<GoParameter> results, bool variadic) : GoType {

  /// <summary>The parameters.</summary>
  public IReadOnlyList<GoParameter> Parameters { get; } = parameters;

  /// <summary>The results.</summary>
  public IReadOnlyList<GoParameter> Results { get; } = results;

  /// <summary>Whether the last parameter is variadic; its type is then a slice.</summary>
  public bool Variadic { get; } = variadic;

  /// <summary>The type of a call's value: void, the one result, or the tuple of several.</summary>
  public GoType? Result => this.Results.Count switch {
    0 => null,
    1 => this.Results[0].Type,
    _ => new GoTuple([.. this.Results.Select(result => result.Type)])
  };

  /// <summary>The signature as a method writes it, without <c>func</c>.</summary>
  public string Text {
    get {
      var builder = new StringBuilder("(");
      for (var index = 0; index < this.Parameters.Count; ++index) {
        if (index > 0)
          builder.Append(", ");

        var parameter = this.Parameters[index];
        if (parameter.Name.Length > 0)
          builder.Append(parameter.Name).Append(' ');

        if (this.Variadic && index == this.Parameters.Count - 1 && parameter.Type is GoSlice slice)
          builder.Append("...").Append(slice.Element);
        else
          builder.Append(parameter.Type);
      }

      builder.Append(')');
      if (this.Results.Count == 1 && this.Results[0].Name.Length == 0)
        builder.Append(' ').Append(this.Results[0].Type);
      else if (this.Results.Count > 0)
        builder.Append(" (").Append(string.Join(", ", this.Results.Select(result => result.Name.Length > 0 ? $"{result.Name} {result.Type}" : result.Type.ToString()))).Append(')');

      return builder.ToString();
    }
  }

  /// <inheritdoc/>
  public override string ToString() => "func" + this.Text;

}

/// <summary>A method of an interface type.</summary>
/// <param name="Name">The name.</param>
/// <param name="Signature">The signature.</param>
internal sealed record GoMethod(string Name, GoSignature Signature);

/// <summary>An interface type with its methods; the empty interface is written <c>any</c> where the source names it so.</summary>
internal sealed class GoInterface(IReadOnlyList<GoMethod> methods, bool isAny = false) : GoType {

  /// <summary>The methods, sorted by name.</summary>
  public IReadOnlyList<GoMethod> Methods { get; } = [.. methods.OrderBy(method => method.Name, System.StringComparer.Ordinal)];

  /// <summary>Whether the type is the predeclared alias <c>any</c>.</summary>
  public bool IsAny { get; } = isAny;

  /// <inheritdoc/>
  public override string ToString() => this.IsAny ? "any" : "interface{" + string.Join("; ", this.Methods.Select(method => method.Name + method.Signature.Text)) + "}";

}

/// <summary>The types of the results of a call with several results.</summary>
internal sealed class GoTuple(IReadOnlyList<GoType> types) : GoType {

  /// <summary>The types.</summary>
  public IReadOnlyList<GoType> Types { get; } = types;

  /// <inheritdoc/>
  public override string ToString() => "(" + string.Join(", ", this.Types) + ")";

}

/// <summary>The type of an expression the checker could give none.</summary>
internal sealed class GoInvalid : GoType {

  /// <inheritdoc/>
  public override string ToString() => "invalid type";

}

/// <summary>The predeclared types and the relations between types: identity, the predicates of the specification, default types.</summary>
internal static class GoTypes {

  /// <summary>The invalid type.</summary>
  public static GoType Invalid { get; } = new GoInvalid();

  private static readonly Dictionary<GoBasicKind, GoBasic> _basic = new() {
    [GoBasicKind.Bool] = new(GoBasicKind.Bool, "bool"),
    [GoBasicKind.Int] = new(GoBasicKind.Int, "int"),
    [GoBasicKind.Int8] = new(GoBasicKind.Int8, "int8"),
    [GoBasicKind.Int16] = new(GoBasicKind.Int16, "int16"),
    [GoBasicKind.Int32] = new(GoBasicKind.Int32, "int32"),
    [GoBasicKind.Int64] = new(GoBasicKind.Int64, "int64"),
    [GoBasicKind.Uint] = new(GoBasicKind.Uint, "uint"),
    [GoBasicKind.Uint8] = new(GoBasicKind.Uint8, "uint8"),
    [GoBasicKind.Uint16] = new(GoBasicKind.Uint16, "uint16"),
    [GoBasicKind.Uint32] = new(GoBasicKind.Uint32, "uint32"),
    [GoBasicKind.Uint64] = new(GoBasicKind.Uint64, "uint64"),
    [GoBasicKind.Uintptr] = new(GoBasicKind.Uintptr, "uintptr"),
    [GoBasicKind.Float32] = new(GoBasicKind.Float32, "float32"),
    [GoBasicKind.Float64] = new(GoBasicKind.Float64, "float64"),
    [GoBasicKind.Complex64] = new(GoBasicKind.Complex64, "complex64"),
    [GoBasicKind.Complex128] = new(GoBasicKind.Complex128, "complex128"),
    [GoBasicKind.String] = new(GoBasicKind.String, "string"),
    [GoBasicKind.UntypedBool] = new(GoBasicKind.UntypedBool, "untyped bool"),
    [GoBasicKind.UntypedInt] = new(GoBasicKind.UntypedInt, "untyped int"),
    [GoBasicKind.UntypedRune] = new(GoBasicKind.UntypedRune, "untyped rune"),
    [GoBasicKind.UntypedFloat] = new(GoBasicKind.UntypedFloat, "untyped float"),
    [GoBasicKind.UntypedComplex] = new(GoBasicKind.UntypedComplex, "untyped complex"),
    [GoBasicKind.UntypedString] = new(GoBasicKind.UntypedString, "untyped string"),
    [GoBasicKind.UntypedNil] = new(GoBasicKind.UntypedNil, "untyped nil")
  };

  /// <summary>The predeclared type of a kind.</summary>
  public static GoBasic Basic(GoBasicKind kind) => _basic[kind];

  /// <summary><c>byte</c>, the alias of <c>uint8</c>.</summary>
  public static GoBasic Byte { get; } = new(GoBasicKind.Uint8, "byte");

  /// <summary><c>rune</c>, the alias of <c>int32</c>.</summary>
  public static GoBasic Rune { get; } = new(GoBasicKind.Int32, "rune");

  /// <summary><c>bool</c>.</summary>
  public static GoBasic Bool => Basic(GoBasicKind.Bool);

  /// <summary><c>int</c>.</summary>
  public static GoBasic Int => Basic(GoBasicKind.Int);

  /// <summary><c>uint</c>.</summary>
  public static GoBasic Uint => Basic(GoBasicKind.Uint);

  /// <summary><c>string</c>.</summary>
  public static GoBasic String => Basic(GoBasicKind.String);

  /// <summary><c>untyped bool</c>.</summary>
  public static GoBasic UntypedBool => Basic(GoBasicKind.UntypedBool);

  /// <summary><c>untyped nil</c>.</summary>
  public static GoBasic UntypedNil => Basic(GoBasicKind.UntypedNil);

  /// <summary>The empty interface.</summary>
  public static GoInterface EmptyInterface { get; } = new([]);

  /// <summary><c>any</c>.</summary>
  public static GoInterface Any { get; } = new([], true);

  /// <summary>The basic type a type is or has as its underlying type, or <see langword="null"/>.</summary>
  public static GoBasic? AsBasic(GoType? type) => type?.Underlying as GoBasic;

  /// <summary>Whether a type is the type of an untyped value.</summary>
  public static bool IsUntyped(GoType? type) => type is GoBasic { IsUntyped: true };

  /// <summary>Whether a type is an integer type, by its underlying type.</summary>
  public static bool IsInteger(GoType? type) => AsBasic(type) is { IsInteger: true };

  /// <summary>Whether a type is an unsigned integer type.</summary>
  public static bool IsUnsigned(GoType? type) => AsBasic(type) is { IsUnsigned: true };

  /// <summary>Whether a type is a floating-point type.</summary>
  public static bool IsFloat(GoType? type) => AsBasic(type) is { IsFloat: true };

  /// <summary>Whether a type is a complex type.</summary>
  public static bool IsComplex(GoType? type) => AsBasic(type) is { IsComplex: true };

  /// <summary>Whether a type is numeric.</summary>
  public static bool IsNumeric(GoType? type) => AsBasic(type) is { IsNumeric: true };

  /// <summary>Whether a type is a string type.</summary>
  public static bool IsString(GoType? type) => AsBasic(type) is { IsString: true };

  /// <summary>Whether a type is a boolean type.</summary>
  public static bool IsBoolean(GoType? type) => AsBasic(type) is { IsBoolean: true };

  /// <summary>Whether the values of a type are ordered.</summary>
  public static bool IsOrdered(GoType? type) => AsBasic(type) is { IsOrdered: true };

  /// <summary>Whether a type is a constant type: a boolean, numeric or string type.</summary>
  public static bool IsConstType(GoType? type) => AsBasic(type) is { IsBoolean: true } or { IsNumeric: true } or { IsString: true };

  /// <summary>Whether a value of the type may be <c>nil</c>.</summary>
  public static bool HasNil(GoType type) => type.Underlying is GoPointer or GoSlice or GoMap or GoChan or GoSignature or GoInterface
    || type.Underlying is GoBasic { Kind: GoBasicKind.UntypedNil };

  /// <summary>Whether a type has a name: a predeclared or a defined type.</summary>
  public static bool HasName(GoType type) => type is GoBasic or GoNamed;

  /// <summary>Whether a type is an interface type.</summary>
  public static bool IsInterface(GoType type) => type.Underlying is GoInterface;

  /// <summary>The default type of an untyped type (bool, int, rune, float64, complex128, string); any other type is its own.</summary>
  public static GoType Default(GoType type) => type is GoBasic { IsUntyped: true } basic ? basic.Kind switch {
    GoBasicKind.UntypedBool => Bool,
    GoBasicKind.UntypedInt => Int,
    GoBasicKind.UntypedRune => Rune,
    GoBasicKind.UntypedFloat => Basic(GoBasicKind.Float64),
    GoBasicKind.UntypedComplex => Basic(GoBasicKind.Complex128),
    GoBasicKind.UntypedString => String,
    _ => type
  } : type;

  /// <summary>Whether two types are identical (The Go Programming Language Specification, "Type identity").</summary>
  public static bool Identical(GoType x, GoType y) {
    if (ReferenceEquals(x, y))
      return true;

    switch (x) {
      case GoBasic a when y is GoBasic b:
        return a.Kind == b.Kind;
      case GoPointer a when y is GoPointer b:
        return Identical(a.Element, b.Element);
      case GoSlice a when y is GoSlice b:
        return Identical(a.Element, b.Element);
      case GoArray a when y is GoArray b:
        return a.Length == b.Length && Identical(a.Element, b.Element);
      case GoMap a when y is GoMap b:
        return Identical(a.Key, b.Key) && Identical(a.Element, b.Element);
      case GoChan a when y is GoChan b:
        return a.Direction == b.Direction && Identical(a.Element, b.Element);
      case GoStruct a when y is GoStruct b:
        return a.Fields.Count == b.Fields.Count && a.Fields.Zip(b.Fields).All(pair
          => pair.First.Name == pair.Second.Name && pair.First.Embedded == pair.Second.Embedded && pair.First.Tag == pair.Second.Tag && Identical(pair.First.Type, pair.Second.Type));
      case GoSignature a when y is GoSignature b:
        return a.Variadic == b.Variadic && a.Parameters.Count == b.Parameters.Count && a.Results.Count == b.Results.Count
          && a.Parameters.Zip(b.Parameters).All(pair => Identical(pair.First.Type, pair.Second.Type))
          && a.Results.Zip(b.Results).All(pair => Identical(pair.First.Type, pair.Second.Type));
      case GoInterface a when y is GoInterface b:
        return a.Methods.Count == b.Methods.Count && a.Methods.Zip(b.Methods).All(pair => pair.First.Name == pair.Second.Name && Identical(pair.First.Signature, pair.Second.Signature));
      case GoTuple a when y is GoTuple b:
        return a.Types.Count == b.Types.Count && a.Types.Zip(b.Types).All(pair => Identical(pair.First, pair.Second));
      default:
        return false;
    }
  }

  /// <summary>Whether the values of a type can be compared with <c>==</c> (The Go Programming Language Specification, "Comparison operators").</summary>
  public static bool Comparable(GoType type) => type.Underlying switch {
    GoBasic basic => basic.Kind != GoBasicKind.UntypedNil,
    GoPointer or GoChan or GoInterface => true,
    GoStruct structure => structure.Fields.All(field => Comparable(field.Type)),
    GoArray array => Comparable(array.Element),
    _ => false
  };

  /// <summary>The larger of two untyped types (<c>maxType</c>): of two numeric ones the later of int, rune, float, complex; of others the type where they are the same.</summary>
  public static GoType? MaxType(GoType x, GoType y) {
    if (x is GoBasic a && y is GoBasic b && a.IsNumeric && b.IsNumeric)
      return a.Kind >= b.Kind ? a : b;

    return Identical(x, y) ? x : null;
  }

}
