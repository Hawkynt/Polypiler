using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Polypiler.Core;
using Polypiler.Core.Frontends.Go.Syntax;
using Polypiler.Core.Frontends.Python.Syntax;

namespace Polypiler.Tooling.Bench;

/// <summary>A parser a benchmark measures: its name and whether it accepts a source.</summary>
/// <param name="Name">The name: <c>hand</c> for the parser written by hand, <c>generated</c> for the one generated from a grammar.</param>
/// <param name="Accepts">Parses a source, given as bytes; whether it is accepted.</param>
public sealed record ParserUnderTest(string Name, Func<byte[], bool> Accepts);

/// <summary>A language whose parsers a benchmark measures, with the extension of its sources.</summary>
/// <param name="Name">The language, as <c>--language</c> names it.</param>
/// <param name="Extension">The extension of the corpus files, with the dot.</param>
/// <param name="Parsers">The parsers that exist, the one written by hand first.</param>
public sealed record ParserBenchLanguage(string Name, string Extension, IReadOnlyList<ParserUnderTest> Parsers);

/// <summary>The languages with parsers to measure, and the parsers each has now.</summary>
public static class ParserBenchLanguages {

  /// <summary>The newest Python minor version, which the Python parsers run at.</summary>
  public const int PythonMinor = 14;

  /// <summary>Every language with at least one parser, by name.</summary>
  public static IReadOnlyDictionary<string, ParserBenchLanguage> All { get; } = new Dictionary<string, ParserBenchLanguage>(StringComparer.Ordinal) {
    ["go"] = new("go", ".go", [new("hand", Go)]),
    ["python"] = new("python", ".py", [new("generated", Python)])
  };

  private static bool Go(byte[] source) {
    try {
      GoParser.ParseFile(new GoSource(source));
      return true;
    } catch (Exception exception) when (exception is GoSyntaxException or GoNestingException) {
      return false;
    }
  }

  private static bool Python(byte[] source) {
    var prepared = PySource.FromBytes(source);
    if (prepared.Utf8 is null)
      return false;

    try {
      new PyParser(prepared.Utf8, PythonMinor).ParseModule();
      return true;
    } catch (PyError) {
      return false;
    }
  }

}

/// <summary>
/// <c>polypiler-tooling bench parsers --language &lt;name&gt; --corpus &lt;dir&gt; [--rounds &lt;n&gt;] [--out
/// &lt;file.json&gt;]</c> (ADR 1000, decision 5): runs every parser of a language over every file of a
/// corpus in one process, a cold round and then the warm ones (3 by default), and prints for each
/// parser its time, its throughput and what it allocated, the ratio of the generated parser to the one
/// written by hand while both exist, and the files on which their verdicts differ; the same as JSON
/// (<c>spec/schemas/parser-bench.schema.json</c>) to <c>--out</c>. Nothing is gated: the migration's
/// pull request records the numbers. Exit 0, 2 for a bad command line, 66 where the corpus is missing.
/// </summary>
/// <param name="languages">The languages by name.</param>
/// <param name="currentDirectory">What relative paths are relative to.</param>
public sealed class ParserBenchCommand(IReadOnlyDictionary<string, ParserBenchLanguage> languages, string currentDirectory) {

  /// <summary>The verb after <c>bench</c>.</summary>
  public const string Verb = "parsers";

  /// <summary>The exit code of a missing corpus: sysexits' <c>EX_NOINPUT</c>.</summary>
  public const int NoInput = 66;

  /// <summary>The stack of the thread the parsers run on: deeply nested sources recurse as deep as their reference compilers allow.</summary>
  private const int StackSize = 256 * 1024 * 1024;

  /// <summary>Creates the command for the parsers of this build.</summary>
  public ParserBenchCommand()
    : this(ParserBenchLanguages.All, Environment.CurrentDirectory) { }

  /// <summary>Runs the arguments after <c>bench parsers</c> and returns the exit code.</summary>
  public int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) {
    ArgumentNullException.ThrowIfNull(args);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(error);
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Count; i++) {
      if (args[i] is not ("--language" or "--corpus" or "--rounds" or "--out"))
        return Usage(error, $"unknown option '{args[i]}'");

      if (i + 1 == args.Count)
        return Usage(error, $"option '{args[i]}' requires a value");

      if (!options.TryAdd(args[i], args[i + 1]))
        return Usage(error, $"option '{args[i]}' is given more than once");

      i++;
    }

    if (!options.TryGetValue("--language", out var name) || !options.TryGetValue("--corpus", out var corpus))
      return Usage(error, "bench parsers needs '--language <name>' and '--corpus <dir>'");

    if (!languages.TryGetValue(name, out var language))
      return Usage(error, $"no parsers of '{name}': one of {string.Join(", ", languages.Keys.Order(StringComparer.Ordinal))}");

    var rounds = 3;
    if (options.TryGetValue("--rounds", out var count) && !(int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out rounds) && rounds >= 0))
      return Usage(error, $"'--rounds {count}' is not a count");

    var folder = Path.GetFullPath(corpus, currentDirectory);
    if (!Directory.Exists(folder)) {
      error.WriteLine($"{Program.Name}: corpus '{corpus}' does not exist.");
      return NoInput;
    }

    JsonObject? report = null;
    ExceptionDispatchInfo? failure = null;
    var thread = new Thread(() => {
      try {
        report = Measure(language, folder, rounds);
      } catch (Exception exception) {
        failure = ExceptionDispatchInfo.Capture(exception);
      }
    }, StackSize);
    thread.Start();
    thread.Join();
    failure?.Throw();
    output.Write(Table(report!));
    if (options.TryGetValue("--out", out var file)) {
      var path = Path.GetFullPath(file, currentDirectory);
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllText(path, report!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    return Program.Success;
  }

  private static int Usage(TextWriter error, string message) {
    error.WriteLine($"{Program.Name}: {message}.");
    return Program.UsageError;
  }

  /// <summary>Measures every parser of a language on a corpus: the report as <c>parser-bench.schema.json</c> describes it.</summary>
  /// <param name="language">The language.</param>
  /// <param name="folder">The corpus folder.</param>
  /// <param name="rounds">The warm rounds after the cold one.</param>
  public static JsonObject Measure(ParserBenchLanguage language, string folder, int rounds) {
    ArgumentNullException.ThrowIfNull(language);
    var files = Directory.EnumerateFiles(folder, "*" + language.Extension, SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
    var sources = files.Select(File.ReadAllBytes).ToList();
    var bytes = sources.Sum(source => (long)source.Length);
    var mebibytes = bytes / 1048576.0;
    var verdicts = language.Parsers.Select(_ => new bool[files.Count]).ToList();
    var roundsJson = new JsonArray();
    for (var round = 0; round <= rounds; round++) {
      var results = new JsonArray();
      var times = new List<double>();
      for (var p = 0; p < language.Parsers.Count; p++) {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < sources.Count; i++)
          verdicts[p][i] = language.Parsers[p].Accepts(sources[i]);

        var milliseconds = watch.Elapsed.TotalMilliseconds;
        times.Add(milliseconds);
        results.Add(new JsonObject {
          ["parser"] = language.Parsers[p].Name,
          ["milliseconds"] = Math.Round(milliseconds, 3),
          ["mibPerSecond"] = milliseconds == 0 ? 0 : Math.Round(mebibytes / (milliseconds / 1000), 3),
          ["allocatedMiB"] = Math.Round((GC.GetTotalAllocatedBytes(true) - allocated) / 1048576.0, 3)
        });
      }

      roundsJson.Add(new JsonObject {
        ["round"] = round == 0 ? "cold" : $"warm {round}",
        ["results"] = results,
        ["ratio"] = times.Count == 2 && times[0] > 0 ? Math.Round(times[1] / times[0], 3) : null
      });
    }

    var accepted = new JsonObject();
    for (var p = 0; p < language.Parsers.Count; p++)
      accepted[language.Parsers[p].Name] = verdicts[p].Count(verdict => verdict);

    var differences = new JsonArray();
    for (var i = 0; i < files.Count; i++) {
      var accepting = language.Parsers.Where((_, p) => verdicts[p][i]).Select(parser => (JsonNode)parser.Name).ToArray();
      if (accepting.Length != 0 && accepting.Length != language.Parsers.Count)
        differences.Add(new JsonObject { ["file"] = Path.GetRelativePath(folder, files[i]).Replace('\\', '/'), ["acceptedBy"] = new JsonArray(accepting) });
    }

    return new JsonObject {
      ["schemaVersion"] = 1,
      ["language"] = language.Name,
      ["corpus"] = folder.Replace('\\', '/'),
      ["files"] = files.Count,
      ["bytes"] = bytes,
      ["parsers"] = new JsonArray([.. language.Parsers.Select(parser => (JsonNode)parser.Name)]),
      ["rounds"] = roundsJson,
      ["verdicts"] = new JsonObject { ["accepted"] = accepted, ["differences"] = differences }
    };
  }

  /// <summary>The report as a table: one line per round, then the verdicts and the files they differ on.</summary>
  /// <param name="report">The report.</param>
  public static string Table(JsonObject report) {
    ArgumentNullException.ThrowIfNull(report);
    var text = new System.Text.StringBuilder();
    var bytes = (long)report["bytes"]!;
    text.Append(CultureInfo.InvariantCulture, $"corpus {report["corpus"]}: {report["files"]} files, {bytes / 1048576.0:F1} MiB\n");
    foreach (var round in report["rounds"]!.AsArray()) {
      var parts = round!["results"]!.AsArray().Select(result => string.Create(CultureInfo.InvariantCulture,
        $"{result!["parser"]} {(double)result["milliseconds"]!:F0} ms ({(double)result["mibPerSecond"]!:F1} MiB/s, {(double)result["allocatedMiB"]!:F0} MiB alloc)"));
      var ratio = round["ratio"] is { } value ? string.Create(CultureInfo.InvariantCulture, $", ratio {(double)value:F2}x") : "";
      text.Append(CultureInfo.InvariantCulture, $"{round["round"]}: {string.Join(", ", parts)}{ratio}\n");
    }

    var verdicts = report["verdicts"]!;
    var accepted = verdicts["accepted"]!.AsObject().Select(pair => $"{pair.Key} accepts {pair.Value}");
    var differences = verdicts["differences"]!.AsArray();
    text.Append(CultureInfo.InvariantCulture, $"verdicts: {string.Join(", ", accepted)}; {differences.Count} differ\n");
    foreach (var difference in differences)
      text.Append(CultureInfo.InvariantCulture, $"  {difference!["file"]}: accepted by {string.Join(", ", difference["acceptedBy"]!.AsArray())}\n");

    return text.ToString();
  }

}
