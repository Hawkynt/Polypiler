using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust.Emit;

/// <summary>
/// A generic class of the source the crate writes as one Rust generic struct, <c>pub struct Ring&lt;T&gt;</c>
/// with <c>impl&lt;T: Default + Copy&gt; Ring&lt;T&gt;</c>, instead of one struct per instantiation.
/// </summary>
/// <param name="Class">The class the crate writes: the template's class declared with the stand-ins of its parameters (<see cref="IrGenerics.Declare"/>).</param>
/// <param name="Functions">Its functions: the member templates instantiated with the stand-ins.</param>
/// <param name="Parameters">Its type parameters.</param>
/// <param name="Instances">The type arguments of every instantiation of the module it may take the place of, by the instantiation.</param>
internal sealed record GenericClass(IrClassType Class, IReadOnlyList<IrFunction> Functions, IReadOnlyList<GenericParameter> Parameters, IReadOnlyDictionary<IrClassType, IReadOnlyList<IrType>> Instances) {

  /// <summary>The name of the generic class as the source names it, without its parameters: <c>Ring</c>.</summary>
  public string Name => IrGenerics.SourceName(this.Class.Name);

  /// <summary>The parameters as they follow the struct's name, <c>&lt;T&gt;</c>.</summary>
  public string Generics => $"<{string.Join(", ", this.Parameters.Select(parameter => parameter.Name))}>";

  /// <summary>The parameters with their bounds as they follow <c>impl</c>, <c>&lt;T: Default + Copy&gt;</c>.</summary>
  public string Bounded => $"<{string.Join(", ", this.Parameters.Select(parameter => parameter.Bounded))}>";

  /// <summary>The name of each parameter by the type that stands for it.</summary>
  public Dictionary<IrType, string> StandIns => this.Parameters.ToDictionary(parameter => parameter.StandIn, parameter => parameter.Name);

}

/// <summary>
/// The generic classes of a module the crate writes as Rust generic structs (#1421). The IR keeps a
/// generic class as one class per instantiation, each with the template of the class
/// (<see cref="IrGenericClassTemplate"/>). A class is written generic where its fields are scalars, its
/// parameters or arrays of either, every parameter is the type of a field, the members take and
/// give only scalars and parameters besides their object, and the bodies do with a value of a
/// parameter only what <see cref="GenericDefinitions"/> admits of a generic method - hold it, store it,
/// compare it, make its default - or call another member and make an array of it. The bounds are
/// those of a generic method, and <c>Default</c> always, which the struct's own default needs; an
/// instantiation is written as the generic struct where its type arguments have the bounds with the
/// meaning .NET gives them (a floating-point number compared keeps its own struct) and its functions
/// are the template instantiated with them. Whether the objects are plain structs is decided later,
/// by the ownership analysis, for the generic class and every instantiation alike.
/// </summary>
internal static class GenericClasses {

  /// <summary>The generic classes of a module, each with the instantiations it may take the place of.</summary>
  /// <param name="module">The module as the backend receives it.</param>
  /// <returns>The generic classes, in the order of their first instantiation.</returns>
  public static IReadOnlyList<GenericClass> Of(IrModule module) {
    var classes = new List<GenericClass>();
    var functions = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    var groups = module.Types.OfType<IrClassType>().Where(type => type.Origin?.ClassTemplate is { IsDefined: true })
      .GroupBy(type => type.Origin!.ClassTemplate!, ReferenceEqualityComparer.Instance);
    foreach (var group in groups) {
      var template = (IrGenericClassTemplate)group.Key!;
      var source = template.Class!;
      if (module.Types.Any(type => type.Name == source.Name) || Needs(template) is not { } needs
          || GenericDefinitions.Stand(template.Members.Select(member => member.Function), source.Fields.Select(field => field.Type), template.Parameters.Count) is not { } standIns)
        continue;

      var admitted = group.Where(instance => instance.Origin!.TypeArguments.Select((argument, index) => GenericDefinitions.Admits(argument, needs[index])).All(admits => admits)
          && instance.Functions.All(functions.ContainsKey) && IsInstance(template, instance, module, functions))
        .ToDictionary<IrClassType, IrClassType, IReadOnlyList<IrType>>(instance => instance, instance => instance.Origin!.TypeArguments, ReferenceEqualityComparer.Instance);
      if (admitted.Count == 0)
        continue;

      var declared = IrGenerics.Declare(template, standIns, source.Name);
      var written = IrGenerics.Instantiate(template, standIns, declared, module)
        .Select(function => function with { Body = AliasSpecialization.Rewrite(function.Body, GenericDefinitions.SignTest) }).ToList();
      var parameters = source.Origin!.TypeParameters.Select((name, index) => new GenericParameter(name, standIns[index], GenericDefinitions.Bounds(needs[index] | Need.Default))).ToList();
      classes.Add(new GenericClass(declared, written, parameters, admitted));
    }

    return classes;
  }

  /// <summary>Whether a class of the module is the template instantiated with its type arguments: its fields of the types, its functions the same trees.</summary>
  private static bool IsInstance(IrGenericClassTemplate template, IrClassType instance, IrModule module, IReadOnlyDictionary<string, IrFunction> functions) {
    var arguments = instance.Origin!.TypeArguments;
    var fields = IrGenerics.Declare(template, arguments, instance.Name).Fields.Select(field => field.Type);
    return fields.SequenceEqual(instance.Fields.Select(field => field.Type))
      && IrGenerics.Instantiate(template, arguments, instance, module).All(function => IrGenerics.Equivalent(function, functions[function.Name]));
  }

  /// <summary>What the members of a generic class need of each of its parameters, or <see langword="null"/> where the class cannot be one Rust generic struct.</summary>
  private static Need[]? Needs(IrGenericClassTemplate template) {
    var standing = new Standing(template.Parameters);
    var source = template.Class!;
    var own = IrType.Object(source);
    var members = source.Functions.ToHashSet(StringComparer.Ordinal);
    // A parameter no field names is one rustc calls unused (E0392).
    if (!source.Fields.All(field => standing.Plain(field.Type)) || template.Parameters.Any(parameter => !source.Fields.Any(field => field.Type == parameter || field.Type.Element == parameter)))
      return null;

    var needs = new Need[template.Parameters.Count];
    // A call of a member, by name or as a method, and a new array of a parameter (its default value repeated) are the class's own; any other node is the generic method's.
    bool Own(IrExpr node) => node is IrFieldLoad || node is IrCall call && members.Contains(call.Function) || node is IrMethodCall invoked && members.Contains(invoked.Method.Function) || node is IrNewArray { Type.Element: { } element } && standing.Of(element) is not null;
    foreach (var member in template.Members) {
      var function = member.Function;
      // The object is the first parameter, of the class; every other value is a scalar or a parameter: an array is only a field's.
      var values = function.Parameters.Skip(function.Parameters is [{ } first, ..] && first.Type == own ? 1 : 0).Concat(function.Locals).Select(local => local.Type);
      if (member.Calls.Count > 0 || !values.All(standing.Scalar) || function.ReturnType != IrType.Void && !standing.Scalar(function.ReturnType)
          || !standing.Collect(function.Body, needs, Own))
        return null;
    }

    return needs;
  }

}
