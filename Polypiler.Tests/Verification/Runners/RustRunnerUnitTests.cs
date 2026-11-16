using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Verification.Runners;

/// <summary>
/// The Rust runner without the toolchain (#468): the trap table, the reading of Cargo's JSON messages,
/// the driver it generates and what it makes of every kind of answer, given by a runner that replays
/// a recorded one. The acceptance project runs the same against the pinned toolchain.
/// </summary>
[TestFixture]
public sealed class RustRunnerUnitTests {

  /// <summary>Answers every plan with one result and keeps the plans.</summary>
  private sealed class Replay(RunResult result) : ICrossLanguageRunner {

    public List<RunPlan> Plans { get; } = [];

    public Task<RunResult> RunAsync(RunPlan plan, CancellationToken cancellationToken = default) {
      this.Plans.Add(plan);
      return Task.FromResult(result);
    }

  }

  private static RunResult Result(RunOutcome outcome, int exitCode, string stdout, string stderr = "", params Diagnostic[] diagnostics)
    => new(outcome, exitCode, Encoding.UTF8.GetBytes(stdout), Encoding.UTF8.GetBytes(stderr), TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited, diagnostics) { WorkingDirectory = "/run/x" };

  private static readonly IReadOnlyDictionary<string, ReadOnlyMemory<byte>> _package = RustRunner.Package("add.rs", "pub fn add(a: u32, b: u32) -> u32 { a + b }");

  private static readonly RustExport _add = new("fixture::add", new CallSignature([new CallParameter("a", ImageType.U32, ParameterMode.In), new CallParameter("b", ImageType.U32, ParameterMode.In)], ImageType.U32));

  private const string Finished = "{\"reason\":\"build-finished\",\"success\":true}\n";

  private const string Failed = "{\"reason\":\"build-finished\",\"success\":false}\n";

  private static string Message(string level, string? code, string file, string text)
    => $"{{\"reason\":\"compiler-message\",\"message\":{{\"level\":\"{level}\",\"code\":{(code is null ? "null" : $"{{\"code\":\"{code}\"}}")},\"message\":\"{text}\",\"spans\":[{{\"file_name\":\"{file}\",\"line_start\":3,\"column_start\":7,\"is_primary\":true}}]}}}}\n";

  /// <summary>Test design 9 of #468: every row of the trap table.</summary>
  [TestCase("attempt to add with overflow", "overflow", "IntegerOverflow")]
  [TestCase("attempt to divide with overflow", "overflow", "IntegerOverflow")]
  [TestCase("attempt to divide by zero", "divide-by-zero", "DivideByZero")]
  [TestCase("attempt to calculate the remainder with a divisor of zero", "divide-by-zero", "DivideByZero")]
  [TestCase("index out of bounds: the len is 3 but the index is 3", "bounds", "IndexOutOfRange")]
  [TestCase("range end index 4 out of range for slice of length 3", "bounds", "IndexOutOfRange")]
  [TestCase("called `Option::unwrap()` on a `None` value", "null", "NullReference")]
  [TestCase("internal error: entered unreachable code", "unreachable", "Unreachable")]
  [TestCase("IntegerOverflow", "overflow", "IntegerOverflow")]
  [TestCase("IntegerOverflow: TryFromIntError(())", "overflow", "IntegerOverflow")]
  [TestCase("DivideByZero", "divide-by-zero", "DivideByZero")]
  [TestCase("ArgumentOutOfRange", "invalid-argument", "ArgumentOutOfRange")]
  [TestCase("InvalidCast", "other:InvalidCast", "InvalidCast")]
  [TestCase("x", "other:panic", null)]
  [TestCase("assertion failed: x > 0\nmore", "other:panic", null)]
  public void PanicMessage_IsItsTrap(string message, string trapClass, string? trapName) {
    Assert.That(RustTraps.Classify(message).ToString(), Is.EqualTo(trapClass));
    Assert.That(RustTraps.TrapName(message), Is.EqualTo(trapName));
  }

  [Test]
  public void CargoMessages_AreRead() {
    var output = "not json\n{broken\n{\"reason\":\"compiler-artifact\"}\n" + Message("error", "E0308", "src/lib.rs", "mismatched types")
      + Message("warning", "clippy::identity_op", "src/m.rs", "this operation has no effect")
      + "{\"reason\":\"compiler-message\",\"message\":{\"level\":\"error\",\"code\":null,\"message\":\"aborting due to 1 previous error\",\"spans\":[]}}\n"
      + "{\"reason\":\"compiler-message\",\"message\":{\"level\":\"warning\",\"code\":null,\"message\":\"unused import\",\"spans\":[]}}\n" + Failed;
    var messages = CargoMessageParser.Parse(output);
    Assert.That(messages.Select(message => message.ToString()), Is.EqualTo(new[] {
      "error[E0308]: mismatched types at src/lib.rs:3:7",
      "warning[clippy::identity_op]: this operation has no effect at src/m.rs:3:7",
      "warning: unused import"
    }));
    Assert.That(CargoMessageParser.Succeeded(output), Is.False);
    Assert.That(CargoMessageParser.Succeeded(Finished), Is.True);
    Assert.That(CargoMessageParser.Succeeded("plain"), Is.Null);
  }

  [Test]
  public void Driver_DeclaresEveryArgumentWithTheTypeOfItsImage() {
    var signature = new CallSignature([
      new CallParameter("a", ImageType.I8, ParameterMode.In), new CallParameter("b", ImageType.Bool, ParameterMode.In),
      new CallParameter("c", ImageType.F32, ParameterMode.In), new CallParameter("d", ImageType.U64, ParameterMode.In)
    ], ImageType.F64);
    var driver = RustDriverGenerator.Generate("crate_name", [new RustExport("m::f", signature), new RustExport("m::g", new CallSignature([], null)), new RustExport("m::h", new CallSignature([], ImageType.Bool))]);
    Assert.That(driver, Does.Contain("if input.len() != 14 {"));
    Assert.That(driver, Does.Contain("let a0: i8 = i8::from_le_bytes(input[0..1].try_into().unwrap());"));
    Assert.That(driver, Does.Contain("let a1: bool = input[1] != 0;"));
    Assert.That(driver, Does.Contain("let a2: f32 = f32::from_bits(u32::from_le_bytes(input[2..6].try_into().unwrap()));"));
    Assert.That(driver, Does.Contain("let result = crate_name::m::f(a0, a1, a2, a3);\n            hex(&result.to_bits().to_le_bytes())"));
    Assert.That(driver, Does.Contain("let () = result;\n            String::new()"));
    Assert.That(driver, Does.Contain("hex(&[u8::from(result)])"));
    Assert.That(driver, Does.Contain("std::panic::catch_unwind"));
  }

  /// <summary>#460: a sequence is a vector passed borrowed, an inout one exclusively and written after the result; a sequence result is read as a slice.</summary>
  [Test]
  public void Driver_PassesSequencesThroughACursor() {
    var signature = new CallSignature([
      new CallParameter("a", ImageType.I32, ParameterMode.In), new CallParameter("b", ImageType.Seq(ImageType.U8), ParameterMode.In),
      new CallParameter("c", ImageType.Seq(ImageType.F64), ParameterMode.InOut), new CallParameter("d", ImageType.Seq(ImageType.Bool), ParameterMode.InOut)
    ], ImageType.Seq(ImageType.I16));
    var driver = RustDriverGenerator.Generate("k", [new RustExport("m::f", signature), new RustExport("m::g", new CallSignature([new CallParameter("x", ImageType.Seq(ImageType.U32), ParameterMode.InOut)], null))]);
    Assert.That(driver, Does.Contain("fn take<'a>(input: &'a [u8], at: &mut usize, n: usize) -> &'a [u8] {"));
    Assert.That(driver, Does.Contain("let a0: i32 = i32::from_le_bytes(take(input, &mut at, 4).try_into().unwrap());"));
    Assert.That(driver, Does.Contain("let a1: Vec<u8> = (0..count).map(|_| u8::from_le_bytes(take(input, &mut at, 1).try_into().unwrap())).collect();"));
    Assert.That(driver, Does.Contain("let mut a2: Vec<f64> = (0..count).map(|_| f64::from_bits(u64::from_le_bytes(take(input, &mut at, 8).try_into().unwrap()))).collect();"));
    Assert.That(driver, Does.Contain("let mut a3: Vec<bool> = (0..count).map(|_| take(input, &mut at, 1)[0] != 0).collect();"));
    Assert.That(driver, Does.Contain("let result = k::m::f(a0, &a1, &mut a2, &mut a3);"));
    Assert.That(driver, Does.Contain("let values: &[i16] = &result;"));
    Assert.That(driver, Does.Contain("let values: &[f64] = &a2;"));
    Assert.That(driver, Does.Contain("image.extend_from_slice(&value.to_bits().to_le_bytes());"));
    Assert.That(driver, Does.Contain("image.extend_from_slice(&[u8::from(value)]);"));
    Assert.That(driver, Does.Contain("let () = result;\n            let values: &[u32] = &a0;"));
    Assert.That(() => RustDriverGenerator.Generate("c", [new RustExport("f", new CallSignature([new CallParameter("a", ImageType.Seq(ImageType.U8), ParameterMode.Out)], null))]), Throws.ArgumentException);
    Assert.That(() => RustDriverGenerator.Generate("c", [new RustExport("f", new CallSignature([], ImageType.Seq(ImageType.Utf8)))]), Throws.ArgumentException);
    Assert.That(RustDriverGenerator.Generate("c", [new RustExport("f", new CallSignature([], ImageType.Seq(ImageType.U8)))]), Does.Contain("let mut at = 0;\n            if at != input.len() {"));
  }

  /// <summary>An optional value is an Option read after its presence byte: of a scalar the value, of a sequence a vector passed as a slice, written back with the byte.</summary>
  [Test]
  public void Driver_PassesOptionalValuesWithTheirPresence() {
    var signature = new CallSignature([
      new CallParameter("a", ImageType.Opt(ImageType.I32), ParameterMode.In), new CallParameter("b", ImageType.Opt(ImageType.Seq(ImageType.U8)), ParameterMode.InOut)
    ], ImageType.Opt(ImageType.F64));
    var driver = RustDriverGenerator.Generate("k", [new RustExport("m::f", signature)]);
    Assert.That(driver, Does.Contain("let a0: Option<i32> = if take(input, &mut at, 1)[0] != 0 {\n                Some(i32::from_le_bytes(take(input, &mut at, 4).try_into().unwrap()))\n            } else {\n                None\n            };"));
    Assert.That(driver, Does.Contain("let a1: Option<Vec<u8>> = if take(input, &mut at, 1)[0] != 0 {"));
    Assert.That(driver, Does.Contain("(0..count).map(|_| u8::from_le_bytes(take(input, &mut at, 1).try_into().unwrap())).collect()"));
    Assert.That(driver, Does.Contain("let result = k::m::f(a0, a1.as_deref());"));
    Assert.That(driver, Does.Contain("match &result {\n                Some(present12) => {\n                    image.push(1);\n                    image.extend_from_slice(&(*present12).to_bits().to_le_bytes());\n                }\n                None => image.push(0),\n            }"));
    Assert.That(driver, Does.Contain("match &a1 {\n                Some(present12) => {\n                    image.push(1);\n                    let values: &[u8] = &present12;"));
    Assert.That(() => RustDriverGenerator.Generate("c", [new RustExport("f", new CallSignature([new CallParameter("a", ImageType.Opt(ImageType.U8), ParameterMode.Out)], null))]), Throws.ArgumentException);
  }

  [Test]
  public void Driver_RefusesWhatItCannotPass() {
    Assert.Throws<ArgumentException>(() => RustDriverGenerator.Generate("c", [new RustExport("f", new CallSignature([new CallParameter("a", ImageType.Bytes, ParameterMode.In)], null))]));
    Assert.Throws<ArgumentException>(() => RustDriverGenerator.Generate("c", [new RustExport("f", new CallSignature([new CallParameter("a", ImageType.U8, ParameterMode.InOut)], null))]));
    Assert.Throws<ArgumentException>(() => RustDriverGenerator.RustType(ImageType.Utf8));
  }

  [Test]
  public void BuildOptions_AreTheProfileVariables() {
    Assert.That(new RustBuildOptions(OverflowChecks.Wrap, false, 2).Environment, Is.EquivalentTo(new Dictionary<string, string> {
      ["CARGO_PROFILE_DEV_OVERFLOW_CHECKS"] = "false", ["CARGO_PROFILE_DEV_DEBUG_ASSERTIONS"] = "false", ["CARGO_PROFILE_DEV_OPT_LEVEL"] = "2"
    }));
    Assert.That(RustBuildOptions.Dev, Is.EqualTo(new RustBuildOptions(OverflowChecks.Panic, true, 0)));
    Assert.Throws<ArgumentOutOfRangeException>(() => _ = new RustBuildOptions(OverflowChecks.Panic, true, 4));
    Assert.Throws<ArgumentOutOfRangeException>(() => _ = new RustBuildOptions(OverflowChecks.Panic, true, -1));
  }

  [Test]
  public void FixtureFile_IsWrappedIntoAPackage() {
    Assert.That(_package.Keys, Is.EquivalentTo(new[] { "Cargo.toml", "src/lib.rs", "src/add.rs" }));
    Assert.That(Encoding.UTF8.GetString(_package["src/lib.rs"].Span), Is.EqualTo("#[path = \"add.rs\"]\npub mod fixture;\n"));
    Assert.That(Encoding.UTF8.GetString(_package["Cargo.toml"].Span), Does.Contain("edition = \"2024\""));
  }

  [Test]
  public async Task Execute_ReadsOneOutcomePerCall() {
    var replay = new Replay(Result(RunOutcome.Completed, 0, Finished + "05000000\n!attempt to add with overflow\n00000000\n"));
    var runner = new RustRunner(replay, new RustBuildOptions(OverflowChecks.Panic, true), new Dictionary<string, string> { ["RUSTUP_TOOLCHAIN"] = "1.99.0" });
    var execution = await runner.ExecuteAsync(_package, "fixture_package", [_add], [(0, [2, 0, 0, 0, 3, 0, 0, 0]), (0, [255, 255, 255, 255, 1, 0, 0, 0]), (0, new byte[8])]);
    Assert.That(execution.Diagnostics, Is.Empty);
    Assert.That(execution.Outcomes!.Select(outcome => outcome.Image is null ? "!" + outcome.Trap : Convert.ToHexStringLower(outcome.Image)), Is.EqualTo(new[] { "05000000", "!overflow", "00000000" }));
    var step = replay.Plans.Single().Execute;
    Assert.That(step.Arguments, Is.EqualTo(new[] { "run", "--offline", "--quiet", "--message-format=json", "--bin", RustDriverGenerator.Binary }));
    Assert.That(step.Environment["CARGO_PROFILE_DEV_OVERFLOW_CHECKS"], Is.EqualTo("true"));
    Assert.That(step.Environment["CARGO_NET_OFFLINE"], Is.EqualTo("true"));
    Assert.That(step.Environment["RUSTUP_TOOLCHAIN"], Is.EqualTo("1.99.0"));
    Assert.That(Encoding.UTF8.GetString(step.Stdin.Span), Is.EqualTo("0 0200000003000000\n0 ffffffff01000000\n0 0000000000000000\n"));
    Assert.That(replay.Plans.Single().Files.Keys, Does.Contain(RustDriverGenerator.FilePath));
  }

  [Test]
  public async Task Execute_ReportsACompileErrorInTheFixture() {
    var runner = new RustRunner(new Replay(Result(RunOutcome.Completed, 101, Message("error", "E0308", "src/add.rs", "mismatched types") + Failed, "error: could not compile")), RustBuildOptions.Dev);
    var execution = await runner.ExecuteAsync(_package, "fixture_package", [_add], [(0, new byte[8])]);
    Assert.That(execution.Outcomes, Is.Null);
    Assert.That(execution.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0030", "PLY1591" }));
    Assert.That(execution.Diagnostics[1].Message, Does.Contain("E0308"));
  }

  [Test]
  public async Task Execute_ReportsASignatureThatDoesNotFitTheDeclaration() {
    var runner = new RustRunner(new Replay(Result(RunOutcome.Completed, 101, Message("error", "E0308", RustDriverGenerator.FilePath, "mismatched types") + Failed)), RustBuildOptions.Dev);
    var execution = await runner.ExecuteAsync(_package, "fixture_package", [_add], [(0, new byte[8])]);
    Assert.That(execution.Diagnostics.Single().Code, Is.EqualTo("PLY1595"));
    Assert.That(execution.Diagnostics.Single().Message, Does.StartWith("the signature does not fit the declaration"));
  }

  [TestCase(134, "05000000\n", "PLY1592")]
  [TestCase(0, "05000000\n", "PLY1595")]
  [TestCase(2, "", "PLY1595")]
  public async Task Execute_ReportsADriverThatDidNotAnswerEveryCall(int exitCode, string stdout, string code) {
    var runner = new RustRunner(new Replay(Result(RunOutcome.Completed, exitCode, Finished + stdout, "aborted")), RustBuildOptions.Dev);
    var execution = await runner.ExecuteAsync(_package, "fixture_package", [_add], [(0, new byte[8]), (0, new byte[8])]);
    Assert.That((execution.Outcomes, execution.Diagnostics.Single().Code), Is.EqualTo(((IReadOnlyList<RustCallOutcome>?)null, code)));
  }

  [Test]
  public async Task Execute_ReportsAnAnswerThatIsNoImage() {
    var runner = new RustRunner(new Replay(Result(RunOutcome.Completed, 0, Finished + "hello\n")), RustBuildOptions.Dev);
    var execution = await runner.ExecuteAsync(_package, "fixture_package", [_add], [(0, new byte[8])]);
    Assert.That(execution.Diagnostics.Single().Message, Is.EqualTo("the driver printed 'hello', which is no output image"));
  }

  [Test]
  public async Task Execute_RefusesAnUnknownExportAndAnUnpairedType_BeforeItBuilds() {
    var replay = new Replay(Result(RunOutcome.Completed, 0, ""));
    var runner = new RustRunner(replay, RustBuildOptions.Dev);
    var unknown = await runner.ExecuteAsync(_package, "fixture_package", [_add], [(1, new byte[8])]);
    Assert.That(unknown.Diagnostics.Single().Message, Is.EqualTo("there is no export 1"));
    var unpaired = await runner.ExecuteAsync(_package, "fixture_package", [new RustExport("f", new CallSignature([new CallParameter("s", ImageType.Utf8, ParameterMode.In)], null))], [(0, [])]);
    Assert.That(unpaired.Diagnostics.Single().Code, Is.EqualTo("PLY1595"));
    Assert.That(replay.Plans, Is.Empty);
  }

  [Test]
  public async Task Execute_PassesOnTheRunnersOwnFailure() {
    var limit = Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 2000);
    var runner = new RustRunner(new Replay(Result(RunOutcome.TimedOut, -1, "", "", limit)), RustBuildOptions.Dev);
    var execution = await runner.ExecuteAsync(_package, "fixture_package", [_add], [(0, new byte[8])]);
    Assert.That(execution.Diagnostics.Single().Code, Is.EqualTo("PLY0031"));
  }

  [Test]
  public async Task Compile_ReportsTheCompilersVerdict() {
    var ok = await new RustRunner(new Replay(Result(RunOutcome.Completed, 0, Finished)), RustBuildOptions.Dev).CompileAsync(_package);
    Assert.That((ok.Succeeded, ok.Diagnostics.Count), Is.EqualTo((true, 0)));
    var rejected = await new RustRunner(new Replay(Result(RunOutcome.Completed, 101, Message("error", "E0308", "src/add.rs", "mismatched types"))), RustBuildOptions.Dev).CompileAsync(_package);
    Assert.That(rejected.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0030", "PLY1591" }));
    var refused = await new RustRunner(new Replay(Result(RunOutcome.Refused, -1, "", "", Diagnostic.Create(RunnerDiagnostics.IsolationUnavailable, null))), RustBuildOptions.Dev).CompileAsync(_package);
    Assert.That(refused.Diagnostics.Single().Code, Is.EqualTo("PLY0034"));
    var silent = await new RustRunner(new Replay(Result(RunOutcome.Completed, 101, "", "error: no Cargo.toml")), RustBuildOptions.Dev).CompileAsync(_package);
    Assert.That(silent.Diagnostics[1].Message, Does.Contain("error: no Cargo.toml"));
  }

  [Test]
  public async Task Tests_AreReadOneByOne() {
    const string output = "running 4 tests\ntest tests::a ... ok\ntest tests::b ... FAILED\ntest tests::c ... ignored\ntest tests::d - should panic ... ok\n\nfailures:\n";
    var report = await new RustRunner(new Replay(Result(RunOutcome.Completed, 101, output)), RustBuildOptions.Dev).RunTestsAsync(_package);
    Assert.That(report.Tests, Is.EqualTo(new[] { ("tests::a", RustTestOutcome.Passed), ("tests::b", RustTestOutcome.Failed), ("tests::c", RustTestOutcome.Skipped), ("tests::d", RustTestOutcome.Passed) }));
    Assert.That((report.Passed, report.Failed, report.Diagnostics.Count), Is.EqualTo((2, 1, 0)));
    var broken = await new RustRunner(new Replay(Result(RunOutcome.Completed, 101, "", "error[E0308]")), RustBuildOptions.Dev).RunTestsAsync(_package);
    Assert.That(broken.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0030", "PLY1591" }));
    var timedOut = await new RustRunner(new Replay(Result(RunOutcome.TimedOut, -1, "", "", Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 5))), RustBuildOptions.Dev).RunTestsAsync(_package);
    Assert.That(timedOut.Diagnostics.Single().Code, Is.EqualTo("PLY0031"));
  }

  [Test]
  public async Task FormatCheck_AndClippy_ReturnFindings() {
    var files = await new RustRunner(new Replay(Result(RunOutcome.Completed, 1, "/run/x/src/lib.rs\nsrc/m.rs\n")), RustBuildOptions.Dev).FormatCheckAsync(_package);
    Assert.That(files, Is.EqualTo(new[] { "src/lib.rs", "src/m.rs" }));
    var findings = await new RustRunner(new Replay(Result(RunOutcome.Completed, 101, Message("error", "clippy::identity_op", "src/m.rs", "this operation has no effect"))), RustBuildOptions.Dev).ClippyAsync(_package);
    Assert.That(findings.Single().Code, Is.EqualTo("clippy::identity_op"));
  }

}
