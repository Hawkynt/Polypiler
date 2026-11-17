using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Optimize.Interprocedural;

/// <summary>The functions reachable from retained declarations and code; all globals remain live.</summary>
/// <param name="LiveFunctions">The functions that must remain, by object identity.</param>
/// <param name="LiveGlobals">Every global of the module, by object identity.</param>
/// <param name="Roots">For each live function, the first root that reaches it, by function name.</param>
public sealed record ReachabilityResult(
  ImmutableHashSet<IrFunction> LiveFunctions,
  ImmutableHashSet<IrGlobal> LiveGlobals,
  ImmutableDictionary<string, string> Roots);

/// <summary>Module reachability over the structured tree IR, including calls inside closures.</summary>
public static class ModuleReachability {

  /// <summary>
  /// Marks exports, the module initializer, functions named by retained type declarations and every
  /// function a global initializer names; follows direct calls and function references in their bodies.
  /// All declarations stay, so their method bodies and abstract signatures stay too. A frontend's test
  /// functions are additional roots. External functions live in a separate list and are untouched.
  /// </summary>
  /// <param name="module">The module to inspect.</param>
  /// <param name="additionalRoots">Additional entry points, such as tests, by function name.</param>
  /// <returns>The live sets and the root that first reached each function.</returns>
  public static ReachabilityResult Compute(IrModule module, IEnumerable<string>? additionalRoots = null) {
    ArgumentNullException.ThrowIfNull(module);
    var functions = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    var roots = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
    var live = ImmutableHashSet.CreateBuilder<IrFunction>(ReferenceEqualityComparer.Instance);
    var pending = new Queue<IrFunction>();

    void Mark(string name, string root) {
      if (!functions.TryGetValue(name, out var function) || !live.Add(function))
        return;

      roots.Add(name, root);
      pending.Enqueue(function);
    }

    void Follow(IEnumerable<IrExpr> nodes, string root) {
      foreach (var node in nodes)
        if (node is IrCall call)
          Mark(call.Function, root);
        else if (node is IrFunctionRef reference)
          Mark(reference.Function, root);
    }

    foreach (var function in module.Functions.Where(function => function.IsPublic))
      Mark(function.Name, "export " + function.Name);

    foreach (var name in (additionalRoots ?? []).Order(StringComparer.Ordinal))
      Mark(name, "entry " + name);

    if (module.Initializer is { } initializer)
      Mark(initializer, "module initializer");

    // A retained type names its bodies even where no instruction calls them. Removing them would
    // leave dangling declarations and break virtual dispatch and callers outside the module.
    foreach (var type in module.Types) {
      var members = type switch {
        IrClassType @class => @class.Functions,
        IrInterfaceType @interface => @interface.Functions,
        _ => []
      };
      foreach (var member in members)
        Mark(member, "type " + type.Name);
    }

    foreach (var global in module.Globals)
      if (global.Initializer is { } value)
        Follow(IrTree.Nodes(value), "global " + global.Name);

    while (pending.TryDequeue(out var function))
      Follow(IrTree.Nodes(function.Body), roots[function.Name]);

    return new(live.ToImmutable(), module.Globals.ToImmutableHashSet<IrGlobal>(ReferenceEqualityComparer.Instance), roots.ToImmutable());
  }

}
