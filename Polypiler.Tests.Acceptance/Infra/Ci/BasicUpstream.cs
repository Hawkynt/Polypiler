using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Formats.Llvm;
using Polypiler.Tooling.Corpus;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The programs of PB-Compiler at its pinned commit and the outputs it ships for them
/// (<c>tests/&lt;NAME&gt;.BAS</c> and <c>tests/&lt;NAME&gt;.expected</c>, fixture <c>pb.dos.arith</c> of
/// <c>tooling/corpus/corpus.yaml</c>), and the comparison PB-Compiler defines for them.
/// </summary>
public static class BasicUpstream {

  /// <summary>The repository of PB-Compiler in <c>tooling/corpus/corpus.yaml</c>.</summary>
  public const string Repository = "Hawkynt/PB-Compiler";

  /// <summary>
  /// The bytes of a file of PB-Compiler at its pin: the copy <c>polypiler-tooling corpus import</c>
  /// wrote when there is one, else read from git at the pinned commit.
  /// </summary>
  /// <param name="path">The file below the repository's root.</param>
  public static byte[] Read(string path) {
    var root = PrototypeFixture.RepositoryRoot;
    var manifest = CorpusManifest.Load(Path.Combine(root, CorpusManifest.RepositoryPath));
    var commit = manifest.Repository(Repository)!.Commit;
    var imported = CorpusImporter.FilePath(root, Repository, commit, path);
    return File.Exists(imported) ? File.ReadAllBytes(imported) : Encoding.Latin1.GetBytes(PbCorpus.ReadPinned(Repository, path));
  }

  /// <summary>The lines of an output, CR LF counting as LF, without the empty line after the last line end.</summary>
  /// <param name="text">The output.</param>
  public static IReadOnlyList<string> Lines(string text) {
    var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
    return normalized.Length == 0 ? [] : normalized.TrimEnd('\n').Split('\n');
  }

  /// <summary>
  /// The lines in which a program's output differs from the expectation PB-Compiler ships for it,
  /// each naming the line, the expected text and the actual one; empty when they agree. The comparison
  /// is the one PB-Compiler's own <c>scripts/run-dos-tests.sh</c> applies to its <c>.expected</c>
  /// files: CR LF counts as LF and blanks at the end of a line are ignored, because PowerBASIC prints
  /// a number with a blank after it and the shipped files do not keep it. Nothing else is normalised.
  /// </summary>
  /// <param name="actual">The captured output.</param>
  /// <param name="expected">The shipped expectation.</param>
  public static IReadOnlyList<string> Differences(string actual, string expected)
    => LineDifferences(Trimmed(actual), Trimmed(expected));

  /// <summary>The lines of an output with the blanks at their ends removed, as PB-Compiler compares them.</summary>
  /// <param name="text">The output.</param>
  public static IReadOnlyList<string> Trimmed(string text) => [.. Lines(text).Select(line => line.TrimEnd(' ', '\t'))];

  /// <summary>The lines in which two texts differ, each naming the line, the expected text and the actual one; empty when they agree.</summary>
  /// <param name="actual">The actual lines.</param>
  /// <param name="expected">The expected lines.</param>
  public static IReadOnlyList<string> LineDifferences(IReadOnlyList<string> actual, IReadOnlyList<string> expected) {
    var differences = new List<string>();
    for (var index = 0; index < Math.Max(actual.Count, expected.Count); ++index) {
      var got = index < actual.Count ? actual[index] : null;
      var line = index < expected.Count ? expected[index] : null;
      if (got != line)
        differences.Add($"line {index + 1}: expected {Quote(line)}, actual {Quote(got)}");
    }

    return differences;
  }

  private static string Quote(string? line) => line is null ? "no line" : $"'{line}'";

}
