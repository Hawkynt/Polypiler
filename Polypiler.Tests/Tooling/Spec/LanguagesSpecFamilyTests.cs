using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;
using Polypiler.Tooling.Spec.Families;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary>
/// <see cref="LanguagesSpecFamily"/>: the schema <c>spec/schemas/languages.schema.json</c>, the rules,
/// the seed documents of <c>spec/languages/</c> and the fixtures of <c>tests/fixtures/spec/languages/</c>,
/// each run through the whole engine in a throw-away repository that holds the real schema and the real
/// <c>tooling/toolchains.lock</c>.
/// </summary>
[TestFixture]
public sealed class LanguagesSpecFamilyTests {

  private static readonly string _fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "spec", "languages");

  private static readonly string _validLanguage = Path.Combine(_fixtures, "valid", "languages", "javascript", "language.yaml");

  private static readonly string _validProfile = Path.Combine(_fixtures, "valid", "languages", "javascript", "profiles", "es2022.yaml");

  private static readonly JsonObject _schema = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "languages.schema.json")))!;

  /// <summary>A repository with the family's schema and, unless told otherwise, the toolchain lock.</summary>
  private static SpecRepository Repository(bool withLock = true) {
    var repository = new SpecRepository();
    repository.Write("spec/schemas/languages.schema.json", File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "languages.schema.json")));
    if (withLock)
      repository.Write(ToolchainLock.RepositoryPath, File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tooling", "toolchains.lock")));
    return repository;
  }

  /// <summary>Where a document with this ID belongs: <c>languages/&lt;identifier&gt;/language.yaml</c> or <c>languages/&lt;identifier&gt;/profiles/&lt;profile&gt;.yaml</c>.</summary>
  private static string PathOf(string id) {
    var parts = id.Split('.', 3);
    return parts.Length == 2 ? $"languages/{parts[1]}/language.yaml" : $"languages/{parts[1]}/profiles/{parts[2]}.yaml";
  }

  private static string IdOf(string text) => Regex.Match(text, "^id: (.+)$", RegexOptions.Multiline).Groups[1].Value;

  /// <summary>The documents, each at the place its ID gives it, validated for the family.</summary>
  private static string[] Lines(params string[] documents) {
    using var repository = Repository();
    foreach (var text in documents)
      repository.Document(PathOf(IdOf(text)), text);
    return repository.Lines("languages");
  }

  /// <summary>
  /// A fixture of <c>invalid/</c>: a file at the place its ID gives it, with the valid JavaScript
  /// language beside a profile; a folder as the whole spec tree.
  /// </summary>
  private static string[] Fixture(string name) {
    using var repository = Repository();
    var file = Path.Combine(_fixtures, "invalid", name + ".yaml");
    if (File.Exists(file)) {
      var text = File.ReadAllText(file);
      var id = IdOf(text);
      if (id.Count(character => character == '.') > 1)
        repository.Document("languages/javascript/language.yaml", File.ReadAllText(_validLanguage));
      repository.Document(PathOf(id), text);
    } else {
      var folder = Path.Combine(_fixtures, "invalid", name);
      foreach (var path in Directory.EnumerateFiles(folder, "*.yaml", SearchOption.AllDirectories))
        repository.Document(Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(path));
    }

    return repository.Lines("languages");
  }

  /// <summary>A body line <c>  key: value</c> of a document replaced; <c>constructs</c>, the last key, is replaced with everything after it; <see langword="null"/> removes the key.</summary>
  private static string With(string text, string key, string? value) {
    var pattern = key == "constructs" ? @"^  constructs:(.|\n)*\z" : $"^  {Regex.Escape(key)}: .*$";
    var replacement = value is null ? "" : $"  {key}: {value}" + (key == "constructs" ? "\n" : "");
    Assert.That(Regex.IsMatch(text, pattern, RegexOptions.Multiline), $"the document has no key '{key}'");
    return Regex.Replace(text, pattern, replacement.Replace("$", "$$", StringComparison.Ordinal), RegexOptions.Multiline);
  }

  private static string Language(string key, string? value) => With(File.ReadAllText(_validLanguage), key, value);

  private static string Profile(string key, string? value) => With(File.ReadAllText(_validProfile), key, value);

  private static string ValidLanguage => File.ReadAllText(_validLanguage);

  private static string Constructs(params string[] rows) => "\n" + string.Concat(rows.Select(row => $"    - {{{row}}}\n")).TrimEnd('\n');

  [Test]
  public void Family_IsRegistered() {
    var family = SpecFamilies.All.OfType<LanguagesSpecFamily>().Single();
    Assert.That((family.Folder, family.SchemaPath), Is.EqualTo(("languages", "spec/schemas/languages.schema.json")));
  }

  [Test]
  public void Check_RefusesNullArguments() {
    var family = new LanguagesSpecFamily();
    var document = SpecDocument.Parse("languages/javascript/language.yaml", ValidLanguage);
    using var repository = Repository();
    Assert.That(() => family.Check(null!, repository.Tree()), Throws.ArgumentNullException);
    Assert.That(() => family.Check(document, null!), Throws.ArgumentNullException);
  }

  /// <summary>The documents of <c>spec/languages/</c>: C# with its nine target frameworks, JavaScript with its five levels.</summary>
  [Test]
  public void RepositorySeed_IsValid() {
    var validation = new SpecValidator(SpecFamilies.All).Run(SpecTree.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "spec"), PrototypeFixture.RepositoryRoot), "languages");
    Assert.That(validation.Diagnostics.Select(DiagnosticRenderer.ToText), Is.Empty);
    Assert.That(validation.Documents, Is.EqualTo(16));
  }

  /// <summary>The seed profiles are the <c>--profile</c> values the backends accept.</summary>
  [Test]
  public void RepositorySeed_HasAProfilePerBackendProfile() {
    var tree = SpecTree.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "spec"), PrototypeFixture.RepositoryRoot);
    string[] Profiles(string language) => [.. tree.Documents.Where(document => document.Path.StartsWith($"languages/{language}/profiles/", StringComparison.Ordinal)).Select(document => Path.GetFileNameWithoutExtension(document.Path)).Order(StringComparer.Ordinal)];
    Assert.That(Profiles("csharp"), Is.EquivalentTo(Polypiler.Core.Backends.CSharp.CSharpProfile.All.Select(profile => profile.Name)));
    Assert.That(Profiles("javascript"), Is.EquivalentTo(new[] { "es5", "es2015", "es2017", "es2020", "es2022" }));
  }

  [Test]
  public void ValidFixture_IsACopyOfTheSeed() {
    Assert.That(File.ReadAllText(_validLanguage), Is.EqualTo(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "languages", "javascript", "language.yaml"))));
    Assert.That(File.ReadAllText(_validProfile), Is.EqualTo(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "languages", "javascript", "profiles", "es2022.yaml"))));
  }

  /// <summary>Test design 1: a language with one profile.</summary>
  [Test]
  public void ValidFixture_Passes() {
    using var repository = Repository();
    repository.Document("languages/javascript/language.yaml", ValidLanguage);
    repository.Document("languages/javascript/profiles/es2022.yaml", File.ReadAllText(_validProfile));
    var validation = repository.Validate("languages");
    Assert.That((validation.Documents, validation.Diagnostics.Count), Is.EqualTo((2, 0)));
  }

  /// <summary>Test design 9: a language with no profile.</summary>
  [Test]
  public void LanguageWithoutProfiles_IsValid() => Assert.That(Lines(ValidLanguage), Is.Empty);

  /// <summary>Test design 2: a property of a profile in a language is a schema violation.</summary>
  [Test]
  public void ProfilePropertyOnLanguage_IsPLY0040()
    => Assert.That(Fixture("profile-property-on-language"), Has.Some.Contains("error PLY0040: /spec/toolchain:"));

  [Test]
  public void LanguagePropertyOnProfile_IsPLY0040()
    => Assert.That(Lines(ValidLanguage, Profile("kind", "profile\n  backlogCode: JS")), Has.Some.Contains("error PLY0040: /spec/backlogCode:"));

  public static IEnumerable<TestCaseData> EnumerationValues() {
    var properties = (JsonObject)_schema["properties"]!;
    foreach (var (key, schema) in properties.Where(pair => pair.Key != "kind" && pair.Value!["enum"] is not null))
      foreach (var value in schema!["enum"]!.AsArray())
        yield return new TestCaseData(key, (string)value!).SetName($"{key} {value}");

    foreach (var value in _schema["$defs"]!["overflow"]!["enum"]!.AsArray())
      foreach (var half in new[] { "signed", "unsigned" })
        yield return new TestCaseData("integerOverflow", half == "signed" ? $"{{signed: {value}, unsigned: wrap}}" : $"{{signed: wrap, unsigned: {value}}}").SetName($"integerOverflow {half} {value}");
  }

  /// <summary>Test design 3: every value of every enumeration of a profile is valid.</summary>
  [TestCaseSource(nameof(EnumerationValues))]
  public void EnumerationValue_IsValid(string key, string value) => Assert.That(Lines(ValidLanguage, Profile(key, value)), Is.Empty);

  /// <summary>The enumerations and how many values each declares, as the test design counts them.</summary>
  [Test]
  public void EnumerationValues_AreTheDeclaredOnes() {
    var counts = ((JsonObject)_schema["properties"]!).Where(pair => pair.Value!["enum"] is not null).ToDictionary(pair => pair.Key, pair => pair.Value!["enum"]!.AsArray().Count);
    counts["overflow"] = _schema["$defs"]!["overflow"]!["enum"]!.AsArray().Count;
    Assert.That(counts, Is.EquivalentTo(new Dictionary<string, int> {
      ["kind"] = 2, ["evaluationOrder"] = 4, ["overflow"] = 6, ["divisionRounding"] = 4, ["remainderSign"] = 3, ["shiftAmount"] = 4,
      ["stringEncoding"] = 5, ["exceptions"] = 4, ["memoryModel"] = 4
    }));
  }

  [TestCase("evaluationOrder", "top-down")]
  [TestCase("integerOverflow", "{signed: saturate, unsigned: wrap}")]
  [TestCase("integerOverflow", "{signed: wrap, unsigned: saturate}")]
  [TestCase("divisionRounding", "round")]
  [TestCase("remainderSign", "positive")]
  [TestCase("shiftAmount", "rotate")]
  [TestCase("stringEncoding", "utf-32")]
  [TestCase("exceptions", "signals")]
  [TestCase("memoryModel", "garbage-collected")]
  [TestCase("kind", "dialect")]
  public void UnknownEnumerationValue_IsPLY0040(string key, string value)
    => Assert.That(Lines(ValidLanguage, Profile(key, value)), Has.Some.Contains($"error PLY0040: /spec/{key}"));

  /// <summary>Test design 4: the four combinations of state and diagnostic.</summary>
  [TestCase("construct: a, family: scalars, state: supported", null)]
  [TestCase("construct: a, family: scalars, state: diagnosed, diagnostic: PLY1102", null)]
  [TestCase("construct: a, family: scalars, state: diagnosed", "javascript/profiles/es2022.yaml(27,7): error PLY0046: construct 'a' is diagnosed but names no diagnostic (rule: diagnosed-needs-code)")]
  [TestCase("construct: a, family: scalars, state: supported, diagnostic: PLY1102", "javascript/profiles/es2022.yaml(27,69): error PLY0046: construct 'a' is supported but names diagnostic PLY1102 (rule: diagnosed-needs-code)")]
  public void ConstructState(string row, string? expected)
    => Assert.That(Lines(ValidLanguage, Profile("constructs", Constructs(row))), expected is null ? Is.Empty : Is.EqualTo(new[] { "languages/" + expected }));

  /// <summary>Test design 5: identifiers with digits and hyphens are valid, upper case and other characters are not.</summary>
  [TestCase("cbmbasic", true)]
  [TestCase("c", true)]
  [TestCase("x86-16", true)]
  [TestCase("CSharp", false)]
  [TestCase("c++", false)]
  public void IdentifierForm(string identifier, bool valid) {
    using var repository = Repository();
    var text = ValidLanguage.Replace("LANGUAGES.javascript", $"LANGUAGES.{identifier}", StringComparison.Ordinal).Replace("identifier: javascript", $"identifier: \"{identifier}\"", StringComparison.Ordinal);
    repository.Document($"languages/{identifier}/language.yaml", text);
    var lines = repository.Lines("languages");
    Assert.That(lines, valid ? Is.Empty : Has.Some.Contains("error PLY0040"));
  }

  /// <summary>Test design 6: the first and last block are valid; below, above and between multiples of 100 are not.</summary>
  [TestCase(900, false)]
  [TestCase(1000, true)]
  [TestCase(2900, true)]
  [TestCase(3000, false)]
  [TestCase(1050, false)]
  public void DiagnosticBlockBoundary(int block, bool valid) {
    var lines = Lines(Language("diagnosticBlock", block.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    Assert.That(lines, valid ? Is.Empty : Has.Some.Contains("error PLY0040: /spec/diagnosticBlock:"));
  }

  /// <summary>Test design 7: the frontend range of JavaScript is PLY1100 to PLY1149.</summary>
  [TestCase("PLY1100", true)]
  [TestCase("PLY1149", true)]
  [TestCase("PLY1150", false)]
  [TestCase("PLY1099", false)]
  public void ConstructDiagnosticBoundary(string code, bool valid) {
    var lines = Lines(ValidLanguage, Profile("constructs", Constructs($"construct: a, family: control, state: diagnosed, diagnostic: {code}")));
    Assert.That(lines, valid ? Is.Empty : Is.EqualTo(new[] { $"languages/javascript/profiles/es2022.yaml(27,69): error PLY0046: diagnostic {code} is outside the frontend range PLY1100-PLY1149 of javascript (rule: diagnostic-in-block)" }));
  }

  /// <summary>Without a valid diagnostic block in the language there is no range to check against; the language's own problem is reported there.</summary>
  [TestCase("diagnosticBlock", "\"1100\"")]
  [TestCase("diagnosticBlock", null)]
  public void ConstructDiagnosticWithoutBlock_IsNotChecked(string key, string? value) {
    var lines = Lines(Language(key, value), Profile("constructs", Constructs("construct: a, family: control, state: diagnosed, diagnostic: PLY1250")));
    Assert.That(lines, Has.Length.EqualTo(1).And.All.StartWith("languages/javascript/language.yaml"));
  }

  [Test]
  public void ConstructDiagnosticOfALanguageWithoutBody_IsNotChecked() {
    var language = ValidLanguage[..ValidLanguage.IndexOf("spec:", StringComparison.Ordinal)];
    var lines = Lines(language, Profile("constructs", Constructs("construct: a, family: control, state: diagnosed, diagnostic: PLY1250")));
    Assert.That(lines, Has.Length.EqualTo(1).And.All.StartWith("languages/javascript/language.yaml"));
  }

  /// <summary>Test design 8: no construct, one construct, the same construct twice.</summary>
  [Test]
  public void NoConstructs_IsValid() => Assert.That(Lines(ValidLanguage, Profile("constructs", "[]")), Is.Empty);

  [Test]
  public void OneConstruct_IsValid() => Assert.That(Lines(ValidLanguage, Profile("constructs", Constructs("construct: a, family: types, state: supported"))), Is.Empty);

  [Test]
  public void ConstructListedTwice_IsReportedAtTheSecond()
    => Assert.That(Fixture("unique-construct"), Is.EqualTo(new[] { "languages/javascript/profiles/es2022.yaml(28,19): error PLY0046: construct 'with statement' is listed twice (rule: unique-construct)" }));

  /// <summary>Test designs 10 to 14 and 17: one fixture per rule.</summary>
  [TestCase("diagnosed-needs-code", "languages/javascript/profiles/es2022.yaml(27,7): error PLY0046: construct 'with statement' is diagnosed but names no diagnostic (rule: diagnosed-needs-code)")]
  [TestCase("supported-with-code", "languages/javascript/profiles/es2022.yaml(27,81): error PLY0046: construct 'typed arrays' is supported but names diagnostic PLY1102 (rule: diagnosed-needs-code)")]
  [TestCase("diagnostic-in-block", "languages/javascript/profiles/es2022.yaml(27,84): error PLY0046: diagnostic PLY1250 is outside the frontend range PLY1100-PLY1149 of javascript (rule: diagnostic-in-block)")]
  [TestCase("toolchain-known", "languages/javascript/profiles/es2022.yaml(16,14): error PLY0046: toolchain 'nodejs' is not a key of tooling/toolchains.lock (rule: toolchain-known)")]
  [TestCase("unique-identifier", "languages/typescript/language.yaml(17,20): error PLY0046: diagnosticBlock 1100 is already used by LANGUAGES.javascript (rule: unique-identifier)")]
  [TestCase("profile-has-language", "languages/javascript/profiles/es2022.yaml(14,13): error PLY0046: profile in folder 'javascript' names language 'LANGUAGES.typescript' (rule: profile-has-language)")]
  [TestCase("profile-without-language", "languages/javascript/profiles/es2022.yaml(14,13): error PLY0043: unknown spec id 'LANGUAGES.javascript'")]
  [TestCase("layout", "languages/javascript.yaml(1,5): error PLY0046: path 'languages/javascript.yaml' is neither languages/<identifier>/language.yaml nor languages/<identifier>/profiles/<profile>.yaml (rule: layout)")]
  [TestCase("layout-id", "languages/javascript/language.yaml(1,5): error PLY0046: id 'LANGUAGES.js' does not match folder 'javascript', expected 'LANGUAGES.javascript' (rule: layout)")]
  [TestCase("layout-kind", "languages/javascript/language.yaml(13,9): error PLY0046: language.yaml holds kind 'profile', expected 'language' (rule: layout)")]
  [TestCase("layout-identifier", "languages/javascript/language.yaml(14,15): error PLY0046: identifier 'js' does not match folder 'javascript' (rule: layout)")]
  public void RuleFixture_IsReported(string fixture, string expected) => Assert.That(Fixture(fixture), Is.EqualTo(new[] { expected }));

  [Test]
  public void BacklogCodeUsedTwice_IsUniqueIdentifier() {
    var other = ValidLanguage.Replace("javascript", "typescript", StringComparison.Ordinal).Replace("diagnosticBlock: 1100", "diagnosticBlock: 1900", StringComparison.Ordinal);
    Assert.That(Lines(ValidLanguage, other), Is.EqualTo(new[] { "languages/typescript/language.yaml(16,16): error PLY0046: backlogCode 'JS' is already used by LANGUAGES.javascript (rule: unique-identifier)" }));
  }

  /// <summary>A profile of another language, or a document without a body, earlier in path order is no language to compare with.</summary>
  [Test]
  public void EarlierDocumentsThatAreNoLanguage_AreNotCompared() {
    using var repository = Repository();
    repository.Document("languages/csharp/profiles/net10.0.yaml", File.ReadAllText(_validProfile).Replace("javascript", "csharp", StringComparison.Ordinal).Replace("es2022", "net10.0", StringComparison.Ordinal).Replace("PLY1102", "PLY1002", StringComparison.Ordinal));
    repository.Document("languages/csharp/language.yaml", ValidLanguage.Replace("javascript", "csharp", StringComparison.Ordinal).Replace("JS", "CS", StringComparison.Ordinal).Replace("1100", "1000", StringComparison.Ordinal));
    repository.Document("languages/broken/language.yaml", ValidLanguage[..ValidLanguage.IndexOf("spec:", StringComparison.Ordinal)].Replace("javascript", "broken", StringComparison.Ordinal));
    repository.Document("languages/javascript/language.yaml", ValidLanguage);
    Assert.That(repository.Lines("languages"), Has.Length.EqualTo(1).And.All.StartWith("languages/broken/language.yaml"));
  }

  /// <summary>A document of another family is never compared, even where it comes first.</summary>
  [Test]
  public void DocumentsOfOtherFamilies_AreNotCompared() {
    using var repository = Repository();
    repository.Document("demo/widget.yaml", SpecText.Document());
    repository.Document("languages/javascript/language.yaml", ValidLanguage);
    Assert.That(repository.Lines(), Is.Empty);
  }

  /// <summary>An earlier language that lacks the compared value shares nothing with a later one.</summary>
  [Test]
  public void EarlierLanguageWithoutTheValues_IsNotAConflict() {
    var earlier = With(With(ValidLanguage, "backlogCode", null), "diagnosticBlock", null).Replace("javascript", "basic", StringComparison.Ordinal);
    Assert.That(Lines(earlier, ValidLanguage), Has.Length.EqualTo(1).And.All.StartWith("languages/basic/language.yaml"));
  }

  /// <summary>Without a readable lock no toolchain can be confirmed, and the reason says so.</summary>
  [Test]
  public void MissingLock_ReportsTheToolchainAsUncheckable() {
    using var repository = Repository(withLock: false);
    repository.Document("languages/javascript/language.yaml", ValidLanguage);
    repository.Document("languages/javascript/profiles/es2022.yaml", File.ReadAllText(_validProfile));
    Assert.That(repository.Lines("languages"), Has.Length.EqualTo(1).And.All.Contains("error PLY0046: toolchain 'node' cannot be checked: tooling/toolchains.lock is not a valid toolchain lock (cannot read the toolchain lock:").And.All.EndsWith("(rule: toolchain-known)"));
  }

  /// <summary>Every property the schema requires, at every level.</summary>
  private static IEnumerable<(string Fixture, string Pointer, string Property)> RequiredProperties() {
    var properties = (JsonObject)_schema["properties"]!;
    foreach (var branch in _schema["allOf"]!.AsArray())
      foreach (var property in branch!["then"]!["required"]!.AsArray())
        yield return ($"missing-{property}", "/spec", (string)property!);
    yield return ("missing-kind", "/spec", "kind");
    foreach (var property in properties["integerOverflow"]!["required"]!.AsArray())
      yield return ($"missing-integerOverflow-{property}", "/spec/integerOverflow", (string)property!);
    foreach (var property in properties["constructs"]!["items"]!["required"]!.AsArray())
      yield return ($"missing-constructs-{property}", "/spec/constructs/0", (string)property!);
  }

  public static IEnumerable<TestCaseData> MissingPropertyCases()
    => RequiredProperties().Select(required => new TestCaseData(required.Fixture, required.Pointer, required.Property).SetName(required.Fixture));

  /// <summary>Test design 15: one fixture per required property, refused with the JSON pointer of the object that lacks it.</summary>
  [TestCaseSource(nameof(MissingPropertyCases))]
  public void MissingProperty_IsPLY0040(string fixture, string pointer, string property)
    => Assert.That(Fixture(fixture), Has.Some.Contains($"error PLY0040: {pointer}: Required properties [\"{property}\"] are not present"));

  /// <summary>Every missing-*.yaml fixture stands for a property the schema requires, and every required property has one.</summary>
  [Test]
  public void MissingFixtures_AreExactlyTheRequiredProperties() {
    var fixtures = Directory.EnumerateFiles(Path.Combine(_fixtures, "invalid"), "missing-*.yaml").Select(Path.GetFileNameWithoutExtension);
    Assert.That(fixtures, Is.EquivalentTo(RequiredProperties().Select(required => required.Fixture)));
  }

  /// <summary>Test design 16: a property the schema does not know.</summary>
  [Test]
  public void ExtraProperty_IsPLY0040()
    => Assert.That(Fixture("extra-property"), Is.EqualTo(new[] { "languages/javascript/language.yaml(18,10): error PLY0040: /spec/extra: property 'extra' is not allowed" }));

}
