using System.Text;
using System.Text.RegularExpressions;

namespace Polypiler.Tooling.Corpus;

/// <summary>
/// A path pattern of <c>/</c>-separated segments: <c>*</c> matches any run of characters within one
/// segment, <c>?</c> one character of a segment, and a segment that is exactly <c>**</c> any number of
/// whole segments, none included. Matching is ordinal and case-sensitive, as git paths are.
/// </summary>
public sealed class GlobPattern {

  private readonly Regex _regex;

  private GlobPattern(string text, Regex regex) {
    this.Text = text;
    this._regex = regex;
  }

  /// <summary>The pattern as it was written.</summary>
  public string Text { get; }

  /// <summary>Compiles a pattern.</summary>
  public static GlobPattern Parse(string text) {
    var regex = new StringBuilder("^");
    var segments = text.Split('/');
    for (var index = 0; index < segments.Length; ++index) {
      var last = index == segments.Length - 1;
      if (segments[index] == "**") {
        regex.Append(last ? ".*" : "(?:[^/]+/)*");
        continue;
      }

      foreach (var character in segments[index])
        regex.Append(character switch {
          '*' => "[^/]*",
          '?' => "[^/]",
          _ => Regex.Escape(character.ToString())
        });
      if (!last)
        regex.Append('/');
    }

    return new(text, new Regex(regex.Append('$').ToString(), RegexOptions.CultureInvariant));
  }

  /// <summary>Whether a path matches the whole pattern.</summary>
  public bool IsMatch(string path) => this._regex.IsMatch(path);

}
