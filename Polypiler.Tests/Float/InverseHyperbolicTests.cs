using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Float;

/// <summary>
/// The inverse hyperbolic functions on the arguments only their slow paths take, which the reference
/// vectors do not reach: hard cases from CORE-MATH's worst-case files <c>asinh.wc</c>, <c>acosh.wc</c>
/// and <c>atanh.wc</c>, and floats whose logarithm lies exactly on a rounding boundary of the fast
/// evaluation. The expected results are GNU MPFR's, rounded to nearest; the compiled implementation
/// and the one the interpreter runs from the imported source both have to give them.
/// </summary>
[TestFixture]
public sealed class InverseHyperbolicTests {

  // AsinhZero, where the middle part of its sum is a power of two.
  [TestCase("Asinh", 0x3fb96375c7eb0bf3L, 0x3fb958da4ed491f1L)]
  [TestCase("Asinh", unchecked((long)0xbfb96375c7eb0bf3UL), unchecked((long)0xbfb958da4ed491f1UL))]
  [TestCase("Asinh", 0x3ea24792e9bb0b7eL, 0x3ea24792e9bb0a7fL)]
  // AsinhRefine, where the middle part is a power of two.
  [TestCase("Asinh", 0x481bfedff028cd40L, 0x4056d726bd3662edL)]
  [TestCase("Asinh", 0x5f19d5ad03f41218L, 0x4075a5c0ce5fe0d7L)]
  // AsinhRefine, too close to round: in the database, and not.
  [TestCase("Asinh", 0x3fea16d9cc06011aL, 0x3fe7d3755d851062L)]
  [TestCase("Asinh", unchecked((long)0xbfea16d9cc06011aUL), unchecked((long)0xbfe7d3755d851062UL))]
  [TestCase("Asinh", 0x3fd00f9476450863L, 0x3fcfcb35067f343cL)]
  [TestCase("Asinh", 0x4f6e4c50ae3c0800L, 0x4065913f772f893cL)]
  [TestCase("Asinh", 0x46adf4aa00000c62L, 0x4052df2b7371ed29L)]
  // AcoshOne, near 1.
  [TestCase("Acosh", 0x3ff19bd09e0800a4L, 0x3fdc76ba721513d2L)]
  [TestCase("Acosh", 0x3ff0001cd7360138L, 0x3f7e6114b40aa763L)]
  [TestCase("Acosh", 0x3ff02926de3b8139L, 0x3fc2210dbb88be4eL)]
  // AcoshRefine, where the middle part is a power of two.
  [TestCase("Acosh", 0x5f19d5ad03f41218L, 0x4075a5c0ce5fe0d7L)]
  [TestCase("Acosh", 0x48425813392d8780L, 0x4057412e182bf0ddL)]
  // AcoshRefine, too close to round, not in the database.
  [TestCase("Acosh", 0x6658a0cdb8499400L, 0x407aab78151372a8L)]
  [TestCase("Acosh", 0x46adf4aa00000c62L, 0x4052df2b7371ed29L)]
  // AtanhZero, where the middle part is a power of two.
  [TestCase("Atanh", 0x3e5a6a58d55e307bL, 0x3e5a6a58d55e307dL)]
  [TestCase("Atanh", 0x3e6fd51bf2069fe0L, 0x3e6fd51bf2069feaL)]
  // AtanhRefine, too close to round, not in the database.
  [TestCase("Atanh", 0x3fd062f67c92b1f8L, 0x3fd0c26a67791952L)]
  [TestCase("Atanh", unchecked((long)0xbfd062f67c92b1f8UL), unchecked((long)0xbfd0c26a67791952UL))]
  public void Double_IsCorrectlyRounded_OnAHardCase(string method, long argument, long expected) {
    Assert.That(FloatImplementations.Compiled(method, IrType.F64, [argument]), Is.EqualTo(expected), "compiled");
    Assert.That(FloatImplementations.Interpreted(method, IrType.F64, [argument]), Is.EqualTo(expected), "interpreted");
  }

  // The accurate logarithm of float lands exactly on a boundary.
  [TestCase("Asinh", 0x4bdd65a5L, 0x418f034bL)]
  [TestCase("Asinh", 0x4c803f2cL, 0x4195bc36L)]
  [TestCase("Acosh", 0x4bdd65a5L, 0x418f034aL)]
  [TestCase("Acosh", 0x4f8ffb03L, 0x41b7ee9aL)]
  public void Float_IsCorrectlyRounded_OnAHardCase(string method, long argument, long expected) {
    Assert.That(FloatImplementations.Compiled(method, IrType.F32, [argument]), Is.EqualTo(expected), "compiled");
    Assert.That(FloatImplementations.Interpreted(method, IrType.F32, [argument]), Is.EqualTo(expected), "interpreted");
  }

}
