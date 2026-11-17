using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Interprocedural;

namespace Polypiler.Core.Optimize.Passes;

/// <summary>O0381: removes functions not reachable from exports, retained declarations or initialization.</summary>
public sealed class DeadFunctionElimination : IIrPass {

  /// <inheritdoc/>
  public string Id => "O0381";

  /// <inheritdoc/>
  public string Name => "dead function elimination";

  /// <summary>Runs a module-wide sweep; a per-function scope cannot restrict reachability.</summary>
  /// <param name="module">The module.</param>
  /// <param name="scope">The pipeline's function scope, validated but not used by this module pass.</param>
  /// <param name="applied">Receives each removed function in source order.</param>
  /// <returns>The swept module, or the same object if every function is live.</returns>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied)
    => this.Run(module, scope, applied, _ => { });

  /// <summary>Runs a sweep and reports each removal with its size and structural proof class.</summary>
  /// <param name="module">The module.</param>
  /// <param name="scope">The pipeline's function scope, validated but not used by this module pass.</param>
  /// <param name="applied">Receives each removed function in source order.</param>
  /// <param name="note">Receives the explanation of each removal.</param>
  /// <param name="roots">Additional entry points such as a frontend's tests.</param>
  /// <returns>The swept module, or the same object if every function is live.</returns>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied, Action<string> note, IEnumerable<string>? roots = null) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(scope);
    ArgumentNullException.ThrowIfNull(applied);
    ArgumentNullException.ThrowIfNull(note);
    var live = ModuleReachability.Compute(module, roots).LiveFunctions;
    if (live.Count == module.Functions.Count)
      return module;

    foreach (var function in module.Functions.Where(function => !live.Contains(function))) {
      applied(function.Name, "module");
      note($"O0381 applied: @{function.Name} removed ({IrPassSupport.Size(function.Body)} instructions): unreachable from exports, tests and taken addresses [proof: structural]");
    }

    var functions = module.Functions.Where(live.Contains).ToList();
    var names = functions.Select(function => function.Name).ToHashSet(StringComparer.Ordinal);
    return module with {
      Functions = functions,
      Implementations = [.. module.Implementations.Where(pair => names.Contains(pair.Function))]
    };
  }

}
