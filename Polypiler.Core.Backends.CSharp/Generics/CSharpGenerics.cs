using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// How the C# backend writes the instantiations of a module (<see cref="IrGenericOrigin"/>) and the
/// copies a frontend made of one function for several types. The instantiations of one generic
/// method are written back as one generic definition with its constraints (re-generalization) when
/// each of them is its template instantiated with its own type arguments
/// (<see cref="IrGenerics.Instantiate"/>), every constraint is one the profile has, and every
/// instantiation its template calls is written back as well; otherwise each is a method of its own
/// named <c>Name_TypeName</c> (<c>IndexOf_Double</c>). An instantiation of a generic class is a class
/// of its own, <c>Ring_Int64</c>. Copies of one function without an origin - a JavaScript function
/// called with arrays of two kinds - are overloads of its name, which their parameter types tell apart.
/// </summary>
internal sealed class CSharpGenerics {

  private CSharpGenerics() { }

  /// <summary>The C# name every instantiation, copy and class instantiation is spelled from, by its name in the IR.</summary>
  public Dictionary<string, string> Spellings { get; } = [];

  /// <summary>The origin of every instantiation that is written back as its generic definition, by its name in the IR.</summary>
  public Dictionary<string, IrGenericOrigin> Generalized { get; } = [];

  /// <summary>The instantiation in whose place each generic definition is written, the first of its origin in the module.</summary>
  public HashSet<string> Written { get; } = [];

  /// <summary>The constraints of every type parameter of a template written back, by the declaration that stands for it.</summary>
  public Dictionary<IrTypeDeclaration, IReadOnlyList<IrGenericConstraintKind>> Parameters { get; } = new(ReferenceEqualityComparer.Instance);

  /// <summary>The calls of every template written back that instantiate another generic method in terms of its parameters, by the name of the call.</summary>
  public Dictionary<string, IrGenericCall> Calls { get; } = [];

  /// <summary>Decides how the instantiations of a module are written in a profile.</summary>
  /// <param name="module">The module.</param>
  /// <param name="has">Whether the profile has a feature of the language.</param>
  /// <returns>The decisions.</returns>
  public static CSharpGenerics Of(IrModule module, Func<CSharpFeature, bool> has) {
    var generics = new CSharpGenerics();
    var groups = module.Functions.Where(function => function.Origin is not null).GroupBy(function => function.Origin!.Name).ToList();
    var generalized = groups.Where(group => Generalizes([.. group], module, has)).Select(group => group.Key).ToHashSet();
    // A definition written back calls the others in terms of its parameters, which are then written back too.
    for (var changed = true; changed;) {
      changed = false;
      foreach (var group in groups.Where(group => generalized.Contains(group.Key)))
        if (group.First().Origin!.Template!.Calls.Values.Any(call => !generalized.Contains(call.Origin)))
          changed = generalized.Remove(group.Key);
    }

    foreach (var group in groups) {
      foreach (var function in group) {
        var origin = function.Origin!;
        if (!generalized.Contains(group.Key)) {
          generics.Spellings[function.Name] = Suffixed(origin);
          continue;
        }

        generics.Spellings[function.Name] = origin.Name;
        generics.Generalized[function.Name] = origin;
      }

      if (!generalized.Contains(group.Key))
        continue;

      var template = group.First().Origin!.Template!;
      generics.Written.Add(group.First().Name);
      foreach (var (call, instantiated) in template.Calls)
        generics.Calls[call] = instantiated;

      var origin0 = group.First().Origin!;
      foreach (var (parameter, index) in template.Parameters.Select((parameter, index) => (parameter, index)))
        generics.Parameters[parameter.Declaration!] = [.. origin0.Constraints.Where(constraint => constraint.Parameter == origin0.TypeParameters[index]).Select(constraint => constraint.Kind)];
    }

    foreach (var copy in module.Functions.Where(function => function.Origin is null && IrGenerics.IsInstance(function.Name)))
      generics.Spellings[copy.Name] = IrGenerics.SourceName(copy.Name);

    foreach (var type in module.Types.OfType<IrClassType>().Where(type => type.Origin is not null))
      generics.Spellings[type.Name] = Suffixed(type.Origin!);

    return generics;
  }

  /// <summary>
  /// Whether the instantiations of one origin are its template instantiated with their own type
  /// arguments, with constraints the profile has.
  /// </summary>
  private static bool Generalizes(IReadOnlyList<IrFunction> instances, IrModule module, Func<CSharpFeature, bool> has) {
    var template = instances[0].Origin!.Template;
    if (template is null || instances.Any(instance => !ReferenceEquals(instance.Origin!.Template, template)))
      return false;

    var constraints = instances[0].Origin!.Constraints;
    if ((constraints.Any(constraint => constraint.Kind == IrGenericConstraintKind.Unmanaged) && !has(CSharpFeature.UnmanagedConstraint))
        || (constraints.Any(constraint => constraint.Kind == IrGenericConstraintKind.NotNull) && !has(CSharpFeature.NotNullConstraint)))
      return false;

    try {
      return instances.All(instance => IrGenerics.Equivalent(IrGenerics.Instantiate(template, instance.Origin!.TypeArguments, instance.Name, module), instance));
    } catch (ArgumentException) {
      return false;
    }
  }

  /// <summary>The name of an instantiation written on its own: the definition's name and the .NET names of its type arguments.</summary>
  private static string Suffixed(IrGenericOrigin origin) => $"{origin.Name}_{string.Join("_", origin.TypeArguments.Select(DotNetName))}";

  /// <summary>The name .NET gives a type: <c>Double</c>, <c>Int64</c>, <c>Int32Array</c>, a declared type by its own name.</summary>
  private static string DotNetName(IrType type) => type.Kind switch {
    IrTypeKind.Bool => "Boolean",
    IrTypeKind.I8 => "SByte",
    IrTypeKind.U8 => "Byte",
    IrTypeKind.I16 => "Int16",
    IrTypeKind.U16 => "UInt16",
    IrTypeKind.I32 => "Int32",
    IrTypeKind.U32 => "UInt32",
    IrTypeKind.I64 => "Int64",
    IrTypeKind.U64 => "UInt64",
    IrTypeKind.F32 => "Single",
    IrTypeKind.F64 => "Double",
    IrTypeKind.String => "String",
    IrTypeKind.Array => DotNetName(type.Element!) + "Array",
    _ => new string([.. (type.Declaration?.Name ?? type.ToString()).Select(character => char.IsLetterOrDigit(character) ? character : '_')])
  };

}
