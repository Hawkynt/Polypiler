using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Python.Model;

/// <summary>The shapes a static type of the import can have.</summary>
internal enum PyKind {

  /// <summary>Not known yet: the inference has not reached a value of the variable.</summary>
  Unknown,

  /// <summary>A <c>bool</c>.</summary>
  Bool,

  /// <summary>An <c>int</c>, exact within 64 bits.</summary>
  Int,

  /// <summary>A <c>float</c>: an IEEE 754 binary64 number.</summary>
  Float,

  /// <summary><c>None</c>: the result of a function that returns nothing, or the constant itself.</summary>
  None,

  /// <summary>A <c>str</c>: a sequence of code points, held as UTF-16 code units.</summary>
  Str,

  /// <summary>A <c>bytes</c>: an immutable array of integers 0 to 255.</summary>
  Bytes,

  /// <summary>A <c>bytearray</c>: a mutable array of integers 0 to 255 of a fixed length.</summary>
  ByteArray,

  /// <summary>A <c>list</c> whose elements all have one type.</summary>
  List,

  /// <summary>A <c>tuple</c> of a fixed length, each position with its own type.</summary>
  Tuple,

  /// <summary>A reference to an object of a class of the module, or to an exception.</summary>
  Object,

  /// <summary>A member of an <c>Enum</c> or <c>IntEnum</c> of the module.</summary>
  Enum,

  /// <summary>A value of a type, or <c>None</c>.</summary>
  Optional,

  /// <summary>A generator: the values a function with <c>yield</c> produces, once.</summary>
  Iterator,

  /// <summary>A table of the module: a list or tuple of numbers a module-level name holds and nothing changes.</summary>
  Table

}

/// <summary>
/// The static type of a Python value as the import knows it. <see cref="Int"/> is Python's unbounded
/// <c>int</c>, carried as a 64-bit integer whose every operation either gives the exact result or
/// raises <c>OverflowError</c>; it never wraps around. A type compares by its structure.
/// </summary>
internal sealed class PyType : IEquatable<PyType> {

  private PyType(PyKind kind, PyType? element = null, IReadOnlyList<PyType>? items = null, ClassModel? @class = null) {
    this.Kind = kind;
    this.Element = element;
    this.Items = items ?? [];
    this.Class = @class;
  }

  /// <summary>The shape.</summary>
  public PyKind Kind { get; }

  /// <summary>The element type of a list, an iterator or a table, the type an optional holds; otherwise <see langword="null"/>.</summary>
  public PyType? Element { get; }

  /// <summary>The types of the positions of a tuple; empty for every other type.</summary>
  public IReadOnlyList<PyType> Items { get; }

  /// <summary>The class of an object or an enum; otherwise <see langword="null"/>.</summary>
  public ClassModel? Class { get; }

  /// <summary>Not known yet.</summary>
  public static PyType Unknown { get; } = new(PyKind.Unknown);

  /// <summary>A <c>bool</c>.</summary>
  public static PyType Bool { get; } = new(PyKind.Bool);

  /// <summary>An <c>int</c>.</summary>
  public static PyType Int { get; } = new(PyKind.Int);

  /// <summary>A <c>float</c>.</summary>
  public static PyType Float { get; } = new(PyKind.Float);

  /// <summary><c>None</c>.</summary>
  public static PyType None { get; } = new(PyKind.None);

  /// <summary>A <c>str</c>.</summary>
  public static PyType Str { get; } = new(PyKind.Str);

  /// <summary>A <c>bytes</c>.</summary>
  public static PyType Bytes { get; } = new(PyKind.Bytes);

  /// <summary>A <c>bytearray</c>.</summary>
  public static PyType ByteArray { get; } = new(PyKind.ByteArray);

  /// <summary>A list of elements of a type.</summary>
  public static PyType List(PyType element) => new(PyKind.List, element);

  /// <summary>A tuple of values of the types, in order.</summary>
  public static PyType Tuple(IReadOnlyList<PyType> items) => new(PyKind.Tuple, items: items);

  /// <summary>A reference to an object of a class.</summary>
  public static PyType Object(ClassModel type) => new(PyKind.Object, @class: type);

  /// <summary>A member of an enum.</summary>
  public static PyType Enum(ClassModel type) => new(PyKind.Enum, @class: type);

  /// <summary>A value of a type, or <c>None</c>.</summary>
  public static PyType Optional(PyType underlying) => underlying.Kind == PyKind.Optional ? underlying : new(PyKind.Optional, underlying);

  /// <summary>The values a generator produces.</summary>
  public static PyType Iterator(PyType element) => new(PyKind.Iterator, element);

  /// <summary>A table of numbers of the module.</summary>
  public static PyType Table(PyType element) => new(PyKind.Table, element);

  /// <summary>Whether the type is a number: <c>bool</c>, <c>int</c> or <c>float</c>.</summary>
  public bool IsNumber => this.Kind is PyKind.Bool or PyKind.Int or PyKind.Float;

  /// <summary>Whether the type is an array of bytes: <c>bytes</c> or <c>bytearray</c>.</summary>
  public bool IsBytes => this.Kind is PyKind.Bytes or PyKind.ByteArray;

  /// <summary>Whether a value of the type is a sequence read by index: a list, a tuple, a str, bytes or a table.</summary>
  public bool IsSequence => this.Kind is PyKind.List or PyKind.Str or PyKind.Bytes or PyKind.ByteArray or PyKind.Table or PyKind.Tuple;

  /// <summary>Whether the type, or a type inside it, is not known yet.</summary>
  public bool IsIncomplete => this.Kind == PyKind.Unknown || (this.Element?.IsIncomplete ?? false) || this.Items.Any(item => item.IsIncomplete);

  /// <inheritdoc/>
  public bool Equals(PyType? other)
    => other is not null && this.Kind == other.Kind && Equals(this.Element, other.Element) && ReferenceEquals(this.Class, other.Class) && this.Items.SequenceEqual(other.Items);

  /// <inheritdoc/>
  public override bool Equals(object? obj) => this.Equals(obj as PyType);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Kind, this.Element, this.Class, this.Items.Count);

  /// <summary>Two types are the same type.</summary>
  public static bool operator ==(PyType? left, PyType? right) => Equals(left, right);

  /// <summary>Two types are different types.</summary>
  public static bool operator !=(PyType? left, PyType? right) => !Equals(left, right);

  /// <summary>The name of the type as Python writes it, for example <c>list[int]</c>.</summary>
  public override string ToString() => this.Kind switch {
    PyKind.Bool => "bool",
    PyKind.Int => "int",
    PyKind.Float => "float",
    PyKind.None => "None",
    PyKind.Str => "str",
    PyKind.Bytes => "bytes",
    PyKind.ByteArray => "bytearray",
    PyKind.List => $"list[{this.Element}]",
    PyKind.Tuple => $"tuple[{string.Join(", ", this.Items)}]",
    PyKind.Object or PyKind.Enum => this.Class!.Name,
    PyKind.Optional => $"{this.Element} | None",
    PyKind.Iterator => $"Iterator[{this.Element}]",
    PyKind.Table => $"tuple[{this.Element}, ...]",
    _ => "unknown"
  };

}

/// <summary>The rules of the types: their IR type and how two of them join.</summary>
internal static class PyTypes {

  /// <summary>The IR type of a Python type.</summary>
  public static IrType Ir(PyType type) => type.Kind switch {
    PyKind.Bool => IrType.Bool,
    PyKind.Int => IrType.I64,
    PyKind.Float => IrType.F64,
    PyKind.None => IrType.Void,
    PyKind.Str => IrType.String,
    PyKind.Bytes or PyKind.ByteArray => IrType.Array(IrType.U8),
    PyKind.List => IrType.List(Ir(type.Element!)),
    PyKind.Tuple => IrType.Struct(Tuples.Of(type)),
    PyKind.Object => type.Class!.Reference,
    PyKind.Enum => type.Class!.EnumType!.Type,
    PyKind.Optional => IsReference(type.Element!) ? Ir(type.Element!) : IrType.Optional(Ir(type.Element!)),
    PyKind.Iterator => IrType.Sequence(Ir(type.Element!), false),
    PyKind.Table => IrType.Array(Ir(type.Element!)),
    _ => throw new ArgumentOutOfRangeException(nameof(type), type, "An unknown type has no IR type.")
  };

  /// <summary>Whether a value of the type is a reference the IR writes <c>null</c> for, so that an optional of it is the reference itself.</summary>
  public static bool IsReference(PyType type) => type.Kind == PyKind.Object;

  /// <summary>Whether a type can be the type an optional holds: a number, a tuple, an enum member or an object.</summary>
  public static bool Optionable(PyType type) => type.IsNumber || type.Kind is PyKind.Tuple or PyKind.Enum or PyKind.Object;

  /// <summary>Whether a type is a number: <c>bool</c>, <c>int</c> or <c>float</c>.</summary>
  public static bool IsNumber(PyType type) => type.IsNumber;

  /// <summary>
  /// The type that holds the values of two types: the wider of two numbers, since a <c>bool</c> is an
  /// <c>int</c> and an <c>int</c> converts to a <c>float</c>; an optional of a type and <c>None</c>;
  /// a list or tuple whose unknown parts the other one knows. <see langword="null"/> where no type holds both.
  /// </summary>
  public static PyType? Join(PyType first, PyType second) {
    if (first.Kind == PyKind.Unknown || first == second)
      return second;

    if (second.Kind == PyKind.Unknown)
      return first;

    if (first.IsNumber && second.IsNumber)
      return first.Kind > second.Kind ? first : second;

    if (first.Kind == PyKind.None)
      return Optionable(second) ? PyType.Optional(second) : second.Kind == PyKind.Optional ? second : null;

    if (second.Kind == PyKind.None)
      return Join(second, first);

    if (first.Kind == PyKind.Optional || second.Kind == PyKind.Optional) {
      var inner = Join(first.Kind == PyKind.Optional ? first.Element! : first, second.Kind == PyKind.Optional ? second.Element! : second);
      return inner is not null && Optionable(inner) ? PyType.Optional(inner) : null;
    }

    if (first.Kind != second.Kind)
      return null;

    switch (first.Kind) {
      case PyKind.List or PyKind.Iterator or PyKind.Table:
        // Elements are never converted: a list of int is no list of float.
        return Exact(first.Element!, second.Element!) is { } element ? first.Kind switch {
          PyKind.List => PyType.List(element),
          PyKind.Iterator => PyType.Iterator(element),
          _ => PyType.Table(element)
        } : null;
      case PyKind.Tuple when first.Items.Count == second.Items.Count:
        var items = new List<PyType>();
        foreach (var (a, b) in first.Items.Zip(second.Items)) {
          if (Exact(a, b) is not { } item)
            return null;

          items.Add(item);
        }

        return PyType.Tuple(items);
      default:
        return null;
    }
  }

  /// <summary>The type two element types have where neither is converted: the same, or the one known.</summary>
  private static PyType? Exact(PyType first, PyType second) {
    if (first.IsIncomplete || second.IsIncomplete)
      return Join(first, second);

    return first == second ? first : null;
  }

  /// <summary>The name of a type as Python writes it.</summary>
  public static string Name(PyType type) => type.ToString();

}
