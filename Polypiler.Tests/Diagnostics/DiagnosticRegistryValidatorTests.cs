using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Diagnostics;

namespace Polypiler.Tests.Diagnostics;

/// <summary>The rules of <c>polypiler-tooling diagnostics validate</c>, on the fixtures and on registries written here.</summary>
[TestFixture]
public sealed class DiagnosticRegistryValidatorTests {

  private const string Blocks = """
    schema: 1
    blocks:
      - { first: PLY0001, last: PLY0099, area: cli }
      - { first: PLY0100, last: PLY0199, area: ir }
    """;

  private static string Fixture(string name) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "diagnostics", name);

  private static string[] Problems(DiagnosticRegistry registry) => [.. registry.Problems.Select(DiagnosticRenderer.ToText)];

  private static string[] Problems(string yaml) => Problems(DiagnosticRegistryValidator.Validate(yaml, "r.yaml"));

  /// <summary>A registry of the blocks above and one code with the given lines.</summary>
  private static string[] Code(string lines) => Problems(Blocks + "\ncodes:\n  - " + lines.Replace("\n", "\n    ", System.StringComparison.Ordinal) + "\n");

  /// <summary>Test design 1: the repository's registry is valid.</summary>
  [Test]
  public void RepositoryRegistry_IsValid() {
    var registry = DiagnosticRegistryValidator.Validate(Path.Combine(PrototypeFixture.RepositoryRoot, DiagnosticRegistry.RepositoryPath));
    Assert.That(Problems(registry), Is.Empty);
    Assert.That(registry.Codes, Is.Not.Empty);
  }

  /// <summary>Every category of PRD §10.1 has a registered code that stands for it.</summary>
  [Test]
  public void RepositoryRegistry_CoversEveryExitCategory() {
    var registry = DiagnosticRegistryValidator.Validate(Path.Combine(PrototypeFixture.RepositoryRoot, DiagnosticRegistry.RepositoryPath));
    var covered = registry.Codes.Select(entry => entry.ExitCode).OfType<ExitCode>().ToHashSet();
    Assert.That(System.Enum.GetValues<ExitCode>().Where(member => member != ExitCode.Success), Is.SubsetOf(covered));
  }

  /// <summary>Test design 8: the first and the last code of a block, a nested block, every severity.</summary>
  [Test]
  public void ValidFixture_IsValid() {
    var registry = DiagnosticRegistryValidator.Validate(Fixture("valid.yaml"));
    Assert.That(Problems(registry), Is.Empty);
    Assert.That(registry.Blocks, Has.Count.EqualTo(4));
    Assert.That(registry.Codes.Select(entry => entry.Code), Is.EqualTo(new[] { "PLY0001", "PLY0009", "PLY0099", "PLY1002", "PLY2900" }));
    Assert.That(registry.Find("PLY1002"), Is.EqualTo(new DiagnosticEntry(
      "PLY1002", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "C# construct not supported", "'<construct>' is not supported yet",
      "csharp.subset", "csharp", "PROTOTYPE", "standard error of polypiler translate --from csharp", false, false, false)));
    Assert.That(registry.Find("PLY0009"), Has.Property("Strict").True.And.Property("Reserved").True.And.Property("ExitCode").Null);
    Assert.That(registry.Find("PLY0099")!.Severity, Is.EqualTo(DiagnosticSeverity.Info));
    Assert.That(registry.Find("PLY2900"), Has.Property("Retired").True.And.Property("Area").EqualTo("languages"));
    Assert.That(registry.Find("PLY0002"), Is.Null);
    var entry = registry.Find("PLY0001")!;
    Assert.That((entry.Title, entry.Message, entry.Owner), Is.EqualTo(("internal compiler error", "internal compiler error: <exception>", "ADR-DIAGNOSTICS")));
  }

  /// <summary>Test design 13: a duplicate code.</summary>
  [Test]
  public void Duplicate_IsRefused()
    => Assert.That(Problems(DiagnosticRegistryValidator.Validate(Fixture("duplicate.yaml"))),
      Is.EqualTo(new[] { $"{Fixture("duplicate.yaml")}(14,5): error PLY0045: duplicate code PLY0030" }));

  /// <summary>Test design 9: a code in the block of another area, the first code of a block just outside the last one.</summary>
  [Test]
  public void WrongBlock_IsRefused()
    => Assert.That(Problems(DiagnosticRegistryValidator.Validate(Fixture("wrong-block.yaml"))).Select(Message), Is.EqualTo(new[] {
      "PLY0100 is registered for area 'cli' but lies in the block of 'ir'",
      "PLY0150 is registered for area 'cli' but lies in the block of 'ir'"
    }));

  /// <summary>Test design 10: a code below every block, and a code with five digits.</summary>
  [Test]
  public void BadCode_IsRefused()
    => Assert.That(Problems(DiagnosticRegistryValidator.Validate(Fixture("bad-code.yaml"))).Select(Message), Is.EqualTo(new[] {
      "PLY0000 lies in no block",
      "'PLY10000' is not a diagnostic code"
    }));

  /// <summary>Test design 14: an error without an exit code, a warning with one.</summary>
  [TestCase("error-without-exit.yaml", "PLY0030: an error needs an exit code")]
  [TestCase("warning-with-exit.yaml", "PLY0009: only errors carry an exit code")]
  public void ExitCodeOnlyOnErrors(string fixture, string message)
    => Assert.That(Problems(DiagnosticRegistryValidator.Validate(Fixture(fixture))).Select(Message), Is.EqualTo(new[] { message }));

  /// <summary>Test design 15: a file that is missing, or not YAML.</summary>
  [Test]
  public void MissingFile_CannotBeRead() {
    var path = Fixture("no-such-registry.yaml");
    var registry = DiagnosticRegistryValidator.Validate(path);
    Assert.That(Problems(registry), Has.Length.EqualTo(1));
    Assert.That(Problems(registry)[0], Does.StartWith($"polypiler: error PLY0045: cannot read registry '{path}': "));
    Assert.That((registry.Blocks.Count, registry.Codes.Count, registry.File), Is.EqualTo((0, 0, path)));
  }

  [TestCase("schema: [1\n", "r.yaml(2,1): error PLY0045: cannot read registry 'r.yaml': ")]
  [TestCase("- 1\n", "r.yaml(1,1): error PLY0045: cannot read registry 'r.yaml': not a YAML mapping")]
  [TestCase("", "polypiler: error PLY0045: cannot read registry 'r.yaml': not a YAML mapping")]
  [TestCase("schema: 1\nschema: 1\n", "r.yaml(2,1): error PLY0045: cannot read registry 'r.yaml': Duplicate key")]
  [TestCase("a: 1\n---\nb: 2\n", "polypiler: error PLY0045: cannot read registry 'r.yaml': not a YAML mapping")]
  public void NotARegistryDocument(string yaml, string start) {
    var problems = Problems(yaml);
    Assert.That(problems, Has.Length.EqualTo(1));
    Assert.That(problems[0], Does.StartWith(start));
  }

  [Test]
  public void RootProperties() => Assert.That(Problems("schema: 2\nextra: 1\n").Select(Message), Is.EqualTo(new[] {
    "unknown property 'extra' in the registry",
    "missing property 'blocks' in the registry",
    "missing property 'codes' in the registry",
    "unsupported schema '2'"
  }));

  [Test]
  public void SchemaIsRequired() => Assert.That(Problems("blocks: []\ncodes: []\n? [a]\n: 1\n").Select(Message), Is.EqualTo(new[] {
    "unknown property '' in the registry",
    "missing property 'schema' in the registry"
  }));

  [Test]
  public void ACodeWithoutSeverity_IsAnErrorWithoutExitCode() => Assert.That(Code("code: PLY0010\ntitle: t\nmessage: m\narea: cli\nowner: o").Select(Message), Is.EqualTo(new[] {
    "missing property 'severity' in a code",
    "PLY0010: an error needs an exit code"
  }));

  [Test]
  public void ListsMustBeLists() => Assert.That(Problems("schema: 1\nblocks: x\ncodes: { a: 1 }\n").Select(Message), Is.EqualTo(new[] {
    "property 'blocks' must be a list",
    "property 'codes' must be a list"
  }));

  [Test]
  public void BlockRules() => Assert.That(Problems("""
    schema: 1
    blocks:
      - x
      - { first: PLY0001 }
      - { first: PLY1, last: PLY2, area: a }
      - { first: PLY0099, last: PLY0001, area: a }
      - { first: PLY0001, last: PLY0099, area: a }
      - { first: PLY0050, last: PLY0150, area: b }
      - { first: PLY0001, last: PLY0099, area: c }
      - { first: PLY0010, last: PLY0019, area: d }
    codes: []
    """).Select(Message), Is.EqualTo(new[] {
      "a block must be a mapping",
      "missing property 'last' in a block",
      "missing property 'area' in a block",
      "'PLY1' is not a diagnostic code",
      "'PLY2' is not a diagnostic code",
      "block PLY0099-PLY0001 ends before it starts",
      "block PLY0050-PLY0150 of 'b' overlaps block PLY0001-PLY0099 of 'a'",
      "block PLY0001-PLY0099 of 'c' overlaps block PLY0001-PLY0099 of 'a'",
      "block PLY0001-PLY0099 of 'c' overlaps block PLY0050-PLY0150 of 'b'"
    }));

  [Test]
  public void ACodeMustBeAMapping() => Assert.That(Problems(Blocks + "\ncodes: [ x ]\n").Select(Message), Is.EqualTo(new[] { "a code must be a mapping" }));

  [Test]
  public void CodeProperties() => Assert.That(Code("code: PLY0010\nseverity: info\ncolour: red").Select(Message), Is.EqualTo(new[] {
    "unknown property 'colour' in a code",
    "missing property 'title' in a code",
    "missing property 'message' in a code",
    "missing property 'area' in a code",
    "missing property 'owner' in a code"
  }));

  [Test]
  public void ACodeWithoutCode_IsSkipped() => Assert.That(Code("severity: info\ntitle: t\nmessage: m\narea: cli\nowner: o").Select(Message), Is.EqualTo(new[] { "missing property 'code' in a code" }));

  [Test]
  public void ValuesMustBeScalars() => Assert.That(Code("code: PLY0010\nseverity: info\ntitle: [t]\nmessage: m\narea: cli\nowner: o").Select(Message), Is.EqualTo(new[] { "PLY0010: 'title' must be a scalar" }));

  [TestCase("severity: fatal\nexit: UsageError", "PLY0010: unknown severity 'fatal'")]
  [TestCase("severity: error\nexit: Usage", "PLY0010: 'Usage' is not an exit code")]
  [TestCase("severity: error\nexit: 2", "PLY0010: '2' is not an exit code")]
  [TestCase("severity: info\nstrict: true", "PLY0010: only warnings can be strict")]
  [TestCase("severity: error\nexit: UsageError\nstrict: true", "PLY0010: only warnings can be strict")]
  [TestCase("severity: warning\nstrict: yes", "PLY0010: 'strict' must be true or false")]
  [TestCase("severity: warning\nreserved: 1", "PLY0010: 'reserved' must be true or false")]
  [TestCase("severity: warning\nretired: on", "PLY0010: 'retired' must be true or false")]
  public void FieldRules(string fields, string message)
    => Assert.That(Code($"code: PLY0010\n{fields}\ntitle: t\nmessage: m\narea: cli\nowner: o").Select(Message), Is.EqualTo(new[] { message }));

  [Test]
  public void FalseFlags_AreAccepted() {
    var registry = DiagnosticRegistryValidator.Validate(Blocks + "\ncodes:\n  - { code: PLY0010, severity: warning, strict: false, reserved: false, retired: false, title: t, message: m, area: cli, owner: o }\n", "r.yaml");
    Assert.That(Problems(registry), Is.Empty);
    Assert.That(registry.Find("PLY0010"), Has.Property("Strict").False.And.Property("Reserved").False.And.Property("Retired").False.And.Property("Rule").Null.And.Property("Surfaces").Null);
  }

  [Test]
  public void Codes_AreAscending() => Assert.That(Problems(Blocks + """

    codes:
      - { code: PLY0020, severity: info, title: t, message: m, area: cli, owner: o }
      - { code: PLY0010, severity: info, title: t, message: m, area: cli, owner: o }
      - { code: PLY0030, severity: info, title: t, message: m, area: cli, owner: o }
      - { code: PLY0020, severity: info, title: t, message: m, area: cli, owner: o }
    """).Select(Message), Is.EqualTo(new[] {
      "PLY0010 follows PLY0020; codes are listed in ascending order",
      "duplicate code PLY0020"
    }));

  /// <summary>The message of a rendered problem, without position, severity and code.</summary>
  private static string Message(string line) => line[(line.IndexOf("PLY0045: ", System.StringComparison.Ordinal) + "PLY0045: ".Length)..];

}
