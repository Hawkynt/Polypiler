using System;
using System.Collections.Generic;
using System.IO;
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
/// The Kotlin runner (#426) without a toolchain: the trap table, the signatures a declaration gives,
/// the driver it generates, and what it makes of the results of its plans - a build, a rejection, a
/// protocol error, a crash - which a scripted cross-language runner replays.
/// </summary>
[TestFixture]
public sealed class KotlinRunnerUnitTests {

  private sealed class Scripted(params RunResult[] results) : ICrossLanguageRunner {

    private int _next;

    public List<RunPlan> Plans { get; } = [];

    public Task<RunResult> RunAsync(RunPlan plan, CancellationToken cancellationToken = default) {
      this.Plans.Add(plan);
      return Task.FromResult(results[Math.Min(this._next++, results.Length - 1)]);
    }

  }

  private static RunResult Result(RunOutcome outcome, int exitCode, string stdout = "", string stderr = "", params Diagnostic[] diagnostics)
    => new(outcome, exitCode, Encoding.UTF8.GetBytes(stdout), Encoding.UTF8.GetBytes(stderr), TimeSpan.Zero, TimeSpan.Zero, 0, IsolationLevel.Limited, diagnostics);

  private static readonly CallSignature _ints = new([new("a", ImageType.I32, ParameterMode.In), new("b", ImageType.I32, ParameterMode.In)], ImageType.I32);

  private static RunnerInput Input(string source, CallSignature? signature = null, string export = "add")
    => new(new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["Add.kt"] = Encoding.UTF8.GetBytes(source), ["notes.txt"] = new byte[] { 1 } }, export, signature ?? _ints, "kotlin/jvm", TrustLevel.Trusted);

  private string _workspace = "";

  [SetUp]
  public void Create() => this._workspace = Directory.CreateTempSubdirectory("polypiler-kotlin-unit-").FullName;

  [TearDown]
  public void Delete() => Directory.Delete(this._workspace, true);

  [TestCase(new[] { "ArithmeticException" }, "/ by zero", TrapKind.DivideByZero)]
  [TestCase(new[] { "ArithmeticException" }, null, TrapKind.DivideByZero)]
  [TestCase(new[] { "ArithmeticException" }, "integer overflow", TrapKind.Overflow)]
  [TestCase(new[] { "ArrayIndexOutOfBoundsException", "IndexOutOfBoundsException" }, "Index 5 out of bounds", TrapKind.Bounds)]
  [TestCase(new[] { "NegativeArraySizeException" }, "-1", TrapKind.Bounds)]
  [TestCase(new[] { "NullPointerException" }, null, TrapKind.Null)]
  [TestCase(new[] { "NumberFormatException", "IllegalArgumentException" }, "x", TrapKind.InvalidArgument)]
  [TestCase(new[] { "IllegalStateException" }, "unreachable", TrapKind.Unreachable)]
  [TestCase(new[] { "NoWhenBranchMatchedException" }, "unreachable", TrapKind.Unreachable)]
  public void TrapTable_ClassifiesTheJvmsExceptions(string[] hierarchy, string? message, TrapKind trap) {
    var (classified, name) = KotlinTrapTable.Classify(hierarchy, message);
    Assert.That((classified.Kind, name), Is.EqualTo((trap, hierarchy[0])));
  }

  [Test]
  public void TrapTable_GivesEveryOtherExceptionItsName() {
    Assert.Multiple(() => {
      Assert.That(KotlinTrapTable.Classify(["IllegalStateException"], "other").Trap.ToString(), Is.EqualTo("other:IllegalStateException"));
      Assert.That(KotlinTrapTable.Classify(["StackOverflowError"], null).Trap.ToString(), Is.EqualTo("other:StackOverflowError"));
      Assert.Throws<ArgumentException>(() => KotlinTrapTable.Classify([], null));
      Assert.Throws<ArgumentNullException>(() => KotlinTrapTable.Classify(null!, null));
    });
  }

  [TestCase("Byte", "i8")]
  [TestCase("UShort", "u16")]
  [TestCase("ULong", "u64")]
  [TestCase("Boolean", "bool")]
  [TestCase("Char", "char16")]
  [TestCase("Float", "f32")]
  [TestCase("Double", "f64")]
  [TestCase("String", "utf16")]
  [TestCase("ByteArray", "bytes")]
  [TestCase("IntArray", "seq<i32>")]
  [TestCase("DoubleArray", "seq<f64>")]
  [TestCase("BooleanArray", "seq<bool>")]
  [TestCase("CharArray", "seq<char16>")]
  [TestCase("ShortArray", "seq<i16>")]
  [TestCase("LongArray", "seq<i64>")]
  [TestCase("FloatArray", "seq<f32>")]
  [TestCase("Long?", "opt<i64>")]
  [TestCase("UByte", "u8")]
  [TestCase("UInt", "u32")]
  [TestCase("Short", "i16")]
  public void Signatures_MapTheKotlinTypes(string kotlin, string image) => Assert.That(KotlinSignatures.ImageOf(kotlin)!.ToString(), Is.EqualTo(image));

  [Test]
  public void Signatures_AreDerivedFromTheDeclaration() {
    Assert.Multiple(() => {
      Assert.That(KotlinSignatures.ImageOf("Map<Int, Int>"), Is.Null);
      Assert.That(KotlinSignatures.ImageOf("Any?"), Is.Null);
      Assert.That(KotlinSignatures.Derive("package p\n\ninline fun add(a: Int, b: Int = 2): Int = a + b\n", "add").ToString(), Is.EqualTo(_ints.ToString()));
      Assert.That(KotlinSignatures.Derive("fun go(): Unit {}", "go").Result, Is.Null);
      Assert.That(KotlinSignatures.Derive("fun go() {}", "go").Parameters, Is.Empty);
      Assert.Throws<ArgumentException>(() => KotlinSignatures.Derive("fun go() {}", "stop"));
      Assert.Throws<ArgumentException>(() => KotlinSignatures.Derive("fun go(a) {}", "go"));
      Assert.Throws<ArgumentException>(() => KotlinSignatures.Derive("fun go(a: Any) {}", "go"));
      Assert.Throws<ArgumentException>(() => KotlinSignatures.Derive("fun go(): Any = 1", "go"));
      Assert.That(KotlinSignatures.PackageOf("// x\npackage a.b\n"), Is.EqualTo("a.b"));
      Assert.That(KotlinSignatures.PackageOf("fun f() {}"), Is.Null);
      var array = new CallSignature([new("a", ImageType.Seq(ImageType.I32), ParameterMode.In)], ImageType.I32);
      var inout = new CallSignature([new("a", ImageType.Seq(ImageType.I32), ParameterMode.InOut)], ImageType.I32);
      Assert.That(KotlinSignatures.Agree(inout, array), Is.True);
      Assert.That(KotlinSignatures.Agree(_ints, array), Is.False);
      Assert.That(KotlinSignatures.Agree(new CallSignature([new("a", ImageType.I32, ParameterMode.InOut), new("b", ImageType.I32, ParameterMode.In)], ImageType.I32), _ints), Is.False);
    });
  }

  [Test]
  public void Driver_ReadsAndWritesEveryImageType() {
    var signature = new CallSignature([
      new("a", ImageType.I8, ParameterMode.In), new("b", ImageType.U16, ParameterMode.In), new("c", ImageType.U32, ParameterMode.In), new("d", ImageType.U64, ParameterMode.In),
      new("e", ImageType.Bool, ParameterMode.In), new("f", ImageType.Char16, ParameterMode.In), new("g", ImageType.F32, ParameterMode.In), new("h", ImageType.F64, ParameterMode.In),
      new("i", ImageType.Utf16, ParameterMode.In), new("j", ImageType.Bytes, ParameterMode.InOut), new("k", ImageType.Seq(ImageType.I64), ParameterMode.InOut), new("l", ImageType.Opt(ImageType.I32), ParameterMode.In),
      new("m", ImageType.Seq(ImageType.Bool), ParameterMode.In), new("n", ImageType.Seq(ImageType.Char16), ParameterMode.In), new("o", ImageType.Seq(ImageType.F32), ParameterMode.In),
      new("p", ImageType.Seq(ImageType.F64), ParameterMode.In), new("q", ImageType.Seq(ImageType.I8), ParameterMode.In), new("r", ImageType.Seq(ImageType.I16), ParameterMode.In),
      new("s", ImageType.I64, ParameterMode.In)
    ], ImageType.Opt(ImageType.Utf16));
    var driver = KotlinDriverGenerator.Generate("p.q", "go", signature);
    Assert.Multiple(() => {
      Assert.That(driver, Does.StartWith("package p.q\n"));
      Assert.That(driver, Does.Contain("    val a0 = input.get()\n").And.Contain("input.getShort().toUShort()").And.Contain("input.getInt().toUInt()").And.Contain("input.getLong().toULong()"));
      Assert.That(driver, Does.Contain("String(CharArray(count(input)) { input.getChar() })").And.Contain("ByteArray(count(input)) { input.get() }").And.Contain("LongArray(count(input)) { input.getLong() }"));
      Assert.That(driver, Does.Contain("if (input.get().toInt() != 0) input.getInt() else null").And.Contain("BooleanArray").And.Contain("CharArray(count(input))").And.Contain("FloatArray").And.Contain("DoubleArray").And.Contain("ShortArray"));
      Assert.That(driver, Does.Contain("    val result = go(a0, a1").And.Contain("if (result == null) output.write(0) else { output.write(1); run { put(output, result.length.toLong(), 4)"));
      Assert.That(driver, Does.Contain("run { put(output, a9.size.toLong(), 4); for (e in a9) put(output, e.toLong(), 1) }").And.Contain("for (e in a10) put(output, e, 8)"));
      Assert.That(driver, Does.Contain("    e is ArithmeticException && (e.message == null || e.message == \"/ by zero\") -> \"divide-by-zero\"\n"));
      Assert.That(driver, Does.Contain("    e is IllegalStateException && e.message == \"unreachable\" -> \"unreachable\"\n"));
      Assert.That(KotlinDriverGenerator.MainClass(null), Is.EqualTo("PolypilerDriverKt"));
      Assert.That(KotlinDriverGenerator.MainClass("a.b"), Is.EqualTo("a.b.PolypilerDriverKt"));
    });
  }

  [Test]
  public void Driver_WritesScalarsOfEveryKind() {
    foreach (var type in new[] { ImageType.Bool, ImageType.Char16, ImageType.F32, ImageType.F64, ImageType.I64, ImageType.U64, ImageType.I16 }) {
      var driver = KotlinDriverGenerator.Generate(null, "go", new CallSignature([], type));
      Assert.That(driver, Does.Contain("    val result = go()\n"), type.ToString());
    }

    Assert.That(KotlinDriverGenerator.Generate(null, "go", new CallSignature([], null)), Does.Contain("    go()\n").And.Not.StartWith("package"));
  }

  [Test]
  public void Driver_RefusesWhatItCannotPass() {
    Assert.Multiple(() => {
      Assert.Throws<ArgumentException>(() => KotlinDriverGenerator.Generate(null, "go", new CallSignature([new("a", ImageType.I32, ParameterMode.Out)], null)));
      Assert.Throws<ArgumentException>(() => KotlinDriverGenerator.Generate(null, "go", new CallSignature([new("a", ImageType.I32, ParameterMode.InOut)], null)));
      Assert.Throws<ArgumentException>(() => KotlinDriverGenerator.Generate(null, "go", new CallSignature([new("a", ImageType.Seq(ImageType.U32), ParameterMode.In)], null)));
      Assert.Throws<ArgumentException>(() => KotlinDriverGenerator.Generate(null, "go", new CallSignature([new("a", ImageType.BigInt, ParameterMode.In)], null)));
      Assert.Throws<ArgumentException>(() => KotlinDriverGenerator.Generate(null, "go", new CallSignature([], ImageType.BigInt)));
      Assert.Throws<ArgumentNullException>(() => KotlinDriverGenerator.Generate(null, null!, _ints));
    });
  }

  [Test]
  public void Compile_BuildsTheUnitAndTheDriverIntoOneJar() {
    var replay = new Scripted(Result(RunOutcome.Completed, 0) with { Collected = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [KotlinRunner.Jar] = new byte[] { 0x50, 0x4b } } });
    var runner = new KotlinRunner(replay, this._workspace, KotlinBuildOptions.Default with { CompilerOptions = ["-jvm-target", "17"] }, new Dictionary<string, string>(StringComparer.Ordinal) { ["JAVA_HOME"] = "/jdk" });
    var built = runner.Compile(Input("package demo\n\nfun add(a: Int, b: Int): Int = a + b\n"));
    var plan = replay.Plans.Single();
    Assert.Multiple(() => {
      Assert.That(built.Outcome, Is.EqualTo(RunOutcome.Completed));
      Assert.That(File.ReadAllBytes(Path.Combine(built.Directory, KotlinRunner.Jar)), Is.EqualTo(new byte[] { 0x50, 0x4b }));
      Assert.That(plan.Build.Single().Arguments, Is.EqualTo(new[] { "Add.kt", "PolypilerDriver.kt", "-include-runtime", "-jvm-target", "17", "-Werror", "-d", "driver.jar" }));
      Assert.That(plan.Build.Single().Environment["JAVA_HOME"], Is.EqualTo("/jdk"));
      Assert.That(Encoding.UTF8.GetString(plan.Files[KotlinDriverGenerator.FileName].Span), Does.StartWith("package demo\n"));
      Assert.That(runner.Language, Is.EqualTo("kotlin"));
      Assert.That(runner.Toolchains, Is.EqualTo(new[] { "kotlinc", "jdk" }));
      Assert.That(runner.Options.WarningsAsErrors, Is.True);
    });
  }

  [Test]
  public void Compile_ReportsTheCompilersRejection() {
    var failed = Result(RunOutcome.BuildFailed, 1, "", "", Diagnostic.Create(RunnerDiagnostics.BuildFailed, null, "kotlinc", 1) with { Cause = "Add.kt:1:5: error: unresolved reference\nwarning: x" });
    var runner = new KotlinRunner(new Scripted(failed), this._workspace, KotlinBuildOptions.Default with { WarningsAsErrors = false });
    var built = runner.Compile(Input("fun add(a: Int, b: Int): Int = a +\n"));
    Assert.Multiple(() => {
      Assert.That(built.Outcome, Is.EqualTo(RunOutcome.BuildFailed));
      Assert.That(built.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0030", "PLY1691" }));
      Assert.That(built.Diagnostics[1].Message, Does.EndWith("Add.kt:1:5: error: unresolved reference"));
    });
    var bare = Result(RunOutcome.BuildFailed, 1, "", "", Diagnostic.Create(RunnerDiagnostics.BuildFailed, null, "kotlinc", 1) with { Cause = " out of memory " });
    Assert.That(new KotlinRunner(new Scripted(bare), this._workspace).Compile(Input("fun add(a: Int, b: Int): Int = a + b")).Diagnostics[1].Message, Does.EndWith("out of memory"));
  }

  [Test]
  public void Compile_RefusesASignatureTheDeclarationContradicts() {
    var replay = new Scripted(Result(RunOutcome.Completed, 0));
    var runner = new KotlinRunner(replay, this._workspace);
    Assert.Multiple(() => {
      Assert.That(runner.Compile(Input("fun add(a: Long, b: Int): Int = 0")).Diagnostics.Single().Code, Is.EqualTo("PLY1695"));
      Assert.That(runner.Compile(Input("fun sub(a: Int, b: Int): Int = 0")).Diagnostics.Single().Message, Does.Contain("no top-level function 'add'"));
      Assert.That(runner.Compile(Input("fun add(a: UIntArray): Int = 0", new CallSignature([new("a", ImageType.Seq(ImageType.U32), ParameterMode.In)], ImageType.I32))).Outcome, Is.EqualTo(RunOutcome.BuildFailed));
      Assert.That(replay.Plans, Is.Empty);
    });
  }

  private string Built() {
    var directory = Path.Combine(this._workspace, "built");
    Directory.CreateDirectory(directory);
    File.WriteAllBytes(Path.Combine(directory, KotlinRunner.Jar), [1, 2]);
    File.WriteAllLines(Path.Combine(directory, "main.txt"), ["demo.PolypilerDriverKt", "demo.AddKt"]);
    return directory;
  }

  [Test]
  public void ExecuteFunction_ReadsTheImagesAndTraps() {
    var replay = new Scripted(Result(RunOutcome.Completed, 0, "05000000\n!divide-by-zero ArithmeticException\n"));
    var runner = new KotlinRunner(replay, this._workspace);
    var outcomes = runner.ExecuteFunction(this.Built(), "add", _ints, [[2, 0, 0, 0, 3, 0, 0, 0], [1, 0, 0, 0, 0, 0, 0, 0]]);
    Assert.Multiple(() => {
      Assert.That(outcomes[0].Image, Is.EqualTo(new byte[] { 5, 0, 0, 0 }));
      Assert.That(outcomes[1].Trap!.Kind, Is.EqualTo(TrapKind.DivideByZero));
      Assert.That(replay.Plans.Single().Execute.Arguments, Is.EqualTo(new[] { "-cp", "driver.jar", "demo.PolypilerDriverKt", "inputs.hex" }));
    });
  }

  [TestCase(2, "PLY1695")]
  [TestCase(1, "PLY1692")]
  public void ExecuteFunction_ReportsADriverThatEndedBadly(int exitCode, string code) {
    var runner = new KotlinRunner(new Scripted(Result(RunOutcome.Completed, exitCode, "", "boom")), this._workspace);
    var error = Assert.Throws<LanguageRunnerException>(() => runner.ExecuteFunction(this.Built(), "add", _ints, [[2, 0, 0, 0, 3, 0, 0, 0]]));
    Assert.That(error!.Diagnostics.Select(diagnostic => diagnostic.Code), Has.Member(code));
  }

  [Test]
  public void ExecuteFunction_PassesOnAFailedRun() {
    var runner = new KotlinRunner(new Scripted(Result(RunOutcome.TimedOut, -1, "", "", Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 3000))), this._workspace);
    var error = Assert.Throws<LanguageRunnerException>(() => runner.ExecuteFunction(this.Built(), "add", _ints, [[2, 0, 0, 0, 3, 0, 0, 0]]));
    Assert.That(error!.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0031" }));
  }

  [Test]
  public void Execute_RunsTheMainOfTheFirstFile() {
    var replay = new Scripted(Result(RunOutcome.Completed, 0, "42\n"));
    var runner = new KotlinRunner(replay, this._workspace);
    var compiled = new CompileResult(RunOutcome.Completed, this.Built(), [], Result(RunOutcome.Completed, 0));
    Assert.Multiple(() => {
      Assert.That(Encoding.UTF8.GetString(runner.Execute(compiled, ["x"]).Stdout.Span), Is.EqualTo("42\n"));
      Assert.That(replay.Plans.Single().Execute.Arguments, Is.EqualTo(new[] { "-cp", "driver.jar", "demo.AddKt", "x" }));
      Assert.Throws<NotSupportedException>(() => runner.RunTests(this._workspace));
      Assert.Throws<ArgumentNullException>(() => runner.Execute(null!, []));
    });
  }

  [Test]
  public void RunHelper_StartsAJarOnTheJdk() {
    var helper = Path.Combine(this._workspace, "helper.jar");
    File.WriteAllBytes(helper, [7]);
    var replay = new Scripted(Result(RunOutcome.Completed, 0, "ok"));
    var result = new KotlinRunner(replay, this._workspace).RunHelper(helper, ["--x"], Encoding.UTF8.GetBytes("in"));
    Assert.Multiple(() => {
      Assert.That(result.ExitCode, Is.Zero);
      Assert.That(replay.Plans.Single().Execute.Arguments, Is.EqualTo(new[] { "-jar", "helper.jar", "--x" }));
      Assert.That(Encoding.UTF8.GetString(replay.Plans.Single().Execute.Stdin.Span), Is.EqualTo("in"));
    });
  }

  [Test]
  public void FileClass_IsTheClassTheCompilerNames() {
    Assert.Multiple(() => {
      Assert.That(KotlinRunner.FileClass("add.kt", null), Is.EqualTo("AddKt"));
      Assert.That(KotlinRunner.FileClass("my-file.kt", "a.b"), Is.EqualTo("a.b.My_fileKt"));
      Assert.Throws<ArgumentNullException>(() => new KotlinRunner(null!, this._workspace));
      Assert.Throws<ArgumentNullException>(() => new KotlinRunner(new Scripted(), null!));
    });
  }

  [Test]
  public void Registry_ResolvesTheRunnerByItsLanguage() {
    var runners = new LanguageRunners();
    runners.Register(new KotlinRunner(new Scripted(), this._workspace));
    Assert.That(runners.Resolve("kotlin"), Is.InstanceOf<KotlinRunner>());
    Assert.That(runners.TryGet<IHelperRunner>("kotlin", out _), Is.True);
  }

}
