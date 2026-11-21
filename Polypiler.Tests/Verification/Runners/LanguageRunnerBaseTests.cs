using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Verification.Runners;

[TestFixture]
public sealed class LanguageRunnerBaseTests {

  [Test]
  public void RecordsImagesAndTrapsThenComparesThem() {
    var source = Vectors();
    var runner = new Runner("03000000\n!divide-by-zero ZeroDivision\n");
    var input = Input(source);
    var recorded = runner.Record(input, source);

    Assert.That(recorded.Provenance.GeneratedBy, Is.EqualTo("dotnet-sdk"));
    Assert.That(recorded.Vectors[0].Expected, Is.EqualTo(new byte[] { 3, 0, 0, 0 }));
    Assert.That(recorded.Vectors[1].ExpectedTrap, Is.EqualTo(new TrapClass(TrapKind.DivideByZero)));
    Assert.That(runner.LastTrust, Is.EqualTo(TrustLevel.Trusted));
    Assert.That(source.Vectors[0].Expected, Is.Null);
    Assert.That(runner.RunVectors(input, recorded).Passed, Is.EqualTo(2));
    Assert.That(() => runner.Record(input, recorded), Throws.TypeOf<InvalidOperationException>());

    var different = new Runner("04000000\n!overflow Overflow\n");
    Assert.That(different.RunVectors(input, recorded).Passed, Is.Zero);
  }

  [Test]
  public void RejectsMismatchedContractsAndIncompleteExpectations() {
    var source = Vectors();
    var runner = new Runner("");
    var input = Input(source);

    Assert.That(() => runner.Record(input with { Export = "other" }, source), Throws.TypeOf<ArgumentException>());
    Assert.That(() => runner.RunVectors(input, source), Throws.TypeOf<ArgumentException>());
  }

  [Test]
  public void RequiresATypedVectorFile() {
    var source = Vectors();
    var untyped = VectorFile.Parse("""
      {
        "schema": 1,
        "fixture": "runner",
        "provenance": {"kind":"published","source":"test","detail":"test","generatedBy":"hand"},
        "vectors": [{"name":"a","input":{"hex":"03000000"}}]
      }
      """, "untyped.vectors.json");

    var error = Assert.Throws<ImageException>(() => new Runner("").Record(Input(source), untyped));
    Assert.That(error!.ToDiagnostic().Code, Is.EqualTo("PLY0064"));
  }

  [Test]
  public void RejectsNonzeroDriverExitWithValidOutput() {
    var vectors = Vectors();
    var runner = new Runner("03000000\n03000000\n", 1);
    Assert.That(() => runner.Record(Input(vectors), vectors), Throws.TypeOf<LanguageRunnerException>());
  }

  [Test]
  public void TreatsCallerSuppliedDirectoriesAsUntrusted() {
    var vectors = Vectors();
    var runner = new Runner("03000000\n");
    _ = runner.ExecuteFunction("caller", "add", vectors.Signature!, [[3, 0, 0, 0]]);
    Assert.That(runner.LastTrust, Is.EqualTo(TrustLevel.Untrusted));
  }

  private static RunnerInput Input(VectorFile vectors) => new(new Dictionary<string, ReadOnlyMemory<byte>>(), "add", vectors.Signature!, "test", TrustLevel.Trusted);

  private static VectorFile Vectors() => VectorFile.Parse("""
    {
      "schema": 1,
      "fixture": "runner",
      "provenance": {"kind":"published","source":"test","detail":"test","generatedBy":"hand"},
      "export": "add",
      "signature": {"parameters":[{"name":"a","type":"i32"}],"result":"i32"},
      "vectors": [{"name":"a","input":{"hex":"03000000"}},{"name":"b","input":{"hex":"00000000"}}]
    }
    """, "runner.vectors.json");

  private sealed class Runner(string stdout, int exitCode = 0) : LanguageRunnerBase {

    public override string Language => "test";
    public override IReadOnlyList<string> Toolchains => ["dotnet-sdk"];
    public override CompileResult Compile(RunnerInput input) => new(RunOutcome.Completed, "test", [], Result("", 0));
    public override RunResult Execute(CompileResult compiled, IReadOnlyList<string> arguments) => throw new NotSupportedException();
    public override TestReport RunTests(string projectDirectory) => throw new NotSupportedException();
    public TrustLevel LastTrust { get; private set; }

    protected override RunResult RunDriver(string directory, string export, CallSignature signature, ReadOnlyMemory<byte> inputs, TrustLevel trust) {
      this.LastTrust = trust;
      return Result(stdout, exitCode);
    }

    private static RunResult Result(string text, int code) => new(RunOutcome.Completed, code, Encoding.UTF8.GetBytes(text), default, default, default, 0, IsolationLevel.Limited, []);

  }

}
