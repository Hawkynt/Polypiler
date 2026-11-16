using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Functions;

/// <summary>
/// Closures made in a loop and kept in a list of function values (#1396). A <c>let</c> in the head of a
/// <c>for</c> binds a fresh variable per pass (ECMA-262 §14.7.4.2, CreatePerIterationEnvironment), so
/// every closure returns the value of its own pass; a <c>var</c> is one binding, which every closure
/// reads after the loop. The import used to stop with an internal compiler error while it named the
/// type of the list. The expected values are node's on the same source.
/// </summary>
[TestFixture]
public sealed class JavaScriptLoopClosureTests {

  private const string Source = """
    /**
     * @param {number} n
     * @returns {number}
     */
    export function sumOfCaptured(n) {
      const fs = [];
      for (let i = 0; i < n; i++)
        fs.push(() => i);
      let total = 0;
      for (const f of fs)
        total += f();
      return total;
    }
    /**
     * @param {number} n
     * @returns {number}
     */
    export function sumOfShared(n) {
      const fs = [];
      for (var i = 0; i < n; i++)
        fs.push(() => i);
      let total = 0;
      for (const f of fs)
        total += f();
      return total;
    }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>Boundaries and equivalence classes: no pass, one pass, several; a binding per pass and one binding for all.</summary>
  [TestCase("sumOfCaptured", 0, 0)]
  [TestCase("sumOfCaptured", 1, 0)]
  [TestCase("sumOfCaptured", 4, 6)]
  [TestCase("sumOfShared", 0, 0)]
  [TestCase("sumOfShared", 1, 1)]
  [TestCase("sumOfShared", 4, 16)]
  public void ClosuresOfALoop_SeeTheirBinding(string function, double n, double expected) {
    var module = Import(Source);
    Assert.That(IrInterpreter.Call(module, function, [n]), Is.EqualTo(expected));
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(GeneratedCSharp.Compile(text, false).Method(function).Invoke(null, [n]), Is.EqualTo(expected), text);
  }

  /// <summary>The list holds function values: a list of delegates in C#, an Array of functions in JavaScript, whose loop binds per pass.</summary>
  [Test]
  public void ListOfFunctionValues_IsAListOfDelegates() {
    var module = Import(Source);
    var text = LegalizedCSharp.Emit(module, CSharpProfile.Default)[0].Content;
    Assert.That(text, Does.Contain("List<global::System.Func<double>>"));
    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
    Assert.That(script, Does.Contain("for (let i = 0;"));
  }

  /// <summary>Regression: the snippet of the report, a function the file does not export, imports without an internal compiler error.</summary>
  [Test]
  public void ReportedSnippet_ImportsWithoutAnInternalError() {
    var result = JavaScriptFrontend.Import("loopClosures.js", Source.Replace("export function", "function"));
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.None.EqualTo("PLY0001"));
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
  }

  /// <summary>Error path: a list that holds a function value and a number is a conflict, named as such.</summary>
  [Test]
  public void FunctionsAndNumbersInOneList_AreRefused() {
    var result = JavaScriptFrontend.Import("m.mjs", "export function f() { const fs = []; fs.push(() => 1); fs.push(2); return fs.length; }");
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.None.EqualTo("PLY0001"));
    Assert.That(result.Diagnostics, Is.Not.Empty);
  }

}
