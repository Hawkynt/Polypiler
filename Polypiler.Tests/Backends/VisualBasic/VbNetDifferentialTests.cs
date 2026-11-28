using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.VisualBasic;

/// <summary>
/// The prototype fixtures through the Visual Basic .NET backend, compiled by the Visual Basic compiler
/// in memory with the options of the generated project, against the IR interpreter: every call of
/// <see cref="PrototypeCases"/> returns what the interpreter returns, the arrays it changes included,
/// or throws the exception of the trap the interpreter raises.
/// </summary>
[TestFixture]
public sealed class VbNetDifferentialTests {

  private static IEnumerable<TestCaseData> Fixtures() => from fixture in PrototypeFixture.Names from removed in new[] { true, false } select new TestCaseData(fixture, removed);

  /// <summary>Every fixture, in a project with <c>RemoveIntegerChecks</c> and in one with integer checks on.</summary>
  [TestCaseSource(nameof(Fixtures))]
  public void PrototypeFixture_ComputesWhatTheInterpreterComputes(string fixture, bool removeIntegerChecks) {
    var imported = PrototypeFixture.Imported(fixture);
    var translation = VbNetFixtures.Translate(imported, VisualBasicProfile.VbNetLatest with { RemoveIntegerChecks = removeIntegerChecks });
    Assert.That(translation.Problems, Is.Empty, string.Join("\n", translation.Problems));
    Assert.That(translation.Project, Does.Contain($"<RemoveIntegerChecks>{(removeIntegerChecks ? "true" : "false")}</RemoveIntegerChecks>"));
    var assembly = VbNetFixtures.Load(translation.Source!, removeIntegerChecks: removeIntegerChecks);
    // The IR has no character type: a char of C# is a UShort in Visual Basic, a code unit as in the IR.
    var calls = PrototypeFixture.Typed(fixture, PrototypeCases.For(fixture)).Select(call => call with { Arguments = [.. call.Arguments.Select(Unit)] });
    var mismatches = new List<string>();
    foreach (var call in calls) {
      var expected = PrototypeFixture.InterpretedOutcome(imported, call);
      var actual = PrototypeFixture.InvokedOutcome(VbNetFixtures.Procedure(assembly, call.Method), call);
      if (actual != expected)
        mismatches.Add($"{call}: expected {expected}, actual {actual}");
    }

    Assert.That(mismatches, Is.Empty, string.Join("\n", mismatches.Take(20)) + "\n" + translation.Source);
  }

  private static object Unit(object argument) => argument switch {
    char unit => (ushort)unit,
    char[] units => units.Select(unit => (ushort)unit).ToArray(),
    _ => argument
  };

  private static IEnumerable<TestCaseData> Profiles()
    => from profile in VisualBasicProfile.NetAll from fixture in PrototypeFixture.First select new TestCaseData(fixture, profile.Name);

  /// <summary>Every language version compiles every first fixture without a warning.</summary>
  [TestCaseSource(nameof(Profiles))]
  public void PrototypeFixture_CompilesInEveryLanguageVersion(string fixture, string name) {
    var profile = VisualBasicProfile.Parse(name);
    var translation = VbNetFixtures.Translate(PrototypeFixture.Imported(fixture), profile);
    Assert.That(translation.Problems, Is.Empty);
    Assert.That(VbNetFixtures.Complaints(translation.Source!, profile), Is.Empty, translation.Source);
  }

}
