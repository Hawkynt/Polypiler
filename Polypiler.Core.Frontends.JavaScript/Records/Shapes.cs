using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The keys of an object literal in source order and the type of the value of each: the shape a
/// value record has. Two shapes are equal when keys and types are, whichever literal they come from.
/// </summary>
internal sealed class RecordShape : IEquatable<RecordShape> {

  /// <summary>Makes a shape.</summary>
  /// <param name="registry">The shapes of the import, which turn this one into a struct.</param>
  /// <param name="keys">The keys, in source order.</param>
  /// <param name="types">The type of the value of each key.</param>
  public RecordShape(Shapes registry, IReadOnlyList<string> keys, IReadOnlyList<JsType> types) {
    this.Registry = registry;
    this.Keys = keys;
    this.Types = types;
  }

  /// <summary>The shapes of the import.</summary>
  public Shapes Registry { get; }

  /// <summary>The keys, in source order.</summary>
  public IReadOnlyList<string> Keys { get; }

  /// <summary>The type of the value of each key.</summary>
  public IReadOnlyList<JsType> Types { get; }

  /// <summary>The type of the value of a key, or <see langword="null"/> when the shape has no such key.</summary>
  public JsType? TypeOf(string key) => this.Keys.ToList().IndexOf(key) is var index and >= 0 ? this.Types[index] : null;

  /// <summary>The shape as a diagnostic names it: <c>{ x, y }</c>.</summary>
  public override string ToString() => this.Keys.Count == 0 ? "{}" : $"{{ {string.Join(", ", this.Keys)} }}";

  public bool Equals(RecordShape? other) => other is not null && this.Keys.SequenceEqual(other.Keys) && this.Types.SequenceEqual(other.Types);

  public override bool Equals(object? obj) => this.Equals(obj as RecordShape);

  public override int GetHashCode() => this.Keys.Aggregate(this.Keys.Count, (hash, key) => HashCode.Combine(hash, key));

}

/// <summary>
/// The structs of one import: one <see cref="IrStructType"/> per distinct shape - the keys in
/// source order and the IR type of each value - named after the first variable or parameter, in
/// source order, that holds a value of it, in PascalCase (<c>point</c> becomes <c>Point</c>), a
/// shape inside another after its key, and with a number appended where a name is taken already.
/// <para>
/// The names are given once the types are inferred, before any struct is made: a struct made
/// earlier would be named before the variable that names it is known.
/// </para>
/// </summary>
internal sealed class Shapes {

  private readonly Dictionary<string, string> _names = [];
  private readonly HashSet<string> _taken = [];
  private readonly Dictionary<string, IrStructType> _declared = [];

  /// <summary>Whether the names are given, so that structs can be made.</summary>
  public bool IsNamed { get; private set; }

  /// <summary>Every struct made, in the order it was made.</summary>
  public IReadOnlyCollection<IrStructType> Declared => this._declared.Values;

  /// <summary>
  /// Names the shape of a type, and those inside it, after a variable unless they have a name
  /// already. The elements of an array are named after it only when asked to, after every variable
  /// that holds a record itself has named its own.
  /// </summary>
  /// <param name="type">The type of the variable.</param>
  /// <param name="name">The name of the variable.</param>
  /// <param name="elements">Whether the elements of an array are named after the array.</param>
  public void Prefer(JsType type, string name, bool elements = false) {
    switch (type.Kind) {
      case JsKind.Array when elements:
        this.Prefer(type.Element!, name, true);
        break;
      case JsKind.Record:
        var shape = type.Shape!;
        var signature = this.Signature(shape);
        if (!this._names.ContainsKey(signature))
          this._names[signature] = this.Unique(name);

        for (var index = 0; index < shape.Keys.Count; ++index)
          this.Prefer(shape.Types[index], shape.Keys[index]);

        break;
    }
  }

  /// <summary>Ends the naming: from now on structs are made, and a shape without a name is named after its first key.</summary>
  public void Seal() => this.IsNamed = true;

  /// <summary>The struct of a shape, made the first time it is asked for.</summary>
  /// <exception cref="InvalidOperationException">The names are not given yet.</exception>
  public IrStructType Declare(RecordShape shape) {
    if (!this.IsNamed)
      throw new InvalidOperationException("A struct is made before the shapes are named.");

    var signature = this.Signature(shape);
    if (this._declared.TryGetValue(signature, out var known))
      return known;

    if (!this._names.TryGetValue(signature, out var name))
      this._names[signature] = name = this.Unique(shape.Keys.FirstOrDefault() ?? "record");

    var fields = shape.Keys.Select((key, index) => new IrField(key, Field(shape.Types[index]), true)).ToList();
    var declared = new IrStructType(name, fields);
    this._declared[signature] = declared;
    return declared;
  }

  /// <summary>The IR type of a value of a record: a place nothing has reached is a number, as a variable is.</summary>
  private static IrType Field(JsType type) => type.Ir ?? IrType.F64;

  /// <summary>The keys of a shape and the IR type of each value, which is what tells two structs apart.</summary>
  private string Signature(RecordShape shape) => string.Join(",", shape.Keys.Select((key, index) => $"{key}:{this.Spelled(shape.Types[index])}"));

  private string Spelled(JsType type) => type.Kind switch {
    JsKind.Record => "{" + this.Signature(type.Shape!) + "}",
    JsKind.Array => this.Spelled(type.Element!) + "[]",
    _ => Field(type).ToString()
  };

  /// <summary>A name in PascalCase that no struct has yet: the name itself, then with 2, 3 and so on appended.</summary>
  private string Unique(string name) {
    var pascal = char.ToUpperInvariant(name[0]) + name[1..];
    var chosen = pascal;
    for (var number = 2; !this._taken.Add(chosen); ++number)
      chosen = pascal + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    return chosen;
  }

}
