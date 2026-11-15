using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Polypiler.Core.Verification.Manifests;

namespace Polypiler.Core.Verification.Gates;

/// <summary>
/// The gate records of a directory, each checked against its evidence before it counts: every
/// evidence file exists below the directory above the gate directory and has the recorded SHA-256, its
/// kind is the one its state takes, a manifest is valid and at least in the state the gate state
/// needs, and every state name is known. A record that fails any check is rejected whole with
/// <c>PLY0058</c>, and its pair reads <see cref="GateState.Planned"/>. Loading never changes a file.
/// </summary>
public sealed class GateStore {

  private readonly IReadOnlyDictionary<PairKey, IReadOnlyList<FixtureGate>> _records;

  private GateStore(IReadOnlyDictionary<PairKey, IReadOnlyList<FixtureGate>> records) {
    this._records = records;
    this.Pairs = [.. records.Keys.OrderBy(pair => pair.FileName, StringComparer.Ordinal)];
  }

  /// <summary>The pairs with a valid record, by the name of their file.</summary>
  public IReadOnlyList<PairKey> Pairs { get; }

  /// <summary>Loads the records of a gate directory; a directory that does not exist holds none.</summary>
  /// <param name="directory">The gate directory, for example <c>artifacts/gates</c>.</param>
  /// <param name="sink">Receives a <c>PLY0058</c> for every problem of every rejected record.</param>
  /// <returns>The store of the valid records.</returns>
  /// <exception cref="IOException">A file cannot be read.</exception>
  public static GateStore Load(string directory, IDiagnosticSink sink) {
    ArgumentException.ThrowIfNullOrEmpty(directory);
    ArgumentNullException.ThrowIfNull(sink);
    var records = new Dictionary<PairKey, IReadOnlyList<FixtureGate>>();
    if (!Directory.Exists(directory))
      return new(records);

    var root = Path.GetDirectoryName(Path.GetFullPath(directory))!;
    foreach (var file in Directory.GetFiles(directory, "*" + PairKey.Extension).Order(StringComparer.Ordinal)) {
      var problems = new List<string>();
      var fixtures = Check(file, root, problems, out var pair);
      foreach (var problem in problems)
        sink.Report(GateDiagnostics.Invalid.At(string.Format(CultureInfo.InvariantCulture, GateDiagnostics.Invalid.MessageFormat, file, problem), file));

      if (problems.Count == 0)
        records[pair!] = fixtures!;
    }

    return new(records);
  }

  /// <summary>The state of a pair: the lowest of its fixtures; <see cref="GateState.Planned"/> without a valid record or without fixtures.</summary>
  /// <param name="pair">The pair.</param>
  /// <returns>The state.</returns>
  public GateState StateOf(PairKey pair) {
    ArgumentNullException.ThrowIfNull(pair);
    return GateStates.Lowest(this.FixturesOf(pair).Select(fixture => fixture.State));
  }

  /// <summary>The fixtures of a pair's valid record.</summary>
  /// <param name="pair">The pair.</param>
  /// <returns>The fixtures by ID; empty without a valid record.</returns>
  public IReadOnlyList<FixtureGate> FixturesOf(PairKey pair) {
    ArgumentNullException.ThrowIfNull(pair);
    return this._records.GetValueOrDefault(pair) ?? [];
  }

  /// <summary>Every pair of a baseline whose state is lower here, or that has no valid record here any more.</summary>
  /// <param name="baseline">The store to compare with.</param>
  /// <returns>One regression per such pair, by the name of its file.</returns>
  public IReadOnlyList<GateRegression> CompareWith(GateStore baseline) {
    ArgumentNullException.ThrowIfNull(baseline);
    return [.. baseline.Pairs
      .Select(pair => new GateRegression(pair, baseline.StateOf(pair), this.StateOf(pair)))
      .Where(regression => regression.After < regression.Before || !this._records.ContainsKey(regression.Pair))];
  }

  /// <summary>Reads and checks one record; every problem is added to the list.</summary>
  private static IReadOnlyList<FixtureGate>? Check(string file, string root, List<string> problems, out PairKey? pair) {
    if (!PairKey.TryParseFileName(Path.GetFileName(file), out pair)) {
      problems.Add("the file name is not <from>--<to>--<source-profile>--<target-profile>.json");
      return null;
    }

    var record = GateRecordFile.Parse(GateRecordFile.Read(file), out var errors);
    problems.AddRange(errors);
    if (record is null)
      return null;
    if (record.Pair != pair)
      problems.Add($"the record is of {record.Pair}, the file name of {pair}");

    var fixtures = new List<FixtureGate>();
    foreach (var group in record.Fixtures.GroupBy(fixture => fixture.Fixture, StringComparer.Ordinal)) {
      if (group.Count() > 1)
        problems.Add($"fixture '{group.Key}' is listed {group.Count()} times");

      var evidence = new List<GateEvidence>();
      foreach (var entry in group.SelectMany(fixture => fixture.Evidence)) {
        void Claim(string reason) => problems.Add($"fixture '{group.Key}' claims '{entry.State}': {reason}");
        if (!GateStates.TryParse(entry.State, out var state) || state == GateState.Planned) {
          Claim($"unknown state '{entry.State}'");
          continue;
        }

        if (evidence.Any(other => other.State == state))
          Claim("the state is claimed twice");
        else if (Problem(state, entry, root) is { } reason)
          Claim(reason);
        else
          evidence.Add(new(state, entry.Kind, entry.Path, entry.Sha256));
      }

      fixtures.Add(new(group.Key, evidence));
    }

    return fixtures;
  }

  /// <summary>What is wrong with one piece of evidence, or <see langword="null"/>.</summary>
  private static string? Problem(GateState state, RawEvidence entry, string root) {
    if (entry.Kind != GateStates.KindOf(state))
      return $"evidence kind '{entry.Kind}' not accepted for this state";
    if (!GateRecorder.IsBelowRoot(entry.Path))
      return $"evidence path '{entry.Path}' leaves the artifacts directory";

    var path = Path.Combine(root, entry.Path);
    if (!File.Exists(path))
      return "evidence file missing";
    if (ManifestWriter.Sha256(File.ReadAllBytes(path)) != entry.Sha256)
      return "evidence hash differs";
    if (GateStates.ManifestStateOf(state) is not { } required)
      return null;

    var diagnostics = new DiagnosticBag();
    if (ManifestReader.Read(path, diagnostics) is not { } manifest)
      return $"the manifest is invalid: {diagnostics.Items[0].Message}";

    return manifest.State < required
      ? $"manifest state '{ManifestStateRule.Name(manifest.State)}' is below '{ManifestStateRule.Name(required)}'"
      : null;
  }

}
