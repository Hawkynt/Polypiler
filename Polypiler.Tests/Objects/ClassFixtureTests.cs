using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Objects;

/// <summary>
/// The C# fixtures of the classes, imported and run by the interpreter, against what the C# compiler
/// built of them: identity, aliasing, record equality and the traps of <c>null</c>.
/// </summary>
[TestFixture]
internal sealed class ClassFixtureTests {

  /// <summary>The calls of <see cref="ClassFixtures.Calls"/> as test cases.</summary>
  public static IEnumerable<TestCaseData> Calls()
    => ClassFixtures.Calls().Select(call => new TestCaseData(call.File, call.Class, call.Call).SetName($"{call.Class}.{call.Call}"));

  /// <summary>Every C# fixture imports into IR the verifier accepts, with its classes declared in the module.</summary>
  [Test]
  public void Fixtures_ImportIntoWellTypedIr() {
    foreach (var (file, className) in ClassFixtures.CSharp) {
      var module = ClassFixtures.Imported(file);
      Assert.That(module.Name, Is.EqualTo(className));
      Assert.That(IrVerifier.Verify(module), Is.Empty, file);
      Assert.That(module.Types.OfType<IrClassType>(), Is.Not.Empty, file);
    }
  }

  /// <summary>The interpreter computes what .NET computes.</summary>
  [TestCaseSource(nameof(Calls))]
  public void Interpreter_AgreesWithDotNet(string file, string className, PrototypeCall call) {
    var expected = PrototypeFixture.InvokedOutcome(ClassFixtures.Compiled(className, call.Method), call);
    Assert.That(PrototypeFixture.InterpretedOutcome(ClassFixtures.Imported(file), call), Is.EqualTo(expected));
  }

  /// <summary>Test design 9: every member reached through null throws NullReferenceException in .NET and traps in the interpreter.</summary>
  [TestCase("CallOnNull")]
  [TestCase("ReadOnNull")]
  [TestCase("WriteOnNull", 3)]
  [TestCase("PropertyOnNull")]
  public void NullReceiver_TrapsAsDotNetThrows(string method, params object[] arguments) {
    var call = new PrototypeCall(method, arguments);
    var trap = PrototypeFixture.Trap(IrTrap.NullReference);
    Assert.That(PrototypeFixture.InvokedOutcome(ClassFixtures.Compiled("NullDemo", method), call), Is.EqualTo(trap));
    Assert.That(PrototypeFixture.InterpretedOutcome(ClassFixtures.Imported("null"), call), Is.EqualTo(trap));
  }

  /// <summary>A static field is shared by every object of its class: two objects made, one stepped and one reset.</summary>
  [Test]
  public void StaticField_CountsTheObjectsMade()
    => Assert.That(IrInterpreter.Call(ClassFixtures.Imported("Counter"), "MadeByBoth", []), Is.EqualTo(3));

  /// <summary>The values the acceptance criteria name, as .NET computes them.</summary>
  [Test]
  public void DotNet_GivesTheValuesOfTheIssue() {
    Assert.That(ClassFixtures.Compiled("CounterDemo", "Run").Invoke(null, [10, 3]), Is.EqualTo(16));
    Assert.That(ClassFixtures.Compiled("CounterDemo", "Aliased").Invoke(null, []), Is.EqualTo(1));
    Assert.That(ClassFixtures.Compiled("CounterDemo", "TwoObjects").Invoke(null, []), Is.False);
    Assert.That(ClassFixtures.Compiled("RangeDemo", "ValueEqual").Invoke(null, [2, 9]), Is.True);
    Assert.That(ClassFixtures.Compiled("RangeDemo", "SameReference").Invoke(null, [2, 9]), Is.False);
    Assert.That(ClassFixtures.Compiled("RangeDemo", "Length").Invoke(null, [2, 9]), Is.EqualTo(7));
  }

}
