using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  /// <summary>
  /// Checks the instantiations of a module: an origin has one type argument per type parameter and
  /// its constraints name its own parameters, the instantiation is named after the origin and its
  /// arguments, and no type of the module is open - none is a type parameter that a template stands
  /// for, which only a template may use.
  /// </summary>
  private static void Generics(IrModule module, List<Diagnostic> diagnostics) {
    var open = new Dictionary<IrTypeDeclaration, IrGenericOrigin>(ReferenceEqualityComparer.Instance);
    var origins = module.Functions.Select(function => (function.Name, function.Origin))
      .Concat(module.Types.OfType<IrClassType>().Select(type => (type.Name, type.Origin)))
      .Where(entry => entry.Origin is not null)
      .Select(entry => (entry.Name, Origin: entry.Origin!))
      .ToList();
    foreach (var (name, origin) in origins) {
      void Require(bool holds, string message) {
        if (!holds)
          diagnostics.Add(Error(name, message));
      }

      Require(origin.TypeArguments.Count == origin.TypeParameters.Count && name == IrGenerics.Mangle(origin.Name, origin.TypeArguments),
        $"the type arguments ({string.Join(", ", origin.TypeArguments)}) of its origin {origin.Display} do not match the instantiation");
      foreach (var constraint in origin.Constraints)
        Require(origin.TypeParameters.Contains(constraint.Parameter), $"a constraint names '{constraint.Parameter}', which is no type parameter of {origin.Display}");

      foreach (var parameter in origin.Template?.Parameters ?? [])
        open.TryAdd(parameter.Declaration!, origin);

      if (origin.ClassTemplate is { Class: { } template } generic) {
        Require(!module.Types.Contains(template), $"the class {template.Name} of the template of {origin.Display} is a type of the module");
        Require(module.Types.FirstOrDefault(type => type.Name == name) is IrClassType { IsDefined: true } instance && instance.Fields.Count == template.Fields.Count
            && instance.Constructors.Count == template.Constructors.Count && instance.Methods.Count == template.Methods.Count,
          $"its fields, constructors and methods are not those of the template {template.Name}");
        Require(generic.Members.Select(member => member.Function.Name).SequenceEqual(template.Functions)
            && generic.Members.All(member => member.Parameters.SequenceEqual(generic.Parameters)),
          $"the templates of the members of {template.Name} are not one per function of it, with its type parameters");
        foreach (var parameter in generic.Parameters.Where(parameter => parameter.Declaration is not null))
          open.TryAdd(parameter.Declaration!, origin);
      }
    }

    void Closed(string member, IrType type) {
      var named = (type.Element ?? type).Declaration;
      if (named is not null && open.TryGetValue(named, out var owner))
        diagnostics.Add(Error(member, $"the type {named.Name} is open: a type parameter of {owner.Display}"));
    }

    foreach (var declaration in module.Types)
      Closed(declaration.Name, declaration is IrInterfaceType contract ? IrType.Object(contract) : IrType.Void);

    foreach (var function in module.Functions)
      foreach (var type in function.Parameters.Concat(function.Locals).Select(variable => variable.Type).Append(function.ReturnType))
        Closed(function.Name, type);
  }

  private sealed partial class FunctionCheck {

    /// <summary>The equality and the order of a generic definition: two operands of one type that the operation is defined on.</summary>
    private void Generic(IrExpr expression) {
      var (left, right, defined, what) = expression switch {
        IrGenericEquals equality => (equality.Left, equality.Right, (System.Func<IrType, bool>)IrGenerics.Equates, "compared for equality"),
        _ => (((IrGenericCompare)expression).Left, ((IrGenericCompare)expression).Right, (System.Func<IrType, bool>)IrGenerics.Orders, "ordered")
      };
      this.Expression(left);
      this.Expression(right);
      this.Require(right.Type == left.Type && defined(left.Type), $"values of the types {left.Type} and {right.Type} are {what} as a generic definition does");
    }

  }

}
