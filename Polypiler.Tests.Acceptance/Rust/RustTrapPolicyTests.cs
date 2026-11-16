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

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The trap policy <c>result</c> of the Rust backend (ADR 0008, #1392, the <c>result</c> parts of #461
/// and #462) on the pinned toolchain: every crate builds with overflow checks on and off, no call
/// panics, every trap is the <c>Err</c> of its trap, and every outcome is what .NET computes - for the
/// scenarios of the issues, for the exception fixtures that catch traps and for every prototype
/// fixture the policy writes. A fixture it does not write is refused by name.
/// </summary>
[TestFixture]
[Layer("rust")]
[Category("Slow")]
public sealed class RustTrapPolicyTests {

  private static readonly RustProfile Result = RustProfile.Default.WithTrapPolicy(RustTrapPolicy.Result);

  private const string CheckedSum = "public static int CheckedSum(int a, int b, int c) => checked(a + b) / c;";

  private static async Task Expect(string methods, IReadOnlyList<PrototypeCall> calls, RustProfile profile) {
    var module = RustCrates.Import(methods);
    var expected = calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)).ToList();
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap })
      Assert.That(await RustRun.OutcomesAsync(module, calls, checks, profile), Is.EqualTo(expected), $"overflow checks {checks}");
  }

  /// <summary>#462 "The result policy turns every trap into an error value" and "The same IR under the panic policy keeps a plain result type".</summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task CheckedSum_IsOkOrTheTrapsError_UnderBothPolicies() {
    PrototypeCall[] calls = [
      new("CheckedSum", [7, 3, 2]) { Returns = typeof(int) },
      new("CheckedSum", [int.MaxValue, 1, 1]) { Returns = typeof(int) },
      new("CheckedSum", [1, 2, 0]) { Returns = typeof(int) }
    ];
    Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(RustCrates.Import(CheckedSum), call)),
      Is.EqualTo(new[] { "5", PrototypeFixture.Trap(IrTrap.IntegerOverflow), PrototypeFixture.Trap(IrTrap.DivideByZero) }));
    await Expect(CheckedSum, calls, Result);
    await Expect(CheckedSum, calls, RustProfile.Default);
  }

  /// <summary>#462 "Error results propagate with the question mark operator": calc(100, 5, 2) is Ok(10), calc(1, 0, 2) the Err of DivideByZero.</summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public Task SafeDivAndCalc_PropagateTheFirstError()
    => Expect("static int Div(int a, int b) => a / b; public static int Calc(int a, int b, int c) { var q = Div(a, b); return Div(q, c); }",
      [new("Calc", [100, 5, 2]) { Returns = typeof(int) }, new("Calc", [1, 0, 2]) { Returns = typeof(int) }, new("Calc", [1, 2, 0]) { Returns = typeof(int) }], Result);

  /// <summary>#1392: a catch of OverflowException around checked(a + b) returns the handler's value.</summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public Task CatchOfAnOverflow_SeesTheTrap()
    => Expect("public static int F(int a, int b) { try { return checked(a + b); } catch (System.OverflowException) { return -1; } }",
      [new("F", [int.MaxValue, 1]) { Returns = typeof(int) }, new("F", [1, 2]) { Returns = typeof(int) }], Result);

  /// <summary>
  /// #462 test design 17: a store before a trap stays visible, nothing after the trap runs - seen by a
  /// handler that reads the array after the trap (-70 - 2: the first element written, the second not).
  /// </summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public Task StoreBeforeATrap_StaysVisible_AndNothingAfterItRuns()
    => Expect("static int F(int[] b, int x) { b[0] = 7; var y = checked(x + 1); b[1] = 9; return y; } "
      + "public static int G(int[] b, int x) { try { return F(b, x); } catch (System.OverflowException) { return -b[0] * 10 - b[1]; } }",
      [new("G", [new int[] { 1, 2 }, int.MaxValue]) { Returns = typeof(int) }, new("G", [new int[] { 1, 2 }, 1]) { Returns = typeof(int) }, new("G", [new int[] { 1 }, 1]) { Returns = typeof(int) }],
      Result);

  /// <summary>#462 test design 8: one function per kind of trap, each the Err of its trap and no panic.</summary>
  [TestCase("public static int F(int a, int b) => checked(a * b);", new object[] { int.MaxValue, 2 })]
  [TestCase("public static int F(int a, int b) => a % b;", new object[] { 5, 0 })]
  [TestCase("public static int F(int a, int b) => a / b;", new object[] { int.MinValue, -1 })]
  [TestCase("public static int F(int[] a, int i) => a[i];", new object[] { new int[] { 1, 2 }, 2 })]
  [TestCase("public static int F(int[] a, int i) => a[i];", new object[] { new int[] { 1, 2 }, -1 })]
  [TestCase("public static void F(int[] a, int i) { a[i] = 1; }", new object[] { new int[] { 1, 2 }, 2 })]
  [TestCase("public static byte F(int a) => checked((byte)a);", new object[] { 256 })]
  [TestCase("public static int F(double x) => checked((int)x);", new object[] { double.NaN })]
  [TestCase("public static int F(int n) => new int[n].Length;", new object[] { -1 })]
  [TestCase("public static int F(int a) => System.Math.Abs(a);", new object[] { int.MinValue })]
  [RequiresToolchain(RustRunner.Toolchain)]
  public Task EveryKindOfTrap_IsItsError(string methods, object[] arguments) {
    var function = RustCrates.Import(methods).Functions.Single(candidate => candidate.Name == "F");
    var returns = function.ReturnType == IrType.Void ? null : function.ReturnType == IrType.U8 ? typeof(byte) : typeof(int);
    return Expect(methods, [new("F", arguments) { Returns = returns }], Result);
  }

  /// <summary>The generated tests assert the Result under result, panic under panic, and both pass; the crates are clean.</summary>
  [TestCase(RustTrapPolicy.Result)]
  [TestCase(RustTrapPolicy.Panic)]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task GeneratedTests_Pass_AndTheCrateIsClean(RustTrapPolicy policy) {
    var options = new RustEmitOptions {
      Tests = [
        new RustTestCase("sums", "CheckedSum", [new IrConst(IrType.I32, 7), new IrConst(IrType.I32, 3), new IrConst(IrType.I32, 2)], new IrConst(IrType.I32, 5)),
        new RustTestCase("overflows", "CheckedSum", [new IrConst(IrType.I32, int.MaxValue), new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 1)], null, IrTrap.IntegerOverflow),
        new RustTestCase("divides_by_zero", "CheckedSum", [new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 2), new IrConst(IrType.I32, 0)], null, IrTrap.DivideByZero)
      ]
    };
    var runner = RustRun.Runner(OverflowChecks.Panic);
    var package = RustCrates.Package(RustCrates.Emit(RustCrates.Import(CheckedSum + " public static int Plain(int a) => a + 1;"), RustProfile.Default.WithTrapPolicy(policy), options));
    var report = await runner.RunTestsAsync(package);
    Assert.That((report.Passed, report.Failed), Is.EqualTo((3, 0)), report.Output);
    Assert.That(await runner.ClippyAsync(package), Is.Empty);
    Assert.That(await runner.FormatCheckAsync(package), Is.Empty);
  }

  /// <summary>Every prototype fixture under result: written and agreeing with .NET with no panic, or refused by name.</summary>
  private static IEnumerable<TestCaseData> Fixtures() => PrototypeFixture.Names.Select(name => new TestCaseData(name));

  [TestCaseSource(nameof(Fixtures))]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task PrototypeFixture_UnderResult_AgreesWithDotNet_OrIsRefusedByName(string fixture) {
    var module = PrototypeFixture.Imported(fixture);
    var problems = RustCrates.Check(module, Result);
    if (problems.Count > 0) {
      Assert.That(problems.Select(problem => problem.Code), Has.All.AnyOf(RustEmitter.TrapPolicyLimit, RustEmitter.Inexpressible, RustEmitter.ExceptionRegion, RustEmitter.MayAlias));
      Assert.That(problems.Select(problem => problem.Message), Has.All.Match("'[^']+'"));
      return;
    }

    var calls = PrototypeCases.For(fixture);
    var expected = calls.Select(call => PrototypeFixture.CompiledOutcome(fixture, call)).ToList();
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap }) {
      var actual = await RustRun.OutcomesAsync(module, calls, checks, Result);
      var mismatches = calls.Select((call, index) => (call, index)).Where(pair => actual[pair.index] != expected[pair.index])
        .Select(pair => $"{pair.call}: C# {expected[pair.index]}, Rust {actual[pair.index]}").ToList();
      Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ with overflow checks {checks}");
    }
  }

  /// <summary>The exceptions family under result: the traps caught by the clauses of their classes, the module's own exceptions as before.</summary>
  [TestCase("Traps", "TrapsDemo")]
  [TestCase("Classes", "ClassesDemo")]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task ExceptionFixture_UnderResult_AgreesWithDotNet(string file, string className) {
    var module = ExceptionFixtures.Imported(file);
    Assert.That(RustCrates.Check(module, Result), Is.Empty);
    var calls = ExceptionFixtures.Calls().Where(call => call.File == file).Select(call => call.Call).ToList();
    Assert.That(calls, Is.Not.Empty);
    var expected = calls.Select(call => PrototypeFixture.InvokedOutcome(ExceptionFixtures.Compiled(className, call.Method), call)).ToList();
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap })
      Assert.That(await RustRun.OutcomesAsync(module, calls, checks, Result), Is.EqualTo(expected), $"overflow checks {checks}");
  }

}
