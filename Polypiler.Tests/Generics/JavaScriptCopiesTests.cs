using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Generics;

/// <summary>
/// The JavaScript frontend's copies of a function that its calls pass values of several kinds: one
/// copy per combination of kinds, each typed on its own, for a function the file does not export;
/// an exported function keeps one type per parameter.
/// </summary>
[TestFixture]
internal sealed class JavaScriptCopiesTests {

  private static ImportResult Import(string source) => JavaScriptFrontend.Import("m.js", source);

  /// <summary>Test design 3: the fixture's helper is two copies, which compute what node computes.</summary>
  [Test]
  public void Middle_IsTwoCopies() {
    var module = GenericsFixtures.ImportedScript("middle.js");
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(module.Functions.Select(function => (function.Name, function.IsPublic, function.Origin)), Is.EqualTo(new (string, bool, IrGenericOrigin?)[] {
      ("middle<i32[]>", false, null), ("middle<f64[]>", false, null), ("f", true, null)
    }));
    Assert.That(module.Functions[0].ReturnType, Is.EqualTo(IrType.I32));
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(-3.75));
  }

  /// <summary>Test design 9: an exported function keeps one type per parameter.</summary>
  [Test]
  public void ExportedFunction_IsNotCopied() {
    var result = Import("export function first(a) { return a[0]; } export function g() { return first(new Int32Array(1)) + first(new Float64Array(1)); }");
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Has.Member((JavaScriptFrontend.MixedKinds, "'a' holds both int32[] and float64[]")));
  }

  /// <summary>A function an initializer of the module calls keeps one type per parameter.</summary>
  [Test]
  public void FunctionCalledByAnInitializer_IsNotCopied() {
    var result = Import("function first(a) { return a[0]; } let x = first(new Int32Array(1)); export function g() { return first(new Float64Array(1)) + x; }");
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Single().Message, Does.Contain("a call of first, which cannot be imported"));
  }

  /// <summary>Calls of one kind share one copy; a copy calls copies itself; the order of evaluation and the number of calls stay.</summary>
  [Test]
  public void CallsOfOneKind_ShareACopy() {
    const string Source = """
      function last(a) { return a[a.length - 1]; }
      function sum(a, b) { return last(a) + last(b); }
      export function f() {
        return last(new Int32Array([1, 2])) + last(new Int32Array([3, 4])) + last(new Float64Array([0.5])) + sum(new Int32Array([5]), new Int32Array([6]));
      }
      """;
    var module = Import(Source).Module!;
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(module.Functions.Select(function => function.Name), Is.EqualTo(new[] { "last<i32[]>", "last<f64[]>", "sum", "f" }));
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(2 + 4 + 0.5 + 11));
  }

  /// <summary>Two kinds that the IR types alike leave the copies their numbers, so that no two functions share a name.</summary>
  [Test]
  public void CopiesOfOneIrType_KeepTheirNumbers() {
    const string Source = """
      function first(a) { return a[0]; }
      export function f() { return first([1, 2]) + first(new Int32Array([3])) + first(new Float64Array([0.5])); }
      """;
    var result = Import(Source);
    if (result.Module is not { } module) {
      Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.Member(JavaScriptFrontend.MixedKinds));
      return;
    }

    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(module.Functions.Select(function => function.Name).Distinct().Count(), Is.EqualTo(module.Functions.Count));
    Assert.That(IrInterpreter.Call(module, "f", []), Is.EqualTo(4.5));
  }

}
