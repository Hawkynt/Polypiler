using System;
using System.Globalization;
using System.Text;

namespace Polypiler.Core;

/// <summary>
/// The two renderings of a diagnostic that ADR 0003 fixes: one line of text in the MSBuild canonical
/// error format, and one line of JSON. Both are pure and culture-invariant; neither changes the
/// command a diagnostic carries.
/// </summary>
public static class DiagnosticRenderer {

  /// <summary>The lower-case name of a severity: <c>info</c>, <c>warning</c> or <c>error</c>.</summary>
  /// <param name="severity">The severity.</param>
  /// <returns>Its name.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The value is no member of <see cref="DiagnosticSeverity"/>.</exception>
  public static string SeverityName(DiagnosticSeverity severity) => severity switch {
    DiagnosticSeverity.Info => "info",
    DiagnosticSeverity.Warning => "warning",
    DiagnosticSeverity.Error => "error",
    _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "not a severity")
  };

  /// <summary>
  /// The diagnostic as one line of text: <c>&lt;file&gt;(&lt;line&gt;,&lt;column&gt;): &lt;severity&gt; &lt;code&gt;: &lt;message&gt;</c>
  /// with a source range, <c>polypiler: &lt;severity&gt; &lt;code&gt;: &lt;message&gt;</c> without; every
  /// <c>\n</c> and <c>\r</c> in the message becomes a space.
  /// </summary>
  /// <param name="diagnostic">The diagnostic.</param>
  /// <returns>The line, without a line break at its end.</returns>
  public static string ToText(Diagnostic diagnostic) {
    ArgumentNullException.ThrowIfNull(diagnostic);
    var prefix = diagnostic.Source is { } source
      ? string.Create(CultureInfo.InvariantCulture, $"{OneLine(source.File)}({source.StartLine},{source.StartColumn})")
      : ProductInfo.Name;
    return $"{prefix}: {SeverityName(diagnostic.Severity)} {diagnostic.Code}: {OneLine(diagnostic.Message)}";
  }

  /// <summary>
  /// The diagnostic as one JSON object on one line, with the keys <c>code</c>, <c>severity</c>,
  /// <c>message</c>, <c>source</c> (<c>file</c>, <c>startLine</c>, <c>startColumn</c>, <c>endLine</c>,
  /// <c>endColumn</c>, or <c>null</c>), <c>rule</c>, <c>cause</c>, <c>workaround</c>,
  /// <c>requiredCapability</c> and <c>command</c> in that order. Only what RFC 8259 requires is
  /// escaped: the quotation mark, the backslash and the control characters; every other character,
  /// non-ASCII included, is written as it is.
  /// </summary>
  /// <param name="diagnostic">The diagnostic.</param>
  /// <returns>The line, without a line break at its end.</returns>
  public static string ToJson(Diagnostic diagnostic) {
    ArgumentNullException.ThrowIfNull(diagnostic);
    var json = new StringBuilder("{");
    Member(json, "code", diagnostic.Code).Append(',');
    Member(json, "severity", SeverityName(diagnostic.Severity)).Append(',');
    Member(json, "message", diagnostic.Message).Append(",\"source\":");
    if (diagnostic.Source is { } source) {
      json.Append('{');
      Member(json, "file", source.File).Append(',');
      json.Append(CultureInfo.InvariantCulture, $"\"startLine\":{source.StartLine},\"startColumn\":{source.StartColumn},\"endLine\":{source.EndLine},\"endColumn\":{source.EndColumn}}}");
    } else {
      json.Append("null");
    }

    json.Append(',');
    Member(json, "rule", diagnostic.Rule).Append(',');
    Member(json, "cause", diagnostic.Cause).Append(',');
    Member(json, "workaround", diagnostic.Workaround).Append(',');
    Member(json, "requiredCapability", diagnostic.RequiredCapability).Append(',');
    return Member(json, "command", diagnostic.Command).Append('}').ToString();
  }

  private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');

  private static StringBuilder Member(StringBuilder json, string name, string? value) {
    json.Append('"').Append(name).Append("\":");
    return value is null ? json.Append("null") : String(json, value);
  }

  private static StringBuilder String(StringBuilder json, string value) {
    json.Append('"');
    foreach (var character in value) {
      switch (character) {
        case '"': json.Append("\\\""); break;
        case '\\': json.Append("\\\\"); break;
        case '\n': json.Append("\\n"); break;
        case '\r': json.Append("\\r"); break;
        case '\t': json.Append("\\t"); break;
        case '\b': json.Append("\\b"); break;
        case '\f': json.Append("\\f"); break;
        case < ' ': json.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}"); break;
        default: json.Append(character); break;
      }
    }

    return json.Append('"');
  }

}
