using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// A fixture of <c>tests/fixtures/prototype/js</c>: its source for Polypiler, and what <c>node</c>
/// did when it ran that same source, recorded in a file beside it. The unit tests compare against the
/// recording; the acceptance tests run <c>node</c> again and hold the recording to what it does.
/// </summary>
internal static class JavaScriptFixture {

  /// <summary>The names of the fixtures written for this repository, which are also the names of their modules.</summary>
  public static readonly string[] Names = ["adler32", "arrays", "crc32", "fnv1a", "gcd", "operators", "rotate", "statements"];

  /// <summary>The directory of the JavaScript fixtures.</summary>
  public static string Directory { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "js");

  /// <summary>The path of Cipher's <c>OpCodes.js</c> at the pinned commit.</summary>
  public static string OpCodesPath { get; } = Path.Combine(Directory, "OpCodes.js");

  /// <summary>The path of a fixture's source file.</summary>
  public static string PathOf(string fixture) => Path.Combine(Directory, fixture + ".mjs");

  /// <summary>The text of a fixture's source file.</summary>
  public static string SourceOf(string fixture) => File.ReadAllText(PathOf(fixture));

  /// <summary>The path of the file that records what node did for the calls of a fixture.</summary>
  public static string RecordingOf(string fixture) => Path.Combine(Directory, "recorded", fixture + ".txt");

  /// <summary>How the expectation for <c>OpCodes.js</c> marks a function that imports.</summary>
  public const string Imported = "imported";

  /// <summary>
  /// The path of the file that says, for every function of <c>OpCodes.js</c>, whether it imports and
  /// otherwise which diagnostics refuse it: one line per function and diagnostic, a tab between the two.
  /// </summary>
  public static string OpCodesExpectationPath { get; } = Path.Combine(Directory, "OpCodes.import.txt");

  /// <summary>The lines of the expectation for <c>OpCodes.js</c>.</summary>
  public static IReadOnlyList<(string Function, string Status)> OpCodesExpectation()
    => File.ReadAllText(OpCodesExpectationPath)
      .Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(line => line.TrimEnd('\r').Split('\t'))
      .Select(fields => (fields[0], fields[1]))
      .ToList();

  /// <summary>Imports a source and fails with its diagnostics when it is refused.</summary>
  public static IrModule Import(string path, string source, IReadOnlyCollection<string>? exports = null) {
    var result = JavaScriptFrontend.Import(path, source, exports);
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>Imports a fixture.</summary>
  public static IrModule Import(string fixture) => Import(fixture + ".mjs", SourceOf(fixture));

  /// <summary>One line of a recording: the call and what node did for it.</summary>
  public static string Line(PrototypeCall call, string outcome) => $"{call} => {outcome}";

  /// <summary>The lines of a recording, whatever line ending the checkout gave the file.</summary>
  public static string[] RecordedLines(string fixture)
    => File.ReadAllText(RecordingOf(fixture)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();

}
