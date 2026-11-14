using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Polypiler.Tooling.Corpus;

/// <summary>
/// Lists the files of a pinned commit that match a glob, as fixture stubs to paste into
/// <c>corpus.yaml</c>; it never edits the manifest. The same commit and pattern always give the same
/// list, sorted by ordinal path.
/// </summary>
public static class FixtureDiscovery {

  /// <summary>The files of the repository's pinned commit that match the pattern, in ordinal order.</summary>
  /// <param name="git">Runs git.</param>
  /// <param name="directory">A git repository holding the commit (<see cref="CorpusImporter.Locate"/>).</param>
  /// <param name="repository">The repository.</param>
  /// <param name="pattern">The glob.</param>
  /// <exception cref="CorpusException">Git cannot list the commit (<c>PLY0025</c>).</exception>
  public static IReadOnlyList<string> Discover(GitProcess git, string directory, CorpusRepository repository, GlobPattern pattern) {
    var (files, result) = git.ListFiles(directory, repository.Commit);
    if (files is null)
      throw new CorpusException(CorpusDiagnostics.Path($"the files of {repository.Name} at {repository.Commit} cannot be listed", result.Error));
    return [.. files.Where(pattern.IsMatch).Order(StringComparer.Ordinal)];
  }

  /// <summary>A fixture stub for a file: every field present, those only a person can fill in left empty.</summary>
  public static string Stub(CorpusRepository repository, string path) => new StringBuilder()
    .Append("- id: ''\n")
    .Append($"  repository: {Quote(repository.Name)}\n")
    .Append($"  path: {Quote(path)}\n")
    .Append("  export: ''\n")
    .Append("  dependencies: []\n")
    .Append("  run: []\n")
    .Append("  vectors: { kind: embedded, source: '' }\n")
    .Append($"  licence: {Quote(repository.Licence)}\n")
    .Append("  determinism: deterministic\n")
    .Append("  tolerance: exact\n")
    .Append("  profiles: []\n")
    .ToString();

  private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

}
