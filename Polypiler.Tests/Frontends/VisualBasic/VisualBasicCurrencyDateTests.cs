using NUnit.Framework;
using Polypiler.Core.Frontends.VisualBasic;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// <c>Currency</c> and <c>Date</c> (#1566) and the locale (#1565) in the interpreter: the operators and
/// conversions of each, typed and in a <c>Variant</c>, their boundaries and the constructs refused.
/// </summary>
[TestFixture]
public sealed class VisualBasicCurrencyDateTests {

  private static string Result(string body, string head = "", short n = 0, VisualBasicLocale? locale = null) {
    var source = VisualBasicBitwiseTests.Function(body, head);
    var imported = VisualBasicFrontend.Import("Test.bas", source, new VisualBasicImportOptions { Locale = locale ?? VisualBasicLocale.Default });
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
    return VisualBasicBitwiseTests.Run(imported.Module!, "F", n);
  }

  [TestCase("F = CStr(2.5@ * 2.5@)", "6.25")]
  [TestCase("F = CStr(CCur(-0.0001))", "-0.0001")]
  [TestCase("F = CStr(CCur(-12.5))", "-12.5")]
  [TestCase("F = CStr(CCur(12345.6789))", "12345.6789")]
  [TestCase("F = CStr(CCur(True)) & \" \" & CStr(CCur(CByte(3)))", "-1 3")]
  [TestCase("Dim c As Currency\nc = 1.5\nF = TypeName(c + 1) & \" \" & TypeName(c - 1.5) & \" \" & TypeName(c * 2) & \" \" & TypeName(c * 2.5) & \" \" & TypeName(c / 2)", "Currency Currency Currency Double Double")]
  [TestCase("Dim c As Currency\nc = 1.5\nF = TypeName(c \\ 1) & \" \" & (c \\ 1) & \" \" & TypeName(c ^ 2) & \" \" & (c Mod 1)", "Long 2 Double 0")]
  [TestCase("Dim c As Currency\nc = 1.5\nF = CStr(-c) & \" \" & CStr(c = 1.5) & \" \" & CStr(c > 1) & \" \" & CStr(c < CCur(2))", "-1.5 True True True")]
  [TestCase("Dim c As Currency\nc = 2.5\nF = CStr(CInt(c)) & \" \" & CStr(CByte(c)) & \" \" & CStr(CDbl(c)) & \" \" & CStr(CSng(c)) & \" \" & CStr(CBool(c))", "2 2 2.5 2.5 True")]
  [TestCase("Dim c As Currency\nc = \"1.25\"\nF = CStr(c)", "1.25")]
  [TestCase("Dim c As Currency\nc = 900000000000000@\nF = CStr(c * 2)", "!6")]
  [TestCase("Dim c As Currency\nc = 900000000000000@\nF = CStr(-c - c)", "!6")]
  [TestCase("F = CStr(CCur(1E+300))", "!6")]
  [TestCase("Dim c As Currency\nIf c Then F = \"x\" Else F = \"zero\"", "zero")]
  [TestCase("Dim c As Currency, d As Date\nd = #1/2/2000#\nc = d\nF = CStr(c)", "36527")]
  public void Currency_IsExactInTenThousandths(string body, string expected) => Assert.That(Result(body), Is.EqualTo(expected));

  [TestCase("Dim v\nv = CCur(1.5)\nIf n = 9 Then v = Null\nF = TypeName(v + 1) & \" \" & (v + 1) & \" \" & TypeName(v * 2.5) & \" \" & TypeName(v / 2)", "Currency 2.5 Double Double")]
  [TestCase("Dim v\nv = CCur(1.5)\nIf n = 9 Then v = Null\nF = CStr(-v) & \" \" & CStr(v - CCur(0.5)) & \" \" & CStr(v * CCur(2))", "-1.5 1 3")]
  [TestCase("Dim v\nv = CCur(1.5)\nIf n = 9 Then v = Null\nF = CStr(v > 1) & CStr(v = CCur(1.5)) & CStr(v < \"2\")", "TrueTrueTrue")]
  [TestCase("Dim v\nv = CCur(1.5)\nIf n = 9 Then v = Null\nDim c As Currency, d As Double, b As Byte, s As String\nc = v\nd = v\nb = v\ns = v\nF = CStr(c) & \" \" & d & \" \" & b & \" \" & s", "1.5 1.5 2 1.5")]
  [TestCase("Dim v\nv = CCur(1.5)\nIf n = 9 Then v = Null\nF = TypeName(v) & VarType(v) & IsNumeric(v)", "Currency6True")]
  [TestCase("Dim v\nv = CCur(1.5)\nIf n = 9 Then v = Null\nF = TypeName(v + Empty) & TypeName(Empty + v) & TypeName(v + Null)", "CurrencyCurrencyNull")]
  [TestCase("Dim v\nv = CCur(1.5)\nIf n = 9 Then v = Null\nF = TypeName(v + #1/2/2000#) & \" \" & TypeName(v + \"1\")", "Date Currency")]
  [TestCase("Dim v\nv = CCur(1.5)\nIf n = 9 Then v = Null\nF = TypeName(v / CCur(0))", "!11")]
  public void Currency_InAVariant_NeverPromotes(string body, string expected) => Assert.That(Result(body), Is.EqualTo(expected));

  [TestCase("F = CStr(CDbl(#1:30 PM#))", "0.5625")]
  [TestCase("F = CStr(CDbl(#12:30:00 AM#))", "0.0208333333333333")]
  [TestCase("F = CStr(CDbl(#1/2/99#)) & \" \" & CStr(CDbl(#1/2/29#))", "36162 47120")]
  [TestCase("F = CStr(CDbl(#12/29/1899 6:00 AM#))", "-1.25")]
  [TestCase("Dim d As Date\nd = #1/2/2000#\nF = CStr(CDbl(d - 1)) & \" \" & CStr(CDbl(-d)) & \" \" & CStr(CLng(d + 0.6)) & \" \" & TypeName(d / 2)", "36526 -36527 36528 Double")]
  [TestCase("Dim d As Date\nd = #1/2/2000#\nF = CStr(d \\ 2) & \" \" & TypeName(d ^ 1) & \" \" & CStr(d = #1/2/2000#) & \" \" & IsNumeric(d)", "18263 Double True False")]
  [TestCase("F = CStr(CDbl(TimeSerial(-1, 0, 0)))", "-1.95833333333333")]
  [TestCase("F = CStr(CDbl(TimeSerial(25, 0, 0)))", "1.04166666666667")]
  [TestCase("F = CStr(Hour(0.99999999)) & \" \" & CStr(Day(0.99999999))", "0 31")]
  [TestCase("F = CStr(Weekday(#1/2/2000#, 0)) & \" \" & CStr(Weekday(#1/2/2000#, 7))", "1 2")]
  [TestCase("F = CStr(Weekday(#1/2/2000#, 8))", "!5")]
  [TestCase("F = CStr(CDbl(CDate(2958466)))", "!6")]
  [TestCase("F = CStr(CDbl(CDate(\"1.5\")))", "1.5")]
  [TestCase("F = CStr(CDbl(DateSerial(2000, -1, 1)))", "36465")]
  [TestCase("Dim v\nv = #1/2/2000#\nIf n = 9 Then v = Null\nF = TypeName(v - #1/1/2000#) & \" \" & TypeName(v * 2) & \" \" & TypeName(v + 3000000) & \" \" & TypeName(-v)", "Double Double Double Date")]
  [TestCase("Dim v\nv = #1/2/2000#\nIf n = 9 Then v = Null\nDim d As Date\nd = v\nF = CStr(Year(d)) & \" \" & CStr(Year(v))", "2000 2000")]
  [TestCase("Dim v\nv = #1/2/2000#\nIf n = 9 Then v = Null\nF = v & \"\"", "!13")]
  public void Date_IsDaysSince1899(string body, string expected) => Assert.That(Result(body), Is.EqualTo(expected));

  [TestCase("Dim v\nv = CCur(1)\nIf n = 9 Then v = Null\nF = CStr(v ^ 2)", "'^' of a Variant that may hold a Currency")]
  [TestCase("Dim v\nv = CCur(1)\nIf n = 9 Then v = Null\nF = CStr(v \\ 2)", "'\\' of a Variant that may hold a Currency")]
  [TestCase("Dim v\nv = CCur(1)\nIf n = 9 Then v = Null\nIf v Then F = \"x\"", "a condition of a Variant that may hold a Currency")]
  [TestCase("Dim v, w\nv = CCur(1)\nIf n = 9 Then v = Null\nw = Null\nF = CStr(IsNull(v & w))", "'&' of two Variants that may be Null and may hold a Currency")]
  [TestCase("Dim v\nv = CCur(1)\nIf n = 9 Then v = Null\nF = CStr(Not v)", "Not of a Variant that may hold a Currency")]
  [TestCase("F = CStr(#2/30/2000#)", "names no date")]
  [TestCase("F = CStr(#13:00 PM#)", "12-hour clock")]
  [TestCase("F = CStr(99999999999999999@)", "does not fit a Currency")]
  public void CurrencyAndDate_ThatTheFrontendDoesNotModel_AreRefused(string body, string message)
    => Assert.That(VisualBasicBitwiseTests.Refused(VisualBasicBitwiseTests.Function(body)), Does.Contain(message));

  [TestCase("en-GB", "F = CStr(1.5) & \"|\" & CStr(CDbl(\"1,000.5\"))", "1.5|1000.5")]
  [TestCase("fr-FR", "F = CStr(1.5) & \"|\" & CStr(CDbl(\"1 000,5\"))", "1,5|1000,5")]
  [TestCase("de-DE", "F = CStr(CCur(1.25)) & \"|\" & CStr(Val(\"1,5\")) & \"|\" & CStr(CSng(\"0,5\"))", "1,25|1|0,5")]
  [TestCase("de-DE", "Dim v\nv = \"2,5\"\nIf n = 9 Then v = Null\nF = CStr(v * 2) & \"|\" & CStr(v > 2) & \"|\" & CStr(IsNumeric(v))", "5|True|True")]
  [TestCase("en-US", "F = CStr(CDbl(\"&O17\")) & \"|\" & CStr(Val(\"&O17x\")) & \"|\" & CStr(Val(\"&H\")) & \"|\" & CStr(Val(\"&HFFFFFFFF\"))", "15|15|0|-1")]
  [TestCase("en-US", "F = CStr(Val(\"&H100000000\"))", "!6")]
  [TestCase("en-US", "F = CStr(CDbl(\"&H\")) & CStr(CDbl(\"&HG\"))", "!13")]
  [TestCase("en-US", "F = CStr(Val(\"1e\")) & \"|\" & CStr(Val(\"1e-2\")) & \"|\" & CStr(Val(\"+.5\")) & \"|\" & CStr(Val(\"1e400\"))", "!6")]
  [TestCase("en-US", "F = CStr(Val(\"1e\")) & \"|\" & CStr(Val(\"1e-2\")) & \"|\" & CStr(Val(\"+.5\")) & \"|\" & CStr(Val(\"1x\"))", "1|0.01|0.5|1")]
  [TestCase("en-US", "F = CStr(CDbl(\" + 1 \")) & \"|\" & CStr(CDbl(\"1 e 2\")) & \"|\" & CStr(CDbl(\"1e-2\")) & \"|\" & CStr(CBool(\"#TRUE#\")) & CStr(CBool(\"#FALSE#\")) & CStr(CBool(\"FALSE\"))", "1|100|0.01|TrueFalseFalse")]
  [TestCase("en-US", "F = CStr(CDbl(\"1e\"))", "!13")]
  [TestCase("en-US", "F = CStr(CSng(\"1e39\"))", "!6")]
  [TestCase("en-US", "F = CStr(CSng(\"x\"))", "!13")]
  [TestCase("en-US", "Dim s As String\ns = \"7\"\nF = CStr(-s) & \"|\" & CStr(Not s) & \"|\" & CStr(s \\ 2) & \"|\" & CStr(s ^ 2) & \"|\" & CStr(s * \"2\")", "-7|-8|3|49|14")]
  public void Locale_DecidesTheSeparators(string locale, string body, string expected) {
    VisualBasicLocale.TryParse(locale, out var parsed);
    Assert.That(Result(body, locale: parsed), Is.EqualTo(expected));
  }

  [Test]
  public void Locale_IsOneOfFourByName() {
    Assert.That(VisualBasicLocale.All, Has.Count.EqualTo(4));
    Assert.That(VisualBasicLocale.TryParse("DE-de", out var german), Is.True);
    Assert.That(german, Is.SameAs(VisualBasicLocale.DeDe));
    Assert.That(german!.ToString(), Is.EqualTo("de-DE"));
    Assert.That((german.Decimal, german.Group), Is.EqualTo((',', '.')));
    Assert.That(VisualBasicLocale.TryParse("xx-XX", out var none), Is.False);
    Assert.That(none, Is.Null);
    Assert.That(VisualBasicLocale.TryParse(null, out _), Is.False);
    Assert.That(new VisualBasicImportOptions().Locale, Is.SameAs(VisualBasicLocale.EnUs));
    Assert.That(VisualBasicLocale.FrFr.Group, Is.EqualTo((char)0xA0));
    Assert.That(VisualBasicLocale.EnGb.Decimal, Is.EqualTo('.'));
  }

}
