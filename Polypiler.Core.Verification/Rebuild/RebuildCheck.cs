using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Rebuild;

/// <summary>The diagnostics of the rebuild check, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class RebuildDiagnostics {

  /// <summary>
  /// <c>PLY0036</c>: two runs of a generating command wrote different trees; one per difference. Argument:
  /// <c>'&lt;path&gt;' differs at byte &lt;offset&gt;</c> or <c>'&lt;path&gt;' exists only in run &lt;n&gt;</c>.
  /// </summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NotDeterministic = new("PLY0036", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "rebuild not deterministic: {0}", "rebuild.determinism");

  /// <summary>The diagnostic of one difference between run 1 and a later run.</summary>
  /// <param name="difference">The difference.</param>
  /// <param name="run">The later run, 2 or more.</param>
  public static Diagnostic Difference(TreeDifference difference, int run) {
    ArgumentNullException.ThrowIfNull(difference);
    return Diagnostic.Create(NotDeterministic, null, difference.Kind switch {
      DifferenceKind.Content => $"'{difference.Path}' differs at byte {difference.Offset}",
      DifferenceKind.OnlyInFirst => $"'{difference.Path}' exists only in run 1",
      _ => $"'{difference.Path}' exists only in run {run}"
    });
  }

}

/// <summary>What the rebuild check runs.</summary>
/// <param name="Output">The output directory, relative to each run's working directory.</param>
/// <param name="Command">The command; <see cref="RebuildCheck.OutputPlaceholder"/> in an argument is replaced by <paramref name="Output"/>.</param>
/// <param name="Runs">How often the command runs, at least 2.</param>
public sealed record RebuildRequest(string Output, ProcessSpec Command, int Runs = 2) {

  /// <summary>The output directory, relative to each run's working directory.</summary>
  /// <exception cref="ArgumentException">The directory is empty, absolute or contains a <c>..</c> segment.</exception>
  public string Output { get; } = RunPlan.IsRelativeName(Output ?? "") ? Output!.Replace('\\', '/').TrimEnd('/') : throw new ArgumentException($"the output directory '{Output}' must be relative, without '..'", nameof(Output));

  /// <summary>The command.</summary>
  public ProcessSpec Command { get; } = Command ?? throw new ArgumentNullException(nameof(Command));

  /// <summary>How often the command runs.</summary>
  /// <exception cref="ArgumentOutOfRangeException">Fewer than <see cref="RebuildCheck.MinimumRuns"/>.</exception>
  public int Runs { get; } = Runs >= RebuildCheck.MinimumRuns ? Runs : throw new ArgumentOutOfRangeException(nameof(Runs), Runs, $"a rebuild check needs at least {RebuildCheck.MinimumRuns} runs");

  /// <summary>The files every run's working directory starts with, by name relative to it. Empty unless given.</summary>
  public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Inputs { get; init; } = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);

  /// <summary>The limits of each run; the runner's defaults unless given.</summary>
  public RunLimits Limits { get; init; } = RunLimits.Default;

  /// <summary>What is exempted from the comparison; nothing unless given.</summary>
  public RebuildIgnore Ignore { get; init; } = RebuildIgnore.Empty;

}

/// <summary>What the rebuild check found.</summary>
/// <param name="Trees">The digest of every run that completed, run 1 first; empty when a run did not complete.</param>
/// <param name="Diagnostics">
/// <c>PLY0036</c> for every difference between run 1 and a later run; or, when a run did not complete,
/// the runner's diagnostics of that run, and nothing was compared.
/// </param>
public sealed record RebuildResult(IReadOnlyList<TreeDigest> Trees, IReadOnlyList<Diagnostic> Diagnostics) {

  /// <summary>Whether every run wrote the same tree.</summary>
  public bool Deterministic => this.Trees.Count > 0 && this.Diagnostics.Count == 0;

}

/// <summary>
/// The rebuild check (PRD §13.1, §23.3): runs a generating command several times through the
/// cross-language runner, each time in a fresh working directory with a scrubbed environment, hashes
/// the output tree of each run and compares run 1 with every later run byte for byte. A run that does
/// not complete ends the check before anything is compared; the check never retries, never modifies
/// the outputs and reports every difference, not only the first.
/// </summary>
/// <param name="runner">The runner.</param>
public sealed class RebuildCheck(ICrossLanguageRunner runner) {

  /// <summary>The text in an argument that stands for the output directory.</summary>
  public const string OutputPlaceholder = "{output}";

  /// <summary>The fewest runs a check makes.</summary>
  public const int MinimumRuns = 2;

  /// <summary>Runs the check.</summary>
  /// <param name="request">What to run.</param>
  /// <param name="cancellationToken">Ends the check.</param>
  /// <returns>The trees and the differences, or why a run did not complete.</returns>
  public async Task<RebuildResult> RunAsync(RebuildRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    var arguments = request.Command.Arguments.Select(argument => argument.Replace(OutputPlaceholder, request.Output, StringComparison.Ordinal)).ToArray();
    var command = new ProcessSpec(request.Command.Toolchain, arguments, request.Command.Stdin) { Environment = request.Command.Environment };
    var plan = new RunPlan(request.Inputs, [], command, request.Limits, TrustLevel.Trusted) { Collect = [request.Output] };
    var prefix = request.Output + "/";
    var trees = new List<TreeDigest>();
    for (var run = 1; run <= request.Runs; ++run) {
      var result = await runner.RunAsync(plan, cancellationToken).ConfigureAwait(false);
      if (result.Outcome != RunOutcome.Completed)
        return new([], result.Diagnostics);
      if (result.ExitCode != 0) {
        var cause = string.Join('\n', new[] { result.Stderr, result.Stdout }.Select(bytes => Encoding.UTF8.GetString(bytes.Span).Trim()).Where(part => part.Length > 0));
        return new([], [Diagnostic.Create(RunnerDiagnostics.BuildFailed, null, command, result.ExitCode) with { Cause = cause.Length > 0 ? cause : "the command printed nothing" }]);
      }

      var files = result.Collected.Where(file => file.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(file => file.Key[prefix.Length..], file => file.Value, StringComparer.Ordinal);
      trees.Add(TreeHash.Compute(files, request.Ignore));
    }

    var diagnostics = new List<Diagnostic>();
    for (var run = 2; run <= trees.Count; ++run)
      diagnostics.AddRange(TreeComparison.Compare(trees[0], trees[run - 1]).Select(difference => RebuildDiagnostics.Difference(difference, run)));

    return new(trees, diagnostics);
  }

}
