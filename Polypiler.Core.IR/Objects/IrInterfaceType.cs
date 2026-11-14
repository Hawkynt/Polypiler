using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// An interface: methods and properties without bodies that classes implement, as a C#
/// <c>interface</c> is. It has no fields and no objects of its own; a reference of its type names an
/// object of a class that implements it, or <c>null</c>. Each method names a function of the module
/// that takes the receiver of the interface type first and has an empty body, which only says the
/// signature every implementation has; it is never called. A call through the interface is stated
/// at <see cref="IrMethodCallKind.Interface"/>.
/// </summary>
public sealed class IrInterfaceType : IrTypeDeclaration {

  /// <summary>Makes an interface without members yet; <see cref="DefineMembers"/> gives it them.</summary>
  /// <param name="name">The name, unique among the declarations of the module.</param>
  public IrInterfaceType(string name) : base(name) => this.Define([]);

  /// <summary>The methods, accessors of the properties among them, each <see cref="IrDispatch.Abstract"/>.</summary>
  public IReadOnlyList<IrMethod> Methods { get; private set; } = [];

  /// <summary>The properties, each a getter, a setter or both.</summary>
  public IReadOnlyList<IrProperty> Properties { get; private set; } = [];

  /// <summary>The interfaces this one extends: an object of this interface is one of each of them.</summary>
  public IReadOnlyList<IrInterfaceType> BaseInterfaces { get; private set; } = [];

  /// <summary>Who may name the interface.</summary>
  public IrVisibility Visibility { get; init; } = IrVisibility.Public;

  /// <summary>Whether <see cref="DefineMembers"/> has been called.</summary>
  public bool HasMembers { get; private set; }

  /// <summary>Gives the interface its members and base interfaces, once.</summary>
  /// <param name="methods">The methods, accessors included.</param>
  /// <param name="properties">The properties.</param>
  /// <param name="baseInterfaces">The interfaces it extends.</param>
  /// <exception cref="InvalidOperationException">The members have been defined before.</exception>
  public void DefineMembers(IReadOnlyList<IrMethod> methods, IReadOnlyList<IrProperty> properties, IReadOnlyList<IrInterfaceType> baseInterfaces) {
    if (this.HasMembers)
      throw new InvalidOperationException($"The members of '{this.Name}' are defined already.");

    this.Methods = [.. methods];
    this.Properties = [.. properties];
    this.BaseInterfaces = [.. baseInterfaces];
    this.HasMembers = true;
  }

  /// <summary>The interface and every interface it extends, directly or not, the interface first; a cycle ends at its first repetition.</summary>
  public IEnumerable<IrInterfaceType> Closure {
    get {
      var seen = new HashSet<IrInterfaceType>(ReferenceEqualityComparer.Instance);
      var pending = new Stack<IrInterfaceType>([this]);
      while (pending.Count > 0) {
        var next = pending.Pop();
        if (!seen.Add(next))
          continue;

        yield return next;
        foreach (var inherited in next.BaseInterfaces.Reverse())
          pending.Push(inherited);
      }
    }
  }

  /// <summary>Every method of the interface and of the interfaces it extends.</summary>
  public IEnumerable<IrMethod> AllMethods => this.Closure.SelectMany(type => type.Methods);

  /// <summary>The functions of the module that are the signatures of the methods.</summary>
  public IEnumerable<string> Functions => this.Methods.Select(method => method.Function);

  /// <summary>The property an accessor belongs to, or <see langword="null"/>.</summary>
  /// <param name="method">The accessor.</param>
  /// <returns>The property.</returns>
  public IrProperty? PropertyOf(IrMethod method) => this.Properties.FirstOrDefault(property => ReferenceEquals(property.Getter, method) || ReferenceEquals(property.Setter, method));

}
