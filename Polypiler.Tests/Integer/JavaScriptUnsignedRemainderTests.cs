using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Integer;

/// <summary>
/// The remainder of two integers that are never negative (#1375), as Adler's <c>y % this.modulo</c> of
/// two uint32 values: an integer below the divisor, of the type that holds both, which a function
/// stated uint32 takes. The expected values are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptUnsignedRemainderTests {

  private const string Source = """
    /** @param {uint32} a @param {uint32} b @returns {uint32} */
    function sub(a, b) { return (a - b) >>> 0; }
    /** @param {uint32} y @param {uint32} m @returns {uint32} */
    export function f(y, m) { const r = y % m; return sub(r, 0); }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>Scenario and test design 2-3: the dividend and the divisor at their bounds give node's remainder, of the type uint32.</summary>
  [TestCase(4294967290u, 65521u, 219u)]
  [TestCase(0u, 7u, 0u)]
  [TestCase(4294967295u, 1u, 0u)]
  [TestCase(5u, 4294967295u, 5u)]
  [TestCase(4294967295u, 4294967295u, 0u)]
  public void RemainderOfTwoUint32_IsTheUint32ItIs(uint y, uint m, uint expected) {
    var module = Import(Source);
    var local = module.Functions.Single(function => function.Name == "f").Locals.Single(item => item.Name == "r");
    Assert.That(local.Type, Is.EqualTo(IrType.U32));
    Assert.That(IrInterpreter.Call(module, "f", [y, m]), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("f").Invoke(null, [y, m]), Is.EqualTo(expected), text);
  }

  /// <summary>Test design 4: a divisor of zero, NaN in JavaScript, traps with divide by zero, as the caveat js-div-zero says.</summary>
  [Test]
  public void DivisorZero_TrapsWithTheCaveat() {
    var result = JavaScriptFrontend.Import("m.mjs", Source);
    Assert.That(result.Caveats.Select(caveat => caveat.Id), Has.Member(JavaScriptFrontend.DivisionByZero));
    Assert.That(() => IrInterpreter.Call(result.Module!, "f", [5u, 0u]), Throws.Exception.With.Message.Contains("divide by zero"));
  }

  /// <summary>Test design 1: a dividend that may be negative keeps the remainder of doubles.</summary>
  [Test]
  public void SignedDividend_StaysADouble() {
    var module = Import("/** @param {int32} y @param {uint32} m */ export function f(y, m) { const r = y % m; return r; }");
    Assert.That(module.Functions.Single().Locals.Single(item => item.Name == "r").Type, Is.EqualTo(IrType.F64));
    Assert.That(IrInterpreter.Call(module, "f", [-7, 3u]), Is.EqualTo(-1.0));
  }

}
