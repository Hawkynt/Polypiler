using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Permutation.Model;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tests.Permutation.Model;

/// <summary>
/// The 44 mappings exported from the pinned BitPerm page (<c>tests/fixtures/bitperm/mappings</c>) in
/// the model: each loads, its route map equals the one the page's own derivation gives under
/// <c>node</c>, and the text and document forms read it back unchanged. The page's route maps are
/// captured in <c>tests/fixtures/permutation/bitperm-routemaps.json</c> by
/// <c>tooling/bitperm/routemap.mjs</c>; the scenario "Every exported BitPerm mapping loads and matches
/// BitPerm's route map" of <c>perm-model.feature</c> runs that derivation live.
/// </summary>
[TestFixture]
public sealed class BitMappingFixtureTests {

  private static readonly string _mappings = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "bitperm", "mappings");
  private static readonly string _routeMaps = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "permutation", "bitperm-routemaps.json");

  private static readonly Lazy<Dictionary<string, int[]>> _captured = new(() => {
    using var document = JsonDocument.Parse(File.ReadAllText(_routeMaps));
    return document.RootElement.GetProperty("routeMaps").EnumerateObject()
      .ToDictionary(map => map.Name, map => map.Value.EnumerateArray().Select(index => index.GetInt32()).ToArray(), StringComparer.Ordinal);
  });

  /// <summary>The fixture names, in ordinal order.</summary>
  public static IEnumerable<string> Names()
    => Directory.GetFiles(_mappings, "*.json").Select(path => Path.GetFileNameWithoutExtension(path)!).Order(StringComparer.Ordinal);

  private static BitMapping Load(string name) {
    using var stream = File.OpenRead(Path.Combine(_mappings, name + ".json"));
    return BitPermState.Read(stream);
  }

  [Test]
  public void ThereAre44Fixtures_AndARouteMapForEach() {
    Assert.That(Names().Count(), Is.EqualTo(44));
    Assert.That(_captured.Value.Keys.Order(StringComparer.Ordinal), Is.EqualTo(Names()));
  }

  /// <summary>Gherkin "Every exported BitPerm mapping loads and matches BitPerm's route map", against the captured route maps.</summary>
  [TestCaseSource(nameof(Names))]
  public void Fixture_LoadsAndMatchesBitPermsRouteMap(string name) {
    var mapping = Load(name);
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_mappings, name + ".json")));
    var width = document.RootElement.GetProperty("width").GetInt32();
    Assert.That((mapping.InputWidth, mapping.OutputWidth), Is.EqualTo((width, width)));
    Assert.That(mapping.ToRouteMap(), Is.EqualTo(_captured.Value[name]));
  }

  /// <summary>Test design 6: the text and the document form read every fixture back unchanged.</summary>
  [TestCaseSource(nameof(Names))]
  public void Fixture_RoundTripsThroughTextAndDocument(string name) {
    var mapping = Load(name);
    Assert.That(BitMapping.Parse(mapping.ToString()), Is.EqualTo(mapping));
    using var stream = new MemoryStream();
    BitMappingDocument.Write(mapping, stream);
    stream.Position = 0;
    Assert.That(BitMappingDocument.Read(stream), Is.EqualTo(mapping));
  }

  /// <summary>Test design 2: the factories give what BitPerm's quick patterns give.</summary>
  [TestCase(8)]
  [TestCase(16)]
  [TestCase(32)]
  [TestCase(64)]
  [TestCase(128)]
  public void Factories_EqualBitPermsPatterns(int width) {
    Assert.Multiple(() => {
      Assert.That(Load($"pattern-identity-{width}"), Is.EqualTo(BitMapping.Identity(width)));
      Assert.That(Load($"pattern-reverse-{width}"), Is.EqualTo(BitMapping.Reverse(width)));
      Assert.That(Load($"pattern-swap_halves-{width}"), Is.EqualTo(BitMapping.SwapHalves(width)));
      Assert.That(Load($"pattern-set_all_0-{width}"), Is.EqualTo(BitMapping.Constant(width, BigInteger.Zero)));
      Assert.That(Load($"pattern-clear-{width}"), Is.EqualTo(BitMapping.FromRouteMap([.. Enumerable.Repeat(-1, width)])));
    });
  }

  /// <summary>Test design 4: a mapping of nothing but constant zeros.</summary>
  [Test]
  public void SetAllZero_IsConstantsOnly() {
    var mapping = Load("pattern-set_all_0-8");
    Assert.Multiple(() => {
      Assert.That(mapping.ZerosMask, Is.EqualTo(new BigInteger(0xFF)));
      Assert.That(mapping.RequiredOutputMask, Is.EqualTo(new BigInteger(0xFF)));
      Assert.That(mapping.UsedInputMask, Is.EqualTo(BigInteger.Zero));
      Assert.That(mapping.DroppedInputMask, Is.EqualTo(new BigInteger(0xFF)));
      Assert.That(mapping.IsBijective, Is.False);
      Assert.That(mapping.ToRouteMap(), Is.All.EqualTo(-1));
      // The route map loses the constants: back from it they are don't-cares, never zeros.
      Assert.That(BitMapping.FromRouteMap(mapping.ToRouteMap()).HasConstants, Is.False);
    });
  }

  [Test]
  public void Clear_IsDontCaresOnly() {
    var mapping = Load("pattern-clear-8");
    Assert.That((mapping.HasDontCares, mapping.RequiredOutputMask, mapping.UsedInputMask), Is.EqualTo((true, BigInteger.Zero, BigInteger.Zero)));
  }

  [Test]
  public void Duplication_FromThePagesTest() {
    // Test 33 of the page: input bit 0 goes to outputs 0, 2 and 4.
    var mapping = Load("test-33-duplicate-same-source-to-multiple-destinations");
    Assert.That((mapping.UseCount(0), mapping.HasDuplication, mapping.IsInjective), Is.EqualTo((3, true, false)));
    Assert.That(mapping.ToString(), Is.EqualTo("8:[0,?,0,?,0,?,?,?]"));
  }

  /// <summary>The mapping document of <c>spec/permutations/</c> is the form <see cref="BitMappingDocument"/> reads.</summary>
  [Test]
  public void SpecMappingDocument_ReadsAsTheModel() {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "permutations", "mappings", "reverse8.yaml");
    var body = SpecDocument.Parse("permutations/mappings/reverse8.yaml", File.ReadAllText(path)).Body!;
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body.ToJsonString()));
    var mapping = BitMappingDocument.Read(stream, "reverse8.yaml");
    Assert.That(mapping, Is.EqualTo(BitMapping.Reverse(8)));
    Assert.That(mapping.IsBijective, Is.EqualTo((bool)body["bijective"]!));
  }

}
