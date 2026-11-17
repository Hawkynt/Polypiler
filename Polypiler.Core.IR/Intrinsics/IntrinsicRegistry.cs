using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Polypiler.Core.IR.Intrinsics;

/// <summary>
/// The intrinsics a compilation knows: for each its contract, the evaluator that states its meaning
/// in code, and its decomposition, the fallback a target without a native form gets.
/// </summary>
public interface IIntrinsicRegistry {

  /// <summary>Every contract, ordered by name ordinally.</summary>
  IReadOnlyList<IntrinsicContract> All { get; }

  /// <summary>The contract of an intrinsic.</summary>
  /// <param name="id">The name.</param>
  /// <returns>The contract.</returns>
  /// <exception cref="IntrinsicRegistryException"><c>PLY0250</c>: no intrinsic has the name.</exception>
  IntrinsicContract Get(IntrinsicId id);

  /// <summary>The contract of an intrinsic, if one has the name.</summary>
  /// <param name="id">The name.</param>
  /// <param name="contract">The contract, or <see langword="null"/>.</param>
  /// <returns>Whether one has the name.</returns>
  bool TryGet(IntrinsicId id, [NotNullWhen(true)] out IntrinsicContract? contract);

  /// <summary>The contract an IR spelling names: its name, or its dotted spelling such as <c>Memory.Copy</c>.</summary>
  /// <param name="spelling">The spelling.</param>
  /// <param name="contract">The contract, or <see langword="null"/>.</param>
  /// <returns>Whether one has the spelling.</returns>
  bool TryResolve(string spelling, [NotNullWhen(true)] out IntrinsicContract? contract);

  /// <summary>The evaluator of an intrinsic.</summary>
  /// <param name="id">The name.</param>
  /// <returns>The evaluator.</returns>
  /// <exception cref="IntrinsicRegistryException"><c>PLY0250</c>: no intrinsic has the name.</exception>
  IIntrinsicEvaluator Evaluator(IntrinsicId id);

  /// <summary>The decomposition of an intrinsic; <see langword="null"/> for one that requires the runtime of the target.</summary>
  /// <param name="id">The name.</param>
  /// <returns>The decomposition, or <see langword="null"/>.</returns>
  /// <exception cref="IntrinsicRegistryException"><c>PLY0250</c>: no intrinsic has the name.</exception>
  IIntrinsicDecomposition? Decomposition(IntrinsicId id);

}

/// <summary>
/// Builds an <see cref="IIntrinsicRegistry"/>. <see cref="Build"/> checks what a registry must hold, so
/// that a defect is found at start-up and not when an intrinsic is first used: each name once
/// (<c>PLY0253</c>), a fallback for each intrinsic (<c>PLY0254</c>) and no intrinsic that lowers to
/// itself (<c>PLY0256</c>). The result does not depend on the order of <see cref="Add"/>.
/// </summary>
public sealed class IntrinsicRegistryBuilder {

  private const string IntrinsicsPrefix = "INTRINSICS.";

  private readonly List<Entry> _entries = [];

  /// <summary>Adds an intrinsic.</summary>
  /// <param name="contract">Its contract.</param>
  /// <param name="evaluator">Its evaluator.</param>
  /// <param name="decomposition">Its decomposition; <see langword="null"/> only when the contract requires the runtime.</param>
  /// <returns>This builder.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="contract"/> or <paramref name="evaluator"/> is <see langword="null"/>.</exception>
  public IntrinsicRegistryBuilder Add(IntrinsicContract contract, IIntrinsicEvaluator evaluator, IIntrinsicDecomposition? decomposition) {
    ArgumentNullException.ThrowIfNull(contract);
    ArgumentNullException.ThrowIfNull(evaluator);
    this._entries.Add(new(contract, evaluator, decomposition));
    return this;
  }

  /// <summary>Builds the registry.</summary>
  /// <returns>The registry.</returns>
  /// <exception cref="IntrinsicRegistryException"><c>PLY0253</c>, <c>PLY0254</c> or <c>PLY0256</c>, for the first intrinsic by name that has the defect.</exception>
  public IIntrinsicRegistry Build() {
    var ordered = this._entries.OrderBy(entry => entry.Contract.Id.Name, StringComparer.Ordinal).ToList();
    for (var index = 1; index < ordered.Count; ++index)
      if (ordered[index].Contract.Id == ordered[index - 1].Contract.Id)
        throw new IntrinsicRegistryException(IntrinsicDiagnostics.Duplicate.At($"intrinsic '{ordered[index].Contract.Id}' is registered twice"));

    if (ordered.FirstOrDefault(entry => entry.Decomposition is null && !entry.Contract.RequiresRuntime) is { } bare)
      throw new IntrinsicRegistryException(IntrinsicDiagnostics.NoFallback.At($"intrinsic '{bare.Contract.Id}' has no fallback"));

    var byName = ordered.ToDictionary(entry => entry.Contract.Id.Name, StringComparer.Ordinal);
    foreach (var entry in ordered)
      if (Cycle(entry.Contract.Id.Name, byName) is { } path)
        throw new IntrinsicRegistryException(IntrinsicDiagnostics.Cycle.At($"intrinsic '{entry.Contract.Id}' lowers to itself through {string.Join(" -> ", path)}"));

    return new Registry(ordered);
  }

  /// <summary>The path along <c>lowersTo</c> from an intrinsic back to itself, or <see langword="null"/> when there is none.</summary>
  private static List<string>? Cycle(string start, Dictionary<string, Entry> byName) {
    var visited = new HashSet<string>(StringComparer.Ordinal);
    List<string>? Walk(string name, List<string> path) {
      foreach (var next in Successors(name, byName)) {
        if (next == start)
          return [.. path, next];
        if (visited.Add(next) && Walk(next, [.. path, next]) is { } found)
          return found;
      }
      return null;
    }

    return Walk(start, [start]);
  }

  private static IEnumerable<string> Successors(string name, Dictionary<string, Entry> byName)
    => byName[name].Contract.LowersTo
      .Where(id => id.StartsWith(IntrinsicsPrefix, StringComparison.Ordinal))
      .Select(id => id[IntrinsicsPrefix.Length..])
      .Where(byName.ContainsKey);

  private sealed record Entry(IntrinsicContract Contract, IIntrinsicEvaluator Evaluator, IIntrinsicDecomposition? Decomposition);

  private sealed class Registry : IIntrinsicRegistry {

    private readonly Dictionary<IntrinsicId, Entry> _byId;
    private readonly Dictionary<string, Entry> _bySpelling;

    public Registry(List<Entry> ordered) {
      this.All = [.. ordered.Select(entry => entry.Contract)];
      this._byId = ordered.ToDictionary(entry => entry.Contract.Id);
      this._bySpelling = new(StringComparer.Ordinal);
      foreach (var entry in ordered) {
        this._bySpelling[entry.Contract.Id.Name] = entry;
        this._bySpelling[entry.Contract.Spelling] = entry;
      }
    }

    public IReadOnlyList<IntrinsicContract> All { get; }

    public IntrinsicContract Get(IntrinsicId id) => this.Entry(id).Contract;

    public bool TryGet(IntrinsicId id, [NotNullWhen(true)] out IntrinsicContract? contract) {
      contract = this._byId.GetValueOrDefault(id)?.Contract;
      return contract is not null;
    }

    public bool TryResolve(string spelling, [NotNullWhen(true)] out IntrinsicContract? contract) {
      ArgumentNullException.ThrowIfNull(spelling);
      contract = this._bySpelling.GetValueOrDefault(spelling)?.Contract;
      return contract is not null;
    }

    public IIntrinsicEvaluator Evaluator(IntrinsicId id) => this.Entry(id).Evaluator;

    public IIntrinsicDecomposition? Decomposition(IntrinsicId id) => this.Entry(id).Decomposition;

    private Entry Entry(IntrinsicId id)
      => this._byId.GetValueOrDefault(id) ?? throw new IntrinsicRegistryException(IntrinsicDiagnostics.Unknown.At($"unknown intrinsic '{id}'"));

  }

}

/// <summary>Checks the intrinsic nodes of a module against the contracts of a registry.</summary>
public static class IntrinsicVerifier {

  /// <summary>
  /// Checks every <see cref="IrIntrinsic"/> node of a module, those of globals and of lambdas included:
  /// <c>PLY0250</c> for an operation the registry has no contract of, <c>PLY0252</c> and <c>PLY0255</c>
  /// for a node that does not keep its contract.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <param name="registry">The registry.</param>
  /// <returns>One diagnostic per node in error, in the order of the module.</returns>
  public static IReadOnlyList<Diagnostic> Verify(IrModule module, IIntrinsicRegistry registry) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(registry);
    var diagnostics = new List<Diagnostic>();
    var nodes = module.Globals.Select(global => global.Initializer).OfType<IrExpr>().SelectMany(IrTree.Nodes)
      .Concat(module.Functions.SelectMany(function => IrTree.Nodes(function.Body)));
    foreach (var node in nodes.OfType<IrIntrinsic>()) {
      var id = IntrinsicId.Of(node.Op);
      if (!registry.TryGet(id, out var contract))
        diagnostics.Add(IntrinsicDiagnostics.Unknown.At($"unknown intrinsic '{id}'"));
      else if (contract.Check(node) is { } problem)
        diagnostics.Add(problem);
    }

    return diagnostics;
  }

}
