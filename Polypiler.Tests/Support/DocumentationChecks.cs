using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Polypiler.Tests.Support;

/// <summary>
/// The checks of the documentation the unit tests and the bindings of the documentation features
/// share: the parts of the PRD and their chapters, the architecture guides and the relative links of
/// a Markdown text.
/// </summary>
internal static class DocumentationChecks {

  /// <summary>The repository.</summary>
  public static string Root { get; } = RepositoryRoot();

  /// <summary>The text of a file of the repository.</summary>
  public static string Read(string path) => File.ReadAllText(Path.Combine(Root, path));

  /// <summary>The parts of the PRD, in order.</summary>
  public static string[] Parts() => [.. Directory.GetFiles(Path.Combine(Root, "docs/prd"), "*.md").Order(StringComparer.Ordinal)];

  /// <summary>The architecture guides and the README, which draws the architecture too.</summary>
  public static IEnumerable<string> ArchitecturePaths()
    => Directory.GetFiles(Path.Combine(Root, "docs/architecture"), "*.md").Order(StringComparer.Ordinal).Append(Path.Combine(Root, "README.md"));

  /// <summary>The numbers of the chapters a part opens with <c>## n.</c>.</summary>
  public static IEnumerable<int> Chapters(string text)
    => Regex.Matches(text, @"(?m)^## (\d+)\. ").Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));

  /// <summary>Every chapter from 1 to 34 that is missing, repeated or out of that range.</summary>
  public static List<string> ChapterErrors(IEnumerable<int> chapters) {
    var values = chapters.ToArray();
    var errors = Enumerable.Range(1, 34).Except(values).Select(chapter => "missing chapter " + chapter).ToList();
    errors.AddRange(values.GroupBy(chapter => chapter).Where(group => group.Count() > 1)
      .Select(group => "duplicate chapter " + group.Key));
    errors.AddRange(values.Where(chapter => chapter < 1 || chapter > 34)
      .Select(chapter => "unexpected chapter " + chapter));
    return errors;
  }

  /// <summary>The relative links of a Markdown text whose target is neither a file nor a directory of the repository.</summary>
  public static List<string> LinkErrors(string source, string markdown) {
    var failures = new List<string>();
    foreach (Match match in Regex.Matches(markdown, @"\[[^\]\r\n]+\]\(([^)\s]+)\)")) {
      var target = match.Groups[1].Value;
      if (target.StartsWith('#') || Uri.TryCreate(target, UriKind.Absolute, out _))
        continue;
      var file = Uri.UnescapeDataString(target.Split('#')[0]);
      var path = Path.GetFullPath(Path.Combine(Root, Path.GetDirectoryName(source)!, file));
      if (!File.Exists(path) && !Directory.Exists(path))
        failures.Add(source + " -> " + target);
    }
    return failures;
  }

  /// <summary>The relative links of a Markdown text whose target, relative to the repository, does not exist as <paramref name="exists"/> says.</summary>
  public static List<string> MissingLinks(string source, string markdown, Func<string, bool> exists) {
    var missing = new List<string>();
    foreach (Match match in Regex.Matches(markdown, @"\[[^\]\r\n]+\]\(([^)\s]+)\)")) {
      var target = match.Groups[1].Value;
      if (target.StartsWith('#') || Uri.TryCreate(target, UriKind.Absolute, out _))
        continue;
      target = Uri.UnescapeDataString(target.Split('#')[0]);
      var absolute = Path.GetFullPath(Path.Combine(Root, Path.GetDirectoryName(source)!, target));
      var relative = Path.GetRelativePath(Root, absolute).Replace('\\', '/');
      if (!exists(relative))
        missing.Add(source + " -> " + relative);
    }
    return missing;
  }

  private static string RepositoryRoot([CallerFilePath] string source = "")
    => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

}
