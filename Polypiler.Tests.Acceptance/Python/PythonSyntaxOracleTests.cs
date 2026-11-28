using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Frontends.Python.Support;

namespace Polypiler.Tests.Acceptance.Python;

/// <summary>
/// The Python frontend's own tokenizer, parser and compile-time checks against CPython itself, the
/// differential oracle of the Python layer (tooling/ci/layers/pyperl.yaml): <c>Python/Oracle/oracle.py</c>,
/// run with the pinned <c>python3</c>, asks <c>ast.parse</c> whether a source parses, <c>compile</c>
/// whether it compiles, and gives the tree in the form the frontend writes its own. On the syntax cases
/// of <c>tests/fixtures/python/syntax</c>, on every Python fixture of the repository, on every
/// <c>.py</c> file of the interpreter's own library - <c>Lib/</c> with <c>Lib/test/</c> - at every
/// version of the frontend, and on every string of <c>Lib/test/</c> taken as a source, the frontend's
/// verdict is CPython's, its tree equal to <c>ast.dump</c>'s with every position, and its message
/// CPython's. The frontend never runs Python; only this test does.
/// </summary>
[TestFixture, Layer("pyperl")]
[Category("EndToEnd")]
public sealed class PythonSyntaxOracleTests {

  private sealed record Verdict(bool Parsed, bool? Compiled, string? Error, string? Digest);

  private static string Oracle { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "Polypiler.Tests.Acceptance", "Python", "Oracle", "oracle.py");

  /// <summary>What CPython decides of requests: <c>P minor path</c> asks of a file, <c>C minor path</c> of its text, <c>S minor path</c> of every string of a file.</summary>
  private static List<JsonElement> Ask(IEnumerable<string> requests) {
    var start = new ProcessStartInfo("python3") {
      RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
      StandardOutputEncoding = Encoding.UTF8
    };
    foreach (var argument in new[] { "-I", "-B", Oracle })
      start.ArgumentList.Add(argument);

    using var process = Process.Start(start)!;
    var output = new List<JsonElement>();
    var reader = Task.Run(() => {
      while (process.StandardOutput.ReadLine() is { } line)
        output.Add(JsonDocument.Parse(line).RootElement.Clone());
    });
    var errors = process.StandardError.ReadToEndAsync();
    using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
      foreach (var request in requests)
        input.WriteLine(request);

    reader.Wait();
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.Zero, errors.Result);
    return output;
  }

  private static Verdict Of(JsonElement result) => new(
    result.GetProperty("parsed").GetBoolean(),
    result.TryGetProperty("compiled", out var compiled) ? compiled.GetBoolean() : null,
    result.TryGetProperty("error", out var error) ? error.GetString() : null,
    result.TryGetProperty("digest", out var digest) ? digest.GetString() : null);

  private static string Digest(string tree) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tree)));

  private sealed class Tally {
    public int Compared;
    public int Undecided;
    public int Messages;
    public readonly List<string> Disagreements = [];
    public readonly List<string> OtherMessages = [];
  }

  /// <summary>Compares the frontend's verdict, tree and message on a source with CPython's.</summary>
  private static void Compare(Tally tally, string what, PythonCheckResult mine, Verdict theirs) {
    if (!mine.Decided) {
      tally.Undecided++;
      return;
    }

    tally.Compared++;
    var message = mine.Diagnostics.FirstOrDefault()?.Message;
    if (mine.Parsed != theirs.Parsed) {
      tally.Disagreements.Add($"{what}: frontend {(mine.Parsed ? "parses" : message)}, CPython {(theirs.Parsed ? "parses" : theirs.Error)}");
      return;
    }

    if (theirs.Parsed && Digest(mine.Tree!) != theirs.Digest) {
      tally.Disagreements.Add($"{what}: the trees differ");
      return;
    }

    if (theirs.Compiled is { } compiled && compiled != mine.Compiled) {
      tally.Disagreements.Add($"{what}: frontend {(mine.Compiled ? "compiles" : message)}, CPython {(compiled ? "compiles" : theirs.Error)}");
      return;
    }

    if (theirs.Error is { } error) {
      if (message == error)
        tally.Messages++;
      else
        tally.OtherMessages.Add($"{what}: frontend {message}, CPython {error}");
    }
  }

  private static string? Library() {
    var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var argument in new[] { "-I", "-c", "import sysconfig; print(sysconfig.get_paths()['stdlib'])" })
      start.ArgumentList.Add(argument);

    using var process = Process.Start(start)!;
    var library = process.StandardOutput.ReadToEnd().Trim();
    process.WaitForExit();
    return Directory.Exists(library) ? library : null;
  }

  /// <summary>Every <c>.py</c> file of the library of the pinned CPython, its third-party packages left out.</summary>
  private static List<string> LibraryFiles(string library)
    => [.. Directory.EnumerateFiles(library, "*.py", SearchOption.AllDirectories)
      .Where(file => !file[library.Length..].Split(Path.DirectorySeparatorChar).Contains("site-packages"))
      .Order(StringComparer.Ordinal)];

  /// <summary>Every syntax case is decided by CPython as the case states, which the unit tests hold the frontend to.</summary>
  [Test]
  [RequiresToolchain("python")]
  public void TheSyntaxCases_AreDecidedAsCPythonDecidesThem() {
    var directory = Directory.CreateTempSubdirectory("polypiler-python-cases-").FullName;
    try {
      var cases = PythonSyntaxCases.All.Select((@case, index) => (Case: @case, Path: Path.Combine(directory, index + ".py"))).ToList();
      foreach (var (@case, path) in cases)
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(@case.Source));

      var results = Ask(cases.Select(entry => $"C {entry.Case.Version} {entry.Path}"));
      var disagreements = new List<string>();
      foreach (var ((@case, _), result) in cases.Zip(results)) {
        var verdict = Of(result);
        var outcome = verdict.Parsed && verdict.Compiled is true ? "valid" : verdict.Error;
        if (outcome != @case.Want)
          disagreements.Add($"{@case}: the case wants {@case.Want}, CPython {outcome}");
      }

      TestContext.Out.WriteLine($"{cases.Count} syntax cases compared with CPython, {disagreements.Count} disagreements");
      Assert.That(disagreements.Take(20), Is.Empty);
    } finally {
      Directory.Delete(directory, true);
    }
  }

  /// <summary>The trees of <c>trees.jsonl</c> are what CPython's <c>ast</c> gives.</summary>
  [Test]
  [RequiresToolchain("python")]
  public void TheTreeCases_AreCPythonsTrees() {
    var directory = Directory.CreateTempSubdirectory("polypiler-python-trees-").FullName;
    try {
      var trees = PythonSyntaxCases.Trees.Select((tree, index) => (Tree: tree, Path: Path.Combine(directory, index + ".py"))).ToList();
      foreach (var (tree, path) in trees)
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(tree.Source));

      var results = Ask(trees.Select(entry => $"T {PythonFrontend.LatestVersion} {entry.Path}"));
      Assert.That(results.Select(result => result.GetProperty("tree").GetString()), Is.EqualTo(trees.Select(entry => entry.Tree.Tree)));
    } finally {
      Directory.Delete(directory, true);
    }
  }

  /// <summary>Every Python fixture of the repository parses, and compiles, as CPython parses and compiles it, with CPython's tree.</summary>
  [Test]
  [RequiresToolchain("python")]
  public void TheFixtures_AreDecidedAsCPythonDecidesThem() {
    var fixtures = Directory.EnumerateFiles(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures"), "*.py", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
    var results = Ask(fixtures.Select(file => $"P {PythonFrontend.LatestVersion} {file}"));
    var tally = new Tally();
    foreach (var (file, result) in fixtures.Zip(results))
      Compare(tally, file, PythonFrontend.Check(file, File.ReadAllBytes(file)), Of(result));

    TestContext.Out.WriteLine($"fixtures: {tally.Compared} compared, {tally.Disagreements.Count} disagreements");
    Assert.That(tally.Disagreements.Take(20), Is.Empty);
    Assert.That((tally.Compared, fixtures.Count > 10), Is.EqualTo((fixtures.Count, true)));
  }

  /// <summary>
  /// Every <c>.py</c> file of the pinned CPython's own library - <c>Lib/</c> and <c>Lib/test/</c> - parses
  /// at every version of the frontend as <c>ast.parse(feature_version=...)</c> parses it, with its tree, and
  /// compiles at the newest as <c>compile</c> does.
  /// </summary>
  [Test]
  [RequiresToolchain("python")]
  public void TheCPythonLibrary_IsDecidedAsCPythonDecidesIt() {
    var library = Library() ?? throw new InconclusiveException("python3 names no library");
    var files = LibraryFiles(library);
    var tests = files.Count(file => file.Contains(Path.DirectorySeparatorChar + "test" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    var report = new StringBuilder();
    var disagreements = new List<string>();
    for (var minor = PythonFrontend.MinimumVersion; minor <= PythonFrontend.LatestVersion; minor++) {
      var results = Ask(files.Select(file => $"P {minor} {file}"));
      var tally = new Tally();
      foreach (var (file, result) in files.Zip(results))
        Compare(tally, $"{file} (3.{minor})", PythonFrontend.Check(file, File.ReadAllBytes(file), new PythonImportOptions { Version = minor }), Of(result));

      report.AppendLine($"3.{minor}: {tally.Compared} files decided, {tally.Undecided} undecided, {tally.Disagreements.Count} disagreements, {tally.Messages} errors with CPython's message, {tally.OtherMessages.Count} with another");
      disagreements.AddRange(tally.Disagreements);
    }

    TestContext.Out.WriteLine($"{files.Count} files of {library} ({tests} of them under test/):");
    TestContext.Out.Write(report);
    Assert.That(disagreements.Take(20), Is.Empty);
    Assert.That(files.Count, Is.GreaterThan(500), "the library has its sources");
  }

  /// <summary>
  /// Every string of the files of <c>Lib/test/</c>, taken as a source of its own - the snippets CPython's
  /// tests compile, valid ones and every kind of error - is decided as CPython decides it, with its tree
  /// and its message.
  /// </summary>
  [Test]
  [RequiresToolchain("python")]
  public void TheStringsOfCPythonsTests_AreDecidedAsCPythonDecidesThem() {
    var library = Library() ?? throw new InconclusiveException("python3 names no library");
    var tests = LibraryFiles(library).Where(file => file.Contains(Path.DirectorySeparatorChar + "test" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToList();
    if (tests.Count == 0)
      Assert.Inconclusive($"{library} has no test/ folder");

    var tally = new Tally();
    foreach (var result in Ask(tests.Select(file => $"S {PythonFrontend.LatestVersion} {file}"))) {
      var source = result.GetProperty("source").GetString()!;
      Compare(tally, $"{result.GetProperty("path").GetString()}: {JsonSerializer.Serialize(source.Length > 200 ? source[..200] : source)}", PythonFrontend.Check("<snippet>", source), Of(result));
    }

    TestContext.Out.WriteLine($"{tally.Compared} strings of {tests.Count} test files decided, {tally.Disagreements.Count} disagreements, {tally.Messages} errors with CPython's message, {tally.OtherMessages.Count} with another");
    foreach (var other in tally.OtherMessages.Take(20))
      TestContext.Out.WriteLine(other);

    Assert.That(tally.Disagreements.Take(20), Is.Empty);
    Assert.That(tally.Compared, Is.GreaterThan(10000), "the tests have their strings");
    Assert.That(tally.OtherMessages.Count, Is.LessThan(Math.Max(1, tally.Messages / 100)), "nearly every error has CPython's message");
  }

}
