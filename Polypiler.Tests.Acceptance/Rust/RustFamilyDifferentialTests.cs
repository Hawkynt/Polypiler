using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Runners;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Infra.Ci;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The fixtures of the prototype's families through the Rust backend (#460, #462): bit operations,
/// value records, strings, enums, numbers as text, classes, dispatch, generics, function values, optional values, views,
/// floating-point views of bytes. Every fixture of a family that the backend writes is
/// built by the pinned toolchain and called on the family's cases with overflow checks on and off;
/// every outcome, the arrays a call was given included, must be what .NET computes of the original
/// fixture. A fixture the backend does not write is refused with a code of the backend that names
/// the construct, and must stay refused only where the list below says so.
/// </summary>
[TestFixture]
[Layer("rust")]
[Category("Slow")]
public sealed class RustFamilyDifferentialTests {

  /// <summary>One fixture of a family: its module, its calls and what .NET computes of each.</summary>
  /// <param name="Name">The fixture, as the test shows it.</param>
  /// <param name="Module">The fixture imported.</param>
  /// <param name="Calls">The calls.</param>
  /// <param name="Expected">What .NET computes of a call.</param>
  /// <param name="Matches">Whether an outcome is the expected one, where the family compares otherwise than by text.</param>
  internal sealed record Family(string Name, Func<IrModule> Module, Func<IReadOnlyList<PrototypeCall>> Calls, Func<PrototypeCall, string> Expected, Func<PrototypeCall, string, string, bool>? Matches = null) {

    /// <inheritdoc />
    public override string ToString() => this.Name;

  }

  /// <summary>The fixtures the backend writes, by family.</summary>
  private static IEnumerable<Family> Families() {
    foreach (var fixture in BitsFixtures.CSharp)
      yield return new($"bits/{fixture}", () => BitsFixtures.Imported(fixture), () => BitsFixtures.Calls(fixture), call => BitsFixtures.CompiledOutcome(fixture, call));

    foreach (var (file, className) in RecordFixtures.CSharp)
      yield return new($"records/{file}", () => RecordFixtures.Imported(file),
        () => [.. RecordFixtures.Calls().Where(call => call.File == file).Select(call => call.Call)],
        call => PrototypeFixture.InvokedOutcome(RecordFixtures.Compiled(className, call.Method), call));

    foreach (var fixture in StringFixtures.CSharp)
      yield return new($"strings/{fixture}", () => PrototypeFixture.Import(fixture + ".cs", System.IO.File.ReadAllText(StringFixtures.PathOf(fixture + ".cs"))),
        () => [.. StringFixtures.CSharpCalls().Where(call => call.Class == fixture).Select(call => call.Call)],
        call => PrototypeFixture.InvokedOutcome(StringFixtures.Compiled(fixture, call.Method), call),
        (_, expected, actual) => StringFixtures.Comparable(expected) == StringFixtures.Comparable(actual));

    foreach (var (className, file) in EnumFixtures.CSharp)
      yield return new($"enums/{file}", () => PrototypeFixture.Import(file + ".cs", System.IO.File.ReadAllText(EnumFixtures.PathOf(file + ".cs"))),
        () => [.. EnumFixtures.CSharpCalls().Where(call => call.Class == className).Select(call => call.Call)],
        call => PrototypeFixture.InvokedOutcome(EnumFixtures.Compiled(className, call.Method), call),
        (_, expected, actual) => EnumFixtures.Normalized(expected) == EnumFixtures.Normalized(actual));

    foreach (var (className, file) in NumberTextFixtures.CSharp)
      yield return new($"numtext/{file}", () => PrototypeFixture.Import(file + ".cs", System.IO.File.ReadAllText(NumberTextFixtures.PathOf(file + ".cs"))),
        () => [.. NumberTextFixtures.CSharpCalls().Where(call => call.Class == className).Select(call => call.Call)],
        call => PrototypeFixture.InvokedOutcome(NumberTextFixtures.Compiled(className, call.Method), call));

    foreach (var (file, className) in ClassFixtures.CSharp)
      yield return new($"classes/{file}", () => ClassFixtures.Imported(file), () => [.. ClassFixtures.Calls().Where(call => call.File == file).Select(call => call.Call)],
        call => PrototypeFixture.InvokedOutcome(ClassFixtures.Compiled(className, call.Method), call));

    foreach (var (file, className) in DispatchFixtures.CSharp)
      yield return new($"dispatch/{file}", () => DispatchFixtures.Imported(file), () => [.. DispatchFixtures.Calls().Where(call => call.File == file).Select(call => call.Call)],
        call => PrototypeFixture.InvokedOutcome(DispatchFixtures.Compiled(className, call.Method), call));

    foreach (var (file, className) in GenericsFixtures.CSharp)
      yield return new($"generics/{file}", () => GenericsFixtures.Imported(file), () => [.. GenericsFixtures.Calls().Where(call => call.File == file).Select(call => call.Call)],
        call => PrototypeFixture.InvokedOutcome(GenericsFixtures.Compiled(className, call.Method), call));

    foreach (var (file, className) in FunctionsFixtures.CSharp)
      yield return new($"functions/{file}", () => FunctionsFixtures.Imported(file), () => [.. FunctionsFixtures.Calls().Where(call => call.File == file).Select(call => call.Call)],
        call => PrototypeFixture.InvokedOutcome(FunctionsFixtures.Compiled(className, call.Method), call));

    yield return new("optional/Find", OptionalFixtures.Imported, () => [.. OptionalFixtures.Calls()],
      call => PrototypeFixture.InvokedOutcome(OptionalFixtures.Compiled(call.Method), call));

    yield return new("spans/Adler32Span", () => SpanFixtures.Imported("Adler32Span"),
      () => [.. new (int Offset, int Length)[] { (5, 9), (0, 20), (20, 0), (19, 2), (-1, 3), (3, -1) }
        .Select(range => new PrototypeCall("Compute", [SpanFixtures.Wikipedia, range.Offset, range.Length]) { Returns = typeof(uint) })],
      call => SpanFixtures.DotNet("Adler32Span", call));

    yield return new("float/FloatView", () => PrototypeFixture.Imported("float/FloatView"),
      () => [.. FloatViewCases.FixtureMethods.SelectMany(method => FloatViewCases.FixtureCalls(method, new Random(2026), 20))],
      call => PrototypeFixture.CompiledOutcome("float/FloatView", call),
      (call, expected, actual) => FloatViewCases.FixtureMatches(call.Method, call, expected, actual));
  }

  /// <summary>The fixtures of the families the backend refuses still, each with the construct its refusal names.</summary>
  private static IEnumerable<TestCaseData> Refusals() {
    yield return new TestCaseData("exceptions/Traps", "'exception handling'");
    yield return new TestCaseData("exceptions/Classes", "'exception handling'");
  }

  [TestCaseSource(nameof(Refusals))]
  public void Fixture_IsRefusedByName(string name, string construct) {
    var module = ExceptionFixtures.Imported(name["exceptions/".Length..]);
    Assert.That(RustCrates.Check(module).Select(problem => problem.Message), Has.Some.Contain(construct));
  }

  private static IEnumerable<TestCaseData> Cases() => Families().Select(family => new TestCaseData(family.Name));

  [TestCaseSource(nameof(Cases))]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task Fixture_AgreesWithDotNet(string name) {
    var family = Families().Single(candidate => candidate.Name == name);
    var module = family.Module();
    Assert.That(RustCrates.Check(module), Is.Empty, "the fixture is written as Rust");
    var calls = family.Calls();
    var expected = calls.Select(family.Expected).ToList();
    var matches = family.Matches ?? ((_, wanted, actual) => wanted == actual);
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap }) {
      var actual = await RustRun.OutcomesAsync(module, calls, checks);
      var mismatches = calls.Select((call, index) => (call, index)).Where(pair => !matches(pair.call, expected[pair.index], actual[pair.index]))
        .Select(pair => $"{pair.call}: C# {expected[pair.index]}, Rust {actual[pair.index]}").ToList();
      Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ with overflow checks {checks}");
    }
  }

}
