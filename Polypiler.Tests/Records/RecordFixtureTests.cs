using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Records;

/// <summary>
/// The C# fixtures of the value records, imported and run by the interpreter, against what the C#
/// compiler built of them: every copy the language makes, field-wise equality and the 128-bit sum.
/// </summary>
[TestFixture]
internal sealed class RecordFixtureTests {

  /// <summary>The calls of <see cref="RecordFixtures.Calls"/> as test cases.</summary>
  public static IEnumerable<TestCaseData> Calls()
    => RecordFixtures.Calls().Select(call => new TestCaseData(call.File, call.Class, call.Call).SetName($"{call.Class}.{call.Call}"));

  /// <summary>Every C# fixture imports into IR the verifier accepts, with its structs declared in the module.</summary>
  [Test]
  public void Fixtures_ImportIntoWellTypedIr() {
    foreach (var (file, className) in RecordFixtures.CSharp) {
      var module = RecordFixtures.Imported(file);
      Assert.That(module.Name, Is.EqualTo(className));
      Assert.That(IrVerifier.Verify(module), Is.Empty, file);
      Assert.That(module.Types, Is.Not.Empty, file);
    }
  }

  /// <summary>The interpreter computes what .NET computes.</summary>
  [TestCaseSource(nameof(Calls))]
  public void Interpreter_AgreesWithDotNet(string file, string className, PrototypeCall call) {
    var expected = PrototypeFixture.InvokedOutcome(RecordFixtures.Compiled(className, call.Method), call);
    Assert.That(PrototypeFixture.InterpretedOutcome(RecordFixtures.Imported(file), call), Is.EqualTo(expected));
  }

  /// <summary>The values the acceptance criteria name, as .NET computes them.</summary>
  [Test]
  public void DotNet_GivesTheValuesOfTheIssue() {
    Assert.That(RecordFixtures.Compiled("Geometry", "Copy").Invoke(null, []), Is.EqualTo(161.0));
    Assert.That(RecordFixtures.Compiled("Geometry", "ArrayCopy").Invoke(null, []), Is.EqualTo(397.0));
    Assert.That(RecordFixtures.Compiled("Ledger", "NaNEqual").Invoke(null, []), Is.True);
  }

}
