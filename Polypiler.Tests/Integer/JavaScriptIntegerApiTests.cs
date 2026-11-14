using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Integer;

/// <summary>
/// The functions and methods of ECMAScript's library the JavaScript frontend imports as integer
/// operations, as <see cref="JavaScriptIntegerApi"/> lists them: every entry has a use here that is
/// imported into the constructs it names, with the type the inference proves. Whether the IR then
/// computes what node computes for the original is asked of node in the acceptance tests, on the
/// same uses (<see cref="JavaScriptIntegerUses"/>).
/// </summary>
[TestFixture]
public sealed class JavaScriptIntegerApiTests {

  private static IEnumerable<TestCaseData> Uses() => JavaScriptIntegerUses.All.Select(use => new TestCaseData(use.Member, use.Source).SetArgDisplayNames(use.Source));

  /// <summary>Test design 3: every use is imported, into the constructs its entry names, with the result type it states.</summary>
  [TestCaseSource(nameof(Uses))]
  public void Member_IsTheOperationTheTableNames(string member, string source) {
    var entry = JavaScriptIntegerApi.Find(member);
    Assert.That(entry, Is.Not.Null, $"{member} is not in the table");
    var result = JavaScriptFrontend.Import("snippet.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, source);
    var function = result.Module!.Functions.Single(candidate => candidate.Name == "f");
    var constructs = Constructs(function.Body).ToList();
    Assert.That(entry!.Ir.Split(" | ").Any(form => form.Split(", ").All(constructs.Contains)), Is.True, $"{source} is {string.Join(", ", constructs)}, not {entry.Ir}");
  }

  /// <summary>Every member of the table has a use.</summary>
  [Test]
  public void EveryMember_HasAUse()
    => Assert.That(JavaScriptIntegerUses.All.Select(use => use.Member).Distinct(), Is.EquivalentTo(JavaScriptIntegerApi.Members.Select(entry => entry.Member)));

  /// <summary>The magnitude of an int32 needs a uint32, the minimum of two int16 is one, the sign is an int32.</summary>
  [TestCase("export function f(a) { return Math.abs(a | 0); }", "u32")]
  [TestCase("/** @param {int16} a @param {int16} b */ export function f(a, b) { return Math.min(a, b); }", "i16")]
  [TestCase("/** @param {uint8} a @param {int8} b */ export function f(a, b) { return Math.max(a, b); }", "i16")]
  [TestCase("/** @param {uint16} a */ export function f(a) { return Math.sign(a); }", "i32")]
  [TestCase("/** @param {uint8} a */ export function f(a) { return Math.abs(a); }", "u8")]
  [TestCase("export function f(a) { return Math.abs(a); }", "f64")]
  public void IntegerMath_HasTheTypeOfItsResult(string source, string type) {
    var result = JavaScriptFrontend.Import("snippet.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(result.Module!.Functions.Single().ReturnType.ToString(), Is.EqualTo(type));
  }

  /// <summary>Test design 10: a DataView whose byte order is no literal, or that is no view of a Uint8Array of the function, is refused by name.</summary>
  [TestCase("/** @param {Uint8Array} b @param {boolean} l */ export function f(b, l) { const v = new DataView(b.buffer); return v.getUint32(0, l); }", "a byte order that is not a literal")]
  [TestCase("/** @param {Uint8Array} b */ export function f(b) { let v = new DataView(b.buffer); return v.getUint32(0); }", "a DataView in a variable that is not const")]
  [TestCase("/** @param {Uint8Array} b @param {Uint8Array} c */ export function f(b, c) { const v = new DataView(b.buffer, c.byteOffset, b.byteLength); return v.getUint16(0); }", "not made of the buffer")]
  [TestCase("/** @param {Uint16Array} b */ export function f(b) { const v = new DataView(b.buffer); return v.getUint16(0); }", "no Uint8Array")]
  [TestCase("/** @param {Uint8Array} b */ export function f(b) { const v = new DataView(b.buffer); return v.getFloat32(0); }", "the DataView method getFloat32")]
  [TestCase("/** @param {Uint8Array} b */ export function f(b) { const v = new DataView(b.buffer); return v.getUint8(0.5); }", "no proven integer")]
  [TestCase("/** @param {Uint8Array} b */ export function f(b) { const v = new DataView(b.buffer); return v.getUint16(); }", "with 0 arguments")]
  [TestCase("/** @param {Uint8Array} b */ export function f(b) { const v = new DataView(b.buffer); return v; }", "used other than by its methods")]
  [TestCase("/** @param {Uint8Array} b */ export function f(b) { const v = new DataView(b.buffer); return v.setUint8(0, 1); }", "DataView.setUint8 inside an expression")]
  [TestCase("export function f(a) { return BigInt.asUintN(65, BigInt(a | 0)); }", "a width other than a literal from 1 to 64")]
  [TestCase("export function f(a, n) { return BigInt.asIntN(n | 0, BigInt(a | 0)); }", "a width other than a literal from 1 to 64")]
  [TestCase("export function f(a) { return BigInt.asIntN(8); }", "BigInt.asIntN with 1 arguments")]
  public void UnsupportedForm_IsRefusedByName(string source, string named) {
    var result = JavaScriptFrontend.Import("snippet.mjs", source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.Member(JavaScriptFrontend.Unsupported));
    Assert.That(string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Message)), Does.Contain(named));
  }

  /// <summary>The names of the intrinsic operations, binary operators, conversions, loads and stores below a statement.</summary>
  private static IEnumerable<string> Constructs(IrStmt statement) => statement switch {
    IrBlock block => block.Statements.SelectMany(Constructs),
    IrDeclare { Initializer: { } value } => Constructs(value),
    IrAssign assign => Constructs(assign.Value),
    IrReturn { Value: { } value } => Constructs(value),
    IrExprStmt expression => Constructs(expression.Expression),
    IrStore store => Constructs(store.Value).Prepend("Store"),
    _ => []
  };

  private static IEnumerable<string> Constructs(IrExpr expression) => expression switch {
    IrIntrinsic intrinsic => intrinsic.Arguments.SelectMany(Constructs).Prepend(intrinsic.Op.ToString()),
    IrBinary binary => Constructs(binary.Left).Concat(Constructs(binary.Right)).Prepend(binary.Op.ToString()),
    IrConvert conversion => Constructs(conversion.Operand).Prepend("Convert"),
    IrArrayLoad load => Constructs(load.Index).Prepend("ArrayLoad"),
    IrUnary unary => Constructs(unary.Operand),
    _ => []
  };

}
