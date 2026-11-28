using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The Python fixtures of <c>tests/fixtures/prototype/python</c>, which the frontend parses itself.
/// What CPython did for the calls of a fixture is recorded under <c>recorded/</c>; the tests of the
/// Python layer hold every recording to what CPython does today.
/// </summary>
internal static class PythonFixture {

  /// <summary>The fixtures, which are also the names of their modules.</summary>
  public static readonly string[] Names = ["scalars", "control", "functions", "memory", "types", "modules"];

  /// <summary>The directory of the fixtures.</summary>
  public static string Directory { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "python");

  /// <summary>The script that runs calls in CPython and prints what they did.</summary>
  public static string Oracle { get; } = Path.Combine(Directory, "oracle.py");

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

  /// <summary>Imports a fixture, and the modules it imports; fails with the diagnostics when it is refused.</summary>
  public static IrModule Import(string fixture) => Module(Import(fixture + ".py", SourceOf(fixture), new PythonImportOptions { Modules = Sibling }));

  /// <summary>A module a fixture imports: a file beside it.</summary>
  public static (string Path, string Source)? Sibling(string name) => File.Exists(PathOf(name)) ? (name + ".py", SourceOf(name)) : null;

  /// <summary>The module of a result; fails with the diagnostics when there is none.</summary>
  public static IrModule Module(PythonImportResult result)
    => result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));

  /// <summary>Imports a source; it imports no module unless the options locate one.</summary>
  public static PythonImportResult Import(string path, string source, PythonImportOptions? options = null)
    => PythonFrontend.Import(path, source, options ?? new PythonImportOptions { Modules = _ => null });

  private static string[] Lines(string path)
    => File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();

}
