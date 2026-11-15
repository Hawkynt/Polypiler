using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Integer;

/// <summary>
/// The integer and bit operations in the JavaScript backend: a form for every operation and width in
/// every profile, at the level of that profile, with exactly the helpers it calls. Whether the forms
/// compute what the reference computes is asked of <c>node</c> in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class IntegerJavaScriptTests {

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
    => LegalizedJavaScript.Emit(IntegerCases.Module([IntegerCases.All.Single(@case => @case.Function == function)], "Snippet"), profile).Single().Content;

  /// <summary>The functions a module declares besides its own - the helpers of the library and those of the writer - in the order of their names.</summary>
  private static string[] Helpers(Core.GeneratedFile file) => [.. file.Helpers.Select(helper => helper.Symbol).Order(StringComparer.Ordinal)];

  private static Core.GeneratedFile File(string function, JavaScriptProfile profile)
    => LegalizedJavaScript.Emit(IntegerCases.Module([IntegerCases.All.Single(@case => @case.Function == function)], "Snippet"), profile).Single();

  /// <summary>
  /// Test design 13: every operation and width can be expressed in every profile - no PLY1152 - and
  /// the file uses nothing its level lacks: below ECMAScript 2020 no <c>BigInt</c>, without typed
  /// arrays none of them.
  /// </summary>
  [TestCaseSource(nameof(Profiles))]
  public void EveryOperation_HasAFormInEveryProfile(JavaScriptProfile profile) {
    var module = IntegerCases.Module(IntegerCases.All);
    Assert.That(LegalizedJavaScript.Check(module, profile), Is.Empty);
    var text = LegalizedJavaScript.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty);
    Assert.That(text, Does.Not.Contain("@"));
    if (profile.Level < JavaScriptLevel.Es2020)
      Assert.That(text, Does.Not.Contain("BigInt").And.Contain("function MulHighUnsigned64("));

    if (!profile.TypedArrays)
      Assert.That(text, Does.Not.Match(@"new \w+Array\("));
  }

  /// <summary>Every helper the module of all operations needs is declared once, and every declared helper is called.</summary>
  [TestCaseSource(nameof(Profiles))]
  public void Helpers_AreDeclaredOnceAndCalled(JavaScriptProfile profile) {
    var file = LegalizedJavaScript.Emit(IntegerCases.Module(IntegerCases.All), profile).Single();
    var helpers = Helpers(file);
    Assert.That(helpers, Is.Unique);
    foreach (var helper in helpers)
      Assert.That(file.Content.Split(helper + "(").Length, Is.GreaterThan(2), $"{helper} is declared but not called");
  }

  /// <summary>A module carries the helpers of the library its one operation needs, the ones those call, and no other.</summary>
  [TestCase("RotateLeft_u16", "es2022", new[] { "RotateLeftNarrow" })]
  [TestCase("RotateLeft_u32", "es2022", new[] { "RotateLeft32" })]
  [TestCase("RotateLeft_i64", "es2022", new[] { "RotateLeft64" })]
  [TestCase("RotateLeft_i64", "es5", new[] { "$or64", "$shl64", "$shr64", "RotateLeft64" })]
  [TestCase("ByteSwap_u32", "es5", new[] { "ByteSwap32" })]
  [TestCase("LeadingZeroCount_u8", "es5", new[] { "LeadingZeroCount32", "LeadingZeroCountNarrow" })]
  [TestCase("LeadingZeroCount_u8", "es2015", new string[0])]
  [TestCase("MulHigh_i64", "es2022", new string[0])]
  [TestCase("Abs_u32", "es2022", new string[0])]
  [TestCase("Sign_u64", "es5", new[] { "$eq64", "SignUnsigned64" })]
  [TestCase("StoreBig_u16", "es2022", new[] { "$set", "CheckBytes", "IndexOutOfRangeError", "Store32" })]
  public void Module_CarriesTheHelpersItCalls(string function, string level, string[] helpers)
    => Assert.That(Helpers(File(function, JavaScriptProfile.Parse(level))), Is.EqualTo(helpers));

  /// <summary>The forms where ECMAScript has the operation: <c>Math.clz32</c>, <c>Math.abs</c>, <c>Math.min</c> and the arithmetic of <c>BigInt</c>.</summary>
  [TestCase("LeadingZeroCount_i16", "es2022", "return Math.clz32(a & 0xFFFF) - 16;")]
  [TestCase("AbsWrap_i32", "es2022", "return (Math.abs(a)) | 0;")]
  [TestCase("Min_u16", "es2022", "return Math.min(a, b);")]
  [TestCase("MulHigh_i64", "es2022", "return BigInt.asIntN(64, a * b >> 64n);")]
  [TestCase("MulHigh_u8", "es2022", "return (Math.floor(a * b / 256)) & 0xFF;")]
  [TestCase("AddCarry_u64", "es2020", "return BigInt.asUintN(64, a + b + (c ? 1n : 0n));")]
  [TestCase("BorrowOut_u32", "es5", "return a < b + (c ? 1 : 0);")]
  [TestCase("Abs_u64", "es5", "return a;")]
  public void Form_IsTheLanguagesOwnWhereItHasOne(string function, string level, string body) {
    var lines = Emit(function, JavaScriptProfile.Parse(level)).Split('\n');
    var start = Array.FindIndex(lines, line => line.Contains($"function {function}("));
    Assert.That(lines[start + 1].Trim(), Is.EqualTo(body));
  }

  /// <summary>The scenario of the byte swap for JavaScript: ECMAScript 5 declares the helper of the library for it, as one of the module's functions.</summary>
  [Test]
  public void ByteSwap_IsAHelperAtEcmaScript5() {
    var text = Emit("ByteSwap_u32", JavaScriptProfile.Parse("es5"));
    Assert.That(text, Does.Contain("function ByteSwap32(value) {").And.Contain("return ByteSwap32(a);"));
  }

  /// <summary>An operation the integer families do not have, on an integer, is still the emitter's own: the bits of a float.</summary>
  [Test]
  public void FloatFromBits_IsNotAnIntegerOperation() {
    var bits = new IrLocal("a", IrType.I32);
    var module = new IrModule("Snippet", [new IrFunction("F", true, IrType.F32, [bits], [], new IrBlock([new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F32, [new IrLocalRef(bits)]))]))]);
    Assert.That(LegalizedJavaScript.Emit(module).Single().Content, Does.Contain("return $float32(a);"));
  }

}
