using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Objects;

/// <summary>
/// Object literals where objects of a class are kept (#1388), as the Cipher framework keeps its test
/// vectors: an algorithm fills its <c>TestCase[]</c> field with literals, which <c>RegisterAlgorithm</c>
/// turns into <c>TestCase</c> objects, copying the keys the class lacks onto them. A record shape that
/// meets objects of a class is an object of a class synthesized for it, which runs no constructor and
/// which <c>instanceof</c> does not take; a <c>new C</c> that keys are copied into is one of a class
/// synthesized from <c>C</c> for the keys of the class they come from. The expected values are node's.
/// </summary>
[TestFixture]
public sealed class JavaScriptRecordAndClassTests {

  /// <summary>The classes and the registration of the framework, as <c>AlgorithmFramework.js</c> writes them.</summary>
  private const string Framework = """
    class LinkItem {
      /** @param {string} text @param {string} uri */
      constructor(text, uri) { /** @type {string} */ this.text = text; /** @type {string} */ this.uri = uri; }
    }
    class TestCase extends LinkItem {
      /** @param {uint8[]} input @param {uint8[]} expected @param {string} description @param {string} uri */
      constructor(input, expected, description = '', uri = '') {
        super(description, uri);
        /** @type {uint8[]} */ this.input = input;
        /** @type {uint8[]} */ this.expected = expected;
      }
    }
    class Algorithm { constructor() { /** @type {TestCase[]} */ this.tests = []; } }
    function process(test, index) {
      if (!(test instanceof TestCase)) {
        const description = test.text || `Test vector #${index + 1}`;
        const uri = test.uri || '';
        const result = new TestCase(test.input, test.expected, description, uri);
        Object.keys(test).forEach(key => {
          if (!['input', 'expected', 'text', 'uri'].includes(key))
            result[key] = test[key];
        });
        return result;
      }
      return test;
    }
    /** @param {Algorithm} algorithm */
    function register(algorithm) { algorithm.tests = algorithm.tests.map((test, index) => process(test, index)); }

    """;

  /// <summary>chacha20's vectors: two literals of text, uri, key, nonce, counter, input and expected, the second without a text.</summary>
  private const string ChaCha = Framework + """
    class ChaCha extends Algorithm {
      constructor() {
        super();
        this.tests = [
          { text: "Block 0", uri: "u1", key: [1, 2], nonce: [3], counter: 1, input: [4, 5], expected: [6] },
          { text: "", uri: "u2", key: [7], nonce: [8, 9], counter: 2, input: [10], expected: [11, 12] }
        ];
      }
    }
    export function f() {
      const a = new ChaCha();
      register(a);
      const first = a.tests[0];
      const second = a.tests[1];
      return `${first.text}|${second.text}|${second.uri}|${first instanceof TestCase}|${Object.keys(first).join(",")}|${first.input.length}${second.expected.length}`;
    }
    """;

  /// <summary>TestCase objects, literals and both in one array; literals of no extra key, of one and of four.</summary>
  private const string Kinds = Framework + """
    /** @param {Algorithm} algorithm */
    function describe(algorithm) {
      let text = "";
      for (const test of algorithm.tests)
        text += `[${test.text}/${test.uri}/${test instanceof TestCase}/${Object.keys(test).join(",")}/${test.input.length}${test.expected.length}]`;
      return text;
    }
    class Mixed extends Algorithm {
      constructor() {
        super();
        this.tests = [
          new TestCase([1], [2, 3], "made", "m"),
          { text: "written", input: [4, 5, 6], expected: [7], key: [8] }
        ];
      }
    }
    class Made extends Algorithm { constructor() { super(); this.tests = [new TestCase([9], [], "only", "o")]; } }
    class None extends Algorithm { constructor() { super(); this.tests = [{ text: "", uri: "u", input: [1], expected: [] }]; } }
    class Many extends Algorithm { constructor() { super(); this.tests = [{ text: "m", uri: "", key: [1], iv: [2, 3], nonce: [4], counter: 5, input: [], expected: [6] }]; } }
    export function f() {
      const before = new Mixed();
      const literal = before.tests[1];
      const made = before.tests[0];
      const algorithms = [new Mixed(), new Made(), new None(), new Many()];
      let text = `${literal instanceof TestCase}${made instanceof TestCase}`;
      for (const algorithm of algorithms) {
        register(algorithm);
        text += describe(algorithm);
      }
      return text;
    }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static Diagnostic Refusal(string source) => JavaScriptFrontend.Import("m.mjs", source).Diagnostics.First();

  private static void Agrees(string source, object expected) {
    var module = Import(source);
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method("f").Invoke(null, []), Is.EqualTo(expected), text);
    foreach (var level in new[] { "es5", "es2015", "es2022" }) {
      var profile = JavaScriptProfile.Parse(level);
      var script = LegalizedJavaScript.Emit(module, profile).Single().Content;
      Assert.That(GeneratedJavaScript.Problems(script, profile), Is.Empty, script);
    }
  }

  /// <summary>Scenario: chacha20's vectors are registered - literals of text, uri, key, nonce, counter, input and expected become TestCase objects with the extra keys.</summary>
  [Test]
  public void VectorsWrittenAsLiterals_AreRegisteredAsTestCases()
    => Agrees(ChaCha, "Block 0|Test vector #2|u2|true|text,uri,input,expected,key,nonce,counter|22");

  /// <summary>Test design 1-3: TestCase objects, literals and both are one type; no extra key, one and four copied; an empty text and a text, as _processTestVector makes them.</summary>
  [Test]
  public void EveryKindOfVector_IsOneTypeWithItsKeys()
    => Agrees(Kinds, "falsetrue[made/m/true/text,uri,input,expected/12][written//true/text,uri,input,expected,key/31][only/o/true/text,uri,input,expected/10]"
      + "[Test vector #1/u/true/text,uri,input,expected/10][m//true/text,uri,input,expected,key,iv,nonce,counter/01]");

  /// <summary>The literals are objects of a class derived from TestCase whose own fields are the keys it lacks, in their order; the object copied into is one of a class with the keys copied.</summary>
  [Test]
  public void SynthesizedClasses_HaveTheKeysTheClassLacks() {
    var classes = Import(ChaCha).Types.OfType<IrClassType>().ToDictionary(type => type.Name);
    Assert.That(classes["TestCaseLiteral"].Base, Is.SameAs(classes["TestCase"]));
    Assert.That(classes["TestCaseLiteral"].Fields.Select(field => (field.Name, field.Type.ToString())), Is.EqualTo(new[] { ("key", "i32[]"), ("nonce", "i32[]"), ("counter", "i32") }));
    Assert.That(classes["TestCaseLiteral"].Constructors.Single().SkipsBase, Is.True);
    Assert.That(classes["TestCaseExtended"].Fields.Select(field => field.Name), Is.EqualTo(new[] { "key", "nonce", "counter" }));
    Assert.That(classes["TestCaseExtended"].Constructors.Single().SkipsBase, Is.False);
  }

  /// <summary>
  /// A field of the class a literal lacks holds undefined; one shape meeting two classes derives from the
  /// class both derive from, and instanceof of either is false; keys copied as a constant; a class of the
  /// file named as the made one would be gives way.
  /// </summary>
  [TestCase("class P { constructor(a) { this.a = a; this.n = 1.5; this.s = 'x'; } }\nclass H { constructor() { /** @type {P[]} */ this.items = [new P(1), { a: 2 }]; } }\n"
    + "export function f() { const h = new H(); const p = h.items[1]; const q = h.items[0]; return `${p.a}${p.n === undefined}${p.s === undefined}${q.n}${q.s}`; }", "2truetrue1.5x")]
  [TestCase("class A { constructor() { this.v = 0; } }\nclass B extends A { }\nclass C extends A { }\n"
    + "class H { constructor() { this.b = new B(); this.c = new C(); this.b = { v: 1, w: 2 }; this.c = { v: 3, w: 4 }; } }\n"
    + "export function f() { const h = new H(); const b = h.b; const c = h.c; return `${b.v}${c.v}${b instanceof B}${b instanceof A}${c instanceof C}`; }", "13falsefalsefalse")]
  [TestCase(Framework + "class A extends Algorithm { constructor() { super(); this.tests = [{ text: 'a', input: [1], expected: [2], seed: [9] }]; } }\n"
    + "export function f() { const a = new A(); const t = a.tests[0]; const before = Object.keys(t).length; register(a); const u = a.tests[0]; return `${before * 10 + Object.keys(u).length}`; }", "45")]
  [TestCase("class C { constructor() { this.a = 1; } }\nclass CLiteral { }\n"
    + "export function f() { const list = [new C(), { a: 2, b: 3 }]; const o = list[1]; const t = new C(); Object.keys(o).forEach(key => { t[key] = 7; }); return Object.keys(t).join(',') + t.a; }", "a,b7")]
  public void LiteralsMeetingClasses_ComputeWhatNodeComputes(string source, string expected) => Agrees(source, expected);

  /// <summary>Scenario and test design 4: a key of two kinds is refused, in one array and in two; what stays out is refused by name.</summary>
  [TestCase("class A extends Algorithm { constructor() { super(); this.tests = [{ text: 'a', input: [1], expected: [2], key: 'k' }, { text: 'b', input: [1], expected: [2], key: [3] }]; } }\n"
    + "export function f() { const a = new A(); register(a); return a.tests.length; }", "PLY1105", "'test' holds both { text, input, expected, key } and { text, input, expected, key }")]
  [TestCase("class A extends Algorithm { constructor() { super(); this.tests = [{ text: 'a', input: [1], expected: [2], key: 'k' }]; } }\n"
    + "class B extends Algorithm { constructor() { super(); this.tests = [{ text: 'b', input: [1], expected: [2], key: [3] }]; } }\n"
    + "export function f() { const a = new A(); register(a); const b = new B(); register(b); return a.tests.length + b.tests.length; }", "PLY1105", "'key' holds both string and int32[]")]
  [TestCase("class A extends Algorithm { constructor() { super(); this.tests = [{ text: 'a', input: [1], expected: [2] }]; } }\n"
    + "export function f() { const a = new A(); return a.tests[0] instanceof TestCase; }", "PLY1102", "'instanceof of a.tests[0], which may be an object literal and is no variable' is not supported yet")]
  [TestCase("class A extends Algorithm { constructor() { super(); this.tests = [{ text: 'a', input: [1], expected: [2] }]; } }\n"
    + "export function f() { const a = new A(); let n = 0; Object.keys(a.tests[0]).forEach(key => { n += 1; }); return n; }", "PLY1102",
    "'Object.keys of a.tests[0], which may be an object of a synthesized class and is no variable' is not supported yet")]
  public void FrameworkOutOfScope_IsRefused(string algorithms, string code, string message) {
    var refusal = Refusal(Framework + algorithms);
    Assert.That((refusal.Code, refusal.Message), Is.EqualTo((code, message)));
  }

  /// <summary>A class of the library meets no literal; keys copied from a variable assigned again, or under a condition, are refused.</summary>
  [TestCase("class E extends Error { constructor(m) { super(m); this.code = 1; } }\nexport function f() { let e = new E('x'); e = { code: 2 }; return e.code; }",
    "'e' holds both E and { code }")]
  [TestCase("class C { constructor() { this.a = 1; } }\nfunction g(o) { o = new C(); const t = new C(); Object.keys(o).forEach(key => { t[key] = o[key]; }); return t.a; }\n"
    + "export function f() { const list = [new C(), { a: 2, b: 3 }]; return g(list[1]); }", "'keys copied into new C from o, which is no variable that keeps its object' is not supported yet")]
  [TestCase("class C { constructor() { this.a = 1; } }\nexport function f() { const r = { a: 2, b: 3, c: 4 }; const list = [new C(), r]; const o = list[1]; const t = new C(); "
    + "Object.keys(o).forEach(key => { if (key !== 'c') t[key] = o[key]; }); return Object.keys(t).join(','); }", "'the key c of CExtended, which is not copied into new C on every pass' is not supported yet")]
  [TestCase("class C { constructor() { this.a = 1; } }\nexport function f() { const list = [new C(), { a: 2, b: 3, c: 4 }]; const o = list[1]; const t = new C(); "
    + "Object.keys(o).forEach(key => { if (!['c'].includes(key)) t[key] = o[key]; }); return Object.keys(t).join(','); }", "'the key c of CExtended, which is not copied into new C on every pass' is not supported yet")]
  [TestCase("class A { constructor() { this.v = 0; } }\nclass B { constructor() { this.v = 0; } }\n"
    + "class H { constructor() { this.a = new A(); this.b = new B(); this.a = { v: 1 }; this.b = { v: 2 }; } }\nexport function f() { const h = new H(); return h.a.v; }", "'b' holds both B and { v }")]
  [TestCase("const K = Object.freeze({ A: { name: 'x' } });\nclass H { constructor() { this.k = K.A; this.k = { name: 'y' }; } }\nexport function f() { const h = new H(); return h.k.name.length; }",
    "'k' holds both K and { name }")]
  public void OutOfScope_IsRefusedByName(string source, string message) => Assert.That(Refusal(source).Message, Is.EqualTo(message));

  /// <summary>A private field of a class a literal meets is out of reach of the class made for it in a class of ECMAScript 2022, and a constructor function writes it.</summary>
  [Test]
  public void PrivateFieldOfTheBase_IsInexpressibleWithPrivateNames() {
    var module = Import("class P { #s = 1; constructor(a) { this.a = a; } get s() { return this.#s; } }\nclass H { constructor() { /** @type {P[]} */ this.items = [new P(1), { a: 2 }]; } }\n"
      + "export function f() { const h = new H(); const p = h.items[1]; const q = h.items[0]; return `${p.a}${q.s}`; }");
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo("21"));
    var problems = LegalizedJavaScript.Check(module, JavaScriptProfile.Parse("es2022")).Select(problem => problem.Message);
    Assert.That(problems, Is.EqualTo(new[] { "'a constructor that skips a base class with private fields' of the class 'PLiteral' cannot be expressed in JavaScript profile 'es2022'" }));
    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Parse("es5")).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Parse("es5")), Is.Empty, script);
  }

  /// <summary>The JavaScript translation, at ES5 and at ES2022, computes what node computes of the original.</summary>
  [Test]
  [Category("EndToEnd")]
  public void JavaScriptTranslation_ComputesWhatNodeComputes() {
    var work = Directory.CreateTempSubdirectory("polypiler-record-class-").FullName;
    try {
      foreach (var source in new[] { ChaCha, Kinds }) {
        File.WriteAllText(Path.Combine(work, "original.mjs"), source);
        var expected = Node(work, "original.mjs");
        var module = Import(source);
        // A constructor function of ECMAScript 5 in a CommonJS module, a class of ECMAScript 2022 in an ES module.
        foreach (var (level, format, file) in new[] { ("es5", "cjs", "es5.cjs"), ("es2022", "esm", "es2022.mjs") }) {
          File.WriteAllText(Path.Combine(work, file), LegalizedJavaScript.Emit(module, JavaScriptProfile.Parse(level, format)).Single().Content);
          Assert.That(Node(work, file), Is.EqualTo(expected), level);
        }
      }
    } finally {
      Directory.Delete(work, true);
    }
  }

  /// <summary>What node prints of <c>f()</c> of a module.</summary>
  private static string Node(string directory, string file) {
    var start = new ProcessStartInfo("node", ["-e", $"import('./{file}').then(m => console.log(m.f()))"]) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.Zero, error);
    return output.TrimEnd('\n', '\r');
  }

}
