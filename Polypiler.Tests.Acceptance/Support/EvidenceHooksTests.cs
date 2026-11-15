using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Evidence;
using EvidenceIdentity = Polypiler.Core.Verification.Evidence.ScenarioIdentity;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary><see cref="EvidenceObserver"/>: every scenario, passed or failed, leaves a bundle; an invalid one fails the scenario.</summary>
[TestFixture]
public sealed class EvidenceHooksTests {

  private string _root = "";

  [SetUp]
  public void CreateRoot() => this._root = Path.Combine(Path.GetTempPath(), "polypiler-evidence-hooks-" + Guid.NewGuid().ToString("N"));

  [TearDown]
  public void DeleteRoot() {
    if (Directory.Exists(this._root))
      Directory.Delete(this._root, true);
  }

  private static JsonObject Evidence(string bundle) => JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, "evidence.json")))!.AsObject();

  /// <summary>The observer adds the runtime's and Polypiler's versions and every command line, so an untagged scenario is complete.</summary>
  [Test]
  public void PassedScenario_LeavesAValidBundleWithItsCommands() {
    var observer = new EvidenceObserver(new EvidenceCollector(), () => new EvidenceIdentity("Feature", "Passed", []), this._root);
    observer.Completed(new ScenarioRecord("feature", "passed", "f.feature", true, [new(0, "out", ""), new(2, "", "err")], this._root));
    var evidence = Evidence(observer.Bundle!);
    Assert.That(evidence["outcome"]!.GetValue<string>(), Is.EqualTo("passed"));
    Assert.That(evidence["toolVersions"]!.AsObject().Select(pair => pair.Key), Is.EqualTo(new[] { "dotnet-runtime", "polypiler" }));
    Assert.That(evidence["toolVersions"]!["polypiler"]!.GetValue<string>(), Is.EqualTo(ProductInfo.Version));
    Assert.That(evidence["processes"]!.AsArray().Select(process => (process!["label"]!.GetValue<string>(), process["exitCode"]!.GetValue<int>())),
      Is.EqualTo(new[] { ("command-1", 0), ("command-2", 2) }));
    Assert.That(File.ReadAllText(Path.Combine(observer.Bundle!, "process", "command-2.stderr.txt")), Is.EqualTo("err"));
    var bag = new DiagnosticBag();
    Assert.That(EvidenceValidator.Validate(observer.Bundle!, bag), Is.True);
  }

  /// <summary>Test design 4: a failed scenario leaves its bundle with outcome <c>failed</c>.</summary>
  [Test]
  public void FailedScenario_LeavesItsBundle() {
    var observer = new EvidenceObserver(new EvidenceCollector(), () => new EvidenceIdentity("Feature", "Failed", []), this._root);
    observer.Completed(new ScenarioRecord("feature", "failed", "f.feature", false, [], this._root));
    Assert.That(Evidence(observer.Bundle!)["outcome"]!.GetValue<string>(), Is.EqualTo("failed"));
  }

  /// <summary>Test design 15: a source deleted before the scenario ends fails the scenario with PLY0060, after the bundle was written.</summary>
  [Test]
  public void SourceGone_FailsTheScenario() {
    var collector = new EvidenceCollector();
    var source = Path.Combine(this._root, "source.txt");
    Directory.CreateDirectory(this._root);
    File.WriteAllText(source, "abc");
    collector.AddSource(source);
    File.Delete(source);
    var observer = new EvidenceObserver(collector, () => new EvidenceIdentity("Feature", "Gone", ["@translation"]), this._root);
    var workspace = ScenarioWorkspace.Create();
    var exception = Assert.Throws<AggregateException>(() => ScenarioHooks.Complete(new ScenarioRecord("feature", "gone", "f.feature", true, [], workspace.Path), [observer], workspace))!;
    Assert.That(exception.InnerExceptions.Single().Message, Does.StartWith("PLY0060: evidence bundle '").And.Contain("is incomplete: missing source hash"));
    Assert.That(File.Exists(Path.Combine(observer.Bundle!, "evidence.json")), Is.True);
  }

}
