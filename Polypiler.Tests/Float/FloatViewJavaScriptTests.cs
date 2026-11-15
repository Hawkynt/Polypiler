using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Float;

/// <summary>
/// The endian loads and stores of floating-point numbers in the JavaScript backend: a helper on a
/// <c>DataView</c> of the array wherever the profile has typed arrays, and below that the integer
/// access of the width with the bit casts of plain arrays. Whether the forms compute what .NET
/// computes is asked of node in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class FloatViewJavaScriptTests {

  /// <summary>Every valid combination of level, module format and, at ECMAScript 5, typed arrays.</summary>
  private static readonly JavaScriptProfile[] _profiles = [
    .. from level in JavaScriptProfile.LevelNames
       from module in JavaScriptProfile.ModuleNames
       where level != "es5" || module != "esm"
       from typed in level == "es5" ? new[] { false, true } : [false]
       select JavaScriptProfile.Parse(level, module, module == "iife" ? "Generated" : null, typed)
  ];

  private static IEnumerable<TestCaseData> Profiles() => _profiles.Select(profile => new TestCaseData(profile).SetArgDisplayNames(profile.ToString()));

  private static string Emit(string function, JavaScriptProfile profile)
    => LegalizedJavaScript.Emit(IntegerCases.Module([FloatViewCases.Of(function)], "Snippet"), profile).Single().Content;

  private static JavaScriptProfile Profile(string level, bool typed = false) => JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm", null, typed);

  /// <summary>The functions a module declares besides its own, in their order.</summary>
  private static string[] Helpers(string text)
    => text.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("function $", StringComparison.Ordinal)).Select(line => line[9..line.IndexOf('(')]).ToArray();

  /// <summary>
  /// Test design 11: every operation and format can be written in every profile - no PLY1152 - and the
  /// file parses at its level; without typed arrays it makes no <c>DataView</c>.
  /// </summary>
  [TestCaseSource(nameof(Profiles))]
  public void EveryOperation_HasAFormInEveryProfile(JavaScriptProfile profile) {
    var module = FloatViewCases.Module();
    Assert.That(LegalizedJavaScript.Check(module, profile), Is.Empty);
    var text = LegalizedJavaScript.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty);
    Assert.That(text, Does.Not.Contain("@"));
    if (!profile.TypedArrays)
      Assert.That(text, Does.Not.Contain("DataView").And.Not.Match(@"new \w+Array\("));
  }

  /// <summary>Every helper the module needs is declared once, and every declared helper is called.</summary>
  [TestCaseSource(nameof(Profiles))]
  public void Helpers_AreDeclaredOnceAndCalled(JavaScriptProfile profile) {
    var text = LegalizedJavaScript.Emit(FloatViewCases.Module(), profile).Single().Content;
    var helpers = Helpers(text);
    Assert.That(helpers, Is.Unique);
    foreach (var helper in helpers)
      Assert.That(text.Split(helper + "(").Length, Is.GreaterThan(2), $"{helper} is declared but not called");
  }

  /// <summary>Test design 7: a module carries the helpers its one operation needs in its profile, and no other.</summary>
  [TestCase("LoadLittle_f32", "es5", false, new[] { "$get", "$float32" })]
  [TestCase("LoadLittle_f32", "es5", true, new[] { "$get", "$float32" })]
  [TestCase("LoadBig_f64", "es5", false, new[] { "$get", "$float64" })]
  [TestCase("LoadBig_f64", "es2015", false, new[] { "$get", "$float64" })]
  [TestCase("StoreBig_f32", "es5", false, new[] { "$imul", "$set", "$bits32" })]
  [TestCase("StoreBig_f32", "es2017", false, new[] { "$set", "$bits32" })]
  [TestCase("StoreLittle_f64", "es5", false, new[] { "$imul", "$set", "$bits64" })]
  [TestCase("StoreLittle_f64", "es2022", false, new[] { "$set", "$bits64" })]
  public void Module_CarriesTheHelpersItCalls(string function, string level, bool typed, string[] helpers)
    => Assert.That(Helpers(Emit(function, Profile(level, typed))), Is.EqualTo(helpers));

  /// <summary>The forms of the lowering table of the issue.</summary>
  [TestCase("LoadLittle_f32", "es5", false, "return $float32(Load32(a, b, 4, true) | 0);")]
  [TestCase("LoadBig_f64", "es5", false, "return $float64(Load64(a, b, false));")]
  [TestCase("StoreBig_f32", "es5", false, "Store32(a, b, $bits32(c) >>> 0, 4, false);")]
  [TestCase("StoreLittle_f64", "es5", false, "Store64(a, b, $bits64(c), true);")]
  [TestCase("LoadLittle_f32", "es5", true, "return $float32(Load32(a, b, 4, true) | 0);")]
  [TestCase("LoadBig_f64", "es2020", false, "return $float64(BigInt.asIntN(64, Load64(a, b, false)));")]
  [TestCase("StoreBig_f32", "es2015", false, "Store32(a, b, $bits32(c) >>> 0, 4, false);")]
  [TestCase("StoreLittle_f64", "es2022", false, "Store64(a, b, BigInt.asUintN(64, $bits64(c)), true);")]
  public void Form_IsTheOneOfTheProfile(string function, string level, bool typed, string body) {
    var lines = Emit(function, Profile(level, typed)).Split('\n');
    var start = Array.FindIndex(lines, line => line.Contains($"function {function}("));
    Assert.That(lines[start + 1].Trim(), Is.EqualTo(body));
  }

  /// <summary>A helper on a <c>DataView</c> checks the whole range before it makes the view, with the error the other endian helpers throw.</summary>
  [Test]
  public void LibraryHelper_ChecksTheRangeFirst() {
    var text = Emit("StoreBig_f64", Profile("es2022"));
    Assert.That(text, Does.Contain("function Store64(array, index, value, little) {\n  CheckBytes(array, index, 8);\n")
      .And.Contain("throw new IndexOutOfRangeError(\"index out of range\");"));
  }

}
