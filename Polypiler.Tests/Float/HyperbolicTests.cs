using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Float;

/// <summary>
/// The branches of the hyperbolic functions that the reference vectors do not reach: the arguments
/// whose fast result lies too close to a rounding boundary, taken from CORE-MATH's worst cases
/// (<c>sinh.wc</c>, <c>cosh.wc</c>, <c>tanh.wc</c>) and from the binary32 arguments that need the
/// accurate polynomial. Every expected result is MPFR's, rounded to nearest; the compiled and the
/// imported implementation have to give it.
/// </summary>
[TestFixture]
public sealed class HyperbolicTests {

  // The double-double polynomial near zero, whose middle part is a power of two moved down
  // (0x1.0dc68d5e8f959p-3) or up (0x1.00a0a44cc45bp-3).
  [TestCase(0x3fc0dc68d5e8f959L, 0x3fc0e8e73dc4fee4L)]
  [TestCase(unchecked((long)0xbfc0dc68d5e8f959UL), unchecked((long)0xbfc0e8e73dc4fee4UL))]
  [TestCase(0x3fc00a0a44cc45b0L, 0x3fc014cb39ade240L)]
  [TestCase(unchecked((long)0xbfc00a0a44cc45b0UL), unchecked((long)0xbfc014cb39ade240UL))]
  // The accurate exponentials and a nearly halfway result: one listed exception (0x1.4169f234f23b9p-2),
  // and two that are not (0x1.07f309caa4695p-2, 0x1.09e1151d501fcp+2).
  [TestCase(0x3fd4169f234f23b9L, 0x3fd46b7b3b358f99L)]
  [TestCase(unchecked((long)0xbfd4169f234f23b9UL), unchecked((long)0xbfd46b7b3b358f99UL))]
  [TestCase(0x3fd07f309caa4695L, 0x3fd0ae1c8e723d30L)]
  [TestCase(unchecked((long)0xbfd07f309caa4695UL), unchecked((long)0xbfd0ae1c8e723d30UL))]
  [TestCase(0x40109e1151d501fcL, 0x403fd90ba7506748L)]
  [TestCase(unchecked((long)0xc0109e1151d501fcUL), unchecked((long)0xc03fd90ba7506748UL))]
  // Above 31.4 the accurate path keeps e^-|x| in doubles (0x1.04b2e7a155193p+5).
  [TestCase(0x40404b2e7a155193L, 0x42d026d7b4e8e7f7L)]
  [TestCase(unchecked((long)0xc0404b2e7a155193UL), unchecked((long)0xc2d026d7b4e8e7f7UL))]
  // Above 36.7 only e^|x| counts; nearly halfway results (0x1.181ccd8144be7p+6, 0x1.2c1e030b5decdp+9).
  [TestCase(0x405181ccd8144be7L, 0x463053d7702a0c15L)]
  [TestCase(unchecked((long)0xc05181ccd8144be7UL), unchecked((long)0xc63053d7702a0c15UL))]
  [TestCase(0x4082c1e030b5decdL, 0x75ff06050a008390L)]
  [TestCase(unchecked((long)0xc082c1e030b5decdUL), unchecked((long)0xf5ff06050a008390UL))]
  public void Sinh_OfAWorstCase_IsCorrectlyRounded(long x, long expected)
    => AssertResult("Sinh", IrType.F64, x, expected);

  // The double-double polynomial near zero, whose middle part is a power of two moved down
  // (0x1.9a3b9a61b3839p-9) or up (0x1.0cf01d3f9f5e4p-11), or has an all-ones significand
  // (0x1.07074ccbc86adp-21).
  [TestCase(0x3f69a3b9a61b3839L, 0x3ff0000522c6371aL)]
  [TestCase(unchecked((long)0xbf69a3b9a61b3839UL), 0x3ff0000522c6371aL)]
  [TestCase(0x3f40cf01d3f9f5e4L, 0x3ff000002350f3dbL)]
  [TestCase(unchecked((long)0xbf40cf01d3f9f5e4UL), 0x3ff000002350f3dbL)]
  [TestCase(0x3ea07074ccbc86adL, 0x3ff000000000021cL)]
  [TestCase(unchecked((long)0xbea07074ccbc86adUL), 0x3ff000000000021cL)]
  // The listed exceptions 0x1.03923f2b47c07p-1, 0x1.e9cc7ed2e1a7ep+0 and 0x1.743d5609348acp+4, and
  // a nearly halfway result that is not listed (0x1.013065f3561b2p+4).
  [TestCase(0x3fe03923f2b47c07L, 0x3ff219c1989e3373L)]
  [TestCase(unchecked((long)0xbfe03923f2b47c07UL), 0x3ff219c1989e3373L)]
  [TestCase(0x3ffe9cc7ed2e1a7eL, 0x400bb0ff220d8eb5L)]
  [TestCase(unchecked((long)0xbffe9cc7ed2e1a7eUL), 0x400bb0ff220d8eb5L)]
  [TestCase(0x403743d5609348acL, 0x41f7a87a8bb7fa28L)]
  [TestCase(unchecked((long)0xc03743d5609348acUL), 0x41f7a87a8bb7fa28L)]
  [TestCase(0x403013065f3561b2L, 0x415241a8076917ceL)]
  [TestCase(unchecked((long)0xc03013065f3561b2UL), 0x415241a8076917ceL)]
  // Above 31.4 the accurate path keeps e^-|x| in doubles (0x1.03fdf3603656dp+5); above 36.7 only
  // e^|x| counts (0x1.303a829e5a25fp+5).
  [TestCase(0x40403fdf3603656dL, 0x42cd925ad6c258aaL)]
  [TestCase(unchecked((long)0xc0403fdf3603656dUL), 0x42cd925ad6c258aaL)]
  [TestCase(0x404303a829e5a25fL, 0x434d1d1e6e4e1460L)]
  [TestCase(unchecked((long)0xc04303a829e5a25fUL), 0x434d1d1e6e4e1460L)]
  public void Cosh_OfAWorstCase_IsCorrectlyRounded(long x, long expected)
    => AssertResult("Cosh", IrType.F64, x, expected);

  // The double-double polynomial near zero, whose middle part is a power of two moved down
  // (0x1.30842605350a2p-21) or up (0x1.880072ccb1051p-11), and the one argument it looks up
  // (0x1.ac343b179fec4p-3).
  [TestCase(0x3ea30842605350a2L, 0x3ea3084260534e64L)]
  [TestCase(unchecked((long)0xbea30842605350a2UL), unchecked((long)0xbea3084260534e64UL))]
  [TestCase(0x3f4880072ccb1051L, 0x3f488006e032a37bL)]
  [TestCase(unchecked((long)0xbf4880072ccb1051UL), unchecked((long)0xbf488006e032a37bUL))]
  [TestCase(0x3fcac343b179fec4L, 0x3fca612499c53078L)]
  [TestCase(unchecked((long)0xbfcac343b179fec4UL), unchecked((long)0xbfca612499c53078UL))]
  // A listed exception (0x1.1005ec0bccabbp-1), a nearly halfway result that is not listed
  // (0x1.036c04e3850f6p+1), and the accurate path above 3.683 (0x1.00a0f3cf999acp+2).
  [TestCase(0x3fe1005ec0bccabbL, 0x3fdf20b1c8557decL)]
  [TestCase(unchecked((long)0xbfe1005ec0bccabbUL), unchecked((long)0xbfdf20b1c8557decUL))]
  [TestCase(0x400036c04e3850f6L, 0x3feee865012f831bL)]
  [TestCase(unchecked((long)0xc00036c04e3850f6UL), unchecked((long)0xbfeee865012f831bUL))]
  [TestCase(0x40100a0f3cf999acL, 0x3feffa9ccbda578aL)]
  [TestCase(unchecked((long)0xc0100a0f3cf999acUL), unchecked((long)0xbfeffa9ccbda578aUL))]
  public void Tanh_OfAWorstCase_IsCorrectlyRounded(long x, long expected)
    => AssertResult("Tanh", IrType.F64, x, expected);

  // The one argument sinhf.c resolves by name (0x1.250bfep-11), and one that needs the accurate
  // polynomial (0x1.000018p-3).
  [TestCase(0x3a1285ffL, 0x3a1285ffL)]
  [TestCase(0xba1285ffL, 0xba1285ffL)]
  [TestCase(0x3e00000cL, 0x3e005573L)]
  [TestCase(0xbe00000cL, 0xbe005573L)]
  public void SinhF_OfAWorstCase_IsCorrectlyRounded(long x, long expected)
    => AssertResult("Sinh", IrType.F32, x, expected);

  // An argument that needs the accurate polynomial (0x1.000b08p-3).
  [TestCase(0x3e000584L, 0x3f81006bL)]
  [TestCase(0xbe000584L, 0x3f81006bL)]
  public void CoshF_OfAWorstCase_IsCorrectlyRounded(long x, long expected)
    => AssertResult("Cosh", IrType.F32, x, expected);

  private static void AssertResult(string method, IrType format, long x, long expected) {
    Assert.That(FloatImplementations.Compiled(method, format, [x]), Is.EqualTo(expected), "compiled");
    Assert.That(FloatImplementations.Interpreted(method, format, [x]), Is.EqualTo(expected), "interpreted");
  }

}
