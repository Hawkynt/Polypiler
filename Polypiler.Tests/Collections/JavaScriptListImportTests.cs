using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Collections;

/// <summary>
/// The JavaScript frontend on arrays that change their length (#1328): the fixture imported and run by
/// the interpreter against what node 22.23 printed for the same calls (captured once, written down
/// here; the acceptance tests run node itself), which arrays become lists, and what is refused.
/// </summary>
[TestFixture]
public sealed class JavaScriptListImportTests {

  private static IrFunction Function(IrModule module, string name) => module.Functions.Single(function => function.Name == name);

  private static IReadOnlyList<string> Problems(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    return result.Module is null ? [.. result.Diagnostics.Select(diagnostic => diagnostic.Message)] : [];
  }

  private static IrModule Imported(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Not.Null, string.Join("\n", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>Test design 1, 4, 5 and 6: the interpreter computes what node computes of the fixture.</summary>
  [TestCase("arrayOps", "72334"), TestCase("pushWhileIterating", "4"), TestCase("popOfEmpty", "1"), TestCase("popped", "51"), TestCase("searches", "-89")]
  [TestCase("cleared", "15"), TestCase("words", "a"), TestCase("copied", "23"), TestCase("stepped", "33"), TestCase("passed", "3"), TestCase("fixedLoop", "123")]
  public void Fixture_ComputesWhatNodeComputes(string function, string outcome)
    => Assert.That(PrototypeFixture.InterpretedOutcome(CollectionFixtures.ImportedScript("collections.js"), new PrototypeCall(function, [])), Is.EqualTo(outcome));

  [Test]
  public void ArraysThatChangeTheirLength_AreLists_TheOthersStayArrays() {
    var module = CollectionFixtures.ImportedScript("collections.js");
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(Function(module, "arrayOps").Locals.Single(local => local.Name == "a").Type, Is.EqualTo(IrType.List(IrType.I32)));
    Assert.That(Function(module, "words").Locals.Single(local => local.Name == "w").Type, Is.EqualTo(IrType.List(IrType.String)));
    Assert.That(Function(module, "fixedLoop").Locals.Single(local => local.Name == "a").Type, Is.EqualTo(IrType.Array(IrType.I32)));
    // An array passed to a function that pushes is the list the function takes.
    Assert.That(Function(module, "passed").Locals.Single(local => local.Name == "a").Type, Is.EqualTo(IrType.List(IrType.I32)));
    Assert.That(Function(module, "append").Parameters[0].Type, Is.EqualTo(IrType.List(IrType.I32)));
    Assert.That(IrTree.Nodes(Function(module, "pushWhileIterating").Body).OfType<IrCollection>().Any(), Is.True);
  }

  /// <summary>An array that becomes a list makes the variable it came from one: an assignment and a return carry it back.</summary>
  [Test]
  public void ListThroughAnAssignmentAndAReturn_IsOneList() {
    var module = Imported("function make() { const a = [1]; return a; } export function f() { const b = make(); const c = b; c.push(2); return b.length; }");
    Assert.That(Function(module, "make").ReturnType, Is.EqualTo(IrType.List(IrType.I32)));
    Assert.That(PrototypeFixture.InterpretedOutcome(module, new PrototypeCall("f", [])), Is.EqualTo("2"));
  }

  /// <summary>A list of objects, an empty literal pushed later, new Array() and a pop of strings as a statement.</summary>
  [Test]
  public void ElementKinds_AreKept() {
    var module = Imported("class K { constructor(v) { this.v = v; } } export function f() { const a = new Array(); a.push(new K(2)); const b = []; b.push(\"x\"); b.pop(); "
      + "const k = a.pop(); return (k === null ? 0 : k.v) * 10 + b.length + a.length; }");
    Assert.That(PrototypeFixture.InterpretedOutcome(module, new PrototypeCall("f", [])), Is.EqualTo("20"));
  }

  /// <summary>A copy of a list, a for...of whose variable is wider than the element, a list of records walked live.</summary>
  [TestCase("export function f() { const a = [1, 2]; a.push(3); const b = [...a]; b.push(4); return a.length * 10 + b.length; }", "34")]
  [TestCase("export function f() { const a = [1, 2]; a.push(3); let s = 0; for (let x of a) { x = x / 2; s += x; } return s; }", "3")]
  [TestCase("export function f() { const a = [{ x: 1 }]; a.push({ x: 2 }); let s = 0; for (const p of a) { s = s * 10 + p.x; } return s; }", "12")]
  public void Construct_ComputesWhatNodeComputes(string source, string outcome)
    => Assert.That(PrototypeFixture.InterpretedOutcome(Imported(source), new PrototypeCall("f", [])), Is.EqualTo(outcome));

  /// <summary>Test design 7: a write at the length appends, one beyond it traps, a read beyond it traps under the caveat of the reads of arrays.</summary>
  [TestCase("a[a.length] = 4; return a.length;", "3")]
  [TestCase("a[1] = 4; return a[1];", "4")]
  [TestCase("a[3] = 4; return a.length;", "trap: argument out of range")]
  [TestCase("return a[2];", "trap: argument out of range")]
  [TestCase("a[-1] = 4; return 0;", "trap: argument out of range")]
  [TestCase("a.splice(1, 1); return a[0] * 10 + a.length;", "11")]
  [TestCase("a.splice(2, 0, 9); return a[2] * 10 + a.length;", "93")]
  [TestCase("a.splice(3, 0, 9); return 0;", "trap: argument out of range")]
  [TestCase("a.length = 0; return a.length;", "0")]
  [TestCase("a[0] -= 3; a[1]--; return a[0] * 10 + a[1];", "-19")]
  public void Element_WritesAndReads(string body, string outcome) {
    var module = Imported($"export function f() {{ const a = [1, 2]; a.push(); a.pop(); a.push(2); {body} }}".Replace("a.push(); ", ""));
    Assert.That(PrototypeFixture.InterpretedOutcome(module, new PrototypeCall("f", [])), Is.EqualTo(outcome));
  }

  /// <summary>Test design 11: what lists do not take is refused by name.</summary>
  [TestCase("export function f() { const a = [1]; a.length = 3; return 0; }", "a.length = 3, a length other than 0")]
  [TestCase("export function f() { const a = [1]; a.splice(0, 2); return 0; }", "a.splice(0, 2) on a list")]
  [TestCase("export function f() { const a = [1]; a.push(...a); return 0; }", "a spread argument")]
  [TestCase("export function f() { const a = [1]; return a.push(2); }", "the value of a.push(2)")]
  [TestCase("export function f() { const a = [\"x\"]; a.push(\"y\"); return a.pop(); }", "a string that may be undefined")]
  [TestCase("export function f() { const a = [1]; a.push(2); return a.join(\",\"); }", "a.join of a list")]
  [TestCase("export function f() { const a = [{ x: 1 }]; a.push({ x: 2 }); return a.indexOf(a[0]); }", "indexOf of a list of records, which compares them by identity")]
  [TestCase("export function f() { const a = [1]; a.push(2); a.y = 3; return 0; }", "a.y")]
  [TestCase("export function f() { const a = new Int32Array(2); let b = [1]; b.push(2); b = a; return 0; }", "'b' holds both")]
  [TestCase("export function f() { const a = [1]; a.push(2); const b = [...a, 3]; return b.length; }", "an array literal with a hole or a spread element")]
  [TestCase("export function f() { const a = []; a.push(new Map()); const m = a.pop(); return 0; }", "the value of pop of a list of Map<float64, float64>")]
  [TestCase("class K { constructor() { this.v = 1; } } export function f() { const a = []; a.push(new K()); const b = [...a]; b.push(new K()); return b.length; }", "of which the IR has no array")]
  [TestCase("export function f() { const b = [...\"ab\"]; b.push(\"c\"); return b.length; }", "a spread of string")]
  [TestCase("class K { constructor() { this.v = 1; } } export function f() { const a = [new K(), , new K()]; a.push(new K()); return a.length; }", "a hole or a spread in a literal of a list of K")]
  [TestCase("export function f() { const a = [1]; a.length += 1; return 0; }", "a length other than 0")]
  [TestCase("export function f() { const a = [1]; a.push(2); a[0] ||= 3; return a[0]; }", "a[0] ||= 3")]
  public void Construct_IsRefused(string source, string named)
    => Assert.That(Problems(source), Has.Some.Contains(named));

  /// <summary>Test design 1 and 9: the fixture is written at every JavaScript level and in every C# profile.</summary>
  [Test]
  public void Fixture_IsWrittenEverywhere() {
    var module = CollectionFixtures.ImportedScript("collections.js");
    foreach (var level in JavaScriptProfile.LevelNames) {
      var profile = JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm");
      Assert.That(LegalizedJavaScript.Check(module, profile), Is.Empty, level);
      var text = LegalizedJavaScript.Emit(module, profile).Single().Content;
      Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, $"{level}\n{text}");
    }

    foreach (var profile in CSharpProfile.All)
      Assert.That(LegalizedCSharp.Check(module, profile), Is.Empty, profile.Name);
  }

}
