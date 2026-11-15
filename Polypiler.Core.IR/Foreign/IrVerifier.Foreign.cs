using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  /// <summary>
  /// The foreign functions of a module (#1332): each under a name no function or constant of the module has, every
  /// parameter and the result of a type its marshalling takes (<see cref="IrForeign.Problems"/>), every struct among the
  /// types of the module.
  /// </summary>
  /// <returns>The declarations by their names, which the calls name.</returns>
  private static Dictionary<string, IrForeignFunction> Foreign(IrModule module, HashSet<string> names, TypeTable types, List<Diagnostic> diagnostics) {
    var declared = new Dictionary<string, IrForeignFunction>();
    foreach (var function in module.ForeignFunctions) {
      if (!names.Add(function.Name) || !declared.TryAdd(function.Name, function))
        diagnostics.Add(Error(function.Name, "the module declares the name twice"));

      foreach (var problem in IrForeign.Problems(function))
        diagnostics.Add(Error(function.Name, problem));

      foreach (var type in function.ParameterTypes.Append(function.ReturnType).Select(type => type.Element ?? type).Where(type => type.StructType is { } named && !types.Declares(named)))
        diagnostics.Add(Error(function.Name, $"the type {type} is not among the types of the module"));
    }

    foreach (var layout in module.Types.OfType<IrStructType>().Where(type => type.Layout is not null && type.IsDefined)) {
      var (kind, pack, size, offsets) = layout.Layout!;
      if (kind == IrLayoutKind.Explicit ? offsets.Count != layout.Fields.Count || offsets.Any(offset => offset < 0) : offsets.Count != 0)
        diagnostics.Add(Error(layout.Name, $"a {kind} layout with {offsets.Count} offsets for {layout.Fields.Count} fields"));

      // StructLayoutAttribute.Pack is 0 or a power of two up to 128.
      if ((pack != 0 && !(pack <= 128 && System.Numerics.BitOperations.IsPow2(pack))) || size < 0)
        diagnostics.Add(Error(layout.Name, $"a layout packed by {pack} of the size {size}"));
    }

    return declared;
  }

  private sealed partial class FunctionCheck {

    /// <summary>The foreign functions of the module by their names, which a <see cref="IrForeignCall"/> names.</summary>
    public IReadOnlyDictionary<string, IrForeignFunction> ForeignFunctions { get; init; } = new Dictionary<string, IrForeignFunction>();

    /// <summary>
    /// A call of a foreign function - one the module declares, with an argument of the parameter's type for every
    /// parameter, of the result's type - or the last error, which is an <c>i32</c>.
    /// </summary>
    private void Foreign(IrExpr expression) {
      if (expression is not IrForeignCall call)
        return;

      foreach (var argument in call.Arguments)
        this.Expression(argument);

      if (!this.ForeignFunctions.TryGetValue(call.Function, out var callee)) {
        this.Require(false, $"a call of the foreign function '{call.Function}', which the module does not declare");
        return;
      }

      this.Require(call.Arguments.Select(argument => argument.Type).SequenceEqual(callee.ParameterTypes),
        $"the foreign function '{call.Function}' ({string.Join(", ", callee.ParameterTypes)}) is called with ({string.Join(", ", call.Arguments.Select(argument => argument.Type))})");
      this.Require(call.Type == callee.ReturnType, $"the foreign function '{call.Function}' returns {callee.ReturnType}, not {call.Type}");
    }

  }

}
