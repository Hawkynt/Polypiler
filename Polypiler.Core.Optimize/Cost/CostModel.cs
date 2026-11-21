using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Polypiler.Core.Optimize.Cost;

/// <summary>
/// The cost model of one target profile: it prices the six decision families — operation form, loop
/// form, data path, storage, evaluation, placement — from the profile's numbers and chooses the
/// cheapest alternative. It holds no target knowledge of its own; every number comes from the profile.
/// </summary>
public sealed class CostModel : ICostModel {

  /// <summary>The note of an alternative priced by the profile's <c>default</c>.</summary>
  public const string DefaultCostNote = "default cost";

  /// <summary>The forms that need nothing of the target and are therefore always available.</summary>
  public static IReadOnlyList<string> UniversalForms { get; } = ["scalar", "cpu", "loop", "unrolled", "unpacked", "packed", "eager", "lazy"];

  private readonly HashSet<string> _available;

  /// <summary>Creates the model of a profile.</summary>
  /// <param name="profile">The profile.</param>
  /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
  public CostModel(TargetCostProfile profile) {
    ArgumentNullException.ThrowIfNull(profile);
    this.Profile = profile;
    this._available = new(UniversalForms, StringComparer.Ordinal);
    foreach (var forms in profile.Operations.Values) {
      if (forms.Native is not null)
        this._available.Add("native");
      if (forms.Sequence is not null)
        this._available.Add("sequence");
      if (forms.Helper is not null)
        this._available.Add("helper");
    }
    if (profile.Swar is { } swar)
      this._available.UnionWith(["swar", Swar(swar)]);
    foreach (var simd in profile.Simd)
      this._available.UnionWith(["simd", Simd(simd)]);
    if (profile.Threads is { } threads)
      this._available.UnionWith(["threads", Threads(threads)]);
    if (profile.Gpu is not null)
      this._available.Add("gpu");
    if (profile.Npu is not null)
      this._available.Add("npu");
  }

  /// <summary>The profile the model prices with.</summary>
  public TargetCostProfile Profile { get; }

  /// <summary>The model of a target profile's <c>cost.yaml</c>.</summary>
  /// <param name="specRoot">The spec root, <c>spec</c> in a checkout.</param>
  /// <param name="target">The target profile, <c>&lt;language&gt;/&lt;profile&gt;</c>.</param>
  /// <returns>The model.</returns>
  /// <exception cref="CostProfileException"><c>PLY0355</c>: the target profile has no <c>cost.yaml</c>; <c>PLY0356</c>: it is malformed.</exception>
  public static CostModel ForTarget(string specRoot, string target) => new(TargetCostProfile.ForTarget(specRoot, target));

  private static string Swar(SwarForm swar) => "swar-" + swar.RegisterBits.ToString(CultureInfo.InvariantCulture);

  private static string Simd(SimdForm simd) => "simd-" + simd.Width.ToString(CultureInfo.InvariantCulture);

  private static string Threads(ThreadsForm threads) => "threads-" + threads.Count.ToString(CultureInfo.InvariantCulture);

  /// <inheritdoc/>
  public bool IsAvailable(string form) => form is not null && this._available.Contains(form);

  /// <inheritdoc/>
  public Cost Of(OperationKey op) => this.Profile.Operations.TryGetValue(op, out var forms)
    ? forms.Native ?? forms.Sequence ?? CostFormulas.Helper(forms.Helper!.Call, forms.Helper.Body, 1)
    : this.Profile.Default;

  /// <summary>
  /// The forms of an operation at a number of call sites, in the order native, sequence, helper, each the
  /// profile lists: native and sequence cost every site in full, a helper <see cref="CostFormulas.Helper"/>.
  /// An operation the profile does not list has the one alternative <c>default</c>, noted <see cref="DefaultCostNote"/>.
  /// </summary>
  /// <param name="op">The operation.</param>
  /// <param name="callSites">The number of call sites, not negative.</param>
  /// <returns>The alternatives.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="callSites"/> is negative.</exception>
  public IReadOnlyList<Alternative> OperationForms(OperationKey op, long callSites) {
    ArgumentOutOfRangeException.ThrowIfNegative(callSites);
    if (!this.Profile.Operations.TryGetValue(op, out var forms))
      return [new("default", this.Profile.Default * callSites, DefaultCostNote)];

    var result = new List<Alternative>();
    if (forms.Native is { } native)
      result.Add(new("native", native * callSites));
    if (forms.Sequence is { } sequence)
      result.Add(new("sequence", sequence * callSites));
    if (forms.Helper is { } helper)
      result.Add(new("helper", CostFormulas.Helper(helper.Call, helper.Body, callSites)));
    return result;
  }

  /// <summary>
  /// The loop forms of a loop whose body costs <paramref name="body"/>: <c>loop</c> (<see cref="CostFormulas.Loop"/>
  /// with the profile's <c>loop.overhead</c>) and <c>unrolled</c> (<see cref="CostFormulas.Unrolled"/>).
  /// </summary>
  /// <param name="body">The cost of one iteration's body.</param>
  /// <param name="trip">What is known of the trip count.</param>
  /// <returns>The alternatives.</returns>
  public IReadOnlyList<Alternative> LoopForms(Cost body, CountFact trip) {
    var count = trip.Resolve(this.Profile.UnknownTripCount);
    return [new("loop", CostFormulas.Loop(body, this.Profile.LoopOverhead, count)), new("unrolled", CostFormulas.Unrolled(body, count))];
  }

  /// <summary>
  /// The data paths of an element-wise operation over <paramref name="n"/> elements: <c>scalar</c> (each
  /// element at <see cref="Of"/>), then <c>swar-&lt;bits&gt;</c> and each <c>simd-&lt;width&gt;</c> the profile lists
  /// whose register holds at least one element (<see cref="CostFormulas.Blocked"/>).
  /// </summary>
  /// <param name="op">The operation on one element.</param>
  /// <param name="n">What is known of the element count.</param>
  /// <returns>The alternatives.</returns>
  public IReadOnlyList<Alternative> DataPaths(OperationKey op, CountFact n) {
    var count = n.Resolve(this.Profile.UnknownTripCount);
    var scalar = this.Of(op);
    var note = this.Profile.Operations.ContainsKey(op) ? null : DefaultCostNote;
    var bits = op.ElementBits;
    var result = new List<Alternative> { new("scalar", scalar * count, note) };
    if (bits > 0 && this.Profile.Swar is { } swar && swar.RegisterBits >= bits)
      result.Add(new(Swar(swar), CostFormulas.Blocked(swar.Setup, swar.PerBlock, scalar, count, swar.RegisterBits / bits), note));
    foreach (var simd in this.Profile.Simd.Where(simd => bits > 0 && simd.Width >= bits))
      result.Add(new(Simd(simd), CostFormulas.Blocked(simd.Setup, simd.PerBlock, scalar, count, simd.Width / bits), note));
    return result;
  }

  /// <summary>
  /// The placements of <paramref name="work"/> units of work: <c>cpu</c> (one unit of latency each), then
  /// <c>threads-&lt;count&gt;</c> (<see cref="CostFormulas.Threaded"/>), <c>gpu</c> and <c>npu</c>
  /// (<see cref="CostFormulas.Offloaded"/>), each where the profile has it.
  /// </summary>
  /// <param name="work">What is known of the units of work.</param>
  /// <returns>The alternatives.</returns>
  public IReadOnlyList<Alternative> Placements(CountFact work) {
    var count = work.Resolve(this.Profile.UnknownTripCount);
    var result = new List<Alternative> { new("cpu", new(count, 0)) };
    if (this.Profile.Threads is { } threads)
      result.Add(new(Threads(threads), CostFormulas.Threaded(threads.Spawn, count, threads.Count)));
    if (this.Profile.Gpu is { } gpu)
      result.Add(new("gpu", CostFormulas.Offloaded(gpu.Transfer, gpu.PerElement, count)));
    if (this.Profile.Npu is { } npu)
      result.Add(new("npu", CostFormulas.Offloaded(npu.Transfer, npu.PerElement, count)));
    return result;
  }

  /// <inheritdoc/>
  /// <exception cref="ArgumentNullException"><paramref name="alternatives"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException"><paramref name="alternatives"/> is empty.</exception>
  public Decision Choose(IReadOnlyList<Alternative> alternatives, Objective objective) => Rank(alternatives, objective);

  /// <summary>
  /// Chooses among alternatives by the three-step rule of <see cref="ICostModel.Choose"/>; it needs no
  /// profile, since the alternatives carry their costs.
  /// </summary>
  /// <param name="alternatives">The alternatives, at least one.</param>
  /// <param name="objective">What to minimise.</param>
  /// <returns>The decision.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="alternatives"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException"><paramref name="alternatives"/> is empty.</exception>
  public static Decision Rank(IReadOnlyList<Alternative> alternatives, Objective objective) {
    ArgumentNullException.ThrowIfNull(alternatives);
    if (alternatives.Count == 0)
      throw new ArgumentException("there is nothing to choose from", nameof(alternatives));

    var scores = alternatives.Select(alternative => alternative.Cost.Score(objective)).ToArray();
    var balanced = alternatives.Select(alternative => alternative.Cost.Score(Objective.Balanced)).ToArray();
    var best = 0;
    for (var i = 1; i < scores.Length; ++i)
      if (scores[i] < scores[best] || (scores[i] == scores[best] && balanced[i] < balanced[best]))
        best = i;

    var tied = Enumerable.Range(0, scores.Length).Where(i => i != best && scores[i] == scores[best]).ToList();
    var reason = tied.Count == 0
      ? $"lowest {objective} score"
      : tied.All(i => balanced[i] > balanced[best])
        ? $"tied under {objective}, lower Balanced score {balanced[best].ToString(CultureInfo.InvariantCulture)}"
        : $"tied under {objective} and Balanced, listed first";
    if (alternatives[best].Note is { } note)
      reason += "; " + note;
    return new(best, alternatives[best].Form, scores[best], reason, Detail(alternatives, scores, best));
  }

  /// <summary>
  /// The detail an opt-report entry carries for a decision: <c>cost: &lt;chosen&gt; &lt;score&gt;</c>, followed by
  /// <c> vs &lt;rejected&gt; &lt;score&gt;</c> for the rejected alternatives in their order, separated by commas.
  /// </summary>
  /// <param name="alternatives">The alternatives the decision chose from.</param>
  /// <param name="decision">The decision.</param>
  /// <param name="objective">The objective the decision was made under.</param>
  /// <returns>The detail.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="alternatives"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The decision's index is not one of the alternatives.</exception>
  public static string FormatDetail(IReadOnlyList<Alternative> alternatives, Decision decision, Objective objective) {
    ArgumentNullException.ThrowIfNull(alternatives);
    ArgumentOutOfRangeException.ThrowIfNegative(decision.Index);
    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(decision.Index, alternatives.Count);
    return Detail(alternatives, [.. alternatives.Select(alternative => alternative.Cost.Score(objective))], decision.Index);
  }

  private static string Detail(IReadOnlyList<Alternative> alternatives, long[] scores, int chosen) {
    string Entry(int i) => alternatives[i].Form + " " + scores[i].ToString(CultureInfo.InvariantCulture);
    var rejected = Enumerable.Range(0, alternatives.Count).Where(i => i != chosen).Select(Entry).ToList();
    return "cost: " + Entry(chosen) + (rejected.Count == 0 ? "" : " vs " + string.Join(", ", rejected));
  }

}
