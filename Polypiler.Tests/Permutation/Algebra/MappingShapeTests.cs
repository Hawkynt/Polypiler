using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using NUnit.Framework;
using Polypiler.Permutation.Algebra;
using Polypiler.Permutation.Model;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Permutation.Algebra;

/// <summary>
/// The normal form: a positive and a negative case for each of the nine shapes, the order in which
/// they are tried, the boundaries of the width conditions, and the shapes of the 44 BitPerm fixtures.
/// </summary>
[TestFixture]
public sealed class MappingShapeTests {

  private static string Describe(string text) => MappingShape.Describe(BitMapping.Parse(text)).ToString();

  private static string List(int width, Func<int, string> entry) => $"{width}:[{string.Join(",", Enumerable.Range(0, width).Select(entry))}]";

  /// <summary>Test design 6, the outline of <c>perm-algebra.feature</c>: one example per shape.</summary>
  [TestCase("8:[0,1,2,3,4,5,6,7]", "Identity")]
  [TestCase("8:[4,5,6,7,0,1,2,3]", "Rotation(4)")]
  [TestCase("8:[7,6,5,4,3,2,1,0]", "ReverseBits")]
  [TestCase("16:[8,9,10,11,12,13,14,15,0,1,2,3,4,5,6,7]", "Rotation(8)")]
  [TestCase("16:[7,6,5,4,3,2,1,0,15,14,13,12,11,10,9,8]", "ReverseBitsInBytes")]
  [TestCase("8:[1,3,5,7,=0,=0,=0,=0]", "Compress(0xAA)")]
  [TestCase("8:[0,=0,1,=0,2,=0,3,=0]", "Expand(0x55)")]
  [TestCase("8:[1,0,3,2,5,4,7,6]", "Permutation")]
  [TestCase("8:[0,0,2,3,4,5,6,7]", "Routing")]
  public void Describe_NamesEachShape(string text, string shape) => Assert.That(Describe(text), Is.EqualTo(shape));

  /// <summary>Test design 8 and 9: width 1 is the identity; the smallest reversal is a rotation by 1; 3 bits reverse.</summary>
  [TestCase("1:[0]", "Identity")]
  [TestCase("2:[1,0]", "Rotation(1)")]
  [TestCase("3:[2,1,0]", "ReverseBits")]
  [TestCase("3:[1,2,0]", "Rotation(2)")]
  [TestCase("3:[2,0,1]", "Rotation(1)")]
  [TestCase("3:[0,2,1]", "Permutation")]
  public void Describe_SmallWidths(string text, string shape) => Assert.That(Describe(text), Is.EqualTo(shape));

  /// <summary>Test design 9: a 24-bit and a 32-bit byte reversal are <c>ReverseBytes</c>; 16-bit is a rotation (test design 7).</summary>
  [TestCase(16, "Rotation(8)")]
  [TestCase(24, "ReverseBytes")]
  [TestCase(32, "ReverseBytes")]
  [TestCase(128, "ReverseBytes")]
  public void Describe_ByteReversal(int width, string shape)
    => Assert.That(Describe(List(width, bit => (8 * (width / 8 - 1 - bit / 8) + bit % 8).ToString(System.Globalization.CultureInfo.InvariantCulture))), Is.EqualTo(shape));

  /// <summary>Reversing the bits inside each byte needs at least two bytes; at 8 bits it is the plain reversal.</summary>
  [TestCase(8, "ReverseBits")]
  [TestCase(24, "ReverseBitsInBytes")]
  [TestCase(64, "ReverseBitsInBytes")]
  public void Describe_ReverseBitsInBytes(int width, string shape)
    => Assert.That(Describe(List(width, bit => (8 * (bit / 8) + 7 - bit % 8).ToString(System.Globalization.CultureInfo.InvariantCulture))), Is.EqualTo(shape));

  /// <summary>Swapping the halves is a rotation by half the width at every even width.</summary>
  [TestCase(2)]
  [TestCase(8)]
  [TestCase(64)]
  [TestCase(4096)]
  public void Describe_SwapHalves_IsRotationByHalf(int width)
    => Assert.That(MappingShape.Describe(BitMapping.SwapHalves(width)).ToString(), Is.EqualTo($"Rotation({width / 2})"));

  /// <summary>Every rotation amount strictly inside the width is named by itself.</summary>
  [Test]
  public void Describe_EveryRotationAmount() {
    for (var amount = 1; amount < 16; ++amount)
      Assert.That(MappingShape.Describe(BitMapping.Rotation(16, amount)), Is.EqualTo(new MappingShape.Rotation(amount)), $"amount {amount}");
  }

  /// <summary>Negative cases: near misses of each shape fall to the next one that applies.</summary>
  [TestCase("8:[0,1,2,3,4,5,7,6]", "Permutation", Description = "identity but one swap")]
  [TestCase("8:[0,1,2,3]", "Routing", Description = "identity list, not square")]
  [TestCase("8:[4,5,6,7,0,1,3,2]", "Permutation", Description = "rotation but one swap")]
  [TestCase("24:[16,17,18,19,20,21,22,23,8,9,10,11,12,13,14,15,1,0,2,3,4,5,6,7]", "Permutation", Description = "byte reversal but one swap")]
  [TestCase("8:[7,6,5,4,3,2,1,~0]", "Routing", Description = "reversal with an inverted bit")]
  [TestCase("16:[7,6,5,4,3,2,1,0,15,14,13,12,11,10,8,9]", "Permutation", Description = "bits in bytes but one swap")]
  [TestCase("8:[0,1,2,3,=0,=0,=0,=0]", "Routing", Description = "test design 10: a contiguous low mask is an And")]
  [TestCase("8:[3,1,5,7,=0,=0,=0,=0]", "Routing", Description = "compress inputs not ascending")]
  [TestCase("8:[1,3,=0,5,=0,=0,=0,=0]", "Routing", Description = "compress outputs not contiguous")]
  [TestCase("8:[1,3,5,7,=0,=0,=0,?]", "Routing", Description = "compress with a don't-care")]
  [TestCase("8:[1,3,5,7,=0,=0,=0,=1]", "Routing", Description = "compress with a one")]
  [TestCase("8:[1,3,~5,7,=0,=0,=0,=0]", "Routing", Description = "compress with an inverted bit")]
  [TestCase("8:[1,=0,0,=0,2,=0,3,=0]", "Routing", Description = "expand inputs out of order")]
  [TestCase("8:[0,=0,1,=0,2,=0,3,=1]", "Routing", Description = "expand with a one")]
  [TestCase("8:[=0,=0,=0,=0,=0,=0,=0,=0]", "Routing", Description = "all zero is neither compress nor expand")]
  [TestCase("8:[0,2,4,6]", "Routing", Description = "not square")]
  public void Describe_NearMisses(string text, string shape) => Assert.That(Describe(text), Is.EqualTo(shape));

  /// <summary>Compress and expand with a single bit, and over the full width minus one.</summary>
  [TestCase("4:[3,=0,=0,=0]", "Compress(0x8)")]
  [TestCase("4:[=0,=0,=0,0]", "Expand(0x8)")]
  [TestCase("4:[1,2,3,=0]", "Compress(0xE)")]
  [TestCase("4:[=0,0,1,2]", "Expand(0xE)")]
  public void Describe_CompressAndExpandBoundaries(string text, string shape) => Assert.That(Describe(text), Is.EqualTo(shape));

  /// <summary>Shapes compare by value and carry their parameter.</summary>
  [Test]
  public void Shapes_AreValues() {
    var compress = (MappingShape.Compress)MappingShape.Describe(BitMapping.Parse("8:[1,3,5,7,=0,=0,=0,=0]"));
    Assert.Multiple(() => {
      Assert.That(compress.Mask, Is.EqualTo(new BigInteger(0xAA)));
      Assert.That(compress, Is.EqualTo(new MappingShape.Compress(new BigInteger(0xAA))));
      Assert.That(MappingShape.Describe(BitMapping.Identity(4)), Is.EqualTo(new MappingShape.Identity()));
      Assert.That(new MappingShape.Expand(BigInteger.One).ToString(), Is.EqualTo("Expand(0x1)"));
      Assert.That((MappingShape)new MappingShape.ReverseBytes(), Is.Not.EqualTo((MappingShape)new MappingShape.ReverseBits()));
      Assert.That(() => MappingShape.Describe(null!), Throws.ArgumentNullException);
    });
  }

  /// <summary>
  /// The shape table of the 44 BitPerm fixtures: the five quick patterns have the shapes their names
  /// promise, and every fixture is named from the permutation family exactly when it is a bijection.
  /// </summary>
  [Test]
  public void BitPermFixtures_HaveTheShapesTheirNamesPromise() {
    var directory = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "bitperm", "mappings");
    var table = new StringBuilder();
    var files = Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal).ToArray();
    Assert.That(files, Has.Length.EqualTo(44));
    foreach (var path in files) {
      BitMapping mapping;
      using (var stream = File.OpenRead(path))
        mapping = BitPermState.Read(stream);

      var name = Path.GetFileNameWithoutExtension(path);
      var shape = MappingShape.Describe(mapping);
      table.AppendLine($"| {name} | {mapping.InputWidth} | {shape} |");
      Assert.That(shape is MappingShape.Compress or MappingShape.Expand or MappingShape.Routing, Is.EqualTo(!mapping.IsBijective), name);

      var width = mapping.InputWidth;
      var expected = name.StartsWith("pattern-", StringComparison.Ordinal) ? name[8..name.LastIndexOf('-')] switch {
        "identity" => "Identity",
        "reverse" => "ReverseBits",
        "swap_halves" => $"Rotation({width / 2})",
        _ => "Routing"
      } : null;
      if (expected is not null)
        Assert.That(shape.ToString(), Is.EqualTo(expected), name);
    }
    TestContext.Out.Write(table.ToString());
  }

}
