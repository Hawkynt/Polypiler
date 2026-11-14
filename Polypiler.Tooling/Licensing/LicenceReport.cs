using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Polypiler.Core;

namespace Polypiler.Tooling.Licensing;

/// <summary>One scanned file as the report records it.</summary>
/// <param name="Path">The file, relative to the scan root, with <c>/</c> separators.</param>
/// <param name="Licence">The licence expression, the family of an ambiguous text, or <see langword="null"/> when none was found.</param>
/// <param name="Source">The rule that determined it, or <see langword="null"/>.</param>
/// <param name="Status">The file's status in the mode of the scan; a file with a violation is <see cref="LicenceStatus.Denied"/>.</param>
public sealed record LicenceReportEntry(string Path, string? Licence, LicenceSource? Source, LicenceStatus Status);

/// <summary>
/// The result of a scan: every file with its licence, and every violation. Written as
/// <c>[{"path","licence","source","status"}]</c>, sorted by path (ordinal), two-space indent, LF line
/// ends and a trailing line break, so the same tree always gives the same bytes.
/// </summary>
public sealed class LicenceReport {

  /// <summary>Where the report goes below the repository root when no other file is named.</summary>
  public const string DefaultPath = "artifacts/licenses/report.json";

  /// <summary>Creates a report of entries, which it sorts, and diagnostics, which it keeps in order.</summary>
  public LicenceReport(IEnumerable<LicenceReportEntry> entries, IEnumerable<Diagnostic> diagnostics) {
    this.Entries = [.. entries.OrderBy(entry => entry.Path, StringComparer.Ordinal)];
    this.Diagnostics = [.. diagnostics];
  }

  /// <summary>The scanned files, sorted by path.</summary>
  public IReadOnlyList<LicenceReportEntry> Entries { get; }

  /// <summary>Every violation, in the order the files were scanned.</summary>
  public IReadOnlyList<Diagnostic> Diagnostics { get; }

  /// <summary>The process exit code the report stands for.</summary>
  public int ExitCode => LicenceDiagnostics.ExitCodeOf(this.Diagnostics);

  /// <summary>The entry of a path, or <see langword="null"/>.</summary>
  public LicenceReportEntry? Entry(string path) => this.Entries.FirstOrDefault(entry => entry.Path == path);

  /// <summary>The text of a source in the report: <c>header</c>, <c>override</c> or <c>licence-file</c>.</summary>
  public static string Name(LicenceSource source) => source switch {
    LicenceSource.Header => "header",
    LicenceSource.Override => "override",
    _ => "licence-file"
  };

  /// <summary>The text of a status in the report and in diagnostics: <c>allowed</c>, <c>reference-only</c> or <c>denied</c>.</summary>
  public static string Name(LicenceStatus status) => status switch {
    LicenceStatus.Allowed => "allowed",
    LicenceStatus.ReferenceOnly => "reference-only",
    _ => "denied"
  };

  /// <summary>The text of a mode in diagnostics and on the command line: <c>corpus</c> or <c>distribution</c>.</summary>
  public static string Name(LicenceMode mode) => mode == LicenceMode.Corpus ? "corpus" : "distribution";

  /// <summary>The report as its canonical JSON text.</summary>
  public string ToJson() {
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, IndentSize = 2, NewLine = "\n" })) {
      writer.WriteStartArray();
      foreach (var entry in this.Entries) {
        writer.WriteStartObject();
        writer.WriteString("path", entry.Path);
        writer.WriteString("licence", entry.Licence);
        writer.WriteString("source", entry.Source is { } source ? Name(source) : null);
        writer.WriteString("status", Name(entry.Status));
        writer.WriteEndObject();
      }

      writer.WriteEndArray();
    }

    return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
  }

}
