using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Cipher;

/// <summary>How far one target got with a file.</summary>
public enum KatStage {

  /// <summary>The translation was refused: <see cref="KatTargetResult.Reason"/> is its first diagnostic.</summary>
  Refused,

  /// <summary>Translated, and nothing is run: the file has no vector a translation can be checked on, or the target has no test project.</summary>
  Translated,

  /// <summary>Translated, and the tests do not build: <see cref="KatTargetResult.Reason"/> is the first error.</summary>
  BuildFailed,

  /// <summary>Translated, built and run: <see cref="KatTargetResult.Passed"/> of the checked vectors passed.</summary>
  Ran

}

/// <summary>What one target made of one file.</summary>
/// <param name="Target">The target, <c>csharp</c> or <c>rust</c>.</param>
/// <param name="Stage">How far it got.</param>
/// <param name="Reason">The first diagnostic of a refusal, the first error of a build, why nothing ran; <see langword="null"/> after a run.</param>
/// <param name="Passed">The vectors that passed.</param>
/// <param name="Failures">Every vector that did not pass, with the reason.</param>
public sealed record KatTargetResult(string Target, KatStage Stage, string? Reason, int Passed, IReadOnlyList<string> Failures) {

  /// <summary>Whether every checked vector passed, of at least one: an algorithm with no vector run never counts as passing.</summary>
  public bool AllPassed => this.Stage == KatStage.Ran && this.Passed > 0 && this.Failures.Count == 0;

}

/// <summary>A row of the table: one algorithm file, its vectors and what each target made of it.</summary>
/// <param name="File">The file below <c>algorithms/</c>.</param>
/// <param name="Vectors">Every vector of every algorithm the file registers.</param>
/// <param name="Checkable">The vectors a translation can be checked on.</param>
/// <param name="Note">Why the file has no vector at all - it did not load in node, it registers nothing - or <see langword="null"/>.</param>
/// <param name="Targets">The targets, in the order they were asked for.</param>
public sealed record KatRow(string File, int Vectors, int Checkable, string? Note, IReadOnlyList<KatTargetResult> Targets);

/// <summary>A target language of the harness: how to translate to it and, where it has one, how to check the result.</summary>
/// <param name="Name">The name of the target in the table.</param>
/// <param name="Language">The value of <c>--to</c>.</param>
/// <param name="Profile">The value of <c>--profile</c>, or <see langword="null"/> for the default.</param>
public sealed record KatTarget(string Name, string Language, string? Profile) {

  /// <summary>C# for .NET 10.</summary>
  public static KatTarget CSharp { get; } = new("csharp", "csharp", "net10.0");

  /// <summary>Rust, edition 2024.</summary>
  public static KatTarget Rust { get; } = new("rust", "rust", null);

  /// <summary>The targets the command knows, by name.</summary>
  public static IReadOnlyDictionary<string, KatTarget> Known { get; } = new Dictionary<string, KatTarget>(StringComparer.Ordinal) {
    [CSharp.Name] = CSharp,
    [Rust.Name] = Rust
  };

}

/// <summary>
/// Checks the translations of Cipher algorithm files on their known-answer tests (#1365): for every
/// file and target it translates the file with <c>polypiler translate</c>, writes a test project with
/// one test per vector a translation can be checked on, builds and runs it, and reads back which
/// vectors passed. Nothing about an algorithm is known to the harness but its golden file.
/// </summary>
/// <param name="runner">Starts polypiler, dotnet and cargo.</param>
/// <param name="polypiler">The command line that starts polypiler: the executable, or <c>dotnet</c> and the assembly.</param>
/// <param name="corpus">The Cipher folder of the pinned corpus.</param>
/// <param name="goldens">The folder of the golden files.</param>
/// <param name="work">The folder the translations and the test projects go to.</param>
public sealed class KatHarness(IProcessRunner runner, IReadOnlyList<string> polypiler, string corpus, string goldens, string work) {

  /// <summary>How long one translation may take.</summary>
  public static readonly TimeSpan TranslateTimeout = TimeSpan.FromMinutes(5);

  /// <summary>How long building and running one test project may take.</summary>
  public static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(15);

  private static readonly JsonSerializerOptions _json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

  /// <summary>Checks one file on every target.</summary>
  /// <param name="relative">The file below <c>algorithms/</c>, <c>checksum/adler.js</c>.</param>
  /// <param name="targets">The targets.</param>
  public KatRow Check(string relative, IReadOnlyList<KatTarget> targets) {
    var golden = KatFile.Load(goldens, relative);
    var vectors = golden.Vectors.ToList();
    var checkable = vectors.Where(entry => entry.Vector.Unchecked is null).ToList();
    var note = golden.Error is { } error ? $"node cannot load it: {error}"
      : golden.Algorithms.Count == 0 ? "registers no algorithm"
      : vectors.Count == 0 ? "has no known-answer test"
      : null;
    var results = targets.Select(target => this.Check(relative, target, checkable)).ToList();
    return new KatRow(relative, vectors.Count, checkable.Count, note, results);
  }

  private KatTargetResult Check(string relative, KatTarget target, IReadOnlyList<(KatAlgorithm Algorithm, KatVector Vector)> checkable) {
    var folder = Path.Combine(work, Path.ChangeExtension(relative, null).Replace('/', Path.DirectorySeparatorChar), target.Name);
    if (Directory.Exists(folder))
      Directory.Delete(folder, true);
    var sources = Path.Combine(folder, "src");
    var argv = new List<string>(polypiler) {
      "translate", Path.Combine(corpus, "algorithms", relative.Replace('/', Path.DirectorySeparatorChar)),
      "--from", "javascript", "--to", target.Language, "--output", sources
    };
    if (target.Profile is { } profile)
      argv.AddRange(["--profile", profile]);

    var translated = runner.Run(argv, TranslateTimeout);
    if (translated.ExitCode != 0)
      return new(target.Name, KatStage.Refused, FirstProblem(translated.TimedOut ? "the translation timed out" : translated.StandardError + "\n" + translated.StandardOutput, ": error "), 0, []);

    if (checkable.Count == 0)
      return new(target.Name, KatStage.Translated, "no known-answer test a translation can be checked on", 0, []);

    return target.Language switch {
      "csharp" => this.RunCSharp(folder, target, checkable),
      _ => new(target.Name, KatStage.Translated, $"no test project for {target.Name} yet", 0, [])
    };
  }

  /// <summary>Writes the C# test project around the translation, runs <c>dotnet test</c> on it and reads its results.</summary>
  private KatTargetResult RunCSharp(string folder, KatTarget target, IReadOnlyList<(KatAlgorithm Algorithm, KatVector Vector)> checkable) {
    var ids = WriteCSharpProject(folder, checkable);
    var results = Path.Combine(folder, "results");
    var run = runner.Run([
      "dotnet", "test", Path.Combine(folder, "Kat.Tests.csproj"), "-c", "Release", "--nologo",
      "--logger", "trx;LogFileName=kat.trx", "--results-directory", results
    ], TestTimeout);
    var trx = Path.Combine(results, "kat.trx");
    if (!File.Exists(trx))
      return new(target.Name, KatStage.BuildFailed, run.TimedOut ? "the build and the tests timed out" : FirstProblem(run.StandardOutput + "\n" + run.StandardError, ": error "), 0, []);

    var outcomes = ReadTrx(File.ReadAllText(trx));
    var passed = 0;
    var failures = new List<string>();
    foreach (var id in ids) {
      if (!outcomes.TryGetValue(id, out var outcome))
        failures.Add($"{id}: did not run");
      else if (outcome.Passed)
        ++passed;
      else
        failures.Add($"{id}: {outcome.Message}");
    }

    return new(target.Name, KatStage.Ran, null, passed, failures);
  }

  /// <summary>
  /// Writes the test project: the project file, the tests, the vectors as <c>kats.json</c> and a
  /// <c>Directory.Build.props</c> that keeps the repository's own settings out.
  /// </summary>
  /// <returns>The name of every test, in the order of the vectors.</returns>
  public static IReadOnlyList<string> WriteCSharpProject(string folder, IReadOnlyList<(KatAlgorithm Algorithm, KatVector Vector)> checkable) {
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "Directory.Build.props"), "<Project />\n");
    File.WriteAllText(Path.Combine(folder, "Directory.Build.targets"), "<Project />\n");
    File.WriteAllText(Path.Combine(folder, "Kat.Tests.csproj"), Template("Kat.Tests.csproj.txt"));
    File.WriteAllText(Path.Combine(folder, "KatTests.cs"), Template("KatTests.cs.txt"));
    var ids = new List<string>();
    var vectors = new JsonArray();
    foreach (var (algorithm, vector) in checkable) {
      var id = $"{algorithm.Name} #{algorithm.Vectors.ToList().IndexOf(vector) + 1}";
      ids.Add(id);
      vectors.Add(new JsonObject {
        ["id"] = id,
        ["algorithm"] = algorithm.Name,
        ["inverse"] = JsonNode.Parse("{\"t\":\"bool\",\"v\":" + (vector.Inverse ? "true" : "false") + "}"),
        ["steps"] = new JsonArray([.. vector.Steps.Select(step => (JsonNode)new JsonObject {
          ["name"] = step.Name,
          ["call"] = step.IsCall,
          ["values"] = new JsonArray([.. step.Values.Select(value => JsonNode.Parse(value.Json.GetRawText()))])
        })]),
        ["input"] = JsonNode.Parse(vector.Input.Json.GetRawText()),
        ["answer"] = Convert.ToHexStringLower(vector.Answer!.Bytes!)
      });
    }

    File.WriteAllText(Path.Combine(folder, "kats.json"), vectors.ToJsonString(_json) + "\n");
    return ids;
  }

  /// <summary>A template of the test projects, embedded in the tool.</summary>
  public static string Template(string name) {
    using var stream = typeof(KatHarness).Assembly.GetManifestResourceStream("Cipher." + name)!;
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }

  /// <summary>The outcome of every test of a TRX file, by the test's name.</summary>
  public static IReadOnlyDictionary<string, (bool Passed, string Message)> ReadTrx(string text) {
    var document = XDocument.Parse(text);
    var outcomes = new Dictionary<string, (bool, string)>(StringComparer.Ordinal);
    foreach (var result in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult")) {
      var message = result.Descendants().FirstOrDefault(element => element.Name.LocalName == "Message")?.Value.Trim() ?? "";
      outcomes[(string?)result.Attribute("testName") ?? ""] = ((string?)result.Attribute("outcome") == "Passed", message.Split('\n')[0].Trim());
    }

    return outcomes;
  }

  /// <summary>The first line of a text that holds a marker, without the path in front of it; the first non-empty line when none does.</summary>
  public static string FirstProblem(string text, string marker) {
    var lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
    var line = lines.FirstOrDefault(line => line.Contains(marker, StringComparison.Ordinal));
    if (line is null)
      return lines.FirstOrDefault() ?? "no output";

    var at = line.IndexOf(marker, StringComparison.Ordinal);
    var problem = line[(at + marker.Length)..];
    // A compiler names the project after the message: it says nothing about the problem.
    var project = problem.LastIndexOf(" [", StringComparison.Ordinal);
    return project > 0 && problem.EndsWith("proj]", StringComparison.Ordinal) ? problem[..project] : problem;
  }

}
