using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Runners;

/// <summary>The diagnostics of the JavaScript runner, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class NodeRunnerDiagnostics {

  /// <summary><c>PLY1191</c>: <c>node --check</c> rejected a file. Argument: the file, line and <c>SyntaxError</c> text.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Rejected = new("PLY1191", DiagnosticSeverity.Error, ExitCode.TargetBuildFailure, "node rejected the input: {0}", "javascript.runner.compile");

  /// <summary><c>PLY1192</c>: the driver or the test host exited non-zero outside a per-call trap. Arguments: the exit code, standard error.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Crashed = new("PLY1192", DiagnosticSeverity.Error, ExitCode.OracleFailure, "the JavaScript driver exited with code {0}: {1}", "javascript.runner.crash");

  /// <summary><c>PLY1194</c>: the detail of a mismatching call. Arguments: the call's index, the expected and the actual outcome.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Mismatch = new("PLY1194", DiagnosticSeverity.Error, ExitCode.SemanticMismatch, "call {0}: expected {1}, actual {2}", "javascript.runner.mismatch");

  /// <summary><c>PLY1195</c>: a protocol error: driver output that is no image or trap line, output written by the function under test, a wrong line count, an image that does not fit.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Protocol = new("PLY1195", DiagnosticSeverity.Error, ExitCode.OracleFailure, "{0}", "javascript.runner.protocol");

}

/// <summary>
/// The language runner of JavaScript, <c>javascript</c> on the toolchain <c>node</c>: the real engine
/// checks and runs every program, each step a plan of the cross-language runner. JavaScript has no
/// compiler, so <see cref="Compile"/> is <c>node --check</c> of every module; a call runs the
/// generated driver (<see cref="NodeDriverGenerator"/>) in a process of its own per operation. The
/// signature always comes from the caller: the runner never infers a type or a mode from the code.
/// </summary>
public sealed partial class NodeRunner : LanguageRunnerBase, IHelperRunner {

  /// <summary>The key of the toolchain in <c>tooling/toolchains.lock</c>.</summary>
  public const string Toolchain = "node";

  private readonly ICrossLanguageRunner _runner;
  private readonly string _workspace;
  private readonly RunLimits _buildLimits;
  private readonly RunLimits _driverLimits;

  /// <summary>Creates a runner.</summary>
  /// <param name="runner">Runs the plans.</param>
  /// <param name="workspace">The directory the checked programs are kept in, one directory per check.</param>
  /// <param name="buildLimits">The limits of a check and of a test run; the runner's defaults when <see langword="null"/>.</param>
  /// <param name="driverLimits">The limits of a driver run, a program run and a helper run; the runner's defaults when <see langword="null"/>.</param>
  public NodeRunner(ICrossLanguageRunner runner, string workspace, RunLimits? buildLimits = null, RunLimits? driverLimits = null) {
    ArgumentNullException.ThrowIfNull(runner);
    ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
    this._runner = runner;
    this._workspace = workspace;
    this._buildLimits = buildLimits ?? RunLimits.Default;
    this._driverLimits = driverLimits ?? RunLimits.Default;
  }

  /// <inheritdoc />
  public override string Language => "javascript";

  /// <inheritdoc />
  public override IReadOnlyList<string> Toolchains => [Toolchain];

  /// <summary>
  /// Checks every <c>.js</c>, <c>.mjs</c> and <c>.cjs</c> file with <c>node --check</c>, one build
  /// step each, and keeps the files: <c>PLY0030</c> with <c>PLY1191</c> when node rejects one.
  /// </summary>
  /// <param name="input">The modules, the export and its signature.</param>
  public override CompileResult Compile(RunnerInput input) {
    ArgumentNullException.ThrowIfNull(input);
    var directory = Path.Combine(this._workspace, Guid.NewGuid().ToString("N"));
    var checks = input.Files.Keys.Where(NodeDriverGenerator.IsScript).Order(StringComparer.Ordinal).Select(file => new ProcessSpec(Toolchain, "--check", file)).ToList();
    var result = this.Run(new(input.Files, checks, new(Toolchain, "--version"), this._buildLimits, input.Trust));
    if (result.Outcome == RunOutcome.Completed) {
      foreach (var (name, bytes) in input.Files) {
        var destination = Path.Combine(directory, name);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, bytes.Span);
      }

      return new(result.Outcome, directory, [], result);
    }

    if (result.Outcome != RunOutcome.BuildFailed)
      return new(result.Outcome, directory, result.Diagnostics, result);

    var output = string.Join("\n", result.Diagnostics.Select(diagnostic => $"{diagnostic.Message}\n{diagnostic.Cause}").Append(Text(result.Stderr)));
    return new(result.Outcome, directory, [.. result.Diagnostics, Diagnostic.Create(NodeRunnerDiagnostics.Rejected, null, SyntaxError(output, input.Files.Keys))], result);
  }

  /// <summary>
  /// The rejection of <c>node --check</c> as one line: <c>file:line: SyntaxError: text</c>, the file
  /// named as the run names it; what node printed, trimmed, when it names no <c>SyntaxError</c>.
  /// </summary>
  /// <param name="output">What the check printed.</param>
  /// <param name="files">The files of the run.</param>
  /// <returns>The text.</returns>
  public static string SyntaxError(string output, IEnumerable<string> files) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(files);
    var error = SyntaxErrorLine().Match(output);
    if (!error.Success)
      return output.Trim();

    var location = LocationLine().Matches(output[..error.Index]).LastOrDefault();
    if (location is null)
      return error.Value.Trim();

    var path = location.Groups["path"].Value.Replace('\\', '/');
    var file = files.Where(name => path == name || path.EndsWith("/" + name, StringComparison.Ordinal)).OrderByDescending(name => name.Length).FirstOrDefault() ?? Path.GetFileName(path);
    return $"{file}:{location.Groups["line"].Value}: {error.Value.Trim()}";
  }

  /// <summary>Runs the entry module of a checked program: <c>node &lt;entry&gt; &lt;arguments&gt;</c>, the result unchanged.</summary>
  /// <param name="compiled">The check.</param>
  /// <param name="arguments">The arguments.</param>
  /// <exception cref="ArgumentException">The program has no entry module.</exception>
  public override RunResult Execute(CompileResult compiled, IReadOnlyList<string> arguments) {
    ArgumentNullException.ThrowIfNull(compiled);
    ArgumentNullException.ThrowIfNull(arguments);
    var files = Files(compiled.Directory);
    return this.Run(new(files, [], new(Toolchain, [NodeDriverGenerator.Entry(files), .. arguments]), this._driverLimits, TrustLevel.Trusted));
  }

  /// <summary>Runs a helper script as trusted code: <c>node &lt;helper&gt; &lt;arguments&gt;</c> with the standard input given.</summary>
  /// <param name="helperPath">The script; it is copied into the run under its file name.</param>
  /// <param name="arguments">The arguments.</param>
  /// <param name="stdin">The standard input.</param>
  public RunResult RunHelper(string helperPath, IReadOnlyList<string> arguments, ReadOnlyMemory<byte> stdin) {
    ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
    ArgumentNullException.ThrowIfNull(arguments);
    var name = Path.GetFileName(helperPath);
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [name] = File.ReadAllBytes(helperPath) };
    return this.Run(new(files, [], new(Toolchain, [name, .. arguments], stdin), this._driverLimits, TrustLevel.Trusted));
  }

  /// <summary>
  /// Runs the <c>node:test</c> suite of a directory with <c>node --test --test-reporter=tap</c> and
  /// reads the TAP report; a suite with a failing test is a completed run with <c>Failed</c> above
  /// zero. A run that ends without the report's counts is <c>PLY1192</c>.
  /// </summary>
  /// <param name="projectDirectory">The directory; its <c>node_modules</c> is not copied.</param>
  /// <exception cref="LanguageRunnerException">The tests did not run.</exception>
  public override TestReport RunTests(string projectDirectory) {
    ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
    foreach (var path in Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)) {
      var name = Path.GetRelativePath(projectDirectory, path).Replace('\\', '/');
      if (!name.StartsWith("node_modules/", StringComparison.Ordinal))
        files[name] = File.ReadAllBytes(path);
    }

    var result = this.Run(new(files, [], new(Toolchain, "--test", "--test-reporter=tap"), this._buildLimits, TrustLevel.Trusted));
    if (result.Outcome != RunOutcome.Completed)
      throw new LanguageRunnerException($"the JavaScript test run ended with {result.Outcome}", result.Diagnostics);

    return ReadTap(Text(result.Stdout)) ?? throw new LanguageRunnerException("the JavaScript test run printed no report",
      [Diagnostic.Create(NodeRunnerDiagnostics.Crashed, null, result.ExitCode.ToString(CultureInfo.InvariantCulture), Text(result.Stderr).Trim())]);
  }

  /// <summary>
  /// The counts and failures of the TAP report of <c>node --test</c>: the summary lines
  /// <c># tests</c>, <c># pass</c>, <c># fail</c>, <c># skipped</c> and <c># todo</c>, and the
  /// top-level <c>not ok</c> lines with the <c>error</c> of their YAML block.
  /// </summary>
  /// <param name="tap">The report.</param>
  /// <returns>The report, or <see langword="null"/> when the text has no <c># tests</c> count.</returns>
  public static TestReport? ReadTap(string tap) {
    ArgumentNullException.ThrowIfNull(tap);
    var lines = tap.ReplaceLineEndings("\n").Split('\n');
    int? Count(string name) => lines.Select(line => SummaryLine().Match(line)).FirstOrDefault(match => match.Success && match.Groups["name"].Value == name) is { } match
      ? int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture) : null;
    if (Count("tests") is not { } total)
      return null;

    var failures = new List<TestFailure>();
    for (var index = 0; index < lines.Length; ++index) {
      var failed = FailedLine().Match(lines[index]);
      if (!failed.Success)
        continue;

      var message = "";
      for (var next = index + 1; next < lines.Length && (lines[next].StartsWith(' ') || lines[next].Length == 0); ++next) {
        var error = ErrorLine().Match(lines[next]);
        if (!error.Success)
          continue;

        var value = error.Groups["value"].Value.Trim();
        message = value is "|-" or "|" or ">-" or ">" ? (next + 1 < lines.Length && lines[next + 1].StartsWith(' ') ? lines[next + 1].Trim() : "") : value.Trim('\'', '"');
        break;
      }

      failures.Add(new(failed.Groups["name"].Value.Trim(), message));
    }

    return new(total, Count("pass") ?? 0, Count("fail") ?? 0, (Count("skipped") ?? 0) + (Count("todo") ?? 0), failures);
  }

  /// <summary>
  /// The diagnostics of the vectors of a report that failed: <c>PLY0051</c> or <c>PLY0052</c> of the
  /// comparer, each followed by <c>PLY1194</c> with the expected and actual values of the call.
  /// </summary>
  /// <param name="report">The report of <see cref="LanguageRunnerBase.RunVectors"/>.</param>
  /// <param name="vectors">The vectors it ran.</param>
  /// <returns>The diagnostics, empty when every vector passed.</returns>
  public static IReadOnlyList<Diagnostic> Explain(ComparisonReport report, VectorFile vectors) {
    ArgumentNullException.ThrowIfNull(report);
    ArgumentNullException.ThrowIfNull(vectors);
    var signature = vectors.Signature ?? throw new ImageException("a vector file used by a language runner requires a call signature");
    var diagnostics = new List<Diagnostic>();
    var compared = report.Diagnostics(vectors.Fixture);
    var failed = 0;
    for (var index = 0; index < report.Outcomes.Count; ++index) {
      var outcome = report.Outcomes[index];
      if (outcome.Passed)
        continue;

      var vector = vectors.Vectors[index];
      diagnostics.Add(compared[failed++]);
      diagnostics.Add(Diagnostic.Create(NodeRunnerDiagnostics.Mismatch, null, index.ToString(CultureInfo.InvariantCulture),
        Describe(signature, vector.Expected, vector.ExpectedTrap), Describe(signature, Image(outcome.ActualHex), Trap(outcome.ActualHex))));
    }

    return diagnostics;
  }

  /// <inheritdoc />
  protected override RunResult RunDriver(string directory, string export, CallSignature signature, ReadOnlyMemory<byte> inputs, TrustLevel trust) {
    var files = Files(directory);
    string driver;
    try {
      driver = NodeDriverGenerator.Generate(files, export, signature);
    } catch (ArgumentException exception) {
      var reason = exception.Message.Replace($" (Parameter '{exception.ParamName}')", "", StringComparison.Ordinal);
      return new(RunOutcome.Refused, -1, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited,
        [Diagnostic.Create(NodeRunnerDiagnostics.Protocol, null, reason)]);
    }

    files[NodeDriverGenerator.FileName] = Encoding.UTF8.GetBytes(driver);
    var result = this.Run(new(files, [], new(Toolchain, [NodeDriverGenerator.FileName, export], inputs), this._driverLimits, trust));
    if (result.Outcome != RunOutcome.Completed || result.ExitCode == 0)
      return result;

    var stderr = Text(result.Stderr).Trim();
    var diagnostic = result.ExitCode == NodeDriverGenerator.ProtocolExitCode
      ? Diagnostic.Create(NodeRunnerDiagnostics.Protocol, null, stderr)
      : Diagnostic.Create(NodeRunnerDiagnostics.Crashed, null, result.ExitCode.ToString(CultureInfo.InvariantCulture), stderr) with { Cause = stderr };
    return result with { Diagnostics = result.Diagnostics.Append(diagnostic).ToList() };
  }

  /// <inheritdoc />
  protected override Diagnostic? ProtocolError(string reason) => Diagnostic.Create(NodeRunnerDiagnostics.Protocol, null, reason);

  private static byte[]? Image(string text) => text.StartsWith('!') ? null : Convert.FromHexString(text);

  private static TrapClass? Trap(string text) => text.StartsWith('!') ? TrapClass.Parse(text[1..]) : null;

  private static string Describe(CallSignature signature, byte[]? image, TrapClass? trap) {
    if (trap is not null)
      return "!" + trap.ToString();
    if (image is null)
      return "nothing";

    try {
      var values = CallImages.Decode(signature, CallImage.Output, image);
      return values.Count switch {
        0 => "nothing",
        1 when values[0].Name == CallImages.ResultName => values[0].Value.ToString()!,
        _ => string.Join(", ", values)
      };
    } catch (ImageException) {
      return Convert.ToHexStringLower(image);
    }
  }

  private static Dictionary<string, ReadOnlyMemory<byte>> Files(string directory) {
    var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
    if (Directory.Exists(directory))
      foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        files[Path.GetRelativePath(directory, file).Replace('\\', '/')] = File.ReadAllBytes(file);

    return files;
  }

  private RunResult Run(RunPlan plan) => this._runner.RunAsync(plan).GetAwaiter().GetResult();

  private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

  [GeneratedRegex(@"^(?:Uncaught )?SyntaxError: [^\r\n]*", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
  private static partial Regex SyntaxErrorLine();

  [GeneratedRegex(@"^(?<path>[^\r\n]+\.[cm]?js):(?<line>\d+)\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
  private static partial Regex LocationLine();

  [GeneratedRegex(@"^# (?<name>tests|pass|fail|skipped|todo|cancelled) (?<count>\d+)$", RegexOptions.CultureInvariant)]
  private static partial Regex SummaryLine();

  [GeneratedRegex(@"^not ok \d+ - (?<name>.*?)(?: # .*)?$", RegexOptions.CultureInvariant)]
  private static partial Regex FailedLine();

  [GeneratedRegex(@"^\s+error: (?<value>.*)$", RegexOptions.CultureInvariant)]
  private static partial Regex ErrorLine();

}
