using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The Python fixtures of <c>tests/fixtures/prototype/python</c>, and the trees CPython's <c>ast</c>
/// module gave for them and for the snippets of the tests, recorded under <c>ast/</c>: the unit tests
/// import from the recorded tree and need no interpreter, the tests of the Python layer hold every
/// recorded tree to what the helper prints today. What CPython did for the calls of a fixture is
/// recorded under <c>recorded/</c> the same way.
/// </summary>
internal static class PythonFixture {

  /// <summary>The fixtures, which are also the names of their modules.</summary>
  public static readonly string[] Names = ["scalars", "control", "functions", "memory", "types", "modules"];

  /// <summary>The directory of the fixtures.</summary>
  public static string Directory { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "python");

  /// <summary>The script that runs calls in CPython and prints what they did.</summary>
  public static string Oracle { get; } = Path.Combine(Directory, "oracle.py");

  /// <summary>The directory of the recorded trees.</summary>
  public static string Trees { get; } = Path.Combine(Directory, "ast");

  /// <summary>Whether the tests are asked to record again: <c>POLYPILER_RECORD=1</c>.</summary>
  public static bool Recording => Environment.GetEnvironmentVariable("POLYPILER_RECORD") == "1";

  /// <summary>The path of a fixture's source file.</summary>
  public static string PathOf(string fixture) => Path.Combine(Directory, fixture + ".py");

  /// <summary>The text of a fixture's source file, with the line endings of the repository.</summary>
  public static string SourceOf(string fixture) => File.ReadAllText(PathOf(fixture)).Replace("\r\n", "\n", StringComparison.Ordinal);

  /// <summary>The path of the file that records what CPython did for the calls of a fixture.</summary>
  public static string RecordingOf(string fixture) => Path.Combine(Directory, "recorded", fixture + ".txt");

  /// <summary>The lines of a recording.</summary>
  public static string[] RecordedLines(string fixture) => Lines(RecordingOf(fixture));

  /// <summary>One line of a recording: the call and what CPython did.</summary>
  public static string Line(PythonCall call, string outcome) => $"{call} => {outcome}";

  /// <summary>Writes the lines of a recording.</summary>
  public static void Record(string path, IEnumerable<string> lines) {
    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, string.Join('\n', lines) + "\n");
  }

  /// <summary>Imports a fixture from its recorded tree, the modules it imports from theirs; fails with the diagnostics when it is refused.</summary>
  public static IrModule Import(string fixture) => Module(Import(fixture + ".py", SourceOf(fixture), new PythonImportOptions { Modules = Sibling }));

  /// <summary>A module a fixture imports: a file beside it, with its recorded tree.</summary>
  public static (string Path, string Source, string? Dump)? Sibling(string name) {
    if (!File.Exists(PathOf(name)))
      return null;

    var source = SourceOf(name);
    return (name + ".py", source, Dump(name + ".py", source));
  }

  /// <summary>The module of a result; fails with the diagnostics when there is none.</summary>
  public static IrModule Module(PythonImportResult result)
    => result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));

  /// <summary>Imports a source from its recorded tree.</summary>
  public static PythonImportResult Import(string path, string source, PythonImportOptions? options = null)
    => PythonFrontend.ImportDump(path, source, Dump(path, source), options ?? new PythonImportOptions { Modules = _ => null });

  /// <summary>
  /// The file of the recorded tree of a source: named after the hash of its text, so that a changed
  /// snippet or fixture is a new file and a stale one is never read.
  /// </summary>
  public static string TreeOf(string source)
    => Path.Combine(Trees, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16] + ".json");

  /// <summary>Every recorded tree, as the source it is the tree of and the helper's output.</summary>
  public static IEnumerable<(string Path, string Source, string Dump)> RecordedTrees()
    => System.IO.Directory.EnumerateFiles(Trees, "*.json").Order(StringComparer.Ordinal).Select(path => {
      var node = JsonNode.Parse(File.ReadAllText(path))!;
      return (path, node["source"]!.GetValue<string>(), node["dump"]!.ToJsonString());
    });

  /// <summary>
  /// What the helper printed for a source: the recorded tree; without one, the helper's output now,
  /// which <c>POLYPILER_RECORD=1</c> records.
  /// </summary>
  public static string Dump(string path, string source) {
    var tree = TreeOf(source);
    if (File.Exists(tree) && !Recording)
      return JsonNode.Parse(File.ReadAllText(tree))!["dump"]!.ToJsonString();

    var (json, failure) = PythonFrontend.Dump(path, source);
    if (json is null)
      throw new InvalidOperationException($"no tree is recorded for this source and python could not make one ({failure}); record it with POLYPILER_RECORD=1:\n{source}");

    if (Recording) {
      System.IO.Directory.CreateDirectory(Trees);
      var dump = JsonNode.Parse(json)!.AsObject();
      // The interpreter's version is no part of the tree: the recording holds under every pinned one.
      dump.Remove("python");
      var record = new JsonObject { ["source"] = source, ["dump"] = dump };
      File.WriteAllText(tree, record.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) + "\n");
    }

    return json;
  }

  /// <summary>The helper's output without the interpreter's version, to compare with a recorded tree.</summary>
  public static string Comparable(string json) {
    var dump = JsonNode.Parse(json)!.AsObject();
    dump.Remove("python");
    return dump.ToJsonString();
  }

  private static string[] Lines(string path)
    => File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();

}
