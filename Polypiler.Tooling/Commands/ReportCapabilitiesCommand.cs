using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Polypiler.Core;
using Polypiler.Core.Verification.Capabilities;
using Polypiler.Core.Verification.Gates;
using Polypiler.Tooling.Ci;
using Polypiler.Tooling.Report;
using Polypiler.Tooling.Spec;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Commands;

/// <summary>
/// <c>polypiler-tooling report capabilities [--gates &lt;dir&gt;] [--spec &lt;dir&gt;] [--out &lt;dir&gt;] [--commit &lt;sha&gt;] [--check]</c>
/// computes the <see cref="CapabilityReport"/> of the source and target profiles under <c>spec/</c>
/// from the gate records of <c>artifacts/gates</c> and writes <c>docs/capabilities.json</c> and
/// <c>docs/capabilities.md</c>. With <c>--check</c> it writes nothing and compares the committed files
/// with the evidence: a cell above it is <c>PLY0061</c>, a cell below it, a changed pair set and a
/// Markdown file that does not match the JSON file are <c>PLY0091</c>. A gate directory that does not
/// exist is an error, never an empty set of records.
/// </summary>
public static partial class ReportCapabilitiesCommand {

  /// <summary>The report of <see cref="ErrorCatalogueCommand.Verb"/> this command writes.</summary>
  public const string Report = "capabilities";

  /// <summary>The default gate directory, relative to the repository root.</summary>
  public const string DefaultGates = "artifacts/gates";

  /// <summary>The default spec root, relative to the repository root.</summary>
  public const string DefaultSpec = "spec";

  /// <summary>The default output directory, relative to the repository root.</summary>
  public const string DefaultOut = "docs";

  private const string LanguagePrefix = "LANGUAGES.";

  private const string TargetPrefix = "TARGETS.";

  /// <summary>Runs the arguments that follow <c>report capabilities</c> in the process's current directory, naming the commit of <see cref="GateRecorder.CommitVariable"/>.</summary>
  /// <param name="args">The arguments after <see cref="Report"/>.</param>
  /// <param name="output">Receives the summary line.</param>
  /// <param name="error">Receives the diagnostics and the refusal of a command line.</param>
  /// <returns>0 on success, 1 when a record or a committed file is wrong or a file cannot be read or written, 2 for a command line that cannot be understood.</returns>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    => Run(args, output, error, Environment.CurrentDirectory, Environment.GetEnvironmentVariable(GateRecorder.CommitVariable));

  /// <summary>Runs the arguments that follow <c>report capabilities</c>, with relative paths taken from a directory of the caller's.</summary>
  /// <param name="args">The arguments after <see cref="Report"/>.</param>
  /// <param name="output">Receives the summary line.</param>
  /// <param name="error">Receives the diagnostics and the refusal of a command line.</param>
  /// <param name="currentDirectory">The directory relative paths and the defaults are taken from.</param>
  /// <param name="environmentCommit">The commit the environment names; used without <c>--commit</c> when it is a 40-digit hash, else the report names <see cref="CapabilityReport.UnknownCommit"/>.</param>
  /// <returns>0 on success, 1 when a record or a committed file is wrong or a file cannot be read or written, 2 for a command line that cannot be understood.</returns>
  public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, string currentDirectory, string? environmentCommit) {
    ArgumentNullException.ThrowIfNull(args);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(error);
    ArgumentNullException.ThrowIfNull(currentDirectory);

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

      if (option is not ("--gates" or "--spec" or "--out" or "--commit"))
        return Usage(error, $"unknown option '{option}'");
      if (index + 1 == args.Count)
        return Usage(error, $"option '{option}' requires a value");
      if (!values.TryAdd(option, args[++index]))
        return Usage(error, $"option '{option}' is given more than once");
    }

    var commit = values.GetValueOrDefault("--commit") ?? (environmentCommit is not null && Commit40().IsMatch(environmentCommit) ? environmentCommit : CapabilityReport.UnknownCommit);
    if (!Commit40().IsMatch(commit))
      return Usage(error, $"'--commit' needs 40 lower-case hexadecimal digits, not '{commit}'");

    var gates = values.GetValueOrDefault("--gates") ?? DefaultGates;
    var spec = values.GetValueOrDefault("--spec") ?? DefaultSpec;
    var outDirectory = values.GetValueOrDefault("--out") ?? DefaultOut;
    var gatesPath = Path.GetFullPath(Path.Combine(currentDirectory, gates));
    var specPath = Path.GetFullPath(Path.Combine(currentDirectory, spec));
    var outPath = Path.GetFullPath(Path.Combine(currentDirectory, outDirectory));
    foreach (var (shown, path, what) in new[] { (spec, specPath, "spec root"), (gates, gatesPath, "gate directory") })
      if (!Directory.Exists(path))
        return Fail(error, [Diagnostic.Create(ToolchainDiagnostic.UnreadableDescriptor, null, $"'{shown}' cannot be read: the {what} does not exist")]);

    var tree = SpecTree.Load(specPath);
    if (tree.Unreadable.Count > 0)
      return Fail(error, tree.Unreadable);

    var diagnostics = new DiagnosticBag();
    var store = GateStore.Load(gatesPath, diagnostics);
    var (sources, targets) = Profiles(tree);
    var report = CapabilityReport.Build(sources, targets, store, Portable(Path.GetRelativePath(currentDirectory, gatesPath)), commit, diagnostics);
    var problems = new List<Diagnostic>(diagnostics.Items);
    var files = (Json: Shown(outDirectory, CapabilityReport.JsonFile), Markdown: Shown(outDirectory, CapabilityReport.MarkdownFile));
    var summary = check
      ? Check(report, outPath, files, problems)
      : Write(report, outPath, outDirectory, files, problems);
    if (problems.Count > 0)
      return Fail(error, problems);

    output.WriteLine(summary);
    return Program.Success;
  }

  /// <summary>
  /// The source and target profiles of a spec tree. A source profile is a <c>languages</c> document of
  /// kind <c>profile</c> with the ID <c>LANGUAGES.&lt;language&gt;.&lt;profile&gt;</c>, its limitations
  /// the <c>constructs</c> in state <c>diagnosed</c>; a target profile is a <c>targets</c> document
  /// with the ID <c>TARGETS.&lt;language&gt;.&lt;profile&gt;</c>, read from its <c>language</c> and
  /// <c>profile</c>. A document that is no such profile is not one.
  /// </summary>
  /// <param name="tree">The spec tree.</param>
  /// <returns>The profiles, in the order of the tree's documents.</returns>
  public static (IReadOnlyList<CapabilitySource> Sources, IReadOnlyList<CapabilityTarget> Targets) Profiles(SpecTree tree) {
    ArgumentNullException.ThrowIfNull(tree);
    var sources = new List<CapabilitySource>();
    var targets = new List<CapabilityTarget>();
    foreach (var document in tree.Documents) {
      if (document.Body is not JsonObject body || document.Id is not { } id)
        continue;

      var language = Text(body["language"]) is { } reference && reference.StartsWith(LanguagePrefix, StringComparison.Ordinal) ? reference[LanguagePrefix.Length..] : null;
      if (language is null)
        continue;

      if (document.Family == "languages" && Text(body["kind"]) == "profile" && id.StartsWith(LanguagePrefix + language + ".", StringComparison.Ordinal))
        sources.Add(new(language, id[(LanguagePrefix.Length + language.Length + 1)..], Limitations(body)));
      else if (document.Family == "targets" && Text(body["profile"]) is { } profile && id == TargetPrefix + language + "." + profile)
        targets.Add(new(language, profile));
    }

    return (sources, targets);
  }

  private static List<CapabilityLimitation> Limitations(JsonObject body)
    => [.. (body["constructs"] as JsonArray ?? [])
      .OfType<JsonObject>()
      .Where(construct => Text(construct["state"]) == "diagnosed" && Text(construct["construct"]) is not null)
      .Select(construct => new CapabilityLimitation(Text(construct["construct"])!, Text(construct["diagnostic"]) ?? ""))];

  private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

  /// <summary>Writes both files through temporary files renamed over them, so that a failure leaves no partial file.</summary>
  private static string Write(CapabilityReport report, string directory, string shown, (string Json, string Markdown) files, List<Diagnostic> problems) {
    var contents = new[] { (CapabilityReport.JsonFile, Text(report.WriteJson)), (CapabilityReport.MarkdownFile, Text(report.WriteMarkdown)) };
    var temporaries = new List<string>();
    try {
      Directory.CreateDirectory(directory);
      var staged = new List<(string Temporary, string Target)>();
      foreach (var (name, content) in contents) {
        var temporary = Path.Combine(directory, $".{name}.{Guid.NewGuid():N}.tmp");
        temporaries.Add(temporary);
        File.WriteAllBytes(temporary, new UTF8Encoding(false).GetBytes(content));
        staged.Add((temporary, Path.Combine(directory, name)));
      }

      foreach (var (temporary, target) in staged)
        File.Move(temporary, target, true);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      problems.Add(Diagnostic.Create(ErrorCatalogueCommand.NotWritable, null, $"the output directory '{shown}' cannot be written: {exception.Message}"));
    } finally {
      foreach (var temporary in temporaries.Where(File.Exists))
        File.Delete(temporary);
    }

    return $"wrote {files.Json} and {files.Markdown}: {Counted(report.Pairs.Count, "pair")} from {Counted(report.GateRecords, "gate record")}";
  }

  /// <summary>Compares the committed files with the report computed from the evidence; writes nothing.</summary>
  private static string Check(CapabilityReport report, string directory, (string Json, string Markdown) files, List<Diagnostic> problems) {
    var up = $"{files.Json} and {files.Markdown} up to date: {Counted(report.Pairs.Count, "pair")}";
    var jsonPath = Path.Combine(directory, CapabilityReport.JsonFile);
    if (!File.Exists(jsonPath)) {
      problems.Add(OutOfDate($"generated document out of date: '{files.Json}' is missing"));
      return up;
    }

    var committed = CapabilityReport.Parse(File.ReadAllText(jsonPath), out var errors);
    if (committed is null) {
      problems.AddRange(errors.Select(problem => Diagnostic.Create(SpecDiagnostics.SchemaViolation, null, files.Json, problem)));
      return up;
    }

    var added = report.Pairs.Count(pair => committed.Find(pair.From, pair.SourceProfile, pair.To, pair.TargetProfile) is null);
    var removed = committed.Pairs.Count(pair => report.Find(pair.From, pair.SourceProfile, pair.To, pair.TargetProfile) is null);
    if (added + removed > 0)
      problems.Add(OutOfDate($"{files.Json}: pair set differs from spec/ ({added} added, {removed} removed)"));

    foreach (var claimed in committed.Pairs) {
      if (report.Find(claimed.From, claimed.SourceProfile, claimed.To, claimed.TargetProfile) is not { } proved)
        continue;

      var (was, @is) = (GateStates.Name(claimed.State), GateStates.Name(proved.State));
      if (claimed.State > proved.State)
        problems.Add(Diagnostic.Create(CapabilityDiagnostics.Unsupported, null, $"{files.Json}: pair {claimed.Name} is committed as '{was}' but the evidence gives '{@is}'"));
      else if (claimed.State < proved.State)
        problems.Add(OutOfDate($"{files.Json} is stale: pair {claimed.Name} reached '{@is}', committed '{was}'"));
      else if (!claimed.SameAs(proved))
        problems.Add(OutOfDate($"{files.Json} is stale: pair {claimed.Name} differs from the evidence in its fixtures, evidence or limitations"));
    }

    var markdownPath = Path.Combine(directory, CapabilityReport.MarkdownFile);
    if (!File.Exists(markdownPath))
      problems.Add(OutOfDate($"generated document out of date: '{files.Markdown}' is missing"));
    else if (File.ReadAllText(markdownPath).Replace("\r\n", "\n", StringComparison.Ordinal) != Text(committed.WriteMarkdown))
      problems.Add(OutOfDate($"{files.Markdown} does not match {files.Json}"));

    return up;
  }

  private static Diagnostic OutOfDate(string message) => Diagnostic.Create(ErrorCatalogueCommand.OutOfDate, null, message);

  private static string Text(Action<TextWriter> write) {
    using var writer = new StringWriter { NewLine = "\n" };
    write(writer);
    return writer.ToString();
  }

  private static string Portable(string path) => path.Replace('\\', '/');

  /// <summary>A file of a directory as the command line named the directory, with a forward slash.</summary>
  private static string Shown(string directory, string name) => Portable(directory).TrimEnd('/') + "/" + name;

  private static string Counted(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

  private static int Fail(TextWriter error, IEnumerable<Diagnostic> problems) {
    foreach (var problem in problems)
      error.WriteLine(problem.Source is null ? CiDiagnostics.Render(problem) : DiagnosticRenderer.ToText(problem));
    return Program.Failure;
  }

  private static int Usage(TextWriter error, string message) {
    error.WriteLine($"{Program.Name}: error {ToolchainsCommand.UsageCode}: {message}");
    return Program.UsageError;
  }

  [GeneratedRegex("^[0-9a-f]{40}\\z", RegexOptions.CultureInvariant)]
  private static partial Regex Commit40();

}
