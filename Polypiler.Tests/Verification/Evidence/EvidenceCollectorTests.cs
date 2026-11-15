using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Evidence;
using Polypiler.Core.Verification.Oracles;

namespace Polypiler.Tests.Verification.Evidence;

/// <summary><see cref="EvidenceCollector"/>: what each add keeps, what replaces, and what is refused.</summary>
[TestFixture]
public sealed class EvidenceCollectorTests {

  /// <summary>An empty collector holds nothing.</summary>
  [Test]
  public void NewCollector_IsEmpty() {
    var collector = new EvidenceCollector();
    Assert.That((collector.Sources.Count, collector.TargetCode.Count, collector.ToolVersions.Count, collector.FixtureValues.Count, collector.Processes.Count, collector.Files.Count),
      Is.EqualTo((0, 0, 0, 0, 0, 0)));
    Assert.That((collector.Manifest, collector.Comparison, collector.OptDiagnostics), Is.EqualTo(((string?)null, ((ComparisonReport, ExactnessPolicy)?)null, (string?)null)));
  }

  /// <summary>Lists keep every item in order; keyed values and the single items replace.</summary>
  [Test]
  public void Adds_KeepOrReplace() {
    var collector = new EvidenceCollector();
    collector.AddSource("a.js");
    collector.AddSource("b.js");
    collector.AddTargetCode("a.cs");
    collector.AddManifest("first.json");
    collector.AddManifest("second.json");
    collector.AddToolVersion("node", "22.0.0");
    collector.AddToolVersion("dotnet-sdk", "10.0.404");
    collector.AddToolVersion("node", "24.22.2");
    collector.AddFixtureValue("input", "abc");
    collector.AddFixtureValue("input", "");
    var first = EvidenceFixtures.Compare("00", "00");
    var second = EvidenceFixtures.Compare("01", "01");
    collector.AddComparison(first, new ExactnessPolicy.Exact());
    collector.AddComparison(second, new ExactnessPolicy.Exact());
    collector.AddProcess("run", "out", "err", 1);
    Assert.That(collector.Sources, Is.EqualTo(new[] { "a.js", "b.js" }));
    Assert.That(collector.TargetCode, Is.EqualTo(new[] { "a.cs" }));
    Assert.That(collector.Manifest, Is.EqualTo("second.json"));
    Assert.That(collector.ToolVersions, Is.EqualTo(new[] { new KeyValuePair<string, string>("node", "24.22.2"), new KeyValuePair<string, string>("dotnet-sdk", "10.0.404") }));
    Assert.That(collector.FixtureValues, Is.EqualTo(new[] { new KeyValuePair<string, string>("input", "") }));
    Assert.That(collector.Comparison!.Value.Report, Is.SameAs(second));
    Assert.That(collector.Processes, Is.EqualTo(new[] { new EvidenceProcess("run", "out", "err", 1) }));
  }

  /// <summary>Empty paths, names that are not file names, a label used twice and missing texts are refused.</summary>
  [Test]
  public void InvalidArguments_AreRefused() {
    var collector = new EvidenceCollector();
    collector.AddProcess("run", "", "", 0);
    Assert.That(() => collector.AddSource(""), Throws.ArgumentException);
    Assert.That(() => collector.AddTargetCode(null!), Throws.ArgumentNullException);
    Assert.That(() => collector.AddManifest(""), Throws.ArgumentException);
    Assert.That(() => collector.AddToolVersion("", "1"), Throws.ArgumentException);
    Assert.That(() => collector.AddToolVersion("node", ""), Throws.ArgumentException);
    Assert.That(() => collector.AddFixtureValue("", "x"), Throws.ArgumentException);
    Assert.That(() => collector.AddFixtureValue("x", null!), Throws.ArgumentNullException);
    Assert.That(() => collector.AddProcess("run", "", "", 0), Throws.ArgumentException.With.Message.Contains("already recorded"));
    Assert.That(() => collector.AddProcess("../run", "", "", 0), Throws.ArgumentException.With.Message.Contains("is not a file name"));
    Assert.That(() => collector.AddProcess(".hidden", "", "", 0), Throws.ArgumentException);
    Assert.That(() => collector.AddProcess(null!, "", "", 0), Throws.ArgumentNullException);
    Assert.That(() => collector.AddProcess("x", null!, "", 0), Throws.ArgumentNullException);
    Assert.That(() => collector.AddProcess("x", "", null!, 0), Throws.ArgumentNullException);
    Assert.That(() => collector.AddComparison(null!, new ExactnessPolicy.Exact()), Throws.ArgumentNullException);
    Assert.That(() => collector.AddComparison(EvidenceFixtures.Compare("00", "00"), null!), Throws.ArgumentNullException);
    Assert.That(() => collector.AddOptDiagnostics(null!), Throws.ArgumentNullException);
    Assert.That(() => collector.AddFile("a/b", "x"), Throws.ArgumentException);
    Assert.That(() => collector.AddFile("log", ""), Throws.ArgumentException);
  }

  /// <summary>Steps that run side by side may add at the same time.</summary>
  [Test]
  public void ParallelAdds_AreAllKept() {
    var collector = new EvidenceCollector();
    Parallel.For(0, 200, index => {
      collector.AddSource($"s{index}");
      collector.AddProcess($"p{index}", "", "", index);
      collector.AddToolVersion($"t{index}", "1");
    });
    Assert.That((collector.Sources.Count, collector.Processes.Count, collector.ToolVersions.Count), Is.EqualTo((200, 200, 200)));
    Assert.That(collector.Processes.Select(process => process.ExitCode).Order(), Is.EqualTo(Enumerable.Range(0, 200)));
  }

}
