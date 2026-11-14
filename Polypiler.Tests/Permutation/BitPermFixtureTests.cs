using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Corpus;

namespace Polypiler.Tests.Permutation;

/// <summary>
/// The fixtures <c>tooling/bitperm/export.mjs</c> exported from the pinned BitPerm page
/// (<c>tests/fixtures/bitperm</c>): their counts, the pin against the corpus lock, sample values of the
/// page's tables, and the vectors, which are recomputed here from their mappings and checked by hand
/// for the reverse and swap-halves patterns. The exporter and its drift check run in
/// <c>tests/features/permutation/perm-pin.feature</c>.
/// </summary>
[TestFixture]
public sealed class BitPermFixtureTests {

  private const string Page = "BitPerm/index.html";
  private const string Readme = "BitPerm/README.md";

  private static readonly string _directory = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "bitperm");

  private static readonly int[] _widths = [8, 16, 32, 64, 128];

  private static readonly string[] _patterns = ["identity", "reverse", "swap_halves", "set_all_0", "clear"];

  private static JsonElement Load(string path) {
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, path)));
    return document.RootElement.Clone();
  }

  private static string[] Names(string folder)
    => [.. Directory.GetFiles(Path.Combine(_directory, folder), "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal)!];

  private static JsonElement Preset(string id) => Load("presets.json").EnumerateArray().Single(preset => preset.GetProperty("id").GetString() == id);

  private static JsonElement Block(string id) => Load("blocks.json").EnumerateArray().Single(block => block.GetProperty("id").GetString() == id);

  private static UInt128 Hex(JsonElement value) {
    var text = value.GetString()!;
    Assert.That(text, Does.StartWith("0x"));
    return UInt128.Parse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
  }

  private static (UInt128 Input, UInt128 Output, UInt128 Mask)[] Vectors(string mapping)
    => [.. Load($"vectors/{mapping}.json").GetProperty("vectors").EnumerateArray().Select(vector => (Hex(vector.GetProperty("input")), Hex(vector.GetProperty("output")), Hex(vector.GetProperty("mask"))))];

  /// <summary>The page's raw evaluation, written again: a source bit, inverted or not, a constant, or nothing.</summary>
  private static (UInt128 Output, UInt128 Mask) Evaluate(JsonElement mapping, UInt128 input) {
    UInt128 output = 0;
    UInt128 mask = 0;
    var destination = 0;
    foreach (var entry in mapping.EnumerateArray()) {
      var bit = UInt128.One << destination;
      switch (entry.GetProperty("type").GetString()) {
        case "src":
          var value = (input >> entry.GetProperty("val").GetInt32()) & 1;
          if (entry.TryGetProperty("inv", out var inverted) && inverted.GetBoolean())
            value ^= 1;
          output |= value << destination;
          mask |= bit;
          break;
        case "const":
          output |= (UInt128)entry.GetProperty("val").GetInt32() << destination;
          mask |= bit;
          break;
        case "empty":
          break;
        default:
          throw new InvalidDataException($"unknown mapping entry {entry}");
      }
      ++destination;
    }
    return (output, mask);
  }

  [Test]
  public void Blocks_AreTheSixteenOfThePageInOrder()
    => Assert.That(Load("blocks.json").EnumerateArray().Select(block => block.GetProperty("id").GetString()),
      Is.EqualTo(new[] { "shl", "shr", "rol", "ror", "mul", "bmi2", "pext", "pdep", "pshufb", "rlwinm", "andc", "bfi", "ubfx", "rev", "rbit", "lut" }));

  [Test]
  public void Presets_AreTwentyFiveWithCustomLast_EachWithElevenCostsAndNineEnables() {
    var presets = Load("presets.json").EnumerateArray().ToArray();
    Assert.That(presets, Has.Length.EqualTo(25));
    Assert.That(presets[0].GetProperty("id").GetString(), Is.EqualTo("modern"));
    Assert.That(presets[^1].GetProperty("id").GetString(), Is.EqualTo("custom"));
    foreach (var preset in presets) {
      Assert.That(preset.GetProperty("costs").EnumerateObject().Select(cost => cost.Name),
        Is.EqualTo(new[] { "shift", "rotate", "mul", "bmi", "pshufb", "lut", "basic", "rlwinm", "bfx", "rev", "rbit" }), preset.ToString());
      Assert.That(preset.GetProperty("enables").EnumerateObject().Count(), Is.EqualTo(9), preset.ToString());
    }
  }

  [TestCase("modern", "bmi", 3)]
  [TestCase("zen1_2", "bmi", 18)]
  [TestCase("6502", "shift", 2)]
  [TestCase("6502", "mul", 999)]
  [TestCase("i8086", "mul", 118)]
  public void Preset_HasThePageCost(string id, string key, int value)
    => Assert.That(Preset(id).GetProperty("costs").GetProperty(key).GetInt32(), Is.EqualTo(value));

  [Test]
  public void Tests_AreFiftyOneInTenSectionsInSourceOrder() {
    var tests = Load("tests.json").EnumerateArray().ToArray();
    Assert.That(tests, Has.Length.EqualTo(51));
    Assert.That(tests.Select(test => test.GetProperty("index").GetInt32()), Is.EqualTo(Enumerable.Range(1, 51)));
    Assert.That(tests.Select(test => test.GetProperty("sourceLine").GetInt32()), Is.Ordered.Ascending);
    Assert.That(tests.Select(test => test.GetProperty("section").GetString()).Distinct().ToArray(), Has.Length.EqualTo(10));
    Assert.That(tests[0].GetProperty("section").GetString(), Is.EqualTo("Basic Operations"));
    Assert.That(tests.All(test => test.GetProperty("body").GetString()!.Length > 0), Is.True);
  }

  [Test]
  public void Limits_AreThePageConstants() {
    var limits = Load("limits.json");
    Assert.That(limits.GetProperty("SEARCH_MAX_STATES").GetInt32(), Is.EqualTo(10000));
    Assert.That(limits.GetProperty("SEARCH_TIMEOUT_MS").GetInt32(), Is.EqualTo(5000));
    Assert.That(limits.GetProperty("MAX_SOLVER_ITERATIONS").GetInt32(), Is.EqualTo(64));
    Assert.That(limits.GetProperty("DEFAULT_BIT_WIDTH").GetInt32(), Is.EqualTo(32));
    Assert.That(limits.GetProperty("defaultCosts").EnumerateObject().Select(cost => cost.Name), Is.EqualTo(new[] { "shift", "rotate", "mul", "bmi", "pshufb", "basic" }));
  }

  [Test]
  public void Mappings_AreTwentyFivePatternsAndNineteenRouteMaps_EachWithAVectorFile() {
    var mappings = Names("mappings");
    var patterns = _patterns.SelectMany(pattern => _widths.Select(width => $"pattern-{pattern}-{width}")).Order(StringComparer.Ordinal);
    Assert.That(mappings.Where(name => name.StartsWith("pattern-", StringComparison.Ordinal)), Is.EqualTo(patterns));
    Assert.That(mappings.Count(name => name.StartsWith("test-", StringComparison.Ordinal)), Is.EqualTo(19));
    Assert.That(mappings, Has.Length.EqualTo(44));
    Assert.That(Names("vectors"), Is.EqualTo(mappings));
  }

  [Test]
  public void Pin_NamesThePageAndReadmeAsTheCorpusLocksThem() {
    var root = PrototypeFixture.RepositoryRoot;
    var repository = CorpusManifest.Load(Path.Combine(root, CorpusManifest.RepositoryPath)).Repository("Hawkynt/Hawkynt.github.io")!;
    var lockFile = CorpusLock.Load(Path.Combine(root, CorpusLock.RepositoryPath));
    var pin = Load("pin.json");
    Assert.That(pin.GetProperty("repository").GetString(), Is.EqualTo(repository.Name));
    Assert.That(pin.GetProperty("commit").GetString(), Is.EqualTo(repository.Commit));
    var files = pin.GetProperty("files").EnumerateArray().ToDictionary(file => file.GetProperty("path").GetString()!, file => file.GetProperty("sha256").GetString());
    Assert.That(files.Keys, Is.EqualTo(new[] { Page, Readme }));
    foreach (var (path, sha256) in files)
      Assert.That(sha256, Is.EqualTo(lockFile.Find(repository.Name, repository.Commit, path)?.Sha256), path);
  }

  [Test]
  public void Notice_AttributesThePinAndTheLicence() {
    var root = PrototypeFixture.RepositoryRoot;
    var repository = CorpusManifest.Load(Path.Combine(root, CorpusManifest.RepositoryPath)).Repository("Hawkynt/Hawkynt.github.io")!;
    var licence = CorpusLock.Load(Path.Combine(root, CorpusLock.RepositoryPath)).Find(repository.Name, repository.Commit, "LICENSE")!;
    var notice = File.ReadAllText(Path.Combine(_directory, "THIRD-PARTY-NOTICE.BitPerm.txt"));
    Assert.That(notice, Does.Contain($"commit {repository.Commit}"));
    Assert.That(notice, Does.Contain($"sha256 {licence.Sha256}"));
    Assert.That(notice, Does.Contain("GNU LESSER GENERAL PUBLIC LICENSE"));
  }

  [Test]
  public void Fixture_IsRegisteredInTheCorpus() {
    var fixture = CorpusManifest.Load(Path.Combine(PrototypeFixture.RepositoryRoot, CorpusManifest.RepositoryPath)).Fixture("bitperm.page")!;
    Assert.That((fixture.Repository, fixture.Path, fixture.Licence), Is.EqualTo(("Hawkynt/Hawkynt.github.io", Page, "LGPL-3.0-or-later")));
    Assert.That(fixture.Files, Does.Contain(Readme));
  }

  [Test]
  public void ReversePattern8_MatchesTheHandComputation() {
    var vectors = Vectors("pattern-reverse-8");
    Assert.That(vectors, Has.Length.EqualTo(256));
    Assert.That(vectors.Select(vector => vector.Input), Is.EqualTo(Enumerable.Range(0, 256).Select(x => (UInt128)x)));
    Assert.That(vectors[0xB4], Is.EqualTo(((UInt128)0xB4, (UInt128)0x2D, (UInt128)0xFF)));
  }

  [Test]
  public void SwapHalvesPattern8_MatchesTheHandComputation()
    => Assert.That(Vectors("pattern-swap_halves-8")[0xB4], Is.EqualTo(((UInt128)0xB4, (UInt128)0x4B, (UInt128)0xFF)));

  [Test]
  public void ReversePattern128_SwapsBitZeroAndBit127() {
    var vectors = Vectors("pattern-reverse-128");
    var top = UInt128.One << 127;
    Assert.That(vectors.Single(vector => vector.Input == UInt128.One).Output, Is.EqualTo(top));
    Assert.That(vectors.Single(vector => vector.Input == top).Output, Is.EqualTo(UInt128.One));
  }

  [TestCase(8, 256)]
  [TestCase(16, 2 + (2 * 16) + 256)]
  [TestCase(32, 2 + (2 * 32) + 256)]
  [TestCase(64, 2 + (2 * 64) + 256)]
  [TestCase(128, 2 + (2 * 128) + 256)]
  public void VectorCount_DependsOnTheWidth(int width, int count) {
    var vectors = Vectors($"pattern-identity-{width}");
    Assert.That(vectors, Has.Length.EqualTo(count));
    if (width == 8)
      return;
    var ones = width == 128 ? UInt128.MaxValue : (UInt128.One << width) - 1;
    Assert.That(vectors.Take(2).Select(vector => vector.Input), Is.EqualTo(new[] { UInt128.Zero, ones }));
    Assert.That(vectors.Skip(2).Take(width).Select(vector => vector.Input), Is.EqualTo(Enumerable.Range(0, width).Select(bit => UInt128.One << bit)));
    Assert.That(vectors.Skip(2 + width).Take(width).Select(vector => vector.Input), Is.EqualTo(Enumerable.Range(0, width).Select(bit => ones ^ (UInt128.One << bit))));
    Assert.That(vectors.Skip(2 + (2 * width)).All(vector => vector.Input <= ones), Is.True);
  }

  [Test]
  public void EmptyRouteMap_HasEightEmptyEntriesAndMaskZero() {
    var name = Names("mappings").Single(name => name.StartsWith("test-", StringComparison.Ordinal) && name.EndsWith("-empty-permutation", StringComparison.Ordinal));
    var mapping = Load($"mappings/{name}.json");
    Assert.That(mapping.GetProperty("width").GetInt32(), Is.EqualTo(8));
    Assert.That(mapping.GetProperty("mapping").EnumerateArray().Select(entry => entry.ToString()), Is.EqualTo(Enumerable.Repeat("""{"type": "empty"}""", 8)));
    Assert.That(Vectors(name).All(vector => vector.Mask == 0 && vector.Output == 0), Is.True);
  }

  [Test]
  public void RouteMap64_RoutesSourceNineToBitZero() {
    var mapping = Load($"mappings/{Names("mappings").Single(name => name.Contains("non-byte-aligned", StringComparison.Ordinal))}.json");
    Assert.That(mapping.GetProperty("width").GetInt32(), Is.EqualTo(64));
    var entries = mapping.GetProperty("mapping").EnumerateArray().ToArray();
    Assert.That(entries[0].ToString(), Is.EqualTo("""{"type": "src", "val": 9, "inv": false}"""));
    Assert.That(entries.Skip(1).All(entry => entry.GetProperty("type").GetString() == "empty"), Is.True);
  }

  [TestCase("pattern-identity-8", "src")]
  [TestCase("pattern-set_all_0-8", "const")]
  [TestCase("pattern-clear-8", "empty")]
  public void PatternEntries_AreThePageObjects(string mapping, string type) {
    var entries = Load($"mappings/{mapping}.json").GetProperty("mapping").EnumerateArray().ToArray();
    Assert.That(entries, Has.Length.EqualTo(8));
    Assert.That(entries.All(entry => entry.GetProperty("type").GetString() == type && entry.GetProperty("inv").GetBoolean() == false), Is.True);
  }

  [Test]
  public void ConstantPattern_KeepsTheMaskAndOutputsZero() {
    foreach (var vector in Vectors("pattern-set_all_0-8"))
      Assert.That((vector.Output, vector.Mask), Is.EqualTo((UInt128.Zero, (UInt128)0xFF)));
  }

  [Test]
  public void Blocks_PlatformsAreNullOrPresetIds() {
    Assert.That(Block("shl").GetProperty("platforms").ValueKind, Is.EqualTo(JsonValueKind.Null));
    Assert.That(Block("bmi2").GetProperty("platforms").EnumerateArray().Select(id => id.GetString()), Is.EqualTo(new[] { "modern", "zen1_2", "zen3" }));
    var presets = Load("presets.json").EnumerateArray().Select(preset => preset.GetProperty("id").GetString()).ToHashSet();
    var named = Load("blocks.json").EnumerateArray().Where(block => block.GetProperty("platforms").ValueKind == JsonValueKind.Array)
      .SelectMany(block => block.GetProperty("platforms").EnumerateArray().Select(id => id.GetString())).Distinct();
    Assert.That(named, Is.SubsetOf(presets));
  }

  [Test]
  public void Blocks_WithoutFindRoutingsAreTheHelpersAndTheLut()
    => Assert.That(Load("blocks.json").EnumerateArray().Where(block => !block.GetProperty("hasFindRoutings").GetBoolean()).Select(block => block.GetProperty("id").GetString()),
      Is.EqualTo(new[] { "pext", "pdep", "andc", "lut" }));

  private static string[] MappingNames() => Names("mappings");

  [TestCaseSource(nameof(MappingNames))]
  public void Vectors_AgreeWithTheirMapping(string name) {
    var mapping = Load($"mappings/{name}.json");
    var width = mapping.GetProperty("width").GetInt32();
    var entries = mapping.GetProperty("mapping");
    Assert.That(mapping.GetProperty("name").GetString(), Is.EqualTo(name));
    Assert.That(entries.GetArrayLength(), Is.EqualTo(width));
    Assert.That(Load($"vectors/{name}.json").GetProperty("width").GetInt32(), Is.EqualTo(width));
    var wrong = Vectors(name).Where(vector => (vector.Output, vector.Mask) != Evaluate(entries, vector.Input)).Select(vector => vector.Input.ToString("X", CultureInfo.InvariantCulture));
    Assert.That(wrong, Is.Empty);
  }

  [Test]
  public void Evaluate_ReadsEveryEntryType() {
    using var document = JsonDocument.Parse("""
      [{"type": "src", "val": 1, "inv": false}, {"type": "src", "val": 1, "inv": true}, {"type": "const", "val": 0}, {"type": "const", "val": 1}, {"type": "empty"}]
      """);
    Assert.That(Evaluate(document.RootElement, 0b10), Is.EqualTo(((UInt128)0b01001, (UInt128)0b01111)));
    Assert.That(Evaluate(document.RootElement, 0b00), Is.EqualTo(((UInt128)0b01010, (UInt128)0b01111)));
  }

  [Test]
  public void Evaluate_RefusesAnUnknownEntryType() {
    using var document = JsonDocument.Parse("""[{"type": "collision"}]""");
    Assert.That(() => Evaluate(document.RootElement, 0), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("collision"));
  }

}
