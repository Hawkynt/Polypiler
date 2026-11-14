using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Oracles;

/// <summary>What running a program on every vector produced.</summary>
/// <param name="Outputs">One output per vector, in vector order; empty when the program failed.</param>
/// <param name="Failure">Why the program failed, or <see langword="null"/>.</param>
public sealed record ProgramRun(IReadOnlyList<byte[]> Outputs, string? Failure);

/// <summary>
/// A program file run through the cross-language runner, once per vector: it reads the vector's
/// input as hexadecimal text on standard input and prints its output as hexadecimal text, surrounding
/// white space ignored. The reference program of an oracle and a candidate are run the same way.
/// </summary>
/// <param name="Runner">The runner every run goes through.</param>
/// <param name="Toolchain">The key of the toolchain that runs the program, from <c>tooling/toolchains.lock</c>.</param>
/// <param name="Program">The program file; it is copied into the run's directory.</param>
/// <param name="Limits">The limits of each run.</param>
/// <param name="Trust">Where the program came from; a program from outside the repository is untrusted.</param>
public sealed record ToolchainProgram(ICrossLanguageRunner Runner, string Toolchain, string Program, RunLimits Limits, TrustLevel Trust) {

  /// <summary>The arguments before the program's name: an emulator's options, for example.</summary>
  public IReadOnlyList<string> Arguments { get; init; } = [];

  /// <summary>Runs the program on every vector, in order, and stops at the first run that fails.</summary>
  /// <param name="vectors">The vectors.</param>
  /// <param name="cancellationToken">Ends the runs.</param>
  /// <returns>The outputs, or why there are none.</returns>
  public async Task<ProgramRun> RunAsync(IReadOnlyList<TestVector> vectors, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(vectors);
    if (!File.Exists(this.Program))
      return new([], $"the program '{this.Program}' does not exist");

    var name = Path.GetFileName(this.Program);
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [name] = await File.ReadAllBytesAsync(this.Program, cancellationToken).ConfigureAwait(false) };
    var outputs = new List<byte[]>();
    foreach (var vector in vectors) {
      var execute = new ProcessSpec(this.Toolchain, [.. this.Arguments, name], Encoding.ASCII.GetBytes(Hex.Format(vector.Input)));
      var result = await this.Runner.RunAsync(new RunPlan(files, [], execute, this.Limits, this.Trust), cancellationToken).ConfigureAwait(false);
      if (result.Outcome != RunOutcome.Completed)
        return new([], $"vector '{vector.Name}': {string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"))}");

      if (result.ExitCode != 0)
        return new([], $"vector '{vector.Name}': '{execute}' exited with {result.ExitCode}: {Text(result.Stderr)}");

      var text = Encoding.ASCII.GetString(result.Stdout.Span).Trim();
      if (Hex.TryParse(text) is not { } output)
        return new([], $"vector '{vector.Name}': the output '{Text(result.Stdout)}' is not hexadecimal");

      outputs.Add(output);
    }

    return new(outputs, null);
  }

  private static string Text(ReadOnlyMemory<byte> bytes) {
    var text = Encoding.UTF8.GetString(bytes.Span).Trim();
    return text.Length <= 200 ? text : text[..200] + "...";
  }

}

/// <summary>
/// The <c>source-toolchain</c> and <c>emulator</c> oracles: the original program run by the
/// toolchain or emulator it was written for, through the cross-language runner.
/// </summary>
/// <param name="id">The oracle's name in a diagnostic.</param>
/// <param name="kind"><see cref="OracleKind.SourceToolchain"/> or <see cref="OracleKind.Emulator"/>.</param>
/// <param name="program">The program and how it is run.</param>
public sealed class ToolchainOracle(string id, OracleKind kind, ToolchainProgram program) : IVerificationOracle {

  private readonly ToolchainProgram _program = program ?? throw new ArgumentNullException(nameof(program));

  /// <inheritdoc />
  public string Id { get; } = id ?? throw new ArgumentNullException(nameof(id));

  /// <inheritdoc />
  public OracleKind Kind { get; } = kind is OracleKind.SourceToolchain or OracleKind.Emulator ? kind : throw new ArgumentOutOfRangeException(nameof(kind), kind, "a toolchain oracle is a source toolchain or an emulator");

  /// <summary>The toolchain's key: the reference is what that toolchain computes.</summary>
  public string Producer => this._program.Toolchain;

  /// <summary>The program.</summary>
  public string? ReferenceFile => this._program.Program;

  /// <inheritdoc />
  public async Task<OracleResult> EvaluateAsync(OracleRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    var run = await this._program.RunAsync(request.Vectors, cancellationToken).ConfigureAwait(false);
    return run.Failure is { } failure ? OracleResult.Failed(this.Id, request.FixtureId, failure) : new(run.Outputs, []);
  }

}
