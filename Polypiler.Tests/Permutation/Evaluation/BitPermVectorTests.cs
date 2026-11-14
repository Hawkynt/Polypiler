using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Permutation.Evaluation;
using Polypiler.Permutation.Model;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Permutation.Evaluation;

/// <summary>
/// The canonical evaluator against BitPerm's raw evaluation: every vector the pinned page's evaluation
/// loop produced under <c>node</c> (<c>tests/fixtures/bitperm/vectors</c>) is reproduced on its mask,
/// for each of the 44 exported mappings (<c>tests/fixtures/bitperm/mappings</c>). The vectors are the
/// page's, never this evaluator's.
/// </summary>
[TestFixture]
public sealed class BitPermVectorTests {

  private static readonly string _directory = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "bitperm");

  /// <summary>The fixture names, in ordinal order.</summary>
  public static IEnumerable<string> Names()
    => Directory.GetFiles(Path.Combine(_directory, "mappings"), "*.json").Select(path => Path.GetFileNameWithoutExtension(path)!).Order(StringComparer.Ordinal);

  private static BitMapping Mapping(string name) {
    using var stream = File.OpenRead(Path.Combine(_directory, "mappings", name + ".json"));
    return BitPermState.Read(stream);
  }

  private static (BigInteger Input, BigInteger Output, BigInteger Mask)[] Vectors(string name) {
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "vectors", name + ".json")));
    Assert.That(document.RootElement.GetProperty("mapping").GetString(), Is.EqualTo(name));
    return [.. document.RootElement.GetProperty("vectors").EnumerateArray()
      .Select(vector => (Hex(vector.GetProperty("input")), Hex(vector.GetProperty("output")), Hex(vector.GetProperty("mask"))))];
  }

  private static BigInteger Hex(JsonElement value) {
    var text = value.GetString()!;
    Assert.That(text, Does.StartWith("0x"));
    return BigInteger.Parse("0" + text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
  }

  /// <summary>The vectors of one mapping that disagree with the evaluator on their mask.</summary>
  private static int Mismatches(string name, out int count) {
    var mapping = Mapping(name);
    var vectors = Vectors(name);
    count = vectors.Length;
    return vectors.Count(vector => !((MappingEvaluator.Evaluate(mapping, vector.Input) ^ vector.Output) & vector.Mask).IsZero);
  }

  /// <summary>Test design 5, per fixture: 0 mismatches on the mask, and the mask is the mapping's required outputs.</summary>
  [TestCaseSource(nameof(Names))]
  public void Fixture_IsReproducedOnItsMask(string name) {
    var mapping = Mapping(name);
    var vectors = Vectors(name);
    Assert.That(vectors, Is.Not.Empty);
    Assert.Multiple(() => {
      foreach (var (input, output, mask) in vectors) {
        Assert.That(mask, Is.EqualTo(mapping.RequiredOutputMask), $"mask of {name} at input 0x{input:X}");
        Assert.That(MappingEvaluator.AgreesOnRequired(mapping, input, output), Is.True, $"{name} at input 0x{input:X}");
      }
    });
  }

  /// <summary>The measurement "BitPerm vectors reproduced": over all 44 mappings, 0 mismatches.</summary>
  [Test]
  public void AllFixtures_HaveNoMismatches() {
    var (mappings, vectors, mismatches) = (0, 0, 0);
    foreach (var name in Names()) {
      mismatches += Mismatches(name, out var count);
      vectors += count;
      ++mappings;
    }
    TestContext.Out.WriteLine($"BitPerm vectors: {mappings} mappings, {vectors} vectors, {mismatches} mismatches");
    Assert.That((mappings, mismatches), Is.EqualTo((44, 0)));
    Assert.That(vectors, Is.GreaterThan(mappings));
  }

}
