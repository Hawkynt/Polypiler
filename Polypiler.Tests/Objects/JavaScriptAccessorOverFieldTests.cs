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
/// A getter and a setter a derived class declares over a field of its base class (#1402), as the AEAD
/// instances of the Cipher corpus declare <c>aad</c> over the field <c>IAeadInstance</c> assigns: the
/// field becomes a virtual property whose base implementation is the field, the accessors override it,
/// and every read and store through any reference - the base constructor's among them - runs the
/// accessor of the object's class. The expected values are node's on the same source.
/// </summary>
[TestFixture]
public sealed class JavaScriptAccessorOverFieldTests {

  private const string Source = """
    let stores = 0;
    class B {
      constructor() { this.aad = []; this.size = 1; }
      count() { return this.aad.length; }
    }
    class D extends B {
      constructor() { super(); this._aad = []; }
      /** @param {int32[]} v */
      set aad(v) { stores++; this._aad = v.slice(); }
      get aad() { return this._aad.slice(); }
    }
    class E extends D {
      constructor() { super(); this.aad = [4, 5, 6]; }
    }
    export function scenario() {
      const before = stores;
      const d = new D();
      d.aad = [1, 2];
      const a = d.aad;
      return (stores - before) * 100 + a[0] * 10 + a[1];
    }
    export function kinds() {
      const before = stores;
      const b = new B();
      b.aad = [3];
      /** @type {B} */
      let x = b;
      const first = x.count();
      x = new E();
      x.aad = [7, 8];
      return first * 1000 + x.count() * 100 + (stores - before) * 10 + x.size;
    }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static string Refusal(string source) => JavaScriptFrontend.Import("m.mjs", source).Diagnostics.First().Message;

  private static GeneratedCSharp Both(IrModule module) {
    foreach (var profile in new[] { JavaScriptProfile.Default, JavaScriptProfile.Parse("es2020", "esm"), JavaScriptProfile.Parse("es5", "cjs") }) {
      var script = LegalizedJavaScript.Emit(module, profile).Single().Content;
      Assert.That(GeneratedJavaScript.Problems(script, profile), Is.Empty, script);
    }

    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    return GeneratedCSharp.Compile(text, false);
  }

  /// <summary>Scenario: the setter copies what is set and counts the stores - the base constructor's and the one from outside: 2 stores and [1, 2].</summary>
  [Test]
  public void SetterOverABaseField_RunsForEveryStore() {
    var module = Import(Source);
    Assert.That(IrInterpreter.Call(module, "scenario", []), Is.EqualTo(212));
    Assert.That(Both(module).Method("scenario").Invoke(null, []), Is.EqualTo(212));
  }

  /// <summary>
  /// Test design 1 and 3: the base field is a virtual property of the base class, backed by a private
  /// field; a store in the base constructor, in a derived constructor and from outside, and a read
  /// through a reference of the base class, run the accessor of the object's class.
  /// </summary>
  [Test]
  public void EveryStoreAndRead_RunsTheAccessorOfTheObjectsClass() {
    var module = Import(Source);
    var classes = module.Types.OfType<IrClassType>().ToDictionary(type => type.Name);
    Assert.That(classes["B"].Properties.Single().Getter!.Dispatch, Is.EqualTo(IrDispatch.Virtual));
    Assert.That(classes["B"].Fields.Single(field => field.Name != "size").Visibility, Is.EqualTo(IrVisibility.Private));
    Assert.That(classes["D"].Properties.Single().Setter!.Dispatch, Is.EqualTo(IrDispatch.Override));
    // b: a store in its constructor and one outside, through the field, which runs no setter (1 element);
    // e: the base constructor's store, its own and the one outside run D's setter (3), read through D's getter (2 elements).
    Assert.That(IrInterpreter.Call(module, "kinds", []), Is.EqualTo(1231));
    var compiled = Both(module);
    Assert.That(compiled.Method("kinds").Invoke(null, []), Is.EqualTo(1231));
    Assert.That(LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content, Does.Contain("public virtual").And.Contain("public override"));
  }

  /// <summary>Test design 2: a getter alone and a setter alone over a base field, and an accessor over a field the base class declares as a class field, are refused naming the key.</summary>
  [TestCase("get aad() { return [1]; }", "class B { constructor() { this.aad = []; } }", "'a getter of aad without a setter over a field of its base class' is not supported yet")]
  [TestCase("set aad(v) { this._a = v; }", "class B { constructor() { this.aad = []; } }", "'a setter of aad without a getter over a field of its base class' is not supported yet")]
  [TestCase("get aad() { return [1]; } set aad(v) { }", "class B { aad = []; constructor() { } }", "'an accessor of aad over a class field of its base class' is not supported yet")]
  public void OneAccessorOrAClassField_IsRefused(string accessors, string @base, string message)
    => Assert.That(Refusal(@base + $$"""
      class D extends B { {{accessors}} }
      export function f() { return new D() instanceof B ? 1 : 0; }
      """), Is.EqualTo(message));

  /// <summary>Scenario and test design 4: a derived accessor over a base method is refused naming the key.</summary>
  [Test]
  public void AccessorOverAMethod_IsRefused()
    => Assert.That(Refusal("""
      class B { m() { return 1; } }
      class D extends B { get m() { return 2; } }
      export function f() { return new D() instanceof B ? 1 : 0; }
      """), Is.EqualTo("'the member m of a base class declared again' is not supported yet"));

  /// <summary>A setter the base constructor runs that reads a field of its class, which JavaScript has not yet given a value then, is the construction refusal.</summary>
  [TestCase("/** @param {int32[]} v */ set aad(v) { this._aad = v; this.n = this.n + 1; }")]
  [TestCase("/** @param {int32[]} v */ set aad(v) { this._aad = v; this.note(); }")]
  public void SetterThatReadsAFieldDuringBaseConstruction_IsRefused(string setter)
    => Assert.That(Refusal($$"""
      class B { constructor() { this.aad = []; } }
      class D extends B {
        constructor() { super(); this._aad = []; this.n = 0; }
        {{setter}}
        get aad() { return this._aad; }
        note() { this.n = this.n + 2; }
      }
      export function f() { const d = new D(); d.aad = [1]; return d.aad.length + d.n; }
      """), Is.EqualTo("'a call of an overridden method on 'this' during construction' is not supported yet"));

  /// <summary>A field of the derived class the setter writes during base construction that the derived constructor does not write again first is the construction refusal.</summary>
  [TestCase("constructor() { super(); this.n = this._aad.length; this._aad = []; }")]
  [TestCase("constructor() { super(); if (this.size > 0) { this._aad = []; } }")]
  public void SetterWriteThatTheDerivedConstructorDoesNotRenew_IsRefused(string constructor)
    => Assert.That(Refusal($$"""
      class B { constructor() { this.size = 1; this.aad = []; } }
      class D extends B {
        {{constructor}}
        /** @param {int32[]} v */
        set aad(v) { this._aad = v; }
        get aad() { return this._aad; }
      }
      export function f() { const d = new D(); d.aad = [1]; return d.aad.length + d.size; }
      """), Is.EqualTo("'a call of an overridden method on 'this' during construction' is not supported yet"));

  /// <summary>Object.keys of an object whose class has a field derived classes declare accessors over is refused: the key is an own key of an object of the base class only.</summary>
  [Test]
  public void ObjectKeysOverAVirtualField_IsRefused()
    => Assert.That(Refusal(Source + "export function keys() { return Object.keys(new B()).length; }"), Is.EqualTo("'Object.keys of B, whose keys are not known' is not supported yet"));

  /// <summary>A compound assignment and a step of a property over a base field read through the getter and store through the setter, or are refused by name.</summary>
  [Test]
  public void CompoundAssignmentAndStep_OfAnAccessorOverAField() {
    var module = Import("""
      class B { constructor() { this.n = 1; } }
      class D extends B {
        constructor() { super(); this._n = 0; }
        set n(v) { this._n = v * 2; }
        get n() { return this._n + 1; }
      }
      export function f() { const d = new D(); d.n += 3; return d.n; }
      """);
    // node: _n = 2 in the base constructor, 0 in D's; d.n is 1; d.n += 3 stores 4 as 8; d.n is 9.
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(9));
    Assert.That(Both(module).Method("f").Invoke(null, []), Is.EqualTo(9));
    Assert.That(Refusal("""
      class B { constructor() { this.n = 1; } }
      class D extends B { constructor() { super(); this._n = 0; } set n(v) { this._n = v; } get n() { return this._n; } }
      export function f() { const d = new D(); d.n++; return d.n; }
      """), Is.EqualTo("'++ of d.n, which is no field' is not supported yet"));
  }

}
