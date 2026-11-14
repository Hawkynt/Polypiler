using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Tooling.Corpus;

namespace Polypiler.Tooling.Commands;

/// <summary>
/// <c>polypiler-tooling corpus import [--source &lt;dir&gt;]... [--update-lock] [--fixture &lt;id&gt;]...</c>
/// materializes the pinned upstream files; <c>corpus verify</c> re-hashes them offline against the
/// lock; <c>corpus discover --repository &lt;name&gt; --glob &lt;pattern&gt; [--source &lt;dir&gt;]...</c>
/// lists the files of a pinned commit as fixture stubs. The manifest, the lock and
/// <c>corpus/upstream</c> are found below the nearest directory, from the current one upwards, that
/// holds <c>tooling/corpus/corpus.yaml</c>.
/// </summary>
/// <param name="git">Runs git.</param>
/// <param name="currentDirectory">Where the search for the repository root starts.</param>
/// <param name="writeFile">Writes an imported file; <see langword="null"/> for the file system's own.</param>
public sealed class CorpusCommand(GitProcess git, string currentDirectory, Action<string, byte[]>? writeFile = null) {

  /// <summary>The verb the command is invoked by.</summary>
  public const string Verb = "corpus";

  /// <summary>Creates the command for this machine.</summary>
  public CorpusCommand()
    : this(new GitProcess(), Environment.CurrentDirectory) { }

  /// <summary>Runs the arguments that follow the verb and returns the exit code.</summary>
  public int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) {
    if (args.Count == 0 || args[0] is not ("import" or "verify" or "discover"))
      return Usage(error, args.Count == 0 ? "corpus needs 'import', 'verify' or 'discover'" : $"unknown corpus command '{args[0]}'");

    var command = args[0];
    var allowed = command switch {
      "import" => new[] { "--source", "--update-lock", "--fixture" },
      "discover" => ["--source", "--repository", "--glob"],
      _ => []
    };
    var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    var updateLock = false;
    for (var index = 1; index < args.Count; ++index) {
      var name = args[index];
      if (!allowed.Contains(name))
        return Usage(error, $"unknown option '{name}'");
      if (name == "--update-lock") {
        updateLock = true;
        continue;
      }

      if (index + 1 == args.Count)
        return Usage(error, $"option '{name}' requires a value");
      if (name is "--repository" or "--glob" && values.ContainsKey(name))
        return Usage(error, $"option '{name}' is given more than once");
      if (!values.TryGetValue(name, out var list))
        values[name] = list = [];
      list.Add(args[++index]);
    }

    if (command == "discover" && values.Keys.Count(key => key is "--repository" or "--glob") < 2)
      return Usage(error, "corpus discover needs '--repository' and '--glob'");

    var root = this.FindRoot();
    var sources = values.GetValueOrDefault("--source") ?? [];
    try {
      var manifest = CorpusManifest.Load(Path.Combine(root, CorpusManifest.RepositoryPath.Replace('/', Path.DirectorySeparatorChar)));
      var importer = new CorpusImporter(git, root, [.. sources.Select(Path.GetFullPath)], writeFile);
      return command switch {
        "import" => Import(importer, manifest, root, values.GetValueOrDefault("--fixture") ?? [], updateLock, output),
        "verify" => Verify(manifest, root, output, error),
        _ => this.Discover(importer, manifest, values["--repository"][0], values["--glob"][0], output)
      };
    } catch (CorpusException exception) {
      return Report(error, exception.Diagnostics);
    }
  }

  private static int Import(CorpusImporter importer, CorpusManifest manifest, string root, IReadOnlyList<string> fixtures, bool updateLock, TextWriter output) {
    var result = importer.Import(manifest, LoadLock(root), fixtures, updateLock);
    output.WriteLine($"{result.Files} corpus files imported{(updateLock ? $", {CorpusLock.RepositoryPath} rewritten" : "")}");
    return Program.Success;
  }

  private static int Verify(CorpusManifest manifest, string root, TextWriter output, TextWriter error) {
    var lockFile = LoadLock(root);
    var problems = CorpusVerifier.Verify(root, manifest, lockFile);
    if (problems.Count > 0)
      return Report(error, problems);

    output.WriteLine($"{lockFile.Entries.Count} corpus files verified");
    return Program.Success;
  }

  private int Discover(CorpusImporter importer, CorpusManifest manifest, string name, string glob, TextWriter output) {
    var repository = manifest.Repository(name) ?? throw new CorpusException(CorpusDiagnostics.Manifest($"repository '{name}' is not declared in {manifest.File}"));
    foreach (var path in FixtureDiscovery.Discover(git, importer.Locate(repository), repository, GlobPattern.Parse(glob)))
      output.Write(FixtureDiscovery.Stub(repository, path));
    return Program.Success;
  }

  private static CorpusLock LoadLock(string root) => CorpusLock.Load(Path.Combine(root, CorpusLock.RepositoryPath.Replace('/', Path.DirectorySeparatorChar)));

  /// <summary>The nearest directory, from the current one upwards, that holds the manifest; the current one when none does.</summary>
  private string FindRoot() {
    for (var directory = new DirectoryInfo(currentDirectory); directory != null; directory = directory.Parent)
      if (File.Exists(Path.Combine(directory.FullName, CorpusManifest.RepositoryPath.Replace('/', Path.DirectorySeparatorChar))))
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
