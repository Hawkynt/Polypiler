using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Helpers;

/// <summary>
/// The scenario of #1358 "A string helper that is not used is not emitted": a method that only adds two
/// ints, translated for every profile of both backends, carries no helper at all - no string helper,
/// no number text, no trap and no limb helper; a helper is bound only where an operation needs it.
/// </summary>
[TestFixture]
public sealed class UnusedHelperScenarioTests {

  private const string Add = "public static int F(int a, int b) => a + b;";

  private static IEnumerable<TestCaseData> JavaScriptProfiles()
    => from level in JavaScriptProfile.LevelNames
       from module in JavaScriptProfile.ModuleNames
       where level != "es5" || module != "esm"
       select new TestCaseData(level, module).SetArgDisplayNames(level, module);

  private static IEnumerable<TestCaseData> CSharpProfiles() => CSharpProfile.All.Select(profile => new TestCaseData(profile.Name).SetArgDisplayNames(profile.Name));

  [TestCaseSource(nameof(JavaScriptProfiles))]
  public void AnAddOfInts_CarriesNoHelperInJavaScript(string level, string module) {
    var file = LegalizedJavaScript.Emit(PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(Add)), JavaScriptProfile.Parse(level, module, module == "iife" ? "Generated" : null)).Single();
    Assert.That(file.Helpers, Is.Empty, file.Content);
    Assert.That(file.Content, Does.Not.Contain("function $"));
  }

  [TestCaseSource(nameof(CSharpProfiles))]
  public void AnAddOfInts_CarriesNoHelperInCSharp(string name) {
    var file = LegalizedCSharp.Emit(PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(Add)), CSharpProfile.Parse(name)).Single();
    Assert.That(file.Helpers, Is.Empty, file.Content);
    Assert.That(file.Content, Does.Not.Contain("private static"));
  }

}
