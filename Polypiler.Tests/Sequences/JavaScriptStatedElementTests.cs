using System;
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
/// An Array the program fills with computed numbers where JSDoc states integer elements (#1413), as
/// <c>OpCodes.Hex8ToBytes</c> builds its <c>@returns {uint8[]}</c> with
/// <c>bytes.push(parseInt(hexString.substr(i, 2), 16))</c> after the guard
/// <c>if (!/^[0-9A-Fa-f]*$/.test(hexString)) throw …</c>: <c>parseInt</c> of a text of at most n digits of
/// its radix is a NaN or an integer below radix^n, which the stated elements hold; the store traps on the
/// NaN, which the Array JavaScript fills would keep (<c>js-nan-store</c>). The expected values are node's
/// on the same source.
/// </summary>
[TestFixture]
public sealed class JavaScriptStatedElementTests {

  private const string Source = """
    /**
     * @param {string} s
     * @returns {uint8[]}
     */
    function hex(s) {
      if (!/^[0-9A-Fa-f]*$/.test(s))
        throw new Error('bad');
      const bytes = [];
      for (let i = 0; i < s.length; i += 2)
        bytes.push(parseInt(s.substr(i, 2), 16));
      return bytes;
    }
    /**
     * @param {string} s
     * @returns {uint8[]}
     */
    function nibbles(s) {
      if (!/^[0-9a-f]+$/.test(s))
        return [];
      const out = [];
      for (let i = 0; i < s.length; ++i)
        out.push(parseInt(s.substr(i, 1), 16));
      return out;
    }
    /**
     * @param {string} s
     * @returns {uint16[]}
     */
    function words(s) {
      if (!/^[0-9A-Fa-f]*$/.test(s))
        throw new Error('bad');
      const out = [];
      for (let i = 0; i < s.length; i += 4)
        out.push(parseInt(s.substr(i, 4), 16));
      return out;
    }
    /**
     * @param {string} s
     * @returns {uint8[]}
     */
    function decimal(s) {
      if (!/^[0-9]*$/.test(s))
        throw new Error('bad');
      const out = [];
      out.push(parseInt(s.substr(0, 2), 10), parseInt('7', 10));
      return out;
    }
    /**
     * @param {string} s
     * @returns {uint8[]}
     */
    function masked(s) {
      const out = [];
      for (let i = 0; i < s.length; ++i)
        out.push(s.charCodeAt(i) & 0xFF);
      return out;
    }
    /**
     * @param {uint8} v
     * @returns {uint8[]}
     */
    function narrow(v) {
      const out = [];
      out.push(v);
      return out;
    }
    /**
     * @param {uint8[]} b
     * @returns {number}
     */
    function sum(b) {
      let t = 0;
      for (let i = 0; i < b.length; ++i) t += b[i];
      return t;
    }
    /** @param {string} s */
    function given(s) {
      if (!/^[0-9A-Fa-f]*$/.test(s))
        throw new Error('bad');
      const list = [];
      for (let i = 0; i < s.length; i += 2)
        list.push(parseInt(s.substr(i, 2), 16));
      return sum(list);
    }
    class Holder {
      /** @param {string} s */
      constructor(s) {
        if (!/^[0-9A-Fa-f]*$/.test(s))
          throw new Error('bad');
        const list = [];
        for (let i = 0; i < s.length; i += 2)
          list.push(parseInt(s.substr(i, 2), 16));
        /** @type {uint8[]} */
        this.bytes = list;
      }
    }
    class Base {
      /**
       * @param {uint8[]} block
       * @returns {byte[]}
       */
      Encrypt(block) { throw new Error('not implemented'); }

      /** @returns {byte[]} */
      Run() {
        /** @type {byte[]} */
        const output = [];
        const processed = this.Encrypt([1, 2]);
        for (let i = 0; i < processed.length; i++) output.push(processed[i]);
        return output;
      }
    }
    class Derived extends Base {
      /** @param {string} s */
      constructor(s) {
        super();
        /** @type {string} */
        this.s = s;
      }
      Run() {
        const s = this.s;
        if (!/^[0-9A-Fa-f]*$/.test(s))
          throw new Error('bad');
        const out = [];
        for (let i = 0; i < s.length; i += 2)
          out.push(parseInt(s.substr(i, 2), 16));
        return out;
      }
    }
    /**
     * @param {string} s
     * @returns {uint8[]}
     */
    function past(s) {
      if (!/^[0-9A-Fa-f]*$/.test(s))
        throw new Error('bad');
      const out = [];
      out.push(parseInt(s.substr(4, 2), 16));
      return out;
    }
    export function scenario() { const b = hex('00ff10'); return b.length * 1e9 + b[0] * 1e6 + b[1] * 1e3 + b[2]; }
    export function oneDigit() { const b = nibbles('0f9'); return b.length * 1e6 + b[0] * 1e4 + b[1] * 100 + b[2]; }
    export function fourDigits() { const w = words('ffff0001'); return w.length * 1e10 + w[0] * 1e5 + w[1]; }
    export function decimals() { const b = decimal('255'); return b[0] * 1000 + b[1]; }
    export function maskedBytes() { const b = masked('AŁ'); return b[0] * 1000 + b[1]; }
    export function narrowed() { return narrow(200)[0]; }
    export function parameter() { return given('ff01'); }
    export function field() { const h = new Holder('80ff'); return h.bytes[0] * 1000 + h.bytes[1]; }
    export function override() { const b = new Derived('0aff').Run(); return b.length * 1e6 + b[0] * 1000 + b[1]; }
    export function nan() { return past('00').length; }
    """;

  private const string Refused = "'float64 list where uint8 list is expected' is not supported yet";

  /// <summary>A place of the stated elements the result of <c>f</c> is passed to, which refuses a list of doubles.</summary>
  private const string Consumer = """

    /**
     * @param {uint8[]} b
     * @returns {number}
     */
    function count(b) { return b.length; }
    /** @param {string} s */
    export function g(s) { return count(f(s)); }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>The single refusal of a source.</summary>
  private static Diagnostic Refusal(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Module, Is.Null);
    return result.Diagnostics.Single(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
  }

  /// <summary>
  /// Scenario: hex to bytes ([0, 255, 16]). Proofs: two hexadecimal digits, one, four into <c>uint16[]</c>,
  /// two decimal ones and a literal, <c>x &amp; 0xFF</c>, a stated <c>uint8</c>. Places: the result, a
  /// parameter, a field, an override of a method whose base only throws.
  /// </summary>
  [TestCase("scenario", 3000255016.0)]
  [TestCase("oneDigit", 3001509.0)]
  [TestCase("fourDigits", 26553500001.0)]
  [TestCase("decimals", 25007.0)]
  [TestCase("maskedBytes", 65065.0)]
  [TestCase("narrowed", 200.0)]
  [TestCase("parameter", 256.0)]
  [TestCase("field", 128255.0)]
  [TestCase("override", 2010255.0)]
  public void StatedElements_HoldWhatNodeHolds(string function, double expected) {
    var module = Import(Source);
    Assert.That(Convert.ToDouble(IrInterpreter.Call(module, function, [])), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(Convert.ToDouble(GeneratedCSharp.Compile(text, false).Method(function).Invoke(null, [])), Is.EqualTo(expected), text);
  }

  /// <summary>Happy path: the lists are of the stated elements, each store a checked conversion; the module announces the trap; the JavaScript translation is well-formed.</summary>
  [Test]
  public void StatedElements_AreListsOfTheStatedIntegers() {
    var result = JavaScriptFrontend.Import("m.mjs", Source);
    Assert.That(result.Caveats.Select(caveat => caveat.Id), Has.Member(JavaScriptFrontend.NaNStore));
    var text = LegalizedCSharp.Emit(result.Module!, CSharpProfile.Default)[0].Content;
    Assert.That(text, Does.Contain("List<byte> hex(string s)").And.Contain("List<ushort> words(string s)").And.Contain("bytes.Add(checked((byte)"));
    var script = LegalizedJavaScript.Emit(result.Module!, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
  }

  /// <summary>Boundary: a text past the end is empty, which parseInt makes a NaN; node keeps it in the Array, the translation traps.</summary>
  [Test]
  public void EmptyText_Traps() {
    var module = Import(Source);
    Assert.That(() => IrInterpreter.Call(module, "nan", []), Throws.TypeOf<IrTrapException>().With.Message.Contains("integer overflow"));
  }

  /// <summary>A module that stores no whole double has no such caveat.</summary>
  [Test]
  public void NoWholeStore_HasNoCaveat() {
    var result = JavaScriptFrontend.Import("m.mjs", "/** @returns {uint8[]} */ export function f() { const b = []; b.push(1); return b; }");
    Assert.That(result.Caveats.Select(caveat => caveat.Id), Has.No.Member(JavaScriptFrontend.NaNStore));
  }

  private const string Guarded = """
    /**
     * @param {string} s
     * @returns {uint8[]}
     */
    function f(s) {
      GUARD
      const b = [];
      for (let i = 0; i < s.length; i += 2)
        b.push(VALUE);
      return b;
    }
    """;

  /// <summary>
  /// Exception: values no proof bounds stay doubles, which the stated place refuses: a quotient, three
  /// digits (the boundary past two), no guard, a guard that does not leave, a parameter assigned, a
  /// pattern with a flag, of a negated class, with an escape, without a repetition or a class, an alphabet
  /// of no digits of the radix, a length that is no literal or is 0, no radix, a radix out of range or of
  /// a fraction, a guard that tests another string.
  /// </summary>
  [TestCase("if (!/^[0-9a-f]*$/.test(s)) throw new Error('x');", "s.charCodeAt(i) / 3")]
  [TestCase("if (!/^[0-9a-f]*$/.test(s)) throw new Error('x');", "parseInt(s.substr(i, 3), 16)")]
  [TestCase("", "parseInt(s.substr(i, 2), 16)")]
  [TestCase("if (!/^[0-9a-f]*$/.test(s)) { }", "parseInt(s.substr(i, 2), 16)")]
  [TestCase("if (!/^[0-9a-f]*$/.test(s)) throw new Error('x'); s = s + ' ';", "parseInt(s.substr(i, 2), 16)")]
  [TestCase("if (!/^[0-9a-f]*$/i.test(s)) throw new Error('x');", "parseInt(s.substr(i, 2), 16)")]
  [TestCase("if (!/^[^g-z]*$/.test(s)) throw new Error('x');", "parseInt(s.substr(i, 2), 16)")]
  [TestCase("if (!/^[\\da-f]*$/.test(s)) throw new Error('x');", "parseInt(s.substr(i, 2), 16)")]
  [TestCase("if (!/^[0-9a-f]$/.test(s)) throw new Error('x');", "parseInt(s.substr(i, 2), 16)")]
  [TestCase("if (!/^a*$/.test(s)) throw new Error('x');", "parseInt(s.substr(i, 2), 16)")]
  [TestCase("if (!/^[]*$/.test(s)) throw new Error('x');", "parseInt(s.substr(i, 2), 16)")]
  [TestCase("if (!/^[0-9a-g]*$/.test(s)) throw new Error('x');", "parseInt(s.substr(i, 2), 16)")]
  [TestCase("if (!/^[0-9.]*$/.test(s)) throw new Error('x');", "parseInt(s.substr(i, 2), 16)")]
  [TestCase("if (!/^[0-9]a*$/.test(s)) throw new Error('x');", "parseInt(s.substr(i, 2), 16)")]
  [TestCase("if (!/^[0-9a-f]*$/.test(s)) throw new Error('x');", "parseInt(s.substr(i, i), 16)")]
  [TestCase("if (!/^[0-9a-f]*$/.test(s)) throw new Error('x');", "parseInt(s.substr(i, 0), 16)")]
  [TestCase("if (!/^[0-9a-f]*$/.test(s)) throw new Error('x');", "parseInt(s.substr(i, 2))")]
  [TestCase("if (!/^[0-9a-f]*$/.test(s)) throw new Error('x');", "parseInt(s.substr(i, 2), 37)")]
  [TestCase("if (!/^[0-9a-f]*$/.test(s)) throw new Error('x');", "parseInt(s.substr(i, 2), 1)")]
  [TestCase("if (!/^[0-9a-f]*$/.test('ab')) throw new Error('x');", "parseInt(s.substr(i, 2), 16)")]
  [TestCase("if (/^[0-9a-f]*$/.test(s)) throw new Error('x');", "parseInt(s.substr(i, 2), 16)")]
  [TestCase("if (!/^[0-9a-f]*$/.test(s)) throw new Error('x'); else { }", "parseInt(s.substr(i, 2), 16)")]
  public void UnprovenValue_IsRefused(string guard, string value) {
    var refusal = Refusal(Guarded.Replace("GUARD", guard).Replace("VALUE", value) + Consumer);
    Assert.That((refusal.Code, refusal.Message), Is.EqualTo((JavaScriptFrontend.Unsupported, Refused)));
  }

  /// <summary>Exception: a guard after the read proves nothing there.</summary>
  [Test]
  public void GuardAfterTheRead_IsRefused() {
    var refusal = Refusal("""
      /**
       * @param {string} s
       * @returns {uint8[]}
       */
      function f(s) {
        const b = [];
        b.push(parseInt(s.substr(0, 2), 16));
        if (!/^[0-9a-f]*$/.test(s)) throw new Error('x');
        return b;
      }
      """ + Consumer);
    Assert.That(refusal.Message, Is.EqualTo(Refused));
  }

  /// <summary>Exception: a parameter a nested function assigns may change after the guard.</summary>
  [Test]
  public void ParameterAssignedByAClosure_IsRefused() {
    var refusal = Refusal("""
      /**
       * @param {string} s
       * @returns {uint8[]}
       */
      function f(s) {
        if (!/^[0-9a-f]*$/.test(s)) throw new Error('x');
        const change = () => { s = 'zz'; };
        change();
        const b = [];
        b.push(parseInt(s.substr(0, 2), 16));
        return b;
      }
      """ + Consumer);
    Assert.That(refusal.Message, Is.EqualTo(Refused));
  }

  /// <summary>Equivalence: a guard that returns, of a constant, and a string literal of digits are proofs as well.</summary>
  [TestCase("const t = s; if (!/^[0-9]+$/.test(t)) return []; const b = []; b.push(parseInt(t.substr(0, 2), 10)); b.push(parseInt('ff', 16)); return b;", "42", 42255.0)]
  [TestCase("const b = []; b.push(parseInt('0f', 16)); return b;", "", 15.0)]
  public void OtherProofs_HoldWhatNodeHolds(string body, string argument, double expected) {
    var module = Import($$"""
      /**
       * @param {string} s
       * @returns {uint8[]}
       */
      function f(s) { {{body}} }
      export function g() { const b = f('{{argument}}'); return b.length === 1 ? b[0] : b[0] * 1000 + b[1]; }
      """);
    Assert.That(Convert.ToDouble(IrInterpreter.Call(module, "g", [])), Is.EqualTo(expected));
  }

  /// <summary>Boundary: a literal of three hexadecimal digits does not fit a byte, a whole double past 2^53 is no proof.</summary>
  [TestCase("parseInt('fff', 16)", "uint8")]
  [TestCase("parseInt('fffffffffffffff', 16)", "uint32")]
  public void LiteralPastTheElements_IsRefused(string value, string element)
    => Assert.That(Refusal($$"""
      /** @returns {number} */
      function f() { const b = []; b.push({{value}}); return count(b); }
      /**
       * @param {{{element}}[]} b
       * @returns {number}
       */
      function count(b) { return b.length; }
      export function g() { return f(); }
      """).Message,
      Is.EqualTo($"'float64 list where {element} list is expected' is not supported yet"));

  /// <summary>Exception: a radix of a fraction proves nothing, and the lowering refuses it by name.</summary>
  [Test]
  public void RadixOfAFraction_IsRefused()
    => Assert.That(JavaScriptFrontend.Import("m.mjs", Guarded.Replace("GUARD", "if (!/^[0-9a-f]*$/.test(s)) throw new Error('x');").Replace("VALUE", "parseInt(s.substr(i, 2), 16.5)") + Consumer)
      .Diagnostics.Select(diagnostic => diagnostic.Message), Is.EquivalentTo(new[] { "'parseInt with a radix that is no integer literal' is not supported yet", Refused }));

  private const string Wholes = """
    /**
     * @param {number} value
     * @returns {int} 0..255
     */
    function toByte(value) {
      const reduced = Math.trunc(value) % 256;
      return reduced < 0 ? reduced + 256 : reduced;
    }
    class Absorber {
      /** @param {int} size */
      constructor(size) {
        /** @type {byte[]} */
        this.held = new Array(size).fill(0);
        /** @type {uint8[]} */
        this.literal = [1, 2];
      }
      /** @param {byte[]} data */
      absorb(data) {
        for (let i = 0; i < data.length; i++)
          this.held[i] = toByte(data[i]);
      }
    }
    /**
     * @param {uint8[]} b
     * @returns {number}
     */
    function total(b) {
      let t = 0;
      for (let i = 0; i < b.length; i++) t = t * 256 + b[i];
      return t;
    }
    /**
     * @param {number} v
     * @returns {uint8[]}
     */
    function variants(v) {
      const r = Math.floor(v) % 256;
      const c = Math.ceil(v) % -256;
      const n = r < 0 ? r : -1;
      const out = [];
      out.push(r < 0 ? r + 256 : r, r <= -1 ? r - -256 : r, r >= 0 ? r : 256 + r, r > -1 ? r : r + 256);
      out.push(c < 0 ? c + 256 : c, (n % 7) + 6, (r < 0 ? r + 256 : r) % 16, Math.round(v) % 100 < 0 ? 0 : 1);
      return out;
    }
    export function absorbed() { const a = new Absorber(4); a.absorb([1, 255, 0, 7]); return total(a.held) * 1000 + total(a.literal); }
    /** @param {number} v */
    function digest(v) {
      const b = variants(v);
      let t = 0;
      for (let i = 0; i < b.length; i++) t = (t * 1000 + b[i]) % 1000000007;
      return t * 100 + b.length;
    }
    export function wholes() { return digest(-1.5); }
    export function positive() { return digest(300.25); }
    export function byte() { return toByte(-1) * 1000 + toByte(300.7); }
    """;

  /// <summary>
  /// Equivalence of the whole doubles: <c>Math.trunc(v) % 256</c> brought to 0..255 by a conditional of each
  /// comparison, <c>floor</c>, <c>ceil</c> and <c>round</c>, a remainder by a negative divisor, of a dividend
  /// never positive and of one never negative; a new Array a stated field is given - <c>new Array(n).fill(0)</c>
  /// and a literal - holds the stated elements. Values: node's.
  /// </summary>
  [TestCase("absorbed", 33488903258.0)]
  [TestCase("byte", 255044.0)]
  [TestCase("wholes", 23669267508.0)]
  [TestCase("positive", 69886184908.0)]
  public void WholeDoubles_HoldWhatNodeHolds(string function, double expected) {
    var module = Import(Wholes);
    Assert.That(Convert.ToDouble(IrInterpreter.Call(module, function, [])), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(text, Does.Contain("public byte[] held;").And.Contain("public byte[] literal;"));
    Assert.That(Convert.ToDouble(GeneratedCSharp.Compile(text, false).Method(function).Invoke(null, [])), Is.EqualTo(expected), text);
  }

  private const string Narrowing = """
    /**
     * @param {number} v
     * @returns {uint8[]}
     */
    function f(v) {
      const r = Math.trunc(v) % 256;
      let s = r;
      s = s + 0;
      const out = [];
      out.push(VALUE);
      return out;
    }
    /**
     * @param {uint8[]} b
     * @returns {number}
     */
    function count(b) { return b.length; }
    /** @param {number} v */
    export function g(v) { return count(f(v)); }
    """;

  /// <summary>
  /// Exception: what no proof bounds to a byte stays a double, which the stated place refuses: the remainder
  /// itself, a variable that is assigned, a bound that is no literal or of a fraction, a test that leaves a
  /// branch nothing, a remainder by 0 or by a computed divisor, a sum of no bound, a product, a test of no
  /// ordering or with the variable on the right.
  /// </summary>
  [TestCase("r")]
  [TestCase("s < 0 ? s + 256 : s")]
  [TestCase("r < v ? r + 256 : r")]
  [TestCase("r < 0.5 ? r + 256 : r")]
  [TestCase("r < -300 ? r + 256 : r")]
  [TestCase("r >= -300 ? r : r + 256")]
  [TestCase("Math.trunc(v) % 0")]
  [TestCase("Math.trunc(v) % v")]
  [TestCase("Math.trunc(v) + 1")]
  [TestCase("(r < 0 ? r + 256 : r) * 1")]
  [TestCase("r === 0 ? r + 256 : r")]
  [TestCase("0 > r ? r + 256 : r")]
  public void UnboundedWhole_IsRefused(string value)
    => Assert.That(Refusal(Narrowing.Replace("VALUE", value)).Message, Is.EqualTo(Refused));

  /// <summary>Exception: a rounding of no number and one of two arguments make no whole double; the lowering refuses them by name.</summary>
  [TestCase("Math.trunc('7') % 256", "'string as a number' is not supported yet")]
  [TestCase("Math.trunc(v, 1) % 256", "'Math.trunc with 2 arguments' is not supported yet")]
  public void RoundingOfNoNumber_IsRefused(string value, string refusal)
    => Assert.That(JavaScriptFrontend.Import("m.mjs", Narrowing.Replace("VALUE", value)).Diagnostics.Select(diagnostic => diagnostic.Message),
      Is.EquivalentTo(new[] { refusal, Refused }));

  /// <summary>Boundary: a new Array of elements past the stated ones keeps them, which the stated place refuses.</summary>
  [Test]
  public void FreshArrayPastTheElements_IsRefused() {
    var refusal = Refusal("""
      class K {
        constructor() {
          /** @type {uint8[]} */
          this.k = [1, 256];
        }
      }
      /**
       * @param {uint8[]} b
       * @returns {number}
       */
      function count(b) { return b.length; }
      export function f() { return count(new K().k); }
      """);
    Assert.That(refusal.Message, Is.EqualTo("'int32[] where uint8[] is expected' is not supported yet"));
  }

  private const string Places = """
    class Base {
      constructor() { /** @type {boolean} */ this.inverse = false; }
      /**
       * @param {uint8[]} block
       * @returns {byte[]}
       */
      Encrypt(block) { throw new Error('not implemented'); }
      /**
       * @param {uint8[]} block
       * @returns {byte[]}
       */
      Decrypt(block) { throw new Error('not implemented'); }
      /** @param {uint8[]} block */
      Plain(block) { throw new Error('not implemented'); }

      /** @returns {byte[]} */
      Run() {
        /** @type {byte[]} */
        const output = [];
        const processed = this.inverse ? this.Decrypt([1, 2]) : this.Encrypt([1, 2]);
        for (let i = 0; i < processed.length; i++) output.push(processed[i]);
        const plain = this.Plain([3]);
        output.push(plain & 0xFF);
        return output;
      }
    }
    class Derived extends Base {
      /** @returns {byte[]} */
      Run() { return [9]; }
    }
    /**
     * @param {string} s
     * @returns {uint8[]}
     */
    function pair(s) {
      if (!/^[0-9a-f]*$/.test(s)) throw new Error('x');
      const out = [parseInt(s.substr(0, 2), 16), parseInt(s.substr(2, 2), 16)];
      return out;
    }
    /**
     * @param {string} s
     * @returns {float64[]}
     */
    function doubles(s) {
      if (!/^[0-9a-f]*$/.test(s)) throw new Error('x');
      const out = [];
      out.push(parseInt(s.substr(0, 2), 16));
      return out;
    }
    export function run() { return new Derived().Run()[0]; }
    export function pairs() { const p = pair('0aff'); return p[0] * 1000 + p[1]; }
    export function floats() { return doubles('ff')[0]; }
    """;

  /// <summary>
  /// Places: a local taking either of two methods that only throw is of the result they state, one that
  /// states none a double; an Array that never grows is an array of the stated bytes; a stated
  /// <c>float64[]</c> keeps the doubles. Values: node's.
  /// </summary>
  [TestCase("run", 9.0)]
  [TestCase("pairs", 10255.0)]
  [TestCase("floats", 255.0)]
  public void OtherPlaces_HoldWhatNodeHolds(string function, double expected) {
    var module = Import(Places);
    Assert.That(Convert.ToDouble(IrInterpreter.Call(module, function, [])), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(text, Does.Contain("static byte[] pair(string s)").And.Contain("List<double> doubles(string s)"));
    Assert.That(Convert.ToDouble(GeneratedCSharp.Compile(text, false).Method(function).Invoke(null, [])), Is.EqualTo(expected), text);
  }

  /// <summary>Exception: a text no guard of the function proves - of a field, of a module constant, read by an arrow of an expression body - stays a double.</summary>
  [TestCase("class H { constructor() { /** @type {string} */ this.s = '00'; } }", "parseInt(new H().s.substr(0, 2), 16)")]
  [TestCase("const T = '00';", "parseInt(T.substr(0, 2), 16)")]
  [TestCase("", "(t => parseInt(s.substr(t, 2), 16))(0)")]
  public void TextOfNoGuard_IsRefused(string declarations, string value) {
    var refusal = Refusal($$"""
      {{declarations}}
      /**
       * @param {string} s
       * @returns {uint8[]}
       */
      function f(s) {
        if (!/^[0-9a-f]*$/.test(s)) throw new Error('x');
        const b = [];
        b.push({{value}});
        return b;
      }
      """ + Consumer);
    Assert.That(refusal.Message, Is.EqualTo(Refused));
  }

}
