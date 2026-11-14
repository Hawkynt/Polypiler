using System;

namespace Polypiler.Core.Verification.Manifests;

/// <summary>
/// The only way a manifest reaches a state (PRD §10.2): <c>generated</c> always; <c>built</c> with a
/// build of exit code 0; <c>executed</c> when built and an execution of exit code 0 is recorded;
/// <c>verified</c> when executed and a verification compared at least one vector, passed all of them,
/// failed none, and was produced by something other than Polypiler. The state is the highest whose
/// conditions hold; the absence of failures where nothing ran never counts.
/// </summary>
public static class ManifestStateRule {

  /// <summary>The producer that may never verify its own output.</summary>
  public const string SelfProducer = "polypiler";

  /// <summary>The highest state the evidence supports, and what keeps it from the next one.</summary>
  /// <param name="build">The build, or <see langword="null"/>.</param>
  /// <param name="execution">The execution, or <see langword="null"/>.</param>
  /// <param name="verification">The verification, or <see langword="null"/>.</param>
  /// <returns>The state, and why the next state is not reached; empty for <see cref="ManifestState.Verified"/>.</returns>
  public static (ManifestState State, string Missing) Derive(ManifestBuild? build, ManifestExecution? execution, ManifestVerification? verification) {
    if (build is null)
      return (ManifestState.Generated, "no build is recorded");
    if (build.ExitCode != 0)
      return (ManifestState.Generated, $"the build exited with {build.ExitCode}");
    if (execution is null)
      return (ManifestState.Built, "no execution is recorded");
    if (execution.ExitCode != 0)
      return (ManifestState.Built, $"the execution exited with {execution.ExitCode}");
    if (verification is null)
      return (ManifestState.Executed, "no verification results are recorded");
    if (verification.Total < 1)
      return (ManifestState.Executed, "the verification compared no vector");
    if (verification.Failed != 0 || verification.Passed != verification.Total)
      return (ManifestState.Executed, $"the verification passed {verification.Passed} of {verification.Total} with {verification.Failed} failed");
    if (string.Equals(verification.Producer, SelfProducer, StringComparison.Ordinal))
      return (ManifestState.Executed, $"the expected values were produced by {SelfProducer} itself");

    return (ManifestState.Verified, "");
  }

  /// <summary>The name of a state as the manifest writes it.</summary>
  /// <param name="state">The state.</param>
  /// <returns><c>generated</c>, <c>built</c>, <c>executed</c> or <c>verified</c>.</returns>
  public static string Name(ManifestState state) => state switch {
    ManifestState.Generated => "generated",
    ManifestState.Built => "built",
    ManifestState.Executed => "executed",
    _ => "verified"
  };

  /// <summary>The state of a name the schema admits.</summary>
  /// <param name="name"><c>generated</c>, <c>built</c>, <c>executed</c> or <c>verified</c>.</param>
  /// <returns>The state.</returns>
  /// <exception cref="ArgumentException">The name is none of them.</exception>
  public static ManifestState Parse(string name) => name switch {
    "generated" => ManifestState.Generated,
    "built" => ManifestState.Built,
    "executed" => ManifestState.Executed,
    "verified" => ManifestState.Verified,
    _ => throw new ArgumentException($"'{name}' is not a manifest state", nameof(name))
  };

}
