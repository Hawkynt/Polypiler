using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Polypiler.Tooling.Cipher;

/// <summary>
/// The table of a run of <c>cipher kat</c> as Markdown (#1365): how many files each target
/// translated, built and passed on every checked vector, the reasons of the refusals by how many
/// files each blocks, and a row per file.
/// </summary>
public static partial class KatReport {

  /// <summary>How many refusal reasons per target the summary lists.</summary>
  public const int TopReasons = 15;

  /// <summary>
  /// A refusal as the summary groups it: without the names a message quotes, which differ from file to
  /// file. The construct a message starts with stays, and of a call in it only the method called.
  /// </summary>
  public static string Reason(string diagnostic) {
    var code = diagnostic.IndexOf(": ", StringComparison.Ordinal) is var colon and > 0 && diagnostic.StartsWith("PLY", StringComparison.Ordinal) ? colon + 2 : 0;
    var message = diagnostic[code..];
    var construct = message.StartsWith('\'') && message.IndexOf('\'', 1) is var close and > 0 ? close + 1 : 0;
    return diagnostic[..code] + Call().Replace(message[..construct], "a call of ….$1") + Quoted().Replace(message[construct..], "'…'");
  }

  /// <summary>The Markdown of a run.</summary>
  /// <param name="rows">The files, in the order of the table.</param>
  /// <param name="targets">The targets, in the order of the columns.</param>
  /// <param name="header">What stands above the summary: the title and how the table was made.</param>
  public static string Markdown(IReadOnlyList<KatRow> rows, IReadOnlyList<KatTarget> targets, string header) {
    var text = new StringBuilder(header.TrimEnd()).Append("\n\n## Summary\n\n");
    text.Append("| Target | Files | Translated | Built and run | Every checked KAT passes | KATs failing in translated files |\n");
    text.Append("| --- | ---: | ---: | ---: | ---: | ---: |\n");
    foreach (var target in targets) {
      var results = rows.Select(row => row.Targets.First(result => result.Target == target.Name)).ToList();
      text.Append($"| {target.Name} | {rows.Count} | {results.Count(result => result.Stage != KatStage.Refused)} | {results.Count(result => result.Stage == KatStage.Ran)} | ")
        .Append($"{results.Count(result => result.AllPassed)} | {results.Sum(result => result.Failures.Count)} |\n");
    }

    text.Append($"\n{rows.Sum(row => row.Vectors)} known-answer tests in {rows.Count} files, {rows.Sum(row => row.Checkable)} of them checkable on a translation ")
      .Append("(node passes it, every value is data and the result is bytes; without an expected result node's own, where it is the same on every run).\n");

    text.Append("\n## Refusal reasons\n\nThe first diagnostic of every refused file, the quoted names left out, by how many files it blocks.\n");
    foreach (var target in targets) {
      var reasons = rows.Select(row => row.Targets.First(result => result.Target == target.Name)).Where(result => result.Stage == KatStage.Refused)
        .GroupBy(result => Reason(result.Reason!)).OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).ToList();
      text.Append($"\n### {target.Name}\n\n");
      if (reasons.Count == 0) {
        text.Append("No file is refused.\n");
        continue;
      }

      text.Append("| Files | First diagnostic |\n| ---: | --- |\n");
      foreach (var reason in reasons.Take(TopReasons))
        text.Append($"| {reason.Count()} | {Cell(reason.Key)} |\n");
      if (reasons.Count > TopReasons)
        text.Append($"| {reasons.Skip(TopReasons).Sum(reason => reason.Count())} | {reasons.Count - TopReasons} other reasons |\n");
    }

    text.Append("\n## Files\n\n| File | KATs checkable / all |");
    foreach (var target in targets)
      text.Append($" {target.Name} |");
    text.Append("\n| --- | ---: |");
    foreach (var _ in targets)
      text.Append(" --- |");
    text.Append('\n');
    foreach (var row in rows) {
      text.Append($"| `{row.File}` | {row.Checkable} / {row.Vectors}{(row.Note is null ? "" : $" ({Cell(row.Note)})")} |");
      foreach (var result in row.Targets)
        text.Append($" {Cell(Status(result))} |");
      text.Append('\n');
    }

    return text.ToString();
  }

  /// <summary>What a cell of a target says about a file.</summary>
  public static string Status(KatTargetResult result) => result.Stage switch {
    KatStage.Refused => $"refused: {result.Reason}",
    KatStage.Translated => $"translated; {result.Reason}",
    KatStage.BuildFailed => $"does not build: {result.Reason}",
    _ => $"{result.Passed} / {result.Passed + result.Failures.Count} pass{(result.Failures.Count == 0 ? "" : $"; {result.Failures[0]}")}"
  };

  private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

  [GeneratedRegex("'[^' ]+'")]
  private static partial Regex Quoted();

  [GeneratedRegex(@"a call of \S+\.(\w+)")]
  private static partial Regex Call();

}
