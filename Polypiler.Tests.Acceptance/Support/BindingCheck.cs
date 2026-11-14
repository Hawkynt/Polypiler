using System;
using System.Collections.Generic;
using Polypiler.Core;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Gherkin;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary>A step definition: the kind of step it binds, its regular expression and the method that runs it.</summary>
/// <param name="Kind">The kind of step the binding is for.</param>
/// <param name="Pattern">The regular expression; one without <c>^</c> and <c>$</c> must still match the whole text.</param>
/// <param name="Method">The binding method, as <c>Class.Method</c>.</param>
/// <param name="Feature">The title of the only feature the binding is scoped to; <see langword="null"/> for every feature.</param>
public sealed record StepBinding(StepKind Kind, string Pattern, string Method, string? Feature = null) {

  /// <summary>Whether the binding runs a step of a feature.</summary>
  public bool Matches(string feature, FeatureStep step)
    => (this.Kind == StepKind.Any || this.Kind == step.Kind)
       && (this.Feature is null || this.Feature == feature)
       && Regex.IsMatch(step.Text, Anchored(this.Pattern), RegexOptions.CultureInvariant);

  private static string Anchored(string pattern) {
    var start = pattern.StartsWith('^') ? pattern : "^" + pattern;
    return start.EndsWith('$') ? start : start + "$";
  }

}

/// <summary>
/// The static check of the feature files (PLY0074): every step has exactly one binding, every feature
/// has a scenario, and every feature file is named <c>tests/features/&lt;area&gt;/&lt;id&gt;.feature</c>
/// after the issue it belongs to.
/// </summary>
public static partial class BindingCheck {

  /// <summary>The code of every problem the check reports.</summary>
  public const string Code = "PLY0074";

  /// <summary>The registered diagnostic of <see cref="Code"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Descriptor = new(Code, DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "bdd.binding");

  /// <summary>
  /// The feature files written before the naming rule, for the prototype that predates the backlog.
  /// They are bound and checked like any other; only their names are left as they are. The list only
  /// ever shrinks.
  /// </summary>
  public static IReadOnlySet<string> LegacyPaths { get; } = new HashSet<string>(StringComparer.Ordinal) {
    "tests/features/infrastructure/documentation-navigation.feature",
    "tests/features/infrastructure/issue-readiness.feature",
    "tests/features/prototype/csharp-profiles.feature",
    "tests/features/prototype/csharp-to-javascript-wide.feature",
    "tests/features/prototype/csharp-to-javascript.feature",
    "tests/features/prototype/float-operations.feature",
    "tests/features/prototype/float-view.feature",
    "tests/features/prototype/integer-operations.feature",
    "tests/features/prototype/ir-to-csharp-wide.feature",
    "tests/features/prototype/ir-to-csharp.feature",
    "tests/features/prototype/javascript-inference.feature",
    "tests/features/prototype/javascript-profiles.feature",
    "tests/features/prototype/javascript-to-ir.feature",
    "tests/features/prototype/jit-shift-xor.feature",
    "tests/features/prototype/recognition.feature",
    "tests/features/prototype/value-records.feature",
    "tests/features/prototype/spans-and-memory.feature"
  };

  [GeneratedRegex(@"^\| `(?<folder>[a-z0-9]+(?:/<code>)?)/` \| (?<prefixes>[^|]+)\|", RegexOptions.Multiline)]
  private static partial Regex AreaRow();

  [GeneratedRegex("`([^`]+)`")]
  private static partial Regex Quoted();

  [GeneratedRegex(@"^tests/features/(?<area>.+)/(?<id>[^/]+)\.feature$")]
  private static partial Regex FeaturePath();

  [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
  private static partial Regex IssueId();

  [GeneratedRegex("^lang/(?<code>[a-z]+)$")]
  private static partial Regex LanguageArea();

  /// <summary>
  /// The areas of the backlog with the ID prefixes each allocates, read from the table "Areas, ID
  /// prefixes and anchor issues" of <c>backlog/README.md</c>: a folder such as <c>infra</c>, or
  /// <c>lang/&lt;code&gt;</c> for every language, and prefixes such as <c>infra-</c> or <c>fe-&lt;c&gt;</c>.
  /// </summary>
  public static IReadOnlyDictionary<string, IReadOnlyList<string>> Areas(string root) {
    var text = File.ReadAllText(Path.Combine(root, "backlog", "README.md"));
    return AreaRow().Matches(text).ToDictionary(
      match => match.Groups["folder"].Value,
      match => (IReadOnlyList<string>)[.. Quoted().Matches(match.Groups["prefixes"].Value).Select(prefix => prefix.Groups[1].Value.TrimEnd('…').ToLowerInvariant())],
      StringComparer.Ordinal);
  }

  /// <summary>Why a feature path breaks the naming rule, or <see langword="null"/> when it follows it.</summary>
  /// <param name="path">The path relative to the repository, with <c>/</c> as separator.</param>
  /// <param name="areas">The areas of <see cref="Areas"/>.</param>
  public static string? PathProblem(string path, IReadOnlyDictionary<string, IReadOnlyList<string>> areas) {
    if (LegacyPaths.Contains(path))
      return null;

    var problem = $"{Code}: {path}: path does not follow tests/features/<area>/<id>.feature";
    if (FeaturePath().Match(path) is not { Success: true } match || !IssueId().IsMatch(match.Groups["id"].Value))
      return problem;

    var area = match.Groups["area"].Value;
    var id = match.Groups["id"].Value;
    IReadOnlyList<string>? prefixes;
    var code = "";
    if (LanguageArea().Match(area) is { Success: true } language) {
      code = language.Groups["code"].Value;
      areas.TryGetValue("lang/<code>", out prefixes);
    } else {
      areas.TryGetValue(area, out prefixes);
    }

    return prefixes is not null && prefixes.Any(prefix => id.StartsWith(prefix.Replace("<c>", code, StringComparison.Ordinal), StringComparison.Ordinal)) ? null : problem;
  }

  /// <summary>
  /// Checks feature files against a set of bindings and returns one line per problem: a step that
  /// matches no binding or more than one, a feature without a scenario, a file that is not Gherkin and,
  /// where <paramref name="areas"/> is given, a path that breaks the naming rule.
  /// </summary>
  /// <param name="root">The repository the paths are relative to.</param>
  /// <param name="paths">The feature files, relative to the repository with <c>/</c> as separator.</param>
  /// <param name="bindings">Every binding of the step assembly.</param>
  /// <param name="areas">The areas of <see cref="Areas"/>; <see langword="null"/> leaves the names unchecked.</param>
  public static IReadOnlyList<string> Check(
    string root, IEnumerable<string> paths, IReadOnlyCollection<StepBinding> bindings, IReadOnlyDictionary<string, IReadOnlyList<string>>? areas = null) {
    var problems = new List<string>();
    foreach (var path in paths) {
      if (areas is not null && PathProblem(path, areas) is { } naming)
        problems.Add(naming);

      Gherkin.Ast.GherkinDocument document;
      try {
        document = FeatureFiles.Parse(Path.Combine(root, path));
      } catch (CompositeParserException exception) {
        problems.Add($"{Code}: {path}: {exception.Errors.First().Message}");
        continue;
      }

      var feature = FeatureFiles.TitleOf(document);
      var cases = FeatureFiles.Cases(document);
      if (cases.Count == 0) {
        problems.Add($"{Code}: {path}: feature has no scenario");
        continue;
      }

      foreach (var step in cases.SelectMany(scenario => scenario.Steps)) {
        var matching = bindings.Where(binding => binding.Matches(feature, step)).ToList();
        var line = $"{Code}: {path}({step.Line}): step '{step.Keyword} {step.Text}'";
        var problem = matching.Count switch {
          0 => line + " has no binding",
          1 => null,
          _ => $"{line} has {matching.Count} bindings: {string.Join(", ", matching.Select(binding => binding.Method))}"
        };
        if (problem is not null && !problems.Contains(problem))
          problems.Add(problem);
      }
    }

    return problems;
  }

  /// <summary>The number of tests the feature files stand for: each scenario, and each example row of an outline.</summary>
  public static int CountScenarios(string root, IEnumerable<string> paths)
    => paths.Sum(path => FeatureFiles.Cases(FeatureFiles.Parse(Path.Combine(root, path))).Count);

}
