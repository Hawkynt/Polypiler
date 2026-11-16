using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Sequences;

/// <summary>
/// The JavaScript round trip of a JSDoc <c>T[]</c> that nothing in the file decides (#1404): the IR
/// holds it as a typed array (#1374), and the callers the code was written for pass Arrays. The copy
/// <c>data.slice()</c> is the kind of array the caller passed - <c>new data.constructor(data)</c> of an
/// Array is an Array of one element. The expected values are node's, of the original.
/// </summary>
[TestFixture]
public sealed class JavaScriptStatedArrayRoundTripTests {

  private const string Copy = """
    /**
     * @param {uint8[]} data
     * @param {int32} value
     * @returns {uint8[]}
     */
    export function copy(data, value) { const c = data.slice(); c[0] = value & 0xFF; c[1] = 255; return c; }
    /**
     * @param {uint8[]} data
     * @returns {int32}
     */
    export function total(data) { const c = data.slice(); let s = 0; for (let i = 0; i < c.length; i++) s += c[i]; return s; }
    /**
     * @param {uint8[]} data
     * @returns {int32}
     */
    export function part(data) { const c = data.slice(1, 3); return (c[0] * 10 + c[1] + c.length * 100) | 0; }
    """;

  /// <summary>Calls both functions with an Array and a Uint8Array and prints what they return, with the kind of array.</summary>
  private const string Probe = """
    const m = await import(process.argv[2]);
    for (const make of [a => a, a => new Uint8Array(a)]) {
      for (const v of [255, 256, 300]) {
        const r = m.copy(make([1, 2, 3]), v);
        console.log(make([]).constructor.name, v, r.constructor.name, JSON.stringify(Array.from(r)), m.total(make([1, 2, 3])), m.part(make([1, 2, 3])));
      }
    }
    """;

  /// <summary>Runs the recorded vectors of one Cipher file on its translation, with Arrays or, given a third argument 'typed', typed arrays.</summary>
  private const string Kat = """
    import fs from 'node:fs';
    import path from 'node:path';
    import { pathToFileURL } from 'node:url';
    const [translated, golden, kind] = process.argv.slice(2);
    const copy = path.join(path.dirname(translated), 'kat-entry.mjs');
    fs.writeFileSync(copy, fs.readFileSync(translated, 'utf8') + '\nexport { Algorithms as $Algorithms };\n');
    const module = await import(pathToFileURL(copy).href);
    const kats = JSON.parse(fs.readFileSync(golden, 'utf8'));
    const value = v => v.t === 'bytes' ? (kind === 'typed' ? new Uint8Array(Buffer.from(v.v, 'hex')) : [...Buffer.from(v.v, 'hex')]) : v.t === 'number' ? Number(v.v) : v.t === 'string' ? v.v : v.t === 'bool' ? v.v : v.t === 'null' ? null : v.t === 'array' ? v.v.map(value) : v.v.map(Number);
    let passed = 0, failed = 0;
    for (const algorithm of kats.algorithms) {
      const registered = module.$Algorithms.find(a => a.name === algorithm.name);
      for (const vector of algorithm.vectors) {
        if (vector.node?.outcome !== 'passed' || !vector.expected) continue;
        try {
          const instance = registered.CreateInstance(vector.inverse);
          for (const step of vector.steps) {
            if (step.call) instance[step.name](...step.values.map(value)); else instance[step.name] = value(step.values[0]);
          }
          instance.Feed(value(vector.input));
          if (Buffer.from(instance.Result()).toString('hex') === vector.expected.v) ++passed; else ++failed;
        } catch (error) {
          ++failed;
        }
      }
    }
    console.log(`${passed} passed, ${failed} failed`);
    """;

  private string _work = "";

  [SetUp]
  public void CreateWork() => this._work = Scratch.Create("polypiler-stated-array-");

  [TearDown]
  public void DeleteWork() => Scratch.Delete(this._work);

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static ProcessResult Run(IReadOnlyList<string> argv) => new ProcessRunner().Run(argv, TimeSpan.FromMinutes(2));

  private string Node(string script, params string[] arguments) {
    var path = Path.Combine(this._work, $"probe-{Guid.NewGuid():N}.mjs");
    File.WriteAllText(path, script);
    var result = Run(["node", path, .. arguments]);
    Assert.That(result.ExitCode, Is.Zero, result.StandardError);
    return result.StandardOutput.ReplaceLineEndings("\n");
  }

  /// <summary>Test design 2 and 3: the translation copies an Array as an Array and a typed array as a typed array, a range of either too (slice, which both have), and stores 255 as the original does, with either.</summary>
  [Test]
  [Category("EndToEnd")]
  public void Copy_IsTheKindOfArrayTheCallerPassed() {
    var original = Path.Combine(this._work, "original.mjs");
    File.WriteAllText(original, Copy);
    var translated = Path.Combine(this._work, "translated.mjs");
    File.WriteAllText(translated, LegalizedJavaScript.Emit(Import(Copy), JavaScriptProfile.Default).Single().Content);
    var expected = this.Node(Probe, new Uri(original).AbsoluteUri);
    Assert.That(expected, Does.Contain("Array 256 Array [0,255,3] 6 223").And.Contain("Uint8Array 300 Uint8Array [44,255,3] 6 223"), "node's own values");
    Assert.That(this.Node(Probe, new Uri(translated).AbsoluteUri), Is.EqualTo(expected));
  }

  /// <summary>
  /// <c>checksum/isbn.js</c> of the fixture <c>cipher.algorithms</c>, at the commit the fixture pins and
  /// checked against the lock; the fixture's own pattern of files is no single file to open.
  /// </summary>
  private static string Isbn() {
    var root = PrototypeFixture.RepositoryRoot;
    var manifest = CorpusManifest.Load(Path.Combine(root, CorpusManifest.RepositoryPath));
    var fixture = manifest.Fixture("cipher.algorithms")!;
    var repository = fixture.PinOf(manifest.Repository(fixture.Repository)!);
    const string file = "Cipher/algorithms/checksum/isbn.js";
    var entry = CorpusLock.Load(Path.Combine(root, CorpusLock.RepositoryPath)).Find(repository.Name, repository.Commit, file);
    Assert.That(entry, Is.Not.Null, $"{file} is not in the corpus lock");
    Assert.That(CorpusVerifier.VerifyFile(root, entry!), Is.Null, "run 'polypiler-tooling corpus import --fixture cipher.algorithms'");
    return CorpusImporter.FilePath(root, repository.Name, repository.Commit, file);
  }

  /// <summary>Scenario: checksum/isbn.js translated to JavaScript passes its 6 recorded vectors with the Arrays node passes, and with typed arrays.</summary>
  [TestCase(false)]
  [TestCase(true)]
  [Category("EndToEnd")]
  public void Isbn_PassesItsVectorsWithArrays(bool typed) {
    var source = Isbn();
    var configuration = new DirectoryInfo(Path.GetDirectoryName(typeof(JavaScriptStatedArrayRoundTripTests).Assembly.Location)!).Parent!.Name;
    var cli = Path.Combine(PrototypeFixture.RepositoryRoot, "Polypiler.Cli", "bin", configuration, "net10.0", "polypiler.dll");
    var output = Path.Combine(this._work, "out");
    var translation = Run(["dotnet", cli, "translate", source, "--from", "javascript", "--to", "javascript", "--output", output]);
    Assert.That(translation.ExitCode, Is.Zero, translation.StandardError);
    var golden = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "corpus", "cipher", "checksum", "isbn.kat.json");
    var script = Path.Combine(this._work, "kat.mjs");
    File.WriteAllText(script, Kat);
    var result = Run(["node", script, Path.Combine(output, "isbn.mjs"), golden, typed ? "typed" : "array"]);
    Assert.That(result.StandardOutput.Trim(), Is.EqualTo("6 passed, 0 failed"), result.StandardError);
  }

  /// <summary>Boundary: 255, 0, a byte of the array and a value masked to 8 bits are bytes for an Array and a typed array alike, and are stored as they are.</summary>
  [TestCase("255")]
  [TestCase("0")]
  [TestCase("data[1]")]
  [TestCase("value & 0xFF")]
  public void StoreBothKindsKeep_IsStored(string stored) {
    var module = Import($$"""
      /**
       * @param {uint8[]} data
       * @param {int32} value
       */
      export function f(data, value) { const c = data.slice(); c[0] = {{stored}}; return c[0]; }
      """);
    Assert.That(IrInterpreter.Call(module, "f", [new byte[] { 1, 2, 3 }, 255]), Is.EqualTo(stored == "0" ? (byte)0 : stored == "data[1]" ? (byte)2 : (byte)255));
  }

  /// <summary>Equivalence: a typed array the file creates wraps a store of 256, as node's does.</summary>
  [Test]
  public void DecidedTypedArray_WrapsAsBefore() {
    var module = Import("export function f() { const c = new Uint8Array(2); c[0] = 256 + 7; return c[0]; }");
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo((byte)7));
  }

  /// <summary>Scenario: a function with @param {uint8[]} a that the file calls with [1] and with new Uint8Array(1) is refused with PLY1105, as before.</summary>
  [Test]
  public void BothKindsPassedByTheFile_IsRefused() {
    var result = JavaScriptFrontend.Import("m.mjs", """
      /** @param {uint8[]} a */
      function first(a) { return a[0]; }
      export function g() { return first([1]) + first(new Uint8Array(1)); }
      """);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.Some.EqualTo(JavaScriptFrontend.MixedKinds), string.Join("; ", result.Diagnostics));
  }

}
