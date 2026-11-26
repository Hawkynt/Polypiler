using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Polypiler.Core;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Report;

/// <summary>
/// <c>polypiler-tooling report showcase</c>: generates or checks the cross-backend, dialect-grouped
/// C# translation showcase document by invoking the latest <c>polypiler</c> binary across all
/// discovered backends and dialect profiles.
/// </summary>
public static class ShowcaseCommand {

  /// <summary>The subcommand under <c>report</c>.</summary>
  public const string Report = "showcase";

  /// <summary>Default directory holding the curated C# showcase examples.</summary>
  public const string DefaultExamples = "docs/showcase";

  /// <summary>Default output Markdown file.</summary>
  public const string DefaultOut = "docs/translation-showcase.md";

  /// <summary>Runs <c>report showcase</c> in the process's current directory using the real process runner.</summary>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    => Run(args, output, error, Environment.CurrentDirectory, new ProcessRunner());

  /// <summary>Runs <c>report showcase</c> against a caller-supplied working directory and process runner.</summary>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, string currentDirectory, IProcessRunner runner) {
    ArgumentNullException.ThrowIfNull(args);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(error);
    ArgumentNullException.ThrowIfNull(currentDirectory);
    ArgumentNullException.ThrowIfNull(runner);

    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var check = false;
    for (var index = 0; index < args.Count; ++index) {
      var option = args[index];
      if (option == "--check") {
        if (check)
          return Usage(error, "option '--check' is given more than once");
        check = true;
        continue;
      }

      if (option is not ("--examples" or "--out" or "--polypiler" or "--targets"))
        return Usage(error, $"unknown option '{option}'");
      if (index + 1 == args.Count)
        return Usage(error, $"option '{option}' requires a value");
      if (!values.TryAdd(option, args[++index]))
        return Usage(error, $"option '{option}' is given more than once");
    }

    var examplesOption = values.GetValueOrDefault("--examples") ?? DefaultExamples;
    var outOption = values.GetValueOrDefault("--out") ?? DefaultOut;
    var examplesPath = Path.GetFullPath(Path.Combine(currentDirectory, examplesOption));
    var outPath = Path.GetFullPath(Path.Combine(currentDirectory, outOption));
    if (!Directory.Exists(examplesPath))
      return Fail(error, Diagnostic.Create(ToolchainDiagnostic.UnreadableDescriptor, null, $"'{examplesOption}' cannot be read: the examples directory does not exist"));

    var polypiler = ResolvePolypiler(currentDirectory, values.GetValueOrDefault("--polypiler"));
    if (polypiler is null)
      return Fail(error, Diagnostic.Create(ToolchainDiagnostic.UnreadableDescriptor, null, "could not locate the built polypiler executable; build Polypiler.Cli or pass --polypiler <path>"));

    IReadOnlyList<ShowcaseTarget> discovered;
    try {
      discovered = ShowcaseGenerator.DiscoverTargets(runner, polypiler);
    } catch (Exception exception) {
      return Fail(error, Diagnostic.Create(ToolchainDiagnostic.UnreadableDescriptor, null, exception.Message));
    }

    IReadOnlyList<ShowcaseTarget> targets;
    if (values.TryGetValue("--targets", out var targetList)) {
      var filter = targetList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      if (filter.Length == 0)
        return Usage(error, "option '--targets' must name at least one backend");
      targets = FilterTargets(discovered, filter);
      if (targets.Count == 0)
        return Usage(error, $"option '--targets' matched no discovered backend in '{targetList}'");
    } else {
      targets = discovered;
    }

    var examples = ShowcaseGenerator.LoadExamples(examplesPath);
    var relativeExamples = Path.GetRelativePath(Path.GetDirectoryName(outPath)!, examplesPath).Replace('\\', '/');
    string markdown;
    try {
      markdown = ShowcaseGenerator.Generate(runner, polypiler, examples, targets, relativeExamples);
    } catch (Exception exception) {
      return Fail(error, Diagnostic.Create(ToolchainDiagnostic.UnreadableDescriptor, null, exception.Message));
    }

    return check
      ? Check(outOption, outPath, markdown, output, error)
      : Write(outOption, outPath, markdown, output, error);
  }

  private static IReadOnlyList<ShowcaseTarget> FilterTargets(IReadOnlyList<ShowcaseTarget> discovered, IReadOnlyList<string> filter) {
    var byBackend = discovered.ToDictionary(t => t.Backend, StringComparer.Ordinal);
    var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    var order = new List<string>();
    foreach (var entry in filter) {
      var colon = entry.IndexOf(':');
      var backend = colon < 0 ? entry : entry[..colon];
      var profile = colon < 0 ? null : entry[(colon + 1)..];
      if (!byBackend.TryGetValue(backend, out var full))
        continue;

      if (!grouped.TryGetValue(backend, out var profiles)) {
        profiles = [];
        grouped[backend] = profiles;
        order.Add(backend);
      }

      if (profile is null) {
        foreach (var p in full.Profiles)
          if (!profiles.Contains(p))
            profiles.Add(p);
      } else if (full.Profiles.Contains(profile) && !profiles.Contains(profile)) {
        profiles.Add(profile);
      }
    }

    return [.. order.Where(b => grouped[b].Count > 0).Select(b => new ShowcaseTarget(b, grouped[b]))];
  }

  private static IReadOnlyList<string>? ResolvePolypiler(string currentDirectory, string? explicitPath) {
    if (explicitPath is not null) {
      var full = Path.GetFullPath(Path.Combine(currentDirectory, explicitPath));
      if (!File.Exists(full))
        return null;
      return full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? ["dotnet", full] : [full];
    }

    string[] candidates = [
      Path.Combine(currentDirectory, "Polypiler.Cli", "bin", "Release", "net10.0", OperatingSystem.IsWindows() ? "polypiler.exe" : "polypiler"),
      Path.Combine(currentDirectory, "Polypiler.Cli", "bin", "Release", "net10.0", "polypiler.dll"),
      Path.Combine(currentDirectory, "Polypiler.Cli", "bin", "Debug", "net10.0", OperatingSystem.IsWindows() ? "polypiler.exe" : "polypiler"),
      Path.Combine(currentDirectory, "Polypiler.Cli", "bin", "Debug", "net10.0", "polypiler.dll")
    ];

    foreach (var candidate in candidates)
      if (File.Exists(candidate))
        return candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? ["dotnet", candidate] : [candidate];

    return null;
  }

  private static int Check(string shown, string path, string expected, TextWriter output, TextWriter error) {
    if (!File.Exists(path))
      return Fail(error, Diagnostic.Create(ErrorCatalogueCommand.OutOfDate, null, $"generated document out of date: '{shown}' is missing"));

    var actual = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
    if (actual != expected)
      return Fail(error, Diagnostic.Create(ErrorCatalogueCommand.OutOfDate, null, $"generated document out of date: '{shown}' differs"));

    output.WriteLine($"{shown} up to date");
    return Program.Success;
  }

  private static int Write(string shown, string path, string content, TextWriter output, TextWriter error) {
    try {
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      var unchanged = File.Exists(path) && File.ReadAllText(path) == content;
      if (!unchanged)
        File.WriteAllText(path, content, new UTF8Encoding(false));
      output.WriteLine(unchanged ? $"{shown} unchanged" : $"{shown} written");
      return Program.Success;
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return Fail(error, Diagnostic.Create(ErrorCatalogueCommand.NotWritable, null, $"the output file '{shown}' cannot be written: {exception.Message}"));
    }
  }

  private static int Fail(TextWriter error, Diagnostic diagnostic) {
    error.WriteLine($"{Program.Name}: {diagnostic}");
    return Program.Failure;
  }

  private static int Usage(TextWriter error, string message) {
    error.WriteLine($"{Program.Name}: error {ToolchainsCommand.UsageCode}: {message}");
    return Program.UsageError;
  }

}
