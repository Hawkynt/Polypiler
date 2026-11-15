using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.JavaScript;

/// <summary>
/// A file imported with the files it requires (#1365): the three module shapes - a UMD factory, a
/// wrapper that assigns <c>module.exports</c>, CommonJS - their exports reached by name or through one
/// object, what only probes the environment folded away, what the entry does not reach left out, and
/// every form the link refuses, with the diagnostic in the file it is about. Test design 1 of #1365:
/// a UMD wrapper, plain CommonJS and an ES module are imported, a script that uses globals is refused.
/// </summary>
[TestFixture]
public sealed class JavaScriptModuleGraphTests {

  private static readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "polypiler-modules"));

  private static string At(string name) => Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar));

  /// <summary>A UMD factory as the Cipher files are written: its wrapper requires the files the factory takes.</summary>
  private static string Factory(string parameters, string requires, string body) => $$"""
    // A header comment
    (function (root, factory) {
      if (typeof define === 'function' && define.amd) {
        define([], factory);
      } else if (typeof module === 'object' && module.exports) {
        module.exports = factory({{requires}});
      } else {
        factory(root.A, root.B);
      }
    }((function() {
      if (typeof globalThis !== 'undefined') return globalThis;
      throw new Error('Unable to locate global object');
    })(), function ({{parameters}}) {
    {{body}}
    }));
    """;

  /// <summary>A helper library as OpCodes is written: a wrapper whose object literal is the export.</summary>
  private const string Helpers = """
    (function(global) {
      'use strict';
      const Ops = {
        /** @param {int32} a @param {int32} b @returns {int32} */
        Add: function(a, b) { return (a + b) | 0; },
        /** @param {int32} a @returns {int32} */
        Twice: function(a) { return Ops.Add(a, a); },
        Unused: function(a) { return typeof a; }
      };
      global.Ops = Ops;
      if (typeof module !== 'undefined' && module.exports) {
        module.exports = Ops;
      }
    })(typeof global !== 'undefined' ? global : this);
    """;

  private const string Library = """
      /** @param {int32} a @returns {int32} */
      function square(a) { return Math.imul(a, a); }
      function broken(a) { return typeof a; }
      return { square, broken };
    """;

  private static ImportResult Import(string entry, params (string Name, string Text)[] files) {
    var all = files.ToDictionary(file => At(file.Name), file => file.Text);
    return JavaScriptFrontend.Import(At(entry), all[At(entry)], null, path => all.GetValueOrDefault(path));
  }

  private static IrModule Imported(string entry, params (string Name, string Text)[] files) {
    var result = Import(entry, files);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static Diagnostic Refusal(string entry, params (string Name, string Text)[] files) {
    var result = Import(entry, files);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics, Is.Not.Empty);
    return result.Diagnostics[0];
  }

  [Test]
  public void Factory_IsImportedWithWhatItRequires_AndOnlyWhatItReaches() {
    var module = Imported("algorithms/a.js",
      ("algorithms/a.js", Factory("Lib, Ops", "require('../lib'), require('../ops.js')", """
        if (!Lib) {
          throw new Error('Lib dependency is required');
        }
        const { square } = Lib;
        /** @param {int32} x @returns {int32} */
        function f(x) { return Ops.Twice(square(x)) + Lib.square(2); }
        return { f };
      """)),
      ("lib.js", Factory("", "", Library)),
      ("ops.js", Helpers));
    Assert.That(module.Name, Is.EqualTo("a"));
    Assert.That(module.Functions.Select(function => function.Name), Is.EquivalentTo(new[] { "Add", "Twice", "square", "f" }));
    Assert.That(IrInterpreter.Call(module, "f", [3]), Is.EqualTo(22));
  }

  [Test]
  public void CommonJs_IsImportedWithWhatItRequires() {
    var module = Imported("main.js",
      ("main.js", """
        const Ops = require('./ops');
        /** @param {int32} x @returns {int32} */
        function f(x) { return Ops.Add(x, 1); }
        module.exports = { f };
        """),
      ("ops.js", Helpers));
    Assert.That(IrInterpreter.Call(module, "f", [41]), Is.EqualTo(42));
  }

  [Test]
  public void RequiredDirectory_IsItsIndexFile() {
    var module = Imported("main.js",
      ("main.js", "const Ops = require('./ops');\n/** @param {int32} x @returns {int32} */\nfunction f(x) { return Ops.Twice(x); }\nmodule.exports = { f };"),
      ("ops/index.js", Helpers));
    Assert.That(IrInterpreter.Call(module, "f", [4]), Is.EqualTo(8));
  }

  [Test]
  public void NamespaceUsedAsAValue_IsReachedWhole() {
    var result = Import("main.js",
      ("main.js", "const Ops = require('./ops');\nfunction f() { return Ops; }\nmodule.exports = { f };"),
      ("ops.js", Helpers));
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.File), Has.Some.EqualTo(At("ops.js")), "Unused is imported too, and refused there");
  }

  [Test]
  public void EsModule_AndAScriptWithoutAShape_AreImportedAsTheyAre() {
    var module = Imported("m.mjs", ("m.mjs", "/** @param {int32} a @returns {int32} */\nexport function f(a) { return a | 0; }"));
    Assert.That(module.Functions.Single().Name, Is.EqualTo("f"));
    var script = Imported("s.js", ("s.js", "/** @param {int32} a @returns {int32} */\nfunction g(a) { return a | 0; }"));
    Assert.That(script.Functions.Single().Name, Is.EqualTo("g"));
    var wrapper = Imported("w.js", ("w.js", "(function () { /** @param {int32} a @returns {int32} */ function h(a) { return a | 0; } })();"));
    Assert.That(wrapper.Functions.Single().Name, Is.EqualTo("h"));
  }

  /// <summary>Test design 1: a script that uses what the environment defines is refused by name.</summary>
  [Test]
  public void ScriptWithGlobals_IsRefusedByName() {
    var refusal = Refusal("s.js", ("s.js", "/** @param {int32} a @returns {int32} */\nfunction g(a) { return window.Ops.Add(a, a); }"));
    Assert.That(refusal.Message, Does.Contain("window"));
  }

  [Test]
  public void Diagnostic_PointsIntoTheFileItIsAbout() {
    var refusal = Refusal("a.js",
      ("a.js", Factory("Lib", "require('./lib')", "  /** @param {int32} x @returns {int32} */\n  function f(x) { return Lib.square(x) + typeof x; }\n  return { f };")),
      ("lib.js", Factory("", "", Library)));
    Assert.That((refusal.File, refusal.Line), Is.EqualTo((At("a.js"), 15)));
    var inLibrary = Refusal("a.js",
      ("a.js", Factory("Lib", "require('./lib')", "  function f(x) { return Lib.broken(x); }\n  return { f };")),
      ("lib.js", Factory("", "", Library)));
    Assert.That((inLibrary.File, inLibrary.Line), Is.EqualTo((At("lib.js"), 16)));
  }

  [Test]
  public void SyntaxErrorOfARequiredFile_IsReportedThere() {
    var refusal = Refusal("main.js", ("main.js", "const B = require('./b');\nmodule.exports = { B };"), ("b.js", "function ("));
    Assert.That((refusal.Code, refusal.File), Is.EqualTo((JavaScriptFrontend.SyntaxError, At("b.js"))));
  }

  [TestCase("const B = require('./missing');\nmodule.exports = {};", "'the required file './missing', which cannot be read' is not supported yet")]
  [TestCase("const B = require('lodash');\nmodule.exports = {};", "'the required file 'lodash', which cannot be read' is not supported yet")]
  [TestCase("const B = require('./plain');\nmodule.exports = {};", "'the required file './plain', which is no module' is not supported yet")]
  [TestCase("const B = require('./self');\nmodule.exports = {};", "'a cycle of required files through './self'' is not supported yet")]
  [TestCase("const { x } = require('./ops');\nmodule.exports = {};", "'a required file that is destructured' is not supported yet")]
  [TestCase("const B = require('./ops');\nfunction f() { return B.Add(1, 2); }\nmodule.exports = { f };", "'a module bound to 'B' whose export is the object 'Ops'' is not supported yet")]
  [TestCase("const L = require('./lib');\nfunction f() { return L.cube(1); }\nmodule.exports = { f };", "''cube', which the module 'L' does not export' is not supported yet")]
  [TestCase("const L = require('./lib');\nfunction f() { return L; }\nmodule.exports = { f };", "'the module 'L' as a value' is not supported yet")]
  [TestCase("const L = require('./lib');\nconst { square: s } = L;\nmodule.exports = {};", "'a destructured export under another name' is not supported yet")]
  [TestCase("const L = require('./lib');\nconst { ['square']: square } = L;\nmodule.exports = {};", "'a destructured export under another name' is not supported yet")]
  [TestCase("const L = require('./lib');\nconst { square: { x } } = L;\nmodule.exports = {};", "'a destructured export under another name' is not supported yet")]
  [TestCase("const L = require('./lib');\nconst { ...rest } = L;\nmodule.exports = {};", "'a destructured export under another name' is not supported yet")]
  [TestCase("const L = require('./lib');\nfunction square() { return 1; }\nmodule.exports = { square };", "'the name 'square', which 'lib.js' declares as well' is not supported yet")]
  [TestCase("const L = require('./lib');\nconst { cube } = L;\nmodule.exports = {};", "''cube', which the module 'L' does not export by name' is not supported yet")]
  [TestCase("const O = require('./ops');\nconst { Add } = O;\nmodule.exports = {};", "''Add', which the module 'O' does not export by name' is not supported yet")]
  [TestCase("const R = require('./renamed');\nconst { sq } = R;\nmodule.exports = {};", "''sq', which the module 'R' exports under another name' is not supported yet")]
  [TestCase("const R = require('./renamed');\nconst sq = R.sq;\nmodule.exports = {};", "''sq', which the module 'R' exports under another name' is not supported yet")]
  public void LinkThatIsNotSupported_IsRefusedByName(string main, string message) {
    var refusal = Refusal("self.js",
      ("self.js", main),
      ("plain.js", "function p() { return 1; }"),
      ("renamed.js", "/** @param {int32} a @returns {int32} */\nfunction square(a) { return Math.imul(a, a); }\nmodule.exports = { sq: square };"),
      ("ops.js", Helpers),
      ("lib.js", Factory("", "", Library)));
    Assert.That(refusal.Message, Is.EqualTo(message));
    Assert.That(refusal.File, Is.EqualTo(At("self.js")));
  }

  [TestCase("A, B", "require('./x')", "'the factory parameter 'B', which the wrapper gives no required file' is not supported yet")]
  [TestCase("{ a }", "require('./x')", "'a factory parameter that is no name' is not supported yet")]
  [TestCase("A", "root.A", "'a factory that its wrapper does not call with the files it requires' is not supported yet")]
  public void FactoryThatIsNotSupported_IsRefusedByName(string parameters, string requires, string message) {
    var refusal = Refusal("f.js", ("f.js", Factory(parameters, requires, "  return {};")), ("x.js", Factory("", "", "  return {};")));
    Assert.That(refusal.Message, Is.EqualTo(message));
  }

  [Test]
  public void FactoryReturningNothing_ExportsNothing() {
    var module = Imported("f.js", ("f.js", Factory("", "", "  /** @param {int32} a @returns {int32} */\n  function g(a) { return a | 0; }\n  return;")));
    Assert.That(module.Functions.Single().Name, Is.EqualTo("g"));
  }

  [Test]
  public void NamespaceOfTheEntry_IsImportedWhole() {
    var module = Imported("ops.js", ("ops.js", Helpers.Replace("    Unused: function(a) { return typeof a; }\n", "")));
    Assert.That(module.Functions.Select(function => function.Name), Is.EquivalentTo(new[] { "Add", "Twice" }));
  }

  [Test]
  public void ExportThatIsNoName_IsRefusedWhereAModuleTakesSomethingOfIt_AndNotInTheEntry() {
    var refusal = Refusal("main.js", ("main.js", "const B = require('./bad');\nfunction f() { return B.x; }\nmodule.exports = { f };"), ("bad.js", Factory("", "", "  return 1;")));
    Assert.That((refusal.File, refusal.Message), Is.EqualTo((At("bad.js"), "'an export that is no name of the module' is not supported yet")));
    var taken = Refusal("main.js", ("main.js", "const B = require('./bad');\nconst { x } = B;\nmodule.exports = { x };"), ("bad.js", "module.exports = { x: 1 };"));
    Assert.That((taken.File, taken.Message), Is.EqualTo((At("bad.js"), "'an export that is no name of the module' is not supported yet")));
    var entry = Imported("main.js",
      ("main.js", "/** @param {int32} a @returns {int32} */\nfunction f(a) { return a | 0; }\nmodule.exports = { f, PARAMETERS: { a: 1 }, g: function () { return 2; } };"));
    Assert.That(entry.Functions.Select(function => function.Name), Has.Member("f"));
  }

  [Test]
  public void RequiredFileUnderAnotherName_OrAnExportTakenUnderItsOwnName_IsThatModule() {
    var module = Imported("main.js",
      ("main.js", "const L = require('./lib');\nconst Other = L;\nconst square = Other.square;\n/** @param {int32} x @returns {int32} */\nfunction f(x) { return square(x) + Other.square(1); }\nmodule.exports = { f };"),
      ("lib.js", Factory("", "", Library)));
    Assert.That(IrInterpreter.Call(module, "f", [3]), Is.EqualTo(10));
  }

  [Test]
  public void ProblemOfARequiredFile_IsReportedThere() {
    var refusal = Refusal("main.js", ("main.js", "const B = require('./bad');\nmodule.exports = {};"), ("bad.js", "(function () { }(this, function () { return {}; }));"));
    Assert.That((refusal.File, refusal.Message), Is.EqualTo((At("bad.js"), "'a factory that its wrapper does not call with the files it requires' is not supported yet")));
  }

  [Test]
  public void MemberOfSomethingElseUnderItsOwnName_IsNoExportTaken() {
    var module = Imported("main.js", ("main.js", "const Ops = require('./ops');\nconst PI = Math.PI;\nconst plus = Ops.Add;\n/** @param {int32} x @returns {int32} */\nfunction f(x) { return Ops.Add(x, 1); }\nmodule.exports = { f };"), ("ops.js", Helpers));
    Assert.That(IrInterpreter.Call(module, "f", [1]), Is.EqualTo(2));
  }

  [Test]
  public void NameOfAModule_AsAKeyOrAPropertyName_IsNoUseOfTheModule() {
    var module = Imported("main.js",
      ("main.js", "const L = require('./lib');\n/** @returns {int32} */\nfunction f() { const o = { L: 3 }; return o.L + L.square(2); }\nmodule.exports = { f };"),
      ("lib.js", Factory("", "", Library)));
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(7));
    var shorthand = Refusal("main.js",
      ("main.js", "const L = require('./lib');\nfunction f() { const o = { L }; return 1; }\nmodule.exports = { f };"),
      ("lib.js", Factory("", "", Library)));
    Assert.That(shorthand.Message, Is.EqualTo("'the module 'L' as a value' is not supported yet"));
  }

  [Test]
  public void ClassOfARequiredFile_IsReachedWhole_AndWhatAFileDoesBesidesDeclaringStays() {
    var module = Imported("main.js",
      ("main.js", "const B = require('./box');\nconst o = { a: { b: 1 } };\no.a.b = 2;\no.c = 1;\nif (Math.random()) { }\nif (!o) { }\nif (typeof o === 'undefined') { }\n/** @returns {int32} */\nfunction f() { return new B.Box().square(); }\nmodule.exports = { f };"),
      ("box.js", Factory("", "", """
          class Box {
            side = 3;
            constructor() { this.unused = 0; }
            /** @returns {int32} */
            square() { return Math.imul(this.side, this.side); }
          }
          class Spare { }
          const [first] = [1];
          void first;
          return { Box };
        """)));
    Assert.That(module.Types.OfType<IrClassType>().Select(type => type.Name), Is.EqualTo(new[] { "Box" }));
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(9));
  }

  [Test]
  public void ClassThatTwoFilesDeclare_IsRefused() {
    var refusal = Refusal("main.js",
      ("main.js", "const B = require('./box');\nclass Box { }\nmodule.exports = { Box };"),
      ("box.js", Factory("", "", "  class Box { }\n  return { Box };")));
    Assert.That(refusal.Message, Is.EqualTo("'the name 'Box', which 'box.js' declares as well' is not supported yet"));
  }

  [Test]
  public void WrapperAfterADirective_WithCarriageReturns_AndCommentsInItsNamespace_IsLinked() {
    var helpers = "'use strict';\n;\n" + Helpers.Replace(
      "    Unused: function(a) { return typeof a; }\n",
      "    \"Quoted\": function(a) { return typeof a; } // not reached\n    /* nor this */ ,\n    Unused: function(a) { return typeof a; }\n").Replace("\n", "\r\n");
    Assert.That(helpers, Does.Contain("Quoted"));
    var module = Imported("main.js",
      ("main.js", "const Ops = require('./ops');\n/** @param {int32} x @returns {int32} */\nfunction f(x) { return Ops.Twice(x); }\nmodule.exports = { f };"),
      ("ops.js", helpers));
    Assert.That(IrInterpreter.Call(module, "f", [5]), Is.EqualTo(10));
    var refusal = Refusal("main.js",
      ("main.js", "const Ops = require('./ops');\nfunction f(x) { return Ops.Twice(x) + typeof x; }\nmodule.exports = { f };"),
      ("ops.js", helpers));
    Assert.That((refusal.File, refusal.Line), Is.EqualTo((At("main.js"), 2)), "the lines of a file with carriage returns stay");
  }

  [Test]
  public void FactoryWhoseWrapperHasNoParameterForIt_IsRefused() {
    var refusal = Refusal("f.js", ("f.js", "(function () { }(this, function () { return {}; }));"));
    Assert.That(refusal.Message, Is.EqualTo("'a factory that its wrapper does not call with the files it requires' is not supported yet"));
  }

  [Test]
  public void SecondRequireOfTheSameFile_IsTheSameModule() {
    var twice = Imported("main.js",
      ("main.js", "const Ops = require('./ops');\nconst L = require('./lib');\nconst M = require('./lib.js');\n/** @param {int32} x @returns {int32} */\nfunction f(x) { return Ops.Add(L.square(x), M.square(1)); }\nmodule.exports = { f };"),
      ("ops.js", Helpers), ("lib.js", Factory("", "", Library)));
    Assert.That(IrInterpreter.Call(twice, "f", [3]), Is.EqualTo(10));
  }

}
