using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.VisualBasic.Net;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Backends.VisualBasic;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// The Visual Basic .NET frontend against the Visual Basic compiler itself, in memory: a fixture
/// compiled by Roslyn and called does what the IR interpreter does with the fixture imported - the
/// value, the arrays it changed, or the exception of the trap - with integer checks on, as a project
/// compiles by default, and removed, as <c>/removeintchecks</c> compiles it.
/// </summary>
[TestFixture]
public sealed class VbNetFrontendTests {

  private static IEnumerable<TestCaseData> Fixtures()
    => from name in VbNetCases.Names from removed in new[] { false, true } select new TestCaseData(name, removed);

  [TestCaseSource(nameof(Fixtures))]
  public void Fixture_ComputesWhatTheCompilerComputes(string name, bool removeIntegerChecks) {
    var source = VbNetCases.Source(name);
    var imported = VbNetFrontend.Import(name + ".vb", source, new VbNetImportOptions { RemoveIntegerChecks = removeIntegerChecks });
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
    var assembly = VbNetFixtures.Load(source, removeIntegerChecks: removeIntegerChecks);
    var mismatches = new List<string>();
    // Without integer checks a counter that steps past Integer.MaxValue wraps and the loop never ends, there as here.
    foreach (var call in VbNetCases.For(name).Where(call => !removeIntegerChecks || call.Method != "PastTheEnd")) {
      var expected = PrototypeFixture.InvokedOutcome(VbNetFixtures.Procedure(assembly, call.Method), call);
      var actual = PrototypeFixture.InterpretedOutcome(imported.Module!, call);
      if (actual != expected)
        mismatches.Add($"{call}: compiled {expected}, interpreted {actual}");
    }

    Assert.That(mismatches, Is.Empty, string.Join("\n", mismatches));
  }

}
