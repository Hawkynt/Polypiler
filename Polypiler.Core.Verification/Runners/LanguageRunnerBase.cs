using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Runners;

/// <summary>Implements the function-driver protocol, differential comparison and vector recording.</summary>
public abstract class LanguageRunnerBase : ILanguageRunner {

  /// <inheritdoc />
  public abstract string Language { get; }

  /// <inheritdoc />
  public abstract IReadOnlyList<string> Toolchains { get; }

  /// <inheritdoc />
  public abstract CompileResult Compile(RunnerInput input);

  /// <inheritdoc />
  public abstract RunResult Execute(CompileResult compiled, IReadOnlyList<string> arguments);

  /// <inheritdoc />
  public abstract TestReport RunTests(string projectDirectory);

  /// <summary>Runs the language-specific driver with the encoded call images at the supplied trust level.</summary>
  protected abstract RunResult RunDriver(string directory, string export, CallSignature signature, ReadOnlyMemory<byte> inputs, TrustLevel trust);

  /// <inheritdoc />
  public IReadOnlyList<CallOutcome> ExecuteFunction(string directory, string export, CallSignature signature, IReadOnlyList<byte[]> inputImages)
    => this.ExecuteFunction(directory, export, signature, inputImages, TrustLevel.Untrusted);

  private IReadOnlyList<CallOutcome> ExecuteFunction(string directory, string export, CallSignature signature, IReadOnlyList<byte[]> inputImages, TrustLevel trust) {
    var encoded = LanguageRunnerProtocol.Write(signature, inputImages);
    var run = this.RunDriver(directory, export, signature, encoded, trust);
    if (run.Outcome != RunOutcome.Completed || run.ExitCode != 0)
      throw new LanguageRunnerException($"the {this.Language} driver ended with {run.Outcome} (exit code {run.ExitCode})", run.Diagnostics);

    try {
      return LanguageRunnerProtocol.Read(run.Stdout.Span, signature, inputImages.Count);
    } catch (ImageException exception) when (this.ProtocolError(exception.Message) is { } diagnostic) {
      throw new LanguageRunnerException($"the {this.Language} driver output does not follow the protocol: {exception.Message}", [diagnostic]);
    }
  }

  /// <summary>
  /// The runner's diagnostic of driver output that does not follow the protocol (a line that is neither
  /// an image nor a trap line, a wrong line count, an image that does not fit the signature); the
  /// operation then fails with it. <see langword="null"/>, the default, lets the <see cref="ImageException"/> through.
  /// </summary>
  /// <param name="reason">What is wrong with the output.</param>
  protected virtual Diagnostic? ProtocolError(string reason) => null;

  /// <inheritdoc />
  public ComparisonReport RunVectors(RunnerInput input, VectorFile vectors) {
    CheckContract(input, vectors);
    if (vectors.Vectors.Any(vector => vector.Expected is null && vector.ExpectedTrap is null))
      throw new ArgumentException("every vector must have an expected output or trap", nameof(vectors));

    var actual = this.RunCalls(input, vectors);
    var outcomes = new VectorOutcome[actual.Count];
    for (var index = 0; index < outcomes.Length; ++index) {
      var vector = vectors.Vectors[index];
      var call = actual[index];
      if (vector.Expected is { } expected && call.Image is { } image) {
        outcomes[index] = DifferentialComparer.CompareOne(vector.Name, expected, image, vectors.Exactness);
      } else {
        var matches = vector.ExpectedTrap is not null && vector.ExpectedTrap == call.Trap;
        outcomes[index] = new(vector.Name, matches, matches ? null : 0,
          Describe(vector.Expected, vector.ExpectedTrap), Describe(call.Image, call.Trap), null);
      }
    }

    return new(vectors.Vectors.Count, outcomes.Count(outcome => outcome.Passed), outcomes) { Policy = vectors.Exactness };
  }

  /// <inheritdoc />
  public VectorFile Record(RunnerInput input, VectorFile vectors) {
    CheckContract(input, vectors);
    if (vectors.Vectors.Any(vector => vector.Expected is not null || vector.ExpectedTrap is not null))
      throw new InvalidOperationException("recording requires vectors without expectations");

    var toolchain = this.Toolchains.First();
    var actual = this.RunCalls(input, vectors);
    var recorded = vectors.Vectors.Select((vector, index) =>
      new TestVector(vector.Name, vector.Input, actual[index].Image) { ExpectedTrap = actual[index].Trap }).ToArray();
    return vectors.WithRecordedVectors(new VectorProvenance("reference-run", toolchain, input.Export, toolchain), recorded);
  }

  private IReadOnlyList<CallOutcome> RunCalls(RunnerInput input, VectorFile vectors) {
    var built = this.Compile(input);
    if (built.Outcome != RunOutcome.Completed)
      throw new LanguageRunnerException($"the {this.Language} build ended with {built.Outcome}", built.Diagnostics);

    return this.ExecuteFunction(built.Directory, input.Export, input.Signature, vectors.Vectors.Select(vector => vector.Input).ToArray(), input.Trust);
  }

  private static void CheckContract(RunnerInput input, VectorFile vectors) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(vectors);
    if (vectors.Signature is not { } signature)
      throw new ImageException("a vector file used by a language runner requires a call signature");

    if (vectors.Export != input.Export ||
        !Equals(signature.Result, input.Signature.Result) || !signature.Parameters.SequenceEqual(input.Signature.Parameters))
      throw new ArgumentException("the vector export and signature must match the runner input", nameof(vectors));
  }

  private static string Describe(byte[]? image, TrapClass? trap) => trap is null ? Hex.Format(image) : $"!{trap}";

}

/// <summary>A build or driver execution that failed before producing call outcomes.</summary>
public sealed class LanguageRunnerException(string message, IReadOnlyList<Diagnostic> diagnostics) : Exception(message) {

  /// <summary>The diagnostics reported by the build or driver runner.</summary>
  public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;

}
