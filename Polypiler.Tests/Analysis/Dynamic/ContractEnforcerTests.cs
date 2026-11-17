using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Analysis.Dynamic;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Analysis.Dynamic;

/// <summary>
/// <see cref="ContractEnforcer"/> on modules the C# frontend imports, run by the IR interpreter as the
/// oracle (issue #777, test design 1, 2, 8, 9, 11-18).
/// </summary>
[TestFixture]
public sealed class ContractEnforcerTests {

  private static IrModule Import(string methods) {
    var result = CSharpFrontend.Import("Snippet.cs", PrototypeFixture.Wrap(methods));
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    return result.Module!;
  }

  private static (ContractEnforcement Enforcement, DiagnosticBag Diagnostics) Enforce(IrModule module, string language, params string[] contracts) {
    var bag = new DiagnosticBag();
    var assumptions = contracts.Select(text => text.StartsWith('~')
      ? Parsed(text[1..], false, ContractParser.Unchecked)
      : Parsed(text, true, ContractParser.Checked)).ToList();
    var enforcement = ContractEnforcer.Enforce(module, assumptions, language, bag);
    if (enforcement.Module is { } enforced)
      Assert.That(IrVerifier.Verify(enforced), Is.Empty);

    return (enforcement, bag);
  }

  private static (ContractEnforcement Enforcement, DiagnosticBag Diagnostics) Enforce(string methods, params string[] contracts) => Enforce(Import(methods), "csharp", contracts);

  private static ContractAssumption Parsed(string text, bool isChecked, string origin) {
    Assert.That(ContractParser.TryParse(text, isChecked, origin, out var assumption, out var problem), Is.True, problem);
    return assumption!;
  }

  private static string Violation(IrModule module, string function, params object[] arguments) {
    var thrown = Assert.Throws<IrException>(() => IrInterpreter.Call(module, function, arguments))!;
    Assert.That(thrown.Class, Is.EqualTo(IrExceptionClass.ArgumentException));
    return thrown.Message;
  }

  private static IEnumerable<string> Codes(DiagnosticBag bag) => bag.Items.Select(item => item.Code);

  private const string Sum = "public static int Sum(int n) { int s = 0; for (int i = 0; i < n; i++) s += i; return s; }";

  /// <summary>Test design 2 and 9: a checked range on an int, on the bounds and just outside them.</summary>
  [Test]
  public void ACheckedRange_GuardsTheEntry() {
    var (enforcement, bag) = Enforce(Sum, "Sum.n: int[0, 65535]");
    Assert.That(bag.Items, Is.Empty);
    var module = enforcement.Module!;
    var guard = (IrIf)module.Functions.Single().Body.Statements[0];
    Assert.That(guard.Condition, Is.InstanceOf<IrBinary>().With.Property(nameof(IrBinary.Op)).EqualTo(IrBinaryOp.LogicalOr));
    Assert.That(IrInterpreter.Call(module, "Sum", [0]), Is.EqualTo(0));
    Assert.That(IrInterpreter.Call(module, "Sum", [4]), Is.EqualTo(6));
    Assert.That(IrInterpreter.Call(module, "Sum", [65535]), Is.EqualTo(IrInterpreter.Call(Import(Sum), "Sum", [65535])));
    Assert.That(Violation(module, "Sum", -1), Is.EqualTo("ContractViolation: Sum.n: int[0, 65535]"));
    Assert.That(Violation(module, "Sum", 65536), Is.EqualTo("ContractViolation: Sum.n: int[0, 65535]"));
    Assert.That(enforcement.Assumptions, Is.EqualTo(new[] { new ContractRecord("Sum.n", "int[0, 65535]", true, "--assume", 0) }));
  }

  /// <summary>A bound outside the type tests only the other side; a narrower scalar is its range.</summary>
  [TestCase("F.n: int[-5, 2147483647]", -6, 5)]
  [TestCase("F.n: int[-2147483648, 9]", 10, -2147483648)]
  [TestCase("F.n: u8", 256, 255)]
  [TestCase("F.n: i16", -32769, -32768)]
  public void OneSidedAndScalarRanges_AreChecked(string contract, int violating, int conforming) {
    var (enforcement, _) = Enforce("public static int F(int n) => n;", contract);
    Assert.That(IrInterpreter.Call(enforcement.Module!, "F", [conforming]), Is.EqualTo(conforming));
    Assert.That(Violation(enforcement.Module!, "F", violating), Does.EndWith(contract));
  }

  /// <summary>A range on an unsigned 64-bit parameter compares its 64 bits as unsigned.</summary>
  [Test]
  public void AnUnsigned64BitRange_IsChecked() {
    var (enforcement, _) = Enforce("public static ulong F(ulong n) => n;", "F.n: int[10, 18446744073709551000]");
    Assert.That(IrInterpreter.Call(enforcement.Module!, "F", [18446744073709551000UL]), Is.EqualTo(18446744073709551000UL));
    Assert.That(Violation(enforcement.Module!, "F", 18446744073709551001UL), Does.StartWith("ContractViolation"));
    Assert.That(Violation(enforcement.Module!, "F", 9UL), Does.StartWith("ContractViolation"));
  }

  /// <summary>Test design 12: a violating call fails before any effect of the callee.</summary>
  [Test]
  public void AViolatingCall_FailsBeforeAnyEffect() {
    var (enforcement, _) = Enforce("static int calls; public static int F(int n) { calls++; return n; } public static int Calls() => calls;", "F.n: int[0, 9]");
    var instance = IrInterpreter.Instantiate(enforcement.Module!);
    Assert.That(instance.Call("F", 5), Is.EqualTo(5));
    Assert.That(() => instance.Call("F", 10), Throws.InstanceOf<IrException>());
    Assert.That(instance.Call("Calls"), Is.EqualTo(1));
  }

  /// <summary>The checks stand in parameter order, whatever order the contracts were given in.</summary>
  [Test]
  public void Checks_StandInParameterOrder() {
    var (enforcement, _) = Enforce("public static int F(int a, int b) => a + b;", "F.b: int[0, 1]", "F.a: int[0, 1]");
    Assert.That(Violation(enforcement.Module!, "F", 5, 5), Does.EndWith("F.a: int[0, 1]"));
    Assert.That(Violation(enforcement.Module!, "F", 0, 5), Does.EndWith("F.b: int[0, 1]"));
  }

  /// <summary>Test design 11: a length, exact and as a range.</summary>
  [Test]
  public void Lengths_AreChecked() {
    var (exact, _) = Enforce("public static int F(byte[] d) => d.Length;", "F.d: seq<u8>[256]");
    Assert.That(IrInterpreter.Call(exact.Module!, "F", [new byte[256]]), Is.EqualTo(256));
    Assert.That(Violation(exact.Module!, "F", new byte[255]), Does.EndWith("seq<u8>[256]"));
    var (range, _) = Enforce("public static int F(byte[] d) => d.Length;", "F.d: seq<u8>[1..4]");
    Assert.That(IrInterpreter.Call(range.Module!, "F", [new byte[4]]), Is.EqualTo(4));
    Assert.That(Violation(range.Module!, "F", new byte[0]), Does.EndWith("seq<u8>[1..4]"));
    Assert.That(Violation(range.Module!, "F", new byte[5]), Does.EndWith("seq<u8>[1..4]"));
    var (open, _) = Enforce("public static int F(byte[] d) => d.Length;", "F.d: seq<u8>[2..4294967296]");
    Assert.That(Violation(open.Module!, "F", new byte[1]), Does.StartWith("ContractViolation"));
    Assert.That(IrInterpreter.Call(open.Module!, "F", [new byte[3]]), Is.EqualTo(3));
  }

  /// <summary>Test design 8: a result contract is checked before every return.</summary>
  [Test]
  public void AResultContract_IsCheckedBeforeEveryReturn() {
    var (enforcement, bag) = Enforce("public static int F(int n) { if (n > 0) { return n; } else { return -n; } }", "F.return: int[0, 9]");
    Assert.That(bag.Items, Is.Empty);
    Assert.That(IrInterpreter.Call(enforcement.Module!, "F", [-9]), Is.EqualTo(9));
    Assert.That(Violation(enforcement.Module!, "F", 10), Does.EndWith("F.return: int[0, 9]"));
    Assert.That(Violation(enforcement.Module!, "F", -10), Does.EndWith("F.return: int[0, 9]"));
    Assert.That(enforcement.Module!.Functions.Single().Locals.Select(local => local.Name), Is.SupersetOf(new[] { "contractResult0", "contractResult1" }));
  }

  /// <summary>A result local takes a name the function does not use.</summary>
  [Test]
  public void AResultLocal_HasAFreshName() {
    var (enforcement, _) = Enforce("public static int F(int contractResult0) { return contractResult0; }", "F.return: int[0, 9]");
    Assert.That(enforcement.Module!.Functions.Single().Locals.Select(local => local.Name), Does.Contain("contractResult1"));
  }

  /// <summary>Test design 13: an unchecked contract is reported and trusted, nothing is checked.</summary>
  [Test]
  public void AnUncheckedContract_IsReportedNotHidden() {
    var module = Import(Sum);
    var (enforcement, bag) = Enforce(module, "csharp", "~Sum.n: int[0, 65535]");
    Assert.That(bag.Items.Select(item => (item.Code, item.Severity, item.Message)), Is.EqualTo(new[] {
      ("PLY0512", DiagnosticSeverity.Warning, "assumption 'Sum.n: int[0, 65535]' is trusted without a run-time check; results are undefined for calls that violate it")
    }));
    Assert.That(enforcement.Module!.Functions.Single().Body, Is.EqualTo(module.Functions.Single().Body));
    Assert.That(enforcement.Assumptions.Single(), Is.EqualTo(new ContractRecord("Sum.n", "int[0, 65535]", false, "--assume-unchecked", 0)));
  }

  /// <summary>Test design 18: a contract the type already proves has no effect, in either mode.</summary>
  [TestCase("public static int F(byte[] d) => d.Length;", "F.d: seq<u8>")]
  [TestCase("public static int F(byte n) => n;", "F.n: int[0, 255]")]
  [TestCase("public static int F(byte n) => n;", "F.n: nullable(u16)")]
  [TestCase("public static int F(int n) => n;", "F.n: i32")]
  [TestCase("public static double F(double x) => x;", "F.x: f64")]
  [TestCase("public static float F(float x) => x;", "F.x: f64")]
  [TestCase("public static bool F(bool x) => x;", "F.x: bool")]
  [TestCase("public static string F(string x) => x;", "F.x: text")]
  [TestCase("public static int F(int? x) => x ?? 0;", "F.x: nullable(i32)")]
  [TestCase("public static int F(byte[] d) => d.Length;", "F.return: i32")]
  public void AProvenContract_HasNoEffect(string method, string contract) {
    foreach (var mode in new[] { contract, "~" + contract }) {
      var (enforcement, bag) = Enforce(method, mode);
      Assert.That(bag.Items.Select(item => item.Message), Is.EqualTo(new[] { $"assumption '{contract}' had no effect" }));
      Assert.That(enforcement.Assumptions.Single().Checked, Is.True);
    }
  }

  /// <summary>Test design 14: a contract no value of the type satisfies is an error in either mode, and nothing is written.</summary>
  [TestCase("public static int F(string x) => x.Length;", "F.x: i32", "the parameter 'x' has the type str at function 'F'")]
  [TestCase("public static int F(int x) => x;", "F.x: text", "the parameter 'x' has the type i32 at function 'F'")]
  [TestCase("public static int F(int x) => x;", "F.x: bool", "the parameter 'x' has the type i32 at function 'F'")]
  [TestCase("public static int F(byte x) => x;", "F.x: int[300, 400]", "the parameter 'x' has the type u8, which never lies in [300, 400] at function 'F'")]
  [TestCase("public static int F(string x) => x.Length;", "F.x: seq<u8>", "the parameter 'x' has the type str at function 'F'")]
  [TestCase("public static int F(int[] x) => x.Length;", "F.x: seq<text>", "the parameter 'x' has the type i32[] whose elements are i32 at function 'F'")]
  [TestCase("public static int F(byte[] x) => x.Length;", "F.x: seq<u8>[4294967296]", "the parameter 'x' has the type u8[], which never has 4294967296 elements at function 'F'")]
  [TestCase("public static int F(string x) => x.Length;", "F.x: f64", "the parameter 'x' has the type str at function 'F'")]
  [TestCase("public static int F(int x) => x;", "F.x: record{ w: i32 }", "the parameter 'x' has the type i32 at function 'F'")]
  [TestCase("public static int F(int? x) => x ?? 0;", "F.x: nullable(text)", "the parameter 'x' has the type i32? at function 'F'")]
  [TestCase("public static string F(int x) => \"\";", "F.return: i32", "the result of 'F' has the type str at function 'F'")]
  public void AContradiction_IsAnError(string method, string contract, string message) {
    foreach (var mode in new[] { contract, "~" + contract }) {
      var (enforcement, bag) = Enforce(method, mode);
      Assert.That(enforcement.Module, Is.Null);
      Assert.That(bag.Items.Select(item => (item.Code, item.Message)), Is.EqualTo(new[] { ("PLY0513", $"assumption '{contract}' contradicts a proven fact: {message}") }));
      Assert.That(bag.ExitCode, Is.EqualTo(ExitCode.InvalidSource));
    }
  }

  /// <summary>Test design 17: a checked contract no constant-cost test checks is PLY0516; unchecked it is PLY0512.</summary>
  [TestCase("public static int F(int[] d) => d.Length;", "F.d: seq<u8>")]
  [TestCase("public static double F(double x) => x;", "F.x: int[0, 9]")]
  [TestCase("public static double F(double x) => x;", "F.x: f32")]
  [TestCase("public static int F(int x) => x;", "F.x: f64")]
  [TestCase("public static int F(int? x) => x ?? 0;", "F.x: i32")]
  [TestCase("public static int F(int? x) => x ?? 0;", "F.x: nullable(u8)")]
  [TestCase("public static int F(System.Collections.Generic.List<int> x) => x.Count;", "F.x: seq<i32>")]
  public void AnUncheckableContract_IsRefusedWhenChecked(string method, string contract) {
    var (refused, bag) = Enforce(method, contract);
    Assert.That(refused.Module, Is.Null);
    Assert.That(bag.Items.Select(item => (item.Code, item.Message)), Is.EqualTo(new[] {
      ("PLY0516", $"assumption '{contract}' cannot be checked at run time in O(1); use --assume-unchecked to accept it explicitly")
    }));
    var (trusted, warned) = Enforce(method, "~" + contract);
    Assert.That(Codes(warned), Is.EqualTo(new[] { "PLY0512" }));
    Assert.That(trusted.Assumptions.Single().Checked, Is.False);
  }

  /// <summary>Test design 6e-6g on the types of the typed IR: a record against a struct.</summary>
  [Test]
  public void ARecordContract_AgainstAStruct() {
    const string Methods = "public struct P { public int W; public byte H; } public static int Area(P p) => p.W * p.H;";
    Assert.That(Codes(Enforce(Methods, "Area.p: record{ w: i32 }").Diagnostics), Is.EqualTo(new[] { "PLY0513" }));
    Assert.That(Codes(Enforce(Methods, "Area.p: record{ W: i32, H: int[0, 255] }").Diagnostics), Is.EqualTo(new[] { "PLY0515" }));
    Assert.That(Codes(Enforce(Methods, "Area.p: record{ W: int[0, 65535] }").Diagnostics), Is.EqualTo(new[] { "PLY0516" }));
    Assert.That(Enforce(Methods, "Area.p: record{ H: text }").Diagnostics.Items.Single().Message, Does.Contain("whose field 'H' has the type u8"));
  }

  /// <summary>Test design 15: a target that names nothing of the module.</summary>
  [TestCase("nosuch.x: u8", "the module has no function 'nosuch'")]
  [TestCase("F.q: u8", "the function 'F' has no parameter 'q'")]
  [TestCase("G.x: u8", "the function 'G' is not exported")]
  [TestCase("V.return: u8", "the function 'V' returns nothing")]
  [TestCase("global.nosuch: u8", "the module has no global 'nosuch'")]
  [TestCase("F.x: bytes", "'bytes' is a spelling of python, and the source is csharp")]
  public void AnUnknownTarget_IsPLY0514(string contract, string problem) {
    var (enforcement, bag) = Enforce("public static int F(int x) => G(x); static int G(int x) => x; public static void V() { }", contract);
    Assert.That(enforcement.Module, Is.Null);
    // The message names the contract as it is canonically spelled.
    Assert.That(bag.Items.Select(item => (item.Code, item.Message)), Is.EqualTo(new[] { ("PLY0514", $"invalid assumption '{ContractParser.Parse(contract).Text}': {problem}") }));
  }

  /// <summary>A global has no boundary: checked it is PLY0516, unchecked PLY0512; one its type proves has no effect.</summary>
  [Test]
  public void AGlobal_IsTrustedOrProven() {
    const string Methods = "static int limit = 5; public static int F() => limit++;";
    Assert.That(Codes(Enforce(Methods, "global.limit: int[0, 9]").Diagnostics), Is.EqualTo(new[] { "PLY0516" }));
    Assert.That(Codes(Enforce(Methods, "~global.limit: int[0, 9]").Diagnostics), Is.EqualTo(new[] { "PLY0512" }));
    Assert.That(Codes(Enforce(Methods, "global.limit: i32").Diagnostics), Is.EqualTo(new[] { "PLY0515" }));
    Assert.That(Enforce(Methods, "global.limit: text").Diagnostics.Items.Single().Message, Does.EndWith("the global 'limit' has the type i32 at global 'limit'"));
  }

  /// <summary>A language-native spelling of the source language is accepted.</summary>
  [Test]
  public void ASpellingOfTheSourceLanguage_IsAccepted() {
    var module = Import("public static int F(byte[] d) => d.Length;");
    Assert.That(Codes(Enforce(module, "javascript", "F.d: Uint8Array").Diagnostics), Is.EqualTo(new[] { "PLY0515" }));
  }

  /// <summary>No contract leaves the module as it is.</summary>
  [Test]
  public void NoContract_LeavesTheModule() {
    var module = Import(Sum);
    var (enforcement, bag) = Enforce(module, "csharp");
    Assert.That((enforcement.Module!.Functions[0], bag.Items.Count, enforcement.Assumptions.Count), Is.EqualTo((module.Functions[0], 0, 0)));
  }

  /// <summary>A record against a class: its fields, the inherited ones included.</summary>
  [Test]
  public void ARecordContract_AgainstAClass() {
    const string Methods = "public sealed class Q { public int W; } public static int F(Q q) => q.W;";
    Assert.That(Codes(Enforce(Methods, "F.q: record{ W: i32 }").Diagnostics), Is.EqualTo(new[] { "PLY0515" }));
    Assert.That(Codes(Enforce(Methods, "F.q: record{ H: i32 }").Diagnostics), Is.EqualTo(new[] { "PLY0513" }));
  }

  /// <summary>An integer contract on an arbitrary-precision integer needs a test this enforcer does not build.</summary>
  [Test]
  public void ARangeOnABigInteger_IsNotCheckable() {
    var n = new IrLocal("n", IrType.BigInt);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [n], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 0))]))]);
    Assert.That(Codes(Enforce(module, "python", "F.n: int[0, 9]").Diagnostics), Is.EqualTo(new[] { "PLY0516" }));
    Assert.That(Codes(Enforce(module, "python", "~F.n: u8").Diagnostics), Is.EqualTo(new[] { "PLY0512" }));
  }

  /// <summary>A contract of a kind the enforcer does not know is a defect of the caller.</summary>
  [Test]
  public void AnUnknownKindOfContract_IsRejected() {
    var module = Import("public static int F(int x) => x;");
    var odd = new ContractAssumption("F.x", new OddContract(), true, ContractParser.Checked);
    Assert.That(() => ContractEnforcer.Enforce(module, [odd], "csharp", new DiagnosticBag()), Throws.TypeOf<ArgumentOutOfRangeException>());
  }

  private sealed record OddContract : Contract {

    public override string Text => "odd";

  }

  [Test]
  public void Arguments_AreChecked() {
    var module = Import(Sum);
    Assert.That(() => ContractEnforcer.Enforce(null!, [], "csharp", new DiagnosticBag()), Throws.ArgumentNullException);
    Assert.That(() => ContractEnforcer.Enforce(module, null!, "csharp", new DiagnosticBag()), Throws.ArgumentNullException);
    Assert.That(() => ContractEnforcer.Enforce(module, [], null!, new DiagnosticBag()), Throws.ArgumentNullException);
    Assert.That(() => ContractEnforcer.Enforce(module, [], "csharp", null!), Throws.ArgumentNullException);
  }

}
