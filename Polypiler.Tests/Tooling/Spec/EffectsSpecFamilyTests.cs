using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;
using Polypiler.Tooling.Spec.Families;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary>
/// <see cref="EffectsSpecFamily"/> with <c>spec/schemas/effects.schema.json</c>, run through the whole
/// engine: the valid fixtures and the seed document, every enumeration value, every rule and every
/// negative fixture of <c>tests/fixtures/spec/effects/</c>.
/// </summary>
[TestFixture]
public sealed class EffectsSpecFamilyTests {

  private const string SchemaPath = "spec/schemas/effects.schema.json";

  private static readonly string _fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "spec", "effects");

  /// <summary>A repository with the family's schema and nothing else of it.</summary>
  private static SpecRepository Repository() {
    var repository = new SpecRepository();
    repository.Write(SchemaPath, File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, SchemaPath)));
    return repository;
  }

  /// <summary>
  /// An effect document <c>effects/&lt;name&gt;.yaml</c>: a strict, non-default private-memory effect
  /// reading and writing nothing, with body keys replaced (<see langword="null"/> removes one). The
  /// body starts on line 13, one key per line in the order of the schema.
  /// </summary>
  private static string Effect(string name, params (string Key, string? Value)[] changes) {
    (string Key, string? Value)[] body = [
      ("name", name), ("class", "private-memory"), ("ordering", "strict"), ("duplication", "forbidden"), ("elimination", "forbidden"),
      ("reads", "[]"), ("writes", "[]"), ("mayTrap", "false"), ("defaultForUnknown", "false"), ("targets", "{unsupported: PLY0100}"),
    ];
    foreach (var (key, value) in changes) {
      var index = Array.FindIndex(body, pair => pair.Key == key);
      if (index < 0)
        body = [.. body, (key, value)];
      else
        body[index] = (key, value);
    }

    return $"id: EFFECTS.{name}\ntitle: Effect {name}\nversion: 1\nstatus: draft\nowner: Hawkynt\nissue: SPEC-EFFECTS\ntests: []\n"
      + "introducedIn: P1\ncompatibility: none\nreviewedRevision: 0\nnormative: \"\"\nspec:\n"
      + string.Concat(body.Where(pair => pair.Value is not null).Select(pair => $"  {pair.Key}: {pair.Value}\n"));
  }

  /// <summary>The default effect, as the seed document states it.</summary>
  private static string DefaultEffect(string name = "unknown-call", params (string Key, string? Value)[] changes)
    => Effect(name, [("class", "unknown-call"), ("reads", "[unknown]"), ("writes", "[unknown]"), ("mayTrap", "true"), ("defaultForUnknown", "true"), .. changes]);

  /// <summary>Validates the family in a repository holding the given documents, as diagnostic lines.</summary>
  private static string[] Lines(params (string Path, string Text)[] documents) {
    using var repository = Repository();
    foreach (var (path, text) in documents)
      repository.Document(path, text);
    return repository.Lines("effects");
  }

  /// <summary>Validates one effect next to the default effect.</summary>
  private static string[] WithDefault(string name, params (string Key, string? Value)[] changes)
    => Lines(("effects/unknown-call.yaml", DefaultEffect()), ($"effects/{name}.yaml", Effect(name, changes)));

  /// <summary>
  /// Validates a fixture of <c>invalid/</c>: a folder is the whole family; a file goes into the family
  /// next to the valid default effect, unless it declares itself the default.
  /// </summary>
  private static string[] Fixture(string fixture) {
    using var repository = Repository();
    var source = Path.Combine(_fixtures, "invalid", fixture);
    if (Directory.Exists(source)) {
      foreach (var file in Directory.EnumerateFiles(source, "*.yaml", SearchOption.AllDirectories))
        repository.Document("effects/" + Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(file));
    } else {
      var text = File.ReadAllText(source);
      repository.Document("effects/" + fixture, text);
      if (!text.Contains("defaultForUnknown: true", StringComparison.Ordinal))
        repository.Document("effects/unknown-call.yaml", File.ReadAllText(Path.Combine(_fixtures, "valid", "effects", "unknown-call.yaml")));
    }

    return repository.Lines("effects");
  }

  [Test]
  public void Family_IsRegistered() {
    var family = SpecFamilies.All.OfType<EffectsSpecFamily>().Single();
    Assert.That((family.Folder, family.SchemaPath), Is.EqualTo(("effects", SchemaPath)));
  }

  [Test]
  public void Schema_IsValidAgainstTheMetaSchema() {
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, SchemaPath)));
    Assert.That(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
  }

  [Test]
  public void ValidFixtures_HaveNoErrors() {
    var result = new SpecValidator(SpecFamilies.All).Run(SpecTree.Load(Path.Combine(_fixtures, "valid"), PrototypeFixture.RepositoryRoot), "effects");
    Assert.That((result.Documents, result.Diagnostics.Select(DiagnosticRenderer.ToText)), Is.EqualTo((3, Array.Empty<string>())));
  }

  [Test]
  public void SeedDocument_IsValidAndIsTheFixture() {
    var seed = Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "effects", "unknown-call.yaml");
    var result = new SpecValidator(SpecFamilies.All).Run(SpecTree.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "spec"), PrototypeFixture.RepositoryRoot), "effects");
    Assert.That(result.Diagnostics.Select(DiagnosticRenderer.ToText), Is.Empty);
    Assert.That(File.ReadAllText(seed), Is.EqualTo(File.ReadAllText(Path.Combine(_fixtures, "valid", "effects", "unknown-call.yaml"))));
  }

  [Test]
  public void SingleDocumentThatIsTheDefault_IsValid() => Assert.That(Lines(("effects/unknown-call.yaml", DefaultEffect())), Is.Empty);

  [Test]
  public void PureReadOfPrivateMemory_IsValid()
    => Assert.That(WithDefault("pure-read", ("ordering", "free"), ("duplication", "allowed"), ("elimination", "allowed"), ("reads", "[heap]")), Is.Empty);

  [TestCase("unknown-call")]
  [TestCase("volatile")]
  [TestCase("mmio")]
  [TestCase("device-io")]
  [TestCase("external-memory")]
  [TestCase("code-write")]
  [TestCase("mixed-asm")]
  [TestCase("trap")]
  [TestCase("allocation")]
  [TestCase("private-memory")]
  public void EveryClass_IsValid(string effectClass) => Assert.That(WithDefault("effect", ("class", effectClass)), Is.Empty);

  [TestCase("ordering", "strict")]
  [TestCase("ordering", "within-region")]
  [TestCase("ordering", "free")]
  [TestCase("duplication", "forbidden")]
  [TestCase("duplication", "allowed")]
  [TestCase("elimination", "forbidden")]
  [TestCase("elimination", "if-unobserved")]
  [TestCase("elimination", "allowed")]
  public void EveryPermission_IsValidForANonVolatileClass(string key, string value) => Assert.That(WithDefault("effect", (key, value)), Is.Empty);

  [TestCase("registers")]
  [TestCase("flags")]
  [TestCase("stack")]
  [TestCase("heap")]
  [TestCase("globals")]
  [TestCase("io")]
  [TestCase("code")]
  [TestCase("unknown")]
  public void EveryRegionKind_IsValidToReadAndWrite(string region)
    => Assert.That(WithDefault("effect", ("reads", $"[{region}]"), ("writes", $"[{region}]")), Is.Empty);

  [TestCase("class", "magic", 14, 10)]
  [TestCase("ordering", "loose", 15, 13)]
  [TestCase("duplication", "sometimes", 16, 16)]
  [TestCase("elimination", "never", 17, 16)]
  [TestCase("reads", "[memory]", 18, 11)]
  [TestCase("writes", "[memory]", 19, 12)]
  public void UnknownEnumerationValue_IsASchemaViolation(string key, string value, int line, int column) {
    var pointer = value.StartsWith('[') ? $"/spec/{key}/0" : $"/spec/{key}";
    Assert.That(WithDefault("effect", (key, value)).Single(), Does.StartWith($"effects/effect.yaml({line},{column}): error PLY0040: {pointer}:"));
  }

  [Test]
  public void RegionGivenTwice_IsASchemaViolation() => Assert.That(WithDefault("effect", ("reads", "[heap, heap]")).Single(), Does.Contain("error PLY0040: /spec/reads:"));

  [TestCase("volatile", "ordering", "within-region", 15, 13)]
  [TestCase("volatile", "duplication", "allowed", 16, 16)]
  [TestCase("volatile", "elimination", "if-unobserved", 17, 16)]
  [TestCase("mmio", "ordering", "free", 15, 13)]
  [TestCase("mmio", "duplication", "allowed", 16, 16)]
  [TestCase("mmio", "elimination", "if-unobserved", 17, 16)]
  [TestCase("device-io", "ordering", "within-region", 15, 13)]
  [TestCase("device-io", "duplication", "allowed", 16, 16)]
  [TestCase("device-io", "elimination", "allowed", 17, 16)]
  [TestCase("code-write", "ordering", "free", 15, 13)]
  [TestCase("code-write", "duplication", "allowed", 16, 16)]
  [TestCase("code-write", "elimination", "if-unobserved", 17, 16)]
  [TestCase("unknown-call", "ordering", "within-region", 15, 13)]
  [TestCase("unknown-call", "duplication", "allowed", 16, 16)]
  [TestCase("unknown-call", "elimination", "allowed", 17, 16)]
  public void VolatileClassWithARelaxedPermission_BreaksVolatileIsStrict(string effectClass, string key, string value, int line, int column) {
    var required = key == "ordering" ? "strict" : "forbidden";
    Assert.That(WithDefault("effect", ("class", effectClass), (key, value)), Is.EqualTo(new[] {
      $"effects/effect.yaml({line},{column}): error PLY0046: class '{effectClass}' requires {key} '{required}', found '{value}' (rule: volatile-is-strict)"
    }));
  }

  [Test]
  public void NonVolatileClassRelaxed_IsValid() => Assert.That(WithDefault("allocation", ("class", "allocation"), ("elimination", "if-unobserved")), Is.Empty);

  [Test]
  public void NoDefault_BreaksSingleDefaultOnce()
    => Assert.That(Lines(("effects/a.yaml", Effect("a")), ("effects/b.yaml", Effect("b"))), Is.EqualTo(new[] {
      "effects/a.yaml(21,22): error PLY0046: no effect has defaultForUnknown: true (rule: single-default)"
    }));

  [Test]
  public void TwoDefaults_BreakSingleDefaultAtTheOneThatIsNotOfClassUnknownCall()
    => Assert.That(Lines(("effects/mmio.yaml", DefaultEffect("mmio", ("class", "mmio"))), ("effects/unknown-call.yaml", DefaultEffect())), Is.EqualTo(new[] {
      "effects/mmio.yaml(21,22): error PLY0046: EFFECTS.mmio is a second default effect; EFFECTS.unknown-call already is (rule: single-default)"
    }));

  [Test]
  public void TwoDefaultsOfAnotherClass_BreakSingleDefaultAtTheLaterOne()
    => Assert.That(Lines(("effects/a.yaml", DefaultEffect("a", ("class", "trap"))), ("effects/b.yaml", DefaultEffect("b", ("class", "trap")))), Is.EqualTo(new[] {
      "effects/b.yaml(21,22): error PLY0046: EFFECTS.b is a second default effect; EFFECTS.a already is (rule: single-default)"
    }));

  [TestCase("ordering", "within-region", 15, 13, "the default effect must have ordering 'strict', found 'within-region'")]
  [TestCase("duplication", "allowed", 16, 16, "the default effect must have duplication 'forbidden', found 'allowed'")]
  [TestCase("elimination", "if-unobserved", 17, 16, "the default effect must have elimination 'forbidden', found 'if-unobserved'")]
  [TestCase("reads", "[heap]", 18, 10, "the default effect must read 'unknown'")]
  [TestCase("writes", "[heap]", 19, 11, "the default effect must write 'unknown'")]
  public void RelaxedDefault_BreaksDefaultIsConservative(string key, string value, int line, int column, string reason)
    => Assert.That(Lines(("effects/default.yaml", DefaultEffect("default", ("class", "trap"), (key, value)))), Is.EqualTo(new[] {
      $"effects/default.yaml({line},{column}): error PLY0046: {reason} (rule: default-is-conservative)"
    }));

  [Test]
  public void EmptyReadsAndWrites_AreValid() => Assert.That(WithDefault("effect", ("ordering", "free"), ("reads", "[]"), ("writes", "[]")), Is.Empty);

  [Test]
  public void OneWriteWithFreeOrdering_BreaksWriteImpliesOrder()
    => Assert.That(WithDefault("effect", ("ordering", "free"), ("writes", "[globals]")), Is.EqualTo(new[] {
      "effects/effect.yaml(15,13): error PLY0046: ordering 'free' is not allowed for an effect that writes globals (rule: write-implies-order)"
    }));

  [Test]
  public void WritesAreListedInTheirOrder()
    => Assert.That(WithDefault("effect", ("ordering", "free"), ("writes", "[stack, heap]")).Single(), Does.Contain("writes stack, heap (rule: write-implies-order)"));

  [Test]
  public void OneWriteWithRegionOrdering_IsValid() => Assert.That(WithDefault("effect", ("ordering", "within-region"), ("writes", "[heap]")), Is.Empty);

  [TestCase("PLY0100", false)]
  [TestCase("PLY100", true)]
  [TestCase("ply0100", true)]
  [TestCase("PLY01000", true)]
  public void UnsupportedDiagnostic_IsAPlyCode(string code, bool refused) {
    var lines = WithDefault("effect", ("targets", $"{{unsupported: {code}}}"));
    Assert.That(lines, refused ? Has.Exactly(1).StartsWith("effects/effect.yaml(22,26): error PLY0040: /spec/targets/unsupported:") : Is.Empty);
  }

  [Test]
  public void TargetsWithAnotherProperty_IsASchemaViolation()
    => Assert.That(WithDefault("effect", ("targets", "{unsupported: PLY0100, drop: true}")).Single(), Does.Contain("PLY0040: /spec/targets/drop: property 'drop' is not allowed"));

  [Test]
  public void NameNotTheFileName_BreaksLayout()
    => Assert.That(Lines(("effects/unknown-call.yaml", DefaultEffect()), ("effects/effect.yaml", Effect("effect", ("name", "other")))), Is.EqualTo(new[] {
      "effects/effect.yaml(13,9): error PLY0046: name 'other' does not match file name 'effect.yaml' (rule: layout)"
    }));

  [Test]
  public void OtherDocumentsOfAnyShape_DoNotDisturbTheRules() {
    var lines = Lines(
      ("effects/a.yaml", Effect("a", ("defaultForUnknown", "\"yes\""))),
      ("effects/b.yaml", Effect("b").Replace("spec:\n", "spec: 5\nx:\n", StringComparison.Ordinal)),
      ("effects/c.yaml", Effect("c", ("name", "7"))),
      ("effects/unknown-call.yaml", DefaultEffect()));
    Assert.That(lines.Select(line => line[..line.IndexOf(':', StringComparison.Ordinal)]), Is.EqualTo(new[] {
      "effects/a.yaml(21,22)", "effects/b.yaml(14,3)", "effects/c.yaml(13,9)"
    }));
  }

  [Test]
  public void Check_RefusesNull() {
    var family = new EffectsSpecFamily();
    using var repository = Repository();
    repository.Document("effects/unknown-call.yaml", DefaultEffect());
    var tree = repository.Tree();
    Assert.That(() => family.Check(null!, tree).ToList(), Throws.ArgumentNullException);
    Assert.That(() => family.Check(tree.Documents[0], null!).ToList(), Throws.ArgumentNullException);
  }

  [TestCase("single-default", "effects/mmio.yaml(21,22): error PLY0046: EFFECTS.mmio is a second default effect; EFFECTS.unknown-call already is (rule: single-default)")]
  [TestCase("no-default", "effects/pure-read.yaml(21,22): error PLY0046: no effect has defaultForUnknown: true (rule: single-default)")]
  [TestCase("default-is-conservative.yaml", "effects/default-is-conservative.yaml(15,13): error PLY0046: the default effect must have ordering 'strict', found 'free' (rule: default-is-conservative)")]
  [TestCase("volatile-is-strict.yaml", "effects/volatile-is-strict.yaml(16,16): error PLY0046: class 'device-io' requires duplication 'forbidden', found 'allowed' (rule: volatile-is-strict)")]
  [TestCase("write-implies-order.yaml", "effects/write-implies-order.yaml(15,13): error PLY0046: ordering 'free' is not allowed for an effect that writes heap (rule: write-implies-order)")]
  [TestCase("unique-name", "effects/unknown-calls.yaml(13,9): error PLY0046: effect 'unknown-call' is already defined by EFFECTS.unknown-call (rule: unique-name)")]
  [TestCase("layout", "effects/calls/unknown-call.yaml(1,5): error PLY0046: file 'effects/calls/unknown-call.yaml' is not directly in the family folder 'effects' (rule: layout)")]
  [TestCase("layout-id", "effects/unknown-call.yaml(1,5): error PLY0046: id 'EFFECTS.call' does not match file name 'unknown-call.yaml' (rule: layout)")]
  [TestCase("bad-class.yaml", "effects/bad-class.yaml(14,10): error PLY0040: /spec/class:")]
  [TestCase("extra-property.yaml", "effects/extra-property.yaml(23,9): error PLY0040: /spec/pure: property 'pure' is not allowed")]
  public void NegativeFixture_IsRefusedWithItsRule(string fixture, string expected)
    => Assert.That(Fixture(fixture), Has.Some.StartsWith(expected));

  [TestCase("name")]
  [TestCase("class")]
  [TestCase("ordering")]
  [TestCase("duplication")]
  [TestCase("elimination")]
  [TestCase("reads")]
  [TestCase("writes")]
  [TestCase("mayTrap")]
  [TestCase("defaultForUnknown")]
  [TestCase("targets")]
  public void MissingProperty_IsASchemaViolationNamingIt(string property) {
    var fixture = $"missing-{property.ToLowerInvariant()}.yaml";
    Assert.That(Fixture(fixture), Is.EqualTo(new[] {
      $"effects/{fixture}(13,3): error PLY0040: /spec: Required properties [\"{property}\"] are not present"
    }));
  }

  [Test]
  public void EveryRequiredPropertyHasAMissingFixture() {
    using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, SchemaPath)));
    var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(property => $"missing-{property.GetString()!.ToLowerInvariant()}.yaml");
    var fixtures = Directory.EnumerateFiles(Path.Combine(_fixtures, "invalid"), "missing-*.yaml").Select(Path.GetFileName);
    Assert.That(fixtures, Is.EquivalentTo(required));
  }

}
