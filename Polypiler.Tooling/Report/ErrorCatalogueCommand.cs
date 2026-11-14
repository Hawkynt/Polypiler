using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Polypiler.Core;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Diagnostics;

namespace Polypiler.Tooling.Report;

/// <summary>
/// <c>polypiler-tooling report errors [--registry &lt;file&gt;] [--notes &lt;dir&gt;] [--out &lt;dir&gt;] [--check]</c>
/// generates the error catalogue from the diagnostics registry, by default from
/// <c>spec/diagnostics.yaml</c> with the notes of <c>docs/errors/notes</c> into <c>docs/errors</c>.
/// Without <c>--check</c> it writes the files that changed and deletes the pages of codes no longer
/// registered; with <c>--check</c> it writes nothing and reports every file that differs, is missing or
/// belongs to no registered code. Nothing is written while the registry or a note has a problem.
/// </summary>
public static class ErrorCatalogueCommand {

  /// <summary>The verb the command is invoked by.</summary>
  public const string Verb = "report";

  /// <summary>The report of <see cref="Verb"/> this command writes.</summary>
  public const string Report = "errors";

  /// <summary>The default directory of the notes, relative to the repository root.</summary>
  public const string DefaultNotes = "docs/errors/notes";

  /// <summary>The default output directory, relative to the repository root.</summary>
  public const string DefaultOut = "docs/errors";

  /// <summary>The code of a generated document that is out of date and of a note that cannot be merged.</summary>
  public const string OutOfDateCode = "PLY0091";

  /// <summary>The registered diagnostic of <see cref="OutOfDateCode"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor OutOfDate = new(OutOfDateCode, DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "docs.generated");

  /// <summary>The code of an output directory that cannot be written.</summary>
  public const string NotWritableCode = "PLY0005";

  /// <summary>The registered diagnostic of <see cref="NotWritableCode"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NotWritable = new(NotWritableCode, DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "io.write");

  /// <summary>Runs the arguments that follow the verb.</summary>
  /// <param name="args">The arguments after <see cref="Verb"/>.</param>
  /// <param name="output">Receives the summary line.</param>
  /// <param name="error">Receives the diagnostics and the refusal of a command line.</param>
  /// <returns>0 on success, 1 when something is out of date or cannot be read or written, 2 for a command line that cannot be understood.</returns>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    => Run(args, output, error, Environment.CurrentDirectory);

  /// <summary>Runs the arguments that follow the verb, with relative paths taken from a directory of the caller's.</summary>
  /// <param name="args">The arguments after <see cref="Verb"/>.</param>
  /// <param name="output">Receives the summary line.</param>
  /// <param name="error">Receives the diagnostics and the refusal of a command line.</param>
  /// <param name="currentDirectory">The directory relative paths and the defaults are taken from.</param>
  /// <returns>0 on success, 1 when something is out of date or cannot be read or written, 2 for a command line that cannot be understood.</returns>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, string currentDirectory) {
    if (args.Count == 0 || args[0] != Report)
      return Usage(error, args.Count == 0 ? $"{Verb} needs '{Report}'" : $"unknown report '{args[0]}'");

    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var check = false;
    for (var index = 1; index < args.Count; ++index) {
      var option = args[index];
      if (option == "--check") {
        if (check)
          return Usage(error, "option '--check' is given more than once");
        check = true;
        continue;
      }

      if (option is not ("--registry" or "--notes" or "--out"))
        return Usage(error, $"unknown option '{option}'");
      if (index + 1 == args.Count)
        return Usage(error, $"option '{option}' requires a value");
      if (!values.TryAdd(option, args[++index]))
        return Usage(error, $"option '{option}' is given more than once");
    }

    var registryPath = values.GetValueOrDefault("--registry") ?? DiagnosticRegistry.RepositoryPath;
    var notes = values.GetValueOrDefault("--notes") ?? DefaultNotes;
    var outDirectory = values.GetValueOrDefault("--out") ?? DefaultOut;

    var registry = DiagnosticRegistryValidator.Validate(Path.Combine(currentDirectory, registryPath));
    if (registry.Problems.Count > 0)
      return Fail(error, registry.Problems);

    var problems = new List<string>();
    var notesByCode = ReadNotes(registry, notes, Path.Combine(currentDirectory, notes), problems);
    if (problems.Count > 0)
      return Fail(error, problems.Select(problem => Diagnostic.Create(OutOfDate, null, problem)));

    var files = ErrorCatalogueWriter.Generate(registry, notesByCode);
    var target = Path.Combine(currentDirectory, outDirectory);
    return check
      ? Check(files, outDirectory, target, output, error)
      : Write(files, outDirectory, target, output, error);
  }

  /// <summary>
  /// Reads every <c>*.md</c> file of the notes directory; a missing directory holds no notes. A note
  /// whose name is not a registered code and a note that does not parse are problems.
  /// </summary>
  private static Dictionary<string, ErrorNote> ReadNotes(DiagnosticRegistry registry, string shown, string directory, List<string> problems) {
    var notes = new Dictionary<string, ErrorNote>(StringComparer.Ordinal);
    if (!Directory.Exists(directory))
      return notes;

    foreach (var file in Directory.GetFiles(directory, "*.md").Order(StringComparer.Ordinal)) {
      var name = Path.GetFileName(file);
      var path = Shown(shown, name);
      var code = Path.GetFileNameWithoutExtension(name);
      if (registry.Find(code) is null) {
        problems.Add($"note '{path}' belongs to no registered code");
        continue;
      }

      notes[code] = ErrorNote.Parse(File.ReadAllText(file), path, problems);
    }

    return notes;
  }

  /// <summary>Compares the generated files with the directory and reports every difference; writes nothing.</summary>
  private static int Check(IReadOnlyList<GeneratedFile> files, string shown, string directory, TextWriter output, TextWriter error) {
    var expected = files.ToDictionary(file => file.Path, StringComparer.Ordinal);
    var present = Directory.Exists(directory) ? Directory.GetFiles(directory).Select(Path.GetFileName).Cast<string>() : [];
    var problems = new List<Diagnostic>();
    foreach (var name in expected.Keys.Union(present, StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
      var file = Path.Combine(directory, name);
      string? state;
      if (!expected.TryGetValue(name, out var generated))
        state = "has no registered code";
      else if (!File.Exists(file))
        state = "is missing";
      else
        state = Normalized(File.ReadAllText(file)) == generated.Content ? null : "differs";

      if (state is not null)
        problems.Add(Diagnostic.Create(OutOfDate, null, $"generated document out of date: '{Shown(shown, name)}' {state}"));
    }

    if (problems.Count > 0)
      return Fail(error, problems);

    output.WriteLine($"{Counted(files.Count, "file")} up to date");
    return Program.Success;
  }

  /// <summary>Writes the files that changed and deletes the pages of codes no longer registered.</summary>
  private static int Write(IReadOnlyList<GeneratedFile> files, string shown, string directory, TextWriter output, TextWriter error) {
    var (written, deleted) = (0, 0);
    try {
      Directory.CreateDirectory(directory);
      foreach (var file in files) {
        var path = Path.Combine(directory, file.Path);
        if (File.Exists(path) && File.ReadAllText(path) == file.Content)
          continue;

        File.WriteAllText(path, file.Content, new UTF8Encoding(false));
        ++written;
      }

      var current = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
      foreach (var stale in Directory.GetFiles(directory, "PLY*.md").Where(path => !current.Contains(Path.GetFileName(path)) && DiagnosticDescriptor.IsCode(Path.GetFileNameWithoutExtension(path)))) {
        File.Delete(stale);
        ++deleted;
      }
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return Fail(error, [Diagnostic.Create(NotWritable, null, $"the output directory '{shown}' cannot be written: {exception.Message}")]);
    }

    output.WriteLine($"{Counted(files.Count, "file")}, {written} written, {deleted} deleted");
    return Program.Success;
  }

  /// <summary>A text with every <c>\r\n</c> read as <c>\n</c>, so that a checkout that converts line endings is not out of date.</summary>
  private static string Normalized(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

  /// <summary>A file of a directory as the command line named the directory, with a forward slash.</summary>
  private static string Shown(string directory, string name) => directory.TrimEnd('/', '\\') + "/" + name;

  private static string Counted(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

  private static int Fail(TextWriter error, IEnumerable<Diagnostic> problems) {
    foreach (var problem in problems)
      error.WriteLine(problem.Source is null ? $"{Program.Name}: {problem}" : DiagnosticRenderer.ToText(problem));
    return Program.Failure;
  }

  private static int Usage(TextWriter error, string message) {
    error.WriteLine($"{Program.Name}: error {ToolchainsCommand.UsageCode}: {message}");
    return Program.UsageError;
  }

}
