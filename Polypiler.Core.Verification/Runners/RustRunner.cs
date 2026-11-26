using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Runners;

/// <summary>The diagnostics of the Rust runner, as <c>spec/diagnostics.yaml</c> registers them.</summary>
public static class RustRunnerDiagnostics {

  /// <summary><c>PLY1591</c>: the Rust compiler rejected the input. Argument: its messages, one per line.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Rejected = new("PLY1591", DiagnosticSeverity.Error, ExitCode.TargetBuildFailure, "the Rust compiler rejected the input: {0}", "rust.runner.compile");

  /// <summary><c>PLY1592</c>: the driver process died inside a call. Arguments: the exit code, standard error.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Crashed = new("PLY1592", DiagnosticSeverity.Error, ExitCode.OracleFailure, "the Rust driver died with exit code {0}: {1}", "rust.runner.crash");

  /// <summary><c>PLY1595</c>: a protocol error: a signature that does not fit the declaration, an export that does not exist, a line the driver cannot read.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Protocol = new("PLY1595", DiagnosticSeverity.Error, ExitCode.OracleFailure, "{0}", "rust.runner.protocol");

}

/// <summary>What a call of an export did: its output image, or the panic it ended in, or the error it returned.</summary>
/// <param name="Image">The output image, or <see langword="null"/> where the call panicked or returned an error.</param>
/// <param name="Panic">The first line of the panic message, or <see langword="null"/>.</param>
public sealed record RustCallOutcome(byte[]? Image, string? Panic) {

  /// <summary>What the <c>Err</c> of an export that returns a <c>Result</c> displays, or <see langword="null"/>.</summary>
  public string? Error { get; init; }

  /// <summary>The trap class of the panic, or <see langword="null"/> for a call that returned.</summary>
  public TrapClass? Trap => this.Panic is null ? null : RustTraps.Classify(this.Panic);

}

/// <summary>What a Cargo command reported: whether it succeeded, the compiler's messages, and the diagnostics of a failure.</summary>
/// <param name="Succeeded">Whether the command did what it was asked.</param>
/// <param name="Messages">The messages of rustc and clippy.</param>
/// <param name="Diagnostics">Why it failed; empty when it succeeded.</param>
public sealed record RustBuild(bool Succeeded, IReadOnlyList<CargoMessage> Messages, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>The outcome of one <c>#[test]</c> function.</summary>
public enum RustTestOutcome {

  /// <summary><c>ok</c>.</summary>
  Passed,

  /// <summary><c>FAILED</c>.</summary>
  Failed,

  /// <summary><c>ignored</c>.</summary>
  Skipped

}

/// <summary>What <c>cargo test</c> reported.</summary>
/// <param name="Tests">Every test function with its outcome, in the order Cargo listed them.</param>
/// <param name="Output">The standard output, which holds the messages of the failed tests.</param>
/// <param name="Diagnostics">Why the tests did not run; empty when they did.</param>
public sealed record RustTestReport(IReadOnlyList<(string Name, RustTestOutcome Outcome)> Tests, string Output, IReadOnlyList<Diagnostic> Diagnostics) {

  /// <summary>How many passed.</summary>
  public int Passed => this.Tests.Count(test => test.Outcome == RustTestOutcome.Passed);

  /// <summary>How many failed.</summary>
  public int Failed => this.Tests.Count(test => test.Outcome == RustTestOutcome.Failed);

}

/// <summary>What the calls of a driver did: one outcome per call in order, or the diagnostics of why there is none.</summary>
/// <param name="Outcomes">The outcomes; <see langword="null"/> when the crate did not build or the driver died.</param>
/// <param name="Diagnostics">Why there are no outcomes.</param>
public sealed record RustExecution(IReadOnlyList<RustCallOutcome>? Outcomes, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Builds, tests, lints and executes Rust through the pinned toolchain <c>rust</c> (Cargo), every step
/// a plan of the cross-language runner: <c>cargo build</c>, <c>cargo test</c>, <c>cargo fmt --check</c>,
/// <c>cargo clippy -- -D warnings</c> and <c>cargo run</c> of a generated driver
/// (<see cref="RustDriverGenerator"/>). Every step runs offline with the build switches of the
/// instance, which have no defaults. The input is a Cargo package, or one <c>*.rs</c> file, which is
/// wrapped into a package whose root holds it as the module <c>fixture</c>.
/// </summary>
public sealed partial class RustRunner {

  /// <summary>The key of the toolchain in <c>tooling/toolchains.lock</c>; its executable is <c>cargo</c>.</summary>
  public const string Toolchain = "rust";

  /// <summary>The name of the module a single fixture file becomes.</summary>
  public const string FixtureModule = "fixture";

  private readonly ICrossLanguageRunner _runner;
  private readonly IReadOnlyDictionary<string, string> _environment;

  /// <summary>Creates a runner.</summary>
  /// <param name="runner">Runs the plans.</param>
  /// <param name="options">How every crate is built.</param>
  /// <param name="environment">Further variables every step gets, such as <c>RUSTUP_HOME</c> and <c>RUSTUP_TOOLCHAIN</c>.</param>
  public RustRunner(ICrossLanguageRunner runner, RustBuildOptions options, IReadOnlyDictionary<string, string>? environment = null) {
    ArgumentNullException.ThrowIfNull(runner);
    ArgumentNullException.ThrowIfNull(options);
    this._runner = runner;
    this.Options = options;
    var variables = new Dictionary<string, string>(StringComparer.Ordinal) {
      ["CARGO_NET_OFFLINE"] = "true",
      ["CARGO_TERM_COLOR"] = "never"
    };
    foreach (var (name, value) in options.Environment)
      variables[name] = value;
    foreach (var (name, value) in environment ?? new Dictionary<string, string>())
      variables[name] = value;
    this._environment = variables;
  }

  /// <summary>How every crate is built.</summary>
  public RustBuildOptions Options { get; }

  /// <summary>
  /// The files of a package for one fixture file: <c>Cargo.toml</c> of the 2024 edition without
  /// dependencies, and <c>src/lib.rs</c> that holds the fixture as <c>pub mod fixture</c>.
  /// </summary>
  /// <param name="name">The fixture's file name, such as <c>add.rs</c>.</param>
  /// <param name="source">Its text.</param>
  /// <returns>The package's files.</returns>
  public static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Package(string name, string source) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(source);
    return new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) {
      ["Cargo.toml"] = Encoding.UTF8.GetBytes("[package]\nname = \"fixture_package\"\nversion = \"0.1.0\"\nedition = \"2024\"\n"),
      ["src/lib.rs"] = Encoding.UTF8.GetBytes($"#[path = \"{name}\"]\npub mod {FixtureModule};\n"),
      ["src/" + name] = Encoding.UTF8.GetBytes(source)
    };
  }

  /// <summary><c>cargo build</c>: the messages of the compiler, and <c>PLY0030</c> with <c>PLY1591</c> where it rejected the package.</summary>
  /// <param name="files">The package.</param>
  /// <param name="cancellationToken">Ends the build.</param>
  public async Task<RustBuild> CompileAsync(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, CancellationToken cancellationToken = default) {
    var step = this.Cargo("build", "--offline", "--message-format=json");
    var result = await this.RunAsync(files, step, cancellationToken).ConfigureAwait(false);
    var output = Text(result.Stdout);
    var messages = CargoMessageParser.Parse(output);
    if (result.Outcome != RunOutcome.Completed)
      return new(false, messages, result.Diagnostics);
    if (result.ExitCode == 0)
      return new(true, messages, []);

    return new(false, messages, Rejection(step, result.ExitCode, messages, Text(result.Stderr)));
  }

  /// <summary><c>cargo test</c>: one entry per <c>#[test]</c> function.</summary>
  /// <param name="files">The package.</param>
  /// <param name="cancellationToken">Ends the run.</param>
  public async Task<RustTestReport> RunTestsAsync(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, CancellationToken cancellationToken = default) {
    var step = this.Cargo("test", "--offline");
    var result = await this.RunAsync(files, step, cancellationToken).ConfigureAwait(false);
    var output = Text(result.Stdout);
    var tests = TestLine().Matches(output)
      .Select(match => (match.Groups["name"].Value, match.Groups["outcome"].Value switch {
        "ok" => RustTestOutcome.Passed,
        "FAILED" => RustTestOutcome.Failed,
        _ => RustTestOutcome.Skipped
      }))
      .ToList();
    IReadOnlyList<Diagnostic> diagnostics = result.Outcome != RunOutcome.Completed ? result.Diagnostics
      : result.ExitCode != 0 && tests.Count == 0 ? Rejection(step, result.ExitCode, [], Text(result.Stderr))
      : [];
    return new(tests, output, diagnostics);
  }

  /// <summary><c>cargo fmt --check</c>: the files rustfmt would change, relative to the package; none when it is formatted.</summary>
  /// <param name="files">The package.</param>
  /// <param name="cancellationToken">Ends the run.</param>
  public async Task<IReadOnlyList<string>> FormatCheckAsync(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, CancellationToken cancellationToken = default) {
    var result = await this.RunAsync(files, this.Cargo("fmt", "--check", "--", "-l"), cancellationToken).ConfigureAwait(false);
    var directory = result.WorkingDirectory ?? "";
    return [.. Text(result.Stdout).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)
      .Select(path => Path.IsPathRooted(path) ? Path.GetRelativePath(directory, path).Replace('\\', '/') : path)];
  }

  /// <summary><c>cargo clippy --all-targets -- -D warnings</c>: the findings, returned, not raised.</summary>
  /// <param name="files">The package.</param>
  /// <param name="cancellationToken">Ends the run.</param>
  public async Task<IReadOnlyList<CargoMessage>> ClippyAsync(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, CancellationToken cancellationToken = default) {
    var result = await this.RunAsync(files, this.Cargo("clippy", "--offline", "--all-targets", "--message-format=json", "--", "-D", "warnings"), cancellationToken).ConfigureAwait(false);
    return CargoMessageParser.Parse(Text(result.Stdout));
  }

  /// <summary>
  /// Calls exports of a package on input images through the generated driver, in the order given:
  /// one outcome per call. A crate that does not build gives <c>PLY0030</c> and <c>PLY1591</c>, or
  /// <c>PLY1595</c> where the error is in the driver - a signature that does not fit; a driver that
  /// dies inside a call gives <c>PLY1592</c>, one that answers what it was not asked <c>PLY1595</c>.
  /// </summary>
  /// <param name="files">The package, whose library crate holds the exports.</param>
  /// <param name="crate">The library crate's name.</param>
  /// <param name="exports">The functions.</param>
  /// <param name="calls">Each call: the index of the export and the input image.</param>
  /// <param name="cancellationToken">Ends the run.</param>
  public async Task<RustExecution> ExecuteAsync(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, string crate, IReadOnlyList<RustExport> exports,
    IReadOnlyList<(int Export, byte[] Input)> calls, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(files);
    ArgumentNullException.ThrowIfNull(calls);
    string driver;
    try {
      driver = RustDriverGenerator.Generate(crate, exports);
    } catch (ArgumentException exception) {
      return new(null, [Diagnostic.Create(RustRunnerDiagnostics.Protocol, null, exception.Message)]);
    }

    if (calls.FirstOrDefault(call => call.Export < 0 || call.Export >= exports.Count) is { Input: not null } unknown)
      return new(null, [Diagnostic.Create(RustRunnerDiagnostics.Protocol, null, $"there is no export {unknown.Export}")]);

    var package = new Dictionary<string, ReadOnlyMemory<byte>>(files, StringComparer.Ordinal) { [RustDriverGenerator.FilePath] = Encoding.UTF8.GetBytes(driver) };
    var input = string.Concat(calls.Select(call => call.Export.ToString(CultureInfo.InvariantCulture) + " " + Convert.ToHexStringLower(call.Input) + "\n"));
    var step = this.Cargo(Encoding.UTF8.GetBytes(input), "run", "--offline", "--quiet", "--message-format=json", "--bin", RustDriverGenerator.Binary);
    var result = await this.RunAsync(package, step, cancellationToken).ConfigureAwait(false);
    if (result.Outcome != RunOutcome.Completed)
      return new(null, result.Diagnostics);

    var stdout = Text(result.Stdout);
    var messages = CargoMessageParser.Parse(stdout);
    if (CargoMessageParser.Succeeded(stdout) == false) {
      var inDriver = messages.Any(message => message.Level == "error" && message.File == RustDriverGenerator.FilePath);
      return new(null, inDriver
        ? [Diagnostic.Create(RustRunnerDiagnostics.Protocol, null, "the signature does not fit the declaration: " + string.Join("; ", messages.Where(message => message.Level == "error")))]
        : Rejection(step, result.ExitCode, messages, Text(result.Stderr)));
    }

    var lines = stdout.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0 && !line.StartsWith('{')).ToList();
    if (result.ExitCode != 0 || lines.Count != calls.Count)
      return new(null, [Diagnostic.Create(result.ExitCode is 0 or 2 ? RustRunnerDiagnostics.Protocol : RustRunnerDiagnostics.Crashed, null,
        result.ExitCode is 0 or 2 ? $"the driver answered {lines.Count} of {calls.Count} calls (exit code {result.ExitCode})" : result.ExitCode.ToString(CultureInfo.InvariantCulture),
        Text(result.Stderr).Trim()) with { Cause = Text(result.Stderr).Trim() }]);

    var outcomes = new List<RustCallOutcome>();
    foreach (var line in lines) {
      if (line.StartsWith('!'))
        outcomes.Add(new(null, line[1..]));
      else if (line.StartsWith('?'))
        outcomes.Add(new(null, null) { Error = line[1..] });
      else if (line.All(char.IsAsciiHexDigitLower) && line.Length % 2 == 0)
        outcomes.Add(new(Convert.FromHexString(line), null));
      else
        return new(null, [Diagnostic.Create(RustRunnerDiagnostics.Protocol, null, $"the driver printed '{line}', which is no output image")]);
    }

    return new(outcomes, []);
  }

  /// <summary>The diagnostics of a crate the compiler rejected: <c>PLY0030</c>, then <c>PLY1591</c> with every error.</summary>
  private static IReadOnlyList<Diagnostic> Rejection(ProcessSpec step, int exitCode, IReadOnlyList<CargoMessage> messages, string stderr) {
    var errors = messages.Where(message => message.Level == "error").Select(message => message.ToString()).ToList();
    return [
      Diagnostic.Create(RunnerDiagnostics.BuildFailed, null, step, exitCode) with { Cause = stderr.Trim() },
      Diagnostic.Create(RustRunnerDiagnostics.Rejected, null, errors.Count > 0 ? string.Join("; ", errors) : stderr.Trim())
    ];
  }

  private Task<RunResult> RunAsync(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, ProcessSpec step, CancellationToken cancellationToken)
    => this._runner.RunAsync(new RunPlan(files, [], step, RunLimits.Default, TrustLevel.Trusted), cancellationToken);

  private ProcessSpec Cargo(params string[] arguments) => this.Cargo(ReadOnlyMemory<byte>.Empty, arguments);

  private ProcessSpec Cargo(ReadOnlyMemory<byte> stdin, params string[] arguments) => new(Toolchain, arguments, stdin) { Environment = this._environment };

  private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

  [GeneratedRegex(@"^test (?<name>\S+)(?: - should panic)? \.\.\. (?<outcome>ok|FAILED|ignored)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
  private static partial Regex TestLine();

}
