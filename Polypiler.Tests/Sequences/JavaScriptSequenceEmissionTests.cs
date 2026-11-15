using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Sequences;

/// <summary>
/// Lazy sequences written by the JavaScript backend (#1327): every level writes the module of
/// iterators and parses at that level - generators from ECMAScript 2015, a state machine below. That
/// the code computes what the interpreter computes is decided by node in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class JavaScriptSequenceEmissionTests {

  private static IEnumerable<JavaScriptProfile> Levels() => [
    .. JavaScriptProfile.LevelNames.Select(level => JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm")),
    JavaScriptProfile.Parse("es5", "cjs", typedArrays: true)
  ];

  /// <summary>Test design 1 and 8: every level writes the module and it parses at that level.</summary>
  [TestCaseSource(nameof(Levels))]
  public void Iterators_ParseAtEveryLevel(JavaScriptProfile profile) {
    var module = SequenceModules.Module();
    Assert.That(JavaScriptEmitter.Check(module, profile), Is.Empty, profile.ToString());
    var text = JavaScriptEmitter.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, $"{profile}\n{text}");
  }

  /// <summary>Test design 8: from ECMAScript 2015 the generators of the language; a restartable sequence calls its generator anew.</summary>
  [Test]
  public void Es2015_WritesGenerators() {
    var text = JavaScriptEmitter.Emit(SequenceModules.Module(), JavaScriptProfile.Parse("es2015", "esm")).Single().Content;
    Assert.That(text, Does.Contain("function* TracedOnce(t) {"));
    Assert.That(text, Does.Contain("const $make = function* (t) {"));
    Assert.That(text, Does.Contain("return { [Symbol.iterator]: () => $make(t) };"));
    Assert.That(text, Does.Contain("yield* Up(2);"));
    Assert.That(text, Does.Contain("for (const v of s) {"));
  }

  /// <summary>Test design 8: at ECMAScript 5 no generator syntax, a state machine on $generator, a loop over next().</summary>
  [Test]
  public void Es5_WritesAStateMachine() {
    var text = JavaScriptEmitter.Emit(SequenceModules.Module(), JavaScriptProfile.Parse("es5", "cjs")).Single().Content;
    Assert.That(text, Does.Not.Contain("function*").And.Not.Contain("yield").And.Not.Contain("Symbol"));
    Assert.That(text, Does.Contain("return $generator(function ($g) {"));
    Assert.That(text, Does.Contain("$machine: for (; ; ) {"));
    Assert.That(text, Does.Contain("return { $iterator: function () {"));
    Assert.That(text, Does.Contain("$it[\"return\"]();"));
  }

}
