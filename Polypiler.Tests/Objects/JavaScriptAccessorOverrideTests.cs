using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Objects;

/// <summary>
/// Getters and setters a derived class declares again (#1368), as every block cipher of the Cipher
/// corpus declares its own <c>key</c> over the one of <c>IBlockCipherInstance</c>: they override the
/// accessors of the base class, and <c>this.key = …</c> of a constructor calls the setter. The
/// expected values are node's on the same source.
/// </summary>
[TestFixture]
public sealed class JavaScriptAccessorOverrideTests {

  private const string Source = """
    class B {
      constructor() { this._key = null; this.size = 0; }
      /** @param {uint8[]|null} k */
      set key(k) { this._key = k; }
      /** @returns {uint8[]|null} */
      get key() { return this._key; }
      get label() { return 1; }
      set level(v) { this.size = v; }
    }
    class D extends B {
      constructor() { super(); this.key = null; }
      /** @param {uint8[]|null} k */
      set key(k) { this._key = k ? [k[2], k[1], k[0]] : null; }
      /** @returns {uint8[]|null} */
      get key() { return this._key; }
      get label() { return 2; }
      set level(v) { this.size = v * 10; }
    }
    class E extends D {
      get label() { return 3; }
    }
    class P extends B {
      constructor() { super(); this.key = [9, 8, 7]; this.size = 4; }
    }
    export function scenario() {
      const d = new D();
      d.key = [1, 2, 3];
      const k = d.key;
      return k[0] * 100 + k[1] * 10 + k[2];
    }
    export function kinds() {
      const d = new D();
      d.level = 5;
      const asBase = new E();
      const p = new P();
      return d.label + asBase.label * 10 + d.size * 100 + p.key[0] * 10000 + p.size * 100000;
    }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static string Refusal(string source) => JavaScriptFrontend.Import("m.mjs", source).Diagnostics.Single().Message;

  /// <summary>Scenario: a derived setter that stores the key reversed overrides the base class's, so that key reads [3, 2, 1].</summary>
  [Test]
  public void DerivedSetter_OverridesTheBaseClasses() {
    var module = Import(Source);
    Assert.That(IrInterpreter.Call(module, "scenario", []), Is.EqualTo(321));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(text, Does.Contain("public virtual byte[] key").And.Contain("public override byte[] key"));
    Assert.That(GeneratedCSharp.Compile(text, false).Method("scenario").Invoke(null, []), Is.EqualTo(321), text);
  }

  /// <summary>
  /// Test design 1-3: a getter, a setter and a pair override; an assignment through this of a derived
  /// constructor calls the inherited setter, and of an inherited field stores it; two levels down the
  /// nearest accessor wins.
  /// </summary>
  [Test]
  public void EveryKind_OverridesAsJavaScriptDoes() {
    var module = Import(Source);
    var classes = module.Types.OfType<IrClassType>().ToDictionary(type => type.Name);
    Assert.That(classes["P"].Fields, Is.Empty, "this.key of P is the setter of B, no field of its own");
    Assert.That(classes["D"].Methods.Where(method => method.Kind != IrMethodKind.Method).Select(method => method.Dispatch).Distinct(), Is.EqualTo(new[] { IrDispatch.Override }));
    Assert.That(IrInterpreter.Call(module, "kinds", []), Is.EqualTo(495032));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("kinds").Invoke(null, []), Is.EqualTo(495032), text);
    foreach (var profile in new[] { JavaScriptProfile.Default, JavaScriptProfile.Parse("es5", "cjs") }) {
      var script = LegalizedJavaScript.Emit(module, profile).Single().Content;
      Assert.That(GeneratedJavaScript.Problems(script, profile), Is.Empty, script);
    }
  }

  /// <summary>Test design 4: a setter alone over a pair of its base class, and a getter alone, hide the other accessor.</summary>
  [TestCase("set key(k) { this._k = k; }")]
  [TestCase("get key() { return 1; }")]
  public void OneAccessorOfAPair_IsRefused(string declared) {
    Assert.That(Refusal($$"""
      class B { constructor() { this._k = 0; } set key(k) { this._k = k; } get key() { return this._k; } }
      class D extends B { {{declared}} }
      export function f() { return new D() instanceof B ? 1 : 0; }
      """), Is.EqualTo("'a getter or setter of key that hides the other accessor of its base class' is not supported yet"));
  }

  /// <summary>An accessor of the key of a field of a base class, and a method of the key of an accessor, stay what they were.</summary>
  [TestCase("class B { constructor() { this.key = 0; } } class D extends B { get key() { return 1; } }", "'the member key of a base class declared again' is not supported yet")]
  [TestCase("class B { get key() { return 0; } } class D extends B { key() { return 1; } }", "'the member key of a base class declared again' is not supported yet")]
  [TestCase("class B { key() { return 0; } } class D extends B { get key() { return 1; } }", "'the member key of a base class declared again' is not supported yet")]
  public void AccessorOverAnotherKind_IsRefused(string classes, string message)
    => Assert.That(Refusal(classes + " export function f() { return new D() instanceof B ? 1 : 0; }"), Is.EqualTo(message));

  /// <summary>A setter a base constructor calls that a derived class overrides runs before the derived class has its fields in JavaScript: refused.</summary>
  [Test]
  public void OverriddenSetterDuringBaseConstruction_IsRefused()
    => Assert.That(Refusal("""
      class B { constructor() { this._k = 0; this.key = 1; } set key(k) { this._k = k; } get key() { return this._k; } }
      class D extends B { constructor() { super(); this.extra = 2; } set key(k) { this._k = k + this.extra; } get key() { return this._k; } }
      export function f() { return new D().key; }
      """), Is.EqualTo("'a call of an overridden method on 'this' during construction' is not supported yet"));

}
