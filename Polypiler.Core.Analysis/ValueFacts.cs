using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis;

/// <summary>
/// The one place a pass asks for facts about the values of a function (#88): of any domain, at any point.
/// A missing fact is the domain's Unknown, never an error and never a proof - when no analysis of the
/// domain is registered, when it ran out of its budget (PLY0301) or when it gives up on the query. Only
/// <see cref="Require{TFact}(IrExpr, ProgramPoint)"/> insists on a provider (PLY0300). Every query is
/// remembered, so that <see cref="Checking.FactChecker"/> can hold each answer against real executions.
/// The facade caches nothing and never changes the IR.
/// </summary>
public sealed class ValueFacts {

  private readonly Dictionary<Type, Registration> _providers = [];
  private readonly List<FactQuery> _queries = [];
  private readonly List<Diagnostic> _diagnostics = [];
  private readonly HashSet<string> _exhausted = new(StringComparer.Ordinal);

  /// <summary>Creates the facade of a function, without providers.</summary>
  /// <param name="function">The function.</param>
  /// <exception cref="ArgumentNullException"><paramref name="function"/> is <see langword="null"/>.</exception>
  public ValueFacts(IrFunction function) => this.Layout = new FunctionLayout(function);

  /// <summary>The function the facts are about.</summary>
  public IrFunction Function => this.Layout.Function;

  /// <summary>The layout of the function: its blocks and which variables each point may ask about.</summary>
  public FunctionLayout Layout { get; }

  /// <summary>Every query answered so far, in order.</summary>
  public IReadOnlyList<FactQuery> Queries => this._queries;

  /// <summary>The diagnostics of the queries: PLY0301 once per domain that ran out of its budget.</summary>
  public IReadOnlyList<Diagnostic> Diagnostics => this._diagnostics;

  /// <summary>Registers the provider of a domain.</summary>
  /// <typeparam name="TFact">The fact type of the domain.</typeparam>
  /// <param name="provider">The provider.</param>
  /// <returns>The facade, for chaining.</returns>
  /// <exception cref="InvalidOperationException">The domain has a provider already.</exception>
  public ValueFacts Add<TFact>(IFactProvider<TFact> provider) where TFact : IFact<TFact> {
    ArgumentNullException.ThrowIfNull(provider);
    if (!this._providers.TryAdd(typeof(TFact), new Registration(TFact.Domain.Name, provider, (facts, value, point) => TFact.Domain.Describe(facts.Get<TFact>(value, point)))))
      throw new InvalidOperationException($"The domain '{TFact.Domain.Name}' has a provider already.");

    return this;
  }

  /// <summary>Whether a provider of a domain is registered.</summary>
  /// <typeparam name="TFact">The fact type of the domain.</typeparam>
  /// <returns><see langword="true"/> when one is.</returns>
  public bool Has<TFact>() where TFact : IFact<TFact> => this._providers.ContainsKey(typeof(TFact));

  /// <summary>The fact of a value at a point; the domain's Unknown when no provider can answer.</summary>
  /// <typeparam name="TFact">The fact type of the domain.</typeparam>
  /// <param name="value">The value: an expression over the variables of the function.</param>
  /// <param name="point">The point, where every variable the value reads is defined.</param>
  /// <returns>The fact.</returns>
  /// <exception cref="ArgumentException">The point lies outside the function or before a variable the value reads is defined.</exception>
  public TFact Get<TFact>(IrExpr value, ProgramPoint point) where TFact : IFact<TFact> {
    this.Layout.Check(value, point);
    var domain = TFact.Domain;
    var fact = this._providers.TryGetValue(typeof(TFact), out var registration) ? this.Ask(domain, (IFactProvider<TFact>)registration.Provider, value, point) : domain.Unknown;
    this._queries.Add(new FactQuery(domain.Name, value, point, domain.Describe(fact), observed => domain.Contains(fact, observed)));
    return fact;
  }

  /// <summary>The fact of a variable at a point; <see cref="Get{TFact}(IrExpr, ProgramPoint)"/> of the variable.</summary>
  /// <typeparam name="TFact">The fact type of the domain.</typeparam>
  /// <param name="local">The variable.</param>
  /// <param name="point">The point.</param>
  /// <returns>The fact.</returns>
  public TFact Get<TFact>(IrLocal local, ProgramPoint point) where TFact : IFact<TFact> => this.Get<TFact>(new IrLocalRef(local), point);

  /// <summary>The fact of a value at a point from a provider that has to be registered.</summary>
  /// <typeparam name="TFact">The fact type of the domain.</typeparam>
  /// <param name="value">The value.</param>
  /// <param name="point">The point.</param>
  /// <returns>The fact.</returns>
  /// <exception cref="FactException">PLY0300: no provider of the domain is registered.</exception>
  public TFact Require<TFact>(IrExpr value, ProgramPoint point) where TFact : IFact<TFact> {
    if (!this.Has<TFact>())
      throw new FactException(Diagnostic.Create(AnalysisDiagnostics.MissingDomain, null, TFact.Domain.Name));

    return this.Get<TFact>(value, point);
  }

  /// <summary>
  /// Whether a comparison holds at a point: asks every registered provider that decides comparisons, in
  /// the order of their domain names. <see cref="Truth.Unknown"/> unless one decides.
  /// </summary>
  /// <param name="comparison">An expression of type <see cref="IrType.Bool"/>.</param>
  /// <param name="point">The point.</param>
  /// <returns>The answer.</returns>
  /// <exception cref="FactException">PLY0302: two providers decide it differently.</exception>
  public Truth Decide(IrExpr comparison, ProgramPoint point) {
    this.Layout.Check(comparison, point);
    var decided = Truth.Unknown;
    string? decider = null;
    foreach (var registration in this.Ordered()) {
      if (registration.Provider is not IComparisonDecider provider)
        continue;

      var truth = this.Guarded(registration.Domain, () => provider.Decide(comparison, point), Truth.Unknown);
      if (truth == Truth.Unknown)
        continue;

      if (decider is not null && truth != decided)
        throw new FactException(Diagnostic.Create(AnalysisDiagnostics.Unsound, null,
          $"{decider} decides {decided} and {registration.Domain} decides {truth} for {AnalysisDiagnostics.Describe(comparison)} at {this.Layout.Describe(point)}"));

      (decided, decider) = (truth, registration.Domain);
    }

    return decided;
  }

  /// <summary>The facts of a value at a point in words: one line per registered domain, in the order of the domain names.</summary>
  /// <param name="value">The value.</param>
  /// <param name="point">The point.</param>
  /// <returns>The lines, such as <c>constant: constant 25</c>.</returns>
  public string Describe(IrExpr value, ProgramPoint point) {
    this.Layout.Check(value, point);
    return string.Join('\n', this.Ordered().Select(registration => $"{registration.Domain}: {registration.Describe(this, value, point)}"));
  }

  /// <summary>Why a value has its fact at a point; an explanation without statements when no provider is registered.</summary>
  /// <typeparam name="TFact">The fact type of the domain.</typeparam>
  /// <param name="value">The value.</param>
  /// <param name="point">The point.</param>
  /// <returns>The explanation.</returns>
  public FactExplanation Explain<TFact>(IrExpr value, ProgramPoint point) where TFact : IFact<TFact> {
    this.Layout.Check(value, point);
    var empty = new FactExplanation(TFact.Domain.Name, [], false);
    return this._providers.TryGetValue(typeof(TFact), out var registration)
      ? this.Guarded(TFact.Domain.Name, () => ((IFactProvider<TFact>)registration.Provider).Explain(value, point), empty)
      : empty;
  }

  private IEnumerable<Registration> Ordered() => this._providers.Values.OrderBy(registration => registration.Domain, StringComparer.Ordinal);

  private TFact Ask<TFact>(IFactDomain<TFact> domain, IFactProvider<TFact> provider, IrExpr value, ProgramPoint point)
    => this.Guarded(domain.Name, () => provider.At(value, point), domain.Unknown);

  /// <summary>What a provider answers, or the fallback when it gives up; a provider out of its budget is reported once.</summary>
  private T Guarded<T>(string domain, Func<T> ask, T fallback) {
    try {
      return ask();
    } catch (FactBudgetException budget) {
      if (this._exhausted.Add(domain))
        this._diagnostics.Add(Diagnostic.Create(AnalysisDiagnostics.Budget, null, budget.Domain, budget.Function));

      return fallback;
    } catch (FactUnavailableException) {
      return fallback;
    }
  }

  /// <summary>A registered provider with the name of its domain and a way to describe its fact without knowing its type.</summary>
  private sealed record Registration(string Domain, object Provider, Func<ValueFacts, IrExpr, ProgramPoint, string> Describe);

}

/// <summary>A query the facade answered: the domain, the value, the point and the fact, with the test a run's observation has to pass.</summary>
/// <param name="Domain">The name of the domain.</param>
/// <param name="Value">The value asked about.</param>
/// <param name="Point">The point.</param>
/// <param name="Fact">The fact in words.</param>
/// <param name="Contains">Whether an observed value is one the fact allows (<see cref="IFactDomain{TFact}.Contains"/>).</param>
public sealed record FactQuery(string Domain, IrExpr Value, ProgramPoint Point, string Fact, Func<object, bool> Contains);
