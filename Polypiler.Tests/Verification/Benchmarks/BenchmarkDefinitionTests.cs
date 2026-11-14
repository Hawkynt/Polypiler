using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Benchmarks;
using static Polypiler.Tests.Verification.Benchmarks.BenchTesting;

namespace Polypiler.Tests.Verification.Benchmarks;

/// <summary>The definition format: defaults, every field, and each schema error naming its field (test design 5, 11, 19).</summary>
[TestFixture]
public sealed class BenchmarkDefinitionTests {

  private const string Minimal = "id: loop\ndescription: d\nkind: process\ntoolchain: node\nrun: [loop.js]\n";

  private static string Problem(string text) {
    var exception = Assert.Throws<BenchmarkFileException>(() => BenchmarkDefinition.Parse(text, "loop.bench.yaml"))!;
    return $"{exception.Line}:{exception.Message}";
  }

  [Test]
  public void TheFixture_IsReadWithEveryField() {
    var definition = BenchmarkDefinition.Load(Path.Combine(FixtureFolder, "sum-loop.bench.yaml"));
    Assert.That((definition.Id, definition.Description), Is.EqualTo(("sum-loop", "Sums the integers 0 to 999 999 in node and prints the sum.")));
    Assert.That((definition.Kind, definition.Toolchain, definition.Warmup, definition.Runs), Is.EqualTo((BenchmarkKind.Process, "node", 1, 5)));
    Assert.That(definition.Sources, Is.EqualTo(new[] { "tests/fixtures/bench/sum-loop.js" }));
    Assert.That(definition.Run, Is.EqualTo(new[] { "tests/fixtures/bench/sum-loop.js" }));
    Assert.That((definition.ExpectStdout, definition.Build, definition.Stdin), Is.EqualTo(("499999500000", (IReadOnlyList<string>?)null, (string?)null)));
    Assert.That(definition.Sizes.Source, Is.EqualTo(new[] { "tests/fixtures/bench/sum-loop.js" }));
    Assert.That(definition.Sizes.Binary, Is.Empty);
  }

  [Test]
  public void AMinimalDefinition_HasOneDefaultVariant_OneWarmupAndFiveRuns() {
    var definition = BenchmarkDefinition.Parse(Minimal, "loop.bench.yaml");
    Assert.That(definition.Variants, Has.Count.EqualTo(1));
    var variant = definition.Variant("default");
    Assert.That((variant.Name, variant.Environment.Count, variant.Arguments.Count), Is.EqualTo(("default", 0, 0)));
    Assert.That((definition.Warmup, definition.Runs, definition.Sources.Count, definition.ExpectStdout), Is.EqualTo((1, 5, 0, (string?)null)));
  }

  /// <summary>Test design 11: three variants with different arguments and environments.</summary>
  [Test]
  public void Variants_CarryTheirArgumentsAndEnvironment() {
    var definition = BenchmarkDefinition.Parse("""
      id: loop
      description: d
      kind: dotnet
      toolchain: dotnet-sdk
      sources: [a.cs]
      build: [build, -o, out]
      run: [out/a.dll]
      stdin: input.txt
      variants:
        - { name: opt-none, args: [--opt, none] }
        - { name: opt-balanced, args: [--opt, balanced], env: { OPT: balanced } }
        - { name: opt-speed, args: [--opt, speed] }
      warmup: 0
      runs: 1
      sizes: { source: [a.cs], binary: [out] }
      """, "loop.bench.yaml");
    Assert.That(definition.Kind, Is.EqualTo(BenchmarkKind.DotNet));
    Assert.That(definition.Variants, Has.Count.EqualTo(3));
    Assert.That(definition.Variant("opt-balanced").Arguments, Is.EqualTo(new[] { "--opt", "balanced" }));
    Assert.That(definition.Variant("opt-balanced").Environment, Is.EqualTo(new Dictionary<string, string> { ["OPT"] = "balanced" }));
    Assert.That((definition.Warmup, definition.Runs, definition.Stdin), Is.EqualTo((0, 1, "input.txt")));
    Assert.That(definition.Build, Is.EqualTo(new[] { "build", "-o", "out" }));
    Assert.That(definition.Sizes.Binary, Is.EqualTo(new[] { "out" }));
  }

  [Test]
  public void AnUnknownVariant_IsRefused()
    => Assert.That(() => BenchmarkDefinition.Parse(Minimal, "x").Variant("opt-speed"), Throws.ArgumentException);

  /// <summary>Test design 5 and 19: each schema error names its field.</summary>
  [TestCase("runs: 0\n", "6:'runs' must be at least 1 and at most 1000, not 0")]
  [TestCase("runs: 1001\n", "6:'runs' must be at least 1 and at most 1000, not 1001")]
  [TestCase("warmup: -1\n", "6:'warmup' must be at least 0 and at most 1000, not -1")]
  [TestCase("runs: many\n", "6:'runs' must be an integer")]
  [TestCase("runs: [1]\n", "6:'runs' must be an integer")]
  [TestCase("runs: '5'\n", "6:'runs' must be an integer")]
  [TestCase("speed: 1\n", "6:definition: unknown property 'speed'")]
  [TestCase("variants: []\n", "6:'variants' needs at least one variant when given")]
  [TestCase("variants: [plain]\n", "6:'variants' must be a list of mappings")]
  [TestCase("variants: { a: b }\n", "6:'variants' must be a list")]
  [TestCase("variants: [{ args: [] }]\n", "6:missing property 'name'")]
  [TestCase("variants: [{ name: Fast }]\n", "6:'variants.name' must be lower-case letters, digits and hyphens, not 'Fast'")]
  [TestCase("variants: [{ name: a }, { name: a }]\n", "6:'variants.name' 'a' is given more than once")]
  [TestCase("variants: [{ name: a, env: [x] }]\n", "6:'variants.env' must be a mapping")]
  [TestCase("variants: [{ name: a, mode: x }]\n", "6:variant: unknown property 'mode'")]
  [TestCase("sizes: [a]\n", "6:'sizes' must be a mapping")]
  [TestCase("sizes: { source: /etc/passwd }\n", "6:'sizes.source' must be a list")]
  [TestCase("sizes: { binary: [../up] }\n", "6:'sizes.binary' must name a relative path without '..', not '../up'")]
  [TestCase("sizes: { total: [a] }\n", "6:sizes: unknown property 'total'")]
  [TestCase("stdin: /dev/zero\n", "6:'stdin' must name a relative path without '..', not '/dev/zero'")]
  [TestCase("expectStdout: [1]\n", "6:'expectStdout' must be text")]
  [TestCase("build: x\n", "6:'build' must be a list")]
  [TestCase("? [a]\n: b\n", "6:definition: property names must be plain text")]
  public void AnInvalidField_IsNamed(string line, string problem)
    => Assert.That(Problem(Minimal + line), Is.EqualTo(problem));

  [TestCase("kind: native\n", "3:'kind' must be 'process' or 'dotnet', not 'native'")]
  [TestCase("id: Loop\n", "1:'id' must be lower-case letters, digits and hyphens, not 'Loop'")]
  [TestCase("run: []\n", "5:'run' needs at least one argument")]
  [TestCase("toolchain: ~\n", "4:'toolchain' must be text")]
  public void AnInvalidRequiredField_IsNamed(string replacement, string problem) {
    var field = replacement[..replacement.IndexOf(':')];
    var lines = Minimal.Split('\n');
    var text = string.Join('\n', System.Array.ConvertAll(lines, line => line.StartsWith(field + ":", System.StringComparison.Ordinal) ? replacement.TrimEnd('\n') : line));
    Assert.That(Problem(text), Is.EqualTo(problem));
  }

  [TestCase("description")]
  [TestCase("kind")]
  [TestCase("toolchain")]
  [TestCase("run")]
  [TestCase("id")]
  public void AMissingRequiredField_IsNamed(string field) {
    var text = string.Join('\n', System.Array.FindAll(Minimal.Split('\n'), line => !line.StartsWith(field + ":", System.StringComparison.Ordinal)));
    Assert.That(Problem(text), Does.EndWith($":missing property '{field}'"));
  }

  [TestCase("id: [", "not valid YAML")]
  [TestCase("- a\n- b\n", "the file must be a mapping")]
  [TestCase("", "the file must be a mapping")]
  public void AFileThatIsNoMapping_IsRefused(string text, string problem)
    => Assert.That(Problem(text), Does.Contain(problem));

  [Test]
  public void AMissingFile_IsAFileProblem() {
    var exception = Assert.Throws<BenchmarkFileException>(() => BenchmarkDefinition.Load(Path.Combine(FixtureFolder, "no-such.bench.yaml")))!;
    Assert.That(exception.Message, Does.StartWith("cannot be read"));
    var diagnostic = exception.ToDiagnostic();
    Assert.That((diagnostic.Code, diagnostic.ExitCode, diagnostic.File), Is.EqualTo(("PLY0078", ExitCode.EnvironmentError, exception.File)));
  }

  [TestCase("sum-loop", true)]
  [TestCase("a", true)]
  [TestCase("9-lives", true)]
  [TestCase("-dash", false)]
  [TestCase("Upper", false)]
  [TestCase("", false)]
  [TestCase("dot.ted", false)]
  public void IsName_AcceptsLowerCaseLettersDigitsAndHyphens(string text, bool expected)
    => Assert.That(BenchmarkDefinition.IsName(text), Is.EqualTo(expected));

}
