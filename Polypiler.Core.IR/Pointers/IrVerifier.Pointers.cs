using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  /// <summary>What the verifier says of a pointer kept where it could outlive its region: a field, a global, a constant or a result.</summary>
  internal const string PointerStored = "a pointer is stored outside the stack";

  /// <summary>A pointer lives on the stack only: no constant, global or field of a struct or a class is of a pointer type.</summary>
  private static void Pointers(IrModule module, List<Diagnostic> diagnostics) {
    var places = module.Constants.Select(constant => (constant.Name, constant.Type))
      .Concat(module.Globals.Select(global => (global.Name, global.Type)))
      .Concat(module.Types.Where(type => type is IrStructType or IrClassType).SelectMany(type => type.Fields.Select(field => ($"{type.Name}.{field.Name}", field.Type))));
    foreach (var (name, type) in places.Where(place => place.Type.IsPointer))
      diagnostics.Add(Error(name, PointerStored));
  }

  private sealed partial class FunctionCheck {

    /// <summary>The rules of the bounded pointers for the function, <see cref="IrPointers.Problems"/>.</summary>
    private void Pointers() {
      foreach (var problem in IrPointers.Problems(function))
        this.Require(false, problem);
    }

  }

}
