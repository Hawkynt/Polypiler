using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize.Interprocedural;

/// <summary>The functions and globals reachable from retained declarations and code.</summary>
/// <param name="LiveFunctions">The functions that must remain, by object identity.</param>
/// <param name="LiveGlobals">The globals that must remain, by object identity.</param>
/// <param name="Roots">For each live function, the first root that reaches it, by function name.</param>
public sealed record ReachabilityResult(
  ImmutableHashSet<IrFunction> LiveFunctions,
  ImmutableHashSet<IrGlobal> LiveGlobals,
  ImmutableDictionary<string, string> Roots);

/// <summary>Which declarations form roots of the joint code and data graph.</summary>
public sealed record ReachabilityOptions {

  /// <summary>Keep every function when the function sweep is disabled.</summary>
  public bool AllFunctionsLive { get; init; }

  /// <summary>Keep every global when the global sweep is disabled; the O0381-only default.</summary>
  public bool AllGlobalsLive { get; init; } = true;

  /// <summary>Global names a host or profile retains, including external and device storage.</summary>
  public IReadOnlySet<string> GlobalRoots { get; init; } = new HashSet<string>();

}

/// <summary>Module reachability over the structured tree IR, including calls inside closures.</summary>
public static class ModuleReachability {

  /// <summary>
  /// Marks exports, the module initializer and functions named by retained type declarations, then
  /// follows calls, function references, global reads and global writes in one worklist.
  /// All declarations stay, so their method bodies and abstract signatures stay too. A frontend's test
  /// functions are additional roots. External functions live in a separate list and are untouched.
  /// </summary>
  /// <param name="module">The module to inspect.</param>
  /// <param name="additionalRoots">Additional entry points, such as tests, by function name.</param>
  /// <param name="options">Whether disabled sweeps retain all functions or globals, and host-owned globals.</param>
  /// <returns>The live sets and the root that first reached each function.</returns>
  public static ReachabilityResult Compute(IrModule module, IEnumerable<string>? additionalRoots = null, ReachabilityOptions? options = null) {
    ArgumentNullException.ThrowIfNull(module);
    options ??= new();
    var functions = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    var roots = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
    var live = ImmutableHashSet.CreateBuilder<IrFunction>(ReferenceEqualityComparer.Instance);
    var globals = module.Globals.ToHashSet<IrGlobal>(ReferenceEqualityComparer.Instance);
    var liveGlobals = ImmutableHashSet.CreateBuilder<IrGlobal>(ReferenceEqualityComparer.Instance);
    var globalOrigins = new Dictionary<IrGlobal, string>(ReferenceEqualityComparer.Instance);
    var pending = new Queue<object>();

    void Mark(string name, string root) {
      if (!functions.TryGetValue(name, out var function) || !live.Add(function))
        return;

      roots.Add(name, root);
      pending.Enqueue(function);
    }

    void MarkGlobal(IrGlobal global, string root) {
      if (!globals.Contains(global) || !liveGlobals.Add(global))
        return;

      globalOrigins.Add(global, root);
      pending.Enqueue(global);
    }

    void Writes(IrBlock body, string root) {
      foreach (var assignment in IrPassSupport.Statements(body).OfType<IrGlobalAssign>())
        MarkGlobal(assignment.Global, root);
    }

    void Follow(IEnumerable<IrExpr> nodes, string root) {
      foreach (var node in nodes) {
        if (node is IrCall call)
          Mark(call.Function, root);
        else if (node is IrFunctionRef reference)
          Mark(reference.Function, root);
        else if (node is IrGlobalRef global)
          MarkGlobal(global.Global, root);
        else if (node is IrLambda lambda)
          Writes(lambda.Body, root);
      }
    }

    foreach (var function in module.Functions.Where(function => function.IsPublic || options.AllFunctionsLive))
      Mark(function.Name, (function.IsPublic ? "export " : "retained function ") + function.Name);

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
      if (type is IrClassType owner)
        foreach (var field in owner.StaticFields)
          MarkGlobal(field, "type " + owner.Name);
    }

    var effects = IrEffects.Of(module);
    foreach (var global in module.Globals)
      if (options.AllGlobalsLive || options.GlobalRoots.Contains(global.Name)
          || global.Initializer is { } value && !CanDiscard(value, effects))
        MarkGlobal(global, "global " + global.Name);

    while (pending.TryDequeue(out var declaration)) {
      if (declaration is IrFunction function) {
        var root = roots[function.Name];
        Follow(IrTree.Nodes(function.Body), root);
        Writes(function.Body, root);
      } else if (declaration is IrGlobal { Initializer: { } value } global) {
        Follow(IrTree.Nodes(value), globalOrigins[global]);
      }
    }

    return new(live.ToImmutable(), liveGlobals.ToImmutable(), roots.ToImmutable());
  }

  /// <summary>
  /// Initializers execute even when nobody reads the value. Preserve traps, unknown effects and
  /// possible nontermination. Listed data and fixed nonnegative array lengths are static storage;
  /// host resource exhaustion is not a modeled IR outcome of removing unused static data.
  /// </summary>
  private static bool CanDiscard(IrExpr value, IrEffectTable effects) {
    var removable = value switch {
      IrGlobalRef or IrConstRef or IrStrConst or IrFunctionRef or IrNull => true,
      IrNewArray { Length: IrConst length } => length.Value is >= 0 and <= int.MaxValue,
      IrIntrinsic { Op: IrIntrinsicOp.ArrayOf } => true,
      IrCall call => effects.Function(call.Function).IsRemovable,
      _ => IrShape.IsSideEffectFreeNode(value) && IrShape.IsTrapFreeNode(value)
    };
    return removable && IrTree.Operands(value).All(operand => CanDiscard(operand, effects));
  }

}
