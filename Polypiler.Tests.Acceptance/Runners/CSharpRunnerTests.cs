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
using Polypiler.Tests.Verification.Running.Support;

namespace Polypiler.Tests.Acceptance.Runners;

/// <summary>
/// The C# runner against the real C# compiler and runtime of <c>dotnet-sdk</c>: the fixtures of
/// <c>tests/fixtures/runner-csharp</c> build, run and reproduce the images their vector files state by
/// hand; compiler errors, traps, crashes, limits and protocol errors are reported with their codes.
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), Layer("core")]
[RequiresToolchain(CSharpRunner.Toolchain)]
public sealed class CSharpRunnerTests {

  internal static readonly string Fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "runner-csharp");

  private DirectoryInfo? _workspace;

  [SetUp]
  public void CreateWorkspace() => this._workspace = Directory.CreateTempSubdirectory("polypiler-csharp-");

  [TearDown]
  public void DeleteWorkspace() => this._workspace?.Delete(true);

  internal static CSharpRunner Runner(string workspace, RunLimits? driverLimits = null)
    => new(new CrossLanguageRunner(RunnerTesting.Host, RunnerTesting.Toolchains), workspace, null, driverLimits);

  internal static RunnerInput Input(string source, string export, string signature, string profile = "csharp/net10")
    => new(new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["Fixture.cs"] = Encoding.UTF8.GetBytes(source) },
      export, Signature(signature), profile, TrustLevel.Trusted);

  internal static RunnerInput FixtureInput(string file, string export, string signature)
    => Input(File.ReadAllText(Path.Combine(Fixtures, file)), export, signature);

  /// <summary>A signature in the notation of the work order: <c>(a: i32 in, b: i32 in) -&gt; i32</c>.</summary>
  internal static CallSignature Signature(string text) {
    var arrow = text.LastIndexOf("->", StringComparison.Ordinal);
    var inner = text[(text.IndexOf('(') + 1)..text.LastIndexOf(')', arrow)].Trim();
    var result = text[(arrow + 2)..].Trim();
    var parameters = inner.Length == 0 ? [] : Split(inner).Select(parameter => {
      var colon = parameter.IndexOf(':');
      var rest = parameter[(colon + 1)..].Trim();
      var space = rest.LastIndexOf(' ');
      var mode = rest[(space + 1)..] switch { "inout" => ParameterMode.InOut, "out" => ParameterMode.Out, _ => ParameterMode.In };
      return new CallParameter(parameter[..colon].Trim(), ImageType.Parse(rest[..space]), mode);
    }).ToList();
    return new(parameters, result == "void" ? null : ImageType.Parse(result));
  }

  /// <summary>The parameters of a signature, split at the commas outside angle brackets and braces.</summary>
  private static IEnumerable<string> Split(string text) {
    var depth = 0;
    var start = 0;
    for (var index = 0; index < text.Length; ++index) {
      depth += text[index] switch { '<' or '{' => 1, '>' or '}' => -1, _ => 0 };
      if (text[index] == ',' && depth == 0) {
        yield return text[start..index].Trim();
        start = index + 1;
      }
    }

    yield return text[start..].Trim();
  }

  private CompileResult Built(RunnerInput input) {
    var built = Runner(this._workspace!.FullName).Compile(input);
    Assert.That(built.Outcome, Is.EqualTo(RunOutcome.Completed), string.Join("\n", built.Diagnostics.Select(diagnostic => $"{diagnostic}: {diagnostic.Cause}")));
    return built;
  }

  private IReadOnlyList<CallOutcome> Calls(RunnerInput input, params string[] images) {
    var built = this.Built(input);
    return Runner(this._workspace!.FullName).ExecuteFunction(built.Directory, input.Export, input.Signature, [.. images.Select(Convert.FromHexString)]);
  }

  private static string Outcome(CallOutcome outcome) => outcome.Trap is { } trap ? $"!{trap} {outcome.NativeTrapName}" : Convert.ToHexStringLower(outcome.Image!);

  [Test]
  public void TheAddFixture_ReproducesItsVectors() {
    var vectors = VectorFile.Load(Path.Combine(Fixtures, "Add.vectors.json"));
    var report = Runner(this._workspace!.FullName).RunVectors(FixtureInput("Add.cs", "T.F", "(a: i32 in, b: i32 in) -> i32"), vectors);

    Assert.That((report.Total, report.Passed), Is.EqualTo((2, 2)));
    Assert.That(report.Outcomes[0].ActualHex, Is.EqualTo("05000000"));
  }

  [Test]
  public void TheCheckedFixture_ReproducesItsTrapVector() {
    var vectors = VectorFile.Load(Path.Combine(Fixtures, "CheckedAdd.vectors.json"));
    var report = Runner(this._workspace!.FullName).RunVectors(FixtureInput("CheckedAdd.cs", "T.F", "(a: i32 in, b: i32 in) -> i32"), vectors);

    Assert.That(report.Verified, Is.True);
  }

  [Test]
  public void AWrongExpectation_IsReportedWithTheValuesOfTheCall() {
    var vectors = VectorFile.Load(Path.Combine(Fixtures, "Mismatch.vectors.json"));
    var report = Runner(this._workspace!.FullName).RunVectors(FixtureInput("Add.cs", "T.F", "(a: i32 in, b: i32 in) -> i32"), vectors);
    var diagnostics = CSharpRunner.Explain(report, vectors);

    Assert.That(report.Passed, Is.Zero);
    Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0051", "PLY1094" }));
    Assert.That(diagnostics[1].Message, Is.EqualTo("call 0: expected 6, actual 5"));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(10_000)]
  public void AnyNumberOfImages_GivesOneOutcomePerImageInOrder(int count) {
    var input = Input("static class T { public static int Id(int a) => a; }", "T.Id", "(a: i32 in) -> i32");
    var built = this.Built(input);
    var images = Enumerable.Range(0, count).Select(index => BitConverter.GetBytes(index)).ToArray();
    var outcomes = Runner(this._workspace!.FullName).ExecuteFunction(built.Directory, input.Export, input.Signature, images);

    Assert.That(outcomes.Select(outcome => outcome.Image), Is.EqualTo(images));
  }

  [Test]
  public void IntegerLimitsAndFloatSpecials_TravelBitExactly() {
    const string Source = """
      public struct All {
        public sbyte I8; public byte U8; public short I16; public ushort U16; public int I32; public uint U32;
        public long I64; public ulong U64; public bool B; public char C; public float F; public double D;
      }

      public static class T {
        public static All Echo(All value) => value;
      }
      """;
    const string Record = "record{I8:i8,U8:u8,I16:i16,U16:u16,I32:i32,U32:u32,I64:i64,U64:u64,B:bool,C:char16,F:f32,D:f64}";
    var images = new[] {
      "80" + "00" + "0080" + "0000" + "00000080" + "00000000" + "0000000000000080" + "0000000000000000" + "00" + "0000" + "0100c07f" + "010000000000f87f",
      "ff" + "ff" + "ffff" + "ffff" + "ffffffff" + "ffffffff" + "ffffffffffffffff" + "ffffffffffffffff" + "01" + "ffff" + "00000080" + "0000000000000080",
      "00" + "01" + "0000" + "0100" + "00000000" + "01000000" + "0000000000000000" + "0100000000000000" + "00" + "4100" + "0000807f" + "000000000000f0ff",
      "7f" + "fe" + "ff7f" + "feff" + "ffffff7f" + "feffffff" + "ffffffffffffff7f" + "feffffffffffffff" + "01" + "00d8" + "01000000" + "0100000000000000"
    };
    var outcomes = this.Calls(Input(Source, "T.Echo", $"(value: {Record} in) -> {Record}"), images);

    Assert.That(outcomes.Select(Outcome), Is.EqualTo(images));
  }

  [Test]
  public void SequencesAndOptionals_TravelAsTheSharedImages() {
    const string Source = """
      using System;

      public static class T {
        public static double G(double a) => a;
        public static int N(int[]? a) => a == null ? -1 : a.Length;
        public static byte[] Reverse(ReadOnlySpan<byte> data) {
          var copy = data.ToArray();
          Array.Reverse(copy);
          return copy;
        }
        public static string Upper(string text) => text.ToUpperInvariant();
      }
      """;
    Assert.That(this.Calls(Input(Source, "T.G", "(a: f64 in) -> f64"), "010000000000f87f").Select(Outcome), Is.EqualTo(new[] { "010000000000f87f" }));
    Assert.That(this.Calls(Input(Source, "T.N", "(a: opt<seq<i32>> in) -> i32"), "00", "010100000007000000", "0100000000").Select(Outcome),
      Is.EqualTo(new[] { "ffffffff", "01000000", "00000000" }));
    var large = Enumerable.Range(0, 65_536).Select(index => (byte)index).ToArray();
    var image = Convert.ToHexStringLower([.. BitConverter.GetBytes(large.Length), .. large]);
    var reversed = Convert.ToHexStringLower([.. BitConverter.GetBytes(large.Length), .. large.Reverse()]);
    Assert.That(this.Calls(Input(Source, "T.Reverse", "(data: bytes in) -> bytes"), "00000000", "0100000007", image).Select(Outcome),
      Is.EqualTo(new[] { "00000000", "0100000007", reversed }));
    Assert.That(this.Calls(Input(Source, "T.Upper", "(text: utf16 in) -> utf16"), "020000006100e900").Select(Outcome), Is.EqualTo(new[] { "020000004100c900" }));
  }

  [Test]
  public void ParameterModes_PutTheValuesAfterTheCallIntoTheOutputImage() {
    const string Source = """
      using System;

      public enum Colour : byte { Red = 1, Green = 2 }

      public static class T {
        public static int Modes(ref uint state, out int doubled, Span<int> scratch, int[] table, Colour colour, (int, bool) pair) {
          state += 1;
          doubled = (int)state * 2;
          scratch[0] = 7;
          table[0] = 9;
          return (int)colour + pair.Item1 + (pair.Item2 ? 100 : 0);
        }
      }
      """;
    var signature = "(state: u32 inout, doubled: i32 out, scratch: seq<i32> inout, table: seq<i32> inout, colour: enum<u8> in, pair: tuple<i32,bool> in) -> i32";
    var input = "04000000" + "0100000000000000" + "0100000000000000" + "02" + "0500000001";
    var outcomes = this.Calls(Input(Source, "T.Modes", signature), input);

    Assert.That(outcomes.Select(Outcome), Is.EqualTo(new[] { "6b000000" + "05000000" + "0a000000" + "0100000007000000" + "0100000009000000" }));
  }

  [Test]
  public void EveryRowOfTheTrapTable_IsTheClassOfItsException() {
    const string Source = """
      using System;
      using System.Diagnostics;

      public static class T {
        public static int Throw(int which) {
          int[] none = [];
          string? absent = which > 100 ? "" : null;
          return which switch {
            0 => checked(int.MaxValue + which + 1),
            1 => 1 / (which - 1),
            2 => none[which],
            3 => absent!.Length,
            4 => throw new ArgumentException("a"),
            5 => throw new ArgumentNullException("b"),
            6 => throw new ArgumentOutOfRangeException("c"),
            7 => throw new UnreachableException(),
            _ => throw new InvalidOperationException()
          };
        }
      }
      """;
    var outcomes = this.Calls(Input(Source, "T.Throw", "(which: i32 in) -> i32"), [.. Enumerable.Range(0, 9).Select(index => Convert.ToHexStringLower(BitConverter.GetBytes(index)))]);

    Assert.That(outcomes.Select(Outcome), Is.EqualTo(new[] {
      "!overflow OverflowException", "!divide-by-zero DivideByZeroException", "!bounds IndexOutOfRangeException", "!null NullReferenceException",
      "!invalid-argument ArgumentException", "!invalid-argument ArgumentNullException", "!invalid-argument ArgumentOutOfRangeException",
      "!unreachable UnreachableException", "!other:InvalidOperationException InvalidOperationException"
    }));
  }

  [TestCase("public static int F(int a) { int unused; return a; }", "CS0168")]
  [TestCase("public static int F(int a) { int unused = 1; return a; }", "CS0219")]
  [TestCase("public static int F(int a) => \"x\";", "CS0029")]
  public void ARejectedSource_IsABuildFailureWithTheCompilerErrors(string member, string error) {
    var built = Runner(this._workspace!.FullName).Compile(Input($"static class T {{ {member} }}", "T.F", "(a: i32 in) -> i32"));

    Assert.That(built.Outcome, Is.EqualTo(RunOutcome.BuildFailed));
    Assert.That(built.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0030", "PLY1091" }));
    Assert.That(built.Diagnostics[1].Message, Does.Contain(error));
    Assert.That(Directory.Exists(built.Directory), Is.False);
  }

  [Test]
  public void ASignatureTheDriverCannotPass_IsAProtocolErrorOfTheBuild() {
    var source = "public struct P { public readonly int X; public P(int x) => X = x; } public static class T { public static int F(P p) => p.X; }";
    var built = Runner(this._workspace!.FullName).Compile(Input(source, "T.F", "(p: record{X:i32} in) -> i32"));

    Assert.That(built.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0030", "PLY1095" }));
    Assert.That(built.Diagnostics[1].Message, Does.StartWith("the signature does not fit the declaration: Driver.cs"));
  }

  [Test]
  public void AFunctionThatWritesToStandardOutput_IsAProtocolError() {
    var input = Input("static class T { public static int F(int a) { System.Console.WriteLine(a); return a; } }", "T.F", "(a: i32 in) -> i32");
    var built = this.Built(input);
    var error = Assert.Throws<LanguageRunnerException>(() => Runner(this._workspace!.FullName).ExecuteFunction(built.Directory, "T.F", input.Signature, [[1, 0, 0, 0]]));

    Assert.That(error!.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY1095" }));
    Assert.That(error.Diagnostics[0].Message, Is.EqualTo("call 1: the function under test wrote to standard output"));
  }

  [Test]
  public void AnExportTheDriverWasNotBuiltFor_IsAProtocolError() {
    var input = FixtureInput("Add.cs", "T.F", "(a: i32 in, b: i32 in) -> i32");
    var built = this.Built(input);
    var error = Assert.Throws<LanguageRunnerException>(() => Runner(this._workspace!.FullName).ExecuteFunction(built.Directory, "T.G", input.Signature, []));

    Assert.That(error!.Diagnostics.Single().Message, Is.EqualTo("the export 'T.G' does not exist; the driver calls T.F"));
  }

  [Test]
  public void ADriverThatDiesInsideACall_IsACrash() {
    var input = Input("static class T { public static int F(int a) { System.Environment.FailFast(\"gone\"); return a; } }", "T.F", "(a: i32 in) -> i32");
    var built = this.Built(input);
    var error = Assert.Throws<LanguageRunnerException>(() => Runner(this._workspace!.FullName).ExecuteFunction(built.Directory, "T.F", input.Signature, [[1, 0, 0, 0]]));

    Assert.That(error!.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY1092" }));
    Assert.That(error.Diagnostics[0].Message, Does.Contain("gone"));
  }

  [Test]
  public void AFunctionThatNeverReturns_EndsAtTheTimeLimit() {
    var runner = Runner(this._workspace!.FullName, RunnerTesting.Limits(wallTime: TimeSpan.FromSeconds(5)));
    var input = FixtureInput("Spin.cs", "T.F", "(a: i32 in) -> i32");
    var built = runner.Compile(input);
    var error = Assert.Throws<LanguageRunnerException>(() => runner.ExecuteFunction(built.Directory, "T.F", input.Signature, [[1, 0, 0, 0]]));

    Assert.That(error!.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("PLY0031"));
  }

  [Test]
  public void ARecordedFile_PassesAndIsByteStableAcrossTwoRuns() {
    var input = FixtureInput("Wide.cs", "T.G", "(a: f64 in) -> f64");
    var vectors = VectorFile.Parse("""
      {
        "schema":1, "fixture":"runner-csharp/Wide",
        "provenance":{"kind":"published","source":"fixture","detail":"inputs only","generatedBy":"hand"},
        "export":"T.G", "signature":{"parameters":[{"name":"a","type":"f64"}],"result":"f64"},
        "vectors":[{"name":"nan","input":{"hex":"010000000000f87f"}}, {"name":"minus-zero","input":{"hex":"0000000000000080"}}]
      }
      """, "Wide.vectors.json");
    var runner = Runner(this._workspace!.FullName);
    var first = runner.Record(input, vectors);
    var second = runner.Record(input, vectors);

    static string Recorded(VectorFile file) => string.Join("\n", file.Vectors.Select(vector =>
      $"{vector.Name} {Convert.ToHexStringLower(vector.Input)} {Convert.ToHexStringLower(vector.Expected ?? [])} {vector.ExpectedTrap}").Prepend(file.Provenance.ToString()));
    Assert.That(Recorded(first), Is.EqualTo(Recorded(second)));
    Assert.That(first.Vectors[0].Expected, Is.EqualTo(Convert.FromHexString("010000000000f87f")));
    Assert.That(runner.RunVectors(input, first).Verified, Is.True);
    Assert.Throws<InvalidOperationException>(() => runner.Record(input, first));
  }

  [Test]
  public void TheNUnitProject_ReportsItsPassingAndFailingTests() {
    TestReport report;
    try {
      report = Runner(this._workspace!.FullName).RunTests(Path.Combine(Fixtures, "Tests"));
    } catch (LanguageRunnerException error) {
      Assert.Fail(string.Join("\n", error.Diagnostics));
      throw;
    }


    Assert.That((report.Total, report.Passed, report.Failed, report.Skipped), Is.EqualTo((3, 2, 1, 0)));
    Assert.That(report.Failures.Single().Name, Does.EndWith("FailsOnPurpose"));
    Assert.That(report.Failures.Single().Message, Does.Contain("this test fails on purpose"));
  }

}
