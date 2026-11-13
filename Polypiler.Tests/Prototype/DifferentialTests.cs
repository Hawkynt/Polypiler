using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
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
    PrototypeFixture.Imported);

  /// <summary>
  /// The fixtures as test cases. The two whose calls are generated for every function - boundary
  /// combinations and a thousand random operand tuples each - take seconds and are in the slow tier.
  /// </summary>
  private static IEnumerable<TestCaseData> Fixtures() => PrototypeFixture.Names.Select(name => {
    var data = new TestCaseData(name);
    return name is "wide/OperatorsWide" or "wide/Conversions" ? data.SetCategory("Slow") : data;
  });

  /// <summary>Test design 1 to 10 and 16: every call has the same outcome in the interpreter and in C#.</summary>
  [TestCaseSource(nameof(Fixtures))]
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
  [TestCase("wide/Fnv1a64", "Hash", "a", 0xAF63DC4C8601EC8CUL)]
  [TestCase("wide/Fnv1a64", "Hash", "", 0xCBF29CE484222325UL)]
  [TestCase("wide/Crc16", "Compute", "123456789", 0x29B1u)]
  [TestCase("wide/Fletcher16", "Compute", "abcde", 0xC8F0u)]
  public void Checksum_MatchesItsPublishedValue(string fixture, string method, string text, ulong published) {
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

  /// <summary>The published first output of SplitMix64 seeded with 0, and the wrap-around of the feature file.</summary>
  [TestCase("wide/SplitMix64", "First", 0UL, "16294208416658607535")]
  [TestCase("wide/OperatorsWide", "UnsignedConstant", ulong.MaxValue, "18446742974197923406")]
  public void WideNumbers_MatchTheirPublishedValue(string fixture, string method, ulong argument, string expected) {
    var call = new PrototypeCall(method, [argument]);
    Assert.That(PrototypeFixture.CompiledOutcome(fixture, call), Is.EqualTo(expected));
    Assert.That(PrototypeFixture.InterpretedOutcome(_modules[fixture], call), Is.EqualTo(expected));
  }

  private static readonly (string Keyword, string Name, Type Type)[] _numeric = [
    ("sbyte", "SByte", typeof(sbyte)), ("byte", "Byte", typeof(byte)), ("short", "Short", typeof(short)),
    ("ushort", "UShort", typeof(ushort)), ("char", "Char", typeof(char)), ("int", "Int", typeof(int)),
    ("uint", "UInt", typeof(uint)), ("long", "Long", typeof(long)), ("ulong", "ULong", typeof(ulong))
  ];

  private static IEnumerable<TestCaseData> Promotions()
    => from left in _numeric from right in _numeric select new TestCaseData(left.Keyword, right.Keyword).SetName($"Promotion: {left.Keyword} + {right.Keyword}");

  /// <summary>
  /// Test design 4: for every pair of numeric types, the type and the value of <c>a + b</c> are the ones
  /// the C# compiler chose, read off the boxed result of <see cref="PromotionOracle"/>. A pair the
  /// compiler rejects is refused with its error.
  /// </summary>
  [TestCaseSource(nameof(Promotions))]
  public void Promotion_FollowsTheCompiler(string left, string right) {
    var a = _numeric.Single(type => type.Keyword == left);
    var b = _numeric.Single(type => type.Keyword == right);
    var oracle = typeof(PromotionOracle).GetMethod(a.Name + b.Name, BindingFlags.Public | BindingFlags.Static);
    if (oracle is null) {
      var refused = CSharpFrontend.Import("Snippet.cs", PrototypeFixture.Wrap($"public static long F({left} a, {right} b) => a + b;"));
      Assert.That(refused.Module, Is.Null);
      Assert.That(refused.Diagnostics.Select(diagnostic => diagnostic.Code), Has.All.EqualTo("PLY1000"));
      Assert.That(refused.Diagnostics[0].Message, Does.StartWith("CS0034: "));
      return;
    }

    var result = oracle.Invoke(null, [PrototypeCases.Edges(a.Type)[0], PrototypeCases.Edges(b.Type)[0]])!.GetType();
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(
      $"public static {_numeric.Single(type => type.Type == result).Keyword} F({left} a, {right} b) => a + b;"));
    foreach (var x in PrototypeCases.Edges(a.Type))
      foreach (var y in PrototypeCases.Edges(b.Type)) {
        var expected = oracle.Invoke(null, [x, y])!;
        var actual = IrInterpreter.Call(module, "F", [x, y])!;
        Assert.That((actual.GetType(), PrototypeFixture.Show(actual)), Is.EqualTo((expected.GetType(), PrototypeFixture.Show(expected))), $"{x} + {y}");
      }
  }

  /// <summary>
  /// Test design 13 and 14: a checked operation, a checked cast and a 64-bit division trap exactly
  /// where the compiled fixture throws.
  /// </summary>
  [TestCase("wide/OperatorsWide", "AddCheckedInt", new object[] { int.MaxValue, 1 }, IrTrap.IntegerOverflow)]
  [TestCase("wide/OperatorsWide", "SubtractCheckedUInt", new object[] { 0u, 1u }, IrTrap.IntegerOverflow)]
  [TestCase("wide/OperatorsWide", "MultiplyCheckedLong", new object[] { long.MinValue, -1L }, IrTrap.IntegerOverflow)]
  [TestCase("wide/OperatorsWide", "MultiplyCheckedULong", new object[] { ulong.MaxValue, 2UL }, IrTrap.IntegerOverflow)]
  [TestCase("wide/OperatorsWide", "NegateCheckedInt", new object[] { int.MinValue }, IrTrap.IntegerOverflow)]
  [TestCase("wide/OperatorsWide", "NegateCheckedLong", new object[] { long.MinValue }, IrTrap.IntegerOverflow)]
  [TestCase("wide/OperatorsWide", "IncrementCheckedByte", new object[] { (byte)254 }, IrTrap.IntegerOverflow)]
  [TestCase("wide/OperatorsWide", "AddAssignCheckedSByte", new object[] { (sbyte)127, (sbyte)1 }, IrTrap.IntegerOverflow)]
  [TestCase("wide/OperatorsWide", "DivideLong", new object[] { long.MinValue, -1L }, IrTrap.IntegerOverflow)]
  [TestCase("wide/OperatorsWide", "RemainderLong", new object[] { long.MinValue, -1L }, IrTrap.IntegerOverflow)]
  [TestCase("wide/OperatorsWide", "DivideLong", new object[] { 1L, 0L }, IrTrap.DivideByZero)]
  [TestCase("wide/OperatorsWide", "RemainderULong", new object[] { 1UL, 0UL }, IrTrap.DivideByZero)]
  [TestCase("wide/Conversions", "IntToByteChecked", new object[] { 256 }, IrTrap.IntegerOverflow)]
  [TestCase("wide/Conversions", "LongToIntChecked", new object[] { 2147483648L }, IrTrap.IntegerOverflow)]
  [TestCase("wide/Conversions", "IntToULongChecked", new object[] { -1 }, IrTrap.IntegerOverflow)]
  [TestCase("wide/Conversions", "ULongToLongChecked", new object[] { ulong.MaxValue }, IrTrap.IntegerOverflow)]
  public void WideOperation_TrapsWhereTheCompiledFixtureThrows(string fixture, string method, object[] arguments, IrTrap trap) {
    var call = new PrototypeCall(method, arguments);
    Assert.That(PrototypeFixture.CompiledOutcome(fixture, call), Is.EqualTo(PrototypeFixture.Trap(trap)));
    Assert.That(PrototypeFixture.InterpretedOutcome(_modules[fixture], call), Is.EqualTo(PrototypeFixture.Trap(trap)));
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
    Assert.That(PrototypeFixture.Outcome('A', [new[] { 'B' }, new[] { ulong.MaxValue }]), Is.EqualTo("65 | [66] | [18446744073709551615]"));
    Assert.That(PrototypeFixture.ClassOf("wide/Crc16"), Is.EqualTo("Crc16"));
    Assert.That(() => PrototypeFixture.Compiled("Numbers", "Missing"), Throws.TypeOf<MissingMethodException>());
    Assert.That(() => PrototypeFixture.Import("x.cs", "class"), Throws.InvalidOperationException);
  }

}
