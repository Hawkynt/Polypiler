using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>A named field of a type declaration.</summary>
/// <param name="Name">The name, unique among the fields of its declaration.</param>
/// <param name="Type">The type of the value the field holds: a truth value, a number, a struct, or an array of numbers.</param>
/// <param name="IsReadOnly">Whether the field is written only when its value is built, never through a place afterwards.</param>
public sealed record IrField(string Name, IrType Type, bool IsReadOnly);

/// <summary>
/// A type a module declares: a name and the fields its values consist of. Types are nominal: two
/// declarations are two types even when their fields agree, and a field belongs to the one
/// declaration that lists it. A declaration is made first and given its fields afterwards, so that
/// declarations can name each other in any order.
/// <para>
/// Every kind of declared type derives from this one: a <see cref="IrStructType"/> is copied by
/// value; a class, which is shared by reference, and a generic declaration, whose fields name type
/// parameters, add what they need on top of the name and the fields.
/// </para>
/// </summary>
public abstract class IrTypeDeclaration {

  private IReadOnlyList<IrField>? _fields;

  /// <summary>Makes a declaration without fields yet.</summary>
  /// <param name="name">The name, unique among the declarations of the module.</param>
  protected IrTypeDeclaration(string name) => this.Name = name;

  /// <summary>The name.</summary>
  public string Name { get; }

  /// <summary>The fields, in declaration order.</summary>
  /// <exception cref="InvalidOperationException">The fields have not been defined yet.</exception>
  public IReadOnlyList<IrField> Fields => this._fields ?? throw new InvalidOperationException($"The fields of '{this.Name}' are not defined yet.");

  /// <summary>Whether the fields have been defined.</summary>
  public bool IsDefined => this._fields is not null;

  /// <summary>Gives the declaration its fields, once.</summary>
  /// <param name="fields">The fields, in declaration order.</param>
  /// <exception cref="InvalidOperationException">The fields have been defined before.</exception>
  public void Define(IReadOnlyList<IrField> fields) {
    if (this._fields is not null)
      throw new InvalidOperationException($"The fields of '{this.Name}' are defined already.");

    this._fields = [.. fields];
  }

  /// <summary>Whether a field is one of this declaration's own, by identity: a field of the same name and type elsewhere is another field.</summary>
  /// <param name="field">The field.</param>
  /// <returns><see langword="true"/> when the declaration lists that very field.</returns>
  public bool Owns(IrField field) => this.Fields.Any(candidate => ReferenceEquals(candidate, field));

  /// <summary>The field of a name, or <see langword="null"/>.</summary>
  /// <param name="name">The name.</param>
  /// <returns>The field.</returns>
  public IrField? Field(string name) => this.Fields.FirstOrDefault(field => field.Name == name);

  /// <summary>The declaration as the IR writes a type: its name.</summary>
  public override string ToString() => this.Name;

}

/// <summary>
/// A record of named fields with value semantics, as a C# <c>struct</c> or <c>record struct</c> and
/// a JavaScript object literal of fixed shape are. What copying, equality and the default value of
/// such a record mean is stated once, at <see cref="IrStructNew"/>.
/// </summary>
public sealed class IrStructType : IrTypeDeclaration {

  /// <summary>Makes a struct without fields yet; <see cref="IrTypeDeclaration.Define"/> gives it them.</summary>
  /// <param name="name">The name, unique among the declarations of the module.</param>
  public IrStructType(string name) : base(name) { }

  /// <summary>Makes a struct with its fields.</summary>
  /// <param name="name">The name, unique among the declarations of the module.</param>
  /// <param name="fields">The fields, in declaration order.</param>
  public IrStructType(string name, IReadOnlyList<IrField> fields) : base(name) => this.Define(fields);

  /// <summary>
  /// Whether the type has equality members a program can call - <c>Equals</c>, <c>GetHashCode</c>,
  /// <c>==</c> and <c>!=</c> - as the C# compiler gives a <c>record struct</c>. A backend writes them
  /// for such a type and for every type <see cref="IrStructEquals"/> compares.
  /// </summary>
  public bool IsEquatable { get; init; }

  /// <summary>Whether every field is read-only, so that no value of the type changes after it is built.</summary>
  public bool IsReadOnly => this.Fields.All(member => member.IsReadOnly);

  /// <summary>
  /// Whether the type has a default value: every field zero, false, or the default of its own
  /// struct. A field that is an array has none, because the IR has no array that is not there. Asked
  /// of a struct that contains itself, which the verifier refuses, it does not end.
  /// </summary>
  public bool HasDefault => this.Fields.All(member => member.Type.Declaration is IrStructType nested ? nested.HasDefault : !member.Type.IsArray);

}
