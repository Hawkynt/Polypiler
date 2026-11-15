using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Verification.Images;

/// <summary>The kinds of image type.</summary>
public enum ImageKind {

  /// <summary><c>u8</c> … <c>i64</c>: a fixed-width two's-complement integer.</summary>
  Integer,

  /// <summary><c>bool</c>: one byte, <c>00</c> or <c>01</c>.</summary>
  Boolean,

  /// <summary><c>f32</c>, <c>f64</c>: the IEEE 754 bit pattern.</summary>
  Float,

  /// <summary><c>char16</c>: one UTF-16 code unit.</summary>
  Char16,

  /// <summary><c>bigint</c>: a byte count, then the minimal two's complement.</summary>
  BigInt,

  /// <summary><c>bytes</c>: a byte count, then the bytes.</summary>
  Bytes,

  /// <summary><c>utf8</c>: a byte count, then well-formed UTF-8.</summary>
  Utf8,

  /// <summary><c>utf16</c>: a count of code units, then the units.</summary>
  Utf16,

  /// <summary><c>seq&lt;T&gt;</c>: an element count, then the elements.</summary>
  Sequence,

  /// <summary><c>opt&lt;T&gt;</c>: a presence byte, then the value when present.</summary>
  Optional,

  /// <summary><c>record{name:T, …}</c>: the fields in declaration order.</summary>
  Record,

  /// <summary><c>tuple&lt;T, U, …&gt;</c>: the elements in order.</summary>
  Tuple,

  /// <summary><c>union&lt;T|U|…&gt;</c>: a tag byte, then the alternative's image.</summary>
  Union,

  /// <summary><c>enum&lt;T&gt;</c>: the image of the underlying integer type.</summary>
  Enum,

  /// <summary><c>null</c>: the empty image; only an alternative of a union.</summary>
  Null

}

/// <summary>A named member of a record type.</summary>
/// <param name="Name">The field's name.</param>
/// <param name="Type">The field's type.</param>
public sealed record ImageField(string Name, ImageType Type);

/// <summary>
/// The type of a value image: how a value travels between a test and a language driver. Its
/// canonical spelling (<see cref="ToString"/>) has no spaces; two types are equal when their
/// spellings are.
/// </summary>
public sealed class ImageType : IEquatable<ImageType> {

  private static readonly Dictionary<string, ImageType> _names = new(StringComparer.Ordinal);

  private readonly string _text;

  private ImageType(ImageKind kind, string text, int width = 0, bool signed = false, ImageType? element = null, IReadOnlyList<ImageField>? fields = null) {
    this.Kind = kind;
    this._text = text;
    this.Width = width;
    this.Signed = signed;
    this.Element = element;
    this.Fields = fields ?? [];
  }

  /// <summary>The unsigned 8-bit integer.</summary>
  public static ImageType U8 { get; } = Named(new(ImageKind.Integer, "u8", 1));

  /// <summary>The unsigned 16-bit integer.</summary>
  public static ImageType U16 { get; } = Named(new(ImageKind.Integer, "u16", 2));

  /// <summary>The unsigned 32-bit integer.</summary>
  public static ImageType U32 { get; } = Named(new(ImageKind.Integer, "u32", 4));

  /// <summary>The unsigned 64-bit integer.</summary>
  public static ImageType U64 { get; } = Named(new(ImageKind.Integer, "u64", 8));

  /// <summary>The signed 8-bit integer.</summary>
  public static ImageType I8 { get; } = Named(new(ImageKind.Integer, "i8", 1, true));

  /// <summary>The signed 16-bit integer.</summary>
  public static ImageType I16 { get; } = Named(new(ImageKind.Integer, "i16", 2, true));

  /// <summary>The signed 32-bit integer.</summary>
  public static ImageType I32 { get; } = Named(new(ImageKind.Integer, "i32", 4, true));

  /// <summary>The signed 64-bit integer.</summary>
  public static ImageType I64 { get; } = Named(new(ImageKind.Integer, "i64", 8, true));

  /// <summary>The boolean.</summary>
  public static ImageType Bool { get; } = Named(new(ImageKind.Boolean, "bool", 1));

  /// <summary>The IEEE 754 binary32.</summary>
  public static ImageType F32 { get; } = Named(new(ImageKind.Float, "f32", 4));

  /// <summary>The IEEE 754 binary64.</summary>
  public static ImageType F64 { get; } = Named(new(ImageKind.Float, "f64", 8));

  /// <summary>One UTF-16 code unit.</summary>
  public static ImageType Char16 { get; } = Named(new(ImageKind.Char16, "char16", 2));

  /// <summary>The unbounded integer.</summary>
  public static ImageType BigInt { get; } = Named(new(ImageKind.BigInt, "bigint"));

  /// <summary>A byte string.</summary>
  public static ImageType Bytes { get; } = Named(new(ImageKind.Bytes, "bytes"));

  /// <summary>A text as UTF-8.</summary>
  public static ImageType Utf8 { get; } = Named(new(ImageKind.Utf8, "utf8"));

  /// <summary>A text as UTF-16 code units.</summary>
  public static ImageType Utf16 { get; } = Named(new(ImageKind.Utf16, "utf16"));

  /// <summary>The empty alternative of a union.</summary>
  public static ImageType Null { get; } = new(ImageKind.Null, "null");

  /// <summary>What kind of type this is.</summary>
  public ImageKind Kind { get; }

  /// <summary>The width in bytes of an integer, float, <c>bool</c> or <c>char16</c>; 0 otherwise.</summary>
  public int Width { get; }

  /// <summary>Whether an integer type is signed.</summary>
  public bool Signed { get; }

  /// <summary>The element type of a sequence or an optional, the underlying type of an enum; <see langword="null"/> otherwise.</summary>
  public ImageType? Element { get; }

  /// <summary>The fields of a record, the elements of a tuple or the alternatives of a union (with empty names); empty otherwise.</summary>
  public IReadOnlyList<ImageField> Fields { get; }

  /// <summary><c>seq&lt;T&gt;</c>.</summary>
  /// <param name="element">The element type.</param>
  public static ImageType Seq(ImageType element) => new(ImageKind.Sequence, $"seq<{Value(element)}>", element: element);

  /// <summary><c>opt&lt;T&gt;</c>.</summary>
  /// <param name="element">The type of the value when present.</param>
  public static ImageType Opt(ImageType element) => new(ImageKind.Optional, $"opt<{Value(element)}>", element: element);

  /// <summary><c>enum&lt;T&gt;</c>.</summary>
  /// <param name="underlying">The underlying integer type.</param>
  /// <exception cref="ImageException">The type is not a fixed-width integer.</exception>
  public static ImageType Enum(ImageType underlying) {
    ArgumentNullException.ThrowIfNull(underlying);
    if (underlying.Kind != ImageKind.Integer)
      throw new ImageException($"the underlying type of an enum must be an integer type, not '{underlying}'");

    return new(ImageKind.Enum, $"enum<{underlying}>", underlying.Width, underlying.Signed, underlying);
  }

  /// <summary><c>record{name:T, …}</c>.</summary>
  /// <param name="fields">The fields, in declaration order.</param>
  /// <exception cref="ImageException">There is no field, or a name is used twice.</exception>
  public static ImageType Record(params IReadOnlyList<ImageField> fields) {
    ArgumentNullException.ThrowIfNull(fields);
    var text = $"record{{{string.Join(",", fields.Select(field => $"{field.Name}:{Value(field.Type)}"))}}}";
    if (fields.Count == 0)
      throw new ImageException($"'{text}' has no field");
    if (fields.GroupBy(field => field.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice)
      throw new ImageException($"'{text}' has the field '{twice.Key}' twice");

    return new(ImageKind.Record, text, fields: [.. fields]);
  }

  /// <summary><c>tuple&lt;T, U, …&gt;</c>.</summary>
  /// <param name="elements">Two or more element types.</param>
  /// <exception cref="ImageException">There are fewer than two elements.</exception>
  public static ImageType Tuple(params IReadOnlyList<ImageType> elements) {
    ArgumentNullException.ThrowIfNull(elements);
    var text = $"tuple<{string.Join(",", elements.Select(Value))}>";
    if (elements.Count < 2)
      throw new ImageException($"'{text}' needs two or more element types");

    return new(ImageKind.Tuple, text, fields: [.. elements.Select(element => new ImageField("", element))]);
  }

  /// <summary><c>union&lt;T|U|…&gt;</c>.</summary>
  /// <param name="alternatives">Two or more alternatives; <see cref="Null"/> is the empty one.</param>
  /// <exception cref="ImageException">There are fewer than two alternatives, or one is given twice.</exception>
  public static ImageType Union(params IReadOnlyList<ImageType> alternatives) {
    ArgumentNullException.ThrowIfNull(alternatives);
    foreach (var alternative in alternatives)
      ArgumentNullException.ThrowIfNull(alternative, nameof(alternatives));

    var text = $"union<{string.Join("|", alternatives)}>";
    if (alternatives.Count < 2)
      throw new ImageException($"'{text}' needs two or more alternatives");
    if (alternatives.Distinct().Count() != alternatives.Count)
      throw new ImageException($"'{text}' lists an alternative twice");

    return new(ImageKind.Union, text, fields: [.. alternatives.Select(alternative => new ImageField("", alternative))]);
  }

  /// <summary>Reads the notation of an image type; spaces between the parts are allowed.</summary>
  /// <param name="text">The notation, e.g. <c>seq&lt;opt&lt;record{a:bigint,b:utf8}&gt;&gt;</c>.</param>
  /// <returns>The type.</returns>
  /// <exception cref="ImageException">The text names an unknown type or is not of the notation's form.</exception>
  public static ImageType Parse(string text) {
    ArgumentNullException.ThrowIfNull(text);
    return new ImageTypeParser(text).ParseAll();
  }

  /// <summary>The fixed-width type of a name: <c>u8</c> … <c>utf16</c>; <see langword="null"/> for any other text.</summary>
  internal static ImageType? Find(string name) => _names.GetValueOrDefault(name);

  /// <summary>The canonical spelling, without spaces.</summary>
  public override string ToString() => this._text;

  /// <inheritdoc />
  public bool Equals(ImageType? other) => other is not null && string.Equals(this._text, other._text, StringComparison.Ordinal);

  /// <inheritdoc />
  public override bool Equals(object? obj) => this.Equals(obj as ImageType);

  /// <inheritdoc />
  public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(this._text);

  private static ImageType Named(ImageType type) {
    _names.Add(type._text, type);
    return type;
  }

  /// <summary>A type a value can have: anything but <see cref="Null"/>, which only a union holds.</summary>
  private static string Value(ImageType type) {
    ArgumentNullException.ThrowIfNull(type);
    return type.Kind == ImageKind.Null ? throw new ImageException("'null' is only an alternative of a union") : type._text;
  }

}

/// <summary>The recursive-descent reader of the image-type notation.</summary>
internal sealed class ImageTypeParser(string text) {

  private int _position;

  public ImageType ParseAll() {
    var type = this.Type();
    this.SkipSpaces();
    if (this._position != text.Length)
      throw this.Malformed($"unexpected '{text[this._position]}'");

    return type;
  }

  private ImageType Type() {
    this.SkipSpaces();
    var name = this.Identifier();
    if (name.Length == 0)
      throw this.Malformed("an image type expected");

    switch (name) {
      case "seq":
        return ImageType.Seq(this.Single());
      case "opt":
        return ImageType.Opt(this.Single());
      case "enum":
        return ImageType.Enum(this.Single());
      case "tuple":
        return ImageType.Tuple(this.List('<', ',', '>', this.Type));
      case "union":
        return ImageType.Union(this.List('<', '|', '>', this.Alternative));
      case "record":
        return ImageType.Record(this.List('{', ',', '}', this.Field));
      default:
        return ImageType.Find(name) ?? throw new ImageException($"unknown image type '{name}'");
    }
  }

  private ImageType Single() {
    this.Expect('<');
    var element = this.Type();
    this.Expect('>');
    return element;
  }

  private ImageType Alternative() {
    var at = this.SkipSpaces();
    if (this.Identifier() == "null")
      return ImageType.Null;

    this._position = at;
    return this.Type();
  }

  private ImageField Field() {
    this.SkipSpaces();
    var name = this.Identifier();
    if (name.Length == 0)
      throw this.Malformed("a field name is missing");

    this.Expect(':');
    return new(name, this.Type());
  }

  private List<T> List<T>(char open, char separator, char close, Func<T> item) {
    this.Expect(open);
    var items = new List<T> { item() };
    while (this.Next() == separator) {
      ++this._position;
      items.Add(item());
    }

    this.Expect(close);
    return items;
  }

  private void Expect(char expected) {
    if (this.Next() != expected)
      throw this.Malformed($"'{expected}' expected");

    ++this._position;
  }

  private char? Next() {
    this.SkipSpaces();
    return this._position < text.Length ? text[this._position] : null;
  }

  private string Identifier() {
    var start = this._position;
    while (this._position < text.Length && (char.IsAsciiLetterOrDigit(text[this._position]) || text[this._position] == '_'))
      ++this._position;

    return text[start..this._position];
  }

  private int SkipSpaces() {
    while (this._position < text.Length && char.IsWhiteSpace(text[this._position]))
      ++this._position;

    return this._position;
  }

  private ImageException Malformed(string problem) => new($"'{text}' is not an image type: {problem} at position {this._position}");

}
