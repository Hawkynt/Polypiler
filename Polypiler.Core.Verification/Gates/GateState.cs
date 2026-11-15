using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Verification.Manifests;

namespace Polypiler.Core.Verification.Gates;

/// <summary>
/// How far a fixture of a language or CPU pair got, in the order of PRD §23.6. <see cref="Planned"/>
/// means that there is no evidence; each other state needs evidence of its own kind, and a pair is
/// advertised at the lowest state among its fixtures.
/// </summary>
public enum GateState {

  /// <summary>No evidence.</summary>
  Planned = 0,

  /// <summary>The frontend read the input without an error.</summary>
  Parsed,

  /// <summary>The input became IR that passed the verifier.</summary>
  Lifted,

  /// <summary>At least one pattern was recognized; optional.</summary>
  Recognized,

  /// <summary>Every applied rewrite has an accepted proof class; optional, and only after <see cref="Recognized"/>.</summary>
  Proved,

  /// <summary>The IR is legal for the target.</summary>
  Legalized,

  /// <summary>The target code was written.</summary>
  Emitted,

  /// <summary>The target code was built.</summary>
  Compiled,

  /// <summary>The target code ran.</summary>
  Executed,

  /// <summary>The target code agreed with an independent oracle.</summary>
  Verified,

  /// <summary>The target code met its benchmark.</summary>
  PerformanceValidated

}

/// <summary>The names of the gate states, the evidence each one needs and the rules that compute a state from evidence.</summary>
public static class GateStates {

  /// <summary>The evidence kind of a frontend result.</summary>
  public const string FrontendLog = "frontend-log";

  /// <summary>The evidence kind of an IR dump.</summary>
  public const string IrDump = "ir-dump";

  /// <summary>The evidence kind of an optimization report.</summary>
  public const string OptReport = "opt-report";

  /// <summary>The evidence kind of a proof report.</summary>
  public const string ProofReport = "proof-report";

  /// <summary>The evidence kind of an output manifest.</summary>
  public const string Manifest = "manifest";

  /// <summary>The evidence kind of a benchmark result.</summary>
  public const string BenchResult = "bench-result";

  private static readonly string[] _names = ["planned", "parsed", "lifted", "recognized", "proved", "legalized", "emitted", "compiled", "executed", "verified", "performance-validated"];

  /// <summary>The ten states that need evidence, in order.</summary>
  public static IReadOnlyList<GateState> Evidenced { get; } = [.. Enum.GetValues<GateState>().Where(state => state != GateState.Planned)];

  /// <summary>The name of a state as the gate record writes it: the lower-case spelling of PRD §23.6.</summary>
  /// <param name="state">The state.</param>
  /// <returns>For example <c>performance-validated</c>.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The value is no state.</exception>
  public static string Name(GateState state)
    => (int)state >= 0 && (int)state < _names.Length ? _names[(int)state] : throw new ArgumentOutOfRangeException(nameof(state), state, "no gate state");

  /// <summary>The state of a name as <see cref="Name"/> writes it.</summary>
  /// <param name="text">The name; the comparison is exact.</param>
  /// <param name="state">The state, or <see cref="GateState.Planned"/> when the name is unknown.</param>
  /// <returns>Whether the name is a state.</returns>
  public static bool TryParse(string? text, out GateState state) {
    var index = Array.IndexOf(_names, text);
    state = index < 0 ? GateState.Planned : (GateState)index;
    return index >= 0;
  }

  /// <summary>Whether a state may be left out of a chain: <see cref="GateState.Recognized"/> and <see cref="GateState.Proved"/>.</summary>
  /// <param name="state">The state.</param>
  /// <returns>Whether it is optional.</returns>
  public static bool IsOptional(GateState state) => state is GateState.Recognized or GateState.Proved;

  /// <summary>The kind of evidence a state accepts.</summary>
  /// <param name="state">A state other than <see cref="GateState.Planned"/>.</param>
  /// <returns>The kind, for example <c>ir-dump</c>.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The state is <see cref="GateState.Planned"/>, which needs no evidence, or no state.</exception>
  public static string KindOf(GateState state) => state switch {
    GateState.Parsed => FrontendLog,
    GateState.Lifted or GateState.Legalized => IrDump,
    GateState.Recognized => OptReport,
    GateState.Proved => ProofReport,
    GateState.Emitted or GateState.Compiled or GateState.Executed or GateState.Verified => Manifest,
    GateState.PerformanceValidated => BenchResult,
    _ => throw new ArgumentOutOfRangeException(nameof(state), state, "the state takes no evidence")
  };

  /// <summary>The lowest manifest state that supports a gate state whose evidence is a manifest.</summary>
  /// <param name="state">The gate state.</param>
  /// <returns>The manifest state, or <see langword="null"/> for a state whose evidence is no manifest.</returns>
  public static ManifestState? ManifestStateOf(GateState state) => state switch {
    GateState.Emitted => ManifestState.Generated,
    GateState.Compiled => ManifestState.Built,
    GateState.Executed => ManifestState.Executed,
    GateState.Verified => ManifestState.Verified,
    _ => null
  };

  /// <summary>
  /// The state of a fixture: the highest state <c>S</c> with evidence such that every mandatory state
  /// up to <c>S</c> has evidence too. <see cref="GateState.Recognized"/> and <see cref="GateState.Proved"/>
  /// may be skipped, and <see cref="GateState.Proved"/> counts only where <see cref="GateState.Recognized"/>
  /// has evidence.
  /// </summary>
  /// <param name="evidenced">The states that have evidence, in any order.</param>
  /// <returns>The state; <see cref="GateState.Planned"/> when the chain does not start with <see cref="GateState.Parsed"/>.</returns>
  public static GateState Reached(IEnumerable<GateState> evidenced) {
    ArgumentNullException.ThrowIfNull(evidenced);
    var present = evidenced.ToHashSet();
    var reached = GateState.Planned;
    foreach (var state in Evidenced) {
      if (present.Contains(state) && (state != GateState.Proved || present.Contains(GateState.Recognized)))
        reached = state;
      else if (!IsOptional(state))
        break;
    }

    return reached;
  }

  /// <summary>The state of a pair: the lowest state among its fixtures, <see cref="GateState.Planned"/> without any.</summary>
  /// <param name="fixtures">The states of the fixtures.</param>
  /// <returns>The state.</returns>
  public static GateState Lowest(IEnumerable<GateState> fixtures) {
    ArgumentNullException.ThrowIfNull(fixtures);
    return fixtures.DefaultIfEmpty(GateState.Planned).Min();
  }

}
