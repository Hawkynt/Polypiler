using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Sequences;

/// <summary>
/// The LINQ fixtures (#1329) through the C# frontend and the interpreter: the operations on lazy
/// sequences as the interpreter computes them - independently of System.Linq - and as
/// <see cref="SequenceFusion"/> lowers them to loops compute what .NET computes, call by call, the
/// recorded traces of the lambdas included.
/// </summary>
[TestFixture]
internal sealed class LinqFixtureTests {

  private static IEnumerable<TestCaseData> Calls()
    => LinqFixtures.CSharpFixtures.SelectMany(fixture => LinqFixtures.Calls(fixture).Select(call => new TestCaseData(fixture, call).SetName($"{fixture}.{call}")));

  /// <summary>Test design 2, 4 to 12, 14 and 17: the interpreter computes on the sequence operations what .NET computes.</summary>
  [TestCaseSource(nameof(Calls))]
  public void Interpreter_ComputesWhatDotNetComputes(string fixture, PrototypeCall call)
    => Assert.That(PrototypeFixture.InterpretedOutcome(LinqFixtures.Imported(fixture), call),
      Is.EqualTo(PrototypeFixture.InvokedOutcome(LinqFixtures.Compiled(fixture, call.Method), call)));

  /// <summary>Test design 2 and 4 to 12: the fused loops compute what .NET computes.</summary>
  [TestCaseSource(nameof(Calls))]
  public void FusedModule_ComputesWhatDotNetComputes(string fixture, PrototypeCall call) {
    var fused = SequenceFusion.Run(LinqFixtures.Imported(fixture));
    Assert.That(IrVerifier.Verify(fused), Is.Empty);
    Assert.That(PrototypeFixture.InterpretedOutcome(fused, call), Is.EqualTo(PrototypeFixture.InvokedOutcome(LinqFixtures.Compiled(fixture, call.Method), call)));
  }

}
