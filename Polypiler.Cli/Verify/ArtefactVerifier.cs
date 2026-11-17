using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Polypiler.Core;
using Polypiler.Core.Verification.Evidence;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Cli.Verify;

/// <summary>What <c>polypiler verify</c> is asked to do.</summary>
/// <param name="Directory">The generated directory, as typed; relative to the verifier's working directory.</param>
/// <param name="Vectors">The vector file of <c>--against</c>, as typed; <see langword="null"/> to use the one the manifest records.</param>
/// <param name="Update">Whether the manifest is rewritten to the state this run reached; <see langword="false"/> for <c>--no-update</c>.</param>
/// <param name="EvidenceDirectory">Where the evidence bundle goes, as typed; <see langword="null"/> for none.</param>
public sealed record VerifyRequest(string Directory, string? Vectors, bool Update, string? EvidenceDirectory);

/// <summary>How far a verification got.</summary>
/// <param name="Reached">The state the evidence of this run supports; the state the manifest claims when the run stopped before building.</param>
/// <param name="Passed">The vectors that passed.</param>
/// <param name="Total">The vectors of the vector file; 0 when it was not read.</param>
public sealed record VerificationOutcome(ManifestState Reached, int Passed, int Total);

/// <summary>
/// Re-establishes from a generated directory and its manifest alone whether the output still builds,
/// runs and matches independent vectors (PRD §1.2, §10), stopping at the first error:
/// <list type="number">
/// <item>reads <c>polypiler.manifest.json</c> (<c>PLY0016</c> when there is none, <c>PLY0053</c> or <c>PLY0054</c> when it is invalid);</item>
/// <item>recomputes the SHA-256 of every output it lists: a missing or edited file is <c>PLY0054</c>, and files it does not list are ignored;</item>
/// <item>reads the vectors of <c>--against</c>, or of the vector file the manifest's <c>verification.oracle</c> records,
/// relative to the directory (<c>PLY0004</c> when the file is missing, <c>PLY0017</c> when it is not a vector file,
/// <c>PLY0063</c> when there is none, it holds no vector, or Polypiler produced its values);</item>
/// <item>builds every JavaScript source output with <c>node --check</c> on the toolchain the manifest's
/// <c>target.runtime</c> names (<c>PLY0021</c> when it is not installed, <c>PLY0030</c> when a check fails);</item>
/// <item>executes every vector through the cross-language runner (<c>PLY0031</c>–<c>PLY0033</c> on a limit):
/// a vector file with an <c>export</c> calls that function through <see cref="JavaScriptDriver"/>, one without
/// runs the first source output as a program that reads the input as hexadecimal text and prints its output;</item>
/// <item>compares the outputs with the oracle of the vector file (<c>PLY0050</c>–<c>PLY0052</c>).</item>
/// </list>
/// Standard output gets <c>  &lt;vector&gt;: pass</c> or <c>  &lt;vector&gt;: FAIL expected &lt;e&gt; actual &lt;a&gt;</c>
/// per vector compared and <c>&lt;directory&gt;: &lt;state&gt; (&lt;passed&gt; of &lt;total&gt; vectors)</c> for every
/// run that got to the build. Unless <see cref="VerifyRequest.Update"/> is off, the manifest's build,
/// execution and verification are then rewritten to what this run found, downwards too; the generated
/// files and the vectors are never written.
/// </summary>
public sealed class ArtefactVerifier {

  /// <summary>The feature name of the evidence bundles this verifier writes.</summary>
  public const string EvidenceFeature = "polypiler verify";

  private static readonly string[] _javaScriptExtensions = [".js", ".mjs", ".cjs"];

  private readonly TextWriter _output;
  private readonly string _workingDirectory;
  private readonly ICrossLanguageRunner _runner;
  private readonly RunLimits _limits;

  /// <summary>Creates a verifier that runs on this host: the toolchains on <c>PATH</c>, in the host's sandbox, under the default limits.</summary>
  /// <param name="output">Standard output.</param>
  /// <param name="workingDirectory">The directory the paths of a request are relative to.</param>
  public ArtefactVerifier(TextWriter output, string workingDirectory) : this(output, workingDirectory, HostRunner(), RunLimits.Default) { }

  /// <summary>Creates a verifier.</summary>
  /// <param name="output">Standard output.</param>
  /// <param name="workingDirectory">The directory the paths of a request are relative to.</param>
  /// <param name="runner">Runs every build and execution.</param>
  /// <param name="limits">The limits of each of them.</param>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public ArtefactVerifier(TextWriter output, string workingDirectory, ICrossLanguageRunner runner, RunLimits limits) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(workingDirectory);
    ArgumentNullException.ThrowIfNull(runner);
    ArgumentNullException.ThrowIfNull(limits);
    this._output = output;
    this._workingDirectory = workingDirectory;
    this._runner = runner;
    this._limits = limits;
  }

  private static CrossLanguageRunner HostRunner() {
    var toolchains = PathToolchainResolver.FromEnvironment();
    return new(SandboxFactory.ForHost(toolchains), toolchains);
  }

  /// <summary>Verifies a generated directory.</summary>
  /// <param name="request">What to verify.</param>
  /// <param name="sink">Receives the diagnostics; the run's exit code is the one they give.</param>
  /// <returns>How far it got.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public VerificationOutcome Run(VerifyRequest request, IDiagnosticSink sink) {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(sink);
    var display = request.Directory;
    var directory = this.Resolve(display);
    if (!Directory.Exists(directory)) {
      sink.Report(Diagnostic.Create(CliDiagnostics.Unreadable, null, string.Format(CultureInfo.InvariantCulture, CliDiagnostics.InputUnreadable, display)));
      return new(ManifestState.Generated, 0, 0);
    }

    var manifestPath = Path.Combine(directory, Manifest.FileName);
    if (!File.Exists(manifestPath)) {
      sink.Report(Diagnostic.Create(VerifyDiagnostics.NoManifest, null, display));
      return new(ManifestState.Generated, 0, 0);
    }

    var manifestDisplay = Within(display, Manifest.FileName);
    var original = File.ReadAllText(manifestPath, Encoding.UTF8);
    if (ManifestReader.Parse(original, manifestDisplay, sink, ManifestSchema.Default) is not { } manifest)
      return new(ManifestState.Generated, 0, 0);

    var claimed = new VerificationOutcome(manifest.State, 0, 0);
    if (!CheckOutputs(manifest, directory, manifestDisplay, sink))
      return claimed;

    if (this.ResolveVectors(request, manifest, directory, display, sink) is not { } vectors)
      return claimed;

    if (manifest.Target.Language != "javascript") {
      sink.Report(Diagnostic.Create(CliDiagnostics.CommandLine, null,
        $"verify runs javascript output; '{display}' holds {manifest.Target.Language} output"));
      return claimed with { Total = vectors.File.Vectors.Count };
    }

    var sources = manifest.Outputs.Where(output => output.Role == ManifestOutputRole.Source && _javaScriptExtensions.Contains(Path.GetExtension(output.Path), StringComparer.OrdinalIgnoreCase)).ToList();
    if (sources.Count == 0) {
      sink.Report(ManifestDiagnostics.Invalid.At(Message(ManifestDiagnostics.Invalid, manifestDisplay, "/outputs", "lists no JavaScript source file"), manifestDisplay));
      return claimed with { Total = vectors.File.Vectors.Count };
    }

    var run = new Session(manifest, directory, sources, vectors, this._runner, this._limits, sink);
    if (!run.Probe())
      return claimed with { Total = vectors.File.Vectors.Count };

    run.BuildExecuteCompare();
    var updated = manifest with { Build = run.Build, Execution = run.Execution, Verification = run.Verification, Toolchains = run.Toolchains };
    if (request.Update)
      ManifestWriter.Write(updated, directory);

    var reached = updated.State;
    foreach (var line in run.VectorLines)
      this._output.WriteLine(line);
    this._output.WriteLine($"{display}: {ManifestStateRule.Name(reached)} ({run.Passed} of {vectors.File.Vectors.Count} vectors)");
    if (request.EvidenceDirectory is { } evidence)
      run.WriteEvidence(this.Resolve(evidence), original, manifestPath, reached == ManifestState.Verified);

    return new(reached, run.Passed, vectors.File.Vectors.Count);
  }

  private string Resolve(string path) => Path.GetFullPath(Path.Combine(this._workingDirectory, path));

  /// <summary>A file of a directory as typed, with <c>/</c> between them.</summary>
  private static string Within(string directory, string file) => directory.TrimEnd('/', '\\') + "/" + file;

  private static string Message(DiagnosticDescriptor descriptor, params object[] arguments)
    => string.Format(CultureInfo.InvariantCulture, descriptor.MessageFormat, arguments);

  /// <summary>Checks that the manifest lists outputs inside the directory and that each has the bytes it records.</summary>
  private static bool CheckOutputs(Manifest manifest, string directory, string manifestDisplay, IDiagnosticSink sink) {
    if (manifest.Outputs.Count == 0) {
      sink.Report(ManifestDiagnostics.Invalid.At(Message(ManifestDiagnostics.Invalid, manifestDisplay, "/outputs", "lists no output file"), manifestDisplay));
      return false;
    }

    var state = ManifestStateRule.Name(manifest.State);
    var intact = true;
    for (var index = 0; index < manifest.Outputs.Count; ++index) {
      var output = manifest.Outputs[index];
      if (!RunPlan.IsRelativeName(output.Path)) {
        sink.Report(ManifestDiagnostics.Invalid.At(Message(ManifestDiagnostics.Invalid, manifestDisplay, $"/outputs/{index}/path", $"'{output.Path}' leaves the output directory"), manifestDisplay));
        intact = false;
        continue;
      }

      var file = Path.Combine(directory, output.Path);
      string? problem = null;
      if (!File.Exists(file))
        problem = $"the output '{output.Path}' is missing";
      else if (ManifestWriter.Sha256(File.ReadAllBytes(file)) is var actual && actual != output.Sha256)
        problem = $"the output '{output.Path}' has the SHA-256 {actual}, the manifest records {output.Sha256}";

      if (problem is not null) {
        sink.Report(ManifestDiagnostics.StateUnsupported.At(Message(ManifestDiagnostics.StateUnsupported, manifestDisplay, state, "none", problem), manifestDisplay));
        intact = false;
      }
    }

    return intact;
  }

  /// <summary>The vector file and its oracle, or <see langword="null"/> after the diagnostic that says why there is none.</summary>
  private ResolvedVectors? ResolveVectors(VerifyRequest request, Manifest manifest, string directory, string display, IDiagnosticSink sink) {
    string shown;
    string path;
    if (request.Vectors is { } against) {
      shown = against;
      path = this.Resolve(against);
    } else if (manifest.Verification?.Oracle is { } reference) {
      shown = Within(display, reference);
      path = Path.GetFullPath(Path.Combine(directory, reference));
    } else {
      sink.Report(Diagnostic.Create(OracleDiagnostics.NoIndependentOracle, null, display));
      return null;
    }

    if (!File.Exists(path)) {
      sink.Report(Diagnostic.Create(CliDiagnostics.Unreadable, null, string.Format(CultureInfo.InvariantCulture, CliDiagnostics.InputUnreadable, shown)));
      return null;
    }

    VectorFile file;
    try {
      file = VectorFile.Parse(File.ReadAllText(path, Encoding.UTF8), path);
    } catch (Exception exception) when (exception is FormatException or ImageException) {
      var reason = exception.Message.Replace(path + ": ", "", StringComparison.Ordinal);
      sink.Report(Diagnostic.Create(VerifyDiagnostics.VectorsNotUnderstood, null, shown, reason));
      return null;
    }

    if (file.Signature is { } signature && JavaScriptDriver.Unsupported(signature) is { } unsupported) {
      sink.Report(Diagnostic.Create(VerifyDiagnostics.VectorsNotUnderstood, null, shown, unsupported));
      return null;
    }

    var registry = new OracleRegistry();
    if (file.Vectors.Count == 0 || !registry.Register(file.Fixture, new VectorFileOracle(file))) {
      sink.Report(Diagnostic.Create(OracleDiagnostics.NoIndependentOracle, null, file.Fixture));
      return null;
    }

    return new(file, registry, ManifestWriter.PortablePath(Path.GetRelativePath(directory, path)));
  }

  /// <summary>A vector file that passed every check, the registry that holds its oracle, and its path relative to the directory.</summary>
  private sealed record ResolvedVectors(VectorFile File, OracleRegistry Registry, string Reference);

  /// <summary>One verification after the checks: the build, the execution and the comparison, and what each recorded.</summary>
  private sealed class Session(Manifest manifest, string directory, IReadOnlyList<ManifestOutput> sources, ResolvedVectors vectors, ICrossLanguageRunner runner, RunLimits limits, IDiagnosticSink sink) {

    private readonly string _toolchain = manifest.Target.Runtime;
    private readonly List<(string Label, RunResult Result)> _processes = [];
    private string _version = "";
    private ComparisonReport? _report;

    public ManifestBuild? Build { get; private set; }

    public ManifestExecution? Execution { get; private set; }

    public ManifestVerification? Verification { get; private set; }

    public IReadOnlyList<ManifestToolchain> Toolchains { get; private set; } = manifest.Toolchains;

    public int Passed => this._report?.Passed ?? 0;

    public List<string> VectorLines { get; } = [];

    private RunResult Start(string label, RunPlan plan) {
      var result = runner.RunAsync(plan).GetAwaiter().GetResult();
      this._processes.Add((label, result));
      return result;
    }

    private RunPlan Plan(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, ProcessSpec execute) => new(files, [], execute, limits, TrustLevel.Trusted);

    /// <summary>Runs <c>&lt;toolchain&gt; --version</c>: whether the toolchain is there, and which version it is.</summary>
    public bool Probe() {
      var probe = this.Start("version", this.Plan(new Dictionary<string, ReadOnlyMemory<byte>>(), new(this._toolchain, "--version")));
      if (probe.Outcome != RunOutcome.Completed || probe.ExitCode != 0) {
        foreach (var diagnostic in probe.Diagnostics.DefaultIfEmpty(Diagnostic.Create(RunnerDiagnostics.BuildFailed, null, $"{this._toolchain} --version", probe.ExitCode)))
          sink.Report(diagnostic);
        return false;
      }

      var version = Encoding.UTF8.GetString(probe.Stdout.Span).Trim().TrimStart('v');
      this._version = version.Length > 0 ? version : "unknown";
      this.Toolchains = [.. manifest.Toolchains.Where(toolchain => toolchain.Key != this._toolchain), new(this._toolchain, this._version)];
      return true;
    }

    public void BuildExecuteCompare() {
      var files = manifest.Outputs.ToDictionary(output => output.Path, output => (ReadOnlyMemory<byte>)File.ReadAllBytes(Path.Combine(directory, output.Path)), StringComparer.Ordinal);
      if (!this.BuildAll(files))
        return;

      var actual = this.Execute(files);
      if (actual is null)
        return;

      this.Compare(actual);
    }

    private bool BuildAll(Dictionary<string, ReadOnlyMemory<byte>> files) {
      var commands = new List<string>();
      for (var index = 0; index < sources.Count; ++index) {
        var check = new ProcessSpec(this._toolchain, "--check", sources[index].Path);
        commands.Add(check.ToString());
        var result = this.Start($"build-{index + 1}", this.Plan(files, check));
        if (result.Outcome != RunOutcome.Completed) {
          Report(result.Diagnostics);
          return false;
        }

        if (result.ExitCode != 0) {
          sink.Report(Diagnostic.Create(RunnerDiagnostics.BuildFailed, null, check, result.ExitCode) with { Cause = Cause(result) });
          this.Build = new(this._toolchain, string.Join("; ", commands), result.ExitCode);
          return false;
        }
      }

      this.Build = new(this._toolchain, string.Join("; ", commands), 0);
      return true;
    }

    /// <summary>Every vector's outcome, or <see langword="null"/> after the diagnostic of a run that failed.</summary>
    private List<DriverOutcome>? Execute(Dictionary<string, ReadOnlyMemory<byte>> files) {
      var entry = sources[0].Path;
      var file = vectors.File;
      if (file.Signature is not { } signature)
        return this.ExecuteProgram(files, entry);

      var withDriver = new Dictionary<string, ReadOnlyMemory<byte>>(files, StringComparer.Ordinal) { [JavaScriptDriver.FileName] = Encoding.UTF8.GetBytes(JavaScriptDriver.Source) };
      var execute = new ProcessSpec(this._toolchain, [JavaScriptDriver.FileName, entry, file.Export!], Encoding.UTF8.GetBytes(JavaScriptDriver.Arguments(signature, file.Vectors)));
      var result = this.Start("execute", this.Plan(withDriver, execute));
      if (!this.Executed(result, execute))
        return null;

      var stdout = Encoding.UTF8.GetString(result.Stdout.Span);
      if (JavaScriptDriver.Outcomes(signature, stdout, file.Vectors.Count) is not { } outcomes) {
        sink.Report(Diagnostic.Create(RunnerDiagnostics.BuildFailed, null, execute, result.ExitCode) with { Cause = $"the driver printed no result line per vector: {Shorten(stdout)}" });
        return null;
      }

      return [.. outcomes];
    }

    private List<DriverOutcome>? ExecuteProgram(Dictionary<string, ReadOnlyMemory<byte>> files, string entry) {
      var outcomes = new List<DriverOutcome>();
      var index = 0;
      foreach (var vector in vectors.File.Vectors) {
        var execute = new ProcessSpec(this._toolchain, [entry], Encoding.ASCII.GetBytes(Hex.Format(vector.Input)));
        var result = this.Start($"execute-{++index}", this.Plan(files, execute));
        if (!this.Executed(result, execute))
          return null;

        var text = Encoding.UTF8.GetString(result.Stdout.Span).Trim();
        outcomes.Add(new(Hex.TryParse(text) ?? Encoding.UTF8.GetBytes(text), null));
      }

      return outcomes;
    }

    /// <summary>Records an execution; whether it ran to its end with exit code 0.</summary>
    private bool Executed(RunResult result, ProcessSpec execute) {
      if (result.Outcome != RunOutcome.Completed) {
        Report(result.Diagnostics);
        return false;
      }

      this.Execution = new(execute.ToString(), result.ExitCode);
      if (result.ExitCode == 0)
        return true;

      sink.Report(Diagnostic.Create(RunnerDiagnostics.BuildFailed, null, execute, result.ExitCode) with { Cause = Cause(result) });
      return false;
    }

    private void Compare(List<DriverOutcome> outcomes) {
      var file = vectors.File;
      var verification = OracleVerification.VerifyAsync(vectors.Registry, file.Fixture, file.Vectors, [.. outcomes.Select(outcome => outcome.Image)], file.Exactness)
        .GetAwaiter().GetResult();
      foreach (var diagnostic in verification.Diagnostics)
        sink.Report(diagnostic);

      var report = verification.Report;
      if (report.Outcomes.Count == 0)
        return;

      this._report = report;
      for (var index = 0; index < report.Outcomes.Count; ++index) {
        var outcome = report.Outcomes[index];
        this.VectorLines.Add(outcome.Passed
          ? $"  {outcome.Name}: pass"
          : $"  {outcome.Name}: FAIL expected {JavaScriptDriver.Display(file.Signature, Convert.FromHexString(outcome.ReferenceHex), null)} actual {JavaScriptDriver.Display(file.Signature, outcomes[index].Image, outcomes[index].Trap)}");
      }

      var passed = report.Outcomes.Where(outcome => outcome.Passed).ToList();
      var worst = passed.Where(outcome => outcome.UlpDistance is > 0).MaxBy(outcome => outcome.UlpDistance);
      var policy = file.Exactness;
      this.Verification = new(vectors.Reference, file.Provenance.GeneratedBy, new(
        manifest.FloatingPoint?.Mode ?? "strict",
        policy is ExactnessPolicy.Exact ? "exact" : "tolerance",
        ExactnessPolicy.Spelling(policy.SpecialValues),
        [],
        policy is ExactnessPolicy.Tolerance { Reason: { } reason } tolerance
          ? [new ManifestDeclaredBound(CallImages.ResultName, tolerance.Mode, double.Parse(tolerance.BoundText, CultureInfo.InvariantCulture), reason)]
          : [],
        new(passed.Count(outcome => outcome.Identical), passed.Count(outcome => !outcome.Identical), worst?.UlpDistance ?? 0, worst?.Name)),
        report.Total, report.Passed, report.Total - report.Passed);
    }

    private void Report(IReadOnlyList<Diagnostic> diagnostics) {
      foreach (var diagnostic in diagnostics)
        sink.Report(diagnostic);
    }

    private static string Cause(RunResult result) {
      var text = string.Join('\n', new[] { result.Stderr, result.Stdout }.Select(bytes => Encoding.UTF8.GetString(bytes.Span).Trim()).Where(part => part.Length > 0));
      return text.Length > 0 ? Shorten(text) : "the step printed nothing";
    }

    private static string Shorten(string text) => text.Length <= 500 ? text : text[..500] + "...";

    /// <summary>Writes the evidence bundle of the run under a root.</summary>
    public void WriteEvidence(string root, string original, string manifestPath, bool verified) {
      var collector = new EvidenceCollector();
      var before = Path.Combine(Path.GetTempPath(), $"polypiler-verify-{Guid.NewGuid():N}.manifest.json");
      File.WriteAllText(before, original, new UTF8Encoding(false));
      try {
        collector.AddManifest(manifestPath);
        collector.AddFile("manifest-before", before);
        collector.AddFile("vectors", vectors.File.Path);
        foreach (var output in manifest.Outputs)
          collector.AddTargetCode(Path.Combine(directory, output.Path));
        collector.AddToolVersion(this._toolchain, this._version);
        foreach (var vector in vectors.File.Vectors)
          collector.AddFixtureValue(vector.Name, Hex.Format(vector.Input));
        foreach (var (label, result) in this._processes)
          collector.AddProcess(label, Encoding.UTF8.GetString(result.Stdout.Span), Encoding.UTF8.GetString(result.Stderr.Span), result.ExitCode);
        if (this._report is { } report)
          collector.AddComparison(report, vectors.File.Exactness);

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
        EvidenceWriter.Write(collector, new ScenarioIdentity(EvidenceFeature, name, ["verify"]), verified ? ScenarioOutcome.Passed : ScenarioOutcome.Failed, root);
      } finally {
        File.Delete(before);
      }
    }

  }

}
