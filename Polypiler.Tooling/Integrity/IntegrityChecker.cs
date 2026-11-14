using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Integrity;

/// <summary>
/// The golden-vector gate below one repository root: compares every golden file with the golden lock,
/// holds every vector file and provenance marker to the provenance rules, and seals a deliberate
/// change. A provenance is independent when Polypiler did not generate it, a <c>reference-run</c> names
/// a key of <c>tooling/toolchains.lock</c>, and an <c>upstream-embedded</c> source is a file of
/// <c>tooling/corpus/corpus.lock.json</c>.
/// </summary>
public sealed class IntegrityChecker {

  private static readonly Regex _upstream = new("^(?<repository>[^/@:]+/[^/@:]+)@(?<commit>[0-9a-f]{40}):(?<path>.+)$", RegexOptions.CultureInvariant);

  private readonly string _root;
  private readonly Lazy<IReadOnlySet<string>> _toolchains;
  private readonly Lazy<CorpusLock> _corpus;

  /// <summary>Creates the gate for a repository root.</summary>
  public IntegrityChecker(string root) {
    this._root = root;
    this._toolchains = new(() => LoadToolchainKeys(this.Resolve(ToolchainLock.RepositoryPath)));
    this._corpus = new(() => LoadCorpusLock(this.Resolve(CorpusLock.RepositoryPath)));
  }

  /// <summary>The golden files and markers as they are on disk now.</summary>
  public GoldenCoverage Coverage() => GoldenCoverage.Of(this._root);

  /// <summary>
  /// Every problem of the repository: golden files whose hash differs from the lock, golden files the
  /// lock does not list, lock entries without their file, and every provenance rule a vector file or a
  /// marker breaks. Reads only; empty when everything holds.
  /// </summary>
  public IReadOnlyList<Diagnostic> Check() {
    GoldenLock lockFile;
    try {
      lockFile = GoldenLock.Load(this.Resolve(GoldenLock.RepositoryPath));
    } catch (IntegrityException exception) {
      return exception.Diagnostics;
    }

    var coverage = this.Coverage();
    var problems = new List<Diagnostic>();
    var onDisk = coverage.Files.ToHashSet(StringComparer.Ordinal);
    foreach (var path in coverage.Files) {
      var entry = lockFile.Find(path);
      if (entry is null)
        problems.Add(IntegrityDiagnostics.Modified(path, "not in the golden lock"));
      else if (GoldenLockEntry.For(path, File.ReadAllBytes(this.Resolve(path))) != entry)
        problems.Add(IntegrityDiagnostics.Modified(path, "hash differs from the golden lock"));
    }

    foreach (var entry in lockFile.Entries.Where(entry => !onDisk.Contains(entry.Path)))
      problems.Add(IntegrityDiagnostics.Modified(entry.Path, "missing on disk"));

    foreach (var path in coverage.Files.Where(IsVectorFile))
      problems.AddRange(this.ProvenanceProblems(path));
    foreach (var marker in coverage.Markers)
      problems.AddRange(this.MarkerProblems(marker));
    return problems;
  }

  /// <summary>
  /// Records the named golden files in the lock as they are on disk now; a named file that no longer
  /// exists loses its entry. Nothing is written when one of them breaks a provenance rule, is not
  /// golden, or the lock cannot be read.
  /// </summary>
  /// <param name="paths">The files, below the repository root, with <c>/</c> separators.</param>
  /// <returns>The problems that kept the lock from being written; empty when it was.</returns>
  /// <exception cref="ArgumentException">A path is neither golden nor in the lock.</exception>
  public IReadOnlyList<Diagnostic> Seal(IReadOnlyList<string> paths) {
    GoldenLock lockFile;
    try {
      lockFile = GoldenLock.Load(this.Resolve(GoldenLock.RepositoryPath));
    } catch (IntegrityException exception) {
      return exception.Diagnostics;
    }

    var coverage = this.Coverage();
    var problems = new List<Diagnostic>();
    var changes = new Dictionary<string, GoldenLockEntry?>(StringComparer.Ordinal);
    foreach (var path in paths.Distinct(StringComparer.Ordinal)) {
      var file = this.Resolve(path);
      if (!File.Exists(file)) {
        if (lockFile.Find(path) is null)
          throw new ArgumentException($"'{path}' does not exist and is not in the golden lock");
        changes[path] = null;
        continue;
      }

      if (!coverage.Covers(path))
        throw new ArgumentException($"'{path}' is not a golden file");
      if (IsVectorFile(path))
        problems.AddRange(this.ProvenanceProblems(path));
      foreach (var marker in coverage.Markers.Where(marker => marker.Path == path || marker.Covers(path)))
        problems.AddRange(this.MarkerProblems(marker));
      changes[path] = GoldenLockEntry.For(path, File.ReadAllBytes(file));
    }

    if (problems.Count > 0)
      return [.. problems.Distinct()];

    var target = this.Resolve(GoldenLock.RepositoryPath);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.WriteAllText(target, lockFile.With(changes).ToJson());
    return [];
  }

  /// <summary>The provenance of a golden path: that of its vector file, else that of the marker covering it; <see langword="null"/> when neither is valid.</summary>
  public Provenance? ProvenanceOf(string path, GoldenCoverage coverage) {
    if (IsVectorFile(path)) {
      try {
        return VectorFile.Load(this.Resolve(path), path).Provenance;
      } catch (IntegrityException) {
        return null;
      }
    }

    return coverage.MarkerOf(path)?.Provenance;
  }

  /// <summary>Whether a path names a vector file.</summary>
  public static bool IsVectorFile(string path) => path.EndsWith(VectorFile.Extension, StringComparison.Ordinal);

  /// <summary>Why a provenance is not independent; empty when it is.</summary>
  public IEnumerable<string> Independence(Provenance provenance) {
    if (provenance.GeneratedBy.Trim().StartsWith("polypiler", StringComparison.OrdinalIgnoreCase))
      yield return $"generatedBy '{provenance.GeneratedBy}' names Polypiler itself";

    if (provenance.Kind == ProvenanceKind.ReferenceRun && !this._toolchains.Value.Contains(provenance.Source))
      yield return $"source is not a toolchain key of {ToolchainLock.RepositoryPath}: '{provenance.Source}'";
    if (provenance.Kind != ProvenanceKind.UpstreamEmbedded)
      yield break;

    var match = _upstream.Match(provenance.Source);
    if (!match.Success)
      yield return $"source is not 'owner/repo@<commit>:<path>': '{provenance.Source}'";
    else if (this._corpus.Value.Find(match.Groups["repository"].Value, match.Groups["commit"].Value, match.Groups["path"].Value) is null)
      yield return $"source is not in the corpus lock: '{provenance.Source}'";
  }

  private IEnumerable<Diagnostic> ProvenanceProblems(string path) {
    VectorFile file;
    try {
      file = VectorFile.Load(this.Resolve(path), path);
    } catch (IntegrityException exception) {
      return exception.Diagnostics;
    }

    return [.. this.Independence(file.Provenance).Select(reason => IntegrityDiagnostics.Provenance(path, reason))];
  }

  private IEnumerable<Diagnostic> MarkerProblems(ProvenanceMarker marker)
    => marker.Provenance is { } provenance
      ? this.Independence(provenance).Select(reason => IntegrityDiagnostics.Provenance(marker.Path, reason))
      : marker.Problems.Select(reason => IntegrityDiagnostics.Provenance(marker.Path, reason));

  private string Resolve(string path) => Path.Combine(this._root, path.Replace('/', Path.DirectorySeparatorChar));

  private static IReadOnlySet<string> LoadToolchainKeys(string file) {
    try {
      return ToolchainLock.Load(file).Entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
    } catch (ToolchainLockException) {
      return new HashSet<string>();
    }
  }

  private static CorpusLock LoadCorpusLock(string file) {
    try {
      return CorpusLock.Load(file);
    } catch (CorpusException) {
      return new([]);
    }
  }

}
