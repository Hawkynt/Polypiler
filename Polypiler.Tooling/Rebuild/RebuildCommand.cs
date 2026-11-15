using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Core.Verification.Rebuild;
using Polypiler.Core.Verification.Running;
using Polypiler.Tooling.Bench;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Rebuild;

/// <summary>
/// <c>polypiler-tooling rebuild check --output &lt;dir&gt; [--runs &lt;n&gt;] [--input &lt;path&gt;]...
/// [--wall-time &lt;seconds&gt;] [--memory &lt;MiB&gt;] -- &lt;toolchain key&gt; &lt;args&gt;</c> runs a
/// generating command several times through the runner, each run in a fresh working directory, and
/// compares the output trees; <c>rebuild compare &lt;first&gt; &lt;second&gt;</c> compares two trees that
/// exist already, for a build that cannot run inside the runner. Both exempt what
/// <c>tooling/ci/rebuild-ignore.yaml</c> lists, print the tree hash of each run and, when every tree is
/// the same, the tree hash; every difference is a <c>PLY0036</c>.
/// </summary>
public sealed class RebuildCommand {

  /// <summary>The verb the command is invoked by.</summary>
  public const string Verb = "rebuild";

  private static readonly string[] _valueOptions = ["--output", "--runs", "--input", "--wall-time", "--memory"];

  private readonly string _currentDirectory;
  private readonly Func<string, IToolchainResolver> _toolchains;
  private readonly Func<IToolchainResolver, ICrossLanguageRunner> _runners;

  /// <summary>Creates the command for this machine: the toolchains of the repository lock, the sandbox of the host.</summary>
  public RebuildCommand()
    : this(Environment.CurrentDirectory, root => BenchCommand.LockToolchains(root).Resolver, resolver => new CrossLanguageRunner(SandboxFactory.ForHost(resolver), resolver)) { }

  /// <summary>Creates the command with everything it reads from the machine given.</summary>
  /// <param name="currentDirectory">Where relative paths start and the search for the repository root begins.</param>
  /// <param name="toolchains">The toolchains of a repository root.</param>
  /// <param name="runners">The runner that runs the command with a resolver.</param>
  public RebuildCommand(string currentDirectory, Func<string, IToolchainResolver> toolchains, Func<IToolchainResolver, ICrossLanguageRunner> runners) {
    this._currentDirectory = currentDirectory;
    this._toolchains = toolchains;
    this._runners = runners;
  }

  /// <summary>Runs the arguments that follow the verb.</summary>
  public int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) {
    ArgumentNullException.ThrowIfNull(args);
    try {
      return args.ToArray() switch {
        ["check", .. var rest] => this.Check(rest, output, error),
        ["compare", var first, var second] => this.Compare(first, second, output, error),
        ["compare", ..] => throw new RebuildUsageException("rebuild compare needs two directories"),
        [] => throw new RebuildUsageException("rebuild needs 'check' or 'compare'"),
        _ => throw new RebuildUsageException($"unknown rebuild command '{args[0]}'")
      };
    } catch (RebuildUsageException exception) {
      error.WriteLine($"{Program.Name}: error {ToolchainsCommand.UsageCode}: {exception.Message}");
      return Program.UsageError;
    } catch (ToolchainLockException exception) {
      foreach (var diagnostic in exception.Diagnostics)
        error.WriteLine(diagnostic);
      return Program.Failure;
    } catch (RebuildIgnoreException exception) {
      return Report([CorpusDiagnostics.Manifest($"rebuild ignore: {exception.Message}", RebuildIgnore.RepositoryPath, exception.Line)], error);
    }
  }

  private int Check(string[] args, TextWriter output, TextWriter error) {
    var separator = Array.IndexOf(args, "--");
    if (separator < 0 || separator == args.Length - 1)
      throw new RebuildUsageException("rebuild check needs '-- <toolchain key> <args>' after its options");

    var options = Options(args[..separator]);
    var directory = Single(options, "--output") ?? throw new RebuildUsageException("rebuild check needs '--output <dir>'");
    if (!RunPlan.IsRelativeName(directory))
      throw new RebuildUsageException($"option '--output': '{directory}' must be a relative directory without '..'");

    var runs = Number(options, "--runs", RebuildCheck.MinimumRuns) ?? RebuildCheck.MinimumRuns;
    var defaults = RunLimits.Default;
    var wallTime = Number(options, "--wall-time", 1) is { } seconds ? TimeSpan.FromSeconds(seconds) : defaults.WallTime;
    var memory = Number(options, "--memory", 1) is { } mebibytes ? (long)mebibytes << 20 : defaults.MemoryBytes;
    var request = new RebuildRequest(directory, new(args[separator + 1], args[(separator + 2)..]), runs) {
      Inputs = this.Inputs(options.GetValueOrDefault("--input") ?? []),
      Limits = new(wallTime, memory, defaults.OutputBytes, defaults.Processes),
      Ignore = this.Ignore(out var root)
    };
    var resolver = this._toolchains(root);
    var result = new RebuildCheck(this._runners(resolver)).RunAsync(request).GetAwaiter().GetResult();
    return Finish(result.Trees, result.Diagnostics, output, error);
  }

  private int Compare(string first, string second, TextWriter output, TextWriter error) {
    var ignore = this.Ignore(out _);
    var trees = new[] { first, second }.Select(directory => {
      var full = Path.GetFullPath(directory, this._currentDirectory);
      return Directory.Exists(full) ? TreeHash.Compute(full, ignore) : throw new RebuildUsageException($"directory '{directory}' does not exist");
    }).ToArray();
    return Finish(trees, [.. TreeComparison.Compare(trees[0], trees[1]).Select(difference => RebuildDiagnostics.Difference(difference, 2))], output, error);
  }

  private static int Finish(IReadOnlyList<TreeDigest> trees, IReadOnlyList<Diagnostic> diagnostics, TextWriter output, TextWriter error) {
    for (var run = 0; run < trees.Count; ++run)
      output.WriteLine($"run {run + 1}: {trees[run].Sha256}");
    if (diagnostics.Count == 0)
      output.WriteLine($"rebuild deterministic over {trees.Count} runs: {trees[0].Sha256}");

    return Report(diagnostics, error);
  }

  /// <summary>The ignore list of the repository root, nothing when the root has none.</summary>
  private RebuildIgnore Ignore(out string root) {
    root = this.Root();
    var file = Path.Combine(root, RebuildIgnore.RepositoryPath);
    return File.Exists(file) ? RebuildIgnore.Load(file) : RebuildIgnore.Empty;
  }

  /// <summary>
  /// The files every run starts with: a file under its own name, a directory under its own name with
  /// every file below it.
  /// </summary>
  private Dictionary<string, ReadOnlyMemory<byte>> Inputs(IReadOnlyList<string> paths) {
    var inputs = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
    foreach (var path in paths) {
      var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, this._currentDirectory));
      var name = Path.GetFileName(full);
      if (File.Exists(full))
        inputs[name] = File.ReadAllBytes(full);
      else if (Directory.Exists(full))
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
          inputs[$"{name}/{Path.GetRelativePath(full, file).Replace('\\', '/')}"] = File.ReadAllBytes(file);
      else
        throw new RebuildUsageException($"option '--input': '{path}' does not exist");
    }

    return inputs;
  }

  /// <summary>The repository root: the nearest directory at or above the current one with <c>tooling/toolchains.lock</c>, else the current directory.</summary>
  private string Root() {
    for (var directory = new DirectoryInfo(this._currentDirectory); directory != null; directory = directory.Parent)
      if (File.Exists(Path.Combine(directory.FullName, ToolchainLock.RepositoryPath)))
        return directory.FullName;

    return this._currentDirectory;
  }

  private static int Report(IReadOnlyList<Diagnostic> diagnostics, TextWriter error) {
    var bag = new DiagnosticBag();
    foreach (var diagnostic in diagnostics) {
      bag.Report(diagnostic);
      error.WriteLine($"{Program.Name}: {diagnostic}");
      if (diagnostic.Cause is { } cause)
        error.WriteLine($"  cause: {cause}");
    }

    return (int)bag.ExitCode;
  }

  /// <summary>The options before <c>--</c>: each maps to its values; only <c>--input</c> may be given more than once.</summary>
  private static Dictionary<string, List<string>> Options(string[] args) {
    var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    for (var index = 0; index < args.Length; ++index) {
      var name = args[index];
      if (!_valueOptions.Contains(name))
        throw new RebuildUsageException($"unknown option '{name}'");
      if (index + 1 == args.Length)
        throw new RebuildUsageException($"option '{name}' requires a value");

      var values = options.TryGetValue(name, out var existing) ? existing : options[name] = [];
      if (values.Count > 0 && name != "--input")
        throw new RebuildUsageException($"option '{name}' is given more than once");

      values.Add(args[++index]);
    }

    return options;
  }

  private static string? Single(Dictionary<string, List<string>> options, string name) => options.TryGetValue(name, out var values) ? values[0] : null;

  /// <summary>A whole-number option of at least a minimum; <see langword="null"/> when it is not given.</summary>
  private static int? Number(Dictionary<string, List<string>> options, string name, int minimum)
    => Single(options, name) is not { } text ? null
      : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= minimum ? value
      : throw new RebuildUsageException($"option '{name}' needs a whole number of at least {minimum}, not '{text}'");

}

/// <summary>Thrown for a rebuild command line that cannot be carried out as given; reported as <c>PLY0003</c>.</summary>
public sealed class RebuildUsageException : Exception {

  /// <summary>Creates the exception with the reason.</summary>
  public RebuildUsageException(string message) : base(message) { }

}
