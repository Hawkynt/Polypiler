using System;
using System.Collections.Generic;
using System.IO;

namespace Polypiler.Core.Verification.Oracles;

/// <summary>
/// The oracle of each fixture. It holds only independent oracles: one whose producer is Polypiler,
/// or whose reference file lies in a Polypiler output directory, is refused, so a fixture is never
/// verified against a self-check (PRD §23.1).
/// </summary>
public sealed class OracleRegistry {

  /// <summary>The producer name Polypiler's own output carries.</summary>
  public const string SelfProducer = "polypiler";

  /// <summary>The file that marks a Polypiler output directory.</summary>
  public const string OutputManifest = "polypiler.manifest.json";

  private readonly Dictionary<string, IVerificationOracle> _oracles = new(StringComparer.Ordinal);

  /// <summary>Registers the oracle of a fixture, unless it is not independent.</summary>
  /// <param name="fixtureId">The fixture's ID.</param>
  /// <param name="oracle">The oracle.</param>
  /// <returns>Whether it was registered; <see langword="false"/> when Polypiler produced the reference.</returns>
  /// <exception cref="ArgumentException">The fixture already has an oracle.</exception>
  public bool Register(string fixtureId, IVerificationOracle oracle) {
    ArgumentNullException.ThrowIfNull(fixtureId);
    ArgumentNullException.ThrowIfNull(oracle);
    if (!IsIndependent(oracle))
      return false;

    if (!this._oracles.TryAdd(fixtureId, oracle))
      throw new ArgumentException($"the fixture '{fixtureId}' already has the oracle '{this._oracles[fixtureId].Id}'", nameof(fixtureId));

    return true;
  }

  /// <summary>The oracle of a fixture.</summary>
  /// <param name="fixtureId">The fixture's ID.</param>
  /// <returns>The oracle, or <see langword="null"/> when none is registered.</returns>
  public IVerificationOracle? Resolve(string fixtureId) {
    ArgumentNullException.ThrowIfNull(fixtureId);
    return this._oracles.GetValueOrDefault(fixtureId);
  }

  /// <summary>Whether an oracle's reference was made by something other than Polypiler.</summary>
  /// <param name="oracle">The oracle.</param>
  public static bool IsIndependent(IVerificationOracle oracle) {
    ArgumentNullException.ThrowIfNull(oracle);
    if (string.Equals(oracle.Producer, SelfProducer, StringComparison.OrdinalIgnoreCase))
      return false;

    if (oracle.ReferenceFile is not { } file)
      return true;

    for (var directory = Path.GetDirectoryName(Path.GetFullPath(file)); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
      if (File.Exists(Path.Combine(directory, OutputManifest)))
        return false;

    return true;
  }

}
