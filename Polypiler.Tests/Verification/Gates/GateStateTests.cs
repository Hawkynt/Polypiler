using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Gates;
using Polypiler.Core.Verification.Manifests;

namespace Polypiler.Tests.Verification.Gates;

/// <summary>The states of PRD §23.6, the evidence each takes and the rules from evidence to a fixture's and a pair's state.</summary>
[TestFixture]
public sealed class GateStateTests {

  /// <summary>PRD §23.6 lists the states in this order; the expected list is copied from it, not from the code.</summary>
  private static readonly string[] _prd = ["parsed", "lifted", "recognized", "proved", "legalized", "emitted", "compiled", "executed", "verified", "performance-validated"];

  private static GateState[] States(string list)
    => list.Length == 0 ? [] : [.. list.Split(", ").Select(name => GateStates.TryParse(name, out var state) ? state : throw new ArgumentException(name))];

  [Test]
  public void TheTenStatesOfThePrd_AreEvidenced_InOrder() {
    Assert.That(GateStates.Evidenced.Select(GateStates.Name), Is.EqualTo(_prd));
    Assert.That(GateStates.Evidenced, Is.Ordered);
    Assert.That((int)GateState.Planned, Is.Zero);
    Assert.That(GateStates.Name(GateState.Planned), Is.EqualTo("planned"));
  }

  [Test]
  public void Names_ReadBack_AndNothingElseIsAState() {
    foreach (var state in Enum.GetValues<GateState>()) {
      Assert.That(GateStates.TryParse(GateStates.Name(state), out var read), Is.True);
      Assert.That(read, Is.EqualTo(state));
    }

    foreach (var text in new[] { "certified", "Parsed", "performance_validated", "", null })
      Assert.That(GateStates.TryParse(text, out var none), Is.False, text);
  }

  [TestCase(-1)]
  [TestCase(11)]
  public void Name_OfNoState_Throws(int value) => Assert.That(() => GateStates.Name((GateState)value), Throws.TypeOf<ArgumentOutOfRangeException>());

  /// <summary>Test design 9: each evidence kind against each state, accepted only as the table of the issue says.</summary>
  [Test]
  public void EachKind_IsAcceptedOnlyByTheStatesOfTheTable() {
    var table = new (string State, string Kind)[] {
      ("parsed", "frontend-log"), ("lifted", "ir-dump"), ("recognized", "opt-report"), ("proved", "proof-report"), ("legalized", "ir-dump"),
      ("emitted", "manifest"), ("compiled", "manifest"), ("executed", "manifest"), ("verified", "manifest"), ("performance-validated", "bench-result")
    };
    string[] kinds = ["frontend-log", "ir-dump", "opt-report", "proof-report", "manifest", "bench-result"];
    foreach (var state in GateStates.Evidenced)
      foreach (var kind in kinds)
        Assert.That(GateStates.KindOf(state) == kind, Is.EqualTo(table.Contains((GateStates.Name(state), kind))), $"{state} {kind}");
  }

  [Test]
  public void Planned_TakesNoEvidence() => Assert.That(() => GateStates.KindOf(GateState.Planned), Throws.TypeOf<ArgumentOutOfRangeException>());

  [TestCase(GateState.Emitted, ManifestState.Generated)]
  [TestCase(GateState.Compiled, ManifestState.Built)]
  [TestCase(GateState.Executed, ManifestState.Executed)]
  [TestCase(GateState.Verified, ManifestState.Verified)]
  public void ManifestStates_AreThoseOfTheTable(GateState state, ManifestState manifest) => Assert.That(GateStates.ManifestStateOf(state), Is.EqualTo(manifest));

  [TestCase(GateState.Planned)]
  [TestCase(GateState.Lifted)]
  [TestCase(GateState.PerformanceValidated)]
  public void OtherStates_NeedNoManifest(GateState state) => Assert.That(GateStates.ManifestStateOf(state), Is.Null);

  [Test]
  public void OnlyRecognitionAndProof_AreOptional()
    => Assert.That(GateStates.Evidenced.Where(GateStates.IsOptional), Is.EqualTo(new[] { GateState.Recognized, GateState.Proved }));

  /// <summary>The scenario outline of the feature, and test design 1 to 7; the expected states are worked out by hand from the rule.</summary>
  [TestCase("parsed", "parsed")]
  [TestCase("parsed, lifted", "lifted")]
  [TestCase("parsed, lifted, legalized, emitted, compiled, executed, verified", "verified")]
  [TestCase("parsed, lifted, recognized, proved, legalized", "legalized")]
  [TestCase("parsed, lifted, legalized, emitted, executed", "emitted")]
  [TestCase("parsed, lifted, proved", "lifted")]
  [TestCase("lifted, legalized", "planned")]
  [TestCase("", "planned")]
  [TestCase("parsed, lifted, recognized, proved, legalized, emitted, compiled, executed, verified, performance-validated", "performance-validated")]
  [TestCase("parsed, lifted, legalized, emitted, compiled, executed, verified, performance-validated", "performance-validated")]
  [TestCase("parsed, lifted, legalized, emitted, compiled, executed, performance-validated", "executed")]
  [TestCase("lifted, legalized, emitted, compiled, executed, verified", "planned")]
  [TestCase("parsed, legalized, emitted, compiled, executed, verified", "parsed")]
  [TestCase("parsed, lifted, emitted, compiled, executed, verified", "lifted")]
  [TestCase("parsed, lifted, legalized, compiled, executed, verified", "legalized")]
  [TestCase("parsed, lifted, legalized, emitted, executed, verified", "emitted")]
  [TestCase("parsed, lifted, legalized, emitted, compiled, verified", "compiled")]
  [TestCase("parsed, lifted, recognized", "recognized")]
  [TestCase("parsed, lifted, recognized, proved", "proved")]
  [TestCase("parsed, lifted, proved, legalized", "legalized")]
  [TestCase("verified, executed, compiled, emitted, legalized, lifted, parsed", "verified")]
  public void AFixture_ReachesTheEndOfItsUnbrokenChain(string evidence, string expected)
    => Assert.That(GateStates.Name(GateStates.Reached(States(evidence))), Is.EqualTo(expected));

  [Test]
  public void AFixtureGate_ComputesItsStateFromItsEvidence() {
    var gate = new FixtureGate("f", [new(GateState.Parsed, "frontend-log", "a", ""), new(GateState.Lifted, "ir-dump", "b", "")]);
    Assert.That(gate.State, Is.EqualTo(GateState.Lifted));
  }

  /// <summary>Test design 8: a pair is the minimum of its fixtures, never the maximum.</summary>
  [TestCase("verified", "verified")]
  [TestCase("verified, compiled", "compiled")]
  [TestCase("verified, compiled, lifted", "lifted")]
  [TestCase("", "planned")]
  public void APair_IsTheLowestOfItsFixtures(string fixtures, string expected)
    => Assert.That(GateStates.Name(GateStates.Lowest(States(fixtures))), Is.EqualTo(expected));

  [Test]
  public void Null_IsRefused() {
    Assert.That(() => GateStates.Reached(null!), Throws.ArgumentNullException);
    Assert.That(() => GateStates.Lowest(null!), Throws.ArgumentNullException);
  }

}
