using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Go;
using Polypiler.Tests.Frontends.Go.Support;

namespace Polypiler.Tests.Frontends.Go;

/// <summary>
/// The Go frontend's own parser and type checker on the cases of <c>tests/fixtures/go/checker</c>: one
/// file per syntax and type error it reports, valid files over every construct it models with the
/// exact constants go/types gives them, and the constructs it refuses by name. The Go layer runs the
/// same files through go/parser and go/types and checks that Go decides them alike.
/// </summary>
[TestFixture]
public sealed class GoCheckerTests {

  private static IEnumerable<TestCaseData> Cases() => GoCheckerCases.All.Select(@case => new TestCaseData(@case.Name).SetName($"Case({@case.Name})"));

  [TestCaseSource(nameof(Cases))]
  public void Case_IsDecidedAsItStates(string name) {
    var @case = GoCheckerCases.All.Single(candidate => candidate.Name == name);
    var result = GoFrontend.Check(@case.Name + ".go", @case.Source);
    var message = result.Diagnostics.Select(diagnostic => diagnostic.ToString()).FirstOrDefault() ?? "";
    var verdict = @case.Want switch {
      "valid" => GoVerdict.Valid,
      "unsupported" => GoVerdict.Unsupported,
      _ => GoVerdict.Invalid
    };
    Assert.That(result.Verdict, Is.EqualTo(verdict), message);
    Assert.That(result.Parsed, Is.EqualTo(@case.Want != "syntax"), message);
    Assert.That(message, Does.Contain(@case.Message));
    if (verdict == GoVerdict.Invalid)
      Assert.That(message, Does.Contain("PLY2100").And.Contain(@case.Want == "syntax" ? "Go syntax error: " : "Go type error: "));

    foreach (var stated in @case.Constants)
      Assert.That(result.Constants, Has.One.EqualTo(new GoConstantValue(stated.Name, stated.Type, stated.Value)), stated.Name);
  }

  [Test]
  public void Cases_CoverEveryVerdict() {
    var wants = GoCheckerCases.All.GroupBy(@case => @case.Want).ToDictionary(group => group.Key, group => group.Count());
    Assert.That(wants.Keys, Is.EquivalentTo(new[] { "valid", "invalid", "syntax", "unsupported" }));
    Assert.That(GoCheckerCases.All.Select(@case => @case.Name).Distinct().Count(), Is.EqualTo(GoCheckerCases.All.Count), "every case has a name of its own");
  }

}
