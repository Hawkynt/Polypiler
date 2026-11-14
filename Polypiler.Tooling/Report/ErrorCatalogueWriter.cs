using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core;
using Polypiler.Tooling.Diagnostics;

namespace Polypiler.Tooling.Report;

/// <summary>
/// Writes the error catalogue of a diagnostics registry: the index <c>README.md</c> with one table per
/// block of codes, and one page <c>PLY####.md</c> per registered code with its properties and the
/// sections of its <see cref="ErrorNote"/>. The text depends on nothing but the registry and the notes:
/// line feeds only, a trailing line break, no date and no path of the machine.
/// </summary>
public static class ErrorCatalogueWriter {

  /// <summary>The first line of every generated file.</summary>
  public const string Marker = "<!-- generated from spec/diagnostics.yaml by \"report errors\"; do not edit -->";

  /// <summary>The name of the index.</summary>
  public const string IndexName = "README.md";

  /// <summary>What a section reads when the note of its code does not provide it.</summary>
  public const string NoExplanation = "No extended explanation has been written for this code yet.";

  /// <summary>
  /// The exit code a strict warning produces under <c>--strict</c>: it is reported as the refusal
  /// <c>PLY0010</c>, which the registry records with <see cref="ExitCode.UnsupportedConstruct"/>.
  /// </summary>
  public const ExitCode StrictExitCode = ExitCode.UnsupportedConstruct;

  /// <summary>The name of the page of a code.</summary>
  /// <param name="code">The code.</param>
  public static string PageName(string code) => code + ".md";

  /// <summary>Writes the index and every page.</summary>
  /// <param name="registry">The registry; its problems are not looked at.</param>
  /// <param name="notes">The notes by code; a code without one gets the default sentence in every section.</param>
  /// <returns>The index first, then the pages in ascending code order.</returns>
  public static IReadOnlyList<GeneratedFile> Generate(DiagnosticRegistry registry, IReadOnlyDictionary<string, ErrorNote> notes) {
    var codes = registry.Codes.OrderBy(entry => entry.Code, StringComparer.Ordinal).ToList();
    return [
      new(IndexName, Index(registry.Blocks, codes)),
      .. codes.Select(entry => new GeneratedFile(PageName(entry.Code), Page(entry, notes.GetValueOrDefault(entry.Code) ?? ErrorNote.Empty)))
    ];
  }

  /// <summary>The index: the introduction, then one table per block that holds a code.</summary>
  private static string Index(IReadOnlyList<DiagnosticBlock> blocks, List<DiagnosticEntry> codes) {
    var text = new StringBuilder();
    text.Append(Marker).Append('\n');
    text.Append("# Error catalogue\n\n");
    text.Append("Every diagnostic Polypiler reports carries a code, `PLY` and four digits, that means one thing forever. ");
    text.Append("Each area allocates its codes from a block of its own; the tables below list every registered code by block with its severity, ");
    text.Append("the exit code it produces and the issue that owns it, and each code links to a page with its cause, an example and what to do about it. ");
    text.Append("Only an error sets the exit code; a warning does so only where `--strict` turns it into an error. ");
    text.Append("A *reserved* code is allocated to an issue that is not implemented yet; a *retired* code is no longer reported and is never given to anything else. ");
    text.Append("The pages are generated from [`spec/diagnostics.yaml`](../../spec/diagnostics.yaml).\n");

    var byBlock = codes.ToLookup(entry => Innermost(blocks, entry.Code));
    var ordered = blocks.OrderBy(block => block.First, StringComparer.Ordinal).ThenByDescending(block => block.Last, StringComparer.Ordinal);
    foreach (var block in ordered.Where(block => byBlock[block].Any())) {
      text.Append(CultureInfo.InvariantCulture, $"\n## {block.First}–{block.Last} · {Escape(block.Area)}\n\n");
      text.Append("| Code | Severity | Exit code | Message | Owner |\n");
      text.Append("| --- | --- | --- | --- | --- |\n");
      foreach (var entry in byBlock[block])
        text.Append(CultureInfo.InvariantCulture, $"| {CodeCell(entry)} | {Severity(entry)} | {ExitCodeText(entry)} | {CodeSpan(entry.Message)} | {Escape(entry.Owner)} |\n");
    }

    return text.ToString();
  }

  /// <summary>The page of one code.</summary>
  private static string Page(DiagnosticEntry entry, ErrorNote note) {
    var text = new StringBuilder();
    text.Append(Marker).Append('\n');
    text.Append(CultureInfo.InvariantCulture, $"# {entry.Code} — {Escape(entry.Title)}\n\n");
    text.Append("| Property | Value |\n");
    text.Append("| --- | --- |\n");
    text.Append(CultureInfo.InvariantCulture, $"| Message | {CodeSpan(entry.Message)} |\n");
    text.Append(CultureInfo.InvariantCulture, $"| Severity | {Severity(entry)} |\n");
    text.Append(CultureInfo.InvariantCulture, $"| Exit code | {ExitCodeText(entry)} |\n");
    text.Append(CultureInfo.InvariantCulture, $"| Strict promotion | {(entry.Strict ? "yes" : "no")} |\n");
    text.Append(CultureInfo.InvariantCulture, $"| Rule | {(entry.Rule is null ? "—" : CodeSpan(entry.Rule))} |\n");
    text.Append(CultureInfo.InvariantCulture, $"| Area | {Escape(entry.Area)} |\n");
    text.Append(CultureInfo.InvariantCulture, $"| Owning issue | {Escape(entry.Owner)} |\n");
    text.Append(CultureInfo.InvariantCulture, $"| Status | {Status(entry)} |\n");
    if (entry.Surfaces is not null)
      text.Append(CultureInfo.InvariantCulture, $"| Seen in | {Escape(entry.Surfaces)} |\n");

    Section(text, ErrorNote.CauseHeading, note.Cause);
    Section(text, ErrorNote.ExampleHeading, note.Example);
    Section(text, ErrorNote.WhatToDoHeading, note.WhatToDo);
    text.Append(CultureInfo.InvariantCulture, $"\n[Back to the error catalogue]({IndexName})\n");
    return text.ToString();
  }

  private static void Section(StringBuilder text, string heading, string? body)
    => text.Append(CultureInfo.InvariantCulture, $"\n{heading}\n\n{body ?? NoExplanation}\n");

  /// <summary>The block a code is listed under: the smallest that contains it; <see langword="null"/> for none.</summary>
  private static DiagnosticBlock? Innermost(IReadOnlyList<DiagnosticBlock> blocks, string code)
    => blocks.Where(block => block.Contains(code)).MinBy(block => Number(block.Last) - Number(block.First));

  private static int Number(string code) => int.Parse(code.AsSpan(3), CultureInfo.InvariantCulture);

  private static string CodeCell(DiagnosticEntry entry) {
    var link = $"[{entry.Code}]({PageName(entry.Code)})";
    return entry.Retired ? $"~~{link}~~ (retired)" : entry.Reserved ? $"{link} (reserved)" : link;
  }

  private static string Severity(DiagnosticEntry entry) => DiagnosticRenderer.SeverityName(entry.Severity);

  /// <summary>
  /// <c>1 (EnvironmentError)</c> for an error, <c>—</c> for a warning or an info, and
  /// <c>— (3 with --strict)</c> for a warning <c>--strict</c> turns into an error.
  /// </summary>
  public static string ExitCodeText(DiagnosticEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.ExitCode is { } exitCode)
      return string.Create(CultureInfo.InvariantCulture, $"{(int)exitCode} ({exitCode})");

    return entry.Strict ? string.Create(CultureInfo.InvariantCulture, $"— ({(int)StrictExitCode} with --strict)") : "—";
  }

  private static string Status(DiagnosticEntry entry) => entry.Retired ? "retired" : entry.Reserved ? "reserved" : "active";

  /// <summary>
  /// A text as a Markdown code span inside a table cell: fenced by one backtick more than the longest
  /// run of backticks in it, padded with a space where it begins or ends with a backtick or with a space
  /// on both ends, and <c>|</c> written <c>\|</c>; an empty text is <c>—</c>.
  /// </summary>
  public static string CodeSpan(string text) {
    ArgumentNullException.ThrowIfNull(text);
    if (text.Length == 0)
      return "—";

    var (longest, run) = (0, 0);
    foreach (var character in text) {
      run = character == '`' ? run + 1 : 0;
      longest = Math.Max(longest, run);
    }

    var fence = new string('`', longest + 1);
    var pad = text[0] == '`' || text[^1] == '`' || (text[0] == ' ' && text[^1] == ' ') ? " " : "";
    return $"{fence}{pad}{text.Replace("|", "\\|", StringComparison.Ordinal)}{pad}{fence}";
  }

  /// <summary>A text as plain Markdown inside a table cell or a heading: every character Markdown could read as markup is escaped with a backslash.</summary>
  public static string Escape(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var result = new StringBuilder(text.Length);
    foreach (var character in text) {
      if (character is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|' or '~' or '&')
        result.Append('\\');
      result.Append(character);
    }

    return result.ToString();
  }

}
