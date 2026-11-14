using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core.Verification.Manifests;

namespace Polypiler.Core.Verification.Gates;

/// <summary>
/// Writes gate records: the run that produced a piece of evidence records it with one call, which
/// replaces the entry of the same fixture and state in the pair's file. Recording the same evidence
/// again leaves the file's bytes as they are. Records of one directory are written one at a time
/// within a process; two processes must not record the same pair at once.
/// </summary>
public sealed partial class GateRecorder {

  /// <summary>The environment variable whose value, when set, is the commit a recorder names by default.</summary>
  public const string CommitVariable = "GITHUB_SHA";

  /// <summary>The commit named when nothing says which it is.</summary>
  public const string UnknownCommit = "unknown";

  private static readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);

  /// <summary>A recorder that names the commit of <see cref="CommitVariable"/>, or <see cref="UnknownCommit"/>.</summary>
  /// <param name="directory">The gate directory, for example <c>artifacts/gates</c>; it is created on the first record.</param>
  public GateRecorder(string directory)
    : this(directory, Environment.GetEnvironmentVariable(CommitVariable) is { Length: > 0 } commit ? commit : UnknownCommit) { }

  /// <summary>A recorder that names a commit.</summary>
  /// <param name="directory">The gate directory, for example <c>artifacts/gates</c>; it is created on the first record.</param>
  /// <param name="commit">The commit the evidence is produced at.</param>
  /// <exception cref="ArgumentException">The directory or the commit is empty.</exception>
  public GateRecorder(string directory, string commit) {
    ArgumentException.ThrowIfNullOrEmpty(directory);
    ArgumentException.ThrowIfNullOrEmpty(commit);
    this.Directory = Path.GetFullPath(directory);
    this.Commit = commit;
  }

  /// <summary>The gate directory, as a full path.</summary>
  public string Directory { get; }

  /// <summary>The directory the evidence paths are relative to: the one above <see cref="Directory"/>.</summary>
  public string Root => Path.GetDirectoryName(this.Directory)!;

  /// <summary>The commit the records name.</summary>
  public string Commit { get; }

  /// <summary>The file of a pair's record.</summary>
  /// <param name="pair">The pair.</param>
  /// <returns>The full path.</returns>
  public string PathOf(PairKey pair) {
    ArgumentNullException.ThrowIfNull(pair);
    return Path.Combine(this.Directory, pair.FileName);
  }

  /// <summary>The evidence a file gives for a state: its kind by the table, its hash and its path below <see cref="Root"/>.</summary>
  /// <param name="state">The state.</param>
  /// <param name="file">The file, which must lie below <see cref="Root"/>.</param>
  /// <returns>The evidence.</returns>
  /// <exception cref="ArgumentException">The state is <see cref="GateState.Planned"/>, or the file is not below <see cref="Root"/>.</exception>
  /// <exception cref="IOException">The file cannot be read.</exception>
  public GateEvidence Evidence(GateState state, string file) {
    ArgumentNullException.ThrowIfNull(file);
    var relative = Path.GetRelativePath(this.Root, Path.GetFullPath(file));
    if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
      throw new ArgumentException($"'{file}' is not below '{this.Root}'", nameof(file));

    return new(state, Kind(state), ManifestWriter.PortablePath(relative), ManifestWriter.Sha256(File.ReadAllBytes(file)));
  }

  /// <summary>Records one piece of evidence of a fixture, replacing the fixture's entry for the same state.</summary>
  /// <param name="pair">The pair.</param>
  /// <param name="fixtureId">The fixture.</param>
  /// <param name="evidence">The evidence.</param>
  /// <exception cref="ArgumentException">The fixture is empty, or the evidence is of the wrong kind, has no valid hash or a path that leaves <see cref="Root"/>.</exception>
  /// <exception cref="InvalidDataException">The pair's file exists but is no gate record of the pair.</exception>
  public void Record(PairKey pair, string fixtureId, GateEvidence evidence) {
    ArgumentNullException.ThrowIfNull(pair);
    ArgumentException.ThrowIfNullOrEmpty(fixtureId);
    ArgumentNullException.ThrowIfNull(evidence);
    if (evidence.Kind != Kind(evidence.State))
      throw new ArgumentException($"evidence kind '{evidence.Kind}' is not accepted for '{GateStates.Name(evidence.State)}'", nameof(evidence));
    if (evidence.Sha256 is null || !Sha256().IsMatch(evidence.Sha256))
      throw new ArgumentException($"'{evidence.Sha256}' is no SHA-256 of 64 lower-case hexadecimal digits", nameof(evidence));
    if (!IsBelowRoot(evidence.Path))
      throw new ArgumentException($"'{evidence.Path}' is no relative path below the artifacts directory", nameof(evidence));

    this.Update(pair, fixtures => {
      var index = fixtures.FindIndex(fixture => fixture.FixtureId == fixtureId);
      List<GateEvidence> others = index < 0 ? [] : fixtures[index].Evidence.Where(entry => entry.State != evidence.State).ToList();
      if (index >= 0)
        fixtures.RemoveAt(index);

      fixtures.Add(new(fixtureId, [.. others, evidence]));
    });
  }

  /// <summary>Removes every entry of a fixture, so that a new run can record its chain from the start.</summary>
  /// <param name="pair">The pair.</param>
  /// <param name="fixtureId">The fixture.</param>
  /// <exception cref="InvalidDataException">The pair's file exists but is no gate record of the pair.</exception>
  public void Forget(PairKey pair, string fixtureId) {
    ArgumentNullException.ThrowIfNull(pair);
    ArgumentException.ThrowIfNullOrEmpty(fixtureId);
    this.Update(pair, fixtures => fixtures.RemoveAll(fixture => fixture.FixtureId == fixtureId));
  }

  /// <summary>Whether a recorded path names a file below the artifacts directory: relative, with <c>/</c>, without <c>.</c> or <c>..</c> parts.</summary>
  /// <param name="path">The path as the record holds it.</param>
  /// <returns>Whether it does.</returns>
  internal static bool IsBelowRoot(string? path)
    => path is { Length: > 0 } && !path.Contains('\\') && !path.Contains(':') && !path.StartsWith('/')
       && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

  private static string Kind(GateState state) {
    try {
      return GateStates.KindOf(state);
    } catch (ArgumentOutOfRangeException exception) {
      throw new ArgumentException($"'{state}' takes no evidence", nameof(state), exception);
    }
  }

  private void Update(PairKey pair, Action<List<FixtureGate>> change) {
    var path = this.PathOf(pair);
    lock (_locks.GetOrAdd(path, _ => new())) {
      var before = File.Exists(path) ? GateRecordFile.Read(path) : null;
      var fixtures = before is null ? [] : Fixtures(path, pair, before);
      change(fixtures);
      var after = GateRecordFile.Serialize(pair, this.Commit, fixtures);
      if (after == before || (before is null && fixtures.Count == 0))
        return;

      System.IO.Directory.CreateDirectory(this.Directory);
      GateRecordFile.Write(path, after);
    }
  }

  private static List<FixtureGate> Fixtures(string path, PairKey pair, string text) {
    var record = GateRecordFile.Parse(text, out var errors) ?? throw new InvalidDataException($"'{path}' is no gate record: {string.Join("; ", errors)}");
    if (record.Pair != pair)
      throw new InvalidDataException($"'{path}' records {record.Pair}, not {pair}");

    return [.. record.Fixtures.Select(fixture => new FixtureGate(fixture.Fixture, [.. fixture.Evidence.Select(evidence => GateStates.TryParse(evidence.State, out var state)
      ? new GateEvidence(state, evidence.Kind, evidence.Path, evidence.Sha256)
      : throw new InvalidDataException($"'{path}': fixture '{fixture.Fixture}' has the unknown state '{evidence.State}'"))]))];
  }

  [GeneratedRegex("^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
  private static partial Regex Sha256();

}
