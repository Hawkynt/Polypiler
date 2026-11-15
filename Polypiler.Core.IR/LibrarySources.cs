using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Polypiler.Core.IR;

/// <summary>
/// The source files of one library of portable implementations as this assembly carries them, each
/// written in the subset of C# the C# frontend imports: which file declares a method, under which
/// licence, and the notices a file obliges an output to keep. Every file states its licence in an
/// <c>SPDX-License-Identifier</c> header on its first line.
/// </summary>
internal sealed class LibrarySources {

  private static readonly Regex _header = new(@"^// SPDX-License-Identifier: (\S.*)$", RegexOptions.CultureInvariant);
  private static readonly Regex _method = new(@"^\s*(?:public|private|internal)\s+static\s+(?!readonly\b)[^=;({\n]*?\b(\w+)[ \t]*\(", RegexOptions.CultureInvariant | RegexOptions.Multiline);

  private readonly string _prefix;
  private readonly IReadOnlyDictionary<string, string[]> _notices;
  private readonly Lazy<IReadOnlyDictionary<string, (string Path, string Licence)>> _origins;

  /// <summary>Describes a library.</summary>
  /// <param name="folder">The folder of the repository the files lie in.</param>
  /// <param name="prefix">The prefix of the names of the resources that carry the files.</param>
  /// <param name="notices">The notices a file obliges an output to keep, by the file's name.</param>
  public LibrarySources(string folder, string prefix, IReadOnlyDictionary<string, string[]> notices) {
    this.Folder = folder;
    this._prefix = prefix;
    this._notices = notices;
    this.All = [.. this.Read()];
    this._origins = new(() => Origins(folder, this.All));
  }

  /// <summary>The folder of the repository the files lie in.</summary>
  public string Folder { get; }

  /// <summary>Every file as its name and its text, in the order of the names.</summary>
  public IReadOnlyList<(string Name, string Text)> All { get; }

  /// <summary>The file that declares a method, with the licence its header states; <see langword="null"/> for a name no file declares.</summary>
  public (string Path, string Licence)? Origin(string function) => this._origins.Value.TryGetValue(function, out var origin) ? origin : null;

  /// <summary>The lines of the notices of the files that declare the methods, each file once, in the order of the file names.</summary>
  public IReadOnlyList<string> Notices(IEnumerable<string> implementations) => [.. implementations
    .Select(this.Origin).OfType<(string Path, string Licence)>()
    .Select(origin => Path.GetFileName(origin.Path))
    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
    .SelectMany(file => this._notices.GetValueOrDefault(file, []))];

  /// <summary>Which file of a set declares each static method, with the licence the header of the file states.</summary>
  /// <param name="folder">The folder of the repository the files lie in.</param>
  /// <param name="sources">The files, as their names and their texts.</param>
  /// <returns>Each method name with the path of its file in the repository and its licence; the first file that declares a name wins.</returns>
  /// <exception cref="InvalidDataException">A file has no <c>SPDX-License-Identifier</c> header on its first line.</exception>
  public static IReadOnlyDictionary<string, (string Path, string Licence)> Origins(string folder, IEnumerable<(string Name, string Text)> sources) {
    ArgumentNullException.ThrowIfNull(sources);
    var origins = new Dictionary<string, (string Path, string Licence)>(StringComparer.Ordinal);
    foreach (var (name, text) in sources) {
      var match = _header.Match(text.Split('\n')[0].TrimEnd('\r'));
      if (!match.Success)
        throw new InvalidDataException($"'{folder}/{name}' has no SPDX-License-Identifier header on its first line.");

      foreach (Match method in _method.Matches(text))
        origins.TryAdd(method.Groups[1].Value, ($"{folder}/{name}", match.Groups[1].Value.Trim()));
    }

    return origins;
  }

  private IEnumerable<(string Name, string Text)> Read() {
    var assembly = typeof(LibrarySources).Assembly;
    var names = assembly.GetManifestResourceNames().Where(name => name.StartsWith(this._prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal);
    foreach (var resource in names) {
      using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
      yield return (resource[this._prefix.Length..], reader.ReadToEnd());
    }
  }

}
