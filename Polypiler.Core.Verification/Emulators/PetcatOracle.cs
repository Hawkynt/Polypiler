using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Core.Verification.Oracles;

namespace Polypiler.Core.Verification.Emulators;

/// <summary>
/// The oracle of a Commodore BASIC token codec: petcat's normalised listing of each vector's input,
/// a PRG, as ASCII. Its kind is <see cref="OracleKind.Emulator"/>, the kind of every reference of
/// the Commodore BASIC run adapter.
/// </summary>
/// <param name="id">The oracle's name in a diagnostic.</param>
/// <param name="petcat">The lister.</param>
/// <param name="dialect">The dialect the PRGs are listed in.</param>
public sealed class PetcatOracle(string id, Petcat petcat, string dialect) : IVerificationOracle {

  private readonly Petcat _petcat = petcat ?? throw new ArgumentNullException(nameof(petcat));
  private readonly string _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));

  /// <inheritdoc />
  public string Id { get; } = id ?? throw new ArgumentNullException(nameof(id));

  /// <inheritdoc />
  public OracleKind Kind => OracleKind.Emulator;

  /// <inheritdoc />
  public string Producer => Petcat.Toolchain;

  /// <summary>None: the programs are the vectors.</summary>
  public string? ReferenceFile => null;

  /// <inheritdoc />
  public async Task<OracleResult> EvaluateAsync(OracleRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    var outputs = new List<byte[]>();
    foreach (var vector in request.Vectors) {
      var listing = await this._petcat.DetokenizeAsync(vector.Input, this._dialect, cancellationToken).ConfigureAwait(false);
      if (!listing.Succeeded)
        return OracleResult.Failed(this.Id, request.FixtureId, $"vector '{vector.Name}': {EmulatorDiagnostics.Describe(listing.Diagnostics)}");

      outputs.Add(Encoding.ASCII.GetBytes(listing.Listing));
    }

    return new(outputs, []);
  }

}
