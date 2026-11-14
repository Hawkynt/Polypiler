using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Emulators;

/// <summary>
/// The <c>emulator</c> oracle of a Commodore BASIC program: the PRG run on VICE once per vector. A
/// vector's input is the keyboard input as ASCII text (empty for none); its output is the screen text
/// of <see cref="ViceResult.ScreenText"/> as ASCII. A run that does not return to BASIC fails the oracle.
/// </summary>
/// <param name="id">The oracle's name in a diagnostic.</param>
/// <param name="runner">The emulator runner.</param>
/// <param name="machine">The machine the program runs on.</param>
/// <param name="program">The PRG file.</param>
/// <param name="cycleLimit">The cycle limit of each run.</param>
public sealed class ViceOracle(string id, ViceRunner runner, CbmMachine machine, string program, long cycleLimit) : IVerificationOracle {

  /// <summary>The producer every VICE reference carries.</summary>
  public const string ProducerName = "vice";

  private readonly ViceRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
  private readonly string _program = program ?? throw new ArgumentNullException(nameof(program));

  /// <inheritdoc />
  public string Id { get; } = id ?? throw new ArgumentNullException(nameof(id));

  /// <inheritdoc />
  public OracleKind Kind => OracleKind.Emulator;

  /// <inheritdoc />
  public string Producer => ProducerName;

  /// <summary>The PRG file.</summary>
  public string? ReferenceFile => this._program;

  /// <summary>Where the program came from; untrusted unless given.</summary>
  public TrustLevel Trust { get; init; } = TrustLevel.Untrusted;

  /// <inheritdoc />
  public async Task<OracleResult> EvaluateAsync(OracleRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    if (!File.Exists(this._program))
      return OracleResult.Failed(this.Id, request.FixtureId, $"the program '{this._program}' does not exist");

    var prg = await File.ReadAllBytesAsync(this._program, cancellationToken).ConfigureAwait(false);
    var outputs = new List<byte[]>();
    foreach (var vector in request.Vectors) {
      var input = Encoding.ASCII.GetString(vector.Input);
      ViceRequest run;
      try {
        run = new(machine, prg, input.Length == 0 ? null : input, [], cycleLimit) { Name = Path.GetFileName(this._program), RequireReturn = true, Trust = this.Trust };
      } catch (ArgumentException exception) {
        return OracleResult.Failed(this.Id, request.FixtureId, $"vector '{vector.Name}': {exception.Message}");
      }

      var result = await this._runner.RunAsync(run, cancellationToken).ConfigureAwait(false);
      if (!result.Succeeded)
        return OracleResult.Failed(this.Id, request.FixtureId, $"vector '{vector.Name}': {EmulatorDiagnostics.Describe(result.Diagnostics)}");

      outputs.Add(Encoding.ASCII.GetBytes(result.ScreenText));
    }

    return new(outputs, []);
  }

}
