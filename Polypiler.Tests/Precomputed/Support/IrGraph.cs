using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Precomputed.Support;

/// <summary>
/// Compares two IR object graphs by their public properties, independently of how either was made:
/// every value equal, and every object that one graph uses in several places used in the same places
/// of the other - a local shared by its declaration and its reads, a constant shared with the list of
/// the module, a struct and its fields, a class and its methods. Types, signatures, lists and strings are
/// values, compared by content only; a class of the library is the same object on both sides.
/// </summary>
internal static class IrGraph {

  private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _properties = new();

  /// <summary>The first difference between two graphs as a path and a description, or <see langword="null"/> when there is none.</summary>
  public static string? Difference(object? left, object? right) => new Walk().Compare(left, right, "module");

  private sealed class Walk {

    private readonly Dictionary<object, object> _forward = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, object> _backward = new(ReferenceEqualityComparer.Instance);

    public string? Compare(object? left, object? right, string path) {
      if (left is null || right is null)
        return left is null && right is null ? null : $"{path}: {left ?? "null"} against {right ?? "null"}";

      // A list is its items, whichever class holds them.
      if (left is IEnumerable sequence && right is IEnumerable others && left is not (string or IrType)) {
        var lefts = sequence.Cast<object?>().ToList();
        var rights = others.Cast<object?>().ToList();
        if (lefts.Count != rights.Count)
          return $"{path}: {lefts.Count} items against {rights.Count}";

        return lefts.Select((item, index) => this.Compare(item, rights[index], $"{path}[{index}]")).FirstOrDefault(difference => difference is not null);
      }

      if (left.GetType() != right.GetType())
        return $"{path}: {left.GetType().Name} against {right.GetType().Name}";

      if (left is string or bool or long or int or Enum)
        return left.Equals(right) ? null : $"{path}: {left} against {right}";

      // An origin is a value: nodes that inherited the same origin share it in memory, the bytes keep a copy each.
      if (left is not (IrType or IrFunctionType or Origin)) {
        var seen = this._forward.TryGetValue(left, out var partner);
        if (seen || this._backward.ContainsKey(right))
          return seen && ReferenceEquals(partner, right) ? null : $"{path}: the objects are shared differently";

        this._forward.Add(left, right);
        this._backward.Add(right, left);
      }

      if (left is IrTypeDeclaration declaration)
        return this.Declaration(declaration, (IrTypeDeclaration)right, path);

      foreach (var property in _properties.GetOrAdd(left.GetType(), type => [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.GetIndexParameters().Length == 0)])) {
        // A property that gives the object itself, as the underlying type of a type that is no enum does, says nothing more.
        var value = property.GetValue(left);
        if (ReferenceEquals(value, left))
          continue;

        if (this.Compare(value, property.GetValue(right), $"{path}.{property.Name}") is { } difference)
          return difference;
      }

      return null;
    }

    /// <summary>Two declarations: the same name and kind, and every part a declaration of its kind has.</summary>
    private string? Declaration(IrTypeDeclaration declaration, IrTypeDeclaration other, string path) {
      if (declaration.Name != other.Name || declaration.IsDefined != other.IsDefined || (declaration as IrStructType)?.IsEquatable != (other as IrStructType)?.IsEquatable)
        return $"{path}: the declarations of '{declaration.Name}' and '{other.Name}' differ";

      if (declaration is IrClassType { IsLibrary: true } || other is IrClassType { IsLibrary: true })
        return ReferenceEquals(declaration, other) ? null : $"{path}: the library class '{declaration.Name}' is another object";

      IEnumerable<(string Name, object? Left, object? Right)> parts = (declaration, other) switch {
        (IrEnumType named, IrEnumType twin) => [
          ("Underlying", named.Underlying, twin.Underlying), ("Members", named.Members, twin.Members), ("IsFlags", named.IsFlags, twin.IsFlags),
          ("IsPublic", named.IsPublic, twin.IsPublic)
        ],
        (IrClassType type, IrClassType twin) => [
          ("Fields", type.IsDefined ? type.Fields : null, twin.IsDefined ? twin.Fields : null),
          ("IsSealed", type.IsSealed, twin.IsSealed), ("IsAbstract", type.IsAbstract, twin.IsAbstract), ("IsRecord", type.IsRecord, twin.IsRecord),
          ("Visibility", type.Visibility, twin.Visibility), ("Origin", type.Origin, twin.Origin),
          ("HasHierarchy", type.HasHierarchy, twin.HasHierarchy), ("Base", type.Base, twin.Base), ("Interfaces", type.Interfaces, twin.Interfaces),
          ("HasMembers", type.HasMembers, twin.HasMembers), ("Constructors", type.Constructors, twin.Constructors), ("Methods", type.Methods, twin.Methods),
          ("Properties", type.Properties, twin.Properties), ("StaticFields", type.StaticFields, twin.StaticFields)
        ],
        (IrInterfaceType type, IrInterfaceType twin) => [
          ("Visibility", type.Visibility, twin.Visibility), ("IsDisposable", type.IsDisposable, twin.IsDisposable), ("HasMembers", type.HasMembers, twin.HasMembers),
          ("Methods", type.Methods, twin.Methods), ("Properties", type.Properties, twin.Properties), ("BaseInterfaces", type.BaseInterfaces, twin.BaseInterfaces)
        ],
        _ => [("Fields", declaration.IsDefined ? declaration.Fields : null, other.IsDefined ? other.Fields : null)]
      };
      return parts.Select(part => this.Compare(part.Left, part.Right, $"{path}.{part.Name}")).FirstOrDefault(difference => difference is not null);
    }

  }

}
