using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core;
using Polypiler.Permutation.Model;

namespace Polypiler.Permutation.Networks;

/// <summary>What a primitive is asked to route: the mapping, the output bits no term has routed yet, and the costs.</summary>
/// <param name="Mapping">The mapping.</param>
/// <param name="Remaining">The output bits still to route.</param>
/// <param name="Costs">The cost table the candidates are priced with.</param>
public sealed record RoutingProblem(BitMapping Mapping, BigInteger Remaining, PrimitiveCostTable Costs);

/// <summary>
/// A term a primitive offers: the output bits it routes, its cost, and how to emit it. The value it
/// emits must equal the <i>uninverted</i> source bit on every bit of <see cref="Routed"/> and be 0 on every
/// other bit (the term contract, which <see cref="CompositeNetwork.Assemble"/> checks).
/// </summary>
/// <param name="Primitive">The primitive's name.</param>
/// <param name="Routed">The output bits the term routes.</param>
/// <param name="Cost">Its cost under the problem's cost table.</param>
/// <param name="Description">What it does, for reports.</param>
/// <param name="Emit">Emits its steps into a builder whose value 0 is the mapping's input and gives the term's value.</param>
public sealed record PrimitiveCandidate(string Primitive, BigInteger Routed, long Cost, string Description, Func<NetworkBuilder, int> Emit);

/// <summary>
/// A routing primitive - a shift, a rotation, a bit extract, a byte shuffle: the operations it emits, the
/// widths it supports, and the terms it can route of a problem. The primitives themselves come with
/// their own issues; this is the point they plug into.
/// </summary>
public interface IPermutationPrimitive {

  /// <summary>The primitive's name, as cost tables name it, such as <c>shift</c> or <c>multiply</c>.</summary>
  string Id { get; }

  /// <summary>The operations its terms are made of.</summary>
  IReadOnlyList<NetworkOp> Ops { get; }

  /// <summary>Whether it routes values of a width.</summary>
  /// <param name="width">The width.</param>
  /// <returns><see langword="true"/> when it does.</returns>
  bool Supports(int width);

  /// <summary>The terms it can route of a problem.</summary>
  /// <param name="problem">The problem.</param>
  /// <returns>The candidates.</returns>
  IEnumerable<PrimitiveCandidate> FindRoutings(RoutingProblem problem);

}

/// <summary>The primitives a search may use, by name.</summary>
public sealed class PrimitiveRegistry {

  private readonly Dictionary<string, IPermutationPrimitive> _primitives = new(StringComparer.Ordinal);

  /// <summary>The registered primitives, in the order of registration.</summary>
  public IReadOnlyList<IPermutationPrimitive> Primitives => [.. this._primitives.Values];

  /// <summary>Registers a primitive.</summary>
  /// <param name="primitive">The primitive.</param>
  /// <returns>The registry, for chaining.</returns>
  /// <exception cref="InvalidOperationException">A primitive of that name is registered already.</exception>
  public PrimitiveRegistry Register(IPermutationPrimitive primitive) {
    ArgumentNullException.ThrowIfNull(primitive);
    if (!this._primitives.TryAdd(primitive.Id, primitive))
      throw new InvalidOperationException($"A primitive '{primitive.Id}' is registered already.");

    return this;
  }

  /// <summary>A primitive a search asks for by name: it must be registered, priced in the cost table and support the width.</summary>
  /// <param name="id">The primitive's name.</param>
  /// <param name="width">The width.</param>
  /// <param name="costs">The cost table.</param>
  /// <returns>The primitive.</returns>
  /// <exception cref="PermutationModelException">PLY0811: not registered or not in the cost table; PLY0812: the width is not supported.</exception>
  public IPermutationPrimitive Require(string id, int width, PrimitiveCostTable costs) {
    ArgumentNullException.ThrowIfNull(costs);
    if (!this._primitives.TryGetValue(id, out var primitive) || !costs.Has(id))
      throw new PermutationModelException(Diagnostic.Create(NetworkDiagnostics.Unavailable, null, id, costs.Name));
    if (!primitive.Supports(width))
      throw new PermutationModelException(Diagnostic.Create(NetworkDiagnostics.UnsupportedWidth, null, id, width));

    return primitive;
  }

  /// <summary>The primitives a search may use under a cost table at a width: priced there and supporting it.</summary>
  /// <param name="costs">The cost table.</param>
  /// <param name="width">The width.</param>
  /// <returns>The primitives, in the order of registration.</returns>
  public IEnumerable<IPermutationPrimitive> Available(PrimitiveCostTable costs, int width) {
    ArgumentNullException.ThrowIfNull(costs);
    return this._primitives.Values.Where(primitive => costs.Has(primitive.Id) && primitive.Supports(width));
  }

}
