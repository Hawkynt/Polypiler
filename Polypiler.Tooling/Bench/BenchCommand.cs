using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Polypiler.Core;
using Polypiler.Core.Verification.Benchmarks;
using Polypiler.Core.Verification.Running;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Bench;

/// <summary>The toolchains a benchmark run uses: their executables for the runner and their versions for the environment.</summary>
/// <param name="Resolver">Finds a toolchain's executable.</param>
/// <param name="Version">The version of a toolchain; <see langword="null"/> when it is not known.</param>
public sealed record BenchToolchains(IToolchainResolver Resolver, Func<string, string?> Version);

/// <summary>
/// <c>polypiler-tooling bench run (--id &lt;id&gt; | --all) [--out &lt;dir&gt;]</c> measures the definitions
/// of <c>tests/benchmarks/</c> and writes <c>&lt;out&gt;/&lt;id&gt;/&lt;variant&gt;.json</c> and
/// <c>&lt;out&gt;/summary.md</c> (default <c>artifacts/bench</c>); <c>bench compare --baseline &lt;dir&gt;
/// --current &lt;dir&gt; [--budgets &lt;file&gt;] [--out &lt;file.md&gt;]</c> compares two result sets and gates
/// only the metrics a budget file names.
/// </summary>
public sealed class BenchCommand {

  /// <summary>The verb the command is invoked by.</summary>
  public const string Verb = "bench";

  /// <summary>Where results are written without <c>--out</c>, relative to the repository root.</summary>
  public const string DefaultOut = "artifacts/bench";

  /// <summary>The placeholder of the build configuration polypiler-tooling was built in, for the path of <c>Polypiler.Benchmarks</c>.</summary>
  public const string ConfigurationPlaceholder = "{configuration}";

  private static readonly Dictionary<string, (string[] Values, string[] Flags)> _options = new(StringComparer.Ordinal) {
    ["run"] = (["--id", "--out"], ["--all"]),
    ["compare"] = (["--baseline", "--current", "--budgets", "--out"], [])
  };

  private readonly string _currentDirectory;
  private readonly Func<string, BenchToolchains> _toolchains;
  private readonly Func<IToolchainResolver, ICrossLanguageRunner> _runners;
  private readonly Func<string, string> _commit;
  private readonly Func<IReadOnlyDictionary<string, string>, BenchmarkEnvironment> _environment;

  /// <summary>Creates the command for this machine: the toolchains of the repository lock, the sandbox of the host, git for the commit.</summary>
  public BenchCommand()
    : this(Environment.CurrentDirectory, LockToolchains, resolver => new CrossLanguageRunner(SandboxFactory.ForHost(resolver), resolver), GitCommit, BenchmarkEnvironment.Detect) { }

  /// <summary>Creates the command with everything it reads from the machine given.</summary>
  /// <param name="currentDirectory">Where relative paths start and the search for the repository root begins.</param>
  /// <param name="toolchains">The toolchains of a repository root.</param>
  /// <param name="runners">The runner that runs the plans with a resolver.</param>
  /// <param name="commit">The commit checked out in a repository root.</param>
  /// <param name="environment">The environment of the toolchain versions given.</param>
  public BenchCommand(string currentDirectory, Func<string, BenchToolchains> toolchains, Func<IToolchainResolver, ICrossLanguageRunner> runners, Func<string, string> commit, Func<IReadOnlyDictionary<string, string>, BenchmarkEnvironment> environment) {
    this._currentDirectory = currentDirectory;
    this._toolchains = toolchains;
    this._runners = runners;
    this._commit = commit;
    this._environment = environment;
  }

  /// <summary>The toolchains of <c>tooling/toolchains.lock</c> below a repository root, each probed once.</summary>
  /// <exception cref="ToolchainLockException">The lock cannot be read or is invalid.</exception>
  public static BenchToolchains LockToolchains(string root) {
    var locator = new ToolchainLocator(ToolchainLock.Load(Path.Combine(root, ToolchainLock.RepositoryPath)), new ProcessRunner());
    var versions = new ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
    return new(new LockToolchainResolver(locator), key => versions.GetOrAdd(key, _ => locator.Lock.TryGet(key, out var _) ? locator.Probe(key).ActualVersion : null));
  }

  /// <summary>The commit <c>git rev-parse HEAD</c> names in a directory; <c>unknown</c> outside a git checkout.</summary>
  public static string GitCommit(string root) => Commit(new ProcessRunner(), root);

  /// <summary>The commit <c>git rev-parse HEAD</c> names in a directory, run by the given runner; <c>unknown</c> when git fails or is missing.</summary>
  public static string Commit(IProcessRunner runner, string root) {
    try {
      var result = runner.Run(["git", "-C", root, "rev-parse", "HEAD"], TimeSpan.FromSeconds(30));
      return result.ExitCode == 0 && result.StandardOutput.Trim() is { Length: > 0 } commit ? commit : "unknown";
    } catch (System.ComponentModel.Win32Exception) {
      return "unknown";
    }
  }

  /// <summary>Runs the arguments that follow the verb.</summary>
  public int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) {
    ArgumentNullException.ThrowIfNull(args);
    if (args.Count == 0 || !_options.ContainsKey(args[0]))
      return Usage(error, args.Count == 0 ? "bench needs 'run' or 'compare'" : $"unknown bench command '{args[0]}'");

    try {
      var options = Options(args);
      return args[0] == "run" ? this.RunBenchmarks(options, output, error) : this.Compare(options, output, error);
    } catch (BenchUsageException exception) {
      return Usage(error, exception.Message);
    } catch (ToolchainLockException exception) {
      foreach (var diagnostic in exception.Diagnostics)
        error.WriteLine(diagnostic);
      return Program.Failure;
    }
  }

  private int RunBenchmarks(Dictionary<string, string?> options, TextWriter output, TextWriter error) {
    var id = options.GetValueOrDefault("--id");
    var all = options.ContainsKey("--all");
    if ((id == null) == !all)
      throw new BenchUsageException("bench run needs either '--id <id>' or '--all'");

    var root = this.Root();
    var folder = Path.Combine(root, BenchmarkDefinition.RepositoryFolder);
    string[] files;
    if (all) {
      files = Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "*" + BenchmarkDefinition.Extension).Order(StringComparer.Ordinal)] : [];
    } else {
      var file = Path.Combine(folder, id + BenchmarkDefinition.Extension);
      files = BenchmarkDefinition.IsName(id!) && File.Exists(file) ? [file] : throw new BenchUsageException($"unknown benchmark '{id}': there is no {BenchmarkDefinition.RepositoryFolder}/{id}{BenchmarkDefinition.Extension}");
    }

    var outDirectory = Path.GetFullPath(options.GetValueOrDefault("--out") ?? Path.Combine(root, DefaultOut), this._currentDirectory);
    var toolchains = this._toolchains(root);
    var host = new BenchmarkHost(root, this._commit(root), toolchains.Version) {
      Environment = this._environment,
      Placeholders = new Dictionary<string, string>(StringComparer.Ordinal) { [ConfigurationPlaceholder] = Configuration(typeof(BenchCommand).Assembly) }
    };
    var bag = new DiagnosticBag();
    var runner = new BenchmarkRunner(this._runners(toolchains.Resolver), host, bag);
    var results = new List<BenchmarkResult>();
    foreach (var file in files) {
      var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
      BenchmarkDefinition definition;
      try {
        definition = BenchmarkDefinition.Load(file);
        if (Path.GetFileName(file) != definition.Id + BenchmarkDefinition.Extension)
          throw new BenchmarkFileException(file, 0, 0, $"'id' is '{definition.Id}', but the file is named '{Path.GetFileName(file)}'");
      } catch (BenchmarkFileException exception) {
        bag.Report(exception.ToDiagnostic() with { File = relative });
        continue;
      }

      foreach (var variant in definition.Variants) {
        var path = Path.Combine(outDirectory, definition.Id, variant.Name + ".json");
        var result = runner.Run(definition, variant.Name);
        if (result == null) {
          // A result of an earlier run would read as this run's.
          if (File.Exists(path))
            File.Delete(path);
          continue;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, result.ToJson());
        var wall = result.Stats[BenchmarkResult.WallNsMetric];
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"bench '{definition.Id}/{variant.Name}': {result.Samples.Count} samples, wall median {wall.Median} ns, MAD {wall.Mad} ns: {this.Shown(path)}"));
        results.Add(result);
      }
    }

    Directory.CreateDirectory(outDirectory);
    File.WriteAllText(Path.Combine(outDirectory, BenchmarkSummary.FileName), BenchmarkSummary.ToMarkdown(results));
    return Report(bag, error);
  }

  private int Compare(Dictionary<string, string?> options, TextWriter output, TextWriter error) {
    var baseline = this.ExistingDirectory(options, "--baseline");
    var current = this.ExistingDirectory(options, "--current");
    var bag = new DiagnosticBag();
    BudgetFile? budgets = null;
    if (options.GetValueOrDefault("--budgets") is { } budgetPath) {
      try {
        budgets = BudgetFile.Load(Path.GetFullPath(budgetPath, this._currentDirectory)) with { File = budgetPath };
      } catch (BenchmarkFileException exception) {
        bag.Report(exception.ToDiagnostic() with { File = budgetPath });
        return Report(bag, error);
      }
    }

    var table = BenchmarkComparer.Compare(baseline, current, budgets, bag).ToMarkdown();
    output.Write(table);
    if (options.GetValueOrDefault("--out") is { } outFile) {
      var full = Path.GetFullPath(outFile, this._currentDirectory);
      Directory.CreateDirectory(Path.GetDirectoryName(full)!);
      File.WriteAllText(full, table);
    }

    return Report(bag, error);
  }

  /// <summary>A path as the output names it: relative to the current directory when it lies below it, else in full.</summary>
  private string Shown(string path) {
    var relative = Path.GetRelativePath(this._currentDirectory, path);
    return (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative).Replace('\\', '/');
  }

  private string ExistingDirectory(Dictionary<string, string?> options, string name) {
    var value = options.GetValueOrDefault(name) ?? throw new BenchUsageException($"bench compare needs '{name} <dir>'");
    var full = Path.GetFullPath(value, this._currentDirectory);
    return Directory.Exists(full) ? full : throw new BenchUsageException($"option '{name}': directory '{value}' does not exist");
  }

  /// <summary>The configuration an assembly was built in, <c>Release</c> when it does not say.</summary>
  public static string Configuration(Assembly assembly) => assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration is { Length: > 0 } name ? name : "Release";

  /// <summary>The repository root: the nearest directory at or above the current one with <c>tooling/toolchains.lock</c>, else the current directory.</summary>
  private string Root() {
    for (var directory = new DirectoryInfo(this._currentDirectory); directory != null; directory = directory.Parent)
      if (File.Exists(Path.Combine(directory.FullName, ToolchainLock.RepositoryPath)))
        return directory.FullName;

    return this._currentDirectory;
  }

  private static int Report(DiagnosticBag bag, TextWriter error) {
    foreach (var diagnostic in bag.Items) {
      error.WriteLine($"{Program.Name}: {diagnostic}");
      if (diagnostic.Cause is { } cause)
        error.WriteLine($"  cause: {cause}");
    }

    return (int)bag.ExitCode;
  }

  /// <summary>The options after the subcommand: a value option maps to its value, a flag to <see langword="null"/>.</summary>
  private static Dictionary<string, string?> Options(IReadOnlyList<string> args) {
    var (values, flags) = _options[args[0]];
    var options = new Dictionary<string, string?>(StringComparer.Ordinal);
    for (var index = 1; index < args.Count; ++index) {
      var name = args[index];
      if (!values.Contains(name) && !flags.Contains(name))
        throw new BenchUsageException($"unknown option '{name}'");
      if (options.ContainsKey(name))
        throw new BenchUsageException($"option '{name}' is given more than once");
      if (flags.Contains(name)) {
        options[name] = null;
        continue;
      }

      if (index + 1 == args.Count)
        throw new BenchUsageException($"option '{name}' requires a value");
      options[name] = args[++index];
    }

    return options;
  }

  private static int Usage(TextWriter error, string message) {
    error.WriteLine($"{Program.Name}: error {ToolchainsCommand.UsageCode}: {message}");
    return Program.UsageError;
  }

}

/// <summary>Thrown for a bench command line that cannot be carried out as given; reported as <c>PLY0003</c>.</summary>
public sealed class BenchUsageException : Exception {

  /// <summary>Creates the exception with the reason.</summary>
  public BenchUsageException(string message) : base(message) { }

}
