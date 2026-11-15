using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Helpers.Support;

namespace Polypiler.Tests.Functions;

/// <summary>
/// Anonymous methods, <c>delegate (int x) { … }</c> and <c>delegate { … }</c> (ECMA-334 §12.21, #1361):
/// imported as the lambda they spell, and translated like it for every profile of both backends. The
/// rows of test design 1 - with parameters, without a parameter list, capturing, of a declared delegate
/// type and nested - each beside its lambda form, against the source compiled by Roslyn.
/// </summary>
[TestFixture]
internal sealed class AnonymousMethodTests {

  private const string Usings = "using System; ";

  /// <summary>Each row: the method with an anonymous method, the same with a lambda, and a call.</summary>
  private static readonly (string Name, string Anonymous, string Lambda, PrototypeCall Call)[] _rows = [
    ("WithParameters",
      "public static int F(int[] a) { Func<int, int> sq = delegate (int x) { return x * x; }; int s = 0; foreach (var v in a) s += sq(v); return s; }",
      "public static int F(int[] a) { Func<int, int> sq = (int x) => { return x * x; }; int s = 0; foreach (var v in a) s += sq(v); return s; }",
      new PrototypeCall("F", [new[] { 1, 2, 3 }])),
    ("WithoutAParameterList",
      "public static int F(int k) { Func<int, int> seven = delegate { return 7; }; Action nothing = delegate { }; nothing(); return seven(k) + k; }",
      "public static int F(int k) { Func<int, int> seven = _ => { return 7; }; Action nothing = () => { }; nothing(); return seven(k) + k; }",
      new PrototypeCall("F", [5])),
    ("Capturing",
      "public static int F(int k) { int n = 0; Action inc = delegate { n += k; }; Func<int, int> add = delegate (int x) { return x + n; }; inc(); inc(); return add(1); }",
      "public static int F(int k) { int n = 0; Action inc = () => { n += k; }; Func<int, int> add = (int x) => { return x + n; }; inc(); inc(); return add(1); }",
      new PrototypeCall("F", [4])),
    ("DeclaredDelegateType",
      "public delegate int Twice(int x); static int Use(Twice t, int v) => t(v); public static int F(int k) { Twice d = delegate (int x) { return x * 2; }; return d(k) + Use(delegate (int y) { return y + k; }, 3); }",
      "public delegate int Twice(int x); static int Use(Twice t, int v) => t(v); public static int F(int k) { Twice d = (int x) => { return x * 2; }; return d(k) + Use((int y) => { return y + k; }, 3); }",
      new PrototypeCall("F", [6])),
    ("Nested",
      "public static int F(int k) { Func<int, Func<int, int>> adder = delegate (int a) { return delegate (int b) { return a + b * k; }; }; return adder(2)(3); }",
      "public static int F(int k) { Func<int, Func<int, int>> adder = (int a) => { return (int b) => { return a + b * k; }; }; return adder(2)(3); }",
      new PrototypeCall("F", [10]))
  ];

  private static string Source(string methods) => Usings + PrototypeFixture.Wrap(methods);

  private static IEnumerable<TestCaseData> Rows() => _rows.Select(row => new TestCaseData(row.Name).SetName($"AnonymousMethod_{row.Name}"));

  private static IEnumerable<TestCaseData> RowsInEveryProfile()
    => from row in _rows
       from profile in CSharpProfile.All
       select new TestCaseData(row.Name, profile.Name).SetName($"AnonymousMethod_{row.Name}_{profile.Name}");

  private static (string Name, string Anonymous, string Lambda, PrototypeCall Call) Row(string name) => _rows.Single(row => row.Name == name);

  /// <summary>The anonymous method imports to the module of its lambda, whose interpreter computes what .NET computes.</summary>
  [TestCaseSource(nameof(Rows))]
  public void ImportsAsItsLambda(string name) {
    var (_, anonymous, lambda, call) = Row(name);
    var fromAnonymous = PrototypeFixture.Import("Snippet.cs", Source(anonymous));
    var fromLambda = PrototypeFixture.Import("Snippet.cs", Source(lambda));
    Assert.That(IrVerifier.Verify(fromAnonymous), Is.Empty);
    var expected = GeneratedCSharp.CompileSource(Source(anonymous)).Outcome(call);
    Assert.That(PrototypeFixture.InterpretedOutcome(fromAnonymous, call), Is.EqualTo(expected));
    if (name != "WithoutAParameterList")
      Assert.That(IrModuleFormat.Write(fromAnonymous), Is.EqualTo(IrModuleFormat.Write(fromLambda)), "the same module as the lambda's");
  }

  /// <summary>Test design 1: in every C# profile both forms translate to the same text, which computes what .NET computes.</summary>
  [TestCaseSource(nameof(RowsInEveryProfile))]
  public void TranslatesAsItsLambda(string name, string profile) {
    var (_, anonymous, lambda, call) = Row(name);
    var (text, _) = SnippetTranslation.Translate(Source(anonymous), "csharp", "--profile", profile);
    if (name != "WithoutAParameterList")
      Assert.That(text, Is.EqualTo(SnippetTranslation.Translate(Source(lambda), "csharp", "--profile", profile).Text));

    Assert.That(GeneratedCSharp.Compile(text, false).Outcome(call), Is.EqualTo(GeneratedCSharp.CompileSource(Source(anonymous)).Outcome(call)), text);
  }

  /// <summary>Test design 1: every JavaScript level writes both forms alike, and the output parses at its level.</summary>
  [TestCaseSource(nameof(Rows))]
  public void TranslatesAsItsLambdaInJavaScript(string name) {
    var (_, anonymous, lambda, _) = Row(name);
    foreach (var level in new[] { "es5", "es2015", "es2022" }) {
      var (text, _) = SnippetTranslation.Translate(Source(anonymous), "javascript", "--profile", level);
      Assert.That(GeneratedJavaScript.Problems(text, JavaScriptProfile.Parse(level)), Is.Empty, text);
      if (name != "WithoutAParameterList")
        Assert.That(text, Is.EqualTo(SnippetTranslation.Translate(Source(lambda), "javascript", "--profile", level).Text), level);
    }
  }

  /// <summary>
  /// Scenario "An anonymous delegate translates like a lambda": where the delegate stays - with the
  /// passes off - .NET Framework 2.0 writes an anonymous method again, and .NET 10 with the passes on
  /// has no delegate left.
  /// </summary>
  [Test]
  public void Net20_WritesAnAnonymousMethod_Net10_NoDelegate() {
    var (_, anonymous, _, call) = Row("WithParameters");
    var (kept, _) = SnippetTranslation.Translate(Source(anonymous), "csharp", "--profile", "net2.0", "--opt", "none");
    Assert.That(kept, Does.Contain("delegate (int x)").And.Not.Contain("=>"), kept);
    Assert.That(GeneratedCSharp.Compile(kept, false).Outcome(call), Does.StartWith("14 |"));
    var (gone, _) = SnippetTranslation.Translate(Source(anonymous), "csharp", "--profile", "net10.0");
    Assert.That(gone, Does.Not.Contain("delegate").And.Not.Contain("Func").And.Not.Contain("=>"), gone);
    Assert.That(GeneratedCSharp.Compile(gone, false).Outcome(call), Does.StartWith("14 |"));
  }

}
