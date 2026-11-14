using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Polypiler.Prototype.Testing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary>
/// Which scenario is running: its feature file, its slugs and, for a row of a scenario outline, the
/// row's number. Reqnroll names the feature's folder and title and the row's values; the file and the
/// row are found by parsing the folder's feature files.
/// </summary>
public sealed class ScenarioIdentity {

  /// <summary>The feature files of the run by folder and title, each with its parsed cases.</summary>
  private static readonly ConcurrentDictionary<(string Folder, string Title), Lazy<(string Path, FeatureCase[] Cases)>> _features = new();

  private readonly Lazy<(string Path, FeatureCase Case)> _case;

  /// <summary>Identifies the scenario of a context.</summary>
  public ScenarioIdentity(FeatureContext feature, ScenarioContext scenario)
    : this(feature.FeatureInfo.FolderPath, feature.FeatureInfo.Title, scenario.ScenarioInfo.Title,
      [.. scenario.ScenarioInfo.Arguments.Values.Cast<object?>().Select(value => value?.ToString() ?? "")]) { }

  /// <summary>Identifies a scenario by what Reqnroll knows of it.</summary>
  /// <param name="folder">The feature's folder, relative to the step project, as Reqnroll gives it.</param>
  /// <param name="feature">The feature's title.</param>
  /// <param name="scenario">The scenario's title.</param>
  /// <param name="values">The values of the example row; empty for a scenario without examples.</param>
  internal ScenarioIdentity(string folder, string feature, string scenario, string[] values) {
    this.FeatureTitle = feature;
    this.ScenarioTitle = scenario;
    this._case = new(() => {
      var (path, cases) = _features.GetOrAdd((folder, feature), key => new(() => Locate(key.Folder, key.Title))).Value;
      var match = cases.FirstOrDefault(candidate => candidate.Title == scenario && candidate.Values.SequenceEqual(values))
        ?? throw new InvalidOperationException($"{path} has no scenario '{scenario}' with the values {string.Join(", ", values)}");
      return (path, match);
    });
  }

  /// <summary>The title of the feature.</summary>
  public string FeatureTitle { get; }

  /// <summary>The title of the scenario.</summary>
  public string ScenarioTitle { get; }

  /// <summary>The feature file, relative to the repository with <c>/</c> as separator.</summary>
  public string FeaturePath => this._case.Value.Path;

  /// <summary>The 1-based example row; 0 for a scenario without examples.</summary>
  public int Row => this._case.Value.Case.Row;

  /// <summary>Whether the scenario is the first, or only, test of its scenario: no outline row, or the first.</summary>
  public bool IsFirstOfItsScenario => this.Row <= 1;

  /// <summary>The slug of the feature's title.</summary>
  public string FeatureSlug => Slug.Of(this.FeatureTitle);

  /// <summary>The slug of the scenario's title, with the row number for a row of an outline.</summary>
  public string ScenarioSlug => this.Row == 0 ? Slug.Of(this.ScenarioTitle) : Slug.Of(this.ScenarioTitle, this.Row);

  private static (string Path, FeatureCase[] Cases) Locate(string folder, string title) {
    var root = PrototypeFixture.RepositoryRoot;
    var directory = Path.GetFullPath(Path.Combine(root, "Polypiler.Tests.Acceptance", folder.Replace('/', Path.DirectorySeparatorChar)));
    foreach (var file in Directory.GetFiles(directory, "*.feature").Order(StringComparer.Ordinal)) {
      var document = FeatureFiles.Parse(file);
      if (FeatureFiles.TitleOf(document) == title)
        return (Path.GetRelativePath(root, file).Replace('\\', '/'), [.. FeatureFiles.Cases(document)]);
    }

    throw new InvalidOperationException($"no feature '{title}' in {directory}");
  }

}
