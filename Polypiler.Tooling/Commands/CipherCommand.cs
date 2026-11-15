using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Tooling.Cipher;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Commands;

/// <summary>
/// <c>polypiler-tooling cipher kat (&lt;file&gt;... | --all) [--targets csharp,rust] [--require &lt;target&gt;,...]
/// [--polypiler &lt;path&gt;] [--report &lt;file.md&gt;] [--work &lt;dir&gt;]</c> checks the translations of
/// Cipher algorithm files on their known-answer tests (#1365): a file is named below <c>algorithms/</c>
/// (<c>checksum/adler.js</c>). The corpus is the one <c>corpus import --fixture cipher.algorithms</c>
/// imported, the golden files those of <c>tests/fixtures/corpus/cipher</c>, both below the nearest
/// directory, from the current one upwards, that holds <c>tooling/corpus/corpus.yaml</c>. Polypiler is
/// the Release build of <c>Polypiler.Cli</c> unless <c>--polypiler</c> names another. It prints the row
/// of every file and, with <c>--report</c>, writes the table. It fails when a target of <c>--require</c>
/// does not pass every checked vector of a file, and only then.
/// </summary>
/// <param name="runner">Starts polypiler and the toolchains.</param>
/// <param name="currentDirectory">Where the search for the repository root starts.</param>
public sealed class CipherCommand(IProcessRunner runner, string currentDirectory) {

  /// <summary>The verb the command is invoked by.</summary>
  public const string Verb = "cipher";

  /// <summary>The fixture of the manifest that pins the corpus.</summary>
  public const string Fixture = "cipher.algorithms";

  /// <summary>The folder of the golden files below the repository root.</summary>
  public const string Goldens = "tests/fixtures/corpus/cipher";

  /// <summary>The folder the translations and test projects go to below the repository root.</summary>
  public const string Work = "artifacts/cipher-kat";

  /// <summary>Creates the command for this machine.</summary>
  public CipherCommand()
    : this(new ProcessRunner(), Environment.CurrentDirectory) { }

  /// <summary>Runs the arguments that follow the verb and returns the exit code.</summary>
  public int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) {
    if (args.Count == 0 || args[0] != "kat")
      return Usage(error, args.Count == 0 ? "cipher needs 'kat'" : $"unknown cipher command '{args[0]}'");

    var files = new List<string>();
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    var all = false;
    for (var index = 1; index < args.Count; ++index) {
      var name = args[index];
      if (name == "--all") {
        all = true;
      } else if (name is "--targets" or "--require" or "--polypiler" or "--report" or "--work") {
        if (index + 1 == args.Count)
          return Usage(error, $"option '{name}' requires a value");
        options[name] = args[++index];
      } else if (name.StartsWith("--", StringComparison.Ordinal)) {
        return Usage(error, $"unknown option '{name}'");
      } else {
        files.Add(name.Replace('\\', '/'));
      }
    }

    if (all == (files.Count > 0))
      return Usage(error, "cipher kat needs the files to check or '--all', not both");

    var targets = new List<KatTarget>();
    foreach (var name in Names(options.GetValueOrDefault("--targets") ?? "csharp,rust")) {
      if (!KatTarget.Known.TryGetValue(name, out var target))
        return Usage(error, $"unknown target '{name}'");
      targets.Add(target);
    }

    var required = Names(options.GetValueOrDefault("--require") ?? "").ToList();
    if (required.FirstOrDefault(name => targets.All(target => target.Name != name)) is { } missing)
      return Usage(error, $"the required target '{missing}' is not checked");

    var root = this.FindRoot();
    CorpusManifest manifest;
    try {
      manifest = CorpusManifest.Load(Path.Combine(root, CorpusManifest.RepositoryPath));
    } catch (CorpusException exception) {
      foreach (var diagnostic in exception.Diagnostics)
        error.WriteLine(Core.DiagnosticRenderer.ToText(diagnostic));
      return Program.Failure;
    }

    if (manifest.Fixture(Fixture) is not { } fixture) {
      error.WriteLine($"{Program.Name}: {CorpusManifest.RepositoryPath} has no fixture '{Fixture}'");
      return Program.Failure;
    }

    var pin = fixture.PinOf(manifest.Repository(fixture.Repository)!);
    var corpus = Path.GetDirectoryName(CorpusImporter.FilePath(root, pin.Name, pin.Commit, fixture.Path))!;
    var algorithms = Path.Combine(corpus, "algorithms");
    if (!Directory.Exists(algorithms)) {
      error.WriteLine($"{Program.Name}: the Cipher corpus is not imported; run '{Program.Name} {CorpusCommand.Verb} import --fixture {Fixture}'");
      return Program.Failure;
    }

    if (all)
      files = [.. Directory.EnumerateFiles(algorithms, "*.js", SearchOption.AllDirectories)
        .Select(file => Path.GetRelativePath(algorithms, file).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal)];
    if (files.FirstOrDefault(file => !File.Exists(Path.Combine(algorithms, file))) is { } unknown) {
      error.WriteLine($"{Program.Name}: '{unknown}' is no algorithm file of the corpus");
      return Program.Failure;
    }

    var polypiler = options.TryGetValue("--polypiler", out var given) ? Command(given) : Command(Path.Combine(root, "Polypiler.Cli", "bin", "Release", "net10.0", "polypiler.dll"));
    var harness = new KatHarness(runner, polypiler, corpus, Path.Combine(root, Goldens), Path.GetFullPath(options.GetValueOrDefault("--work") ?? Path.Combine(root, Work)));
    var rows = new List<KatRow>();
    foreach (var file in files) {
      KatRow row;
      try {
        row = harness.Check(file, targets);
      } catch (Exception exception) when (exception is IOException or FormatException or UnauthorizedAccessException) {
        error.WriteLine($"{Program.Name}: the golden file of '{file}' cannot be read: {exception.Message}");
        return Program.Failure;
      }

      rows.Add(row);
      output.WriteLine($"{file}: {row.Checkable} / {row.Vectors} KATs checkable; {string.Join("; ", row.Targets.Select(result => $"{result.Target} {KatReport.Status(result)}"))}");
    }

    if (options.TryGetValue("--report", out var report)) {
      var header = $"""
        # Cipher corpus

        Every algorithm file of `Cipher/algorithms` of `{pin.Name}` at `{pin.Commit}`, translated from
        JavaScript and checked on the known-answer tests the file itself carries (#1365). Written by
        `{Program.Name} {Verb} kat --all --report docs/corpus/cipher.md` from the golden files of
        `{Goldens}`, which node records with `tooling/cipher/record-kats.mjs`.
        """;
      File.WriteAllText(report, KatReport.Markdown(rows, targets, header));
    }

    var failing = rows.Where(row => row.Targets.Any(result => required.Contains(result.Target) && !result.AllPassed)).ToList();
    foreach (var row in failing)
      error.WriteLine($"{Program.Name}: {row.File} does not pass every checked KAT on {string.Join(", ", row.Targets.Where(result => required.Contains(result.Target) && !result.AllPassed).Select(result => result.Target))}");
    return failing.Count == 0 ? Program.Success : Program.Failure;
  }

  /// <summary>The command line that starts polypiler: an assembly through <c>dotnet</c>, an executable as it is.</summary>
  public static IReadOnlyList<string> Command(string path)
    => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? ["dotnet", Path.GetFullPath(path)] : [Path.GetFullPath(path)];

  private static IEnumerable<string> Names(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

  /// <summary>The nearest directory, from the current one upwards, that holds the corpus manifest; the current one when none does.</summary>
  private string FindRoot() {
    for (var directory = new DirectoryInfo(currentDirectory); directory != null; directory = directory.Parent)
      if (File.Exists(Path.Combine(directory.FullName, CorpusManifest.RepositoryPath)))
        return directory.FullName;
    return currentDirectory;
  }

  private static int Usage(TextWriter error, string message) {
    error.WriteLine($"{Program.Name}: error {ToolchainsCommand.UsageCode}: {message}");
    return Program.UsageError;
  }

}
