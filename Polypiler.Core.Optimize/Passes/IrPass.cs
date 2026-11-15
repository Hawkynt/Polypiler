using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Optimize.Passes;

/// <summary>
/// A pass over a legalized module: what runs after a backend bound the helpers a module uses into it
/// and before the backend writes it, so that a helper is optimised like the code that calls it. A
/// pass shares the namespace of optimization numbers with the rules and the target passes.
/// </summary>
public interface IIrPass {

  /// <summary>The optimization number, such as <c>O0006</c>.</summary>
  string Id { get; }

  /// <summary>What the pass does, as <c>--explain-opt</c> names it.</summary>
  string Name { get; }

  /// <summary>Runs the pass once over the functions of a module it is to look at.</summary>
  /// <param name="module">The module.</param>
  /// <param name="scope">Whether the pass looks at a function; inlining, which brings a helper into a function, looks at every one.</param>
  /// <param name="applied">Receives every change: the function and the position of the statement it was made at.</param>
  /// <returns>The module, the same object when nothing changed.</returns>
  IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied);

}

/// <summary>
/// The passes that run over a legalized module, in table order, round after round until a round
/// changes nothing or <see cref="RoundLimit"/> rounds have run: inlining (O0006), constant propagation
/// (O0003), constant folding (O0001), dead-code removal (O0005) and full unrolling (O0023). Each
/// makes work for the next - an inlined helper has constant parameters, a constant condition a dead
/// branch, an unrolled loop constant indices - and none of them changes what a module computes, its
/// traps and the point they happen at included. They look at the functions legalization copied in
/// from the libraries and at every function a helper was inlined into; the rest of the program stays
/// as the frontend made it.
/// </summary>
public sealed class IrPassPipeline {

  /// <summary>The most rounds over a module.</summary>
  public const int RoundLimit = 6;

  /// <summary>The pipeline of <c>translate</c>.</summary>
  public static IrPassPipeline Default { get; } = new([new O0006Inlining(), new O0003ConstantPropagation(), new O0001ConstantFolding(), new O0005DeadCode(), new O0023Unrolling()]);

  /// <summary>Makes a pipeline of passes.</summary>
  /// <param name="passes">The passes, in the order they run.</param>
  /// <exception cref="ArgumentException">Two passes have the same number.</exception>
  public IrPassPipeline(IEnumerable<IIrPass> passes) {
    this.Passes = [.. passes];
    if (this.Passes.Select(pass => pass.Id).Distinct(StringComparer.Ordinal).Count() != this.Passes.Count)
      throw new ArgumentException("Two passes have the same optimization number.", nameof(passes));
  }

  /// <summary>The passes, in the order they run.</summary>
  public IReadOnlyList<IIrPass> Passes { get; }

  /// <summary>The numbers of the passes, in order.</summary>
  public IEnumerable<string> Ids => this.Passes.Select(pass => pass.Id);

  /// <summary>Runs the passes the options let run over a module.</summary>
  /// <param name="module">A legalized module.</param>
  /// <param name="options">The level, which runs none at <see cref="OptimizationLevel.None"/>, and the numbers that do not run.</param>
  /// <returns>The module and every change, in the order the changes were made.</returns>
  /// <exception cref="InvalidOperationException">With <see cref="OptimizerOptions.Verify"/>: a pass left the module ill-typed.</exception>
  public OptimizationResult Run(IrModule module, OptimizerOptions options) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(options);
    var applications = new List<RuleApplication>();
    if (options.Level == OptimizationLevel.None)
      return new(module, applications);

    var passes = this.Passes.Where(pass => !options.Disabled.Contains(pass.Id)).ToList();
    var scope = module.Implementations.Select(pair => pair.Function).ToHashSet(StringComparer.Ordinal);
    for (var round = 0; round < RoundLimit; ++round) {
      var before = applications.Count;
      foreach (var pass in passes) {
        var inlining = pass is O0006Inlining;
        module = pass.Run(module, function => inlining || scope.Contains(function.Name), (function, position) => {
          applications.Add(new(pass.Id, pass.Name, function, position));
          if (inlining)
            scope.Add(function);
        });
        if (options.Verify && IrVerifier.Verify(module) is { Count: > 0 } problems)
          throw new InvalidOperationException($"{pass.Id} {pass.Name} left the module ill-typed: {string.Join("; ", problems)}");
      }

      if (applications.Count == before)
        break;
    }

    return new(module, applications);
  }

}
