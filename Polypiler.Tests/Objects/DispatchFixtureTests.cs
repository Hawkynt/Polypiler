using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Objects;

/// <summary>
/// The C# fixtures of the hierarchies, imported and run by the interpreter, against what the C#
/// compiler built of them: dispatch, base calls, type tests and the cast that traps.
/// </summary>
[TestFixture]
internal sealed class DispatchFixtureTests {

  /// <summary>The calls of <see cref="DispatchFixtures.Calls"/> as test cases.</summary>
  public static IEnumerable<TestCaseData> Calls()
    => DispatchFixtures.Calls().Select(call => new TestCaseData(call.File, call.Class, call.Call).SetName($"{call.Class}.{call.Call}"));

  /// <summary>Every C# fixture imports into IR the verifier accepts, with its hierarchy.</summary>
  [Test]
  public void Fixtures_ImportIntoWellTypedIr() {
    foreach (var (file, className) in DispatchFixtures.CSharp) {
      var module = DispatchFixtures.Imported(file);
      Assert.That(module.Name, Is.EqualTo(className));
      Assert.That(IrVerifier.Verify(module), Is.Empty, file);
      Assert.That(module.Types.OfType<IrClassType>().Where(type => type.Base is not null), Is.Not.Empty, file);
    }
  }

  /// <summary>The interpreter computes what .NET computes.</summary>
  [TestCaseSource(nameof(Calls))]
  public void Interpreter_AgreesWithDotNet(string file, string className, PrototypeCall call) {
    var expected = PrototypeFixture.InvokedOutcome(DispatchFixtures.Compiled(className, call.Method), call);
    Assert.That(PrototypeFixture.InterpretedOutcome(DispatchFixtures.Imported(file), call), Is.EqualTo(expected));
  }

  /// <summary>The values the acceptance criteria name, as .NET computes them.</summary>
  [Test]
  public void DotNet_GivesTheValuesOfTheIssue() {
    Assert.That(DispatchFixtures.Compiled("ShapesDemo", "SumTwice").Invoke(null, [2.0, 3.0, 1.0]), Is.EqualTo(18.0));
    Assert.That(DispatchFixtures.Compiled("ShapesDemo", "SumSides").Invoke(null, [2.0, 3.0, 1.0]), Is.EqualTo(4));
    Assert.That(DispatchFixtures.Compiled("AnimalsDemo", "DogDescribe").Invoke(null, []), Is.EqualTo(42));
    Assert.That(DispatchFixtures.Compiled("AnimalsDemo", "AnimalDescribe").Invoke(null, []), Is.EqualTo(1));
    Assert.That(DispatchFixtures.Compiled("CtorVirtualDemo", "Seen").Invoke(null, []), Is.EqualTo(42));
    Assert.That(PrototypeFixture.InvokedOutcome(DispatchFixtures.Compiled("ShapesDemo", "CastToRect"), new PrototypeCall("CastToRect", [1.0])),
      Is.EqualTo(PrototypeFixture.Trap(IrTrap.InvalidCast)));
  }

  /// <summary>A virtual call from a base constructor is refused with the construct named.</summary>
  [Test]
  public void VirtualCallDuringConstruction_IsRefused() {
    var result = CSharpFrontend.Import("CtorVirtual.cs", DispatchFixtures.SourceOf("CtorVirtual.cs"));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Line)), Is.EqualTo(new[] { ("PLY1002", 11) }));
    Assert.That(result.Diagnostics[0].Message, Does.Contain("a virtual call on 'this' during construction"));
  }

}
