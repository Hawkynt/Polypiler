using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>Who may name a member of a class, or a class.</summary>
public enum IrVisibility {

  /// <summary>Every code, also code outside the module.</summary>
  Public,

  /// <summary>The code of the module and of the assembly or package it is built into.</summary>
  Internal,

  /// <summary>Only the members of the declaration it belongs to.</summary>
  Private

}

/// <summary>What a method of a class is to the source it came from.</summary>
public enum IrMethodKind {

  /// <summary>A method called by its name.</summary>
  Method,

  /// <summary>The accessor that reads a property: no parameter besides the receiver, the value of the property as its result.</summary>
  Getter,

  /// <summary>The accessor that writes a property: the value as its one parameter besides the receiver, nothing as its result.</summary>
  Setter

}

/// <summary>
/// A method of a class. Its body is the function of the module that <see cref="Function"/> names, so
/// that every pass that walks the functions of a module also walks the methods; that function is no
/// function a call by name (<see cref="IrCall"/>) reaches, only an <see cref="IrMethodCall"/>. The
/// first parameter of the function of an instance method is the receiver, the object the method is
/// called on - <c>this</c> of both languages - which the body reads and never assigns; a static
/// method has none.
/// </summary>
/// <param name="Name">The name as the source has it; overloads share it, their functions differ.</param>
/// <param name="Function">The name of the function of the module that is the body.</param>
/// <param name="IsStatic">Whether the method is called without a receiver.</param>
public sealed record IrMethod(string Name, string Function, bool IsStatic) {

  /// <summary>Who may call the method.</summary>
  public IrVisibility Visibility { get; init; } = IrVisibility.Public;

  /// <summary>Whether the method is called by its name or is an accessor of a property.</summary>
  public IrMethodKind Kind { get; init; } = IrMethodKind.Method;

}

/// <summary>
/// A constructor of a class: the function of the module that <see cref="Function"/> names, whose first
/// parameter is the new object and which returns nothing. What creating an object means is stated at
/// <see cref="IrNew"/>. A constructor that begins with a call of the function of another constructor
/// of its class - <c>this(…)</c> of C# - chains to it; every other constructor first runs the field
/// initializers of the class, which the frontend has written at its start.
/// </summary>
/// <param name="Function">The name of the function of the module that is the body.</param>
public sealed record IrConstructor(string Function) {

  /// <summary>Who may create objects with the constructor.</summary>
  public IrVisibility Visibility { get; init; } = IrVisibility.Public;

}

/// <summary>
/// A property of a class: a name that reads like a field and is either a field itself - an
/// auto-property, whose <see cref="BackingField"/> holds the value and is read and written as that
/// field - or a pair of accessor methods, of which a read-only or a write-only property has one.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="Type">The type of the value.</param>
public sealed record IrProperty(string Name, IrType Type) {

  /// <summary>The field of an auto-property, one of its class's own; <see langword="null"/> for a property of accessors.</summary>
  public IrField? BackingField { get; init; }

  /// <summary>The accessor that reads the property, of the kind <see cref="IrMethodKind.Getter"/>.</summary>
  public IrMethod? Getter { get; init; }

  /// <summary>The accessor that writes the property, of the kind <see cref="IrMethodKind.Setter"/>.</summary>
  public IrMethod? Setter { get; init; }

  /// <summary>Whether an auto-property can be written by code other than a constructor: <c>{ get; set; }</c> but not <c>{ get; }</c>.</summary>
  public bool HasSetter { get; init; }

  /// <summary>Whether the setter of an auto-property is for object initializers and constructors only, as C# <c>init</c> is.</summary>
  public bool IsInitOnly { get; init; }

  /// <summary>Who may read and write the property.</summary>
  public IrVisibility Visibility { get; init; } = IrVisibility.Public;

}

/// <summary>
/// A class: objects with an identity and mutable fields, made by its constructors, with methods,
/// properties and static fields, as a C# class and a JavaScript class or constructor function are.
/// What creating, sharing and comparing objects means is stated once, at <see cref="IrNew"/>. Only
/// sealed classes without a base class exist so far: <see cref="Base"/>, <see cref="Interfaces"/>,
/// <see cref="IsSealed"/> and <see cref="IsAbstract"/> name what inheritance will add.
/// </summary>
public sealed class IrClassType : IrTypeDeclaration {

  /// <summary>Makes a class without members yet; <see cref="IrTypeDeclaration.Define"/> gives it its fields and <see cref="DefineMembers"/> the rest.</summary>
  /// <param name="name">The name, unique among the declarations of the module.</param>
  public IrClassType(string name) : base(name) { }

  /// <summary>The class this one derives from; there is none yet.</summary>
  public IrClassType? Base => null;

  /// <summary>The interfaces the class implements; there are none yet.</summary>
  public IReadOnlyList<IrClassType> Interfaces => [];

  /// <summary>Whether no class derives from this one; every class is so far.</summary>
  public bool IsSealed => true;

  /// <summary>Whether the class has no objects of its own; no class is so far.</summary>
  public bool IsAbstract => false;

  /// <summary>
  /// Whether objects of the class compare by value as well, as a C# <c>record class</c> does: its
  /// <see cref="IrStructEquals"/> compares the fields, while <see cref="IrReferenceEquals"/> still
  /// compares identities.
  /// </summary>
  public bool IsRecord { get; init; }

  /// <summary>Who may name the class.</summary>
  public IrVisibility Visibility { get; init; } = IrVisibility.Public;

  /// <summary>The constructors, in source order.</summary>
  public IReadOnlyList<IrConstructor> Constructors { get; private set; } = [];

  /// <summary>The methods, in source order; the accessors of the properties among them.</summary>
  public IReadOnlyList<IrMethod> Methods { get; private set; } = [];

  /// <summary>The properties, in source order.</summary>
  public IReadOnlyList<IrProperty> Properties { get; private set; } = [];

  /// <summary>The static fields: globals of the module (<see cref="IrModule.Globals"/>) that the class owns and a target writes as members of it.</summary>
  public IReadOnlyList<IrGlobal> StaticFields { get; private set; } = [];

  /// <summary>Whether <see cref="DefineMembers"/> has been called.</summary>
  public bool HasMembers { get; private set; }

  /// <summary>Gives the class its members, once.</summary>
  /// <param name="constructors">The constructors.</param>
  /// <param name="methods">The methods, accessors included.</param>
  /// <param name="properties">The properties.</param>
  /// <param name="staticFields">The globals the class owns.</param>
  /// <exception cref="InvalidOperationException">The members have been defined before.</exception>
  public void DefineMembers(IReadOnlyList<IrConstructor> constructors, IReadOnlyList<IrMethod> methods, IReadOnlyList<IrProperty> properties, IReadOnlyList<IrGlobal> staticFields) {
    if (this.HasMembers)
      throw new InvalidOperationException($"The members of '{this.Name}' are defined already.");

    this.Constructors = [.. constructors];
    this.Methods = [.. methods];
    this.Properties = [.. properties];
    this.StaticFields = [.. staticFields];
    this.HasMembers = true;
  }

  /// <summary>The names of the functions of the module that are bodies of the class: constructors and methods.</summary>
  public IEnumerable<string> Functions => this.Constructors.Select(constructor => constructor.Function).Concat(this.Methods.Select(method => method.Function));

  /// <summary>The auto-property a field is the backing field of, or <see langword="null"/>.</summary>
  /// <param name="field">The field.</param>
  /// <returns>The property.</returns>
  public IrProperty? PropertyOf(IrField field) => this.Properties.FirstOrDefault(property => ReferenceEquals(property.BackingField, field));

  /// <summary>The property an accessor belongs to, or <see langword="null"/>.</summary>
  /// <param name="method">The accessor.</param>
  /// <returns>The property.</returns>
  public IrProperty? PropertyOf(IrMethod method) => this.Properties.FirstOrDefault(property => ReferenceEquals(property.Getter, method) || ReferenceEquals(property.Setter, method));

}
