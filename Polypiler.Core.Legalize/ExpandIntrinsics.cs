using System;
using Polypiler.Core.IR;
using Polypiler.Core.IR.Intrinsics;

namespace Polypiler.Core.Legalize;

/// <summary>
/// Replaces intrinsic nodes by the decompositions their registry gives: the generic lowering of an
/// intrinsic a target has no native form of. A node keeps its value, the order in which its operands
/// are evaluated and the traps they raise; a node whose contract requires the runtime, or whose
/// decomposition has nothing for its type, stays as it is.
/// </summary>
public static class ExpandIntrinsics {

  private static readonly IrModule _noLibrary = new("NoLibrary", []);

  /// <summary>The module with every selected intrinsic node expanded.</summary>
  /// <param name="module">The module.</param>
  /// <param name="registry">The registry whose decompositions expand the nodes.</param>
  /// <param name="predicate">Selects the nodes to expand.</param>
  /// <param name="implementations">
  /// Gives the libraries a decomposition calls into as one module; <see langword="null"/> for none, when no
  /// decomposition calls a library.
  /// </param>
  /// <returns>The module, the functions of the libraries the decompositions call copied in.</returns>
  /// <exception cref="ArgumentNullException">An argument other than <paramref name="implementations"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException">A decomposition calls a function the libraries do not have.</exception>
  public static IrModule Run(IrModule module, IIntrinsicRegistry registry, Func<IrIntrinsic, bool> predicate, Func<IrModule>? implementations = null) {
    ArgumentNullException.ThrowIfNull(module);
    return LibraryLegalization.Lower(module, implementations ?? (() => _noLibrary), Rewrite(registry, predicate));
  }

  /// <summary>The expansion as a rewrite of <see cref="LibraryLegalization"/>, for a backend that composes it with its own.</summary>
  /// <param name="registry">The registry.</param>
  /// <param name="predicate">Selects the nodes to expand.</param>
  /// <returns>The rewrite.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static LibraryRewrite Rewrite(IIntrinsicRegistry registry, Func<IrIntrinsic, bool> predicate) {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(predicate);
    return (expression, calls) => expression is IrIntrinsic node && predicate(node)
      && registry.TryGet(IntrinsicId.Of(node.Op), out var contract) && !contract.RequiresRuntime
      && registry.Decomposition(contract.Id) is { } decomposition
        ? decomposition.Expand(node, calls)
        : null;
  }

}
