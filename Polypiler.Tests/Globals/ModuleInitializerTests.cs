using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Globals;

/// <summary>
/// What a module does when it is loaded (#1365): the statements at the top level of a JavaScript
/// module with state become its initializer, which runs once after the globals have their values - the
/// registration of the Cipher algorithms - and a global may hold objects and collections of them.
/// The IR rule, the interpreter, the format, and both backends: a static constructor in C#, a call at
/// the end of the module in JavaScript.
/// </summary>
[TestFixture]
public sealed class ModuleInitializerTests {

  private const string Registry = """
    const registry = [];
    class Thing {
      constructor(n) { this.n = n | 0; }
    }
    function register(thing) { registry.push(thing); }
    register(new Thing(2));
    register(new Thing(3));
    const extra = new Thing(4);
    if (registry.length < 3) {
      register(extra);
    }
    export function total() {
      let sum = 0;
      for (const thing of registry)
        sum = (sum + thing.n) | 0;
      return sum;
    }
    """;

  private static IrModule Import(string source) {
    var result = JavaScriptFrontend.Import("m.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  [Test]
  public void StatementsAtTheTopLevel_AreTheInitializer_WhichRunsOnceAtLoad() {
    var module = Import(Registry);
    Assert.That(module.Initializer, Is.EqualTo("initialize"));
    var initializer = module.Functions.Single(function => function.Name == "initialize");
    Assert.That((initializer.IsPublic, initializer.Parameters.Count, initializer.ReturnType), Is.EqualTo((false, 0, IrType.Void)));
    var registry = module.Globals.Single();
    Assert.That(registry.Type.ToString(), Is.EqualTo("list<Thing>"));
    var instance = IrInterpreter.Instantiate(module);
    Assert.That(instance.Call("total"), Is.EqualTo(9));
    Assert.That(instance.Call("total"), Is.EqualTo(9), "once, not per call");
  }

  [Test]
  public void Initializer_IsAStaticConstructorInCSharp_AndACallAtTheEndInJavaScript() {
    var module = Import(Registry);
    foreach (var profile in new[] { CSharpProfile.Parse("net2.0"), CSharpProfile.Default }) {
      var text = LegalizedCSharp.Emit(module, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
      Assert.That(text, Does.Contain("static @m()").And.Contain("initialize();"));
      Assert.That(GeneratedCSharp.Compile(text, false).Method("total").Invoke(null, []), Is.EqualTo(9), text);
    }

    var script = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(script, JavaScriptProfile.Default), Is.Empty, script);
    Assert.That(script.TrimEnd(), Does.EndWith("initialize();"));
  }

  [Test]
  public void Initializer_IsNamedApartFromTheFunctionsOfTheModule() {
    var module = Import(Registry.Replace("export function total()", "function initialize() { return 1; }\nexport function total()"));
    Assert.That(module.Initializer, Is.EqualTo("initialize1"));
    Assert.That(IrInterpreter.Instantiate(module).Call("total"), Is.EqualTo(9));
  }

  /// <summary>An assignment, an <c>if</c> and a call at the top level run in order, once; a loop as well.</summary>
  [Test]
  public void AssignmentsAndBranches_RunInOrder() {
    var module = Import("let c = 0;\nc = 5;\nif (c) {\n  c = (c + 1) | 0;\n}\nfor (let i = 0; i < 2; i++)\n  bump();\nfunction bump() { c = (c + 10) | 0; }\nexport function f() { return c; }");
    Assert.That(IrInterpreter.Instantiate(module).Call("f"), Is.EqualTo(26));
  }

  /// <summary>A declaration, an export, a constructor function with its prototype and a wrapper are no statements of the initializer.</summary>
  [Test]
  public void DeclarationsOfEveryKind_StayOutOfTheInitializer() {
    var module = Import("""
      'use strict';
      ;
      let count = 0;
      function Counter() { this.n = 1; }
      Counter.prototype.add = function () { count = (count + this.n) | 0; };
      new Counter().add();
      export function f() { return count; }
      """);
    var initializer = module.Functions.Single(function => function.Name == module.Initializer);
    Assert.That(initializer.Body.Statements, Has.Count.EqualTo(1));
    Assert.That(IrInterpreter.Instantiate(module).Call("f"), Is.EqualTo(1));
    // An immediately invoked function stays what it was: no statement of the initializer, refused where the module has state.
    var wrapped = JavaScriptFrontend.Import("m.mjs", "let count = 0;\n(function () { count = 1; })();\nexport function f() { return count; }");
    Assert.That(wrapped.Diagnostics.Single().Message, Is.EqualTo("'a statement at the top level of the module' is not supported yet"));
  }

  /// <summary>A global of an object that only the initializer of the module assigns and reads starts as null; one of a list is a new list.</summary>
  [Test]
  public void GlobalsOfReferences_AreNullOrANewCollection() {
    var module = Import("""
      const registry = [];
      let pending;
      class Thing { constructor(n) { this.n = n | 0; } }
      function register(thing) { registry.push(thing); }
      pending = new Thing(3);
      register(pending);
      export function f() { return registry.length; }
      """);
    Assert.That(module.Globals.Select(global => (global.Name, global.Initializer is null)), Is.EqualTo(new[] { ("registry", false), ("pending", true) }));
    Assert.That(IrInterpreter.Instantiate(module).Call("f"), Is.EqualTo(1));
  }

  [Test]
  public void ModuleWithoutState_DoesNotRunItsStatements() {
    var module = Import("console.log('loaded');\n/** @param {int32} a @returns {int32} */\nexport function f(a) { return a | 0; }");
    Assert.That((module.Initializer, module.Functions.Single().Name), Is.EqualTo(((string?)null, "f")));
  }

  [Test]
  public void Initializer_RoundTripsThroughTheFormat() {
    var module = Import(Registry);
    Assert.That(IrModuleFormat.Read(IrModuleFormat.Write(module)).Initializer, Is.EqualTo("initialize"));
    var without = Import("/** @param {int32} a @returns {int32} */\nexport function f(a) { return a | 0; }");
    Assert.That(IrModuleFormat.Read(IrModuleFormat.Write(without)).Initializer, Is.Null);
  }

  [TestCase("missing")]
  [TestCase("total")]
  [TestCase("register")]
  public void Initializer_ThatIsNoPrivateFunctionOfNothing_IsRefused(string name) {
    var module = Import(Registry) with { Initializer = name };
    Assert.That(IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message),
      Has.Some.Contains("the initializer of the module is no private function of the module that takes and gives nothing"));
  }

}
