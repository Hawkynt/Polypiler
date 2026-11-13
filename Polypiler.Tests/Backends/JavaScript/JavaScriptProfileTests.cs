using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;

namespace Polypiler.Tests.Backends.JavaScript;

/// <summary>The profile type: which combinations of level, module format, typed arrays and global name it accepts.</summary>
[TestFixture]
public sealed class JavaScriptProfileTests {

  [Test]
  public void Default_IsEcmaScript2022AsAnEsModule() {
    var profile = JavaScriptProfile.Default;
    Assert.That((profile.Level, profile.Module, profile.TypedArrays, profile.Global), Is.EqualTo((JavaScriptLevel.Es2022, JavaScriptModuleFormat.Esm, true, (string?)null)));
    Assert.That((profile.LevelName, profile.ModuleName, profile.Extension, profile.ToString()), Is.EqualTo(("es2022", "esm", ".mjs", "es2022, esm")));
    Assert.That(JavaScriptProfile.Parse(), Is.EqualTo(profile));
    Assert.That(JavaScriptProfile.Parse("es2022", "esm"), Is.EqualTo(profile));
    Assert.That(JavaScriptProfile.Parse("es2022", "esm", null, true), Is.EqualTo(profile));
  }

  /// <summary>Test design 2: every level with every module format it has.</summary>
  [TestCase("es5", "cjs", JavaScriptLevel.Es5, JavaScriptModuleFormat.Cjs, ".cjs", false)]
  [TestCase("es5", "iife", JavaScriptLevel.Es5, JavaScriptModuleFormat.Iife, ".js", false)]
  [TestCase("es2015", "esm", JavaScriptLevel.Es2015, JavaScriptModuleFormat.Esm, ".mjs", true)]
  [TestCase("es2015", "cjs", JavaScriptLevel.Es2015, JavaScriptModuleFormat.Cjs, ".cjs", true)]
  [TestCase("es2015", "iife", JavaScriptLevel.Es2015, JavaScriptModuleFormat.Iife, ".js", true)]
  [TestCase("es2017", "esm", JavaScriptLevel.Es2017, JavaScriptModuleFormat.Esm, ".mjs", true)]
  [TestCase("es2017", "cjs", JavaScriptLevel.Es2017, JavaScriptModuleFormat.Cjs, ".cjs", true)]
  [TestCase("es2017", "iife", JavaScriptLevel.Es2017, JavaScriptModuleFormat.Iife, ".js", true)]
  [TestCase("es2020", "esm", JavaScriptLevel.Es2020, JavaScriptModuleFormat.Esm, ".mjs", true)]
  [TestCase("es2020", "cjs", JavaScriptLevel.Es2020, JavaScriptModuleFormat.Cjs, ".cjs", true)]
  [TestCase("es2020", "iife", JavaScriptLevel.Es2020, JavaScriptModuleFormat.Iife, ".js", true)]
  [TestCase("es2022", "esm", JavaScriptLevel.Es2022, JavaScriptModuleFormat.Esm, ".mjs", true)]
  [TestCase("es2022", "cjs", JavaScriptLevel.Es2022, JavaScriptModuleFormat.Cjs, ".cjs", true)]
  [TestCase("es2022", "iife", JavaScriptLevel.Es2022, JavaScriptModuleFormat.Iife, ".js", true)]
  public void Parse_AcceptsEveryValidCombination(string level, string module, JavaScriptLevel expectedLevel, JavaScriptModuleFormat expectedModule, string extension, bool typedArrays) {
    var global = module == "iife" ? "Lib" : null;
    var profile = JavaScriptProfile.Parse(level, module, global);
    Assert.That((profile.Level, profile.Module, profile.Extension, profile.TypedArrays, profile.Global), Is.EqualTo((expectedLevel, expectedModule, extension, typedArrays, global)));
    Assert.That((profile.LevelName, profile.ModuleName), Is.EqualTo((level, module)));
    Assert.That(profile.ToString(), Is.EqualTo(global is null ? $"{level}, {module}" : $"{level}, {module} as the global Lib"));
    Assert.That(JavaScriptProfile.TryParse(level, module, global, false, out var again, out var error), Is.True);
    Assert.That((again, error), Is.EqualTo((profile, (string?)null)));
  }

  /// <summary>The module format a level starts with: an ES module where the level has modules, CommonJS at ECMAScript 5.</summary>
  [TestCase("es5", "cjs")]
  [TestCase("es2015", "esm")]
  [TestCase("es2017", "esm")]
  [TestCase("es2020", "esm")]
  [TestCase(null, "esm")]
  public void Parse_ChoosesTheModuleFormatOfTheLevel(string? level, string module)
    => Assert.That(JavaScriptProfile.Parse(level).ModuleName, Is.EqualTo(module));

  /// <summary>Typed arrays are a choice at ECMAScript 5 only; from ECMAScript 2015 on the level has them.</summary>
  [Test]
  public void TypedArrays_AreAChoiceAtEcmaScript5() {
    Assert.That(JavaScriptProfile.Parse("es5").TypedArrays, Is.False);
    Assert.That(JavaScriptProfile.Parse("es5", typedArrays: true).TypedArrays, Is.True);
    Assert.That(JavaScriptProfile.Parse("es5", typedArrays: true).ToString(), Is.EqualTo("es5 with typed arrays, cjs"));
    Assert.That(JavaScriptProfile.Parse("es5", "iife", "G", true).ToString(), Is.EqualTo("es5 with typed arrays, iife as the global G"));
    Assert.That(JavaScriptProfile.Parse("es5", typedArrays: true), Is.Not.EqualTo(JavaScriptProfile.Parse("es5")));
    Assert.That(JavaScriptProfile.Parse("es2015", typedArrays: true), Is.EqualTo(JavaScriptProfile.Parse("es2015")));
    Assert.That(JavaScriptProfile.Parse("es2015", typedArrays: true).ToString(), Is.EqualTo("es2015, esm"));
  }

  /// <summary>Test design 8: what is no profile, each with the reason the command line shows.</summary>
  [TestCase("es3", null, null, "unknown JavaScript profile 'es3'; the profiles are 'es5', 'es2015', 'es2017', 'es2020', 'es2022'")]
  [TestCase("es2016", null, null, "unknown JavaScript profile 'es2016'; the profiles are 'es5', 'es2015', 'es2017', 'es2020', 'es2022'")]
  [TestCase("ES5", null, null, "unknown JavaScript profile 'ES5'; the profiles are 'es5', 'es2015', 'es2017', 'es2020', 'es2022'")]
  [TestCase("", null, null, "unknown JavaScript profile ''; the profiles are 'es5', 'es2015', 'es2017', 'es2020', 'es2022'")]
  [TestCase(null, "amd", null, "unknown module format 'amd'; the module formats are 'esm', 'cjs', 'iife'")]
  [TestCase("es5", "umd", null, "unknown module format 'umd'; the module formats are 'esm', 'cjs', 'iife'")]
  [TestCase("es5", "esm", null, "the profile 'es5' has no ES modules; its module formats are 'cjs' and 'iife'")]
  [TestCase("es5", "iife", null, "the module format 'iife' needs the name of its global object: --global <name>")]
  [TestCase(null, "iife", null, "the module format 'iife' needs the name of its global object: --global <name>")]
  [TestCase(null, null, "Lib", "a global object belongs to the module format 'iife', not to 'esm'")]
  [TestCase("es5", null, "Lib", "a global object belongs to the module format 'iife', not to 'cjs'")]
  [TestCase(null, "iife", "", "'' is not a name a JavaScript global can have")]
  [TestCase(null, "iife", "1st", "'1st' is not a name a JavaScript global can have")]
  [TestCase(null, "iife", "my-lib", "'my-lib' is not a name a JavaScript global can have")]
  [TestCase(null, "iife", "my lib", "'my lib' is not a name a JavaScript global can have")]
  [TestCase(null, "iife", "function", "'function' is not a name a JavaScript global can have")]
  [TestCase(null, "iife", "Größe", "'Größe' is not a name a JavaScript global can have")]
  public void Parse_RefusesWhatIsNoProfile(string? level, string? module, string? global, string message) {
    Assert.That(() => JavaScriptProfile.Parse(level, module, global), Throws.TypeOf<FormatException>().With.Message.EqualTo(message));
    Assert.That(JavaScriptProfile.TryParse(level, module, global, false, out var profile, out var error), Is.False);
    Assert.That((profile, error), Is.EqualTo(((JavaScriptProfile?)null, message)));
  }

  /// <summary>The names a global may have: the identifiers of ECMAScript 5 in ASCII.</summary>
  [TestCase("Lib")]
  [TestCase("_")]
  [TestCase("$")]
  [TestCase("a1")]
  [TestCase("my_lib$2")]
  public void Parse_AcceptsAnIdentifierAsTheGlobal(string global)
    => Assert.That(JavaScriptProfile.Parse("es5", "iife", global).Global, Is.EqualTo(global));

  [Test]
  public void Names_AreListedOldestFirst() {
    Assert.That(JavaScriptProfile.LevelNames, Is.EqualTo(new[] { "es5", "es2015", "es2017", "es2020", "es2022" }));
    Assert.That(JavaScriptProfile.ModuleNames, Is.EqualTo(new[] { "esm", "cjs", "iife" }));
    Assert.That(Enum.GetValues<JavaScriptLevel>().Select(level => (int)level), Is.EqualTo(new[] { 5, 2015, 2017, 2020, 2022 }));
  }

}
