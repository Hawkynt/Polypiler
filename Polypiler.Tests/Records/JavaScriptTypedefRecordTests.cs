using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Records;

/// <summary>
/// A <c>@typedef {Object}</c> with <c>@property</c> tags, some of them optional (#1409), as the Cipher
/// framework's <c>MerkleDamgardOptions</c>: a class of read-only fields, an optional key an optional
/// value, and an object literal passed where the typedef is stated a new object of it that holds none
/// for every key it leaves out. What the interpreter computes, what C# compiles and runs, the JavaScript
/// written back, and what is refused by name. The expected values are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptTypedefRecordTests {

  private const string Opts = """
    /**
     * @typedef {Object} Opts
     * @property {int} blockSize - bytes per block
     * @property {int} [pad=1] - the pad
     */

    """;

  private const string Scenario = Opts + """
    /**
     * @param {Opts} o
     * @returns {number}
     */
    function f(o) { return (o || {}).pad === undefined ? o.blockSize : o.pad; }
    /** @param {int32} which */
    export function run(which) { return which === 0 ? f({ blockSize: 64 }) : f({ blockSize: 64, pad: 3 }); }
    """;

  /// <summary>The settings MerkleDamgardBlocks reads, as it reads them; the result spells every value out.</summary>
  private const string MerkleDamgard = """
    /**
     * @typedef {Object} MerkleDamgardOptions
     * @property {int} blockSize - bytes per block
     * @property {int} [padByte=0x80] - 0x01 for Tiger
     * @property {int} [lengthBytes=8] - 16 for SHA-512, 0 for no length field
     * @property {bool} [lengthLittleEndian=false] - true for MD4, MD5, RIPEMD
     * @property {bool} [lengthInBits=true] - false where the field counts bytes
     */

    /**
     * @param {int} pending
     * @param {MerkleDamgardOptions} options
     * @returns {number}
     */
    function settle(pending, options) {
      /** @type {MerkleDamgardOptions} */
      const settings = options || {}
      /** @type {int} */
      const blockSize = settings.blockSize;
      if (!(blockSize > 0)) throw new Error('blockSize must be positive');
      /** @type {int} */
      const padByte = settings.padByte === undefined ? 0x80 : settings.padByte;
      /** @type {int} */
      const lengthBytes = settings.lengthBytes === undefined ? 8 : settings.lengthBytes;
      const littleEndian = settings.lengthLittleEndian === true;
      const inBits = settings.lengthInBits !== false;
      return pending + blockSize * 10 + padByte * 10000 + lengthBytes * 10000000 + (littleEndian ? 100000000 : 0) + (inBits ? 1000000000 : 0);
    }

    /** @param {int32} which */
    export function run(which) {
      if (which === 0) return settle(1, { blockSize: 64, lengthBytes: 8 });
      if (which === 1) return settle(2, { blockSize: 64 });
      if (which === 2) return settle(3, { blockSize: 128, padByte: 1, lengthBytes: 16, lengthLittleEndian: true, lengthInBits: false });
      return settle(4, { lengthInBits: true, blockSize: 32 });
    }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static Diagnostic Refusal(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics, Is.Not.Empty);
    return result.Diagnostics[0];
  }

  private static object? RunCSharp(IrModule module, string function, params object[] arguments) {
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    return GeneratedCSharp.Compile(text, false).Method(function).Invoke(null, arguments);
  }

  private static void WritesJavaScript(IrModule module) {
    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
  }

  /// <summary>Scenario: options with defaults - f({ blockSize: 64 }) is 64 and f({ blockSize: 64, pad: 3 }) is 3, as in node.</summary>
  [TestCase(0, 64.0)]
  [TestCase(1, 3.0)]
  public void OptionsWithDefaults_TakeTheKeysTheLiteralGives(int which, double expected) {
    var module = Import(Scenario);
    Assert.That(IrInterpreter.Call(module, "run", [which]), Is.EqualTo(expected));
    Assert.That(RunCSharp(module, "run", which), Is.EqualTo(expected));
    WritesJavaScript(module);
  }

  /// <summary>The typedef is a class of read-only fields, an optional key an optional value; the literal's own shape makes no struct.</summary>
  [Test]
  public void Typedef_IsAClassOfReadOnlyFields() {
    var module = Import(Scenario);
    var type = module.Types.OfType<IrClassType>().Single();
    Assert.That(type.Name, Is.EqualTo("Opts"));
    Assert.That(type.Fields.Select(field => $"{field.Name}: {field.Type}{(field.IsReadOnly ? "" : " mutable")}"), Is.EqualTo(new[] { "blockSize: i32", "pad: i32?" }));
    Assert.That(module.Types.OfType<IrStructType>(), Is.Empty);
  }

  /// <summary>
  /// Equivalence: the branch that reads what the test proves present is the integer - strict and loose,
  /// either way round; a strict test for null of a key only undefined reaches proves nothing; an integer
  /// read unchecked is a NaN where it is undefined.
  /// </summary>
  [TestCase("o.pad === undefined ? 1 : o.pad", "i32", 1.0, 5.0)]
  [TestCase("o.pad !== undefined ? o.pad : 1", "i32", 1.0, 5.0)]
  [TestCase("o.pad == null ? 1 : o.pad", "i32", 1.0, 5.0)]
  [TestCase("undefined != o.pad ? o.pad : 1", "i32", 1.0, 5.0)]
  [TestCase("o.pad === null ? 1 : o.pad", "f64?", double.NaN, 5.0)]
  [TestCase("o.pad === undefined ? 1 : o.blockSize", "i32", 1.0, 2.0)]
  [TestCase("o.pad + 1", "f64", double.NaN, 6.0)]
  public void TestedKey_IsPresentInTheBranchThatReadsIt(string expression, string type, double without, double with) {
    var module = Import(Opts + $$"""
      /** @param {Opts} o */
      function padOf(o) { return {{expression}}; }
      /** @param {int32} which @returns {number} */
      export function run(which) { return (which === 0 ? padOf({ blockSize: 2 }) : padOf({ blockSize: 2, pad: 5 })) * 1; }
      """);
    Assert.That(module.Functions.Single(function => function.Name == "padOf").ReturnType.ToString(), Is.EqualTo(type));
    Assert.That(IrInterpreter.Call(module, "run", [0]), Is.EqualTo(without));
    Assert.That(IrInterpreter.Call(module, "run", [1]), Is.EqualTo(with));
    Assert.That(RunCSharp(module, "run", 0), Is.EqualTo(without));
    WritesJavaScript(module);
  }

  /// <summary>Test design 1-3: MerkleDamgardBlocks' settings of the literal SHA-256 passes, of only the required key, of every key, and of keys in another order.</summary>
  [TestCase(0, 1081280641.0)]
  [TestCase(1, 1081280642.0)]
  [TestCase(2, 260011283.0)]
  [TestCase(3, 1081280324.0)]
  public void MerkleDamgardSettings_AreReadAsNodeReadsThem(int which, double expected) {
    var module = Import(MerkleDamgard);
    Assert.That(IrInterpreter.Call(module, "run", [which]), Is.EqualTo(expected));
    Assert.That(RunCSharp(module, "run", which), Is.EqualTo(expected));
    WritesJavaScript(module);
  }

  /// <summary>Test design 3: an options object that may be null takes the literal {} through ||, of every key undefined.</summary>
  [TestCase(0, 7.0)]
  [TestCase(1, 5.0)]
  public void EmptyLiteralThroughOr_HoldsEveryKeyUndefined(int which, double expected) {
    var module = Import("""
      /**
       * @typedef {Object} Loose
       * @property {int} [size] - the size
       */
      /**
       * @param {Loose|null} o
       * @returns {number}
       */
      function f(o) { const s = o || {}; return s.size === undefined ? 7 : s.size; }
      /** @param {int32} which */
      export function run(which) { return which === 0 ? f(null) : f({ size: 5 }); }
      """);
    Assert.That(IrInterpreter.Call(module, "run", [which]), Is.EqualTo(expected));
    Assert.That(RunCSharp(module, "run", which), Is.EqualTo(expected));
    WritesJavaScript(module);
  }

  /// <summary>Equivalence: a typedef of a string, a truth value and a record of the file passed on as it is.</summary>
  [Test]
  public void TypedefPassedOn_KeepsItsValues() {
    var module = Import("""
      /**
       * @typedef {Object} Label
       * @property {string} text - the text
       * @property {boolean} [loud] - whether it is loud
       */
      /** @param {Label} l @returns {number} */
      function size(l) { return l.text.length + (l.loud === true ? 100 : 0); }
      /** @param {Label} l @returns {number} */
      function twice(l) { return size(l) * 2; }
      export function run() { return twice({ text: 'abc', loud: true }) + twice({ text: 'de' }); }
      """);
    Assert.That(IrInterpreter.Call(module, "run", []), Is.EqualTo(210.0));
    Assert.That(RunCSharp(module, "run"), Is.EqualTo(210.0));
    WritesJavaScript(module);
  }

  /// <summary>Equivalence: an optional truth value compared with one, on either side, strictly and loosely - undefined equals neither.</summary>
  [TestCase(0, 1001.0)]
  [TestCase(1, 11.0)]
  [TestCase(2, 1100.0)]
  public void OptionalTruthValue_EqualsNeitherWhenUndefined(int which, double expected) {
    var module = Import("""
      /**
       * @typedef {Object} Flags
       * @property {bool} [on]
       */
      /** @param {Flags} f @returns {number} */
      function code(f) { return (f.on !== false ? 1 : 0) + (f.on === true ? 10 : 0) + (false == f.on ? 100 : 0) + (true != f.on ? 1000 : 0); }
      /** @param {int32} which */
      export function run(which) { return which === 0 ? code({}) : which === 1 ? code({ on: true }) : code({ on: false }); }
      """);
    Assert.That(IrInterpreter.Call(module, "run", [which]), Is.EqualTo(expected));
    Assert.That(RunCSharp(module, "run", which), Is.EqualTo(expected));
    WritesJavaScript(module);
  }

  /// <summary>Equivalence: an integer null reaches equals no integer - null === 0 is false.</summary>
  [TestCase(0, 10.0)]
  [TestCase(1, 1.0)]
  public void OptionalInteger_IsNoZeroWhenNull(int set, double expected) {
    var module = Import("""
      class Box { constructor() { this.n = null; } }
      /** @param {int32} set */
      export function zero(set) { const b = new Box(); if (set === 1) b.n = 0; return (b.n === 0 ? 1 : 0) + (b.n !== 0 ? 10 : 0); }
      """);
    Assert.That(IrInterpreter.Call(module, "zero", [set]), Is.EqualTo(expected));
    Assert.That(RunCSharp(module, "zero", set), Is.EqualTo(expected));
  }

  /// <summary>Error path: the other operand of such a comparison runs only where the optional one is present, so it may have no effect.</summary>
  [Test]
  public void ComparisonWithAnEffect_IsRefused() {
    var refusal = Refusal("""
      /**
       * @typedef {Object} Flags
       * @property {bool} [on]
       */
      /** @returns {boolean} */
      function g() { return true; }
      /** @param {Flags} f @returns {number} */
      function code(f) { return f.on === g() ? 1 : 0; }
      export function run() { return code({}); }
      """);
    Assert.That(refusal.Message, Does.Contain("a comparison with f.on, which may be absent, of an operand with an effect"));
  }

  /// <summary>Error path: keys in another order than the @property tags are taken only where no value has an effect.</summary>
  [Test]
  public void KeysOutOfOrderWithAnEffect_AreRefused() {
    var refusal = Refusal(Opts + """
      /** @returns {int} */
      function g() { return 2; }
      /** @param {Opts} o @returns {number} */
      function f(o) { return o.blockSize; }
      export function run() { return f({ pad: g(), blockSize: 1 }); }
      """);
    Assert.That(refusal.Message, Does.Contain("an object literal of Opts whose keys are in another order than its @property tags and whose values have an effect"));
  }

  /// <summary>Scenario: a required key left out is refused, naming the key.</summary>
  [Test]
  public void RequiredKeyLeftOut_IsRefusedByName() {
    var refusal = Refusal(Opts + """
      /** @param {Opts} o @returns {number} */
      function f(o) { return o.blockSize; }
      export function run() { return f({ pad: 3 }); }
      """);
    Assert.That(refusal.Code, Is.EqualTo("PLY1105"));
    Assert.That(refusal.Message, Does.Contain("blockSize"));
  }

  /// <summary>Error path: a key the typedef does not have, and a value of another kind, are refused by name.</summary>
  [TestCase("f({ blockSize: 1, extra: 2 })", "PLY1105", "extra")]
  [TestCase("f({ blockSize: 'a' })", "PLY1102", "string where int32 is expected")]
  public void LiteralThatDoesNotFit_IsRefused(string call, string code, string reason) {
    var refusal = Refusal(Opts + $$"""
      /** @param {Opts} o @returns {number} */
      function f(o) { return o.blockSize; }
      export function run() { return {{call}}; }
      """);
    Assert.That(refusal.Code, Is.EqualTo(code));
    Assert.That(refusal.Message, Does.Contain(reason));
  }

  /// <summary>Error path: a write into the record is out of scope and refused by name.</summary>
  [Test]
  public void WriteIntoTheRecord_IsRefused() {
    var refusal = Refusal(Opts + """
      /** @param {Opts} o @returns {number} */
      function f(o) { o.pad = 2; return o.blockSize; }
      export function run() { return f({ blockSize: 3 }); }
      """);
    Assert.That(refusal.Message, Does.Contain("a write into the field pad of the @typedef Opts"));
  }

  /// <summary>Error path: a typedef of another form, or with a property of no supported type, is no JSDoc type (PLY1101).</summary>
  [TestCase("@typedef {Array} Opts")]
  [TestCase("@typedef {number|string} Opts")]
  [TestCase("@typedef {Object} Opts\n * @property {function} blockSize")]
  [TestCase("@typedef {Object} Opts\n * @property {Opts} blockSize")]
  [TestCase("@typedef {Object} Other\n * @property {int} blockSize")]
  public void TypedefOutsideTheSubset_IsNoType(string typedef) {
    var refusal = Refusal($$"""
      /**
       * {{typedef}}
       */
      /** @param {Opts} o @returns {number} */
      function f(o) { return 1; }
      export function run() { return f({ blockSize: 3 }); }
      """);
    Assert.That(refusal.Code, Is.EqualTo("PLY1101"));
    Assert.That(refusal.Message, Does.Contain("parameter 'o' of 'f' has no supported JSDoc type"));
  }

  /// <summary>Boundary: a typedef without properties is a class without fields, which only {} fits.</summary>
  [Test]
  public void TypedefWithoutProperties_TakesTheEmptyLiteral() {
    var module = Import("""
      /** @typedef {Object} Nothing */
      /** @param {Nothing} o @returns {number} */
      function f(o) { return 4; }
      export function run() { return f({}); }
      """);
    Assert.That(IrInterpreter.Call(module, "run", []), Is.EqualTo(4.0));
  }

  /// <summary>Equivalence: @prop for @property, a typedef of a class of the file and of another typedef, and a key of this tested as a typedef's is.</summary>
  [Test]
  public void NestedTypesAndThis_AreTypedAsStated() {
    var module = Import("""
      class Counter { constructor() { this.n = null; } /** @param {int32} v */ put(v) { this.n = v; } /** @returns {number} */ get() { return (this.n === null ? 40 : this.n) * 1; } }
      /**
       * @typedef {Object} Inner
       * @prop {int} [v]
       */
      /**
       * @typedef {Object} Outer
       * @property {Inner} inner
       * @property {Counter} counter
       */
      /** @param {Outer} o @returns {number} */
      function f(o) { return (o.inner.v === undefined ? 1 : o.inner.v) + o.counter.get(); }
      export function run() { return f({ inner: {}, counter: new Counter() }) + f({ inner: { v: 2 }, counter: new Counter() }); }
      """);
    Assert.That(IrInterpreter.Call(module, "run", []), Is.EqualTo(83.0));
    Assert.That(RunCSharp(module, "run"), Is.EqualTo(83.0));
    WritesJavaScript(module);
  }

  /// <summary>Error path: a property named twice makes no type; a literal with a spread or a key twice is refused by name.</summary>
  [TestCase("@property {int} blockSize\n * @property {int} [blockSize]", "f({ blockSize: 1 })", "PLY1101", "parameter 'o' of 'f' has no supported JSDoc type")]
  [TestCase("@property {int} blockSize", "f({ ...{ blockSize: 1 } })", "PLY1102", "a spread in an object literal")]
  [TestCase("@property {int} blockSize", "f({ blockSize: 1, blockSize: 2 })", "PLY1102", "the key blockSize twice in an object literal")]
  public void MalformedTypedefOrLiteral_IsRefused(string properties, string call, string code, string reason) {
    var refusal = Refusal($$"""
      /**
       * @typedef {Object} Opts
       * {{properties}}
       */
      /** @param {Opts} o @returns {number} */
      function f(o) { return o.blockSize; }
      export function run() { return {{call}}; }
      """);
    Assert.That(refusal.Code, Is.EqualTo(code));
    Assert.That(refusal.Message, Does.Contain(reason));
  }

  /// <summary>Boundary: || of an object that may be null and one of another class stays refused; only a typedef takes an object literal there.</summary>
  [Test]
  public void OrOfTwoClasses_StaysRefused() {
    var refusal = Refusal("""
      class A { constructor() { this.x = 1; } }
      class B { constructor() { this.y = 2; } }
      /** @param {A|null} a @returns {number} */
      function f(a) { const c = a || new B(); return 1; }
      export function run() { return f(null); }
      """);
    Assert.That(refusal.Message, Does.Contain("|| on A or null and B"));
  }

}
