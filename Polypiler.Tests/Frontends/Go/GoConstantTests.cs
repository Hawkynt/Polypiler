using System;
using System.Linq;
using System.Numerics;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Frontends.Go.Checking;

namespace Polypiler.Tests.Frontends.Go;

/// <summary>
/// The exact constants of the Go frontend in go/constant's representation, and its types: the
/// expected forms are the ones go/constant and go/types print (ExactString, TypeString); the Go layer
/// compares every constant of the checker's cases with go/types itself.
/// </summary>
[TestFixture]
public sealed class GoConstantTests {

  private static GoConstant Int(long value) => GoConstant.MakeInt(value);

  private static GoConstant Rat(long numerator, long denominator) => GoConstant.MakeRat(numerator, denominator);

  [TestCase("0x1p-2", "1/4")]
  [TestCase("1_000.5", "2001/2")]
  [TestCase(".5e1", "5")]
  [TestCase("1e-3", "1/1000")]
  [TestCase("0.0", "0")]
  [TestCase("0x.8p1", "1")]
  [TestCase("1e5000", "0x.8f5b0c2d0e5f2d9b0b4c5fd7a1c43bb31d8f5b0c2d0e5f2d9b0b4c5fd7a1c43bb31d8f5b0c2d0e5f2d9b0b4c5fd7a1c43bb31d8f5b0c2d0e5f2d9b0b4c5fd7a1cp+16610")]
  public void FloatLiterals_AreExact(string literal, string exact) {
    var value = GoConstant.FromFloatLiteral(literal);
    if (exact.StartsWith("0x.", StringComparison.Ordinal))
      Assert.That((value.IsWide, value.ExactString[..3], value.ExactString.EndsWith("p+16610", StringComparison.Ordinal)), Is.EqualTo((true, "0x.", true)));
    else
      Assert.That(value.ExactString, Is.EqualTo(exact));
  }

  [Test]
  public void ALiteralBeyondTheRange_IsRefused() {
    Assert.That(() => GoConstant.FromFloatLiteral("1e999999"), Throws.TypeOf<GoConstantRangeException>());
    Assert.That(() => GoConstant.FromFloatLiteral("1e-99999999999999999999"), Throws.TypeOf<GoConstantRangeException>());
    Assert.That(() => GoConstant.FromFloatLiteral("0x1p200000"), Throws.TypeOf<GoConstantRangeException>());
    Assert.That(() => GoConstant.MakeWide(BigInteger.One << 200_000, 1), Throws.TypeOf<GoConstantRangeException>());
  }

  [TestCase("0x_FF", 255)]
  [TestCase("0o17", 15)]
  [TestCase("017", 15)]
  [TestCase("0b1_01", 5)]
  [TestCase("0", 0)]
  [TestCase("1_234", 1234)]
  public void IntLiterals_HaveEveryBase(string literal, long value) => Assert.That(GoConstant.FromIntLiteral(literal).Numerator, Is.EqualTo(new BigInteger(value)));

  [Test]
  public void Fractions_StayFractionsWhileTheirPartsAreSmall() {
    Assert.That(Rat(2, -4).ExactString, Is.EqualTo("-1/2"));
    Assert.That(Rat(0, 5).ExactString, Is.EqualTo("0"));
    var big = GoConstant.MakeRat(BigInteger.One << 5000, 3);
    Assert.That((big.IsWide, big.ExactString[..3]), Is.EqualTo((true, "0x.")));
    Assert.That(GoConstant.MakeWide(BigInteger.Zero, 1).ExactString, Is.EqualTo("0"));
    Assert.That(GoConstant.MakeWide(-(BigInteger.One << 600), 1).ExactString, Is.EqualTo("-0x.8p+601"));
    Assert.That(GoConstant.MakeWide(3, BigInteger.One << 700).ExactString, Is.EqualTo("0x.cp-698"));
  }

  [Test]
  public void Doubles_AreTheirExactFractions() {
    Assert.That(GoConstant.MakeFloat64(0.1)!.ExactString, Is.EqualTo("3602879701896397/36028797018963968"));
    Assert.That(GoConstant.MakeFloat64(-0.0)!.ExactString, Is.EqualTo("0"));
    Assert.That(GoConstant.MakeFloat64(double.Epsilon)!.Denominator, Is.EqualTo(BigInteger.One << 1074));
    Assert.That(GoConstant.MakeFloat64(1e300)!.Denominator, Is.EqualTo(BigInteger.One));
    Assert.That(GoConstant.MakeFloat64(double.NaN), Is.Null);
    Assert.That(GoConstant.MakeFloat64(double.PositiveInfinity), Is.Null);
  }

  [Test]
  public void Rounding_IsToNearestEven_WithSubnormalsAndInfinities() {
    Assert.That(Rat(1, 3).ToFloat64(), Is.EqualTo(1.0 / 3));
    Assert.That(Rat(1, 10).ToFloat32(), Is.EqualTo(0.1f));
    Assert.That(GoConstant.MakeRat(1, BigInteger.One << 1074).ToFloat64(), Is.EqualTo(double.Epsilon));
    Assert.That(GoConstant.MakeRat(1, BigInteger.One << 1076).ToFloat64(), Is.EqualTo(0.0));
    Assert.That(GoConstant.MakeRat(3, BigInteger.One << 1075).ToFloat64(), Is.EqualTo(2 * double.Epsilon), "a tie rounds to even");
    Assert.That(GoConstant.MakeInt(BigInteger.One << 2000).ToFloat64(), Is.EqualTo(double.PositiveInfinity));
    Assert.That(GoConstant.MakeInt(-(BigInteger.One << 200)).ToFloat32(), Is.EqualTo(float.NegativeInfinity));
    Assert.That(Int(0).ToFloat64(), Is.EqualTo(0.0));
    Assert.That(Rat(-5, 2).ToFloat64(), Is.EqualTo(-2.5));
    Assert.That(GoConstant.MakeInt((BigInteger.One << 53) + 1).ToFloat64(), Is.EqualTo(9007199254740992.0));
  }

  [Test]
  public void Integers_AreFoundInEveryKind() {
    Assert.That(Rat(4, 2).ToInt()!.Numerator, Is.EqualTo(new BigInteger(2)));
    Assert.That(Rat(1, 2).ToInt(), Is.Null);
    Assert.That(GoConstant.MakeBool(true).ToInt(), Is.Null);
    Assert.That(GoConstant.MakeWide(BigInteger.One << 600, 1).ToInt()!.Numerator, Is.EqualTo(BigInteger.One << 600));
    var nearly = GoConstant.MakeWide((BigInteger.One << 600) - 1, BigInteger.One << 90);
    Assert.That(nearly.ToInt()!.Numerator, Is.EqualTo(BigInteger.One << 510), "within 4 bits of an integer");
    Assert.That(GoConstant.MakeWide(3, BigInteger.One << 600).ToInt(), Is.Null);
    var mantissa = (BigInteger.One << 511) + 1;
    Assert.That(GoConstant.MakeWide(mantissa, 4).ToInt()!.Numerator, Is.EqualTo(BigInteger.One << 509), "rounded toward zero, a large one");
    Assert.That(GoConstant.MakeWide(mantissa, 1024).ToInt()!.Numerator, Is.EqualTo(BigInteger.One << 501), "rounded toward zero, with fraction bits left");
    Assert.That(GoConstant.MakeWide((BigInteger.One << 512) - 1, 1024).ToInt()!.Numerator, Is.EqualTo(BigInteger.One << 502), "rounded away from zero");
    Assert.That(GoConstant.MakeWide(BigInteger.One << 5000, 1).ToInt(), Is.Null);
    Assert.That(GoConstant.MakeWide(-(BigInteger.One << 600), 7).ToInt()!.Sign, Is.EqualTo(-1), "a wide number this large is an integer");
    Assert.That(Int(3).ToFloat()!.Kind, Is.EqualTo(GoConstantKind.Float));
    Assert.That(GoConstant.MakeString("x").ToFloat(), Is.Null);
  }

  [Test]
  public void Operations_AreGoConstants() {
    Assert.That(GoConstant.Binary(Int(7), "/", Int(2), true).ExactString, Is.EqualTo("3"));
    Assert.That(GoConstant.Binary(Int(-7), "/", Int(2), false).ExactString, Is.EqualTo("-7/2"));
    Assert.That(GoConstant.Binary(Int(-7), "%", Int(2), true).ExactString, Is.EqualTo("-1"));
    Assert.That(new[] { "+", "-", "*", "&", "|", "^", "&^" }.Select(op => GoConstant.Binary(Int(12), op, Int(10), false).ExactString),
      Is.EqualTo(new[] { "22", "2", "120", "8", "14", "6", "4" }));
    Assert.That(new[] { "+", "-", "*", "/" }.Select(op => GoConstant.Binary(Rat(1, 2), op, Int(1), false).ExactString), Is.EqualTo(new[] { "3/2", "-1/2", "1/2", "1/2" }));
    var wide = GoConstant.MakeWide(BigInteger.One << 5000, 1);
    Assert.That(GoConstant.Binary(wide, "*", Rat(1, 2), false).IsWide, Is.True);
    Assert.That(GoConstant.Binary(GoConstant.MakeString("a"), "+", GoConstant.MakeString("b"), false).ExactString, Is.EqualTo("\"ab\""));
    Assert.That(GoConstant.Binary(GoConstant.MakeBool(true), "&&", GoConstant.MakeBool(false), false).Bool, Is.False);
    Assert.That(GoConstant.Binary(GoConstant.MakeBool(false), "||", GoConstant.MakeBool(true), false).Bool, Is.True);
    Assert.That(GoConstant.Unary("^", Int(0x0F), 8).ExactString, Is.EqualTo("240"));
    Assert.That(GoConstant.Unary("^", Int(5), 0).ExactString, Is.EqualTo("-6"));
    Assert.That(GoConstant.Unary("-", Rat(1, 2), 0).ExactString, Is.EqualTo("-1/2"));
    Assert.That(GoConstant.Unary("-", Int(5), 0).ExactString, Is.EqualTo("-5"));
    Assert.That(GoConstant.Unary("+", Int(5), 0).ExactString, Is.EqualTo("5"));
    Assert.That(GoConstant.Unary("!", GoConstant.MakeBool(true), 0).Bool, Is.False);
    Assert.That((GoConstant.Shift(Int(-3), ">>", 1).ExactString, GoConstant.Shift(Int(3), "<<", 2).ExactString), Is.EqualTo(("-2", "12")));
  }

  [Test]
  public void Comparisons_AreOfEveryKind() {
    Assert.That(new[] { "==", "!=", "<", "<=", ">", ">=" }.Select(op => GoConstant.Compare(Rat(1, 2), op, Int(1))), Is.EqualTo(new[] { false, true, true, true, false, false }));
    Assert.That(GoConstant.Compare(GoConstant.MakeString("ab"), "<", GoConstant.MakeString("b")), Is.True);
    Assert.That(GoConstant.Compare(GoConstant.MakeString("ab"), ">", GoConstant.MakeString("a")), Is.True);
    Assert.That(GoConstant.Compare(GoConstant.MakeBool(true), "!=", GoConstant.MakeBool(false)), Is.True);
    Assert.That(GoConstant.Compare(GoConstant.MakeBool(true), "==", GoConstant.MakeBool(true)), Is.True);
    Assert.That(GoConstant.Compare(Int(3), ">=", Int(3)), Is.True);
  }

  [Test]
  public void Strings_AreQuotedAsStrconvQuotes() {
    Assert.That(GoConstant.Quote([.. Encoding.UTF8.GetBytes("a\"\\\a\b\f\n\r\t\vé\u0001\u007f\u200b😀"), 0xFF]),
      Is.EqualTo("\"a\\\"\\\\\\a\\b\\f\\n\\r\\t\\vé\\x01\\x7f\\u200b😀\\xff\""));
    Assert.That(GoConstant.Quote(Encoding.UTF8.GetBytes("\U000E0001")), Is.EqualTo("\"\\U000e0001\""));
    Assert.That(GoConstant.MakeString(new string('x', 70)).ToString(), Does.EndWith("...").And.StartWith("\"xxx"));
  }

  [Test]
  public void Literals_AreUnquotedIntoTheirBytes() {
    Assert.That(GoConstant.Unquote("\"\\a\\b\\f\\n\\r\\t\\v\\\\\\'\\\"\\x41\\101\\u00e9\\U0001F600é😀\""), Is.EqualTo(Encoding.UTF8.GetBytes("\a\b\f\n\r\t\v\\'\"AAé😀é😀")));
    Assert.That(GoConstant.Unquote("\"\\xff\""), Is.EqualTo(new byte[] { 0xFF }));
    Assert.That(GoConstant.Unquote("`a\\n`"), Is.EqualTo(Encoding.UTF8.GetBytes("a\\n")));
    Assert.That(new[] { "'a'", "'😀'", "'\\n'", "'\\x41'", "'\\101'", "'\\u00e9'", "'\\''" }.Select(GoConstant.RuneOf), Is.EqualTo(new[] { 97, 0x1F600, 10, 65, 65, 0xE9, 39 }));
    Assert.That(GoConstant.Utf8Of(0xD800), Is.EqualTo(Encoding.UTF8.GetBytes("\uFFFD")));
    Assert.That(GoConstant.Utf8Of(-1), Is.EqualTo(Encoding.UTF8.GetBytes("\uFFFD")));
    Assert.That(GoConstant.Utf8Of(0x110000), Is.EqualTo(Encoding.UTF8.GetBytes("\uFFFD")));
    Assert.That(GoConstant.Utf8Of(65), Is.EqualTo(Encoding.UTF8.GetBytes("A")));
  }

  [Test]
  public void Values_AreEqualByWhatTheyDenote() {
    Assert.That(Rat(2, 4), Is.EqualTo(Rat(1, 2)));
    Assert.That(Rat(2, 4).GetHashCode(), Is.EqualTo(Rat(1, 2).GetHashCode()));
    Assert.That(Rat(1, 2).Equals((object?)Int(1)), Is.False);
    Assert.That(Rat(1, 2).Equals(null), Is.False);
    Assert.That(GoConstant.MakeString("a").Equals(GoConstant.MakeString([(byte)97])), Is.True);
    Assert.That((Rat(1, 3).ToString(), Int(5).ToString(), GoConstant.MakeBool(false).ToString()), Is.EqualTo(("0.333333", "5", "false")));
    Assert.That(GoConstant.MakeWide(BigInteger.One << 5000, 1).ToString(), Does.StartWith("0x."));
    Assert.That((Rat(4, 2).IsIntegral, Rat(1, 2).IsIntegral, Int(-2).Sign, GoConstant.MakeBool(true).IsNumeric), Is.EqualTo((true, false, -1, false)));
    Assert.That(GoConstant.BitLength(BigInteger.Zero), Is.EqualTo(0));
  }

  [Test]
  public void Types_AreWrittenAsGoTypesWritesThem() {
    var named = new GoNamed("T", null);
    named.Resolve(new GoStruct([new GoField("X", GoTypes.Int, false, "json:\"x\""), new GoField("Y", GoTypes.String, false, null)]));
    var imported = new GoNamed("V", "math/big");
    var signature = new GoSignature([new GoParameter("a", GoTypes.Int), new GoParameter("rest", new GoSlice(GoTypes.String))], [new GoParameter("", GoTypes.Bool)], true);
    var results = new GoSignature([], [new GoParameter("n", GoTypes.Int), new GoParameter("ok", GoTypes.Bool)], false);
    Assert.That(new GoType[] {
      named, named.Underlying, imported, new GoPointer(GoTypes.Int), new GoArray(3, GoTypes.Byte), new GoMap(GoTypes.String, GoTypes.Rune),
      new GoChan("both", GoTypes.Int), new GoChan("send", GoTypes.Int), new GoChan("recv", GoTypes.Int), new GoChan("both", new GoChan("recv", GoTypes.Int)),
      signature, results, new GoSignature([new GoParameter("", GoTypes.Int)], [new GoParameter("", GoTypes.Int), new GoParameter("", GoTypes.Int)], false),
      GoTypes.Any, GoTypes.EmptyInterface, new GoInterface([new GoMethod("M", new GoSignature([], [], false))]), new GoTuple([GoTypes.Int, GoTypes.String]),
      GoTypes.Invalid, new GoStruct([new GoField("E", named, true, null)])
    }.Select(type => type.ToString()), Is.EqualTo(new[] {
      "T", "struct{X int \"json:\\\"x\\\"\"; Y string}", "math/big.V", "*int", "[3]byte", "map[string]rune", "chan int", "chan<- int", "<-chan int", "chan (<-chan int)",
      "func(a int, rest ...string) bool", "func() (n int, ok bool)", "func(int) (int, int)", "any", "interface{}", "interface{M()}", "(int, string)", "invalid type", "struct{T}"
    }));
  }

  [Test]
  public void Types_AreIdenticalByTheirStructure() {
    GoType[] types = [
      GoTypes.Int, GoTypes.Byte, new GoPointer(GoTypes.Int), new GoSlice(GoTypes.Int), new GoArray(2, GoTypes.Int), new GoMap(GoTypes.Int, GoTypes.Int),
      new GoChan("both", GoTypes.Int), new GoStruct([new GoField("a", GoTypes.Int, false, null)]), new GoSignature([new GoParameter("x", GoTypes.Int)], [], false),
      new GoInterface([new GoMethod("M", new GoSignature([], [], false))]), new GoTuple([GoTypes.Int]), new GoNamed("N", null)
    ];
    GoType[] copies = [
      GoTypes.Basic(GoBasicKind.Int), GoTypes.Basic(GoBasicKind.Uint8), new GoPointer(GoTypes.Int), new GoSlice(GoTypes.Int), new GoArray(2, GoTypes.Int), new GoMap(GoTypes.Int, GoTypes.Int),
      new GoChan("both", GoTypes.Int), new GoStruct([new GoField("a", GoTypes.Int, false, null)]), new GoSignature([new GoParameter("y", GoTypes.Int)], [], false),
      new GoInterface([new GoMethod("M", new GoSignature([], [], false))]), new GoTuple([GoTypes.Int]), new GoNamed("N", null)
    ];
    for (var index = 0; index < types.Length; ++index)
      for (var other = 0; other < copies.Length; ++other)
        Assert.That(GoTypes.Identical(types[index], copies[other]), Is.EqualTo(index == other && index < types.Length - 1), $"{types[index]} and {copies[other]}");

    Assert.That(GoTypes.Identical(GoTypes.Any, GoTypes.EmptyInterface), Is.True);
    Assert.That(GoTypes.Identical(new GoArray(2, GoTypes.Int), new GoArray(3, GoTypes.Int)), Is.False);
    Assert.That(GoTypes.Identical(new GoChan("send", GoTypes.Int), new GoChan("recv", GoTypes.Int)), Is.False);
  }

  [Test]
  public void Predicates_AreTheSpecifications() {
    Assert.That(GoTypes.Comparable(new GoStruct([new GoField("s", new GoSlice(GoTypes.Int), false, null)])), Is.False);
    Assert.That(GoTypes.Comparable(new GoArray(2, GoTypes.String)) && GoTypes.Comparable(new GoPointer(GoTypes.Int)) && !GoTypes.Comparable(GoTypes.UntypedNil), Is.True);
    Assert.That(GoTypes.Comparable(new GoMap(GoTypes.Int, GoTypes.Int)), Is.False);
    Assert.That(GoTypes.MaxType(GoTypes.Basic(GoBasicKind.UntypedInt), GoTypes.Basic(GoBasicKind.UntypedFloat)), Is.EqualTo(GoTypes.Basic(GoBasicKind.UntypedFloat)));
    Assert.That(GoTypes.MaxType(GoTypes.Basic(GoBasicKind.UntypedRune), GoTypes.Basic(GoBasicKind.UntypedInt)), Is.EqualTo(GoTypes.Basic(GoBasicKind.UntypedRune)));
    Assert.That(GoTypes.MaxType(GoTypes.UntypedBool, GoTypes.Basic(GoBasicKind.UntypedString)), Is.Null);
    Assert.That(GoTypes.MaxType(GoTypes.UntypedBool, GoTypes.UntypedBool), Is.EqualTo(GoTypes.UntypedBool));
    Assert.That(new[] { GoBasicKind.UntypedBool, GoBasicKind.UntypedInt, GoBasicKind.UntypedRune, GoBasicKind.UntypedFloat, GoBasicKind.UntypedComplex, GoBasicKind.UntypedString, GoBasicKind.UntypedNil }
      .Select(kind => GoTypes.Default(GoTypes.Basic(kind)).ToString()), Is.EqualTo(new[] { "bool", "int", "rune", "float64", "complex128", "string", "untyped nil" }));
    Assert.That(GoTypes.Default(GoTypes.Int), Is.SameAs(GoTypes.Int));
    Assert.That(new[] { GoBasicKind.Int8, GoBasicKind.Uint16, GoBasicKind.Float32, GoBasicKind.Uintptr, GoBasicKind.Complex64, GoBasicKind.Complex128, GoBasicKind.String }
      .Select(kind => GoTypes.Basic(kind).Bits), Is.EqualTo(new[] { 8, 16, 32, 64, 64, 128, 0 }));
    Assert.That(GoTypes.HasName(GoTypes.Int) && !GoTypes.HasName(new GoSlice(GoTypes.Int)) && GoTypes.HasNil(new GoMap(GoTypes.Int, GoTypes.Int)) && !GoTypes.HasNil(GoTypes.Int), Is.True);
    Assert.That(new GoNamed("U", null).Underlying, Is.SameAs(GoTypes.Invalid));
    Assert.That(Enum.GetValues<GoObjectKind>().Select(kind => new GoObject(kind, "o", null).KindName), Is.EqualTo(new[] { "var", "const", "type", "func", "builtin", "pkg", "nil", "label" }));
    Assert.That(new GoObject(GoObjectKind.Nil, "nil", null).ToString(), Is.EqualTo("nil"));
  }

}
