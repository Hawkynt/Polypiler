using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Core.Verification.Oracles;

namespace Polypiler.Core.Verification.Emulators;

/// <summary>
/// The emulator oracle of a GW-BASIC token file: PC-BASIC runs the program once per vector with the
/// vector's input, read as code page 437 text, on standard input; the reference output is the
/// normalised console output as code page 437 bytes. A run that fails or is cut off by its limit makes
/// the oracle fail; it never yields an empty reference.
/// </summary>
/// <param name="pcBasic">The adapter the runs go through.</param>
/// <param name="program">The token file.</param>
/// <param name="limit">How long each run may take.</param>
public sealed class PcBasicOracle(PcBasic pcBasic, string program, TimeSpan limit) : IVerificationOracle {

  private readonly PcBasic _pcBasic = pcBasic ?? throw new ArgumentNullException(nameof(pcBasic));
  private readonly string _program = program ?? throw new ArgumentNullException(nameof(program));

  /// <inheritdoc />
  public string Id => PcBasic.Toolchain;

  /// <summary>An emulator: PC-BASIC stands in for the GW-BASIC interpreter.</summary>
  public OracleKind Kind => OracleKind.Emulator;

  /// <summary>The toolchain's key, <c>pcbasic</c>: the reference is what PC-BASIC computes.</summary>
  public string Producer => PcBasic.Toolchain;

  /// <summary>The token file.</summary>
  public string? ReferenceFile => this._program;

  /// <summary>How long each run may take.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The limit is not positive.</exception>
  public TimeSpan Limit { get; } = limit > TimeSpan.Zero ? limit : throw new ArgumentOutOfRangeException(nameof(limit), limit, "the limit must be positive");

  /// <inheritdoc />
  public async Task<OracleResult> EvaluateAsync(OracleRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    if (!File.Exists(this._program))
      return OracleResult.Failed(this.Id, request.FixtureId, $"the program '{this._program}' does not exist");

    var bytes = await File.ReadAllBytesAsync(this._program, cancellationToken).ConfigureAwait(false);
    var outputs = new List<byte[]>();
    foreach (var vector in request.Vectors) {
      var run = await this._pcBasic.RunAsync(bytes, PcBasic.CodePage437.GetString(vector.Input), this.Limit, cancellationToken).ConfigureAwait(false);
      if (run.Output is not { } output)
        return OracleResult.Failed(this.Id, request.FixtureId, $"vector '{vector.Name}': {string.Join("; ", run.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"))}");

      outputs.Add(PcBasic.CodePage437.GetBytes(output));
    }

    return new(outputs, []);
  }

}
