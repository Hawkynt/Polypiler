using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;
using Polypiler.Tooling.Spec.Families;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary>
/// <see cref="TargetsSpecFamily"/>: the schema <c>spec/schemas/targets.schema.json</c>, the rules, the
/// seed documents of <c>spec/targets/</c> and the fixtures of <c>tests/fixtures/spec/targets/</c>, each
/// run through the whole engine in a throw-away repository that holds the real schema, the real
/// <c>tooling/toolchains.lock</c> and the language and type documents of the valid fixture tree.
/// </summary>
[TestFixture]
public sealed class TargetsSpecFamilyTests {

  private const string SeedPath = "targets/javascript/es2022/target.yaml";

  private static readonly string _fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "spec", "targets");

  private static readonly string _valid = Path.Combine(_fixtures, "valid");

  private static readonly string _validTarget = Path.Combine(_valid, "targets", "javascript", "es2022", "target.yaml");

  private static readonly JsonObject _schema = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "targets.schema.json")))!;

  private static string Seed => File.ReadAllText(_validTarget);

  /// <summary>A repository with the family's schema, the toolchain lock unless told otherwise, and the language and type documents of the valid tree.</summary>
  private static SpecRepository Repository(bool withLock = true) {
    var repository = new SpecRepository();
    repository.Write("spec/schemas/targets.schema.json", File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "targets.schema.json")));
    if (withLock)
      repository.Write(ToolchainLock.RepositoryPath, File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tooling", "toolchains.lock")));
    foreach (var file in Directory.EnumerateFiles(_valid, "*.yaml", SearchOption.AllDirectories)) {
      var path = Relative(_valid, file);
      if (!path.StartsWith("targets/", StringComparison.Ordinal))
        repository.Document(path, File.ReadAllText(file));
    }

    return repository;
  }

  private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

  /// <summary>Where a document with this ID belongs: <c>targets/&lt;language&gt;/&lt;profile&gt;/target.yaml</c>.</summary>
  private static string PathOf(string text) {
    var parts = Regex.Match(text, "^id: (.+)$", RegexOptions.Multiline).Groups[1].Value.Split('.', 3);
    return $"targets/{parts[1]}/{parts[2]}/target.yaml";
  }

  /// <summary>The target documents, each at the place its ID gives it, validated for the family.</summary>
  private static string[] Lines(params string[] documents) {
    using var repository = Repository();
    foreach (var text in documents)
      repository.Document(PathOf(text), text);
    return repository.Lines("targets");
  }

  /// <summary>A fixture of <c>invalid/</c>: a file at the place its ID gives it, a folder as the target documents of the tree.</summary>
  private static string[] Fixture(string name) {
    using var repository = Repository();
    var file = Path.Combine(_fixtures, "invalid", name + ".yaml");
    if (File.Exists(file)) {
      var text = File.ReadAllText(file);
      repository.Document(PathOf(text), text);
    } else {
      var folder = Path.Combine(_fixtures, "invalid", name);
      foreach (var path in Directory.EnumerateFiles(folder, "*.yaml", SearchOption.AllDirectories))
        repository.Document(Relative(folder, path), File.ReadAllText(path));
    }

    return repository.Lines("targets");
  }

  /// <summary>A body line <c>  key: value</c> of the seed replaced; <see langword="null"/> removes it. <c>physicalTypes</c> takes its list lines with it.</summary>
  private static string With(string text, string key, string? value) {
    var pattern = key == "physicalTypes" ? @"^  physicalTypes:\n(    - .*\n)*" : $"^  {Regex.Escape(key)}: .*\n";
    Assert.That(Regex.IsMatch(text, pattern, RegexOptions.Multiline), $"the document has no key '{key}'");
    var replacement = value is null ? "" : $"  {key}: {value}\n";
    return Regex.Replace(text, pattern, replacement.Replace("$", "$$", StringComparison.Ordinal), RegexOptions.Multiline);
  }

  private static string Seeded(string key, string? value) => With(Seed, key, value);

  /// <summary>The seed with its 64-bit integers behind a helper, so that it fits an integer limit of 53 bits.</summary>
  private static string Narrow(string limits)
    => Seeded("limits", limits).Replace("…)\", helper: false", "…)\", helper: true", StringComparison.Ordinal);

  private static string Types(params string[] rows) => "\n" + string.Concat(rows.Select(row => $"    - {{{row}}}\n")).TrimEnd('\n');

  /// <summary>An integer type document <c>TYPES.&lt;name&gt;</c> of the given width, for the boundaries of <c>type-fits-target</c>.</summary>
  private static string IntegerType(string name, int bits) {
    var text = File.ReadAllText(Path.Combine(_valid, "types", "integer", "i64.yaml"));
    return Regex.Replace(text.Replace("TYPES.i64", $"TYPES.{name}", StringComparison.Ordinal), "^  bits: 64$", $"  bits: {bits.ToString(CultureInfo.InvariantCulture)}", RegexOptions.Multiline);
  }

  [Test]
  public void Family_IsRegistered() {
    var family = SpecFamilies.All.OfType<TargetsSpecFamily>().Single();
    Assert.That((family.Folder, family.SchemaPath), Is.EqualTo(("targets", "spec/schemas/targets.schema.json")));
  }

  [Test]
  public void Check_RefusesNullArguments() {
    var family = new TargetsSpecFamily();
    var document = SpecDocument.Parse(SeedPath, Seed);
    using var repository = Repository();
    Assert.That(() => family.Check(null!, repository.Tree()), Throws.ArgumentNullException);
    Assert.That(() => family.Check(document, null!), Throws.ArgumentNullException);
  }

  /// <summary>The documents of <c>spec/targets/</c>: C# with its nine target frameworks, JavaScript with its five levels.</summary>
  [Test]
  public void RepositorySeed_IsValid() {
    var validation = new SpecValidator(SpecFamilies.All).Run(SpecTree.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "spec"), PrototypeFixture.RepositoryRoot), "targets");
    Assert.That(validation.Diagnostics.Select(DiagnosticRenderer.ToText), Is.Empty);
    Assert.That(validation.Documents, Is.EqualTo(14));
  }

  /// <summary>The seed profiles are exactly the profiles the backends accept.</summary>
  [Test]
  public void RepositorySeed_HasATargetPerBackendProfile() {
    Assert.That(Profiles("csharp"), Is.EquivalentTo(CSharpProfile.All.Select(profile => profile.Name)));
    Assert.That(Profiles("javascript"), Is.EquivalentTo(JavaScriptProfile.LevelNames));
  }

  /// <summary>A target runs on the toolchain the language profile of the same name names.</summary>
  [Test]
  public void RepositorySeed_UsesTheToolchainOfTheLanguageProfile() {
    var tree = SpecTree.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "spec"), PrototypeFixture.RepositoryRoot);
    var targets = tree.Documents.Where(document => document.Family == "targets").ToList();
    Assert.That(targets, Is.Not.Empty);
    foreach (var target in targets) {
      var parts = target.Path.Split('/');
      var profile = tree.ById[$"LANGUAGES.{parts[1]}.{parts[2]}"];
      Assert.That((string?)target.Body!["toolchain"], Is.EqualTo((string?)profile.Body!["toolchain"]), target.Path);
    }
  }

  /// <summary>
  /// ECMA-262: below ES2020 there is no <c>BigInt</c>, so 64-bit integers need the pair of halves; below
  /// ES2015 there is no <c>Math.fround</c>. Number holds integers exactly up to 2^53.
  /// </summary>
  [TestCase("es5", 53, true, true)]
  [TestCase("es2015", 53, true, false)]
  [TestCase("es2017", 53, true, false)]
  [TestCase("es2020", 64, false, false)]
  [TestCase("es2022", 64, false, false)]
  public void RepositorySeed_JavaScriptFacts(string profile, long maxIntegerBits, bool longHelper, bool floatHelper) {
    var body = SpecDocument.Parse($"targets/javascript/{profile}/target.yaml", File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "targets", "javascript", profile, "target.yaml"))).Body!;
    bool Helper(string type) => (bool)body["physicalTypes"]!.AsArray().Single(entry => (string?)entry!["type"] == type)!["helper"]!;
    Assert.That(((long)body["limits"]!["maxIntegerBits"]!, (long)body["limits"]!["exactIntegerBits"]!), Is.EqualTo((maxIntegerBits, 53L)));
    Assert.That((Helper("TYPES.i64"), Helper("TYPES.u64"), Helper("TYPES.f32"), Helper("TYPES.u32")), Is.EqualTo((longHelper, longHelper, floatHelper, false)));
  }

  private static string[] Profiles(string language) {
    var folder = Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "targets", language);
    return [.. Directory.EnumerateDirectories(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
  }

  [Test]
  public void ValidFixture_IsACopyOfTheSeed()
    => Assert.That(Seed, Is.EqualTo(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "targets", "javascript", "es2022", "target.yaml"))));

  /// <summary>The valid tree's language and type documents are copies of the repository's.</summary>
  [Test]
  public void ValidFixture_ReferencedDocumentsAreCopies() {
    foreach (var file in Directory.EnumerateFiles(_valid, "*.yaml", SearchOption.AllDirectories))
      Assert.That(File.ReadAllText(file), Is.EqualTo(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", Relative(_valid, file)))), file);
  }

  /// <summary>Test design 1: the pointerless managed target.</summary>
  [Test]
  public void ValidFixture_Passes() {
    var validation = new SpecValidator(SpecFamilies.All).Run(SpecTree.Load(_valid, PrototypeFixture.RepositoryRoot), "targets");
    Assert.That((validation.Documents, validation.Diagnostics.Count), Is.EqualTo((1, 0)));
  }

  /// <summary>Test designs 2 and 5: each pointer model with a consistent and an inconsistent width.</summary>
  [TestCase("native", 64, null)]
  [TestCase("native", 32, null)]
  [TestCase("native", 16, null)]
  [TestCase("fat", 0, null)]
  [TestCase("none", 0, null)]
  [TestCase("native", 0, "pointers 'native' requires abi.pointerBits above 0, found 0")]
  [TestCase("fat", 64, "pointers 'fat' requires abi.pointerBits 0, found 64")]
  [TestCase("none", 32, "pointers 'none' requires abi.pointerBits 0, found 32")]
  public void PointerConsistency(string pointers, int bits, string? reason) {
    var text = Seeded("pointers", pointers).Replace("pointerBits: 0}", $"pointerBits: {bits.ToString(CultureInfo.InvariantCulture)}}}", StringComparison.Ordinal);
    Assert.That(Lines(text), reason is null ? Is.Empty : Is.EqualTo(new[] { $"{SeedPath}(16,57): error PLY0046: {reason} (rule: pointer-consistency)" }));
  }

  /// <summary>Test design 5: a pointer width outside 0, 16, 32 and 64.</summary>
  [TestCase(8)]
  [TestCase(128)]
  public void PointerBitsOutsideTheSet_IsPLY0040(int bits)
    => Assert.That(Lines(Seeded("pointers", "native").Replace("pointerBits: 0}", $"pointerBits: {bits.ToString(CultureInfo.InvariantCulture)}}}", StringComparison.Ordinal)), Has.Some.Contains("error PLY0040: /spec/abi/pointerBits:"));

  public static IEnumerable<TestCaseData> EnumerationValues() {
    var properties = (JsonObject)_schema["properties"]!;
    foreach (var value in properties["unions"]!["enum"]!.AsArray())
      yield return new TestCaseData(Seeded("unions", (string)value!)).SetName($"unions {value}");
    foreach (var value in properties["abi"]!["properties"]!["byteOrder"]!["enum"]!.AsArray())
      yield return new TestCaseData(Seed.Replace("byteOrder: host", $"byteOrder: {value}", StringComparison.Ordinal)).SetName($"byteOrder {value}");
    foreach (var value in properties["alignment"]!["properties"]!["packing"]!["enum"]!.AsArray())
      yield return new TestCaseData(Seed.Replace("packing: not-observable", $"packing: {value}", StringComparison.Ordinal)).SetName($"packing {value}");
    foreach (var value in properties["packaging"]!["properties"]!["ecosystem"]!["enum"]!.AsArray())
      yield return new TestCaseData(Seed.Replace("ecosystem: npm", $"ecosystem: {value}", StringComparison.Ordinal)).SetName($"ecosystem {value}");
  }

  /// <summary>Test design 3: every value of every enumeration is valid.</summary>
  [TestCaseSource(nameof(EnumerationValues))]
  public void EnumerationValue_IsValid(string text) => Assert.That(Lines(text), Is.Empty);

  /// <summary>The enumerations and how many values each declares, as the measurement counts them.</summary>
  [Test]
  public void EnumerationValues_AreTheDeclaredOnes() {
    var properties = (JsonObject)_schema["properties"]!;
    int Count(JsonNode? node) => node!["enum"]!.AsArray().Count;
    Assert.That(
      (Count(properties["pointers"]), Count(properties["unions"]), Count(properties["abi"]!["properties"]!["byteOrder"]), Count(properties["abi"]!["properties"]!["pointerBits"]),
        Count(properties["alignment"]!["properties"]!["max"]), Count(properties["alignment"]!["properties"]!["packing"]), Count(properties["packaging"]!["properties"]!["ecosystem"])),
      Is.EqualTo((3, 4, 3, 4, 7, 3, 11)));
  }

  [TestCase("unions", "struct", "/spec/unions")]
  [TestCase("pointers", "smart", "/spec/pointers")]
  [TestCase("abi", "{name: ecmascript, byteOrder: middle, pointerBits: 0}", "/spec/abi/byteOrder")]
  [TestCase("alignment", "{max: 8, packing: tight}", "/spec/alignment/packing")]
  [TestCase("packaging", "{ecosystem: apt, layout: x}", "/spec/packaging/ecosystem")]
  public void UnknownEnumerationValue_IsPLY0040(string key, string value, string pointer)
    => Assert.That(Lines(Seeded(key, value)), Has.Some.Contains($"error PLY0040: {pointer}:"));

  /// <summary>Test designs 4 and 14: a 64-bit integer at an integer limit of 53, with and without a helper.</summary>
  [Test]
  public void WideIntegerWithHelper_IsValid()
    => Assert.That(Lines(Narrow("{maxIntegerBits: 53, exactIntegerBits: 53}")), Is.Empty);

  [Test]
  public void WideIntegerWithoutHelper_IsTypeFitsTarget() {
    var text = Narrow("{maxIntegerBits: 53, exactIntegerBits: 53}").Replace("asUintN(64, …)\", helper: true", "asUintN(64, …)\", helper: false", StringComparison.Ordinal);
    Assert.That(Lines(text), Is.EqualTo(new[] { $"{SeedPath}(28,90): error PLY0046: TYPES.u64 (64 bits) is mapped without a helper but limits.maxIntegerBits is 53 (rule: type-fits-target)" }));
  }

  /// <summary>Only integers are held to the integer limit: a 64-bit float maps without a helper at 32.</summary>
  [Test]
  public void WideFloatWithoutHelper_IsValid()
    => Assert.That(Lines(Narrow("{maxIntegerBits: 32, exactIntegerBits: 32}")), Is.Empty);

  /// <summary>Test design 7: an integer type exactly as wide as the limit, and one bit wider.</summary>
  [TestCase(53, true)]
  [TestCase(54, false)]
  public void IntegerWidthAtTheLimit(int bits, bool valid) {
    using var repository = Repository();
    repository.Document("types/integer/w.yaml", IntegerType("w", bits));
    repository.Document(SeedPath, With(Narrow("{maxIntegerBits: 53, exactIntegerBits: 53}"), "physicalTypes", Types("type: TYPES.w, native: Number, helper: false")));
    Assert.That(repository.Lines("targets"), valid ? Is.Empty : Is.EqualTo(new[] { $"{SeedPath}(20,47): error PLY0046: TYPES.w ({bits} bits) is mapped without a helper but limits.maxIntegerBits is 53 (rule: type-fits-target)" }));
  }

  /// <summary>A type document without a body or without an integer width cannot be measured, and is not.</summary>
  [TestCase("spec")]
  [TestCase("  bits: wide")]
  [TestCase("")]
  public void TypeWithoutWidth_IsNotMeasured(string cut) {
    using var repository = Repository();
    var type = IntegerType("w", 99);
    repository.Document("types/integer/w.yaml", cut == "spec" ? type[..type.IndexOf("spec:", StringComparison.Ordinal)] : Regex.Replace(type, "^  bits: .*$", cut, RegexOptions.Multiline));
    repository.Document(SeedPath, With(Narrow("{maxIntegerBits: 53, exactIntegerBits: 53}"), "physicalTypes", Types("type: TYPES.w, native: Number, helper: false")));
    Assert.That(repository.Lines("targets"), Is.Empty);
  }

  /// <summary>Test design 6: the exact width below, at and above the integer limit.</summary>
  [TestCase(52, true)]
  [TestCase(53, true)]
  [TestCase(54, false)]
  public void ExactIntegerBitsAtTheLimit(int exact, bool valid) {
    var lines = Lines(Narrow($"{{maxIntegerBits: 53, exactIntegerBits: {exact.ToString(CultureInfo.InvariantCulture)}}}"));
    Assert.That(lines, valid ? Is.Empty : Is.EqualTo(new[] { $"{SeedPath}(33,50): error PLY0046: limits.exactIntegerBits 54 exceeds limits.maxIntegerBits 53 (rule: limit-order)" }));
  }

  /// <summary>Test design 8: the alignment is a power of two from 1 to 64.</summary>
  [TestCase(0, false)]
  [TestCase(1, true)]
  [TestCase(64, true)]
  [TestCase(128, false)]
  [TestCase(3, false)]
  public void AlignmentMax(int max, bool valid) {
    var lines = Lines(Seed.Replace("max: 8,", $"max: {max.ToString(CultureInfo.InvariantCulture)},", StringComparison.Ordinal));
    Assert.That(lines, valid ? Is.Empty : Has.Some.Contains("error PLY0040: /spec/alignment/max:"));
  }

  /// <summary>Test design 9: no physical types, no SIMD features, no dependencies.</summary>
  [TestCase("physicalTypes")]
  [TestCase("simd")]
  [TestCase("dependencies")]
  public void EmptyList_IsValid(string key) => Assert.That(Lines(Seeded(key, "[]")), Is.Empty);

  /// <summary>A SIMD feature list and a dependency with an SPDX expression.</summary>
  [Test]
  public void FeaturesAndDependencies_AreValid()
    => Assert.That(Lines(Seeded("simd", "[sse2, avx2, neon]").Replace("dependencies: []", "dependencies: [{name: a, licence: MIT OR Apache-2.0, required: false}, {name: b, licence: LGPL-3.0-or-later, required: true}]", StringComparison.Ordinal)), Is.Empty);

  [TestCase("simd", "[sse2, sse2]", "/spec/simd")]
  [TestCase("simd", "[SSE2]", "/spec/simd/0")]
  [TestCase("dependencies", "[{name: a, licence: \"MIT, BSD\", required: true}]", "/spec/dependencies/0/licence")]
  public void MalformedList_IsPLY0040(string key, string value, string pointer)
    => Assert.That(Lines(Seeded(key, value)), Has.Some.Contains($"error PLY0040: {pointer}:"));

  /// <summary>Test design 10: the optional limits are at least 1 when present.</summary>
  [TestCase("maxRamBytes", 0, false)]
  [TestCase("maxRamBytes", 1, true)]
  [TestCase("maxCodeBytes", 0, false)]
  [TestCase("maxCodeBytes", 1, true)]
  [TestCase("maxArrayLength", 0, false)]
  [TestCase("maxArrayLength", 1, true)]
  public void OptionalLimit(string key, int value, bool valid) {
    var lines = Lines(Seed.Replace("maxArrayLength: 4294967295}", $"maxArrayLength: 4294967295, {key}: {value.ToString(CultureInfo.InvariantCulture)}}}", StringComparison.Ordinal).Replace("maxArrayLength: 4294967295, maxArrayLength", "maxArrayLength", StringComparison.Ordinal));
    Assert.That(lines, valid ? Is.Empty : Has.Some.Contains($"error PLY0040: /spec/limits/{key}:"));
  }

  [Test]
  public void OptionalLimitsAbsent_IsValid() => Assert.That(Lines(Seeded("limits", "{maxIntegerBits: 64, exactIntegerBits: 53}")), Is.Empty);

  [TestCase("{maxIntegerBits: 0, exactIntegerBits: 53}", "maxIntegerBits")]
  [TestCase("{maxIntegerBits: 64, exactIntegerBits: 0}", "exactIntegerBits")]
  public void IntegerLimitZero_IsPLY0040(string limits, string key)
    => Assert.That(Lines(Seeded("limits", limits)), Has.Some.Contains($"error PLY0040: /spec/limits/{key}:"));

  /// <summary>Test designs 11 to 15 and 18, and the rules beyond them: one fixture each.</summary>
  [TestCase("pointer-consistency", $"{SeedPath}(16,57): error PLY0046: pointers 'none' requires abi.pointerBits 0, found 32 (rule: pointer-consistency)")]
  [TestCase("limit-order", $"{SeedPath}(33,50): error PLY0046: limits.exactIntegerBits 64 exceeds limits.maxIntegerBits 53 (rule: limit-order)")]
  [TestCase("toolchain-known", $"{SeedPath}(15,14): error PLY0046: toolchain 'nodejs' is not a key of tooling/toolchains.lock (rule: toolchain-known)")]
  [TestCase("type-fits-target", $"{SeedPath}(28,90): error PLY0046: TYPES.u64 (64 bits) is mapped without a helper but limits.maxIntegerBits is 53 (rule: type-fits-target)")]
  [TestCase("unique-type", $"{SeedPath}(30,14): error PLY0046: type 'TYPES.bool' is listed twice (rule: unique-type)")]
  [TestCase("unknown-type", $"{SeedPath}(30,14): error PLY0043: unknown spec id 'TYPES.f80'")]
  [TestCase("unknown-language", "targets/ecmascript/es2022/target.yaml(13,13): error PLY0043: unknown spec id 'LANGUAGES.ecmascript'")]
  [TestCase("layout-language", $"{SeedPath}(13,13): error PLY0046: language 'LANGUAGES.csharp' does not match folder 'javascript' (rule: layout)")]
  [TestCase("layout-profile", $"{SeedPath}(14,12): error PLY0046: profile 'es2020' does not match folder 'es2022' (rule: layout)")]
  [TestCase("layout", "targets/javascript/target.yaml(1,5): error PLY0046: path 'targets/javascript/target.yaml' is not targets/<language identifier>/<profile>/target.yaml (rule: layout)")]
  [TestCase("layout-id", "targets/javascript/node-es2022/target.yaml(1,5): error PLY0046: id 'TARGETS.javascript.es2022' does not match folder 'node-es2022', expected 'TARGETS.javascript.node-es2022' (rule: layout)")]
  public void RuleFixture_IsReported(string fixture, string expected) => Assert.That(Fixture(fixture), Is.EqualTo(new[] { expected }));

  /// <summary>Test design 15: a second document for the same language and profile, which also lies in the wrong folder.</summary>
  [Test]
  public void UniqueTargetFixture_IsReportedAtTheSecond()
    => Assert.That(Fixture("unique-target"), Is.EqualTo(new[] {
      "targets/javascript/node-es2022/target.yaml(14,12): error PLY0046: profile 'es2022' does not match folder 'node-es2022' (rule: layout)",
      "targets/javascript/node-es2022/target.yaml(14,12): error PLY0046: target javascript/es2022 is already defined by TARGETS.javascript.es2022 (rule: unique-target)"
    }));

  /// <summary>An ID in the wrong language folder names that folder.</summary>
  [Test]
  public void IdOfAnotherLanguage_NamesTheLanguageFolder() {
    using var repository = Repository();
    repository.Document("targets/csharp/es2022/target.yaml", Seed);
    Assert.That(repository.Lines("targets"), Is.EqualTo(new[] { "targets/csharp/es2022/target.yaml(1,5): error PLY0046: id 'TARGETS.javascript.es2022' does not match folder 'csharp', expected 'TARGETS.csharp.es2022' (rule: layout)" }));
  }

  /// <summary>Earlier target documents without a body, of another language or of another profile describe no target this one repeats.</summary>
  [Test]
  public void EarlierTargetsOfOtherProfiles_AreNotCompared() {
    using var repository = Repository();
    repository.Document("targets/javascript/aaa/target.yaml", Seed[..Seed.IndexOf("spec:", StringComparison.Ordinal)].Replace("es2022", "aaa", StringComparison.Ordinal));
    repository.Document("targets/csharp/es2022/target.yaml", Seed.Replace("javascript", "csharp", StringComparison.Ordinal).Replace("toolchain: node", "toolchain: ref-net10-0", StringComparison.Ordinal));
    repository.Document("targets/javascript/ab/target.yaml", Seed.Replace("es2022", "ab", StringComparison.Ordinal).Replace("language: LANGUAGES.javascript", "language: 5", StringComparison.Ordinal));
    repository.Document("targets/javascript/ac/target.yaml", Seed.Replace("es2022", "ac", StringComparison.Ordinal).Replace("language: LANGUAGES.javascript", "language: [LANGUAGES.javascript]", StringComparison.Ordinal));
    repository.Document("targets/javascript/es2020/target.yaml", Seed.Replace("es2022", "es2020", StringComparison.Ordinal));
    repository.Document("demo/widget.yaml", SpecText.Document());
    repository.Document(SeedPath, Seed);
    Assert.That(repository.Lines("targets"), Is.EqualTo(new[] {
      "targets/csharp/es2022/target.yaml(13,13): error PLY0043: unknown spec id 'LANGUAGES.csharp'",
      "targets/javascript/aaa/target.yaml(1,1): error PLY0040: /: Required properties [\"spec\"] are not present",
      "targets/javascript/ab/target.yaml(13,13): error PLY0040: /spec/language: Value is \"integer\" but should be \"string\"",
      "targets/javascript/ac/target.yaml(13,13): error PLY0040: /spec/language: Value is \"array\" but should be \"string\""
    }));
  }

  /// <summary>Without a readable lock no toolchain can be confirmed, and the reason says so.</summary>
  [Test]
  public void MissingLock_ReportsTheToolchainAsUncheckable() {
    using var repository = Repository(withLock: false);
    repository.Document(SeedPath, Seed);
    Assert.That(repository.Lines("targets"), Has.Length.EqualTo(1).And.All.Contains("error PLY0046: toolchain 'node' cannot be checked: tooling/toolchains.lock is not a valid toolchain lock (cannot read the toolchain lock:").And.All.EndsWith("(rule: toolchain-known)"));
  }

  /// <summary>Every property the schema requires, at every level, with the fixture that lacks it.</summary>
  private static IEnumerable<(string Fixture, string Pointer, string Property)> RequiredProperties() {
    var properties = (JsonObject)_schema["properties"]!;
    foreach (var property in _schema["required"]!.AsArray())
      yield return ($"missing-{property}", "/spec", (string)property!);
    foreach (var name in new[] { "abi", "alignment", "limits", "packaging" })
      foreach (var property in properties[name]!["required"]!.AsArray())
        yield return ($"missing-{name}-{property}", $"/spec/{name}", (string)property!);
    foreach (var name in new[] { "physicalTypes", "dependencies" })
      foreach (var property in properties[name]!["items"]!["required"]!.AsArray())
        yield return ($"missing-{name}-{property}", $"/spec/{name}/0", (string)property!);
  }

  public static IEnumerable<TestCaseData> MissingPropertyCases()
    => RequiredProperties().Select(required => new TestCaseData(required.Fixture, required.Pointer, required.Property).SetName(required.Fixture));

  /// <summary>Test design 16: one fixture per required property, refused with the JSON pointer of the object that lacks it.</summary>
  [TestCaseSource(nameof(MissingPropertyCases))]
  public void MissingProperty_IsPLY0040(string fixture, string pointer, string property)
    => Assert.That(Fixture(fixture), Has.Some.Contains($"error PLY0040: {pointer}: Required properties [\"{property}\"] are not present"));

  /// <summary>Every missing-*.yaml fixture stands for a property the schema requires, and every required property has one.</summary>
  [Test]
  public void MissingFixtures_AreExactlyTheRequiredProperties() {
    var fixtures = Directory.EnumerateFiles(Path.Combine(_fixtures, "invalid"), "missing-*.yaml").Select(Path.GetFileNameWithoutExtension);
    Assert.That(fixtures, Is.EquivalentTo(RequiredProperties().Select(required => required.Fixture)));
  }

  /// <summary>The scenario's fixture: a dependency without its licence.</summary>
  [Test]
  public void DependencyWithoutLicence_IsPLY0040()
    => Assert.That(Fixture("dependency-without-licence"), Is.EqualTo(new[] { $"{SeedPath}(34,18): error PLY0040: /spec/dependencies/0: Required properties [\"licence\"] are not present" }));

  /// <summary>Test design 17: a property the schema does not know.</summary>
  [Test]
  public void ExtraProperty_IsPLY0040()
    => Assert.That(Fixture("extra-property"), Is.EqualTo(new[] { $"{SeedPath}(36,10): error PLY0040: /spec/extra: property 'extra' is not allowed" }));

}
