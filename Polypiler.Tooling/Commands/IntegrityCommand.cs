using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Integrity;

namespace Polypiler.Tooling.Commands;

/// <summary>
/// <c>polypiler-tooling integrity check [--base &lt;git ref&gt;]</c> compares every golden file with
/// <c>tests/fixtures/golden.lock.json</c>, holds every vector file and provenance marker to the
/// provenance rules and, with <c>--base</c>, every commit after the base to the pull-request rule; it
/// writes nothing. <c>integrity seal &lt;path&gt;...</c> records a deliberate change of the named files
/// in the lock. The repository root is the nearest directory, from the current one upwards, that holds
/// <c>tests/fixtures</c>.
/// </summary>
/// <param name="git">Runs git for the pull-request rule.</param>
/// <param name="currentDirectory">Where the search for the repository root starts, and what relative paths are relative to.</param>
public sealed class IntegrityCommand(GitProcess git, string currentDirectory) {

  /// <summary>The verb the command is invoked by.</summary>
  public const string Verb = "integrity";

  /// <summary>Creates the command for this machine.</summary>
  public IntegrityCommand()
    : this(new GitProcess(), Environment.CurrentDirectory) { }

  /// <summary>Runs the arguments that follow the verb and returns the exit code.</summary>
  public int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) {
    if (args.Count == 0 || args[0] is not ("check" or "seal"))
      return Usage(error, args.Count == 0 ? "integrity needs 'check' or 'seal'" : $"unknown integrity command '{args[0]}'");

    var root = this.FindRoot();
    var checker = new IntegrityChecker(root);
    return args[0] == "check" ? Check(checker, new ChangeRule(git, root, checker), args, output, error) : this.Seal(checker, root, args, output, error);
  }

  private static int Check(IntegrityChecker checker, ChangeRule rule, IReadOnlyList<string> args, TextWriter output, TextWriter error) {
    string? baseRevision = null;
    for (var index = 1; index < args.Count; ++index) {
      if (args[index] != "--base")
        return Usage(error, $"unknown option '{args[index]}'");
      if (index + 1 == args.Count)
        return Usage(error, "option '--base' requires a value");
      if (baseRevision is not null)
        return Usage(error, "option '--base' is given more than once");
      baseRevision = args[++index];
    }

    if (baseRevision is not null && !rule.Resolves(baseRevision))
      return Usage(error, $"'{baseRevision}' does not name a commit of this repository");

    var problems = checker.Check().ToList();
    var commits = 0;
    if (baseRevision is not null)
      try {
        problems.AddRange(rule.Check(baseRevision, out commits));
      } catch (InvalidOperationException exception) {
        return Usage(error, exception.Message);
      }

    if (problems.Count > 0)
      return Report(error, problems);

    output.WriteLine($"{checker.Coverage().Files.Count} golden files verified{(baseRevision is null ? "" : $", {commits} commits since {baseRevision} checked")}");
    return Program.Success;
  }

  private int Seal(IntegrityChecker checker, string root, IReadOnlyList<string> args, TextWriter output, TextWriter error) {
    if (args.Count == 1)
      return Usage(error, "integrity seal needs the files to seal");

    var paths = new List<string>();
    foreach (var arg in args.Skip(1)) {
      var relative = Path.GetRelativePath(root, Path.GetFullPath(arg, currentDirectory)).Replace(Path.DirectorySeparatorChar, '/');
      if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        return Usage(error, $"'{arg}' is outside the repository root '{root}'");
      paths.Add(relative);
    }

    IReadOnlyList<Diagnostic> problems;
    try {
      problems = checker.Seal(paths);
    } catch (ArgumentException exception) {
      return Usage(error, exception.Message);
    }

    if (problems.Count > 0)
      return Report(error, problems);

    output.WriteLine($"{paths.Distinct(StringComparer.Ordinal).Count()} golden files sealed in {GoldenLock.RepositoryPath}");
    return Program.Success;
  }

  /// <summary>The nearest directory, from the current one upwards, that holds <c>tests/fixtures</c>; the current one when none does.</summary>
  private string FindRoot() {
    for (var directory = new DirectoryInfo(currentDirectory); directory != null; directory = directory.Parent)
      if (Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
        return directory.FullName;
    return currentDirectory;
  }

  private static int Report(TextWriter error, IEnumerable<Diagnostic> diagnostics) {
    foreach (var diagnostic in diagnostics)
      error.WriteLine(DiagnosticRenderer.ToText(diagnostic));
    return Program.Failure;
  }

  private static int Usage(TextWriter error, string message) {
    error.WriteLine($"{Program.Name}: error {ToolchainsCommand.UsageCode}: {message}");
    return Program.UsageError;
  }

}
