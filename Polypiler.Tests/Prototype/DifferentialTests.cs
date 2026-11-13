using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Prototype;

/// <summary>
/// Compares the interpreter with the fixtures as the C# compiler built them. The same calls run
/// through the generated JavaScript in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class DifferentialTests {

  private static readonly Dictionary<string, IrModule> _modules = PrototypeFixture.Names.ToDictionary(
    name => name,
    name => PrototypeFixture.Import(name + ".cs", PrototypeFixture.SourceOf(name)));

  /// <summary>Test design 1 to 10 and 16: every call has the same outcome in the interpreter and in C#.</summary>
  [TestCaseSource(typeof(PrototypeFixture), nameof(PrototypeFixture.Names))]
  public void Interpreter_AgreesWithTheCompiledFixture(string fixture) {
    var calls = PrototypeCases.For(fixture);
    var mismatches = calls
      .Select(call => (Call: call, Expected: PrototypeFixture.CompiledOutcome(fixture, call), Actual: PrototypeFixture.InterpretedOutcome(_modules[fixture], call)))
      .Where(run => run.Expected != run.Actual)
      .Select(run => $"{run.Call}: C# {run.Expected}, interpreter {run.Actual}")
      .ToList();

    Assert.That(calls, Is.Not.Empty);
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

  /// <summary>Test design 1: the published check values, in C# and in the interpreter.</summary>
  [TestCase("Adler32", "Compute", "Wikipedia", 0x11E60398u)]
  [TestCase("Crc32", "Compute", "123456789", 0xCBF43926u)]
  [TestCase("Fnv1a", "Hash", "", 0x811C9DC5u)]
  [TestCase("Fnv1a", "Hash", "a", 0xE40C292Cu)]
  public void Checksum_MatchesItsPublishedValue(string fixture, string method, string text, uint published) {
    var call = new PrototypeCall(method, [PrototypeCases.Text(text)]);
    var expected = PrototypeFixture.Outcome(published, call.Arguments);
    Assert.That(PrototypeFixture.CompiledOutcome(fixture, call), Is.EqualTo(expected));
    Assert.That(PrototypeFixture.InterpretedOutcome(_modules[fixture], call), Is.EqualTo(expected));
  }

  /// <summary>The values of the feature file that are not checksums.</summary>
  [TestCase("Gcd", new object[] { 1071, 462 }, "21")]
  [TestCase("Fibonacci", new object[] { 48 }, "512559680")]
  [TestCase("RotateLeft", new object[] { 0x80000001u, 1 }, "3")]
  public void Numbers_MatchTheFeatureFile(string method, object[] arguments, string expected) {
    var call = new PrototypeCall(method, arguments);
    Assert.That(PrototypeFixture.CompiledOutcome("Numbers", call), Is.EqualTo(expected));
    Assert.That(PrototypeFixture.InterpretedOutcome(_modules["Numbers"], call), Is.EqualTo(expected));
  }

  /// <summary>Scenario: An array is modified in place.</summary>
  [Test]
  public void Reverse_ChangesTheCallersArray() {
    var data = new[] { 1, 2, 3, 4, 5 };
    Assert.That(IrInterpreter.Call(_modules["Arrays"], "Reverse", [data]), Is.Null);
    Assert.That(data, Is.EqualTo(new[] { 5, 4, 3, 2, 1 }));
  }

  /// <summary>
  /// Test design 6 and 10: what division does at its edges, recorded from the compiled fixture. On
  /// .NET 10 the smallest <c>int</c> divided by -1 throws <see cref="OverflowException"/>, for the
  /// remainder as well; the IR makes that a trap rather than a wrapped value.
  /// </summary>
  [TestCase("DivideInt", 1, 0, IrTrap.DivideByZero)]
  [TestCase("RemainderInt", 1, 0, IrTrap.DivideByZero)]
  [TestCase("DivideInt", int.MinValue, -1, IrTrap.IntegerOverflow)]
  [TestCase("RemainderInt", int.MinValue, -1, IrTrap.IntegerOverflow)]
  [TestCase("DivideAssignInt", int.MinValue, -1, IrTrap.IntegerOverflow)]
  public void Division_TrapsWhereTheCompiledFixtureThrows(string method, int dividend, int divisor, IrTrap trap) {
    var call = new PrototypeCall(method, [dividend, divisor]);
    Assert.That(PrototypeFixture.CompiledOutcome("Operators", call), Is.EqualTo(PrototypeFixture.Trap(trap)));
    Assert.That(
      () => IrInterpreter.Call(_modules["Operators"], method, call.FreshArguments()),
      Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(trap));
  }

  /// <summary>Test design 16: the random part is as large as the issue asks, and reproducible.</summary>
  [Test]
  public void OperatorCalls_AreAThousandRandomOnesPerFunctionAndReproducible() {
    var calls = PrototypeCases.For("Operators");
    var perMethod = calls.GroupBy(call => call.Method).ToList();
    Assert.That(perMethod, Has.Count.GreaterThan(100));
    Assert.That(perMethod.Select(group => group.Count()), Has.All.GreaterThanOrEqualTo(PrototypeCases.RandomCallsPerOperator));
    Assert.That(PrototypeCases.For("Operators").Select(call => call.ToString()), Is.EqualTo(calls.Select(call => call.ToString())));
    Assert.That(() => PrototypeCases.For("Missing"), Throws.ArgumentException);
  }

  /// <summary>The comparison cannot pass by accident: an outcome names the result and every array.</summary>
  [Test]
  public void Outcome_ShowsResultAndArrays() {
    Assert.That(PrototypeFixture.Outcome(null, [new[] { 1, -2 }, 7, new byte[] { 255 }]), Is.EqualTo("void | [1,-2] | [255]"));
    Assert.That(PrototypeFixture.Outcome(true, []), Is.EqualTo("true"));
    Assert.That(PrototypeFixture.Outcome(uint.MaxValue, []), Is.EqualTo("4294967295"));
    Assert.That(() => PrototypeFixture.Compiled("Numbers", "Missing"), Throws.TypeOf<MissingMethodException>());
    Assert.That(() => PrototypeFixture.Import("x.cs", "class"), Throws.InvalidOperationException);
  }

}
