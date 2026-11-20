using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Interprocedural;

namespace Polypiler.Core.Optimize.Passes;

/// <summary>O0383 removes unreferenced global storage and, with O0381, its unreachable code.</summary>
public sealed class DeadGlobalElimination : IIrPass {

  /// <inheritdoc/>
  public string Id => "O0383";

  /// <inheritdoc/>
  public string Name => "dead global elimination";

  /// <summary>Sweeps globals while retaining every existing function.</summary>
  /// <param name="module">The module.</param>
  /// <param name="scope">Validated but unused: reachability is module-wide.</param>
  /// <param name="applied">Receives each removed global in source order.</param>
  /// <returns>The swept module, or the same object when nothing changes.</returns>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied)
    => this.Run(module, scope, applied, _ => { });

  /// <summary>Sweeps code and data atomically when a function-removal callback is supplied.</summary>
  /// <param name="module">The module.</param>
  /// <param name="scope">Validated but unused: reachability is module-wide.</param>
  /// <param name="applied">Receives removed globals.</param>
  /// <param name="note">Receives removal explanations for both rules.</param>
  /// <param name="roots">Additional function entry points.</param>
  /// <param name="globalRoots">Global names a host or target profile retains.</param>
  /// <param name="functionApplied">When supplied, enables O0381 and receives its removals.</param>
  /// <returns>The swept module, with no dangling references between sweeps.</returns>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied, Action<string> note,
    IEnumerable<string>? roots = null, IReadOnlySet<string>? globalRoots = null, Action<string, string>? functionApplied = null) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(scope);
    ArgumentNullException.ThrowIfNull(applied);
    ArgumentNullException.ThrowIfNull(note);
    var live = ModuleReachability.Compute(module, roots, new ReachabilityOptions {
      AllGlobalsLive = false, AllFunctionsLive = functionApplied is null, GlobalRoots = globalRoots ?? new HashSet<string>()
    });
    var result = functionApplied is null ? module : DeadFunctionElimination.Sweep(module, live.LiveFunctions, functionApplied, note);
    if (live.LiveGlobals.Count == module.Globals.Count)
      return result;

    foreach (var global in module.Globals.Where(global => !live.LiveGlobals.Contains(global))) {
      applied(global.Name, "module global");
      note($"O0383 applied: global @{global.Name} removed ({DataBytes(global)} bytes): unreferenced [proof: structural]");
    }

    return result with { Globals = [.. module.Globals.Where(live.LiveGlobals.Contains)] };
  }

  /// <summary>Logical payload size, excluding target-dependent references, alignment and headers.</summary>
  private static string DataBytes(IrGlobal global) {
    var element = global.Type.Element ?? global.Type;
    var size = element == IrType.Bool ? 1 : element.Size;
    long? count = global.Initializer switch {
      IrIntrinsic { Op: IrIntrinsicOp.ArrayOf } array => array.Arguments.Count,
      IrNewArray { Length: IrConst length } => length.Value,
      _ => global.Type.IsArray ? null : 1
    };
    return size > 0 && count is { } elements
      ? (size * elements).ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
  }

}
