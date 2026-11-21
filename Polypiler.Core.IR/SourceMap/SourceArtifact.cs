using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Polypiler.Core.IR;

/// <summary>
/// A source a module was made from: a file of a repository at one commit, with the hash of its content
/// and the dialect it is written in. It never holds an absolute local path, and never the content.
/// </summary>
/// <param name="Repository">The repository, such as <c>Hawkynt/PB-Compiler</c>; no <c>@</c> and no white space.</param>
/// <param name="Commit">The commit, a full hash or another name of it; no <c>:</c> and no white space.</param>
/// <param name="Path">The path of the file in the repository, relative, with <c>/</c> between the parts.</param>
/// <param name="Sha256">The SHA-256 of the bytes of the file, 64 lower-case hexadecimal digits.</param>
/// <param name="Dialect">The ID of the dialect, such as <c>pb/3.5</c>.</param>
public sealed partial record SourceArtifact(string Repository, string Commit, string Path, string Sha256, string Dialect) {

  /// <summary>The source as the <c>sources</c> table names it: <c>repository@commit:path</c>.</summary>
  public string Reference => $"{this.Repository}@{this.Commit}:{this.Path}";

  /// <summary>Whether the source is well formed: <see langword="null"/> when it is, otherwise why not.</summary>
  public string? Problem
    => !ReferencePattern().IsMatch(this.Reference) ? "the reference is not repository@commit:path"
      : this.Path.StartsWith('/') || this.Path.Contains('\\') || this.Path.Contains(':') || this.Path.Split('/').Any(part => part is "" or "." or "..") ? "the path is not relative to the repository"
      : !HashPattern().IsMatch(this.Sha256) ? "the SHA-256 is not 64 lower-case hexadecimal digits"
      : this.Dialect.Length == 0 || this.Dialect.Contains('"') ? "the dialect is empty or holds a quote"
      : null;

  /// <summary>
  /// The text form of the table of sources, which follows the module line:
  /// <c>sources {</c>, one line <c>&lt;index&gt;: "repository@commit:path" sha256:&lt;hex&gt; dialect:"&lt;id&gt;"</c>
  /// per source, and <c>}</c>.
  /// </summary>
  /// <param name="sources">The sources, in the order of their indices.</param>
  /// <returns>The table, as <see cref="TryParseTable"/> reads it back.</returns>
  public static string FormatTable(IReadOnlyList<SourceArtifact> sources) {
    var text = new StringBuilder("sources {\n");
    for (var index = 0; index < sources.Count; ++index)
      text.Append(CultureInfo.InvariantCulture, $"  {index}: \"{sources[index].Reference}\" sha256:{sources[index].Sha256} dialect:\"{sources[index].Dialect}\"\n");

    return text.Append('}').ToString();
  }

  /// <summary>Reads a table of sources that <see cref="FormatTable"/> wrote, on several lines or on one.</summary>
  /// <param name="text">The table.</param>
  /// <param name="sources">The sources read, in order; empty when the text is refused.</param>
  /// <param name="diagnostic">
  /// <see cref="SourceMapDiagnostics.MalformedLocation"/> (PLY0116) naming the part that is no table, no
  /// entry, an entry out of order or a malformed source; <see langword="null"/> when the text is read.
  /// </param>
  /// <returns>Whether the text was read.</returns>
  public static bool TryParseTable(string text, out IReadOnlyList<SourceArtifact> sources, out Diagnostic? diagnostic) {
    sources = [];
    diagnostic = null;
    var table = TablePattern().Match(text);
    if (!table.Success)
      return Refuse(text, out diagnostic);

    var read = new List<SourceArtifact>();
    var body = table.Groups[1].Value;
    var position = 0;
    for (var entry = EntryPattern().Match(body); entry.Success; entry = entry.NextMatch()) {
      var reference = ReferencePattern().Match(entry.Groups[2].Value);
      if (entry.Groups[1].Value != read.Count.ToString(CultureInfo.InvariantCulture) || !reference.Success)
        return Refuse(entry.Value.Trim(), out diagnostic);

      var source = new SourceArtifact(reference.Groups[1].Value, reference.Groups[2].Value, reference.Groups[3].Value, entry.Groups[3].Value, entry.Groups[4].Value);
      if (source.Problem is not null)
        return Refuse(entry.Value.Trim(), out diagnostic);

      read.Add(source);
      position = entry.Index + entry.Length;
    }

    if (body[position..].Trim() is { Length: > 0 } rest)
      return Refuse(rest, out diagnostic);

    sources = read;
    return true;
  }

  private static bool Refuse(string text, out Diagnostic? diagnostic) {
    diagnostic = Diagnostic.Create(SourceMapDiagnostics.MalformedLocation, null, text);
    return false;
  }

  [GeneratedRegex(@"^\s*sources\s*\{(.*)\}\s*$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
  private static partial Regex TablePattern();

  [GeneratedRegex(@"\G\s*([0-9]+):\s*""([^""]*)""\s+sha256:(\S*)\s+dialect:""([^""]*)""", RegexOptions.CultureInvariant)]
  private static partial Regex EntryPattern();

  [GeneratedRegex(@"^([^@\s]+)@([^:\s]+):(\S+)$", RegexOptions.CultureInvariant)]
  private static partial Regex ReferencePattern();

  [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
  private static partial Regex HashPattern();

}
