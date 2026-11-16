using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.JavaScript;

/// <summary>
/// A parameter JSDoc states as an integer of up to 32 bits that the function reads only under
/// <c>ToInt32</c> or <c>ToUint32</c>, as OpCodes' <c>Or32(a, b)</c> does with <c>(a | b) &gt;&gt;&gt; 0</c>:
/// those coercions see the low 32 bits of any integer, so a call may pass the other 32-bit integer,
/// wrapped; a narrower one of a function only the file calls is widened to its 32 bits. The expected
/// values are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptCoercedParameterTests {

  private const string Source = """
    /** @param {int32} a @param {int32} b @returns {uint32} */
    function or32(a, b) { return (a | b) >>> 0; }
    /** @param {uint16} w @returns {uint8[]} */
    function unpack16(w) { w = w & 0xFFFF; return [(w >>> 8) & 0xFF, w & 0xFF]; }
    /** @param {uint32} x */
    export function coerced(x) { const word = or32(x, 0x80000000); const bytes = unpack16(word); return bytes[0] * 256 + bytes[1] + (word >>> 31) * 100000; }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>Happy path and boundaries: uint32 values at 0x1234 and 2^32 - 1 through an int32 and a uint16 parameter.</summary>
  [TestCase(0x1234u, 104660)]
  [TestCase(0xFFFFFFFFu, 165535)]
  [TestCase(0u, 100000)]
  public void Uint32PassedWhereTheFunctionCoerces_KeepsItsBits(uint x, double expected) {
    var module = Import(Source);
    Assert.That(module.Functions.Single(function => function.Name == "unpack16").Parameters.Single().Type, Is.EqualTo(IrType.U32), "widened: only the file calls it");
    Assert.That(module.Functions.Single(function => function.Name == "or32").Parameters.Select(parameter => parameter.Type), Is.EqualTo(new[] { IrType.I32, IrType.I32 }));
    Assert.That(IrInterpreter.Call(module, "coerced", [x]), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("coerced").Invoke(null, [x]), Is.EqualTo(expected), text);
  }

  /// <summary>Equivalence: a function called from outside keeps the narrow contract it states.</summary>
  [Test]
  public void ExportedFunction_KeepsItsStatedType() {
    var module = Import("/** @param {uint16} w @returns {int32} */ export function f(w) { return w & 0xFF; }");
    Assert.That(module.Functions.Single().Parameters.Single().Type, Is.EqualTo(IrType.U16));
  }

  /// <summary>Error path: a parameter read without a coercion keeps its contract, which a wider argument breaks.</summary>
  [Test]
  public void ParameterReadAsItIs_RefusesAWiderArgument() {
    var result = JavaScriptFrontend.Import("m.mjs", """
      /** @param {int32} a @returns {int32} */
      function plus(a) { return a + 1 | 0; }
      /** @param {uint32} x */
      export function f(x) { return plus(x); }
      """);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.EqualTo("'uint32 where int32 is expected' is not supported yet"));
  }

}
