using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Infra.Ci;
using Polypiler.Tests.Verification.Running.Support;

namespace Polypiler.Tests.Acceptance.Runners;

/// <summary>
/// The Kotlin runner (#426) on the pinned <c>kotlinc</c> and JDK: the language-independent contract
/// on its fixtures (<c>tests/fixtures/runner-kotlin</c>), the vector files of the fixtures, wide
/// values and arrays bit-exactly, a signature that contradicts the declaration and a driver that
/// cannot read its input. Every unit is compiled and executed for real.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), Layer("jvm")]
[RequiresToolchain("kotlinc"), RequiresToolchain("jdk")]
public sealed class KotlinRunnerTests : LanguageRunnerConformance {

  /// <summary>The folder of the runner's fixtures.</summary>
  public static string Fixtures { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "runner-kotlin");

  private DirectoryInfo? _directory;

  [SetUp]
  public void CreateWorkspace() => this._directory = Directory.CreateTempSubdirectory("polypiler-kotlin-");

  [TearDown]
  public void DeleteWorkspace() => this._directory?.Delete(true);

  /// <summary>A runner over the repository lock, the JDK's <c>java</c> found beside its <c>javac</c>, keeping its builds in a folder.</summary>
  /// <param name="workspace">The folder.</param>
  /// <param name="limits">The limits of one run of the driver; the default ones when <see langword="null"/>.</param>
  internal static KotlinRunner Runner(string workspace, RunLimits? limits = null) {
    var commands = new JvmCommands(RunnerTesting.Toolchains);
    var environment = commands.JdkHome is { } home ? new Dictionary<string, string>(StringComparer.Ordinal) { ["JAVA_HOME"] = home } : null;
    return new KotlinRunner(new CrossLanguageRunner(RunnerTesting.Host, commands.Toolchains), workspace, KotlinBuildOptions.Default with { DriverLimits = limits ?? RunLimits.Default }, environment);
  }

  /// <summary>The input of a unit of one file.</summary>
  internal static RunnerInput Input(string file, string source, VectorFile vectors)
    => new(new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [file] = Encoding.UTF8.GetBytes(source) }, vectors.Export!, vectors.Signature!, "kotlin/jvm", TrustLevel.Trusted);

  private static string Source(string file) => File.ReadAllText(Path.Combine(Fixtures, file));

  private static string FixtureOf(VectorFile vectors) => (vectors.Export, vectors.Signature!.Result!.ToString()) switch {
    ("add", "u32") => "AddUnsigned.kt",
    ("add", _) => "Add.kt",
    _ => "Div.kt"
  };

  protected override (ILanguageRunner Runner, RunnerInput Input) Fixture(VectorFile vectors) {
    var file = FixtureOf(vectors);
    return (Runner(this._directory!.FullName), Input(file, Source(file), vectors));
  }

  protected override (ILanguageRunner Runner, RunnerInput Input, string RejectionCode) BrokenFixture(VectorFile vectors)
    => (Runner(this._directory!.FullName), Input("Broken.kt", Source("Broken.kt"), vectors), KotlinRunnerDiagnostics.Rejected.Code);

  protected override (ILanguageRunner Runner, RunnerInput Input) EndlessFixture(VectorFile vectors)
    => (Runner(this._directory!.FullName, RunnerTesting.Limits(wallTime: TimeSpan.FromSeconds(3))),
      Input("Spin.kt", Source("Spin.kt").Replace("spin(a: Int): Int", "add(a: UInt, b: UInt): UInt", StringComparison.Ordinal), vectors));

  [TestCase("Add.kt", "add.vectors.json")]
  [TestCase("Div.kt", "div.vectors.json")]
  [TestCase("Sum.kt", "sum.vectors.json")]
  public void Fixture_ReproducesItsVectors(string source, string vectorFile) {
    var vectors = VectorFile.Parse(File.ReadAllText(Path.Combine(Fixtures, vectorFile)), vectorFile);
    var report = Runner(this._directory!.FullName).RunVectors(Input(source, Source(source), vectors), vectors);
    Assert.That((report.Total, report.Passed), Is.EqualTo((vectors.Vectors.Count, vectors.Vectors.Count)), string.Join("; ", report.Outcomes.Select(outcome => $"{outcome.Name}: {outcome.ReferenceHex} / {outcome.ActualHex}")));
  }

  [Test]
  public void SignatureThatContradictsTheDeclaration_IsAProtocolError() {
    var vectors = VectorFile.Parse(File.ReadAllText(Path.Combine(Fixtures, "add.vectors.json")), "add.vectors.json");
    var result = Runner(this._directory!.FullName).Compile(Input("Div.kt", Source("Div.kt").Replace("div", "add", StringComparison.Ordinal).Replace("Int", "Long", StringComparison.Ordinal), vectors));
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.BuildFailed));
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { KotlinRunnerDiagnostics.Protocol.Code }));
  }

  [Test]
  public void ExportTheUnitDoesNotDefine_IsAProtocolError() {
    var vectors = VectorFile.Parse(File.ReadAllText(Path.Combine(Fixtures, "add.vectors.json")), "add.vectors.json");
    var result = Runner(this._directory!.FullName).Compile(Input("Div.kt", Source("Div.kt"), vectors));
    Assert.That(result.Diagnostics.Single().Message, Does.Contain("no top-level function 'add'"));
  }

  [Test]
  public void WideValuesAndArrays_TravelBitExactly() {
    var runner = Runner(this._directory!.FullName);
    var id = Record(runner, "Wide.kt", "id", "u64", "", "ffffffffffffffff");
    var nan = Record(runner, "Wide.kt", "nan", "f64", "", "010000000000f87f");
    var sum = Record(runner, "Sum.kt", "sum", "seq<i32>", "i32", "03000000010000000200000003000000");
    Assert.Multiple(() => {
      Assert.That(Convert.ToHexStringLower(id), Is.EqualTo("ffffffffffffffff"));
      Assert.That(Convert.ToHexStringLower(nan), Is.EqualTo("010000000000f87f"));
      Assert.That(Convert.ToHexStringLower(sum), Is.EqualTo("06000000"));
    });
  }

  /// <summary>Records the output image of one call of a function of one parameter.</summary>
  private static byte[] Record(KotlinRunner runner, string file, string export, string type, string result, string input) {
    var vectors = VectorFile.Parse($$"""
      {
        "schema":1, "fixture":"runner-kotlin.{{export}}",
        "provenance":{"kind":"published","source":"fixture","detail":"identity","generatedBy":"hand"},
        "export":"{{export}}",
        "signature":{"parameters":[{"name":"a","type":"{{type}}"}],"result":"{{(result.Length == 0 ? type : result)}}"},
        "vectors":[{"name":"call","input":{"hex":"{{input}}"} } ]
      }
      """, export + ".vectors.json");
    return runner.Record(Input(file, Source(file), vectors), vectors).Vectors[0].Expected!;
  }

  [Test]
  public void DriverThatCannotReadItsInput_EndsWithAProtocolError() {
    var vectors = VectorFile.Parse(File.ReadAllText(Path.Combine(Fixtures, "add.vectors.json")), "add.vectors.json");
    var runner = Runner(this._directory!.FullName);
    var built = runner.Compile(Input("Add.kt", Source("Add.kt"), vectors));
    var error = Assert.Throws<LanguageRunnerException>(() => runner.ExecuteFunction(built.Directory, "add", new CallSignature([new("a", ImageType.I32, ParameterMode.In)], ImageType.I32), [[1, 0, 0, 0]]));
    Assert.That(error!.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain(KotlinRunnerDiagnostics.Protocol.Code));
  }

  [Test]
  public void Execute_RunsTheMainOfTheUnit() {
    var vectors = VectorFile.Parse(File.ReadAllText(Path.Combine(Fixtures, "add.vectors.json")), "add.vectors.json");
    var runner = Runner(this._directory!.FullName);
    var built = runner.Compile(Input("Main.kt", Source("Add.kt") + "\nfun main(args: Array<String>) {\n    println(add(args.size, 40))\n}\n", vectors));
    var result = runner.Execute(built, ["x", "y"]);
    Assert.That(Encoding.UTF8.GetString(result.Stdout.Span), Is.EqualTo("42\n"));
  }

}
