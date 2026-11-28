using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Core.Frontends.Go;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Frontends.Go.Support;

namespace Polypiler.Tests.Acceptance.Go;

/// <summary>
/// The Go frontend's own parser and type checker against Go itself, the differential oracle of the Go
/// layer (tooling/ci/layers/go.yaml): <c>Go/Oracle/oracle.go</c>, built with the pinned <c>go</c>,
/// asks go/parser whether a file parses and go/types whether it type-checks and which constants it
/// declares. On every case of <c>tests/fixtures/go/checker</c>, on the frontend's fixtures and on the
/// Go distribution itself - every <c>.go</c> file of <c>src/</c> and <c>test/</c> for the parser, the
/// type checker's own test data and <c>test/</c> for the checker - the frontend's verdict is Go's
/// wherever it gives one, and every constant of a valid file has go/types' type and exact value. The
/// frontend never runs Go; only this test does.
/// </summary>
[TestFixture, Layer("go")]
[Category("EndToEnd")]
public sealed class GoOracleTests {

  private sealed record Verdict(string Path, bool Valid, string? Error, IReadOnlyList<GoConstantValue> Constants);

  private static readonly Lazy<string> _oracle = new(Build);

  private static string Build() {
    var directory = Path.Combine(Path.GetTempPath(), "polypiler-go-oracle-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "go.mod"), "module oracle\n\ngo 1.22\n");
    File.Copy(Path.Combine(PrototypeFixture.RepositoryRoot, "Polypiler.Tests.Acceptance", "Go", "Oracle", "oracle.go"), Path.Combine(directory, "oracle.go"));
    var (exitCode, output) = GoDriver.Run(directory, "build", "-o", "oracle", ".");
    Assert.That(exitCode, Is.Zero, output);
    return Path.Combine(directory, OperatingSystem.IsWindows() ? "oracle.exe" : "oracle");
  }

  /// <summary>What Go decides of files: <c>P</c> asks go/parser, <c>T</c> go/types.</summary>
  private static Dictionary<string, Verdict> Ask(IEnumerable<(string Kind, string Path)> requests) {
    var start = new ProcessStartInfo(_oracle.Value) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
    start.Environment["GOTOOLCHAIN"] = "local";
    start.Environment["GOPROXY"] = "off";
    start.Environment["CGO_ENABLED"] = "0";
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
      foreach (var (kind, path) in requests)
        input.WriteLine($"{kind} {path}");

    process.WaitForExit();
    Assert.That(process.ExitCode, Is.Zero, errors.Result);
    var verdicts = new Dictionary<string, Verdict>(StringComparer.Ordinal);
    foreach (var line in output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
      using var document = JsonDocument.Parse(line);
      var root = document.RootElement;
      var constants = root.TryGetProperty("constants", out var list)
        ? list.EnumerateArray().Select(item => new GoConstantValue(item.GetProperty("name").GetString()!, item.GetProperty("type").GetString()!, item.GetProperty("value").GetString()!)).ToList()
        : [];
      var path = root.GetProperty("path").GetString()!;
      verdicts[path] = new(path, root.GetProperty("valid").GetBoolean(), root.TryGetProperty("error", out var error) ? error.GetString() : null, constants);
    }

    return verdicts;
  }

  private static string? Root() {
    var start = new ProcessStartInfo("go") { RedirectStandardOutput = true, UseShellExecute = false };
    start.ArgumentList.Add("env");
    start.ArgumentList.Add("GOROOT");
    start.Environment["GOTOOLCHAIN"] = "local";
    using var process = Process.Start(start)!;
    var root = process.StandardOutput.ReadToEnd().Trim();
    process.WaitForExit();
    return Directory.Exists(root) ? root : null;
  }

  /// <summary>Compares the frontend with Go on files: the type checker's verdicts where it gives one, or with <paramref name="parseOnly"/> the parser's on all.</summary>
  private static (int Compared, int Unsupported, List<string> Disagreements) Compare(IReadOnlyList<string> files, bool parseOnly) {
    var ours = files.ToDictionary(file => file, file => GoFrontend.Check(Path.GetFileName(file), File.ReadAllText(file)), StringComparer.Ordinal);
    var decided = parseOnly ? files : [.. files.Where(file => ours[file].Verdict != GoVerdict.Unsupported)];
    var go = Ask(decided.Select(file => (parseOnly ? "P" : "T", file)));
    var disagreements = new List<string>();
    foreach (var file in decided) {
      var (mine, theirs) = (ours[file], go[file]);
      var message = mine.Diagnostics.Select(diagnostic => diagnostic.ToString()).FirstOrDefault();
      if (parseOnly ? mine.Parsed != theirs.Valid : (mine.Verdict == GoVerdict.Valid) != theirs.Valid) {
        disagreements.Add($"{file}: frontend {(parseOnly ? mine.Parsed ? "parses" : "refuses" : mine.Verdict)} ({message}), go {(theirs.Valid ? "accepts" : theirs.Error)}");
        continue;
      }

      if (!parseOnly && theirs.Valid)
        foreach (var constant in mine.Constants)
          if (!theirs.Constants.Contains(constant))
            disagreements.Add($"{file}: constant {constant}, go {theirs.Constants.FirstOrDefault(other => other.Name == constant.Name)}");
    }

    return (decided.Count, files.Count - decided.Count, disagreements);
  }

  /// <summary>Every case of the checker: Go decides it as the case states, and every constant of a valid one is go/types'.</summary>
  [Test]
  [RequiresToolchain("go")]
  public void TheCheckersCases_AreDecidedAsGoDecidesThem() {
    var directory = Path.Combine(Path.GetTempPath(), "polypiler-go-cases-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var cases = GoCheckerCases.All.Where(@case => @case.Want != "unsupported").ToDictionary(@case => Path.Combine(directory, @case.Name.Replace('/', '_') + ".go"));
      foreach (var (path, @case) in cases)
        File.WriteAllText(path, @case.Source);

      var go = Ask(cases.Select(entry => (entry.Value.Want == "syntax" ? "P" : "T", entry.Key)));
      var disagreements = new List<string>();
      foreach (var (path, @case) in cases) {
        var verdict = go[path];
        if (verdict.Valid != (@case.Want == "valid"))
          disagreements.Add($"{@case.Name}: wants {@case.Want}, go {(verdict.Valid ? "accepts" : verdict.Error)}");

        if (@case.Want != "valid")
          continue;

        foreach (var constant in GoFrontend.Check(path, @case.Source).Constants.Concat(@case.Constants.Select(stated => new GoConstantValue(stated.Name, stated.Type, stated.Value))))
          if (!verdict.Constants.Contains(constant))
            disagreements.Add($"{@case.Name}: constant {constant}, go {verdict.Constants.FirstOrDefault(other => other.Name == constant.Name)}");
      }

      TestContext.Out.WriteLine($"{cases.Count} cases compared with go, {disagreements.Count} disagreements");
      Assert.That(disagreements, Is.Empty);
    } finally {
      Directory.Delete(directory, true);
    }
  }

  /// <summary>The frontend's fixtures - the imported sources, the refused ones and the grammar - are decided as Go decides them.</summary>
  [Test]
  [RequiresToolchain("go")]
  public void TheFixtures_AreDecidedAsGoDecidesThem() {
    var folder = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "go");
    var frontend = Directory.EnumerateFiles(Path.Combine(folder, "frontend"), "*.go", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
    var (compared, unsupported, disagreements) = Compare(frontend, false);
    var grammar = Compare([.. Directory.EnumerateFiles(Path.Combine(folder, "syntax"), "*.go")], true);
    TestContext.Out.WriteLine($"fixtures: {compared} decided, {unsupported} outside the checker; grammar: {grammar.Compared} parsed");
    Assert.That(disagreements.Concat(grammar.Disagreements), Is.Empty);
    Assert.That(compared, Is.GreaterThan(frontend.Count / 2), "most fixtures are within the checker");
  }

  /// <summary>Every Go file of the pinned Go distribution parses or is refused exactly as go/parser parses or refuses it.</summary>
  [Test]
  [RequiresToolchain("go")]
  public void TheGoDistribution_ParsesAsGoParserParsesIt() {
    var root = Root() ?? throw new InconclusiveException("go env GOROOT names no folder");
    var files = new[] { "src", "test" }.Select(folder => Path.Combine(root, folder)).Where(Directory.Exists)
      .SelectMany(folder => Directory.EnumerateFiles(folder, "*.go", SearchOption.AllDirectories)).Order(StringComparer.Ordinal).ToList();
    var (compared, _, disagreements) = Compare(files, true);
    TestContext.Out.WriteLine($"{compared} files of {root} parsed by the frontend and go/parser, {disagreements.Count} disagreements");
    Assert.That(disagreements.Take(20), Is.Empty);
    Assert.That(compared, Is.GreaterThan(5000), "the distribution has its sources");
  }

  /// <summary>The type checker's own test data and the test programs of the distribution: where the frontend decides, it decides as go/types, with go/types' constants.</summary>
  [Test]
  [RequiresToolchain("go")]
  public void TheGoDistribution_TypeChecksAsGoTypesChecksIt() {
    var root = Root() ?? throw new InconclusiveException("go env GOROOT names no folder");
    var testdata = Path.Combine(root, "src", "internal", "types", "testdata");
    var files = Directory.EnumerateDirectories(testdata).SelectMany(folder => Directory.EnumerateFiles(folder, "*.go"))
      .Concat(new[] { Path.Combine(root, "test"), Path.Combine(root, "test", "ken") }.Where(Directory.Exists).SelectMany(folder => Directory.EnumerateFiles(folder, "*.go")))
      .Order(StringComparer.Ordinal).ToList();
    var (compared, unsupported, disagreements) = Compare(files, false);
    TestContext.Out.WriteLine($"{compared} files decided by the frontend and go/types, {unsupported} outside the checker, {disagreements.Count} disagreements");
    Assert.That(disagreements.Take(20), Is.Empty);
    Assert.That(compared, Is.GreaterThan(100), "the checker decides a fair part of the distribution's test programs");
  }

}
