using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.BigInt;

/// <summary>
/// JSDoc's <c>int64</c> and <c>uint64</c> (#1401) state the range of a place, not whether it holds
/// Numbers or BigInts: the values that reach it decide. A place only Numbers reach is a Number of the
/// type those values give, one only BigInts reach a BigInt of the stated type, one nothing decides a
/// BigInt as before, and one both reach is refused. The Cipher corpus states <c>@type {uint64}</c> for a
/// byte counter it keeps in a Number and <c>@param {int64}</c> for <c>OpCodes.ToUint32</c>, which every
/// caller passes a Number. The expected values are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptStatedLongNumberTests {

  private const string Counter = """
    class Counter {
      constructor() {
        /** @type {uint64} - bytes counted */
        this.n = 0;
      }
      /** @param {int32} k */
      add(k) { this.n += k; }
      /** @returns {uint64} the count */
      get Length() { return this.n; }
    }
    export function scenario() { const c = new Counter(); c.add(3); c.add(4); return c.Length; }
    """;

  private const string Places = """
    /**
     * @param {int64} value
     * @returns {uint32}
     */
    function toUint32(value) { return value >>> 0; }
    /** @param {uint64} v */
    function bump(v) { return BigInt.asUintN(64, v & 255n); }
    /** @param {uint64} v */
    export function outside(v) { return v; }
    export function wrap(x) { return toUint32(x); }
    export function local(k) {
      /** @type {uint64} */
      let total = 0;
      total += k;
      return total;
    }
    export function big() { return bump(5n); }
    class Sum { constructor() { /** @type {uint64} */ this.v = 0; } }
    export function large(a, b) {
      const s = new Sum();
      s.v += a;
      s.v += b;
      return s.v;
    }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static IrFunction FunctionOf(IrModule module, string name) => module.Functions.Single(item => item.Name == name);

  /// <summary>Scenario: a byte counter stated uint64, added 3 and 4, is 7 as in node, and a Number.</summary>
  [Test]
  public void ByteCounterStatedUint64_IsANumber() {
    var module = Import(Counter);
    var field = module.Types.OfType<IrClassType>().Single().Fields.Single();
    Assert.That(field.Type.IsLong, Is.False, field.Type.ToString());
    Assert.That(field.Type.IsNumeric, Is.True, field.Type.ToString());
    Assert.That(IrInterpreter.Call(module, "scenario", []), Is.EqualTo(7d));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("scenario").Invoke(null, []), Is.EqualTo(7d), text);
    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
  }

  /// <summary>Test design 2: a parameter only Numbers reach is a double, and <c>&gt;&gt;&gt; 0</c> of it is ToUint32 as in node.</summary>
  [TestCase(5d, 5d)]
  [TestCase(-1d, 4294967295d)]
  [TestCase(4294967301d, 5d)]
  [TestCase(1.5d, 1d)]
  public void ParameterOnlyNumbersReach_IsADouble(double argument, double expected) {
    var module = Import(Places);
    Assert.That(FunctionOf(module, "toUint32").Parameters.Single().Type, Is.EqualTo(IrType.F64));
    Assert.That(FunctionOf(module, "toUint32").ReturnType, Is.EqualTo(IrType.U32));
    Assert.That(System.Convert.ToDouble(IrInterpreter.Call(module, "wrap", [argument])), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(System.Convert.ToDouble(GeneratedCSharp.Compile(text, false).Method("wrap").Invoke(null, [argument])), Is.EqualTo(expected), text);
  }

  /// <summary>Test design 2: a local stated uint64 that Numbers reach is a double, and the result stated uint64 that returns one is one.</summary>
  [Test]
  public void LocalAndResult_OnlyNumbersReach_AreNumbers() {
    var module = Import(Places);
    Assert.That(FunctionOf(module, "local").ReturnType, Is.EqualTo(IrType.F64));
    Assert.That(IrInterpreter.Call(module, "local", [7d]), Is.EqualTo(7d));
    Assert.That(Import(Counter).Functions.Single(item => item.Name.Contains("Length")).ReturnType.IsLong, Is.False);
  }

  /// <summary>Test design 3: only BigInts reach a stated uint64 - a BigInt of the stated type; nothing reaches an exported one - a BigInt as before.</summary>
  [Test]
  public void OnlyBigIntsOrNothing_StayBigInts() {
    var module = Import(Places);
    Assert.That(FunctionOf(module, "bump").Parameters.Single().Type, Is.EqualTo(IrType.U64));
    Assert.That(FunctionOf(module, "outside").Parameters.Single().Type, Is.EqualTo(IrType.U64));
    Assert.That(IrInterpreter.Call(module, "big", []), Is.EqualTo(5UL));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("big").Invoke(null, []), Is.EqualTo(5UL), text);
  }

  /// <summary>Equivalence: a stated uint64 that only other undecided stated ones reach - a field, an argument - or that nothing reaches, a method no call passes anything, stays a BigInt.</summary>
  [Test]
  public void OnlyUndecidedStatedOnesReach_StayBigInts() {
    var module = Import("""
      class Box {
        /** @param {uint64} v */
        constructor(v) { /** @type {uint64} */ this.n = v; }
        /** @param {uint64} v */
        peek(v) { return v; }
      }
      /** @param {uint64} v */
      function inner(v) { return v; }
      /** @param {uint64} v */
      export function outer(v) { const b = new Box(v); return inner(b.n); }
      """);
    Assert.That(module.Types.OfType<IrClassType>().Single().Fields.Single().Type, Is.EqualTo(IrType.U64));
    Assert.That(FunctionOf(module, "inner").Parameters.Single().Type, Is.EqualTo(IrType.U64));
    Assert.That(module.Functions.Single(item => item.Name.Contains("peek")).Parameters.Last().Type, Is.EqualTo(IrType.U64), "a method no call of the file passes anything");
    Assert.That(IrInterpreter.Call(module, "outer", [7UL]), Is.EqualTo(7UL));
  }

  /// <summary>Test design 4: around 2^53 the counter keeps the precision of node's doubles, which round 2^53 + 1 to 2^53.</summary>
  [TestCase(9007199254740990d, 1d, 9007199254740991d)]
  [TestCase(9007199254740991d, 2d, 9007199254740992d)]
  [TestCase(9007199254740992d, 1d, 9007199254740992d)]
  public void Counter_Around2To53_IsANodeDouble(double a, double b, double expected) {
    var module = Import(Places);
    Assert.That(IrInterpreter.Call(module, "large", [a, b]), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("large").Invoke(null, [a, b]), Is.EqualTo(expected), text);
  }

  /// <summary>Scenario: a stated int64 called with 5 and 5n is refused with PLY1105 naming v.</summary>
  [Test]
  public void BothKindsReachAStatedInt64_IsRefused() {
    var result = JavaScriptFrontend.Import("m.mjs", """
      /** @param {int64} v */
      function f(v) { return v; }
      export function g() { f(5); return f(5n); }
      """);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.Some.EqualTo(JavaScriptFrontend.MixedKinds), string.Join("; ", result.Diagnostics));
    Assert.That(result.Diagnostics.Where(diagnostic => diagnostic.Code == JavaScriptFrontend.MixedKinds).Select(diagnostic => diagnostic.Message), Has.Some.Contains("'v' holds both"));
  }

  /// <summary>Exception: a field stated uint64 that a Number and a BigInt reach, or a string, is refused as before.</summary>
  [TestCase("5n")]
  [TestCase("'x'")]
  public void BothKindsReachAStatedField_IsRefused(string other) {
    var result = JavaScriptFrontend.Import("m.mjs", $$"""
      class C { constructor() { /** @type {uint64} */ this.n = 0; } grow() { this.n = {{other}}; } }
      export function g() { const c = new C(); c.grow(); return 1; }
      """);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains("'n' holds both"), string.Join("; ", result.Diagnostics));
  }

  /// <summary>Exception: a string passed to a stated int64 decides nothing; the call is refused as before.</summary>
  [Test]
  public void StringPassedToAStatedInt64_IsRefused() {
    var result = JavaScriptFrontend.Import("m.mjs", """
      /** @param {int64} v */
      function f(v) { return v; }
      export function g() { return f('x'); }
      """);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains("string where int64 is expected"), string.Join("; ", result.Diagnostics));
  }

  /// <summary>Exception: <c>&gt;&gt;&gt;</c> of a stated int64 nothing decides is a BigInt, refused as before (node throws a TypeError).</summary>
  [Test]
  public void UnsignedShiftOfUndecidedInt64_IsRefused() {
    var result = JavaScriptFrontend.Import("m.mjs", """
      /** @param {int64} v */
      export function f(v) { return v >>> 0; }
      """);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains(">>>"), string.Join("; ", result.Diagnostics));
  }

}
