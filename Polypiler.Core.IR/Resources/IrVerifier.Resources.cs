using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  /// <summary>
  /// Checks the interfaces of cleanup of a module (<see cref="IrInterfaceType.IsDisposable"/>): each has
  /// the one method <c>Dispose()</c>, which returns nothing, no property and no base interface - the
  /// shape of .NET's <c>System.IDisposable</c>, which a backend names in their place.
  /// </summary>
  private static void Disposables(TypeTable types, Dictionary<string, IrFunction> functions, List<Diagnostic> diagnostics) {
    foreach (var contract in types.Interfaces.Where(contract => contract.IsDisposable))
      if (!(contract.Methods is [{ Name: IrDisposable.Method, Kind: IrMethodKind.Method } dispose] && contract.Properties.Count == 0 && contract.BaseInterfaces.Count == 0
          && functions.TryGetValue(dispose.Function, out var signature) && signature.Parameters.Count == 1 && signature.ReturnType == IrType.Void))
        diagnostics.Add(Error(contract.Name, "an interface of cleanup has another member than the method Dispose(), which returns nothing"));
  }

  private sealed partial class FunctionCheck {

    /// <summary>
    /// A resource scope: the resource is a local of the function, declared once, a reference to an
    /// object that implements the interface of cleanup whose <c>Dispose</c> the scope calls; the
    /// initializer is of its type; the body sees it and assigns it nowhere.
    /// </summary>
    private void Using(IrUsing scope) {
      var resource = scope.Resource;
      this.Require(function.Locals.Contains(resource), $"local '{resource.Name}' is not among the locals of the function");
      this.Value(scope.Initializer, resource.Type, $"the initializer of the resource '{resource.Name}'");
      var contract = types.Interfaces.FirstOrDefault(type => type.IsDisposable && type.Methods.Contains(scope.Dispose));
      this.Require(contract is not null && resource.Type.IsObject && resource.Type.IsAssignableTo(IrType.Object(contract)),
        $"the resource '{resource.Name}' of the type {resource.Type} is disposed through no interface of cleanup it implements");
      this.Require(this._visible.Add(resource), $"local '{resource.Name}' is declared twice");
      this.Require(!Assigns(scope.Body, resource), $"the resource '{resource.Name}' is assigned");
      this.Block(scope.Body);
      this._visible.Remove(resource);
    }

    /// <summary>Whether a statement or one in it assigns a local.</summary>
    private static bool Assigns(IrStmt statement, IrLocal local)
      => (statement is IrAssign assign && assign.Local == local)
        || (statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(inner => inner.Statements)).Any(inner => Assigns(inner, local));

  }

}
