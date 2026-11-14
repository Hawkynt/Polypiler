using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.String;

/// <summary>
/// The string operations written by the JavaScript backend: every operation in every profile, parsed
/// at the level it claims and using nothing its level lacks, and the form of each where the levels
/// differ. Whether the code computes what the interpreter computes is decided by node in the
/// acceptance tests.
/// </summary>
[TestFixture]
public sealed class JavaScriptStringEmissionTests {

  private static readonly JavaScriptProfile[] _profiles = [
    .. from level in JavaScriptProfile.LevelNames
       from typed in level == "es5" ? new[] { false, true } : [false]
       from module in JavaScriptProfile.ModuleNames.Where(module => level != "es5" || module != "esm")
       select JavaScriptProfile.Parse(level, module, module == "iife" ? "Strings" : null, typed)
  ];

  private static string Emit(JavaScriptProfile profile) {
    Assert.That(JavaScriptEmitter.Check(StringModules.Operations, profile), Is.Empty);
    var text = JavaScriptEmitter.Emit(StringModules.Operations, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, text);
    return text;
  }

  /// <summary>Test design 14: every operation in every level, typed arrays and module format; nothing is inexpressible.</summary>
  [Test]
  public void EveryOperation_IsWrittenInEveryProfile() {
    foreach (var profile in _profiles)
      Assert.That(Emit(profile), Does.Contain("function NextCodePoint(a, i)"), profile.ToString());
  }

  [TestCase("es5", "return a.length;")]
  [TestCase("es5", "return $unitAt(a, i);")]
  [TestCase("es5", "return a + b + $substr(a, 0, 0) + \"!\";")]
  [TestCase("es5", "return a === b;")]
  [TestCase("es5", "return a < b ? -1 : a > b ? 1 : 0;")]
  [TestCase("es5", "return a < b;")]
  [TestCase("es5", "return $indexOf(a, String.fromCharCode(c), i);")]
  [TestCase("es5", "return $startsWith(a, b);")]
  [TestCase("es5", "return a.indexOf(b) !== -1;")]
  [TestCase("es5", "return $replaceAll(a, b, \"$&$$\");")]
  [TestCase("es5", "return a.split(String.fromCharCode(c)).join(String.fromCharCode(d));")]
  [TestCase("es5", "return $replaceFirst(a, b, \"[$&$1$`]\");")]
  [TestCase("es5", "return a.split(String.fromCharCode(c));")]
  [TestCase("es5", "return a.split(b, i);")]
  [TestCase("es5", "return parts.join(a);")]
  [TestCase("es5", "return a.trim();")]
  [TestCase("es5", "return $trimEcma(a, true, false);")]
  [TestCase("es5", "return $trimNet(a, true, true);")]
  [TestCase("es5", "return $pad(a, i, b, true);")]
  [TestCase("es5", "var Greeting = \"h\\u00E9llo \\\"\\\\\\u0001\\uD800\";")]
  [TestCase("es5", "var Names = [")]
  [TestCase("es2015", "return a.startsWith(b);")]
  [TestCase("es2015", "return a.includes(String.fromCharCode(c));")]
  [TestCase("es2015", "return $trimEcma(a, false, true);")]
  [TestCase("es2017", "return a.padStart(i, b);")]
  [TestCase("es2017", "return a.padEnd(i, b);")]
  [TestCase("es2020", "return a.trimStart();")]
  [TestCase("es2020", "return a.trimEnd();")]
  [TestCase("es2022", "return Greeting + $get(Names, i);")]
  public void Operation_HasTheFormOfTheLevel(string level, string form)
    => Assert.That(Emit(JavaScriptProfile.Parse(level)), Does.Contain(form));

  /// <summary>The methods of strings a level may lack, each with the function of <see cref="StringModules.Operations"/> that uses it.</summary>
  internal static readonly IReadOnlyDictionary<string, (string Function, string Native, string Replacement)> NativeMethods = new Dictionary<string, (string, string, string)> {
    [".startsWith"] = ("StartsWith", "return a.startsWith(b);", "return $startsWith(a, b);"),
    [".endsWith"] = ("EndsWith", "return a.endsWith(b);", "return $endsWith(a, b);"),
    [".includes"] = ("Contains", "return a.includes(b);", "return a.indexOf(b) !== -1;"),
    [".repeat"] = ("Repeat", "return s.repeat(n);", "r += s;"),
    [".codePointAt"] = ("CodePointAt", "return s.codePointAt(i);", "(high - 0xD800) * 0x400"),
    [".padStart"] = ("PadStart", "return a.padStart(i, b);", "return $pad(a, i, b, true);"),
    [".padEnd"] = ("PadEnd", "return a.padEnd(i, b);", "return $pad(a, i, b, false);"),
    [".trimStart"] = ("TrimStartEcma", "return a.trimStart();", "return $trimEcma(a, true, false);"),
    [".trimEnd"] = ("TrimEndEcma", "return a.trimEnd();", "return $trimEcma(a, false, true);"),
    [".replaceAll"] = ("ReplaceAll", "", "")
  };

  /// <summary>The names of <see cref="NativeMethods"/>.</summary>
  internal static IReadOnlySet<string> Methods { get; } = NativeMethods.Keys.ToHashSet();

  /// <summary>Test design 13: each method of a string a level may lack is native from its first level and replaced in the one before.</summary>
  [Test]
  public void NativeMethod_BeginsAtItsLevel() {
    var levels = JavaScriptProfile.LevelNames.Select(name => JavaScriptProfile.Parse(name)).ToList();
    var replaceable = JavaScriptCapabilities.Standard.Entries.Where(entry => entry.Replacement is not null && Methods.Contains(entry.Name)).Select(entry => entry.Name);
    Assert.That(replaceable, Is.EquivalentTo(Methods));
    foreach (var (name, (_, native, replacement)) in NativeMethods.Where(entry => entry.Value.Native.Length > 0)) {
      var since = JavaScriptCapabilities.Standard[name].Since;
      var first = levels.First(profile => (int)profile.Level >= since);
      var before = levels[levels.IndexOf(first) - 1];
      Assert.That(Emit(first), Does.Contain(native), $"{name} in {first}");
      Assert.That(Emit(before), Does.Not.Contain(native).And.Contain(replacement), $"{name} in {before}");
    }
  }

  [Test]
  public void ConstantReplacement_EscapesItsPatterns() {
    var text = new IrLocal("s", IrType.String);
    IrModule Module(IrIntrinsicOp op, IrExpr needle) => new("M", [new IrFunction("F", true, IrType.String, [text], [], new IrBlock([
      new IrReturn(new IrIntrinsic(op, IrType.String, [new IrLocalRef(text), needle, new IrStrConst("$&")]))]))]);
    string Body(IrIntrinsicOp op, IrExpr needle, string level) => JavaScriptEmitter.Emit(Module(op, needle), JavaScriptProfile.Parse(level)).Single().Content;
    Assert.That(Body(IrIntrinsicOp.StrReplaceFirst, new IrStrConst("b"), "es5"), Does.Contain("return s.replace(\"b\", \"$$&\");"));
    Assert.That(Body(IrIntrinsicOp.StrReplaceAll, new IrStrConst("b"), "es2022"), Does.Contain("return s.replaceAll(\"b\", \"$$&\");"));
    Assert.That(Body(IrIntrinsicOp.StrReplaceAll, new IrStrConst("b"), "es2020"), Does.Contain("return s.split(\"b\").join(\"$&\");"));
    Assert.That(Body(IrIntrinsicOp.StrReplaceAll, new IrStrConst(""), "es2022"), Does.Contain("return $replaceAll(s, \"\", \"$&\");"));
  }

  [Test]
  public void LastIndexOf_FromTheLengthIsTheMethod() {
    var text = new IrLocal("s", IrType.String);
    var other = new IrLocal("t", IrType.String);
    string Body(IrLocal measured) => JavaScriptEmitter.Emit(new IrModule("M", [new IrFunction("F", true, IrType.I32, [text, other], [], new IrBlock([
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrLastIndexOf, IrType.I32, [new IrLocalRef(text), new IrStrConst("b"),
        new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrLocalRef(measured)])]))]))]), JavaScriptProfile.Default).Single().Content;
    Assert.That(Body(text), Does.Contain("return s.lastIndexOf(\"b\");"));
    Assert.That(Body(other), Does.Contain("return $lastIndexOf(s, \"b\", t.length);"));
  }

  [Test]
  public void Comparison_OfComputedStringsReadsEachOnce() {
    var text = new IrLocal("s", IrType.String);
    var computed = new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [new IrLocalRef(text), new IrStrConst("x")]);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [text], [], new IrBlock([
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [computed, new IrLocalRef(text)]))]))]);
    Assert.That(JavaScriptEmitter.Emit(module, JavaScriptProfile.Default).Single().Content, Does.Contain("return $cmp(s + \"x\", s);"));
  }

  [Test]
  public void HelperSources_HaveEveryStringHelper() {
    var names = JavaScriptEmitter.HelperSources(JavaScriptProfile.Parse("es5")).Select(helper => helper.Name).ToList();
    Assert.That(names, Is.SupersetOf(new[] { "$unitAt", "$fromUnits", "$toUnits", "$repeat", "$substr", "$cmp", "$trimNet", "$trimEcma", "$pad", "$codePointAt", "$nextCodePoint" }));
  }

}
