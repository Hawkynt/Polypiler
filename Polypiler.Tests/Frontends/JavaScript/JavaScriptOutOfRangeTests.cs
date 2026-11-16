using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.JavaScript;

/// <summary>
/// An index outside an array and a divisor of zero (#1367): where the value JavaScript reads outside
/// an array meets <c>ToInt32</c>, <c>ToUint32</c> or <c>ToNumber</c>, where a typed array is written
/// outside, and where integers are divided under a coercion, the imported code computes what node
/// computes; everything else keeps the trap and the caveat that announces it. The expected values are
/// node's (22.23.3).
/// </summary>
[TestFixture]
public sealed class JavaScriptOutOfRangeTests {

  private const string Source = """
    export function f(a, i) { return a[i] + 1; }
    export function main() { return f([1, 2], 5); }
    /** @param {Uint8Array} b @param {int32} i */
    export function low(b, i) { return b[i] | 0; }
    /** @param {Int32Array} b @param {int32} i */
    export function unsigned(b, i) { return b[i] >>> 0; }
    /** @param {Uint8Array} b @param {int32} i */
    export function word(b, i) { return (b[i] | (b[i + 1] << 8)) >>> 0; }
    /** @param {Uint8Array} b @param {number} i */
    export function half(b, i) { return b[i] * 0.5; }
    /** @param {Uint8Array} b @param {int32} i @param {int32} v */
    export function store(b, i, v) { b[i] = v; return b; }
    /** @param {Uint8Array} b @param {number} i @param {int32} v */
    export function storeAt(b, i, v) { b[i] = v; return b; }
    /** @param {Int32Array} to @param {Uint8Array} from @param {int32} i */
    export function copy(to, from, i) { to[0] = from[i]; return to; }
    /** @param {int32} a @param {int32} b */
    export function divide(a, b) { return (a / b) | 0; }
    /** @param {int32} a @param {int32} b */
    export function remainder(a, b) { return (a % b) | 0; }
    /** @param {uint32} a @param {uint32} b */
    export function divideUnsigned(a, b) { return (a / b) >>> 0; }
    /** @param {number} a @param {number} b */
    export function divideDoubles(a, b) { return (a / b) | 0; }
    """;

  private static readonly Lazy<ImportResult> _imported = new(() => JavaScriptFrontend.Import("m.mjs", Source));

  private static IrModule Module {
    get {
      Assert.That(_imported.Value.Diagnostics, Is.Empty, string.Join("; ", _imported.Value.Diagnostics));
      Assert.That(IrVerifier.Verify(_imported.Value.Module!), Is.Empty);
      return _imported.Value.Module!;
    }
  }

  private static readonly Lazy<GeneratedCSharp> _compiled = new(() => GeneratedCSharp.Compile(LegalizedCSharp.Emit(Module, CSharpProfile.Default)[0].Content, false));

  /// <summary>What the interpreter and the C# translation compute for a call; both are compared with node's outcome.</summary>
  private static void AssertOutcome(string function, object[] arguments, string expected) {
    Assert.That(PrototypeFixture.InterpretedOutcome(Module, new PrototypeCall(function, arguments)), Is.EqualTo(expected), "interpreted");
    Assert.That(PrototypeFixture.InvokedOutcome(_compiled.Value.Method(function), new PrototypeCall(function, arguments)), Is.EqualTo(expected), "C#");
  }

  /// <summary>The scenario: f([1,2], 5) reads undefined, and undefined + 1 is NaN.</summary>
  [Test]
  public void ReadPastTheEnd_PlusOne_IsNaN() => AssertOutcome("main", [], "NaN");

  /// <summary>Test design 2: the index at -1, 0, length - 1, length and length + 1, and at a fraction, of a read taken as a number.</summary>
  [TestCase(-1d, "NaN")]
  [TestCase(0d, "2")]
  [TestCase(1d, "3")]
  [TestCase(2d, "NaN")]
  [TestCase(3d, "NaN")]
  [TestCase(0.5d, "NaN")]
  [TestCase(double.NaN, "NaN")]
  [TestCase(-0d, "2")]
  public void ReadAsNumber_IsNaNOutside(double index, string expected) => AssertOutcome("f", [new[] { 1, 2 }, index], expected + " | [1,2]");

  /// <summary>Test design 2: a read under ToInt32 is 0 outside, under ToUint32 too, inside it is the element.</summary>
  [TestCase("low", -1, "0")]
  [TestCase("low", 0, "1")]
  [TestCase("low", 1, "2")]
  [TestCase("low", 2, "0")]
  [TestCase("low", 3, "0")]
  [TestCase("unsigned", 1, "4294967295")]
  [TestCase("unsigned", 2, "0")]
  [TestCase("unsigned", int.MinValue, "0")]
  public void ReadUnderACoercion_IsZeroOutside(string function, int index, string expected) {
    object array = function == "low" ? new byte[] { 1, 2 } : new[] { 1, -1 };
    AssertOutcome(function, [array, index], $"{expected} | {(function == "low" ? "[1,2]" : "[1,-1]")}");
  }

  /// <summary>Two reads of one expression: the second past the end is 0, as little32 of a short buffer is in node.</summary>
  [TestCase(0, "513")]
  [TestCase(1, "2")]
  [TestCase(-1, "256")]
  [TestCase(2, "0")]
  public void TwoReads_EachIsZeroOutside(int index, string expected) => AssertOutcome("word", [new byte[] { 1, 2 }, index], expected + " | [1,2]");

  /// <summary>A read at a double index taken as a number: the element where the index is the integer of one.</summary>
  [TestCase(1d, "1")]
  [TestCase(1.5d, "NaN")]
  [TestCase(-1d, "NaN")]
  [TestCase(1e10, "NaN")]
  public void ReadAtADouble_IsNaNOutside(double index, string expected) => AssertOutcome("half", [new byte[] { 1, 2 }, index], expected + " | [1,2]");

  /// <summary>Test design 2: a store into a typed array outside it is ignored, inside it wraps into the element.</summary>
  [TestCase(-1, "[0,0]")]
  [TestCase(0, "[257,0]")]
  [TestCase(1, "[0,257]")]
  [TestCase(2, "[0,0]")]
  [TestCase(3, "[0,0]")]
  public void StoreOutsideATypedArray_IsIgnored(int index, string expected) {
    var shown = expected.Replace("257", "1", StringComparison.Ordinal);
    AssertOutcome("store", [new byte[2], index, 257], $"{shown} | {shown}");
  }

  /// <summary>A store at a double index: ignored at a fraction and outside.</summary>
  [TestCase(1d, "[0,7]")]
  [TestCase(0.5d, "[0,0]")]
  [TestCase(double.NaN, "[0,0]")]
  [TestCase(2d, "[0,0]")]
  public void StoreAtADouble_IsIgnoredOutside(double index, string expected) => AssertOutcome("storeAt", [new byte[2], index, 7], $"{expected} | {expected}");

  /// <summary>An element read outside one typed array and stored into another is 0 there, as ToInt32 of undefined is.</summary>
  [TestCase(1, "[2]")]
  [TestCase(2, "[0]")]
  public void ReadStoredIntoATypedArray_IsZeroOutside(int index, string expected) => AssertOutcome("copy", [new[] { 9 }, new byte[] { 1, 2 }, index], $"{expected} | {expected} | [1,2]");

  /// <summary>Test design 3: a divisor of 0 and of 1, and the smallest value divided by -1, under ToInt32 and ToUint32.</summary>
  [TestCase("divide", 7, 0, "0")]
  [TestCase("divide", 7, 1, "7")]
  [TestCase("divide", -7, 2, "-3")]
  [TestCase("divide", int.MinValue, -1, "-2147483648")]
  [TestCase("divide", 7, -1, "-7")]
  [TestCase("remainder", 7, 0, "0")]
  [TestCase("remainder", -7, 2, "-1")]
  [TestCase("remainder", int.MinValue, -1, "0")]
  public void SignedQuotient_IsWhatTheDoubleBecomes(string function, int a, int b, string expected) => AssertOutcome(function, [a, b], expected);

  /// <summary>Test design 3: an unsigned divisor of 0 and 1.</summary>
  [TestCase(7u, 0u, "0")]
  [TestCase(7u, 1u, "7")]
  [TestCase(4294967295u, 2u, "2147483647")]
  public void UnsignedQuotient_IsWhatTheDoubleBecomes(uint a, uint b, string expected) => AssertOutcome("divideUnsigned", [a, b], expected);

  /// <summary>Test design 3: a quotient of doubles divided by 0, -0 and 1 is ToInt32 of the double, which never traps.</summary>
  [TestCase(7d, 0d, "0")]
  [TestCase(7d, -0d, "0")]
  [TestCase(7d, 1d, "7")]
  public void DoubleQuotient_UnderToInt32(double a, double b, string expected) => AssertOutcome("divideDoubles", [a, b], expected);

  /// <summary>Every read and write of the module is modeled: neither caveat is announced for it.</summary>
  [Test]
  public void ModeledAccesses_AnnounceNothing() {
    Assert.That(Module, Is.Not.Null);
    Assert.That(_imported.Value.Caveats.Select(caveat => caveat.Id), Has.No.Member(JavaScriptFrontend.OutOfRangeAccess).And.No.Member(JavaScriptFrontend.DivisionByZero));
  }

  /// <summary>
  /// What is not modeled keeps the trap and announces it: an element returned as it is, an element in
  /// a wrapped sum, a step of an element, a store into an Array, and a divisor tested before a dividend
  /// with an effect would be evaluated.
  /// </summary>
  [TestCase("/** @param {Uint8Array} b @param {int32} i */ export function f(b, i) { return b[i]; }", "js-oob-read")]
  [TestCase("/** @param {Uint8Array} b @param {int32} i */ export function f(b, i) { return (b[i] + 1) | 0; }", "js-oob-read")]
  [TestCase("/** @param {Uint8Array} b @param {int32} i */ export function f(b, i) { b[i]++; return b; }", "js-oob-read")]
  [TestCase("/** @param {int32[]} b @param {int32} i */ export function f(b, i) { b[i] = 1; return b; }", "js-oob-read")]
  [TestCase("function g() { return 7; } /** @param {int32} b */ export function f(b) { return (g() / b) | 0; }", "js-div-zero")]
  [TestCase("/** @param {uint32} a @param {uint32} b @returns {uint32} */ export function f(a, b) { return a % b; }", "js-div-zero")]
  public void Unmodeled_IsAnnounced(string source, string caveat) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(result.Caveats.Select(item => item.Id), Has.Member(caveat));
  }

  /// <summary>
  /// No test where the program proves the access inside: the variable of a loop up to the length of
  /// the array it indexes, a constant index into a constant table, and a constant divisor.
  /// </summary>
  [TestCase("/** @param {Uint8Array} b */ export function f(b) { let s = 0; for (let i = 0; i < b.length; i++) s = s ^ b[i]; return s; }")]
  [TestCase("/** @param {Uint8Array} b */ export function f(b) { for (let i = 1; i < b.length; i++) b[i] = b[i - 1] | 0; return b; }", false)]
  [TestCase("const T = [1, 2, 3]; export function f() { return T[2] | 0; }")]
  [TestCase("/** @param {int32} a */ export function f(a) { return ((a / 7) | 0) + ((a % 3) | 0); }")]
  public void ProvenAccess_HasNoTest(string source, bool proven = true) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    var function = result.Module!.Functions.Single(item => item.Name == "f");
    var tests = Count(function.Body, node => node is IrConditional or IrIf);
    Assert.That(tests, proven ? Is.Zero : Is.Positive);
  }

  /// <summary>
  /// An array that is no variable is evaluated once, before the index; a constant index into a
  /// constant table is tested where it is no element of it. The values are node's.
  /// </summary>
  [TestCase("function mk() { return new Uint8Array([4, 5]); } /** @param {int32} i */ export function f(i) { return mk()[i] | 0; }", 1, "5")]
  [TestCase("function mk() { return new Uint8Array([4, 5]); } /** @param {int32} i */ export function f(i) { return mk()[i] | 0; }", 2, "0")]
  [TestCase("const T = [1, 2, 3]; /** @param {int32} i */ export function f(i) { return (T[5] | 0) + i; }", 1, "1")]
  [TestCase("const T = [1, 2, 3]; /** @param {int32} i */ export function f(i) { return (T[1.5] | 0) + i; }", 1, "1")]
  [TestCase("/** @param {int32} i */ export function f(i) { const b = new Uint8Array(2); b[i] = 7; return b[1] | 0; }", 1, "7")]
  [TestCase("/** @param {int32} i */ export function f(i) { const b = new Uint8Array(2); b[i] = 7; return b[1] | 0; }", 2, "0")]
  public void Edge_IsWhatNodeComputes(string source, int argument, string expected) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    Assert.That(PrototypeFixture.InterpretedOutcome(result.Module!, new PrototypeCall("f", [argument])), Is.EqualTo(expected));
  }

  /// <summary>
  /// A table lookup at <c>x &amp; 255</c>, whose index the inference bounds to a uint8, is proven inside a
  /// table of 256 and tested in a table of 255.
  /// </summary>
  [TestCase(256, true)]
  [TestCase(255, false)]
  public void MaskedIndexIntoATable_IsProvenByItsLength(int length, bool proven) {
    var source = $"const T = [{string.Join(", ", Enumerable.Range(0, length))}]; /** @param {{int32}} x */ export function f(x) {{ return T[x & 255] | 0; }}";
    this.ProvenAccess_HasNoTest(source, proven);
  }

  /// <summary>A loop whose body writes its variable or its array proves nothing about the index, and nor does a loop of another shape.</summary>
  [TestCase("/** @param {Uint8Array} b */ export function f(b) { let s = 0; for (let i = 0; i < b.length; i++) { s = s ^ b[i]; i = i + 2; } return s; }")]
  [TestCase("/** @param {Uint8Array} b */ export function f(b) { let s = 0; for (let i = 0.5; i < b.length; i++) s = s ^ b[i]; return s; }")]
  [TestCase("/** @param {Uint8Array} b */ export function f(b) { let s = 0; for (let i = 0; i < b.length; i--) s = s ^ b[i]; return s; }")]
  [TestCase("/** @param {Uint8Array} b @param {Uint8Array} c */ export function f(b, c) { let s = 0; for (let i = 0; i < c.length; i++) s = s ^ b[i]; return s; }")]
  [TestCase("/** @param {Uint8Array} b */ export function f(b) { let s = 0; for (let i = 0; i < b.length; i++) { s = s ^ b[i]; } for (let i = 0; i < 3; i++) s = s ^ b[i]; return s; }")]
  [TestCase("/** @param {Uint8Array} b @param {Uint8Array} c */ export function f(b, c) { let s = 0; for (let i = 0; i < b.length; i++) { s = s ^ b[i]; b = c; } return s; }")]
  [TestCase("/** @param {Uint8Array} b */ export function f(b) { let s = 0; for (let i = 0; i < b.length; i += 1) s = s ^ b[i]; return s; }")]
  [TestCase("/** @param {Uint8Array} b */ export function f(b) { let s = 0; for (let i = -1; i < b.length; i++) s = s ^ b[i + 1]; return s; }")]
  public void UnprovenLoop_IsTested(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    var function = result.Module!.Functions.Single(item => item.Name == "f");
    Assert.That(Count(function.Body, node => node is IrConditional), Is.Positive);
  }

  /// <summary>The nodes of a tree that match, found through every record-typed property.</summary>
  private static int Count(object node, Func<object, bool> matches) {
    var seen = 0;
    var pending = new Stack<object>([node]);
    while (pending.TryPop(out var item)) {
      if (matches(item))
        ++seen;

      foreach (var property in item.GetType().GetProperties()) {
        if (property.GetIndexParameters().Length > 0)
          continue;

        var value = property.GetValue(item);
        if (value is IrExpr or IrStmt)
          pending.Push(value);
        else if (value is System.Collections.IEnumerable items and not string)
          foreach (var child in items.OfType<object>().Where(child => child is IrExpr or IrStmt))
            pending.Push(child);
      }
    }

    return seen;
  }

}
