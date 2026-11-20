using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Acceptance.Runners;

/// <summary>Checks the language-independent contract against each runner's real fixtures.</summary>
public abstract class LanguageRunnerConformance {

  /// <summary>The runner and the inputs for the operation named by the vectors.</summary>
  protected abstract (ILanguageRunner Runner, RunnerInput Input) Fixture(VectorFile vectors);

  /// <summary>A fixture with a native syntax error and its language-specific diagnostic code.</summary>
  protected abstract (ILanguageRunner Runner, RunnerInput Input, string RejectionCode) BrokenFixture(VectorFile vectors);

  /// <summary>A fixture that builds but cannot finish a call within its configured limit.</summary>
  protected abstract (ILanguageRunner Runner, RunnerInput Input) EndlessFixture(VectorFile vectors);

  [TestCase("add", "u32", "0100000002000000", "03000000")]
  [TestCase("add", "u32", "ffffffff01000000", "00000000")]
  [TestCase("add", "u32", "0000000000000000", "00000000")]
  [TestCase("add", "u32", "0000008000000080", "00000000")]
  [TestCase("div", "i32", "0700000002000000", "03000000")]
  [TestCase("div", "i32", "f9ffffff02000000", "fdffffff")]
  public void ComparesNativeResults(string export, string type, string input, string expected) {
    var vectors = Vectors(export, type, input, $$""" , "expected":{"hex":"{{expected}}"} """);
    var (runner, source) = this.Fixture(vectors);

    Assert.That(runner.RunVectors(source, vectors).Verified, Is.True);
  }

  [Test]
  public void ClassifiesNativeTraps() {
    var vectors = Vectors("div", "i32", "0100000000000000", """, "expectedTrap":"divide-by-zero" """);
    var (runner, source) = this.Fixture(vectors);

    Assert.That(runner.RunVectors(source, vectors).Verified, Is.True);
  }

  [Test]
  public void RecordsAndRechecksExpectations() {
    var vectors = Vectors("add", "u32", "0100000002000000", "");
    var (runner, source) = this.Fixture(vectors);
    var recorded = runner.Record(source, vectors);

    Assert.That(recorded.Provenance.GeneratedBy, Is.EqualTo(runner.Toolchains.First()));
    Assert.That(recorded.Vectors[0].Expected, Is.EqualTo(Convert.FromHexString("03000000")));
    Assert.That(runner.RunVectors(source, recorded).Verified, Is.True);
  }

  [Test]
  public void ReportsAnIncorrectExpectation() {
    var vectors = Vectors("add", "u32", "0100000002000000", """, "expected":{"hex":"04000000"} """);
    var (runner, source) = this.Fixture(vectors);
    var report = runner.RunVectors(source, vectors);

    Assert.That(report.Verified, Is.False);
    Assert.That(report.Outcomes[0].ReferenceHex, Is.EqualTo("04000000"));
    Assert.That(report.Outcomes[0].ActualHex, Is.EqualTo("03000000"));
  }

  [Test]
  public void ReportsNativeBuildRejection() {
    var vectors = Vectors("add", "u32", "0100000002000000", "");
    var (runner, source, rejectionCode) = this.BrokenFixture(vectors);
    var result = runner.Compile(source);

    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.BuildFailed));
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("PLY0030").And.Contain(rejectionCode));
  }

  [Test]
  public void ReportsDriverTimeout() {
    var vectors = Vectors("add", "u32", "0100000002000000", "");
    var (runner, source) = this.EndlessFixture(vectors);
    var error = Assert.Throws<LanguageRunnerException>(() => runner.Record(source, vectors));

    Assert.That(error!.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("PLY0031"));
  }

  [Test]
  public void RepeatingAVectorProducesTheSameOutcome() {
    var vectors = Vectors("add", "u32", "0100000002000000", """, "expected":{"hex":"03000000"} """);
    var (runner, source) = this.Fixture(vectors);
    var first = runner.RunVectors(source, vectors);
    var second = runner.RunVectors(source, vectors);

    Assert.That(first.Verified, Is.True);
    Assert.That(second.Outcomes, Is.EqualTo(first.Outcomes));
  }

  private static VectorFile Vectors(string export, string type, string input, string expectation) => VectorFile.Parse($$"""
    {
      "schema":1,
      "fixture":"runner-conformance",
      "provenance":{"kind":"published","source":"fixture","detail":"integer arithmetic","generatedBy":"hand"},
      "export":"{{export}}",
      "signature":{"parameters":[{"name":"a","type":"{{type}}"},{"name":"b","type":"{{type}}"}],"result":"{{type}}"},
      "vectors":[{"name":"call","input":{"hex":"{{input}}"}{{expectation}} }]
    }
    """, "runner-conformance.vectors.json");

}
