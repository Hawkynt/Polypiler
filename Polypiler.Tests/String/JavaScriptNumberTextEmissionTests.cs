using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.String;

/// <summary>
/// The conversions between numbers and text written by the JavaScript backend (#1315): every operation
/// in every profile, parsed at the level it claims and using nothing its level lacks, and the form of
/// each where the levels differ. Whether the code computes what the interpreter computes is decided by
/// node in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class JavaScriptNumberTextEmissionTests {

  private static readonly JavaScriptProfile[] _profiles = [
    .. from level in JavaScriptProfile.LevelNames
       from typed in level == "es5" ? new[] { false, true } : [false]
       select JavaScriptProfile.Parse(level, "cjs", null, typed)
  ];

  private static string Emit(JavaScriptProfile profile, IrModule? module = null) {
    module ??= NumberTextModules.Operations;
    Assert.That(LegalizedJavaScript.Check(module, profile), Is.Empty);
    var text = LegalizedJavaScript.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, text);
    return text;
  }

  /// <summary>Test design 13 and 14: every operation at every level; nothing is inexpressible.</summary>
  [Test]
  public void EveryOperation_IsWrittenAtEveryLevel() {
    foreach (var profile in _profiles)
      Assert.That(Emit(profile), Does.Contain("function ParseNetULong(x)"), profile.ToString());
  }

  [TestCase("es5", "return String(x);")]
  [TestCase("es5", "return $radix64(x, 10, 0, false, true);")]
  [TestCase("es5", "return $fmtNet(x);")]
  [TestCase("es5", "return $fmtNet32(x);")]
  [TestCase("es5", "return x ? \"True\" : \"False\";")]
  [TestCase("es5", "return $radix(x >>> 0, 16, 8, false);")]
  [TestCase("es5", "return x.toString(7);")]
  [TestCase("es5", "return $radix(x & 0xFF, 16, 2, false);")]
  [TestCase("es5", "return (x & 0xFFFF).toString(16);")]
  [TestCase("es5", "return x.toString(36);")]
  [TestCase("es5", "return $radix64(x, 16, 16, true, false);")]
  [TestCase("es5", "return x.toFixed(2);")]
  [TestCase("es5", "return $fixedNet(x, 15);")]
  [TestCase("es5", "return Number(x);")]
  [TestCase("es5", "return parseFloat(x);")]
  [TestCase("es5", "return parseInt(x);")]
  [TestCase("es5", "return parseInt(x, 16);")]
  [TestCase("es5", "return $parseNet(x, true);")]
  [TestCase("es5", "return $parseNetInt(x, -2147483648, 2147483647);")]
  [TestCase("es5", "return $parseNet64(x, false);")]
  [TestCase("es5", "function $fround(value)")]
  [TestCase("es2015", "Math.fround(+x.toPrecision(p))")]
  [TestCase("es2020", "return String(x);")]
  [TestCase("es2020", "return BigInt.asUintN(64, x).toString(8);")]
  [TestCase("es2020", "return $radix(BigInt.asUintN(64, x), 16, 16, true);")]
  [TestCase("es2020", "BigInt($netDigits(s))")]
  [TestCase("es2022", "return x.toString(2);")]
  public void Operation_HasTheFormOfTheLevel(string level, string form)
    => Assert.That(Emit(JavaScriptProfile.Parse(level, "cjs")), Does.Contain(form));

  [Test]
  public void ALiteralReceiver_IsParenthesized() {
    var value = new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 2.5));
    var function = new IrFunction("F", true, IrType.String, [], [], new IrBlock([new IrReturn(NumberTextOperations.Fixed(IrIntrinsicOp.FixedEcma, value, 0))]));
    var radix = new IrFunction("G", true, IrType.String, [], [], new IrBlock([new IrReturn(NumberTextOperations.Radix(new IrConst(IrType.I32, 255), 16, 0, false, RadixMode.SignMagnitude))]));
    var text = Emit(JavaScriptProfile.Parse("es2022"), new IrModule("M", [function, radix]));
    Assert.That(text, Does.Contain("(2.5).toFixed(0)").And.Contain("(255).toString(16)"));
  }

  [Test]
  public void Header_NamesTheCaveatsOfTheImport() {
    var module = NumberTextModules.Operations with { Caveats = ["cs-invariant-culture"] };
    Assert.That(LegalizedJavaScript.Emit(module, JavaScriptProfile.Parse("es2022")).Single().Content, Does.Contain("// Caveats of the import: cs-invariant-culture."));
  }

}
