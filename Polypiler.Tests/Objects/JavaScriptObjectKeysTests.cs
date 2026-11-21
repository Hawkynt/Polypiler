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
/// <c>Object.keys</c> of an object of a known shape (#1373), as the Cipher framework copies the extra
/// fields of a test vector: the keys are those the constructors create, base class first, so a loop
/// over them is its body once per key, with <c>o[key]</c> the field; as a value it is the Array of the
/// keys. The expected values are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptObjectKeysTests {

  private const string Source = """
    class Base { constructor() { this.a = 1; } }
    class Item extends Base { constructor() { super(); this.b = 2; this.c = 3; } get d() { return 4; } }
    class Empty { }
    export function scenario() { const item = new Item(); return Object.keys(item).join(","); }
    export function loops() {
      const item = new Item();
      const copy = new Item();
      copy.a = 10; copy.b = 20; copy.c = 30;
      let total = 0;
      Object.keys(copy).forEach(key => {
        if (!['c'].includes(key))
          item[key] = copy[key];
      });
      for (const key of Object.keys(item))
        total = total * 100 + item[key];
      const record = { x: 5, y: 6 };
      Object.keys(record).forEach(key => { total += record[key]; });
      Object.keys(new Empty()).forEach(key => { total += 1000; });
      return total;
    }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static string Refusal(string source) => JavaScriptFrontend.Import("m.mjs", source).Diagnostics.Single().Message;

  /// <summary>Scenario: the keys of an object of a class with the fields a, b and c, the getter d no own key, are 'a,b,c'.</summary>
  [Test]
  public void KeysOfAnObjectOfAClass_AreItsFields() {
    var module = Import(Source);
    Assert.That(IrInterpreter.Call(module, "scenario", []), Is.EqualTo("a,b,c"));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("scenario").Invoke(null, []), Is.EqualTo("a,b,c"), text);
  }

  /// <summary>Test design 1-3: a class instance and a record; no key, one and several; forEach and for…of alike.</summary>
  [Test]
  public void Loops_AreWrittenOutOncePerKey() {
    var module = Import(Source);
    Assert.That(IrInterpreter.Call(module, "loops", []), Is.EqualTo(102014));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("loops").Invoke(null, []), Is.EqualTo(102014), text);
    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
    Assert.That(script, Does.Not.Contain("Object.keys"), "the keys are known when the module is imported");
  }

  /// <summary>Test design 4 and what stays out: a store into a key the target lacks, a jump out of the loop, an object of no known shape, an operand with an effect.</summary>
  [TestCase("class A { constructor() { this.x = 1; this.y = 2; } } class B { constructor() { this.x = 0; } } export function f() { const a = new A(); let b = new B(); Object.keys(a).forEach(key => { b[key] = a[key]; }); return b.x; }",
    "'the property y added to B outside its constructor' is not supported yet")]
  [TestCase("class A { constructor() { this.x = 1; } } export function f() { const a = new A(); for (const key of Object.keys(a)) { if (a[key] > 0) break; } return 0; }",
    "'a return, a break or a continue in a loop over Object.keys' is not supported yet")]
  [TestCase("/** @param {int32[]} a */ export function f(a) { let n = 0; Object.keys(a).forEach(key => { n += 1; }); return n; }",
    "'Object.keys of int32[], whose keys are not known' is not supported yet")]
  [TestCase("class A { constructor() { this.x = 1; } } export function f() { return Object.keys(new A()).length; }",
    "'Object.keys of an operand with an effect' is not supported yet")]
  [TestCase("class A { constructor() { this.x = 1; } } export function f() { const a = new A(); return a[Object.keys(a)[0]]; }",
    "'a computed member name' is not supported yet")]
  public void OutOfScope_IsRefusedByName(string source, string message) => Assert.That(Refusal(source), Is.EqualTo(message));

}
