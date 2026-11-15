using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Verification.Evidence;

/// <summary>How a scenario ended.</summary>
public enum ScenarioOutcome {

  /// <summary>Every step passed.</summary>
  Passed,

  /// <summary>A step failed, or a hook after the steps did.</summary>
  Failed

}

/// <summary>Which scenario an evidence bundle belongs to.</summary>
public sealed class ScenarioIdentity {

  /// <summary>Identifies a scenario.</summary>
  /// <param name="feature">The feature's title.</param>
  /// <param name="scenario">The scenario's title.</param>
  /// <param name="tags">The tags of the scenario and its feature, with or without the leading <c>@</c>.</param>
  /// <param name="row">The 1-based row of a scenario outline's examples; 0 for a scenario without examples.</param>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is negative.</exception>
  public ScenarioIdentity(string feature, string scenario, IEnumerable<string> tags, int row = 0) {
    ArgumentNullException.ThrowIfNull(feature);
    ArgumentNullException.ThrowIfNull(scenario);
    ArgumentNullException.ThrowIfNull(tags);
    ArgumentOutOfRangeException.ThrowIfNegative(row);
    this.Feature = feature;
    this.Scenario = scenario;
    this.Tags = [.. tags.Select(tag => tag.TrimStart('@')).Distinct(StringComparer.Ordinal)];
    this.Row = row;
  }

  /// <summary>The feature's title.</summary>
  public string Feature { get; }

  /// <summary>The scenario's title.</summary>
  public string Scenario { get; }

  /// <summary>The tags without the leading <c>@</c>, each once, in the order given.</summary>
  public IReadOnlyList<string> Tags { get; }

  /// <summary>The 1-based example row; 0 for a scenario without examples.</summary>
  public int Row { get; }

  /// <summary>The slug of the feature's title.</summary>
  public string FeatureSlug => Slug.From(this.Feature);

  /// <summary>The slug of the scenario's title, with <c>-&lt;row&gt;</c> for an example row.</summary>
  public string ScenarioSlug => this.Row == 0 ? Slug.From(this.Scenario) : $"{Slug.From(this.Scenario)}-{this.Row}";

}
