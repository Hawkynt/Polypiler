using System;
using System.Collections.Generic;
using System.IO;
using Polypiler.Core;
using Polypiler.Tooling.Catalog.AdapterAudit;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tooling.Commands;

/// <summary>
/// <c>polypiler-tooling catalog audit-adapter --audit &lt;file&gt; [--touch &lt;path&gt;]... [--corpus &lt;dir&gt;]</c>
/// holds an adapter licence audit against the imported corpus: <c>corpus/upstream</c>,
/// <c>tooling/corpus/corpus.lock.json</c> and <c>tooling/licensing</c> below the corpus root, which is
/// the folder <c>--corpus</c> names or else the nearest directory, from the current one upwards, that
/// holds the lock. Each <c>--touch</c> names a path of the audited repository, or the name of an external
/// component, that is about to be compiled, linked or loaded. Nothing is written.
/// </summary>
/// <param name="currentDirectory">Where the search for the corpus root starts, and what relative paths are relative to.</param>
public sealed class CatalogCommand(string currentDirectory) {

  /// <summary>The verb the command is invoked by.</summary>
  public const string Verb = "catalog";

  /// <summary>Creates the command for this process.</summary>
  public CatalogCommand()
    : this(Environment.CurrentDirectory) { }

  /// <summary>Runs the arguments that follow the verb and returns the exit code.</summary>
  public int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) {
    if (args.Count == 0 || args[0] != "audit-adapter")
      return Usage(error, args.Count == 0 ? "catalog needs 'audit-adapter'" : $"unknown catalog command '{args[0]}'");

    string? audit = null;
    string? corpus = null;
    var touches = new List<string>();
    for (var index = 1; index < args.Count; index += 2) {
      var name = args[index];
      if (name is not ("--audit" or "--touch" or "--corpus"))
        return Usage(error, $"unknown option '{name}'");
      if (index + 1 == args.Count)
        return Usage(error, $"option '{name}' requires a value");
      var value = args[index + 1];
      if (name == "--touch")
        touches.Add(value);
      else if ((name == "--audit" ? audit : corpus) is not null)
        return Usage(error, $"option '{name}' is given more than once");
      else if (name == "--audit")
        audit = value;
      else
        corpus = value;
    }

    if (audit is null)
      return Usage(error, "catalog audit-adapter needs '--audit <file>'");

    var root = corpus is null ? this.FindRoot() : Path.GetFullPath(corpus, currentDirectory);
    try {
      var loaded = AdapterAudit.Load(Path.GetFullPath(audit, currentDirectory));
      var policyFile = Path.Combine(root, LicencePolicy.RepositoryPath.Replace('/', Path.DirectorySeparatorChar));
      var policy = LicencePolicy.Load(policyFile);
      var texts = LicenceTexts.Load(Path.Combine(Path.GetDirectoryName(policyFile)!, LicencePolicy.TextsFolder));
      var lockFile = CorpusLock.Load(Path.Combine(root, CorpusLock.RepositoryPath.Replace('/', Path.DirectorySeparatorChar)));
      var problems = new AdapterAuditValidator(policy, texts).Validate(loaded, root, lockFile, touches);
      if (problems.Count > 0)
        return Report(error, problems);

      output.WriteLine($"{loaded.Records.Count} records and {loaded.External.Count} components of {loaded.Repository}@{loaded.Commit} checked"
        + (touches.Count == 0 ? "" : $", {touches.Count} touched {(touches.Count == 1 ? "path" : "paths")} usable"));
      return Program.Success;
    } catch (AdapterAuditException exception) {
      return Report(error, exception.Diagnostics);
    } catch (CorpusException exception) {
      return Report(error, exception.Diagnostics);
    }
  }

  /// <summary>The nearest directory, from the current one upwards, that holds the corpus lock; the current one when none does.</summary>
  private string FindRoot() {
    for (var directory = new DirectoryInfo(currentDirectory); directory != null; directory = directory.Parent)
      if (File.Exists(Path.Combine(directory.FullName, CorpusLock.RepositoryPath.Replace('/', Path.DirectorySeparatorChar))))
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
