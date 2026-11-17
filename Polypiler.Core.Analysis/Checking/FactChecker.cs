using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Checking;

/// <summary>
/// Holds the facts a facade answered against real executions (#88): runs the function in the interpreter
/// on each input and, every time a run reaches the point of a query, checks that the value observed there
/// is one the fact allows. It sees only what the runs observe - never the providers' reasoning - so a fact
/// it refutes is unsound (PLY0302). A run that traps or runs out of its steps still counts up to there.
/// </summary>
public static class FactChecker {

  /// <summary>The most steps one run may take.</summary>
  public const long Steps = 1_000_000;

  /// <summary>Checks every query of a facade against runs of its function, which calls no other function.</summary>
  /// <param name="facts">The facade whose queries are checked.</param>
  /// <param name="inputs">The arguments of each run.</param>
  /// <returns>One PLY0302 per refuted query, in the order of the queries; empty when every fact held.</returns>
  public static IReadOnlyList<Diagnostic> Run(ValueFacts facts, IEnumerable<object[]> inputs) {
    ArgumentNullException.ThrowIfNull(facts);
    return Run(new IrModule("checked", [facts.Function]), facts, inputs);
  }

  /// <summary>Checks every query of a facade against runs of its function in a module.</summary>
  /// <param name="module">The module the function is a function of, without globals.</param>
  /// <param name="facts">The facade whose queries are checked.</param>
  /// <param name="inputs">The arguments of each run.</param>
  /// <returns>One PLY0302 per refuted query, in the order of the queries; empty when every fact held.</returns>
  public static IReadOnlyList<Diagnostic> Run(IrModule module, ValueFacts facts, IEnumerable<object[]> inputs) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(facts);
    ArgumentNullException.ThrowIfNull(inputs);
    var watcher = new Watcher(facts.Queries);
    foreach (var input in inputs)
      try {
        IrInterpreter.Call(module, facts.Function.Name, input, watcher, Steps);
      } catch (Exception exception) when (exception is IrException or IrBudgetException) {
        // What the run observed until it stopped is still checked.
      }

    return [.. watcher.Refuted.OrderBy(pair => pair.Key).Select(pair => Diagnostic.Create(AnalysisDiagnostics.Unsound, null,
      $"{pair.Value.Query.Domain} claims {pair.Value.Query.Fact} for {AnalysisDiagnostics.Describe(pair.Value.Query.Value)} at {facts.Layout.Describe(pair.Value.Query.Point)}, observed {Observed(pair.Value.Observed)}"))];
  }

  /// <summary>An observation in words: a number, or where a pointer points.</summary>
  private static string Observed(object value) => value is IrConst constant
    ? constant.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
    : (IrObservedPointer)value is { IsNull: true } ? "null" : $"offset {((IrObservedPointer)value).Offset} in {((IrObservedPointer)value).Bytes} bytes";

  /// <summary>Checks the queries of each point a run reaches.</summary>
  private sealed class Watcher : IIrObserver {

    private readonly Dictionary<ProgramPoint, List<(int Index, FactQuery Query)>> _byPoint = [];

    public Watcher(IReadOnlyList<FactQuery> queries) {
      for (var index = 0; index < queries.Count; ++index) {
        var query = queries[index];
        if (!this._byPoint.TryGetValue(query.Point, out var list))
          this._byPoint[query.Point] = list = [];

        list.Add((index, query));
      }
    }

    public SortedDictionary<int, (FactQuery Query, object Observed)> Refuted { get; } = [];

    public void Reached(IrBlock block, int index, IrObservation state) {
      if (!this._byPoint.TryGetValue(new ProgramPoint(block, index), out var queries))
        return;

      foreach (var (position, query) in queries)
        if (!this.Refuted.ContainsKey(position) && state.Value(query.Value) is { } observed && !query.Contains(observed))
          this.Refuted[position] = (query, observed);
    }

  }

}
