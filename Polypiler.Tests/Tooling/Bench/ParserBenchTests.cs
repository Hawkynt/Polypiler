using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Toolchains;
using Polypiler.Tooling;
using Polypiler.Tooling.Bench;

namespace Polypiler.Tests.Tooling.Bench;

/// <summary><c>polypiler-tooling bench parsers</c> (#1610) on scratch corpora with parsers of the test, and on the parsers of this build.</summary>
[TestFixture]
public sealed class ParserBenchTests {

  private static readonly JsonSchema _schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "parser-bench.schema.json")));

  /// <summary>Accepts what does not contain "bad"; the generated one also accepts "quirk".</summary>
  private static readonly ParserBenchLanguage _toy = new("toy", ".toy", [
    new("hand", source => !System.Text.Encoding.UTF8.GetString(source).Contains("bad", StringComparison.Ordinal)),
    new("generated", source => System.Text.Encoding.UTF8.GetString(source) is var text && (!text.Contains("bad", StringComparison.Ordinal) || text.Contains("quirk", StringComparison.Ordinal)))
  ]);

  private ScratchFolder _folder = null!;

  [SetUp]
  public void Create() => this._folder = new ScratchFolder();

  [TearDown]
  public void Delete() => this._folder.Dispose();

  private (int Exit, string Output, string Error) Run(IReadOnlyDictionary<string, ParserBenchLanguage>? languages, params string[] args) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exit = new ParserBenchCommand(languages ?? new Dictionary<string, ParserBenchLanguage> { ["toy"] = _toy }, this._folder.Path).Run(args, output, error);
    return (exit, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
  }

  private void Source(string relative, string text) => File.WriteAllText(this._folder.File(relative), text);

  private static void IsValid(JsonNode report) {
    using var document = System.Text.Json.JsonDocument.Parse(report.ToJsonString());
    Assert.That(_schema.Evaluate(document.RootElement).IsValid, Is.True, report.ToJsonString());
  }

  /// <summary>The schema itself is valid JSON Schema 2020-12.</summary>
  [Test]
  public void TheSchema_IsValid() {
    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "parser-bench.schema.json")));
    Assert.That(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
  }

  /// <summary>Test design 1: a cold and three warm rounds with both parsers, their ratio and the differing verdicts, as a table and as JSON.</summary>
  [Test]
  public void BothParsers_AreMeasuredAndCompared() {
    this.Source("c/a.toy", "fine");
    this.Source("c/sub/b.toy", "bad");
    this.Source("c/sub/c.toy", "bad quirk");
    this.Source("c/ignored.txt", "bad");
    var (exit, output, _) = this.Run(null, "--language", "toy", "--corpus", "c", "--out", "out/r.json");
    Assert.That(exit, Is.Zero);
    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Assert.That(lines[0], Does.StartWith("corpus ").And.EndWith(": 3 files, 0.0 MiB"));
    Assert.That(lines.Skip(1).Take(4).Select(line => line.Split(':')[0]), Is.EqualTo(new[] { "cold", "warm 1", "warm 2", "warm 3" }));
    Assert.That(lines[1], Does.Contain("hand ").And.Contain("generated ").And.Contain("MiB/s").And.Contain("MiB alloc"));
    Assert.That(lines[5..], Is.EqualTo(new[] { "verdicts: hand accepts 1, generated accepts 2; 1 differ", "  sub/c.toy: accepted by generated" }));
    var report = JsonNode.Parse(File.ReadAllText(Path.Combine(this._folder.Path, "out", "r.json")))!;
    IsValid(report);
    Assert.That(((int)report["files"]!, report["rounds"]!.AsArray().Count, report["verdicts"]!["differences"]![0]!["file"]!.ToString()), Is.EqualTo((3, 4, "sub/c.toy")));
  }

  /// <summary>Test design 3: an empty corpus measures zeros; a single parser has no ratio; zero warm rounds is the cold round alone.</summary>
  [Test]
  public void AnEmptyCorpus_MeasuresZeros() {
    Directory.CreateDirectory(Path.Combine(this._folder.Path, "empty"));
    var single = new Dictionary<string, ParserBenchLanguage> { ["one"] = new("one", ".one", [new("generated", _ => true)]) };
    var (exit, output, _) = this.Run(single, "--language", "one", "--corpus", "empty", "--rounds", "0", "--out", "r.json");
    // Allocations are counted for the whole process, which runs other tests beside this one.
    Assert.That((exit, output.Split('\n')[1][..33], output.Split('\n')[2]), Is.EqualTo((0, "cold: generated 0 ms (0.0 MiB/s, ", "verdicts: generated accepts 0; 0 differ")));
    var report = JsonNode.Parse(File.ReadAllText(Path.Combine(this._folder.Path, "r.json")))!;
    IsValid(report);
    Assert.That(report["rounds"]![0]!["ratio"], Is.Null);
  }

  /// <summary>Test design 3: one file is one row of each round.</summary>
  [Test]
  public void OneFile_IsMeasured() {
    this.Source("one/x.toy", "fine");
    var (exit, output, _) = this.Run(null, "--corpus", "one", "--language", "toy", "--rounds", "1");
    Assert.That(exit, Is.Zero);
    Assert.That(output, Does.Contain("1 files").And.Contain("warm 1: ").And.Contain("hand accepts 1, generated accepts 1; 0 differ"));
  }

  /// <summary>Test design 2 and the scenario: a missing corpus exits 66.</summary>
  [Test]
  public void AMissingCorpus_Exits66() {
    var (exit, _, error) = this.Run(null, "--language", "toy", "--corpus", "/nonexistent");
    Assert.That((exit, error), Is.EqualTo((66, "polypiler-tooling: corpus '/nonexistent' does not exist.\n")));
  }

  /// <summary>The command line is checked first.</summary>
  [TestCase(new[] { "--language", "toy" }, "bench parsers needs '--language <name>' and '--corpus <dir>'")]
  [TestCase(new[] { "--language", "cobol", "--corpus", "c" }, "no parsers of 'cobol': one of toy")]
  [TestCase(new[] { "--language", "toy", "--corpus", "c", "--rounds", "-1" }, "'--rounds -1' is not a count")]
  [TestCase(new[] { "--wat", "x" }, "unknown option '--wat'")]
  [TestCase(new[] { "--corpus" }, "option '--corpus' requires a value")]
  [TestCase(new[] { "--corpus", "a", "--corpus", "b" }, "option '--corpus' is given more than once")]
  public void ABadCommandLine_IsAUsageError(string[] args, string message) {
    var (exit, _, error) = this.Run(null, args);
    Assert.That((exit, error), Is.EqualTo((2, $"polypiler-tooling: {message}.\n")));
  }

  /// <summary>A parser that throws what it should not stops the benchmark with that exception.</summary>
  [Test]
  public void AFailingParser_Propagates() {
    this.Source("c/a.toy", "x");
    var broken = new Dictionary<string, ParserBenchLanguage> { ["toy"] = new("toy", ".toy", [new("hand", _ => throw new InvalidOperationException("broken"))]) };
    Assert.That(Assert.Throws<InvalidOperationException>(() => this.Run(broken, "--language", "toy", "--corpus", "c"))!.Message, Is.EqualTo("broken"));
  }

  /// <summary>Test design 4: the parsers of this build, Go and Python, run on a fixture each.</summary>
  [TestCase("go", "go/syntax", 1)]
  [TestCase("python", "python/syntax", 1)]
  public void TheBuildsParsers_Run(string language, string corpus, int accepted) {
    var folder = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", corpus);
    var report = ParserBenchCommand.Measure(ParserBenchLanguages.All[language], folder, 0);
    IsValid(report);
    Assert.That(report["verdicts"]!["accepted"]!.AsObject().Single().Value!.GetValue<int>(), Is.EqualTo(accepted));
  }

  /// <summary>A Python source that is no valid encoding, or does not parse, is refused; Go's nesting limit is a refusal.</summary>
  [TestCase("python", "x = (\n", false)]
  [TestCase("python", "# coding: bogus\nx = 1\n", false)]
  [TestCase("python", "x = 1\n", true)]
  [TestCase("go", "package p\nfunc f() { x := }\n", false)]
  public void TheBuildsParsers_GiveVerdicts(string language, string source, bool accepted)
    => Assert.That(ParserBenchLanguages.All[language].Parsers[0].Accepts(System.Text.Encoding.UTF8.GetBytes(source)), Is.EqualTo(accepted));

  /// <summary>Go's nesting limit is a refusal, not a failure of the benchmark.</summary>
  [Test]
  public void GoNesting_IsARefusal() {
    var deep = "package p\nvar x = " + new string('(', 1100) + "1" + new string(')', 1100) + "\n";
    Assert.That(ParserBenchLanguages.All["go"].Parsers[0].Accepts(System.Text.Encoding.UTF8.GetBytes(deep)), Is.False);
  }

  /// <summary>The program routes the verb.</summary>
  [Test]
  public void TheProgram_RoutesTheVerb() {
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(Program.Run(["bench", "parsers"], output, error), Is.EqualTo(2));
    Assert.That(error.ToString(), Does.Contain("bench parsers needs"));
  }

}
