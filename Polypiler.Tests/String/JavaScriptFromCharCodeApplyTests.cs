using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.String;

/// <summary>
/// <c>String.fromCharCode.apply(null, a)</c> and <c>String.fromCharCode(...a)</c> (#1400): the string
/// of one code unit per element, each <c>ToUint16</c> of it, for an Array, a list and a typed array;
/// the expected strings are what node 22 printed for the same calls.
/// </summary>
[TestFixture]
public sealed class JavaScriptFromCharCodeApplyTests {

  private static IrModule Imported(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Not.Null, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static string Refused(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Null);
    return string.Join("; ", result.Diagnostics);
  }

  private const string Apply = "/** @param {number[]} a @returns {string} */ export function f(a) { return String.fromCharCode.apply(null, a); }";

  /// <summary>Scenario: bytes to characters, translated to C# and run.</summary>
  [Test]
  public void BytesToCharacters_TranslatedToCSharp_AsNodeDoes() {
    var module = Imported(Apply);
    var build = GeneratedCSharp.Compile(LegalizedCSharp.Emit(module)[0].Content, true);
    Assert.That(build.Method("f").Invoke(null, [new double[] { 72, 105, 65601 }]), Is.EqualTo("HiA"));
    Assert.That(IrInterpreter.Call(module, "f", [new double[] { 72, 105, 65601 }]), Is.EqualTo("HiA"));
  }

  /// <summary>Test design 1 to 3: bytes, the empty array and one element, and the values ToUint16 reduces.</summary>
  [TestCase(new double[] { 72, 105 }, "Hi")]
  [TestCase(new double[0], "")]
  [TestCase(new double[] { 97 }, "a")]
  [TestCase(new double[] { 65535, 65536, -1, 65.9, -65.9, 1e10, double.NaN }, "\uFFFF\u0000\uFFFFA\uFFBF\uE400\u0000")]
  public void Codes_AreToUint16OfEachElement(double[] codes, string expected) {
    var module = Imported(Apply);
    Assert.That(IrInterpreter.Call(module, "f", [codes]), Is.EqualTo(expected));
    var build = GeneratedCSharp.Compile(LegalizedCSharp.Emit(module)[0].Content, true);
    Assert.That(build.Method("f").Invoke(null, [codes]), Is.EqualTo(expected));
  }

  /// <summary>Test design 4: the receivers of the codes - a typed array, a list, a spread, undefined as the receiver.</summary>
  [Test]
  public void Receivers_OfTheCodes_AsNodeDoes() {
    var module = Imported("""
      /** @param {Uint8Array} b @returns {string} */
      export function bytes(b) { return String.fromCharCode.apply(undefined, b); }
      /** @param {Uint16Array} u @returns {string} */
      export function units(u) { return String.fromCharCode.apply(null, u); }
      /** @param {Int32Array} w @returns {string} */
      export function words(w) { return String.fromCharCode(...w); }
      /** @param {int} n @returns {string} */
      export function listed(n) { const l = []; for (let i = 0; i < n; i++) l.push(65 + i); return String.fromCharCode.apply(null, l); }
      /** @param {number[]} a @returns {string} */
      export function spread(a) { return String.fromCharCode(66, ...a, 67); }
      """);
    Assert.That(IrInterpreter.Call(module, "bytes", [new byte[] { 255, 0, 72 }]), Is.EqualTo("\u00FF\u0000H"));
    Assert.That(IrInterpreter.Call(module, "units", [new ushort[] { 0xD83D, 0xDE00 }]), Is.EqualTo("\uD83D\uDE00"));
    Assert.That(IrInterpreter.Call(module, "words", [new int[] { -1, 65601 }]), Is.EqualTo("\uFFFFA"));
    Assert.That(IrInterpreter.Call(module, "listed", [3]), Is.EqualTo("ABC"));
    Assert.That(IrInterpreter.Call(module, "spread", [new double[] { 97 }]), Is.EqualTo("BaC"));
    // A u16 array needs no function of the module; the others one per type of the codes.
    var calls = module.Functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrCall>().Select(call => call.Function).Where(name => name.StartsWith("$fromCharCodes", System.StringComparison.Ordinal));
    Assert.That(calls.Distinct().Count(), Is.EqualTo(4));
    var build = GeneratedCSharp.Compile(LegalizedCSharp.Emit(module)[0].Content, true);
    Assert.That(build.Method("bytes").Invoke(null, [new byte[] { 255, 0, 72 }]), Is.EqualTo("\u00FF\u0000H"));
    Assert.That(build.Method("listed").Invoke(null, [3]), Is.EqualTo("ABC"));
  }

  /// <summary>Scenario and test design 5: apply of another function stays refused as the call of it.</summary>
  [TestCase("return Math.max.apply(null, a);", "a call of Math.max.apply")]
  [TestCase("return String.fromCharCode.apply(a, a);", "String.fromCharCode.apply with a receiver other than null or undefined")]
  [TestCase("return String.fromCharCode.apply(null);", "String.fromCharCode.apply with 1 arguments")]
  [TestCase("return String.fromCharCode.apply(null, \"ab\");", "String.fromCharCode of the elements of string")]
  public void OtherApply_IsRefusedByName(string body, string what)
    => Assert.That(Refused($"/** @param {{number[]}} a */ export function f(a) {{ {body} }}"), Does.Contain($"PLY1102: '{what}"));

}
