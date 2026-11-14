using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  /// <summary>
  /// Checks the hierarchy of the classes and interfaces of a module: a base class and an interface
  /// are declared by the module, a sealed class has no derived class, no class or interface derives
  /// from itself; a method's dispatch agrees with its class, an override replaces a virtual method of
  /// a base class of one signature, an abstract method is one of an abstract class and has no body,
  /// an implementation implements a method of an interface of its class with its signature, and a
  /// class that is not abstract has a body for every abstract method and every method of its interfaces.
  /// </summary>
  /// <returns>The functions that are the signatures of abstract methods, which nothing calls.</returns>
  private static HashSet<string> Hierarchy(TypeTable types, Dictionary<string, IrFunction> functions, List<Diagnostic> diagnostics) {
    var signatures = new HashSet<string>();
    bool SameSignature(IrMethod first, IrMethod second)
      => functions.TryGetValue(first.Function, out var a) && functions.TryGetValue(second.Function, out var b) && a.ReturnType == b.ReturnType
        && a.Parameters.Skip(1).Select(parameter => parameter.Type).SequenceEqual(b.Parameters.Skip(1).Select(parameter => parameter.Type));

    void Abstract(IrMethod method, System.Action<bool, string> require) {
      signatures.Add(method.Function);
      require(functions.TryGetValue(method.Function, out var body) && body.Body.Statements.Count == 0 && body.Locals.Count == 0,
        $"the abstract method '{method.Name}' has a body");
    }

    foreach (var contract in types.Interfaces) {
      void Require(bool holds, string message) {
        if (!holds)
          diagnostics.Add(Error(contract.Name, message));
      }

      Require(!contract.BaseInterfaces.Any(inherited => inherited.Closure.Contains(contract)), "the interface extends itself");
      foreach (var inherited in contract.BaseInterfaces)
        Require(types.Declares(inherited), $"the interface {inherited.Name} is not among the types of the module");

      foreach (var method in contract.Methods) {
        Require(method.Dispatch == IrDispatch.Abstract, $"the method '{method.Name}' of an interface is not abstract");
        Require(functions.TryGetValue(method.Function, out var body) && body.Parameters.Count > 0 && body.Parameters[0].Type == IrType.Object(contract),
          $"the function '{method.Function}' does not take the receiver of {contract.Name} first");
        Abstract(method, Require);
      }
    }

    foreach (var type in types.Classes) {
      void Require(bool holds, string message) {
        if (!holds)
          diagnostics.Add(Error(type.Name, message));
      }

      if (type.Base is { } @base) {
        Require(types.Declares(@base), $"the base class {@base.Name} is not among the types of the module");
        Require(!@base.IsSealed, $"the class derives from the sealed class {@base.Name}");
        Require(!@base.Lineage.Contains(type), "the class derives from itself");
      }

      foreach (var contract in type.Interfaces)
        Require(types.Declares(contract), $"the interface {contract.Name} is not among the types of the module");

      var interfaces = type.AllInterfaces.ToList();
      foreach (var method in type.Methods) {
        Require(method.IsStatic == (method.Dispatch == IrDispatch.Static), $"the method '{method.Name}' has a receiver and the dispatch {method.Dispatch}");
        Require(method.Kind == IrMethodKind.Method || method.Dispatch == IrDispatch.Instance, $"the accessor '{method.Name}' has the dispatch {method.Dispatch}");
        Require(method.Overrides is null == (method.Dispatch != IrDispatch.Override) && (!method.IsSealedOverride || method.Dispatch == IrDispatch.Override),
          $"the method '{method.Name}' names an overridden method without being an override, or the other way round");
        if (method.Dispatch == IrDispatch.Abstract) {
          Require(type.IsAbstract, $"the abstract method '{method.Name}' is one of a class that is not abstract");
          Abstract(method, Require);
        }

        if (method.Overrides is { } replaced)
          Require(type.Base?.OwnerOf(replaced) is not null && replaced.IsVirtual && !replaced.IsSealedOverride && replaced.Name == method.Name && SameSignature(method, replaced),
            $"the method '{method.Name}' overrides no virtual method of a base class with its name and signature");

        foreach (var member in method.Implements)
          Require(interfaces.Any(contract => contract.Methods.Contains(member)) && !method.IsStatic && SameSignature(method, member),
            $"the method '{method.Name}' implements no method of an interface of {type.Name} with its signature");
      }

      if (type.IsAbstract)
        continue;

      foreach (var slot in type.Lineage.SelectMany(ancestor => ancestor.Methods).Where(method => method.Dispatch == IrDispatch.Abstract))
        Require(type.Select(slot).Dispatch != IrDispatch.Abstract, $"the class does not implement the abstract method '{slot.Name}'");

      foreach (var member in interfaces.SelectMany(contract => contract.Methods))
        Require(type.Implementation(member) is not null, $"the class does not implement the method '{member.Name}' of an interface");
    }

    return signatures;
  }

  private sealed partial class FunctionCheck {

    /// <summary>The functions that are the signatures of abstract methods, which nothing calls.</summary>
    public IReadOnlySet<string> Signatures { get; init; } = new HashSet<string>();

    /// <summary>A type test, a cast or an <c>as</c>: a reference converted to a class or an interface of the module.</summary>
    private void Conversion(IrExpr expression) {
      var (value, type) = expression switch {
        IrTypeTest test => (test.Value, test.Target),
        IrCast cast => (cast.Value, cast.Type),
        _ => (((IrAsCast)expression).Value, expression.Type)
      };
      this.Expression(value);
      this.Reference(value.Type);
      this.Reference(type);
    }

  }

}
