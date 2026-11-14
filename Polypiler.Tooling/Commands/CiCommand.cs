using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Tooling.Ci;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Commands;

/// <summary>
/// <c>polypiler-tooling ci audit|matrix|env</c>, the commands of the CI layers. Each reads the
/// repository at <c>--root</c>, the current directory by default.
/// <list type="bullet">
/// <item><c>ci audit</c> runs <see cref="CiAudit"/>: one line per layer, then
/// <c>&lt;n&gt; categories, &lt;m&gt; problems</c> on standard output, every problem on standard error.</item>
/// <item><c>ci matrix --changed &lt;file&gt;</c> prints, as a JSON array sorted by name, the layers the
/// changed paths of the file (one per line) select, the <c>always</c> layers included; <c>--all</c>
/// prints every layer.</item>
/// <item><c>ci env --layer &lt;name&gt;</c> prints <c>&lt;key&gt;=&lt;version&gt;</c> for every toolchain of
/// the layer, in key order, for <c>$GITHUB_OUTPUT</c>.</item>
/// </list>
/// </summary>
public static class CiCommand {

  /// <summary>The verb the command is invoked by.</summary>
  public const string Verb = "ci";

  /// <summary>Runs the arguments that follow the verb in the process's current directory.</summary>
  /// <param name="args">The arguments after <see cref="Verb"/>.</param>
  /// <param name="output">Receives what the command prints.</param>
  /// <param name="error">Receives the problems and the refusal of a command line.</param>
  /// <returns>0 without problems, 1 with, 2 for a command line that cannot be understood.</returns>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) => Run(args, output, error, Environment.CurrentDirectory);

  /// <summary>Runs the arguments that follow the verb.</summary>
  /// <param name="args">The arguments after <see cref="Verb"/>.</param>
  /// <param name="output">Receives what the command prints.</param>
  /// <param name="error">Receives the problems and the refusal of a command line.</param>
  /// <param name="currentDirectory">The directory relative paths are resolved against.</param>
  /// <returns>0 without problems, 1 with, 2 for a command line that cannot be understood.</returns>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, string currentDirectory) {
    ArgumentNullException.ThrowIfNull(args);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(error);
    if (args.Count == 0 || args[0] is not ("audit" or "matrix" or "env"))
      return Usage(error, args.Count == 0 ? "ci needs 'audit', 'matrix' or 'env'" : $"unknown ci command '{args[0]}'");

    string[] allowed = args[0] switch {
      "audit" => ["--root"],
      "matrix" => ["--root", "--changed", "--all"],
      _ => ["--root", "--layer"]
    };
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 1; index < args.Count; ++index) {
      var name = args[index];
      if (!allowed.Contains(name))
        return Usage(error, $"unknown option '{name}'");
      if (name == "--all") {
        if (!options.TryAdd(name, ""))
          return Usage(error, $"option '{name}' is given more than once");
        continue;
      }

      if (index + 1 == args.Count)
        return Usage(error, $"option '{name}' requires a value");
      if (!options.TryAdd(name, args[++index]))
        return Usage(error, $"option '{name}' is given more than once");
    }

    var root = Path.GetFullPath(Path.Combine(currentDirectory, options.GetValueOrDefault("--root", ".")));
    try {
      return args[0] switch {
        "audit" => Audit(root, output, error),
        "matrix" => Matrix(root, options, currentDirectory, output, error),
        _ => Env(root, options, output, error)
      };
    } catch (CiEnvironmentException exception) {
      error.WriteLine(CiDiagnostics.Render(exception.Diagnostic));
      return Program.Failure;
    }
  }

  private static int Audit(string root, TextWriter output, TextWriter error) {
    var result = CiAudit.Run(root);
    foreach (var line in result.Layers)
      output.WriteLine(line);
    output.WriteLine($"{result.Categories} categories, {result.Problems.Count} problems");
    return Report(result.Problems, error);
  }

  private static int Matrix(string root, Dictionary<string, string> options, string currentDirectory, TextWriter output, TextWriter error) {
    var all = options.ContainsKey("--all");
    if (all == options.ContainsKey("--changed"))
      return Usage(error, "ci matrix needs either '--changed <file>' or '--all'");

    var (layers, problems) = CiLayer.LoadAll(root);
    if (problems.Count > 0)
      return Report(problems, error);

    var changed = all ? [] : ChangedPaths(options["--changed"], currentDirectory);
    output.WriteLine(CiMatrix.Json(CiMatrix.Select(layers, changed, all)));
    return Program.Success;
  }

  private static string[] ChangedPaths(string file, string currentDirectory) {
    var path = Path.Combine(currentDirectory, file);
    if (!File.Exists(path))
      throw new CiEnvironmentException(CiDiagnostics.Unreadable(file, "the file does not exist"));

    return File.ReadAllLines(path);
  }

  private static int Env(string root, Dictionary<string, string> options, TextWriter output, TextWriter error) {
    if (!options.TryGetValue("--layer", out var name))
      return Usage(error, "ci env needs '--layer <name>'");

    var (layers, problems) = CiLayer.LoadAll(root);
    if (layers.FirstOrDefault(layer => layer.Name == name) is not { } selected)
      return Report([.. problems, CiDiagnostics.InconsistentWith($"unknown layer '{name}'")], error);

    ToolchainLock toolchains;
    try {
      toolchains = ToolchainLock.Load(Path.Combine(root, ToolchainLock.RepositoryPath));
    } catch (ToolchainLockException exception) {
      throw new CiEnvironmentException(CiDiagnostics.Unreadable(ToolchainLock.RepositoryPath, exception.Diagnostics[0].Message));
    }

    var unknown = selected.Toolchains.Where(key => !toolchains.TryGet(key, out _)).ToArray();
    if (unknown.Length > 0)
      return Report([.. unknown.Select(key => CiDiagnostics.InconsistentWith($"layer '{name}' names unknown toolchain '{key}'"))], error);

    foreach (var key in selected.Toolchains.Order(StringComparer.Ordinal))
      output.Write($"{key}={toolchains[key].Version}\n");
    return Program.Success;
  }

  private static int Report(IEnumerable<Diagnostic> problems, TextWriter error) {
    var failed = false;
    foreach (var problem in problems) {
      error.WriteLine(CiDiagnostics.Render(problem));
      failed = true;
    }

    return failed ? Program.Failure : Program.Success;
  }

  private static int Usage(TextWriter error, string message) {
    error.WriteLine($"{Program.Name}: error {ToolchainsCommand.UsageCode}: {message}");
    return Program.UsageError;
  }

}
