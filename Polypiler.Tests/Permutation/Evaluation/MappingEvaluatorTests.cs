using System;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Permutation.Evaluation;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Semantics;

namespace Polypiler.Tests.Permutation.Evaluation;

/// <summary>
/// The canonical mapping evaluator against hand computations and closed-form facts: every entry kind,
/// every overload, the input contract, the condition check and the truth-table and basis helpers.
/// </summary>
[TestFixture]
public sealed class MappingEvaluatorTests {

  private const string Mixed = "8:[0,0,~1,=1,?,7,6,5]";

  private static BigInteger Bit(int index) => BigInteger.One << index;

  private static BigInteger AllOnes(int width) => (BigInteger.One << width) - 1;

  /// <summary>Test design 1, 2 and 4, and the outline of <c>perm-eval.feature</c>.</summary>
  [TestCase("8:[7,6,5,4,3,2,1,0]", 0xB4, 0x2D)]
  [TestCase("8:[4,5,6,7,0,1,2,3]", 0xB4, 0x4B)]
  [TestCase("8:[5,6,7,0,1,2,3,4]", 0x81, 0x0C)]
  [TestCase(Mixed, 0xB4, 0xAC)]
  [TestCase(Mixed, 0xFF, 0xEB)]
  [TestCase(Mixed, 0x00, 0x0C)]
  [TestCase("8:[0,2,4,6]", 0x55, 0xF)]
  [TestCase("2:[0,1,0,1]", 0x2, 0xA)]
  public void Evaluate_GivesTheHandComputedResult(string text, int input, int output) {
    var mapping = BitMapping.Parse(text);
    Assert.Multiple(() => {
      Assert.That(MappingEvaluator.Evaluate(mapping, new BigInteger(input)), Is.EqualTo(new BigInteger(output)));
      Assert.That(MappingEvaluator.Evaluate(mapping, (ulong)input), Is.EqualTo((ulong)output));
    });
  }

  /// <summary>Each entry kind alone: plain, inverted, the two constants and the don't-care, which is 0.</summary>
  [TestCase("1:[0]", 0, 0)]
  [TestCase("1:[0]", 1, 1)]
  [TestCase("1:[~0]", 0, 1)]
  [TestCase("1:[~0]", 1, 0)]
  [TestCase("1:[=0]", 1, 0)]
  [TestCase("1:[=1]", 0, 1)]
  [TestCase("1:[?]", 0, 0)]
  [TestCase("1:[?]", 1, 0)]
  public void Evaluate_EachEntryKind(string text, int input, int output)
    => Assert.That(MappingEvaluator.Evaluate(BitMapping.Parse(text), new BigInteger(input)), Is.EqualTo(new BigInteger(output)));

  /// <summary>Test design 7: input 0 and all ones under reverse-8.</summary>
  [TestCase(0x00, 0x00)]
  [TestCase(0xFF, 0xFF)]
  public void Reverse8_OnZeroAndAllOnes(int input, int output)
    => Assert.That(MappingEvaluator.Evaluate(BitMapping.Reverse(8), new BigInteger(input)), Is.EqualTo(new BigInteger(output)));

  /// <summary>Test design 6: reversal of bit 0 sets bit <c>w - 1</c>, on every overload that takes the width.</summary>
  [TestCase(1)]
  [TestCase(63)]
  [TestCase(64)]
  [TestCase(65)]
  [TestCase(4096)]
  public void Reverse_OfBitZero_SetsTheTopBit(int width) {
    var mapping = BitMapping.Reverse(width);
    Assert.That(MappingEvaluator.Evaluate(mapping, BigInteger.One), Is.EqualTo(Bit(width - 1)));

    var limbs = (width + 63) / 64;
    var input = new ulong[limbs];
    input[0] = 1;
    var output = new ulong[limbs];
    MappingEvaluator.Evaluate(mapping, input, output);
    Assert.That(FromLimbs(output), Is.EqualTo(Bit(width - 1)));

    if (width <= 64)
      Assert.That(MappingEvaluator.Evaluate(mapping, 1UL), Is.EqualTo(1UL << (width - 1)));
  }

  /// <summary>Test design 6: the <see langword="ulong"/> overload refuses a width of 65, on either side.</summary>
  [TestCase("65:[0]")]
  [TestCase("1:[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]")]
  public void UlongOverload_RefusesAWidthAbove64(string text) {
    var mapping = BitMapping.Parse(text);
    var exception = Assert.Throws<ArgumentException>(() => MappingEvaluator.Evaluate(mapping, 0UL));
    Assert.That(exception!.Message, Does.StartWith("the ulong overload needs widths of at most 64 bits"));
  }

  /// <summary>The ulong overload at the 64-bit boundary and its input contract.</summary>
  [Test]
  public void UlongOverload_At64Bits_AndItsInputContract() {
    Assert.That(MappingEvaluator.Evaluate(BitMapping.Identity(64), ulong.MaxValue), Is.EqualTo(ulong.MaxValue));
    Assert.That(MappingEvaluator.Evaluate(BitMapping.Reverse(64), 1UL << 63), Is.EqualTo(1UL));
    var exception = Assert.Throws<ArgumentOutOfRangeException>(() => MappingEvaluator.Evaluate(BitMapping.Reverse(8), 0x100UL));
    Assert.That(exception!.Message, Is.EqualTo(new ArgumentOutOfRangeException("input", "input 0x100 does not fit 8 bits").Message));
  }

  /// <summary>Test design 8: <c>2^w - 1</c> is accepted, <c>2^w</c> and negative inputs are refused.</summary>
  [Test]
  public void BigIntegerOverload_InputContract() {
    var mapping = BitMapping.Reverse(8);
    Assert.That(MappingEvaluator.Evaluate(mapping, new BigInteger(0xFF)), Is.EqualTo(new BigInteger(0xFF)));
    Assert.Multiple(() => {
      var tooWide = Assert.Throws<ArgumentOutOfRangeException>(() => MappingEvaluator.Evaluate(mapping, new BigInteger(0x100)));
      Assert.That(tooWide!.Message, Is.EqualTo(new ArgumentOutOfRangeException("input", "input 0x100 does not fit 8 bits").Message));
      Assert.That(tooWide.ParamName, Is.EqualTo("input"));
      var negative = Assert.Throws<ArgumentOutOfRangeException>(() => MappingEvaluator.Evaluate(mapping, BigInteger.MinusOne));
      Assert.That(negative!.Message, Does.StartWith("input -0x1 does not fit 8 bits"));
    });
  }

  [Test]
  public void NullMapping_IsRefused() {
    Assert.Multiple(() => {
      Assert.Throws<ArgumentNullException>(() => MappingEvaluator.Evaluate(null!, BigInteger.Zero));
      Assert.Throws<ArgumentNullException>(() => MappingEvaluator.Evaluate(null!, 0UL));
      Assert.Throws<ArgumentNullException>(() => MappingEvaluator.Evaluate(null!, new ulong[1], new ulong[1]));
      Assert.Throws<ArgumentNullException>(() => MappingEvaluator.EvaluateChecked(null!, BigInteger.Zero));
      Assert.Throws<ArgumentNullException>(() => MappingEvaluator.AgreesOnRequired(null!, BigInteger.Zero, BigInteger.Zero));
      Assert.Throws<ArgumentNullException>(() => MappingEvaluator.TruthTable(null!));
      Assert.Throws<ArgumentNullException>(() => MappingEvaluator.BasisImage(null!));
    });
  }

  /// <summary>The limb overload over a narrowing mapping whose output fills less than its limb.</summary>
  [Test]
  public void LimbOverload_ClearsTheUnusedHighBitsOfTheLastLimb() {
    var mapping = BitMapping.Parse("100:[99,~0,=1]");
    var input = new ulong[2];
    input[1] = 1UL << 35;
    ulong[] output = [ulong.MaxValue];
    MappingEvaluator.Evaluate(mapping, input, output);
    Assert.That(output[0], Is.EqualTo(0b111UL));
  }

  /// <summary>Test design 11: a wrong limb count on either side and a stray bit above the width.</summary>
  [Test]
  public void LimbOverload_RefusesWrongShapes() {
    var mapping = BitMapping.Identity(100);
    Assert.Multiple(() => {
      var shortInput = Assert.Throws<ArgumentException>(() => MappingEvaluator.Evaluate(mapping, new ulong[1], new ulong[2]));
      Assert.That(shortInput!.Message, Does.StartWith("a width of 100 bits needs 2 input limbs, got 1"));
      Assert.That(shortInput.ParamName, Is.EqualTo("input"));
      var longOutput = Assert.Throws<ArgumentException>(() => MappingEvaluator.Evaluate(mapping, new ulong[2], new ulong[3]));
      Assert.That(longOutput!.Message, Does.StartWith("a width of 100 bits needs 2 output limbs, got 3"));
      Assert.That(longOutput.ParamName, Is.EqualTo("output"));

      ulong[] stray = [0, 1UL << 36];
      var strayBit = Assert.Throws<ArgumentException>(() => MappingEvaluator.Evaluate(mapping, stray, new ulong[2]));
      Assert.That(strayBit!.Message, Does.StartWith("input has bits set at or above its width of 100 bits"));
    });

    // Bit 99 is the last one that fits.
    ulong[] top = [0, 1UL << 35];
    var output = new ulong[2];
    MappingEvaluator.Evaluate(mapping, top, output);
    Assert.That(output, Is.EqualTo(top));
  }

  /// <summary>A limb-multiple width has no unused bits, so every bit of the last limb is input.</summary>
  [Test]
  public void LimbOverload_AtAWholeNumberOfLimbs() {
    ulong[] input = [0, ulong.MaxValue];
    var output = new ulong[2];
    MappingEvaluator.Evaluate(BitMapping.Identity(128), input, output);
    Assert.That(output, Is.EqualTo(input));
  }

  [Test]
  public void LimbOverload_RefusesOverlappingSpans() {
    var buffer = new ulong[2];
    var exception = Assert.Throws<ArgumentException>(() => MappingEvaluator.Evaluate(BitMapping.Identity(64), buffer.AsSpan(0, 1), buffer.AsSpan(0, 1)));
    Assert.That(exception!.Message, Does.StartWith("input and output overlap"));
  }

  /// <summary>
  /// Test design 3 and the measurement "Overload agreement": 10 000 seeded random mappings of widths
  /// 1 to 64 with every entry kind; the three overloads agree on every input.
  /// </summary>
  [Test]
  public void Overloads_AgreeOnSeededRandomMappings() {
    var random = new Random(806);
    var differences = 0;
    for (var test = 0; test < 10_000; ++test) {
      var inputWidth = random.Next(1, 65);
      var outputWidth = random.Next(1, 65);
      var outputs = new BitSource[outputWidth];
      for (var bit = 0; bit < outputWidth; ++bit)
        outputs[bit] = random.Next(5) switch {
          0 => BitSource.Zero,
          1 => BitSource.One,
          2 => BitSource.DontCare,
          3 => BitSource.Not(random.Next(inputWidth)),
          _ => BitSource.From(random.Next(inputWidth))
        };
      var mapping = BitMapping.Create(inputWidth, outputs);
      var input = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 63);
      if (inputWidth < 64)
        input &= (1UL << inputWidth) - 1;

      var big = MappingEvaluator.Evaluate(mapping, new BigInteger(input));
      var word = MappingEvaluator.Evaluate(mapping, input);
      var limb = new ulong[1];
      MappingEvaluator.Evaluate(mapping, [input], limb);
      if (big != new BigInteger(word) || word != limb[0])
        ++differences;
    }
    Assert.That(differences, Is.Zero);
  }

  /// <summary>
  /// Closed-form facts above BitPerm's 128 bits: reversal of <c>2^k</c> is <c>2^(w-1-k)</c>, a
  /// bijection keeps the population count, identity returns its input.
  /// </summary>
  [Test]
  public void WideWidths_HoldTheClosedFormFacts() {
    const int width = 4096;
    var random = new Random(4096);
    var reverse = BitMapping.Reverse(width);
    var identity = BitMapping.Identity(width);
    var rotation = BitMapping.Rotation(width, 1234);
    foreach (var k in (int[])[0, 1, 63, 64, 2047, 4095])
      Assert.That(MappingEvaluator.Evaluate(reverse, Bit(k)), Is.EqualTo(Bit(width - 1 - k)));

    for (var test = 0; test < 8; ++test) {
      var bytes = new byte[width / 8];
      random.NextBytes(bytes);
      var input = new BigInteger(bytes, isUnsigned: true);
      Assert.That(MappingEvaluator.Evaluate(identity, input), Is.EqualTo(input));
      Assert.That(PopCount(MappingEvaluator.Evaluate(rotation, input)), Is.EqualTo(PopCount(input)));
      Assert.That(PopCount(MappingEvaluator.Evaluate(reverse, input)), Is.EqualTo(PopCount(input)));
    }
    Assert.That(MappingEvaluator.Evaluate(identity, AllOnes(width)), Is.EqualTo(AllOnes(width)));
  }

  /// <summary>Test design 10 and the scenario of <c>perm-eval.feature</c>: an input outside the domain.</summary>
  [Test]
  public void EvaluateChecked_RefusesAnInputOutsideTheDomain() {
    var mapping = BitMapping.Parse("8:[0,1,2,3,=0,=0,=0,=0]");
    var routing = BitRouting.From(mapping, new BitCondition(new BigInteger(0xF0), BigInteger.Zero, mapping.RequiredOutputMask));
    var exception = Assert.Throws<PermutationModelException>(() => MappingEvaluator.EvaluateChecked(routing, new BigInteger(0x1F)));
    Assert.That(exception!.Message, Is.EqualTo("PLY0806: input 0x1F is outside the declared input domain"));
    Assert.That(exception.Diagnostic.Code, Is.EqualTo("PLY0806"));
  }

  /// <summary>Inside the domain, including a known-one bit, the result is that of <see cref="MappingEvaluator.Evaluate(BitMapping, BigInteger)"/>.</summary>
  [Test]
  public void EvaluateChecked_InsideTheDomain_Evaluates() {
    var mapping = BitMapping.Parse("8:[0,1,2,3,=0,=0,=0,=0]");
    var routing = BitRouting.From(mapping, new BitCondition(new BigInteger(0xF0), new BigInteger(0x01), mapping.RequiredOutputMask));
    Assert.That(MappingEvaluator.EvaluateChecked(routing, new BigInteger(0x0B)), Is.EqualTo(new BigInteger(0x0B)));
    var missingOne = Assert.Throws<PermutationModelException>(() => MappingEvaluator.EvaluateChecked(routing, new BigInteger(0x0A)));
    Assert.That(missingOne!.Message, Is.EqualTo("PLY0806: input 0xA is outside the declared input domain"));
  }

  /// <summary>The input contract comes before the condition: a too-wide input is an argument error, not a domain error.</summary>
  [Test]
  public void EvaluateChecked_ChecksTheWidthFirst()
    => Assert.Throws<ArgumentOutOfRangeException>(() => MappingEvaluator.EvaluateChecked(BitRouting.From(BitMapping.Reverse(8)), new BigInteger(0x100)));

  [Test]
  public void AgreesOnRequired_ComparesTheRequiredBitsOnly() {
    var mapping = BitMapping.Parse(Mixed);
    var input = new BigInteger(0xB4);
    Assert.Multiple(() => {
      Assert.That(MappingEvaluator.AgreesOnRequired(mapping, input, new BigInteger(0xAC)), Is.True);
      // Bit 4 is the don't-care: setting it changes nothing.
      Assert.That(MappingEvaluator.AgreesOnRequired(mapping, input, new BigInteger(0xBC)), Is.True);
      Assert.That(MappingEvaluator.AgreesOnRequired(mapping, input, new BigInteger(0xA8)), Is.False);
      Assert.That(MappingEvaluator.AgreesOnRequired(BitMapping.Parse("8:[?,?]"), input, new BigInteger(3)), Is.True);
    });
  }

  [Test]
  public void AgreesOnRequired_RefusesACandidateWiderThanTheOutput() {
    var mapping = BitMapping.Parse("8:[0,1]");
    Assert.That(MappingEvaluator.AgreesOnRequired(mapping, BigInteger.Zero, new BigInteger(3)), Is.False);
    var exception = Assert.Throws<ArgumentOutOfRangeException>(() => MappingEvaluator.AgreesOnRequired(mapping, BigInteger.Zero, new BigInteger(4)));
    Assert.That(exception!.Message, Does.StartWith("candidate output 0x4 does not fit 2 bits"));
    Assert.Throws<ArgumentOutOfRangeException>(() => MappingEvaluator.AgreesOnRequired(mapping, BigInteger.Zero, BigInteger.MinusOne));
    Assert.Throws<ArgumentOutOfRangeException>(() => MappingEvaluator.AgreesOnRequired(mapping, new BigInteger(0x100), BigInteger.Zero));
  }

  /// <summary>Test design 9: the truth table at 16 inputs, and <c>PLY0807</c> at 17.</summary>
  [Test]
  public void TruthTable_At16Inputs_And17() {
    var table = MappingEvaluator.TruthTable(BitMapping.Identity(16));
    Assert.That(table, Has.Length.EqualTo(65_536));
    Assert.That(table.Select((value, index) => value == index).All(same => same), Is.True);

    var exception = Assert.Throws<PermutationModelException>(() => MappingEvaluator.TruthTable(BitMapping.Identity(17)));
    Assert.That(exception!.Message, Is.EqualTo("PLY0807: truth table of 17 inputs exceeds the limit of 16"));
  }

  [Test]
  public void TruthTable_OfASmallMapping() {
    Assert.That(MappingEvaluator.TruthTable(BitMapping.Parse("2:[1,~0,=1,?]")), Is.EqualTo(new BigInteger[] { 0b0110, 0b0100, 0b0111, 0b0101 }));
    Assert.That(MappingEvaluator.TruthTable(BitMapping.Parse("1:[=1]")), Is.EqualTo(new BigInteger[] { 1, 1 }));
  }

  /// <summary>The basis image: <c>Evaluate(2^j) XOR Evaluate(0)</c>, so constants and inversions cancel.</summary>
  [Test]
  public void BasisImage_IsTheChangeEachInputBitCauses() {
    Assert.That(MappingEvaluator.BasisImage(BitMapping.Parse("3:[2,~0,=1,?,0]")), Is.EqualTo(new BigInteger[] { 0b10010, 0, 0b00001 }));
    Assert.That(MappingEvaluator.BasisImage(BitMapping.Reverse(1024)).Select((image, j) => image == Bit(1023 - j)).All(same => same), Is.True);
  }

  /// <summary>The result never has a bit at or above the output width.</summary>
  [Test]
  public void Result_StaysInsideTheOutputWidth() {
    var mapping = BitMapping.Parse("8:[=1,=1,=1]");
    Assert.That(MappingEvaluator.Evaluate(mapping, new BigInteger(0xFF)), Is.EqualTo(new BigInteger(7)));
  }

  private static BigInteger FromLimbs(ulong[] limbs) {
    var value = BigInteger.Zero;
    for (var limb = limbs.Length - 1; limb >= 0; --limb)
      value = (value << 64) | limbs[limb];
    return value;
  }

  private static int PopCount(BigInteger value) {
    var count = 0;
    foreach (var b in value.ToByteArray(isUnsigned: true))
      count += BitOperations.PopCount(b);
    return count;
  }

}
