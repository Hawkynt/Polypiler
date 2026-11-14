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
  Private,

  /// <summary>The members of the class it belongs to and of every class derived from it.</summary>
  Protected

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

  private readonly IrDispatch? _dispatch;

  /// <summary>Who may call the method.</summary>
  public IrVisibility Visibility { get; init; } = IrVisibility.Public;

  /// <summary>Whether the method is called by its name or is an accessor of a property.</summary>
  public IrMethodKind Kind { get; init; } = IrMethodKind.Method;

  /// <summary>
  /// How a call reaches the method: <see cref="IrDispatch.Static"/> for a static method and
  /// <see cref="IrDispatch.Instance"/> for any other unless stated otherwise. What a virtual call
  /// selects is stated at <see cref="IrMethodCallKind"/>.
  /// </summary>
  public IrDispatch Dispatch {
    get => this._dispatch ?? (this.IsStatic ? IrDispatch.Static : IrDispatch.Instance);
    init => this._dispatch = value;
  }

  /// <summary>The method of a base class an <see cref="IrDispatch.Override"/> replaces; <see langword="null"/> for every other method.</summary>
  public IrMethod? Overrides { get; init; }

  /// <summary>Whether an override cannot be overridden again, as C# <c>sealed override</c> says.</summary>
  public bool IsSealedOverride { get; init; }

  /// <summary>The methods of interfaces the method is the implementation of, in its class and every class derived from it.</summary>
  public IReadOnlyList<IrMethod> Implements { get; init; } = [];

  /// <summary>Whether a call of the method selects an override by the class of the receiver: a virtual, abstract or overriding method.</summary>
  public bool IsVirtual => this.Dispatch is IrDispatch.Virtual or IrDispatch.Override or IrDispatch.Abstract;

  /// <summary>The method that first declared the slot an overriding method fills: the method itself for any other.</summary>
  public IrMethod Root => this.Overrides?.Root ?? this;

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
/// What creating, sharing and comparing objects means is stated once, at <see cref="IrNew"/>; what
/// deriving a class from another and implementing an interface mean, at <see cref="IrMethodCallKind"/>.
/// </summary>
public sealed class IrClassType : IrTypeDeclaration {

  /// <summary>Makes a class without members yet; <see cref="IrTypeDeclaration.Define"/> gives it its fields and <see cref="DefineMembers"/> the rest.</summary>
  /// <param name="name">The name, unique among the declarations of the module.</param>
  public IrClassType(string name) : base(name) { }

  /// <summary>The class this one derives from, whose fields and methods it has as well; <see langword="null"/> for none.</summary>
  public IrClassType? Base { get; private set; }

  /// <summary>The interfaces the class names as implemented; those of its base class and their base interfaces are implemented as well.</summary>
  public IReadOnlyList<IrInterfaceType> Interfaces { get; private set; } = [];

  /// <summary>Whether the source forbids deriving from the class, as C# <c>sealed</c> does.</summary>
  public bool IsSealed { get; init; }

  /// <summary>Whether the class has no objects of its own, only those of the classes derived from it, and may have abstract methods.</summary>
  public bool IsAbstract { get; init; }

  /// <summary>Whether <see cref="Inherit"/> has been called.</summary>
  public bool HasHierarchy { get; private set; }

  /// <summary>Gives the class its base class and the interfaces it implements, once.</summary>
  /// <param name="base">The base class, or <see langword="null"/>.</param>
  /// <param name="interfaces">The interfaces.</param>
  /// <exception cref="InvalidOperationException">The hierarchy has been given before.</exception>
  public void Inherit(IrClassType? @base, IReadOnlyList<IrInterfaceType> interfaces) {
    if (this.HasHierarchy)
      throw new InvalidOperationException($"The base of '{this.Name}' is given already.");

    this.Base = @base;
    this.Interfaces = [.. interfaces];
    this.HasHierarchy = true;
  }

  /// <summary>The class and its base classes, the class first; a cycle, which the verifier refuses, ends at its first repetition.</summary>
  public IEnumerable<IrClassType> Lineage {
    get {
      var seen = new HashSet<IrClassType>(ReferenceEqualityComparer.Instance);
      for (var type = this; type is not null && seen.Add(type); type = type.Base)
        yield return type;
    }
  }

  /// <summary>Whether a class is this one or one of its base classes.</summary>
  /// <param name="type">The class.</param>
  /// <returns><see langword="true"/> when an object of this class is one of <paramref name="type"/>.</returns>
  public bool DerivesFrom(IrClassType type) => this.Lineage.Any(ancestor => ReferenceEquals(ancestor, type));

  /// <summary>Every interface an object of the class is one of: those of the class and its base classes, and their base interfaces.</summary>
  public IEnumerable<IrInterfaceType> AllInterfaces => this.Lineage.SelectMany(type => type.Interfaces).SelectMany(type => type.Closure).Distinct();

  /// <summary>The fields an object of the class has: those of its base classes first, then its own.</summary>
  public IReadOnlyList<IrField> AllFields => [.. this.Lineage.Reverse().SelectMany(type => type.Fields)];

  /// <summary>The class of the hierarchy whose fields a field is, or <see langword="null"/>.</summary>
  /// <param name="field">The field.</param>
  /// <returns>This class or a base class.</returns>
  public IrClassType? OwnerOf(IrField field) => this.Lineage.FirstOrDefault(type => type.IsDefined && type.Owns(field));

  /// <summary>The class of the hierarchy that has a method, or <see langword="null"/>.</summary>
  /// <param name="method">The method.</param>
  /// <returns>This class or a base class.</returns>
  public IrClassType? OwnerOf(IrMethod method) => this.Lineage.FirstOrDefault(type => type.Methods.Contains(method));

  /// <summary>
  /// The method a virtual call of a method selects on an object of the class: the last override of
  /// its slot on the way from the class to the method's own class, or the method itself.
  /// </summary>
  /// <param name="method">The method called.</param>
  /// <returns>The method that runs; an abstract one only where the class does not implement it.</returns>
  public IrMethod Select(IrMethod method) {
    var slot = method.Root;
    foreach (var type in this.Lineage)
      if (type.Methods.FirstOrDefault(candidate => candidate.IsVirtual && ReferenceEquals(candidate.Root, slot) && candidate.Dispatch != IrDispatch.Abstract) is { } found)
        return found;

    return method;
  }

  /// <summary>The method of the class or a base class that implements a method of an interface, or <see langword="null"/>.</summary>
  /// <param name="member">The method of the interface.</param>
  /// <returns>The implementation, before any override of it is selected.</returns>
  public IrMethod? Implementation(IrMethod member)
    => this.Lineage.SelectMany(type => type.Methods).FirstOrDefault(candidate => candidate.Implements.Contains(member));

  /// <summary>
  /// Whether objects of the class compare by value as well, as a C# <c>record class</c> does: its
  /// <see cref="IrStructEquals"/> compares the fields, while <see cref="IrReferenceEquals"/> still
  /// compares identities.
  /// </summary>
  public bool IsRecord { get; init; }

  /// <summary>
  /// Whether the class is one of the library's (<see cref="IrExceptionClass"/>), which no module declares
  /// and every module may name; a target writes it as its own library class.
  /// </summary>
  public bool IsLibrary { get; init; }

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
