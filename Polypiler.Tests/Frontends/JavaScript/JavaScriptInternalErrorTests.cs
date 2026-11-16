using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.JavaScript;

/// <summary>
/// The two internal compiler errors the Cipher corpus reached (#1377), each on the minimal source cut
/// from its files: the signature of a function value whose result is a record was made while the shapes
/// were not named yet (asymmetric/mayo.js, asymmetric/rainbow.js, block/ff.js), and the reason of a
/// parameter stated <c>T|null</c> spelled an optional type the spelling did not know (stream/xchacha20.js),
/// as a setter called before it was lowered had no parameter yet. The expected values are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptInternalErrorTests {

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.None.EqualTo("PLY0001"));
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>A function used as a value whose result is a record: its signature names the struct, made once the shapes are named.</summary>
  [Test]
  public void FunctionValueOfARecord_IsSignedAfterTheShapesAreNamed()
    => Assert.That(IrInterpreter.Call(Import("function point(x) { return { x: x, y: 2 }; } export function f() { const make = point; const p = make(3); return p.x + p.y; }"), "f", []), Is.EqualTo(5));

  /// <summary>A parameter stated uint32|null: its reason is spelled as JSDoc writes it.</summary>
  [TestCase(null, 0u)]
  [TestCase(5u, 5u)]
  public void StatedOptionalParameter_IsSpelled(uint? given, uint expected) {
    var result = JavaScriptFrontend.Import("m.mjs", "/** @param {uint32|null} value */ export function g(value) { return value === null ? 0 : value; }");
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(result.Explanations.Single(explanation => explanation.Role == "parameter").Evidence, Is.EqualTo("@param {uint32|null}"));
    Assert.That(IrInterpreter.Call(result.Module!, "g", [given!]), Is.EqualTo(expected));
  }

  /// <summary>A setter a constructor calls before the setter itself is lowered takes the value as the type it will have.</summary>
  [Test]
  public void SetterCalledBeforeItIsLowered_TakesItsType()
    => Assert.That(IrInterpreter.Call(Import("""
      class D {
        constructor() { this._k = 0; this.key = 3; }
        /** @param {int32} k */
        set key(k) { this._k = k * 2; }
        get key() { return this._k; }
      }
      export function f() { return new D().key; }
      """), "f", []), Is.EqualTo(6));

}
