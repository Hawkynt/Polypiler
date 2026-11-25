using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Polypiler.Core.Verification.Runners;

/// <summary>A message of the Rust compiler or of clippy: its level, its code or lint, where it points and what it says.</summary>
/// <param name="Level">The level: <c>error</c>, <c>warning</c>, <c>note</c>, …</param>
/// <param name="Code">The error code (<c>E0308</c>) or the lint (<c>clippy::identity_op</c>), or <see langword="null"/>.</param>
/// <param name="File">The file of the primary span, relative to the package, or <see langword="null"/>.</param>
/// <param name="Line">The line of the primary span, 1-based; 0 without one.</param>
/// <param name="Column">The column of the primary span, 1-based; 0 without one.</param>
/// <param name="Text">The message.</param>
public sealed record CargoMessage(string Level, string? Code, string? File, int Line, int Column, string Text) {

  /// <inheritdoc/>
  public override string ToString()
    => $"{this.Level}{(this.Code is null ? "" : $"[{this.Code}]")}: {this.Text}{(this.File is null ? "" : $" at {this.File}:{this.Line}:{this.Column}")}";

}

/// <summary>
/// Reads the output of a Cargo command run with <c>--message-format=json</c> (The Cargo Book, "JSON
/// messages"): one JSON object per line; the lines whose <c>reason</c> is <c>compiler-message</c> carry
/// a diagnostic of rustc or clippy. Every other line - an artifact, the end of the build, the output of
/// a program <c>cargo run</c> started - is no message.
/// </summary>
public static class CargoMessageParser {

  /// <summary>The compiler messages of an output, in order; the summary lines (<c>aborting due to …</c>) left out.</summary>
  /// <param name="output">The standard output of the command.</param>
  /// <returns>The messages.</returns>
  public static IReadOnlyList<CargoMessage> Parse(string output) {
    ArgumentNullException.ThrowIfNull(output);
    var messages = new List<CargoMessage>();
    foreach (var line in output.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith('{'))) {
      using var document = Read(line);
      if (document is null || !document.RootElement.TryGetProperty("reason", out var reason) || reason.GetString() != "compiler-message"
        || !document.RootElement.TryGetProperty("message", out var message))
        continue;

      var level = message.GetProperty("level").GetString() ?? "";
      var text = message.GetProperty("message").GetString() ?? "";
      if (text.StartsWith("aborting due to", StringComparison.Ordinal) || text.EndsWith("warnings emitted", StringComparison.Ordinal) || text.EndsWith("warning emitted", StringComparison.Ordinal))
        continue;

      var code = message.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Object ? codeElement.GetProperty("code").GetString() : null;
      var span = message.TryGetProperty("spans", out var spans) ? spans.EnumerateArray().FirstOrDefault(candidate => candidate.GetProperty("is_primary").GetBoolean()) : default;
      messages.Add(span.ValueKind == JsonValueKind.Object
        ? new CargoMessage(level, code, span.GetProperty("file_name").GetString()?.Replace('\\', '/'), span.GetProperty("line_start").GetInt32(), span.GetProperty("column_start").GetInt32(), text)
        : new CargoMessage(level, code, null, 0, 0, text));
    }

    return messages;
  }

  /// <summary>Whether the output says the build succeeded: its <c>build-finished</c> line; <see langword="null"/> without one.</summary>
  /// <param name="output">The standard output of the command.</param>
  public static bool? Succeeded(string output) {
    ArgumentNullException.ThrowIfNull(output);
    foreach (var line in output.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith('{'))) {
      using var document = Read(line);
      if (document is not null && document.RootElement.TryGetProperty("reason", out var reason) && reason.GetString() == "build-finished")
        return document.RootElement.GetProperty("success").GetBoolean();
    }

    return null;
  }

  private static JsonDocument? Read(string line) {
    try {
      return JsonDocument.Parse(line);
    } catch (JsonException) {
      return null;
    }
  }

}
