using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gherkin;
using Gherkin.Ast;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary>Whether a step sets up, acts or checks; <see cref="Any"/> for a binding that serves all three.</summary>
public enum StepKind {

  /// <summary>A <c>Given</c> step.</summary>
  Given,

  /// <summary>A <c>When</c> step.</summary>
  When,

  /// <summary>A <c>Then</c> step.</summary>
  Then,

  /// <summary>A binding for any kind of step.</summary>
  Any

}

/// <summary>A step as it runs: its kind, after <c>And</c> and <c>But</c> took the kind before them, and its text with the example values filled in.</summary>
/// <param name="Kind">The kind of the step.</param>
/// <param name="Keyword">The keyword as written, without the space after it.</param>
/// <param name="Text">The text after the keyword.</param>
/// <param name="Line">The 1-based line of the step in its file.</param>
public sealed record FeatureStep(StepKind Kind, string Keyword, string Text, int Line);

/// <summary>One test a feature file stands for: a scenario, or one example row of a scenario outline.</summary>
/// <param name="Title">The title of the scenario.</param>
/// <param name="Row">The 1-based example row; 0 for a scenario without examples.</param>
/// <param name="Values">The values of the example row in the order of its columns; empty for a scenario without examples.</param>
/// <param name="Steps">The steps of the background and of the scenario, in order.</param>
public sealed record FeatureCase(string Title, int Row, IReadOnlyList<string> Values, IReadOnlyList<FeatureStep> Steps);

/// <summary>Reads feature files with the Gherkin reference parser.</summary>
public static class FeatureFiles {

  /// <summary>Every <c>.feature</c> file under a folder of the repository, relative to the repository and with <c>/</c> as separator, in ordinal order.</summary>
  public static IReadOnlyList<string> Under(string root, string folder)
    => [.. Directory.GetFiles(Path.Combine(root, folder), "*.feature", SearchOption.AllDirectories)
      .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
      .Order(StringComparer.Ordinal)];

  /// <summary>Parses a feature file.</summary>
  /// <exception cref="CompositeParserException">The file is not Gherkin.</exception>
  public static GherkinDocument Parse(string path) {
    using var reader = new StreamReader(path);
    return new Parser().Parse(reader);
  }

  /// <summary>The title of the feature of a document; empty for a document without one.</summary>
  public static string TitleOf(GherkinDocument document) => document.Feature?.Name ?? "";

  /// <summary>The tests a feature document stands for: each scenario, and each example row of an outline, with its steps.</summary>
  public static IReadOnlyList<FeatureCase> Cases(GherkinDocument document) {
    var cases = new List<FeatureCase>();
    if (document.Feature is { } feature)
      Collect(feature.Children, [], cases);

    return cases;
  }

  private static void Collect(IEnumerable<IHasLocation> children, IReadOnlyList<Step> inherited, List<FeatureCase> cases) {
    var background = inherited;
    foreach (var child in children)
      switch (child) {
        case Background block:
          background = [.. inherited, .. block.Steps];
          break;
        case Rule rule:
          Collect(rule.Children, background, cases);
          break;
        case Scenario scenario:
          var steps = background.Concat(scenario.Steps).ToList();
          var rows = scenario.Examples.SelectMany(examples => examples.TableBody.Select(row => (examples.TableHeader, row))).ToList();
          if (!scenario.Examples.Any()) {
            cases.Add(new FeatureCase(scenario.Name, 0, [], Expand(steps, [], [])));
            break;
          }

          for (var index = 0; index < rows.Count; ++index) {
            var (header, row) = rows[index];
            var names = header?.Cells.Select(cell => cell.Value).ToList() ?? [];
            var values = row.Cells.Select(cell => cell.Value).ToList();
            cases.Add(new FeatureCase(scenario.Name, index + 1, values, Expand(steps, names, values)));
          }

          break;
      }
  }

  /// <summary>The steps with their kinds resolved and the placeholders of the example row replaced.</summary>
  private static List<FeatureStep> Expand(IEnumerable<Step> steps, IReadOnlyList<string> names, IReadOnlyList<string> values) {
    var result = new List<FeatureStep>();
    var kind = StepKind.Given;
    foreach (var step in steps) {
      kind = step.KeywordType switch {
        StepKeywordType.Context => StepKind.Given,
        StepKeywordType.Action => StepKind.When,
        StepKeywordType.Outcome => StepKind.Then,
        _ => kind
      };
      var text = step.Text;
      for (var index = 0; index < names.Count && index < values.Count; ++index)
        text = text.Replace("<" + names[index] + ">", values[index], StringComparison.Ordinal);

      result.Add(new FeatureStep(kind, step.Keyword.Trim(), text, (int)step.Location.Line));
    }

    return result;
  }

}
