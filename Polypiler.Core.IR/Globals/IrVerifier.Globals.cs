using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  /// <summary>
  /// Checks the globals of a module: each under a name of its own, of a truth value, a number or an
  /// array of numbers, an array with an initializer, and every initializer static - well-typed, of the
  /// global's type, naming only globals declared before it, and built of nothing but constants,
  /// operators, new arrays, listed arrays and calls of pure functions.
  /// </summary>
  /// <returns>The globals the functions may name.</returns>
  private static HashSet<IrGlobal> Globals(
    IrModule module,
    HashSet<string> names,
    Dictionary<string, IrFunction> functions,
    HashSet<IrModuleConst> constants,
    TypeTable types,
    List<Diagnostic> diagnostics) {
    var earlier = new HashSet<IrGlobal>(ReferenceEqualityComparer.Instance);
    var impure = Impure(module);
    foreach (var global in module.Globals) {
      void Require(bool holds, string message) {
        if (!holds)
          diagnostics.Add(Error(global.Name, message));
      }

      Require(names.Add(global.Name), "the module declares the name twice");
      var element = global.Type.Element ?? global.Type;
      Require(global.Type.IsStorable && !element.IsStruct, $"a global of the type {global.Type}");
      if (global.Initializer is not { } initializer) {
        Require(!global.Type.IsArray, "an array global without an initializer");
      } else {
        var check = new FunctionCheck(new IrFunction(global.Name, false, global.Type, [], [], new IrBlock([])), functions, constants, types, diagnostics) {
          Globals = new HashSet<IrGlobal>(earlier, ReferenceEqualityComparer.Instance)
        };
        check.Initializer(initializer, global.Type);
        foreach (var node in IrTree.Nodes(initializer).Where(node => !IsStatic(node, impure)).Take(1))
          Require(false, node is IrCall call
            ? $"the initializer calls '{call.Function}', which reads or writes a global"
            : $"the initializer holds a {node.GetType().Name}, which is not static");
      }

      earlier.Add(global);
    }

    return earlier;
  }

  /// <summary>Whether a node may stand in the initializer of a global.</summary>
  private static bool IsStatic(IrExpr node, HashSet<string> impure) => node switch {
    IrConst or IrConstRef or IrGlobalRef or IrUnary or IrBinary or IrConvert or IrConditional or IrNewArray => true,
    IrIntrinsic intrinsic => intrinsic.Op == IrIntrinsicOp.ArrayOf,
    IrCall call => !impure.Contains(call.Function),
    _ => false
  };

  /// <summary>
  /// The functions of a module that read or write a global, directly or through a function they call:
  /// the least fixed point over the calls.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <returns>The names of those functions.</returns>
  public static HashSet<string> Impure(IrModule module) {
    var impure = new HashSet<string>();
    var calls = module.Functions.Select(function => (function.Name, Callees: IrTree.Nodes(function.Body).OfType<IrCall>().Select(call => call.Function).ToList())).ToList();
    foreach (var function in module.Functions)
      if (IrTree.Nodes(function.Body).Any(node => node is IrGlobalRef) || Statements(function.Body).Any(statement => statement is IrGlobalAssign))
        impure.Add(function.Name);

    for (var changed = true; changed;) {
      changed = false;
      foreach (var (name, callees) in calls.Where(function => !impure.Contains(function.Name) && function.Callees.Any(impure.Contains)))
        changed |= impure.Add(name);
    }

    return impure;
  }

  /// <summary>Every statement within a statement, the statement first.</summary>
  private static IEnumerable<IrStmt> Statements(IrStmt statement)
    => (statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(nested => nested.Statements)).SelectMany(Statements).Prepend(statement);

  private sealed partial class FunctionCheck {

    /// <summary>Checks the initializer of a global as an expression of the global's type.</summary>
    public void Initializer(IrExpr initializer, IrType type) => this.Value(initializer, type, "the initializer");

  }

}
