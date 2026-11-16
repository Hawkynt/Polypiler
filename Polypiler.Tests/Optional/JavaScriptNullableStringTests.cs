using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Optional;

/// <summary>
/// A string that may be null (#1370): a field set to null and later to a string, as the Cipher
/// framework's <c>Algorithm</c> sets its metadata. A string is a reference of the IR that may be null,
/// as an array or an object is; what the interpreter computes, what C# compiles and runs, the
/// JavaScript written back, and what is refused. The expected values are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptNullableStringTests {

  private const string Uses = """
    class Box { constructor() { this.text = null; } }
    /** @param {int32} which */
    export function uses(which) {
      const box = new Box();
      if (which === 1) box.text = '';
      if (which === 2) box.text = 'a';
      const condition = box.text ? 1 : 0;
      const or = (box.text || 'none').length;
      const joined = (box.text + 'x').length;
      const test = box.text === null ? 1000 : 0;
      const loose = box.text != null ? 10000 : 0;
      return condition + or * 10 + joined * 100 + test + loose;
    }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static object? RunCSharp(IrModule module, string function, params object[] arguments) {
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    return GeneratedCSharp.Compile(text, false).Method(function).Invoke(null, arguments);
  }

  /// <summary>Scenario: a name set to null and then to 'Adler-32' has the length 8, as in node.</summary>
  [Test]
  public void NameSetToNullAndThenToAString_HasItsLength() {
    var module = Import("""
      class Named {
        constructor() { this.name = null; }
        rename() { this.name = 'Adler-32'; return this.name.length; }
      }
      export function scenario() { const n = new Named(); return n.rename(); }
      """);
    Assert.That(IrInterpreter.Call(module, "scenario", []), Is.EqualTo(8));
    Assert.That(RunCSharp(module, "scenario"), Is.EqualTo(8));
    Assert.That(module.Types.OfType<IrClassType>().Single().Fields.Single().Type, Is.EqualTo(IrType.String));
  }

  /// <summary>Test design 1-3: null, the empty string and a string, in a condition, behind ||, joined and compared with null.</summary>
  [TestCase(0, 1540)]
  [TestCase(1, 10140)]
  [TestCase(2, 10211)]
  public void EveryValue_IsWhatNodeComputes(int which, double expected) {
    var module = Import(Uses);
    Assert.That(IrInterpreter.Call(module, "uses", [which]), Is.EqualTo(expected));
    Assert.That(RunCSharp(module, "uses", which), Is.EqualTo(expected));
    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
  }

  /// <summary>
  /// Test design 4: a place null and undefined both reach is refused (PLY1105), one only undefined
  /// reaches - pop of an empty list, a missing argument - by name; a strict test for undefined of a
  /// place only null reaches is false.
  /// </summary>
  [Test]
  public void NullAndUndefinedToldApart_AreRefused() {
    var module = Import("""
      class Box { constructor() { this.text = null; } name() { this.text = 'x'; } }
      export function f() { const box = new Box(); return box.text === undefined ? 1 : 0; }
      """);
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(0));
    var both = JavaScriptFrontend.Import("m.mjs", """
      /** @param {int32} n */
      export function f(n) { let s = 'a'; if (n === 1) s = null; if (n === 2) s = undefined; return s === null ? 1 : 0; }
      """);
    Assert.That(both.Diagnostics.Single().Code, Is.EqualTo(JavaScriptFrontend.MixedKinds));
    Assert.That(both.Diagnostics.Single().Message, Is.EqualTo("'s' holds both null and undefined"));
    var undefinedOnly = JavaScriptFrontend.Import("m.mjs", "export function f() { const a = ['x']; a.push('y'); const s = a.pop(); return s.length; }");
    Assert.That(undefinedOnly.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains("a string that may be undefined"));
  }

  /// <summary>A string operation on null traps with a null reference in the IR, as it throws in both targets; equality is null-safe.</summary>
  [Test]
  public void Null_TrapsInAStringOperation_AndComparesSafely() {
    var module = Import("""
      class Box { constructor() { this.text = null; } name() { this.text = 'x'; } }
      /** @param {int32} which */
      export function f(which) {
        const box = new Box();
        const other = new Box();
        if (which === 1) return box.text === other.text ? 1 : 0;
        if (which === 2) { other.text = 'x'; return box.text === other.text ? 1 : 0; }
        return box.text.length;
      }
      """);
    Assert.That(IrInterpreter.Call(module, "f", [1]), Is.EqualTo(1));
    Assert.That(IrInterpreter.Call(module, "f", [2]), Is.EqualTo(0));
    Assert.That(() => IrInterpreter.Call(module, "f", [0]), Throws.Exception.With.Message.Contains("null reference"));
  }

  /// <summary>null and undefined in a template are the words, as ToString makes them.</summary>
  [Test]
  public void AbsentValuesInATemplate_AreTheirWords()
    => Assert.That(IrInterpreter.Call(Import("export function f() { return `${null}|${undefined}`; }"), "f", []), Is.EqualTo("null|undefined"));

  /// <summary>An optional number joined to text is written as null where it holds none, as ToString(null) is.</summary>
  [Test]
  public void OptionalNumber_IsWrittenAsNull() {
    var module = Import("""
      class Year { constructor() { this.year = null; } }
      /** @param {int32} set */
      export function f(set) { const y = new Year(); if (set === 1) y.year = 1995; return ('' + y.year).length; }
      """);
    Assert.That(IrInterpreter.Call(module, "f", [0]), Is.EqualTo(4));
    Assert.That(IrInterpreter.Call(module, "f", [1]), Is.EqualTo(4));
    Assert.That(RunCSharp(module, "f", 0), Is.EqualTo(4));
  }

}
