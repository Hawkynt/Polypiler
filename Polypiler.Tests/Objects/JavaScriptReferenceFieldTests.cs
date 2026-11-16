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
/// Fields of a JavaScript class that hold references (#1365), as the classes of the Cipher framework
/// have them: a string, an Array of strings, of objects or of arrays - which is a list - and a list
/// that grows through the field, of this object or of another. What the interpreter computes, what C#
/// compiles and runs, and the JavaScript written back.
/// </summary>
[TestFixture]
public sealed class JavaScriptReferenceFieldTests {

  private const string Source = """
    class Item {
      /** @param {string} text */
      constructor(text) { this.text = text; }
    }
    class Holder {
      constructor() {
        this.name = 'x';
        this.items = [new Item('first')];
        this.words = ['a'];
        this.rows = [[1, 2]];
        this.buffer = [];
      }
      /** @param {string} t */
      add(t) {
        this.name = this.name + t;
        this.items.push(new Item(t));
        this.words.push(t);
        this.buffer.push(t.length);
        return this.name.length + this.items.length + this.words[1].length + this.rows[0][1];
      }
      clear() { this.buffer.length = 0; }
    }
    export function f() {
      const h = new Holder();
      const other = new Holder();
      other.words.push('zz');
      h.add('a');
      const total = h.add('bc') + h.items[2].text.length + h.buffer.length + other.words.length;
      h.clear();
      return total + h.buffer.length;
    }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  // name 'xabc' 4 + items 3 + words[1] 'a' 1 + rows[0][1] 2 = 10; items[2].text 'bc' 2; buffer 2; other.words 2 => 16.
  private const double Expected = 16;

  /// <summary>An object, an array or a function counts as true unless it is null; a list, which the IR never holds null, as true.</summary>
  [Test]
  public void References_AsConditions_CountAsTrueUnlessNull() {
    var module = Import("""
      class Link {
        constructor() { this.next = null; this.items = []; this.seen = new Map(); }
      }
      export function f() {
        const link = new Link();
        let count = 0;
        if (!link.next)
          count++;
        link.next = new Link();
        if (link.next)
          count += 10;
        link.items.push(1);
        if (link.items && link.seen)
          count += 100;
        const data = new Uint8Array(2);
        if (data && !!data)
          count += 1000;
        return count;
      }
      """);
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(1111));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("f").Invoke(null, []), Is.EqualTo(1111), text);
    var refused = JavaScriptFrontend.Import("m.mjs", "function make() { const a = []; a.push(1); return a; } export function g() { return make() ? 1 : 0; }");
    Assert.That(refused.Diagnostics.Single().Message, Does.EndWith("as a condition' is not supported yet"));
  }

  /// <summary>Array.isArray of a value whose type says what it is: an Array where it is not null, never a string, a number or an object.</summary>
  [Test]
  public void ArrayIsArray_IsDecidedByTheType() {
    var module = Import("""
      class Box { constructor() { this.items = []; this.missing = null; this.bytes = [1, 2]; } }
      export function f() {
        const box = new Box();
        box.items.push(1);
        let count = 0;
        if (Array.isArray(box.items)) count += 1;
        if (Array.isArray(box.bytes)) count += 10;
        if (!Array.isArray('text') && !Array.isArray(4) && !Array.isArray(box)) count += 100;
        box.missing = [3];
        if (Array.isArray(box.missing)) count += 1000;
        return count;
      }
      """);
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(1111));
    // An array that may be null is read once, effects and all; a list, which is never null, would not be read at all.
    Assert.That(IrInterpreter.Call(Import("function make() { return [1]; } export function f() { return Array.isArray(make()) ? 1 : 0; }"), "f", []), Is.EqualTo(1));
    Assert.That(JavaScriptFrontend.Import("m.mjs", "function make() { const a = []; a.push(1); return a; } export function f() { return Array.isArray(make()) ? 1 : 0; }").Diagnostics.Single().Message,
      Is.EqualTo("'Array.isArray of an operand with an effect' is not supported yet"));
    Assert.That(JavaScriptFrontend.Import("m.mjs", "/** @param {uint8[]} d */ export function f(d) { return Array.isArray(d) ? 1 : 0; }").Diagnostics.Single().Message,
      Is.EqualTo("'Array.isArray of uint8[], which JSDoc may have meant as an Array' is not supported yet"));
    Assert.That(JavaScriptFrontend.Import("m.mjs", "export function f() { return Array.isArray(1, 2) ? 1 : 0; }").Diagnostics.Single().Message,
      Is.EqualTo("'Array.isArray with 2 arguments' is not supported yet"));
  }

  /// <summary>Every change of the length of an array through a field, and a member that is no field of an object, which grows nothing.</summary>
  [Test]
  public void ChangesThroughAField_MakeItAList() {
    var module = Import("""
      class Bag {
        constructor() { this.items = [1]; this.keys = new Set(); this.seen = new Map(); }
        get first() { return this.items[0]; }
      }
      export function f() {
        const bag = new Bag();
        bag.items.push(2, 3);
        bag.items.pop();
        bag.items.splice(0, 1);
        bag.keys.add(5);
        bag.seen.set(1, 2);
        const at = bag.items.indexOf(2) + (bag.items.includes(2) ? 10 : 0);
        return at + bag.items.length + bag.keys.size + bag.seen.size + bag.first;
      }
      """);
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(15));
    var refused = JavaScriptFrontend.Import("m.mjs", "export function f() { const a = [1]; a.x.push(2); return 1; }");
    Assert.That(refused.Module, Is.Null);
  }

  [Test]
  public void ReferenceFields_HaveTheirTypes() {
    var holder = Import(Source).Types.OfType<IrClassType>().Single(type => type.Name == "Holder");
    var item = IrType.Object(Import(Source).Types.OfType<IrClassType>().Single(type => type.Name == "Item"));
    Assert.That(holder.Fields.Select(field => (field.Name, field.Type.ToString())), Is.EqualTo(new[] {
      ("name", IrType.String.ToString()),
      ("items", IrType.List(item).ToString()),
      ("words", IrType.List(IrType.String).ToString()),
      ("rows", IrType.List(IrType.Array(IrType.I32)).ToString()),
      ("buffer", IrType.List(IrType.I32).ToString())
    }));
  }

  [Test]
  public void ReferenceFields_ComputeWhatJavaScriptComputes() {
    var module = Import(Source);
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(Expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, CSharpProfile.Default.Name, CSharpProfile.Default.LanguageVersionName), Is.Empty, text);
    Assert.That(GeneratedCSharp.Compile(text, false).Method("f").Invoke(null, []), Is.EqualTo(Expected), text);
    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
  }

}
