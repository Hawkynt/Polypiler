using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Tooling.Licensing;

/// <summary>One entry of a provenance notice: a third-party work some code of the project was taken from.</summary>
/// <param name="Line">The 1-based line the entry starts on.</param>
/// <param name="Origins">The values of its <c>Origin:</c> lines: <c>owner/repo@sha:path</c> or a URL.</param>
/// <param name="Licences">The values of its <c>Licence:</c> lines, SPDX expressions.</param>
public sealed record ProvenanceEntry(int Line, IReadOnlyList<string> Origins, IReadOnlyList<string> Licences) {

  /// <summary>Whether the entry has both machine-read lines.</summary>
  public bool IsComplete => this.Origins.Count > 0 && this.Licences.Count > 0;

}

/// <summary>
/// The machine-read part of a provenance notice, the two forms AGENTS.md names: <c>THIRD_PARTY_NOTICES.md</c>,
/// whose <c>## </c> sections are one entry each (the text before the first is a preamble), and
/// <c>THIRD-PARTY-NOTICE.&lt;Name&gt;.txt</c>, which is one entry as a whole. An entry carries at least one
/// line <c>Origin: &lt;owner/repo@sha:path or URL&gt;</c> and at least one line
/// <c>Licence: &lt;SPDX expression&gt;</c>; a line counts when it starts with the key after optional
/// indentation and an optional Markdown list marker (<c>- </c> or <c>* </c>). An entry of several
/// works lists an <c>Origin:</c> and a <c>Licence:</c> line for each.
/// </summary>
public static class ProvenanceNotice {

  /// <summary>The key of the line that says where the code came from.</summary>
  public const string OriginKey = "Origin:";

  /// <summary>The key of the line that says what the code is licensed under.</summary>
  public const string LicenceKey = "Licence:";

  /// <summary>The name of the Markdown form.</summary>
  public const string MarkdownName = "THIRD_PARTY_NOTICES.md";

  /// <summary>Whether a file name is one of the two forms of a provenance notice.</summary>
  /// <param name="name">The file name.</param>
  public static bool IsNotice(string name)
    => name == MarkdownName
      || (name.StartsWith("THIRD-PARTY-NOTICE.", StringComparison.Ordinal) && name.EndsWith(".txt", StringComparison.Ordinal) && name.Length > "THIRD-PARTY-NOTICE..txt".Length);

  /// <summary>The entries of a notice's text.</summary>
  /// <param name="name">The file name, which selects the form.</param>
  /// <param name="text">The text.</param>
  /// <returns>The entries in file order; a Markdown notice without a section has none.</returns>
  public static IReadOnlyList<ProvenanceEntry> Parse(string name, string text) {
    var markdown = name == MarkdownName;
    var entries = new List<ProvenanceEntry>();
    var lines = text.Split('\n');
    var start = markdown ? -1 : 0;
    List<string> origins = [];
    List<string> licences = [];
    for (var index = 0; index < lines.Length; ++index) {
      var line = lines[index].TrimEnd('\r');
      if (markdown && line.StartsWith("## ", StringComparison.Ordinal)) {
        if (start >= 0)
          entries.Add(new(start + 1, origins, licences));
        (start, origins, licences) = (index, [], []);
        continue;
      }

      if (Value(line, OriginKey) is { } origin)
        origins.Add(origin);
      else if (Value(line, LicenceKey) is { } licence)
        licences.Add(licence);
    }

    if (start >= 0)
      entries.Add(new(start + 1, origins, licences));
    return entries;
  }

  /// <summary>The value of a machine-read line with a key, or <see langword="null"/> when the line is another or the value is empty.</summary>
  private static string? Value(string line, string key) {
    var text = line.TrimStart();
    if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal))
      text = text[2..].TrimStart();
    var value = text.StartsWith(key, StringComparison.Ordinal) ? text[key.Length..].Trim() : "";
    return value.Length > 0 ? value : null;
  }

}
