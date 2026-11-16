using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Optional;

/// <summary>
/// The C# frontend on nullable values and null references: the fixture imported and run by the
/// interpreter against what the C# compiler built of it, and what is refused.
/// </summary>
[TestFixture]
internal sealed class CSharpOptionalImportTests {

  public static IEnumerable<TestCaseData> Calls() => OptionalFixtures.Calls().Select(call => new TestCaseData(call).SetName($"Optional.{call}"));

  [Test]
  public void Fixture_ImportsIntoWellTypedIr() {
    var module = OptionalFixtures.Imported();
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(module.Functions.Single(function => function.Name == "Find").ReturnType, Is.EqualTo(IrType.Optional(IrType.I32)));
  }

  /// <summary>Test design 1, 3, 5, 6, 8: the interpreter computes what .NET computes.</summary>
  [TestCaseSource(nameof(Calls))]
  public void Interpreter_AgreesWithDotNet(PrototypeCall call) {
    var expected = PrototypeFixture.InvokedOutcome(OptionalFixtures.Compiled(call.Method), call);
    Assert.That(PrototypeFixture.InterpretedOutcome(OptionalFixtures.Imported(), call), Is.EqualTo(expected));
  }

  /// <summary>The values the acceptance criteria name, as .NET computes them.</summary>
  [Test]
  public void DotNet_GivesTheValuesOfTheIssue() {
    Assert.That(OptionalFixtures.Compiled("FindOr").Invoke(null, [new[] { 4, 5, 6 }, 6]), Is.EqualTo(2));
    Assert.That(OptionalFixtures.Compiled("FindOr").Invoke(null, [new[] { 4, 5, 6 }, 7]), Is.EqualTo(-1));
    Assert.That(OptionalFixtures.Compiled("Next").Invoke(null, [null]), Is.EqualTo(-1));
    Assert.That(OptionalFixtures.Compiled("Less").Invoke(null, [null]), Is.False);
    Assert.That(OptionalFixtures.Compiled("IsNull").Invoke(null, [null]), Is.True);
    Assert.That(OptionalFixtures.Compiled("Or5").Invoke(null, [0]), Is.EqualTo(0));
    Assert.That(OptionalFixtures.Compiled("Assigned").Invoke(null, []), Is.EqualTo(7));
    Assert.That(OptionalFixtures.Compiled("NaNEqualsItself").Invoke(null, [double.NaN]), Is.False);
  }

  /// <summary>Test design 9: .Value of none is the MissingValue trap.</summary>
  [Test]
  public void ValueOfNone_Traps() {
    var call = new PrototypeCall("ValueOfNone", []);
    var trap = PrototypeFixture.Trap(IrTrap.MissingValue);
    Assert.That(PrototypeFixture.InvokedOutcome(OptionalFixtures.Compiled("ValueOfNone"), call), Is.EqualTo(trap));
    Assert.That(PrototypeFixture.InterpretedOutcome(OptionalFixtures.Imported(), call), Is.EqualTo(trap));
  }

  private static IReadOnlyList<string> Refusals(string body) {
    var result = CSharpFrontend.Import("R.cs", "#nullable enable\npublic static class R {\n" + body + "\n}\n");
    Assert.That(result.Module, Is.Null);
    return [.. result.Diagnostics.Select(diagnostic => diagnostic.Message)];
  }

  /// <summary>A string is a reference of the IR that may be null (#1370): ?? and ?. of strings are what C# computes.</summary>
  [TestCase(null, "x")]
  [TestCase("a", "a")]
  public void NullableString_CoalescesAsCSharpDoes(string? given, string expected) {
    var result = CSharpFrontend.Import("R.cs", "#nullable enable\npublic static class R {\n public static string? F(string? s) => s ?? \"x\";\n public static string G(string[]? a) => a?[0] ?? \"none\";\n}\n");
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrInterpreter.Call(result.Module!, "F", [given!]), Is.EqualTo(expected));
    Assert.That(IrInterpreter.Call(result.Module!, "G", [null!]), Is.EqualTo("none"));
    Assert.That(IrInterpreter.Call(result.Module!, "G", [new[] { "first" }]), Is.EqualTo("first"));
  }

  /// <summary>Test design 10: what stays outside the subset is refused with PLY1002, naming it.</summary>
  [TestCase("public static int F(int? o) => o is int i ? i : 0;", "is int i")]
  [TestCase("public static bool? F(bool? a, bool? b) => a & b;", "BitwiseAndExpression")]
  [TestCase("public static int? F(int? a) { a++; return a; }", "a step of i32?")]
  [TestCase("public static int F(int[]? a) => a?.GetHashCode() ?? 0;", "null-conditional access")]
  [TestCase("public static int F(int? a) => a?.GetHashCode() ?? 0;", "?. on the type i32?")]
  [TestCase("private static int? s; public static int F() => s ??= 1;", "??= of anything but a variable as a value")]
  public void OutOfSubset_IsRefused(string body, string named)
    => Assert.That(Refusals(body), Has.Some.Contains(named), string.Join("\n", Refusals(body)));

}
