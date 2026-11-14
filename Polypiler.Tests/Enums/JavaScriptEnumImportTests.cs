using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Enums;

/// <summary>
/// The enum objects of JavaScript (#1318): a frozen object of distinct 32-bit integers is an enum over
/// <c>i32</c>, a value that only its members reach is of the enum, any other use of a member is its
/// number; what node computes for each supported row, and every refused construct of
/// <see cref="JavaScriptEnumApi"/> by name.
/// </summary>
[TestFixture]
public sealed class JavaScriptEnumImportTests {

  private const string Color = "const Color = Object.freeze({ Red: 0, Green: 5, Blue: 6 });\n";

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Not.Null, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static IrEnumType EnumOf(IrModule module) => module.Types.OfType<IrEnumType>().Single();

  /// <summary>One source per supported row, with its calls and what node returns for each.</summary>
  private static readonly (string Row, string Source, (object[] Arguments, object Node)[] Calls)[] _cases = [
    ("const X = Object.freeze({ A: <integer literal>, … }); X.A",
      Color + "export function f() { return Color.Green + Color.Blue * 10; }", [([], 65.0)]),
    ("a variable, parameter or result that only members of one enum object reach, and literals equal to a member",
      Color + "function pick(c) { return c === Color.Blue ? 1 : 2; } /** @param {int32} n */ export function f(n) { let c = Color.Red; if (n > 0) c = Color.Blue; if (n > 5) c = 5; return pick(c) * 10 + pick(Color.Green); }",
      [([0], 22.0), ([1], 12.0), ([9], 22.0)]),
    ("x === X.A, x !== X.A, switch (x) { case X.A: … } on such a value",
      Color + "function name(c) { switch (c) { case Color.Red: return 1; case Color.Green: return 2; default: return c !== Color.Blue ? 9 : 3; } } /** @param {int32} n */ export function f(n) { return name(n > 0 ? Color.Blue : Color.Green) + name(Color.Red) * 10; }",
      [([0], 12.0), ([1], 13.0)]),
    ("any other use of a member: arithmetic, X.A | X.B, x < X.A",
      Color + "/** @param {int32} a @returns {int32} */ export function f(a) { return (a | Color.Green) + (Color.Green | Color.Blue) + (a < Color.Blue ? 100 : 0); }",
      [([0], 112), ([8], 20)]),
    ("export const X = Object.freeze({ … }), export { X }, an enum object of a script",
      "export " + Color + "const Mode = Object.freeze({ On: 1 }); export { Mode }; export function f() { return Color.Red + Mode.On; }", [([], 1.0)])
  ];

  private static IEnumerable<TestCaseData> Cases() => _cases.Select(entry => new TestCaseData(entry.Source, entry.Calls).SetName("Imports: " + entry.Row));

  /// <summary>Test design 1 and 12: every supported row computes in the interpreter what node computes.</summary>
  [TestCaseSource(nameof(Cases))]
  public void Row_IsImported_AndComputesWhatNodeComputes(string source, (object[] Arguments, object Node)[] calls) {
    var module = Import(source);
    foreach (var (arguments, node) in calls)
      Assert.That(PrototypeFixture.Show(IrInterpreter.Call(module, "f", arguments)), Is.EqualTo(PrototypeFixture.Show(node)), string.Join(", ", arguments.Select(PrototypeFixture.Show)));
  }

  [Test]
  public void EverySupportedRow_HasACase()
    => Assert.That(_cases.Select(entry => entry.Row), Is.EquivalentTo(JavaScriptEnumApi.Supported.Select(member => member.Construct)));

  [Test]
  public void EveryRefusedRow_HasAReason() {
    Assert.That(JavaScriptEnumApi.Refused.Select(member => member.Refusal), Has.All.Not.Empty);
    Assert.That(JavaScriptEnumApi.Members.Select(member => member.Construct), Is.Unique);
  }

  [Test]
  public void EnumObject_IsAnEnumOverInt32() {
    var named = EnumOf(Import("export " + Color + "export function f() { return Color.Red; }"));
    Assert.That((named.Name, named.Underlying, named.IsFlags, named.IsPublic), Is.EqualTo(("Color", IrType.I32, false, true)));
    Assert.That(named.Members.Select(member => (member.Name, member.Value)), Is.EqualTo(new[] { ("Red", 0L), ("Green", 5L), ("Blue", 6L) }));
    Assert.That(EnumOf(Import(Color + "export function f() { return Color.Red; }")).IsPublic, Is.False);
    Assert.That(EnumOf(Import(Color + "function f() { return Color.Red; }")).IsPublic, Is.True, "a script makes every declaration visible");
    Assert.That(EnumOf(Import("const Signs = Object.freeze({ Minus: -1, Max: 2147483647, Min: -2147483648 }); export function f() { return Signs.Minus; }")).Members[2].Value, Is.EqualTo(int.MinValue));
  }

  /// <summary>Test design 12: a variable, a parameter and a result that only members reach are of the enum; a member used as a number is its i32 value.</summary>
  [Test]
  public void Values_AreTypedAsTheEnumWhereOnlyMembersReachThem() {
    var module = Import(Color + "function pick(c) { return c === Color.Blue ? 1 : 2; } function best() { return Color.Blue; } /** @param {int32} n */ export function f(n) { let c = Color.Red; c = 5; let d = Color.Red; d = 7; return pick(c) + pick(best()) + d + (n | Color.Green); }");
    var named = EnumOf(module);
    var pick = module.Functions.Single(function => function.Name == "pick");
    var best = module.Functions.Single(function => function.Name == "best");
    var f = module.Functions.Single(function => function.Name == "f");
    Assert.That(pick.Parameters[0].Type.EnumType, Is.SameAs(named));
    Assert.That(best.ReturnType.EnumType, Is.SameAs(named));
    Assert.That(f.Locals.Single(local => local.Name == "c").Type.EnumType, Is.SameAs(named));
    Assert.That(f.Locals.Single(local => local.Name == "d").Type, Is.EqualTo(IrType.I32));
    var or = IrTree.Nodes(f.Body).OfType<IrBinary>().Single(binary => binary.Op == IrBinaryOp.Or);
    Assert.That(or.Right, Is.EqualTo(new IrConvert(IrType.I32, new IrConst(named.Type, 5))));
    Assert.That(IrTree.Nodes(pick.Body).OfType<IrBinary>().Single().Left.Type.EnumType, Is.SameAs(named));
  }

  [Test]
  public void Switch_OnAValueOfTheEnum_ChoosesByItsMembers() {
    var module = Import(Color + "function name(c) { switch (c) { case Color.Red: return 1; case 6: return 3; default: return 2; } } export function f() { return name(Color.Green); }");
    var choice = module.Functions.Single(function => function.Name == "name").Body.Statements.OfType<IrSwitch>().Single();
    Assert.That(choice.Value.Type.EnumType, Is.SameAs(EnumOf(module)));
    Assert.That(choice.Sections.SelectMany(section => section.Labels), Is.EqualTo(new long[] { 0, 6 }));
  }

  /// <summary>Test design 8: every refused construct, by name, naming the object.</summary>
  [TestCase("const C = { A: 0, B: 1 }; export function f() { return C.A; }", "the object C, which is not frozen: an enum object is Object.freeze({ … })")]
  [TestCase("const C = Object.freeze({ A: \"a\" }); export function f() { return C.A; }", "the string member C.A: an enum object holds numbers")]
  [TestCase("const C = Object.freeze({ A: 1, B: 1 }); export function f() { return C.A; }", "the members C.A and C.B of the same value")]
  [TestCase("const C = Object.freeze({ A: 1.5 }); export function f() { return C.A; }", "the member C.A, which is no 32-bit integer")]
  [TestCase("const C = Object.freeze({ A: 2147483648 }); export function f() { return C.A; }", "the member C.A, which is no 32-bit integer")]
  [TestCase("const C = Object.freeze({ A: -0 }); export function f() { return C.A; }", "the member C.A, which is no 32-bit integer")]
  [TestCase("const C = Object.freeze({ A: 1, A: 2 }); export function f() { return C.A; }", "the member C.A twice")]
  [TestCase("const k = \"A\"; const C = Object.freeze({ [k]: 0 }); export function f() { return C.A; }", "a member of the enum object C that is not a name and a number")]
  [TestCase("const C = Object.freeze({ A() { return 1; } }); export function f() { return C.A; }", "a member of the enum object C that is not a name and a number")]
  [TestCase("const C = Object.freeze({ A: 0, B: 1 }); export function f(k) { return C[k]; }", "a computed key into the enum object C")]
  [TestCase("const C = Object.freeze({ A: 0, B: 1 }); export function f() { return Object.keys(C).length; }", "the enum object C used other than by its members, as X.A")]
  [TestCase("const C = Object.freeze({ A: 0 }); function g(x) { return 1; } export function f() { return g(C); }", "the enum object C used other than by its members, as X.A")]
  [TestCase("const C = Object.freeze({ A: 0 }); export function f() { return C.Q; }", "C.Q, which is no member of the enum object C")]
  [TestCase("var C; (function (C) { C[C[\"A\"] = 0] = \"A\"; })(C || (C = {})); export function f() { return C.A; }",
    "the TypeScript-style enum C, an object that maps names to numbers and numbers to names: an enum object is Object.freeze({ … })")]
  public void Construct_IsRefusedByName(string source, string what) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Has.Some.EqualTo((JavaScriptFrontend.Unsupported, $"'{what}' is not supported yet")));
  }

  /// <summary>An enum object inside the function that wraps the file is one of the file, private to it.</summary>
  [Test]
  public void EnumObject_InTheWrappingFunction_IsPrivate() {
    var module = Import("(function () { const Mode = Object.freeze({ On: 1, Off: 0 }); function f() { return Mode.On; } })();");
    Assert.That(EnumOf(module).IsPublic, Is.False);
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(1));
  }

  [Test]
  public void ObjectOfNumbers_ThatNoFunctionUses_IsNoProblem() {
    var module = Import("const C = { A: 0 }; const D = Object.freeze({ A: \"a\" }); export function f() { return 1; }");
    Assert.That(module.Types, Is.Empty);
  }

  [Test]
  public void EnumObject_HiddenByALocal_IsTheLocal() {
    var module = Import(Color + "/** @param {int32} Color */ export function f(Color) { return Color + 1; }");
    Assert.That(module.Functions[0].Parameters[0].Type, Is.EqualTo(IrType.I32));
  }

  [Test]
  public void EnumObjects_OfOneName_AreNone() {
    var result = JavaScriptFrontend.Import("snippet.js", "const C = Object.freeze({ A: 0 }); function C() { return 1; } export function f() { return C.A; }");
    Assert.That(result.Module, Is.Null);
  }

}
