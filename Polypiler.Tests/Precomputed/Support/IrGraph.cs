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
/// the module, a struct and its fields. Types, lists and strings are values, compared by content only.
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

      if (left is not IrType) {
        var seen = this._forward.TryGetValue(left, out var partner);
        if (seen || this._backward.ContainsKey(right))
          return seen && ReferenceEquals(partner, right) ? null : $"{path}: the objects are shared differently";

        this._forward.Add(left, right);
        this._backward.Add(right, left);
      }

      if (left is IrTypeDeclaration declaration) {
        var other = (IrTypeDeclaration)right;
        if (declaration.Name != other.Name || declaration.IsDefined != other.IsDefined || (declaration as IrStructType)?.IsEquatable != (other as IrStructType)?.IsEquatable)
          return $"{path}: the declarations of '{declaration.Name}' and '{other.Name}' differ";

        return declaration.IsDefined ? this.Compare(declaration.Fields, other.Fields, $"{path}.Fields") : null;
      }

      foreach (var property in _properties.GetOrAdd(left.GetType(), type => [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.GetIndexParameters().Length == 0)]))
        if (this.Compare(property.GetValue(left), property.GetValue(right), $"{path}.{property.Name}") is { } difference)
          return difference;

      return null;
    }

  }

}
