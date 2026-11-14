using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Polypiler.Core;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Commands;

/// <summary>
/// <c>polypiler-tooling toolchains validate [--lock &lt;path&gt;]</c> checks the lock without starting a
/// process; <c>polypiler-tooling toolchains probe [--lock &lt;path&gt;] [--only &lt;key&gt;[,&lt;key&gt;…]]
/// [--format text|json]</c> runs each toolchain's version command and compares it with the pin.
/// </summary>
public sealed class ToolchainsCommand {

  /// <summary>The verb the command is invoked by.</summary>
  public const string Verb = "toolchains";

  /// <summary>Exit code when the lock is invalid or a toolchain is missing or at another version.</summary>
  public const int Failure = 1;

  /// <summary>The diagnostic code of a command line that cannot be understood.</summary>
  public const string UsageCode = "PLY0003";

  /// <summary>The registered diagnostic of <see cref="UsageCode"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UsageDescriptor = new(UsageCode, DiagnosticSeverity.Error, ExitCode.UsageError, "{0}", "adr-0004.grammar");

  private readonly IProcessRunner _runner;
  private readonly Func<string, string?> _environment;
  private readonly string _platform;
  private readonly string _currentDirectory;
  private readonly TimeSpan _timeout;

  /// <summary>Creates the command for this machine.</summary>
  public ToolchainsCommand()
    : this(new ProcessRunner(), Environment.GetEnvironmentVariable, ToolchainLocator.CurrentPlatform, Environment.CurrentDirectory, ToolchainLocator.DefaultTimeout) { }

  /// <summary>Creates the command with everything it reads from the machine given.</summary>
  /// <param name="runner">Runs the version commands.</param>
  /// <param name="environment">Reads an environment variable.</param>
  /// <param name="platform">The platform probed for.</param>
  /// <param name="currentDirectory">Where the search for <c>tooling/toolchains.lock</c> starts when <c>--lock</c> is not given.</param>
  /// <param name="timeout">How long a version command may run.</param>
  public ToolchainsCommand(IProcessRunner runner, Func<string, string?> environment, string platform, string currentDirectory, TimeSpan timeout) {
    this._runner = runner;
    this._environment = environment;
    this._platform = platform;
    this._currentDirectory = currentDirectory;
    this._timeout = timeout;
  }

  /// <summary>Runs the arguments that follow the verb and returns the exit code.</summary>
  public int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) {
    if (args.Count == 0 || args[0] is not ("validate" or "probe"))
      return Usage(error, args.Count == 0 ? "toolchains needs 'validate' or 'probe'" : $"unknown toolchains command '{args[0]}'");

    var probe = args[0] == "probe";
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 1; index < args.Count; ++index) {
      var name = args[index];
      if (name is not ("--lock" or "--only" or "--format") || (!probe && name != "--lock"))
        return Usage(error, $"unknown option '{name}'");
      if (index + 1 == args.Count)
        return Usage(error, $"option '{name}' requires a value");
      if (!options.TryAdd(name, args[++index]))
        return Usage(error, $"option '{name}' is given more than once");
    }

    var format = options.GetValueOrDefault("--format", "text");
    if (format is not ("text" or "json"))
      return Usage(error, $"option '--format' does not allow '{format}'");

    string[]? only = null;
    if (options.TryGetValue("--only", out var list)) {
      only = list.Split(',');
      if (only.Any(key => key.Length == 0))
        return Usage(error, "option '--only' has an empty list element");
    }

    ToolchainLock toolchains;
    try {
      toolchains = ToolchainLock.Load(options.GetValueOrDefault("--lock") ?? this.FindLock());
    } catch (ToolchainLockException exception) {
      return Report(error, exception.Diagnostics);
    }

    return probe ? this.Probe(toolchains, only, format == "json", output, error) : Validate(toolchains, output, error);
  }

  private static int Validate(ToolchainLock toolchains, TextWriter output, TextWriter error) {
    var missing = toolchains.MissingCoreKeys();
    if (missing.Count > 0)
      return Report(error, missing);

    output.WriteLine($"{toolchains.File}: {toolchains.Entries.Count} toolchains, valid");
    return Program.Success;
  }

  private int Probe(ToolchainLock toolchains, string[]? only, bool json, TextWriter output, TextWriter error) {
    var unknown = (only ?? []).Where(key => !toolchains.TryGet(key, out _)).Select(toolchains.UnknownKey).ToArray();
    if (unknown.Length > 0)
      return Report(error, unknown);

    var keys = only?.Distinct().Order(StringComparer.Ordinal) ?? toolchains.Entries.Select(entry => entry.Key).Where(ToolchainLock.CoreKeys.Contains);
    var locator = new ToolchainLocator(toolchains, this._runner, this._environment, this._platform, this._timeout);
    var exitCode = Program.Success;
    foreach (var key in keys) {
      var result = locator.Probe(key);
      output.WriteLine(json ? Json(result) : Text(result));
      if (result.Problem is { } problem) {
        var entry = toolchains[key];
        error.WriteLine(new ToolchainDiagnostic(toolchains.File, entry.Line, entry.Column, problem.Code, problem.Message));
        exitCode = Failure;
      }
    }

    return exitCode;
  }

  private static string Text(ToolchainProbe result)
    => $"{result.Key} {result.Status} expected={result.ExpectedVersion} actual={result.ActualVersion ?? "-"} path={result.Path ?? "-"}";

  private static string Json(ToolchainProbe result) {
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer)) {
      writer.WriteStartObject();
      writer.WriteString("key", result.Key);
      writer.WriteString("status", result.Status.ToString());
      writer.WriteString("expected", result.ExpectedVersion);
      writer.WriteString("actual", result.ActualVersion);
      writer.WriteString("path", result.Path);
      writer.WriteString("cause", result.Cause);
      writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(buffer.ToArray());
  }

  /// <summary>The repository lock above the current directory, or the path it would have below it.</summary>
  private string FindLock() {
    for (var directory = new DirectoryInfo(this._currentDirectory); directory != null; directory = directory.Parent) {
      var candidate = Path.Combine(directory.FullName, "tooling", "toolchains.lock");
      if (File.Exists(candidate))
        return candidate;
    }

    return Path.Combine(this._currentDirectory, "tooling", "toolchains.lock");
  }

  private static int Report(TextWriter error, IEnumerable<ToolchainDiagnostic> diagnostics) {
    foreach (var diagnostic in diagnostics)
      error.WriteLine(diagnostic);
    return Failure;
  }

  private static int Usage(TextWriter error, string message) {
    error.WriteLine($"{Program.Name}: error {UsageCode}: {message}");
    return Program.UsageError;
  }

}
