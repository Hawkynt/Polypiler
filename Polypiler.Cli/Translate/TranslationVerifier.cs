using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Cli;

/// <summary>What <c>translate --verify</c> checks generated JavaScript against.</summary>
/// <param name="Fixture">The fixture the diagnostics name: the vector file's, or the input as typed without one.</param>
/// <param name="Vectors">The vector file; <see langword="null"/> when none was given.</param>
/// <param name="Export">The function the driver calls.</param>
public sealed record VerificationRequest(string Fixture, VectorFile? Vectors, string Export);

/// <summary>
/// The steps of <c>translate --verify</c> after emission (PRD §10.1): build, execute and compare,
/// each through the cross-language runner and the oracle framework, the manifest rewritten after each
/// step with the evidence the step earned and never ahead of it. A build that fails leaves the state
/// <c>generated</c>, an execution that fails <c>built</c>, a comparison that fails or compared nothing
/// <c>executed</c>; only a comparison of every vector with an independent oracle reaches <c>verified</c>.
/// </summary>
/// <param name="runner">Builds and runs the generated code.</param>
public sealed class TranslationVerifier(ICrossLanguageRunner runner) {

  /// <summary>The toolchain the generated JavaScript is built and run with.</summary>
  public const string Toolchain = "node";

  /// <summary>The verifier of this machine: <c>node</c> from the search path, in the sandbox of the host.</summary>
  public static TranslationVerifier ForHost() {
    var toolchains = PathToolchainResolver.ForHost();
    return new(new CrossLanguageRunner(SandboxFactory.ForHost(toolchains), toolchains));
  }

  /// <summary>Builds, runs and compares a generated module, rewriting the manifest after each step.</summary>
  /// <param name="request">What to compare with.</param>
  /// <param name="manifest">The manifest emission wrote, in the state <c>generated</c>.</param>
  /// <param name="files">The generated files by their paths in the output directory; the first is the module.</param>
  /// <param name="directory">The output directory, as resolved.</param>
  /// <param name="shown">The output directory as typed, which the summary line names.</param>
  /// <param name="context">Receives the diagnostics and the summary line.</param>
  /// <returns>0 when verified; otherwise a non-zero code, the diagnostics deciding the process's exit code.</returns>
  public int Run(VerificationRequest request, Manifest manifest, IReadOnlyList<(string Path, byte[] Bytes)> files, string directory, string shown, CliContext context) {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(manifest);
    ArgumentNullException.ThrowIfNull(files);
    ArgumentNullException.ThrowIfNull(context);
    // The JavaScript backend writes one module; the helpers it needs are inside it.
    var module = files[0].Path;
    var vectors = request.Vectors?.Vectors ?? [];
    var driver = JavaScriptDriver.Script(module, request.Export, request.Vectors?.Signature ?? new([], null), vectors);
    var planFiles = files.ToDictionary(file => file.Path, file => (ReadOnlyMemory<byte>)file.Bytes, StringComparer.Ordinal);
    planFiles[JavaScriptDriver.FileName] = Encoding.UTF8.GetBytes(driver);
    var build = new ProcessSpec(Toolchain, "--check", module);
    var execute = new ProcessSpec(Toolchain, JavaScriptDriver.FileName);
    var result = runner.RunAsync(new RunPlan(planFiles, [build], execute, RunLimits.Default, TrustLevel.Trusted)).GetAwaiter().GetResult();
    foreach (var diagnostic in result.Diagnostics)
      context.Report(diagnostic);

    if (result.Outcome is not (RunOutcome.Completed or RunOutcome.BuildFailed))
      return Summary(manifest, shown, context);

    manifest = manifest with { Build = new(Toolchain, build.ToString(), result.Outcome == RunOutcome.BuildFailed ? result.ExitCode : 0) };
    ManifestWriter.Write(manifest, directory);
    if (result.Outcome == RunOutcome.BuildFailed)
      return Summary(manifest, shown, context);

    manifest = manifest with { Execution = new(execute.ToString(), result.ExitCode) };
    ManifestWriter.Write(manifest, directory);
    if (vectors.Count == 0) {
      context.Report(Diagnostic.Create(OracleDiagnostics.NoIndependentOracle, null, request.Fixture));
      return Summary(manifest, shown, context);
    }

    var file = request.Vectors!;
    // A driver that did not end normally has no outputs to trust: every vector then fails.
    var actual = JavaScriptDriver.Outputs(file.Signature!, result.ExitCode == 0 ? Encoding.UTF8.GetString(result.Stdout.Span) : "", vectors.Count);
    var oracle = new VectorFileOracle(file);
    var registry = new OracleRegistry();
    registry.Register(request.Fixture, oracle);
    var verification = OracleVerification.VerifyAsync(registry, request.Fixture, vectors, actual, file.Exactness).GetAwaiter().GetResult();
    foreach (var diagnostic in verification.Diagnostics)
      context.Report(diagnostic);

    var report = verification.Report;
    if (report.Outcomes.Count == vectors.Count) {
      manifest = manifest with { Verification = Record(oracle, report, manifest.FloatingPoint?.Mode ?? "accurate") };
      ManifestWriter.Write(manifest, directory);
    }

    return Summary(manifest, shown, context);
  }

  /// <summary>The verification record of a comparison under the exact policy.</summary>
  private static ManifestVerification Record(VectorFileOracle oracle, ComparisonReport report, string profile) {
    var identical = report.Outcomes.Count(outcome => outcome.Passed && outcome.Identical);
    var exactness = new ManifestExactness(profile, "exact", ExactnessPolicy.Spelling(report.Policy!.SpecialValues), [], [], new(identical, report.Passed - identical, 0, null));
    return new(oracle.Id, oracle.Producer, exactness, report.Total, report.Passed, report.Total - report.Passed);
  }

  /// <summary>
  /// Writes the summary line, <c>&lt;output&gt;: &lt;state&gt;</c> with <c>(&lt;passed&gt; of
  /// &lt;total&gt; vectors)</c> once verified, and says whether the run is verified.
  /// </summary>
  private static int Summary(Manifest manifest, string shown, CliContext context) {
    var state = manifest.State;
    var counts = state == ManifestState.Verified ? $" ({manifest.Verification!.Passed} of {manifest.Verification.Total} vectors)" : "";
    context.Output.WriteLine($"{shown}: {ManifestStateRule.Name(state)}{counts}");
    return state == ManifestState.Verified ? 0 : 1;
  }

}
