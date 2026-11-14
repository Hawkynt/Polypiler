using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;

namespace Polypiler.Tests;

/// <summary>
/// <see cref="EmittedHelper"/>: the measuring of the helpers of a file — uses, size, dependencies,
/// overloads, helpers that are values — and the header line that names their licences.
/// </summary>
[TestFixture]
public sealed class EmittedHelperTests {

  private const string Origin = "Hawkynt/Polypiler:Somewhere";

  private static HelperText Text(string name, string text, string licence = EmittedHelper.OwnLicence, string? symbol = null)
    => new(symbol ?? name, name, licence, Origin, text);

  [Test]
  public void Measure_CountsTheUsesOutsideTheHelpersOwnText() {
    var div = Text("$div", "function $div(a, b) {\n  return $trunc(a / b);\n}");
    var trunc = Text("$trunc", "function $trunc(x) {\n  return x < 0 ? Math.ceil(x) : Math.floor(x);\n}");
    var file = $"{div.Text}\n{trunc.Text}\nexport function f(a, b) {{ return $div(a, b) + $div(b, a) + $divide(a); }}\n";
    var helpers = EmittedHelper.Measure([div, trunc], file);
    Assert.That(helpers, Has.Count.EqualTo(2));
    Assert.That((helpers[0].Symbol, helpers[0].CallSites, helpers[0].Size, helpers[0].DependsOn), Is.EqualTo(("$div", 2, div.Text.Length, new[] { "$trunc" })));
    Assert.That((helpers[1].Symbol, helpers[1].CallSites, helpers[1].DependsOn.Count, helpers[1].Licence, helpers[1].Origin), Is.EqualTo(("$trunc", 1, 0, "MIT", Origin)));
  }

  [Test]
  public void Measure_CountsTheReadOfAHelperThatIsAValue() {
    var view = Text("$view", "const $view = new DataView(new ArrayBuffer(8));");
    var bits = Text("$bits64", "function $bits64(x) {\n  $view.setFloat64(0, x);\n  return $view.getBigUint64(0);\n}");
    var helpers = EmittedHelper.Measure([bits, view], $"{bits.Text}\n{view.Text}\n");
    Assert.That((helpers[0].DependsOn, helpers[1].CallSites, helpers[0].CallSites), Is.EqualTo((new[] { "$view" }, 2, 0)));
  }

  [Test]
  public void Measure_LeavesMembersAndLongerNamesAlone() {
    var clamp = Text("Clamp", "private static double Clamp(double value) {\n  return value;\n}", symbol: "Clamp(double value)");
    var file = $"{clamp.Text}\nreturn global::System.Math.Clamp(1, 2, 3) + Clamped(1) + Clamp_(2);\n";
    Assert.That(EmittedHelper.Measure([clamp], file)[0].CallSites, Is.Zero);
  }

  [Test]
  public void Measure_ShareTheCallsOfOverloads_AndSkipTheirDeclarations() {
    var wide = Text("Clamp", "private static double Clamp(double value) {\n  return value;\n}", symbol: "Clamp(double value)");
    var narrow = Text("Clamp", "private static float Clamp(float value) {\n  return value;\n}", symbol: "Clamp(float value)");
    var helpers = EmittedHelper.Measure([wide, narrow], $"{wide.Text}\n{narrow.Text}\nreturn Clamp(1.0) + Clamp(2.0f) + Ignore<int>(3);\n");
    Assert.That((helpers[0].CallSites, helpers[1].CallSites, helpers[0].DependsOn.Count), Is.EqualTo((2, 2, 0)));
  }

  [Test]
  public void Measure_SizeIsInUtf8Bytes() {
    var helper = Text("$s", "function $s() { return \"ä\"; }");
    Assert.That(EmittedHelper.Measure([helper], helper.Text)[0].Size, Is.EqualTo(helper.Text.Length + 1));
  }

  [Test]
  public void Measure_WithoutHelpers_IsEmpty() => Assert.That(EmittedHelper.Measure([], "function f() {}"), Is.Empty);

  [Test]
  public void Measure_RefusesTheSameSymbolTwice() {
    var helper = Text("$a", "function $a() {}");
    Assert.That(() => EmittedHelper.Measure([helper, helper], helper.Text), Throws.ArgumentException.With.Message.Contains("symbol of its own"));
  }

  [Test]
  public void Measure_RefusesATextThatIsNotPartOfTheFile()
    => Assert.That(() => EmittedHelper.Measure([Text("$a", "function $a() {}")], "function f() {}"), Throws.ArgumentException.With.Message.Contains("'$a' is not part of the file"));

  [Test]
  public void Measure_RefusesNull() {
    Assert.That(() => EmittedHelper.Measure(null!, ""), Throws.ArgumentNullException);
    Assert.That(() => EmittedHelper.Measure([], null!), Throws.ArgumentNullException);
  }

  [Test]
  public void HeaderLine_WithoutHelpers_IsNone() => Assert.That(EmittedHelper.HeaderLine([]), Is.Null);

  [Test]
  public void HeaderLine_NamesEveryLicenceOnce_InOrdinalOrder() {
    EmittedHelper[] helpers = [
      new("$a", "MIT", Origin, 1, 1, []), new("FloatF", "MIT AND BSD-2-Clause", Origin, 1, 1, []), new("$b", "MIT", Origin, 1, 1, [])
    ];
    Assert.That(EmittedHelper.HeaderLine(helpers), Is.EqualTo(
      "Helpers added by polypiler: BSD-2-Clause AND MIT (https://github.com/Hawkynt/Polypiler/blob/main/LICENSE-OUTPUT.md); the translated code keeps the licence of its source."));
  }

  [Test]
  public void HeaderLine_RefusesNull() => Assert.That(() => EmittedHelper.HeaderLine(null!), Throws.ArgumentNullException);

  [Test]
  public void OriginOf_NamesTheRepositoryAndThePath()
    => Assert.That((EmittedHelper.OriginOf("A/B.cs"), EmittedHelper.OwnLicence, EmittedHelper.GrantFile), Is.EqualTo(("Hawkynt/Polypiler:A/B.cs", "MIT", "LICENSE-OUTPUT.md")));

  [Test]
  public void Helpers_AreEqualByTheirFields_TheDependenciesElementByElement() {
    var helper = new EmittedHelper("$a", "MIT", Origin, 1, 2, ["$b"]);
    Assert.That((helper == new EmittedHelper("$a", "MIT", Origin, 1, 2, ["$b"]), helper.GetHashCode() == new EmittedHelper("$a", "MIT", Origin, 1, 2, ["$b"]).GetHashCode()), Is.EqualTo((true, true)));
    Assert.That(new[] {
      helper with { Symbol = "$c" }, helper with { Licence = "ISC" }, helper with { Origin = "x" }, helper with { CallSites = 2 }, helper with { Size = 3 }, helper with { DependsOn = ["$c"] }
    }.Any(other => other.Equals(helper)), Is.False);
    Assert.That(helper.Equals(null), Is.False);
  }

  [Test]
  public void Files_AreEqualByPathTextAndHelpers() {
    var file = new GeneratedFile("a.mjs", "x") { Helpers = [new("$a", "MIT", Origin, 1, 2, ["$b"])] };
    var same = new GeneratedFile("a.mjs", "x") { Helpers = [new("$a", "MIT", Origin, 1, 2, ["$b"])] };
    Assert.That((file == same, file.GetHashCode() == same.GetHashCode()), Is.EqualTo((true, true)));
    Assert.That(new[] { file with { Path = "b.mjs" }, file with { Content = "y" }, file with { Helpers = [] } }.Any(other => other.Equals(file)), Is.False);
    Assert.That(file.Equals(null), Is.False);
  }

  [Test]
  public void GeneratedFile_HasNoHelpersByDefault() {
    var file = new GeneratedFile("a.mjs", "");
    Assert.That((file.Helpers.Count, (file with { Helpers = [new("$a", "MIT", Origin, 0, 0, Array.Empty<string>())] }).Helpers.Count), Is.EqualTo((0, 1)));
  }

}
