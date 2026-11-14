using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Polypiler.Core;
using Polypiler.Tooling.Corpus;

namespace Polypiler.Tooling.Integrity;

/// <summary>
/// The pull-request rule over the commits <c>&lt;base&gt;..HEAD</c>: a commit that changes a golden
/// file changes nothing outside <see cref="Companions"/>, and <c>tests/fixtures/golden.changes.md</c>
/// gains, between the base and <c>HEAD</c>, a line <c>- &lt;path&gt;: &lt;reason&gt; (source: &lt;source&gt;)</c>
/// for every golden path changed; where the path has a provenance, the source is that provenance's.
/// </summary>
/// <param name="git">Runs git in the repository root.</param>
/// <param name="root">The repository root, which is the root of the git working tree.</param>
/// <param name="checker">The gate of the same root, which knows the golden files and their provenance.</param>
public sealed class ChangeRule(GitProcess git, string root, IntegrityChecker checker) {

  /// <summary>The folders a commit that changes a golden file may also change.</summary>
  public static IReadOnlyList<string> Companions { get; } = ["tests/fixtures/", "tests/features/", "tests/vectors/", "backlog/"];

  private static readonly Regex _line = new(@"^- (?<path>[^:]+): (?<reason>.+) \(source: (?<source>[^)]+)\)$", RegexOptions.CultureInvariant);

  /// <summary>Whether a git revision names a commit of the repository.</summary>
  public bool Resolves(string revision) => git.Run(root, "rev-parse", "--verify", "--quiet", "--end-of-options", revision + "^{commit}").Succeeded;

  /// <summary>Every breach of the rule by the commits after <paramref name="baseRevision"/>; empty when there is none.</summary>
  /// <param name="baseRevision">A revision that <see cref="Resolves"/>.</param>
  /// <param name="commits">Receives the number of commits checked.</param>
  public IReadOnlyList<Diagnostic> Check(string baseRevision, out int commits) {
    var coverage = checker.Coverage();
    var list = Lines(this.Git("rev-list", "--reverse", $"{baseRevision}..HEAD"));
    commits = list.Length;
    var problems = new List<Diagnostic>();
    var changed = new List<string>();
    foreach (var commit in list) {
      var files = this.Git("diff-tree", "--no-commit-id", "--name-only", "-r", "--root", "--no-renames", "-z", commit).Split('\0', StringSplitOptions.RemoveEmptyEntries);
      var golden = files.Where(coverage.Covers).ToArray();
      var others = files.Where(file => !Companions.Any(folder => file.StartsWith(folder, StringComparison.Ordinal))).ToArray();
      foreach (var path in golden) {
        if (others.Length > 0)
          problems.Add(IntegrityDiagnostics.Modified(path, $"changed together with {string.Join(", ", others.Select(other => $"'{other}'"))} in commit {commit[..12]}"));
        if (!changed.Contains(path))
          changed.Add(path);
      }
    }

    if (changed.Count == 0)
      return problems;

    var added = Lines(this.Show("HEAD")).ToList();
    foreach (var line in Lines(this.Show(baseRevision)))
      added.Remove(line);
    var declared = added.Select(line => _line.Match(line)).Where(match => match.Success).ToList();
    foreach (var path in changed) {
      var source = checker.ProvenanceOf(path, coverage)?.Source;
      var lines = declared.Where(match => match.Groups["path"].Value == path).ToList();
      if (lines.Count == 0)
        problems.Add(IntegrityDiagnostics.Modified(path, $"no entry in {GoldenLock.ChangeLogPath}"));
      else if (source is not null && !lines.Any(match => match.Groups["source"].Value == source))
        problems.Add(IntegrityDiagnostics.Modified(path, $"the entry in {GoldenLock.ChangeLogPath} does not name the source '{source}'"));
    }

    return problems;
  }

  private string Git(params string[] arguments) {
    var result = git.Run(root, arguments);
    if (!result.Succeeded)
      throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.Error}");
    return Encoding.UTF8.GetString(result.Output);
  }

  /// <summary>The change log at a revision; empty where it does not exist yet.</summary>
  private string Show(string revision) {
    var result = git.Run(root, "show", $"{revision}:{GoldenLock.ChangeLogPath}");
    return result.Succeeded ? Encoding.UTF8.GetString(result.Output) : "";
  }

  private static string[] Lines(string text) => [.. text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0)];

}
