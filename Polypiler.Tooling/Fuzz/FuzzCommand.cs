using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Polypiler.Core;
using Polypiler.Core.Verification.Fuzzing;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Fuzz;

/// <summary>
/// <c>polypiler-tooling fuzz run --target &lt;id&gt; [--seed &lt;n&gt;] [--iterations &lt;n&gt;] [--engine inprocess|afl]
/// [--duration &lt;seconds&gt;] [--assembly &lt;path&gt;]…</c> fuzzes a target from
/// <c>tests/fixtures/fuzz/&lt;id&gt;/</c> and records each finding under <c>artifacts/fuzz/&lt;id&gt;/</c>;
/// <c>fuzz minimize --target &lt;id&gt; --input &lt;file&gt;</c> minimizes and records one input;
/// <c>fuzz replay [--target &lt;id&gt;]</c> runs the inputs of fixed findings again. The verbs
/// <c>fuzz worker</c> and <c>fuzz afl-worker</c> are the processes the engines start.
/// </summary>
public sealed class FuzzCommand {

  /// <summary>The verb the command is invoked by.</summary>
  public const string Verb = "fuzz";

  /// <summary>The internal verb of the worker process the in-process engine supervises.</summary>
  public const string Worker = "worker";

  /// <summary>The internal verb of the process AFL++ runs.</summary>
  public const string AflWorker = "afl-worker";

  /// <summary>The diagnostic code of a finding.</summary>
  public const string FindingCode = "PLY0070";

  /// <summary>The registered diagnostic of <see cref="FindingCode"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor FindingDescriptor = new(FindingCode, DiagnosticSeverity.Error, ExitCode.EnvironmentError,
    "fuzz target '{0}': {1} '{2}' at iteration {3} (seed {4}); minimized to {5} bytes: {6}", "fuzz.finding");

  /// <summary>The diagnostic code of a record that cannot be written.</summary>
  public const string NotWritableCode = "PLY0005";

  /// <summary>The registered diagnostic of <see cref="NotWritableCode"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NotWritableDescriptor = new(NotWritableCode, DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "io.write");

  /// <summary>The diagnostic code of an engine that failed: a worker that does not start, or AFL++ ending with an error.</summary>
  public const string EngineFailedCode = "PLY0077";

  /// <summary>The registered diagnostic of <see cref="EngineFailedCode"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor EngineFailedDescriptor = new(EngineFailedCode, DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "fuzz.engine");

  /// <summary>The seed of a run without <c>--seed</c>.</summary>
  public const ulong DefaultSeed = 1;

  /// <summary>The iterations of a run without <c>--iterations</c>.</summary>
  public const long DefaultIterations = 10000;

  /// <summary>The seconds AFL++ runs without <c>--duration</c>.</summary>
  public const int DefaultDuration = 60;

  /// <summary>Where the corpora are, below the repository root.</summary>
  public static readonly string FixturesPath = Path.Combine("tests", "fixtures", "fuzz");

  private static readonly Dictionary<string, string[]> _options = new(StringComparer.Ordinal) {
    ["run"] = ["--target", "--seed", "--iterations", "--engine", "--duration", "--assembly"],
    ["minimize"] = ["--target", "--input", "--assembly"],
    ["replay"] = ["--target", "--assembly"],
    [Worker] = ["--target", "--assembly"],
    [AflWorker] = ["--target", "--assembly", "--probe"]
  };

  private static readonly string[] _repeatable = ["--assembly", "--probe"];

  private readonly string _currentDirectory;
  private readonly Func<IFuzzTarget, IReadOnlyList<string>, IFuzzExecutor> _executors;
  private readonly IProcessRunner _runner;
  private readonly Func<string, string?> _environment;
  private readonly string _platform;
  private readonly Action<string> _instrument;

  /// <summary>Creates the command for this machine: workers are <c>polypiler-tooling</c> processes.</summary>
  public FuzzCommand()
    : this(Environment.CurrentDirectory, WorkerExecutor, new ProcessRunner(), Environment.GetEnvironmentVariable, ToolchainLocator.CurrentPlatform, AflFuzzer.InstrumentWithSharpFuzz) { }

  /// <summary>Creates the command with everything it reads from the machine given.</summary>
  /// <param name="currentDirectory">Where relative paths start, the search for <c>tests/fixtures/fuzz</c> begins and <c>artifacts/</c> is written.</param>
  /// <param name="executors">Creates the executor of a target from the target and the <c>--assembly</c> paths.</param>
  /// <param name="runner">Runs AFL++ and the probe of its version.</param>
  /// <param name="environment">Reads an environment variable.</param>
  /// <param name="platform">The platform AFL++ is looked for on.</param>
  /// <param name="instrument">Instruments an assembly for AFL++ in place.</param>
  public FuzzCommand(string currentDirectory, Func<IFuzzTarget, IReadOnlyList<string>, IFuzzExecutor> executors, IProcessRunner runner, Func<string, string?> environment, string platform, Action<string> instrument) {
    this._currentDirectory = currentDirectory;
    this._executors = executors;
    this._runner = runner;
    this._environment = environment;
    this._platform = platform;
    this._instrument = instrument;
  }

  /// <summary>
  /// Serves the inputs of AFL++ in the <c>afl-worker</c> process: SharpFuzz's fork server, which runs
  /// the action once per input and tells AFL++ how it ended.
  /// </summary>
  public Action<Action<Stream>> AflServe { get; init; } = SharpFuzz.Fuzzer.OutOfProcess.Run;

  /// <summary>The executor of the command run on a real machine: a supervised <c>polypiler-tooling fuzz worker</c>.</summary>
  public static IFuzzExecutor WorkerExecutor(IFuzzTarget target, IReadOnlyList<string> assemblies) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(assemblies);
    var command = WorkerCommand(AppContext.BaseDirectory, OperatingSystem.IsWindows());
    return new WorkerProcessExecutor(() => {
      var info = new ProcessStartInfo(command[0]);
      foreach (var argument in command.Skip(1).Concat([Verb, Worker, "--target", target.Id]).Concat(assemblies.SelectMany(path => new[] { "--assembly", Path.GetFullPath(path) })))
        info.ArgumentList.Add(argument);
      return info;
    });
  }

  /// <summary>
  /// The command line that starts <c>polypiler-tooling</c> from a directory: its executable when the
  /// directory has one, else <c>dotnet</c> with its assembly.
  /// </summary>
  public static IReadOnlyList<string> WorkerCommand(string directory, bool windows) {
    var executable = Path.Combine(directory, Program.Name + (windows ? ".exe" : ""));
    return File.Exists(executable) ? [executable] : ["dotnet", Path.Combine(directory, Program.Name + ".dll")];
  }

  /// <summary>Runs the arguments that follow the verb; the workers use the console's standard streams.</summary>
  public int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) {
    ArgumentNullException.ThrowIfNull(args);
    if (args.Count > 0 && args[0] == Worker) {
      // Standard output carries the protocol: whatever a target prints goes to standard error.
      var responses = Console.OpenStandardOutput();
      Console.SetOut(Console.Error);
      return this.RunWorker(args, Console.OpenStandardInput(), responses, error);
    }

    if (args.Count > 0 && args[0] == AflWorker)
      return this.RunAflWorker(args, error);
    return this.Run(args, output, error, Stream.Null, Stream.Null);
  }

  /// <summary>Runs the arguments that follow the verb, with the streams a worker serves on given.</summary>
  public int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, Stream requests, Stream responses) {
    ArgumentNullException.ThrowIfNull(args);
    if (args.Count == 0 || !_options.ContainsKey(args[0]) || args[0] == AflWorker)
      return Usage(error, args.Count == 0 ? "fuzz needs 'run', 'minimize' or 'replay'" : $"unknown fuzz command '{args[0]}'");

    try {
      return args[0] switch {
        "run" => this.FuzzRun(Options(args), output, error),
        "minimize" => this.Minimize(Options(args), output, error),
        "replay" => this.Replay(Options(args), output, error),
        _ => this.RunWorker(args, requests, responses, error)
      };
    } catch (FuzzUsageException exception) {
      return Usage(error, exception.Message);
    } catch (FuzzCorpusException exception) {
      return Usage(error, exception.Message);
    } catch (FuzzWorkerException exception) {
      error.WriteLine($"{Program.Name}: error {EngineFailedCode}: {exception.Message}");
      return Program.Failure;
    } catch (ToolchainLockException exception) {
      foreach (var diagnostic in exception.Diagnostics)
        error.WriteLine(diagnostic);
      return Program.Failure;
    }
  }

  private int RunWorker(IReadOnlyList<string> args, Stream requests, Stream responses, TextWriter error) {
    try {
      var options = Options(args);
      var target = this.Target(options);
      FuzzWorker.Serve(target, requests, responses);
      return Program.Success;
    } catch (FuzzUsageException exception) {
      return Usage(error, exception.Message);
    }
  }

  private int RunAflWorker(IReadOnlyList<string> args, TextWriter error) {
    ResolveEventHandler? resolve = null;
    try {
      var options = Options(args);
      var probes = options.GetValueOrDefault("--probe") ?? [];
      resolve = (_, request) => Probe(probes, request.Name);
      AppDomain.CurrentDomain.AssemblyResolve += resolve;
      var target = this.Target(options);
      this.AflServe(stream => {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        target.Execute(buffer.ToArray());
      });
      return Program.Success;
    } catch (FuzzUsageException exception) {
      return Usage(error, exception.Message);
    } finally {
      AppDomain.CurrentDomain.AssemblyResolve -= resolve;
    }
  }

  /// <summary>
  /// The assembly a name stands for, loaded from the first directory that has it: the instrumented
  /// copy AFL++ runs lies apart from the dependencies of the assembly it was copied from.
  /// </summary>
  public static Assembly? Probe(IEnumerable<string> directories, string name)
    => directories
      .Select(directory => Path.Combine(directory, new AssemblyName(name).Name + ".dll"))
      .Where(File.Exists)
      .Select(Assembly.LoadFrom)
      .FirstOrDefault();

  private int FuzzRun(Dictionary<string, List<string>> options, TextWriter output, TextWriter error) {
    var target = this.Target(options);
    var seed = Number(options, "--seed", DefaultSeed, ulong.TryParse);
    var iterations = Number(options, "--iterations", DefaultIterations, long.TryParse);
    var duration = Number(options, "--duration", DefaultDuration, int.TryParse);
    if (iterations < 0)
      throw new FuzzUsageException("option '--iterations' must not be negative");
    if (duration < 1)
      throw new FuzzUsageException("option '--duration' must be at least 1");

    var engineName = Single(options, "--engine") ?? FuzzEngine.InProcess;
    if (engineName is not (FuzzEngine.InProcess or AflFuzzer.Engine))
      throw new FuzzUsageException($"option '--engine' does not allow '{engineName}'");

    var corpusDirectory = Path.Combine(this.FixturesDirectory(), target.Id);
    var assemblies = options.GetValueOrDefault("--assembly") ?? [];
    var engine = new FuzzEngine(fuzzed => this._executors(fuzzed, this.Absolute(assemblies)), this.ArtifactsDirectory, engineName);
    FuzzReport report;
    if (engineName == AflFuzzer.Engine) {
      var corpus = FuzzCorpus.Load(corpusDirectory);
      var afl = new AflFuzzer(new ToolchainLocator(ToolchainLock.Load(this.FindLock()), this._runner, this._environment, this._platform, ToolchainLocator.DefaultTimeout), this._runner, this._instrument);
      var (path, missing) = afl.Locate();
      if (missing != null)
        return Problem(error, missing);

      var (found, problem) = afl.Run(path!, target, corpus, WorkerCommand(AppContext.BaseDirectory, OperatingSystem.IsWindows()), engine,
        Path.Combine(this.ArtifactsDirectory, "fuzz", target.Id, "afl"), TimeSpan.FromSeconds(duration), seed, FuzzBudget.Default);
      if (problem != null)
        return Problem(error, problem);
      report = found!;
    } else {
      report = engine.Run(target, corpusDirectory, seed, iterations, FuzzBudget.Default);
    }

    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
      $"fuzz target '{report.Target}': {report.Executed} iterations, {report.Records.Count} findings, {report.WorkerStarts} worker starts, {report.ExecutionsPerSecond:F0} executions per second (engine {engineName}, seed {report.Seed})"));
    return this.Findings(report, error);
  }

  private int Minimize(Dictionary<string, List<string>> options, TextWriter output, TextWriter error) {
    var target = this.Target(options);
    var input = Single(options, "--input") ?? throw new FuzzUsageException("fuzz minimize needs '--input'");
    byte[] bytes;
    try {
      bytes = File.ReadAllBytes(Path.GetFullPath(input, this._currentDirectory));
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      throw new FuzzUsageException($"input '{input}' cannot be read: {exception.Message}");
    }

    var assemblies = this.Absolute(options.GetValueOrDefault("--assembly") ?? []);
    var report = new FuzzEngine(fuzzed => this._executors(fuzzed, assemblies), this.ArtifactsDirectory).Confirm(target, [bytes], 0, FuzzBudget.Default);
    if (report.Records.Count == 0)
      output.WriteLine($"fuzz target '{target.Id}': input '{input}' shows no finding");
    return this.Findings(report, error);
  }

  private int Replay(Dictionary<string, List<string>> options, TextWriter output, TextWriter error) {
    var targets = FuzzTargetCatalog.Load(options.GetValueOrDefault("--assembly") ?? [], this._currentDirectory);
    var only = Single(options, "--target");
    if (only != null && !targets.ContainsKey(only))
      throw new FuzzUsageException($"unknown fuzz target '{only}'");

    var assemblies = this.Absolute(options.GetValueOrDefault("--assembly") ?? []);
    var failures = FuzzRegressions.Replay(this.FixturesDirectory(), targets, target => this._executors(target, assemblies), FuzzBudget.Default, only);
    foreach (var failure in failures)
      error.WriteLine($"{Program.Name}: error {FindingCode}: regression {failure}");
    output.WriteLine($"{failures.Count} regression inputs fail");
    return failures.Count == 0 ? Program.Success : Program.Failure;
  }

  private int Findings(FuzzReport report, TextWriter error) {
    foreach (var record in report.Records) {
      var finding = record.Finding;
      var where = Path.GetRelativePath(this._currentDirectory, record.BinaryPath).Replace('\\', '/');
      error.WriteLine($"{Program.Name}: error {FindingCode}: " + string.Format(CultureInfo.InvariantCulture, FindingDescriptor.MessageFormat,
        finding.Target, finding.Kind, finding.Signature, finding.Iteration, finding.Seed, finding.Minimized.Length, where));
      if (record.WriteError != null)
        error.WriteLine($"{Program.Name}: error {NotWritableCode}: {record.WriteError}");
    }

    return report.Records.Count == 0 ? Program.Success : Program.Failure;
  }

  private IFuzzTarget Target(Dictionary<string, List<string>> options) {
    var id = Single(options, "--target") ?? throw new FuzzUsageException("option '--target' is required");
    var targets = FuzzTargetCatalog.Load(options.GetValueOrDefault("--assembly") ?? [], this._currentDirectory);
    return targets.TryGetValue(id, out var target) ? target : throw new FuzzUsageException($"unknown fuzz target '{id}'");
  }

  private string ArtifactsDirectory => Path.Combine(this._currentDirectory, "artifacts");

  private string[] Absolute(IEnumerable<string> paths) => [.. paths.Select(path => Path.GetFullPath(path, this._currentDirectory))];

  /// <summary>The corpora above the current directory, or where they would be below it.</summary>
  private string FixturesDirectory() => this.Above(FixturesPath) ?? Path.Combine(this._currentDirectory, FixturesPath);

  private string FindLock() => this.Above(ToolchainLock.RepositoryPath) ?? Path.Combine(this._currentDirectory, ToolchainLock.RepositoryPath);

  private string? Above(string relative) {
    for (var directory = new DirectoryInfo(this._currentDirectory); directory != null; directory = directory.Parent) {
      var candidate = Path.Combine(directory.FullName, relative);
      if (Directory.Exists(candidate) || File.Exists(candidate))
        return candidate;
    }

    return null;
  }

  private delegate bool Parser<T>(string? text, NumberStyles style, IFormatProvider? provider, out T value);

  private static T Number<T>(Dictionary<string, List<string>> options, string name, T fallback, Parser<T> parse) {
    var text = Single(options, name);
    if (text == null)
      return fallback;
    return parse(text, NumberStyles.None | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : throw new FuzzUsageException($"option '{name}' does not allow '{text}'");
  }

  private static string? Single(Dictionary<string, List<string>> options, string name)
    => options.TryGetValue(name, out var values) ? values[0] : null;

  /// <summary>The options after the subcommand, each with its values; checks names, values and repetition.</summary>
  private static Dictionary<string, List<string>> Options(IReadOnlyList<string> args) {
    var allowed = _options[args[0]];
    var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    for (var index = 1; index < args.Count; ++index) {
      var name = args[index];
      if (!allowed.Contains(name))
        throw new FuzzUsageException($"unknown option '{name}'");
      if (index + 1 == args.Count)
        throw new FuzzUsageException($"option '{name}' requires a value");
      if (options.TryGetValue(name, out var values) && !_repeatable.Contains(name))
        throw new FuzzUsageException($"option '{name}' is given more than once");
      if (values == null)
        options[name] = values = [];
      values.Add(args[++index]);
    }

    return options;
  }

  private static int Problem(TextWriter error, AflProblem problem) {
    error.WriteLine($"{Program.Name}: error {problem.Code}: {problem.Message}");
    return Program.Failure;
  }

  private static int Usage(TextWriter error, string message) {
    error.WriteLine($"{Program.Name}: error {ToolchainsCommand.UsageCode}: {message}");
    return Program.UsageError;
  }

}

/// <summary>Thrown for a fuzz command line that cannot be carried out as given; reported as <c>PLY0003</c>.</summary>
public sealed class FuzzUsageException : Exception {

  /// <summary>Creates the exception with the reason.</summary>
  public FuzzUsageException(string message) : base(message) { }

}
