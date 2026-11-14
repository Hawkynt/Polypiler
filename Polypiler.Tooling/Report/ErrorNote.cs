using System;
using System.Collections.Generic;
using System.Text;

namespace Polypiler.Tooling.Report;

/// <summary>
/// The hand-written explanation of one diagnostic code, <c>docs/errors/notes/PLY####.md</c>: Markdown
/// with any of the sections <c>## Cause</c>, <c>## Example</c> and <c>## What to do</c>, each merged into
/// the generated page of its code under the same heading.
/// </summary>
/// <param name="Cause">The text of <c>## Cause</c>, or <see langword="null"/> when the note has none.</param>
/// <param name="Example">The text of <c>## Example</c>, or <see langword="null"/> when the note has none.</param>
/// <param name="WhatToDo">The text of <c>## What to do</c>, or <see langword="null"/> when the note has none.</param>
public sealed record ErrorNote(string? Cause, string? Example, string? WhatToDo) {

  /// <summary>The heading of the section that says why the diagnostic is reported.</summary>
  public const string CauseHeading = "## Cause";

  /// <summary>The heading of the section that shows an input that is refused.</summary>
  public const string ExampleHeading = "## Example";

  /// <summary>The heading of the section that says what to do about the diagnostic.</summary>
  public const string WhatToDoHeading = "## What to do";

  /// <summary>A note without any section; every section of its page reads the default sentence.</summary>
  public static ErrorNote Empty { get; } = new(null, null, null);

  /// <summary>
  /// Reads a note. A line that starts with <c>#</c> outside a fenced code block is a heading and must be
  /// one of the three sections, each at most once; text before the first heading is refused, text under an
  /// unknown heading belongs to no section, blank lines
  /// around a section's text are dropped and line breaks become line feeds.
  /// </summary>
  /// <param name="text">The text of the note.</param>
  /// <param name="path">The note's path as the problems name it.</param>
  /// <param name="problems">Receives one message per problem, without the code.</param>
  /// <returns>The note; with problems, what could be read of it.</returns>
  public static ErrorNote Parse(string text, string path, ICollection<string> problems) {
    var sections = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
    StringBuilder? current = null;
    string? fence = null;
    var (headed, strayText) = (false, false);
    foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) {
      var trimmed = line.TrimStart();
      if (fence is null && trimmed.StartsWith('#')) {
        var heading = trimmed.TrimEnd();
        headed = true;
        if (heading is not (CauseHeading or ExampleHeading or WhatToDoHeading)) {
          problems.Add($"note '{path}' has an unknown section '{heading}'");
          current = null;
          continue;
        }

        if (sections.ContainsKey(heading))
          problems.Add($"note '{path}' has the section '{heading}' twice");
        current = sections[heading] = new StringBuilder();
        continue;
      }

      if (fence is null)
        fence = Fence(trimmed);
      else if (Closes(trimmed.TrimEnd(), fence))
        fence = null;

      if (current is not null)
        current.Append(line).Append('\n');
      else if (!headed && !strayText && line.Trim().Length > 0) {
        problems.Add($"note '{path}' has text before its first section");
        strayText = true;
      }
    }

    return new(Section(sections, CauseHeading), Section(sections, ExampleHeading), Section(sections, WhatToDoHeading));
  }

  /// <summary>The fence a line opens or closes: three or more backticks or tildes at its start.</summary>
  private static string? Fence(string trimmed) {
    if (trimmed.Length < 3 || trimmed[0] is not ('`' or '~'))
      return null;

    var count = CountLeading(trimmed, trimmed[0]);
    return count >= 3 ? new string(trimmed[0], count) : null;
  }

  /// <summary>Whether a line closes a fence: only the fence's character, at least as many as opened it.</summary>
  private static bool Closes(string trimmed, string fence)
    => trimmed.Length >= fence.Length && CountLeading(trimmed, fence[0]) == trimmed.Length;

  private static int CountLeading(string text, char character) {
    var count = 0;
    while (count < text.Length && text[count] == character)
      ++count;
    return count;
  }

  private static string? Section(Dictionary<string, StringBuilder> sections, string heading)
    => sections.TryGetValue(heading, out var text) && text.ToString().Trim('\n').TrimEnd() is var body && body.Trim().Length > 0 ? body : null;

}
