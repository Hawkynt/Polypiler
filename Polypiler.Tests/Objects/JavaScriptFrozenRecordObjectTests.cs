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
/// Frozen objects of value records (#1371), as the Cipher framework keeps its metadata:
/// <c>Object.freeze({ HASH: { name: 'Hash Functions', … }, … })</c>. Such an object is a class of
/// read-only fields per shape, and a read of a member a new object of its values - which nothing can
/// tell from the member, as it is never written and never compared by identity. The expected values
/// are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptFrozenRecordObjectTests {

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static string Refusal(string source) => JavaScriptFrontend.Import("m.mjs", source).Diagnostics.Single().Message;

  private static void Agrees(IrModule module, double expected) {
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("f").Invoke(null, []), Is.EqualTo(expected), text);
    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
  }

  /// <summary>Scenario: the name of a category stored into a field is 'Hash Functions'.</summary>
  [Test]
  public void CategoryReadThroughTheFieldThatHoldsIt_HasItsName() {
    var module = Import("""
      const Category = Object.freeze({ HASH: { name: 'Hash Functions', level: 2 } });
      class Algorithm { constructor() { this.category = null; } }
      export function f() { const a = new Algorithm(); a.category = Category.HASH; return a.category.name.length; }
      """);
    Agrees(module, "Hash Functions".Length);
    var record = module.Types.OfType<IrClassType>().Single(type => type.Name == "Category");
    Assert.That(record.Fields.Select(field => (field.Name, field.Type.ToString(), field.IsReadOnly)), Is.EqualTo(new[] { ("name", "str", true), ("level", "i32", true) }));
  }

  /// <summary>Test design 1-3: strings, numbers and truth values kept; a field store, a key read and an argument; one member and seventeen.</summary>
  [Test]
  public void EveryValueAndUse_IsKept() {
    var members = string.Join(", ", Enumerable.Range(0, 17).Select(index => $"M{index}: {{ name: 'n{index}', level: {index - 3}, on: {(index % 2 == 0 ? "true" : "false")} }}"));
    Agrees(Import($$"""
      const One = Object.freeze({ ONLY: { text: 'x' } });
      const Many = Object.freeze({ {{members}} });
      class Holder { constructor() { this.kind = null; } }
      function level(kind) { return kind.level; }
      export function f() {
        const h = new Holder();
        h.kind = Many.M16;
        return level(Many.M2) * 1000 + h.kind.level * 10 + (Many.M4.on ? 1 : 0) + One.ONLY.text.length * 100000;
      }
      """), 100000 + -1 * 1000 + 13 * 10 + 1);
  }

  /// <summary>Test design 4: members of two shapes, a comparison by identity, a write and a member that is none are refused by name.</summary>
  [TestCase("const C = Object.freeze({ A: { name: 'a' }, B: { title: 'b' } }); export function f() { const a = C.A; return 0; }",
    "'the members of the frozen object C, which have different keys' is not supported yet")]
  [TestCase("const C = Object.freeze({ A: { name: 'a' }, B: { name: 'b' } }); export function f() { return C.A === C.B ? 1 : 0; }",
    "'a comparison of members of the frozen object C by identity' is not supported yet")]
  [TestCase("const C = Object.freeze({ A: { name: 'a' } }); export function f() { const a = C.A; a.name = 'b'; return 0; }",
    "'a write into a member of the frozen object C' is not supported yet")]
  [TestCase("const C = Object.freeze({ A: { name: 'a' } }); export function f() { const a = C.Z; return 0; }",
    "'C.Z, which is no member of the frozen object C' is not supported yet")]
  [TestCase("const C = Object.freeze({ A: { name: 'a' } }); export function f() { const a = C['A']; return 0; }",
    "'a computed key into the frozen object C' is not supported yet")]
  [TestCase("const C = Object.freeze({ A: { name: 'a' }, B: { name: 2 } }); export function f() { const a = C.A; return 0; }",
    "'the key name of the members of the frozen object C, which holds values of two kinds' is not supported yet")]
  [TestCase("const C = Object.freeze({ A: { name: f() } }); export function f() { const a = C.A; return 0; }",
    "'the member C.A, which is no object literal of strings, numbers and truth values' is not supported yet")]
  [TestCase("const C = Object.freeze({ ['A']: { name: 'a' } }); export function f() { const a = C.A; return 0; }",
    "'a member of the frozen object C that is not a name of its own and an object literal' is not supported yet")]
  public void OutOfScope_IsRefusedByName(string source, string message) => Assert.That(Refusal(source), Is.EqualTo(message));

}
