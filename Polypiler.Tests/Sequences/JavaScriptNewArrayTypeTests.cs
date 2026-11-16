using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Sequences;

/// <summary>
/// <c>new Array(n)</c> takes the element type of the place it is stored into (#1406), as ChaCha20 keeps
/// its state in <c>/** @type {uint32[]} */ this.state = new Array(16)</c> and SHA-256 its schedule in
/// <c>const W = new Array(64)</c>: a field JSDoc states <c>T[]</c> of is an array of <c>T</c>, a local the
/// elements the program stores. An element never stored reads 0 where JavaScript reads undefined, which
/// the <c>js-oob-read</c> caveat states. The expected values are node's on the same source.
/// </summary>
[TestFixture]
public sealed class JavaScriptNewArrayTypeTests {

  private const string Source = """
    class S {
      constructor() {
        /** @type {uint32[]} */
        this.s = new Array(4);
      }
      /** @returns {uint32} */
      run() {
        this.s[0] = 0xFFFFFFFF;
        return this.s[0];
      }
    }
    export function scenario() { return new S().run(); }
    export function local() {
      const w = new Array(3);
      w[0] = 0xFFFFFFFF;
      w[1] = 1;
      w[2] = 2;
      return w[0] + w[2] + w.length;
    }
    export function schedule() {
      const W = new Array(64);
      for (let t = 0; t < 64; t++) W[t] = (t * 0x9E3779B9) >>> 0;
      let x = 0;
      for (let t = 0; t < 64; t++) x = (x ^ W[t]) >>> 0;
      return x;
    }
    class B {
      constructor() {
        /** @type {uint8[]} */
        this.z = new Array(0);
        /** @type {uint16[]} */
        this.o = new Array(1);
        /** @type {uint8[]} */
        this.b = [];
      }
      /** @returns {number} */
      run() {
        this.o[0] = 65535;
        this.b.push(200);
        return this.z.length * 1000000 + this.o[0] + this.o.length * 100000 + this.b[0] * 10 + this.b.length;
      }
    }
    export function lengths() { return new B().run(); }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>
  /// Scenario: state words (4294967295). Places: a stated field, a local of the stored words. Boundaries
  /// of the length: 0 and 1 (fields), 64 (a local); and an empty Array of a stated field that grows.
  /// </summary>
  [TestCase("scenario", 4294967295.0)]
  [TestCase("local", 4294967300.0)]
  [TestCase("schedule", 2129767936.0)]
  [TestCase("lengths", 167536.0)]
  public void NewArray_HoldsWhatNodeHolds(string function, double expected) {
    var module = Import(Source);
    Assert.That(System.Convert.ToDouble(IrInterpreter.Call(module, function, [])), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(System.Convert.ToDouble(GeneratedCSharp.Compile(text, false).Method(function).Invoke(null, [])), Is.EqualTo(expected), text);
  }

  /// <summary>Happy path: the field is an array of the stated elements, the local one of the stored words, the empty field a list of them.</summary>
  [Test]
  public void NewArray_IsAnArrayOfTheElementsOfItsPlace() {
    var text = LegalizedCSharp.Emit(Import(Source), CSharpProfile.Default)[0].Content;
    Assert.That(text, Does.Contain("public uint[] s;").And.Contain("this.s = new uint[4];"));
    Assert.That(text, Does.Contain("public byte[] z;").And.Contain("public ushort[] o;").And.Contain("List<byte> b;"));
    Assert.That(text, Does.Contain("var W = new uint[64];"));
    var script = LegalizedJavaScript.Emit(Import(Source), JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
  }

  /// <summary>Scenario and error path: stores of a string and a number into one new Array are values of two kinds.</summary>
  [Test]
  public void StoresOfTwoKinds_AreRefused() {
    var result = JavaScriptFrontend.Import("m.mjs", "export function f() { const a = new Array(2); a[0] = 'x'; a[1] = 1; return a.length; }");
    Assert.That(result.Module, Is.Null);
    var refusal = result.Diagnostics.Single();
    Assert.That((refusal.Code, refusal.Message), Is.EqualTo((JavaScriptFrontend.MixedKinds, "the elements of 'a' hold both string and number")));
  }

  /// <summary>A stated field the program gives an Array of its own elements keeps that Array: the statement decides only elements no value reached.</summary>
  [Test]
  public void StatedField_TakesAnArrayOfKnownElementsAsItIs() {
    var module = Import("""
      class K {
        constructor() {
          /** @type {uint8[]} */
          this.k = [1.5, 2];
        }
      }
      export function f() { return new K().k[0]; }
      """);
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(1.5));
  }

}
