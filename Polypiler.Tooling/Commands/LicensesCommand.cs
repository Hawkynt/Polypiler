using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tooling.Commands;

/// <summary>
/// <c>polypiler-tooling licenses scan --mode corpus|distribution [--root &lt;dir&gt;] [--report &lt;file&gt;] [--policy &lt;file&gt;]</c>
/// scans files for their licences: without <c>--root</c> the imported corpus of
/// <c>tooling/corpus/corpus.lock.json</c> (corpus mode only), with it every file below the folder. The
/// report goes to <c>artifacts/licenses/report.json</c> below the repository root unless another file
/// is named. <c>licenses inventory [--policy &lt;file&gt;]</c> holds <c>tooling/licensing/third-party.yaml</c>
/// against the repository. The repository root is the nearest directory, from the current one
/// upwards, that holds <c>tooling/licensing/policy.yaml</c>; the policy's folder holds
/// <c>DECISIONS.md</c> and the reference texts.
/// </summary>
/// <param name="currentDirectory">Where the search for the repository root starts, and what relative paths are relative to.</param>
public sealed class LicensesCommand(string currentDirectory) {

  /// <summary>The verb the command is invoked by.</summary>
  public const string Verb = "licenses";

  /// <summary>Creates the command for this process.</summary>
  public LicensesCommand()
    : this(Environment.CurrentDirectory) { }

  /// <summary>Runs the arguments that follow the verb and returns the exit code.</summary>
  public int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) {
    if (args.Count == 0 || args[0] is not ("scan" or "inventory"))
      return Usage(error, args.Count == 0 ? "licenses needs 'scan' or 'inventory'" : $"unknown licenses command '{args[0]}'");

    var command = args[0];
    string[] allowed = command == "scan" ? ["--mode", "--root", "--report", "--policy"] : ["--policy"];
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 1; index < args.Count; index += 2) {
      var name = args[index];
      if (!allowed.Contains(name))
        return Usage(error, $"unknown option '{name}'");
      if (index + 1 == args.Count)
        return Usage(error, $"option '{name}' requires a value");
      if (!values.TryAdd(name, args[index + 1]))
        return Usage(error, $"option '{name}' is given more than once");
    }

    LicenceMode mode = default;
    if (command == "scan") {
      if (!values.TryGetValue("--mode", out var modeName))
        return Usage(error, "licenses scan needs '--mode corpus' or '--mode distribution'");
      if (modeName is not ("corpus" or "distribution"))
        return Usage(error, $"unknown mode '{modeName}', expected corpus or distribution");
      mode = modeName == "corpus" ? LicenceMode.Corpus : LicenceMode.Distribution;
      if (mode == LicenceMode.Distribution && !values.ContainsKey("--root"))
        return Usage(error, "licenses scan --mode distribution needs '--root'");
    }

    var root = this.FindRoot();
    var policyFile = values.TryGetValue("--policy", out var named) ? Path.GetFullPath(named, currentDirectory) : Path.Combine(root, LicencePolicy.RepositoryPath.Replace('/', Path.DirectorySeparatorChar));
    try {
      var policy = LicencePolicy.Load(policyFile);
      return command == "scan"
        ? this.Scan(policy, policyFile, root, mode, values, output, error)
        : Inventory(policy, root, output, error);
    } catch (CorpusException exception) {
      return Report(error, exception.Diagnostics);
    }
  }

  private int Scan(LicencePolicy policy, string policyFile, string root, LicenceMode mode, Dictionary<string, string> values, TextWriter output, TextWriter error) {
    var texts = LicenceTexts.Load(Path.Combine(Path.GetDirectoryName(policyFile)!, LicencePolicy.TextsFolder));
    var scanner = new LicenceScanner(policy, texts);
    LicenceReport report;
    if (values.TryGetValue("--root", out var tree)) {
      var folder = Path.GetFullPath(tree, currentDirectory);
      if (!Directory.Exists(folder))
        return Usage(error, $"scan root '{tree}' is not a folder");
      report = scanner.ScanTree(folder, mode);
    } else {
      var manifest = CorpusManifest.Load(Path.Combine(root, CorpusManifest.RepositoryPath.Replace('/', Path.DirectorySeparatorChar)));
      var lockFile = CorpusLock.Load(Path.Combine(root, CorpusLock.RepositoryPath.Replace('/', Path.DirectorySeparatorChar)));
      report = scanner.ScanCorpus(root, manifest, lockFile, mode);
    }

    var file = values.TryGetValue("--report", out var named) ? Path.GetFullPath(named, currentDirectory) : Path.Combine(root, LicenceReport.DefaultPath.Replace('/', Path.DirectorySeparatorChar));
    try {
      Directory.CreateDirectory(Path.GetDirectoryName(file)!);
      File.WriteAllText(file, report.ToJson());
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return Report(error, [CorpusDiagnostics.Path($"licence report '{file}' cannot be written", exception.Message)]);
    }

    foreach (var diagnostic in report.Diagnostics)
      error.WriteLine(DiagnosticRenderer.ToText(diagnostic));
    output.WriteLine($"{report.Entries.Count} files scanned in {LicenceReport.Name(mode)} mode, {report.Diagnostics.Count} violations, report written to {file}");
    return report.ExitCode;
  }

  private static int Inventory(LicencePolicy policy, string root, TextWriter output, TextWriter error) {
    var inventory = ThirdPartyInventory.Load(Path.Combine(root, ThirdPartyInventory.RepositoryPath.Replace('/', Path.DirectorySeparatorChar)));
    var problems = inventory.Check(root, policy);
    if (problems.Count > 0)
      return Report(error, problems);

    output.WriteLine($"{inventory.Notices.Count} notices and {inventory.Packages.Count} packages checked");
    return Program.Success;
  }

  /// <summary>The nearest directory, from the current one upwards, that holds the policy; the current one when none does.</summary>
  private string FindRoot() {
    for (var directory = new DirectoryInfo(currentDirectory); directory != null; directory = directory.Parent)
      if (File.Exists(Path.Combine(directory.FullName, LicencePolicy.RepositoryPath.Replace('/', Path.DirectorySeparatorChar))))
        return directory.FullName;
    return currentDirectory;
  }

  private static int Report(TextWriter error, IReadOnlyList<Diagnostic> diagnostics) {
    foreach (var diagnostic in diagnostics)
      error.WriteLine(DiagnosticRenderer.ToText(diagnostic));
    return LicenceDiagnostics.ExitCodeOf(diagnostics);
  }

  private static int Usage(TextWriter error, string message) {
    error.WriteLine($"{Program.Name}: error {ToolchainsCommand.UsageCode}: {message}");
    return Program.UsageError;
  }

}
