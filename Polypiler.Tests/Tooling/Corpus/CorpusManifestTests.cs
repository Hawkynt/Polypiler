using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Corpus;

namespace Polypiler.Tests.Tooling.Corpus;

/// <summary><see cref="CorpusManifest"/>: the repository manifest, every field rule and every cross-check.</summary>
[TestFixture]
public sealed class CorpusManifestTests {

  private const string Commit = "0123456789abcdef0123456789abcdef01234567";

  private static string Repository(string name = "o/r", string commit = Commit, string licenceFiles = "[LICENSE]") => $"""
      - name: {name}
        url: https://example.org/{name}.git
        commit: '{commit}'
        licence: MIT
        licenceFiles: {licenceFiles}

    """;

  private static string Fixture(
    string id = "a.b",
    string repository = "o/r",
    string path = "src/a.js",
    string dependencies = "[]",
    string run = "[node, '{file}']",
    string vectors = "{ kind: embedded, source: inline }",
    string determinism = "deterministic",
    string tolerance = "exact",
    string extra = ""
  ) => $"""
      - id: {id}
        repository: {repository}
        path: '{path}'
        export: main
        dependencies: {dependencies}
        run: {run}
        vectors: {vectors}
        licence: MIT
        determinism: {determinism}
        tolerance: {tolerance}
        profiles: [es2022]

    """ + extra;

  private static string Manifest(string repositories, string fixtures) => $"schema: 1\nrepositories:\n{repositories}fixtures:\n{fixtures}";

  private static CorpusManifest Parse(string text) => CorpusManifest.Parse(text, "corpus.yaml");

  private static string[] Problems(string text) {
    var exception = Assert.Throws<CorpusException>(() => Parse(text))!;
    Assert.That(exception.Diagnostics.Select(diagnostic => diagnostic.Code), Is.All.EqualTo(CorpusDiagnostics.ManifestInvalid));
    return [.. exception.Diagnostics.Select(diagnostic => diagnostic.Message)];
  }

  [Test]
  public void ValidManifest_IsRead() {
    var manifest = Parse(Manifest(Repository(), Fixture(dependencies: "[lib/x.js]", vectors: "{ kind: file, source: vectors/a.json }")));
    var repository = manifest.Repositories.Single();
    Assert.That((repository.Name, repository.Commit, repository.Licence, repository.Folder), Is.EqualTo(("o/r", Commit, "MIT", $"o/r@{Commit}")));
    Assert.That(repository.LicenceFiles, Is.EqualTo(new[] { "LICENSE" }));
    var fixture = manifest.Fixture("a.b")!;
    Assert.That(fixture.Files, Is.EqualTo(new[] { "src/a.js", "lib/x.js", "vectors/a.json" }));
    Assert.That((fixture.Export, fixture.Licence, fixture.Determinism, fixture.Tolerance), Is.EqualTo(("main", "MIT", "deterministic", "exact")));
    Assert.That(fixture.Run, Is.EqualTo(new[] { "node", "{file}" }));
    Assert.That(fixture.Profiles, Is.EqualTo(new[] { "es2022" }));
    Assert.That(manifest.Fixture("x.y"), Is.Null);
    Assert.That(manifest.Repository("x/y"), Is.Null);
    Assert.That(manifest.File, Is.EqualTo("corpus.yaml"));
  }

  [TestCase("embedded", "inline", new[] { "src/a.js" })]
  [TestCase("published", "RFC 1950", new[] { "src/a.js" })]
  [TestCase("file", "v/a.json", new[] { "src/a.js", "v/a.json" })]
  public void VectorKind_DecidesWhetherTheSourceIsAFile(string kind, string source, string[] files)
    => Assert.That(Parse(Manifest(Repository(), Fixture(vectors: $"{{ kind: {kind}, source: '{source}' }}"))).Fixtures[0].Files, Is.EqualTo(files));

  [Test]
  public void EmptyLists_AreAccepted() {
    var manifest = Parse(Manifest(Repository(licenceFiles: "[]"), "  []\n"));
    Assert.That(manifest.Fixtures, Is.Empty);
    Assert.That(manifest.Repositories[0].LicenceFiles, Is.Empty);
  }

  [TestCase("exact")]
  [TestCase("declared")]
  [TestCase("VERIFICATION.tolerance.ulp-1")]
  public void Tolerance_OfEachForm_IsAccepted(string tolerance)
    => Assert.That(Parse(Manifest(Repository(), Fixture(tolerance: tolerance))).Fixtures[0].Tolerance, Is.EqualTo(tolerance));

  [Test]
  public void Determinism_Seeded_IsAccepted()
    => Assert.That(Parse(Manifest(Repository(), Fixture(determinism: "seeded"))).Fixtures[0].Determinism, Is.EqualTo("seeded"));

  [TestCase("0123456789abcdef0123456789abcdef0123456")]
  [TestCase("0123456789abcdef0123456789abcdef012345678")]
  [TestCase("0123456789ABCDEF0123456789ABCDEF01234567")]
  public void Commit_NotFortyLowerHexDigits_IsRefused(string commit)
    => Assert.That(Problems(Manifest(Repository(commit: commit), "  []\n")), Is.EqualTo(new[] {
      $"corpus manifest invalid: property 'commit' of repository 'o/r' is '{commit}', expected 40 lower-case hexadecimal digits"
    }));

  [Test]
  public void Commit_OfFortyLowerHexDigits_IsAccepted()
    => Assert.That(Parse(Manifest(Repository(commit: "f".PadRight(40, 'f')), "  []\n")).Repositories[0].Commit, Has.Length.EqualTo(40));

  [TestCase("../x", "contains '..'")]
  [TestCase("a/../x", "contains '..'")]
  [TestCase("/etc/passwd", "is absolute")]
  [TestCase("C:/x", "is absolute")]
  [TestCase("\\\\server\\x", "is absolute")]
  [TestCase("a\\\\b", "contains a backslash")]
  [TestCase("a//b", "has an empty or '.' segment")]
  [TestCase("./a", "has an empty or '.' segment")]
  public void Path_Traversing_IsRefused(string path, string reason) {
    var yamlPath = path.Replace("\\\\", "\\");
    Assert.That(Problems(Manifest(Repository(), Fixture(path: yamlPath))), Is.EqualTo(new[] { $"corpus manifest invalid: path '{yamlPath}' of fixture 'a.b' {reason}" }));
  }

  [Test]
  public void PathProblem_OfTheEmptyPath_IsReported() => Assert.That(CorpusManifest.PathProblem(""), Is.EqualTo("is empty"));

  [Test]
  public void DependencyAndLicenceFileAndVectorFile_AreCheckedAsPaths() {
    Assert.That(Problems(Manifest(Repository(), Fixture(dependencies: "['../d.js']"))), Does.Contain("corpus manifest invalid: path '../d.js' of fixture 'a.b' contains '..'"));
    Assert.That(Problems(Manifest(Repository(licenceFiles: "[/LICENSE]"), "  []\n")), Does.Contain("corpus manifest invalid: path '/LICENSE' of repository 'o/r' is absolute"));
    Assert.That(Problems(Manifest(Repository(), Fixture(vectors: "{ kind: file, source: '../v.json' }"))), Does.Contain("corpus manifest invalid: path '../v.json' of fixture 'a.b' contains '..'"));
  }

  [Test]
  public void DuplicateFixtureId_IsRefused()
    => Assert.That(Problems(Manifest(Repository(), Fixture() + Fixture(path: "src/b.js"))), Is.EqualTo(new[] { "corpus manifest invalid: fixture ID 'a.b' is duplicated" }));

  [Test]
  public void DuplicateRepository_IsRefused()
    => Assert.That(Problems(Manifest(Repository() + Repository(), "  []\n")), Is.EqualTo(new[] { "corpus manifest invalid: repository 'o/r' is declared twice" }));

  [Test]
  public void UndeclaredRepository_IsRefusedWithItsPosition() {
    var exception = Assert.Throws<CorpusException>(() => Parse(Manifest(Repository(), Fixture(id: "x.y", repository: "Hawkynt/Unknown"))))!;
    var diagnostic = exception.Diagnostics.Single();
    Assert.That(DiagnosticRenderer.ToText(diagnostic), Is.EqualTo("corpus.yaml(9,5): error PLY0026: corpus manifest invalid: fixture 'x.y' names undeclared repository 'Hawkynt/Unknown'"));
    Assert.That(exception.Message, Does.Contain("PLY0026"));
  }

  [TestCase("A.b")]
  [TestCase("ab")]
  [TestCase("a.")]
  [TestCase("a_b.c")]
  public void FixtureId_OutsideThePattern_IsRefused(string id)
    => Assert.That(Problems(Manifest(Repository(), Fixture(id: $"'{id}'"))).Single(), Does.StartWith($"corpus manifest invalid: property 'id' of fixture '{id}' is '{id}', expected"));

  [TestCase("owner")]
  [TestCase("a/b/c")]
  public void RepositoryName_NotOwnerSlashRepo_IsRefused(string name)
    => Assert.That(Problems(Manifest(Repository(name: name), "  []\n")).Single(), Is.EqualTo($"corpus manifest invalid: property 'name' of repository '{name}' is '{name}', expected owner/repo"));

  [TestCase("determinism: deterministic", "determinism: random", "property 'determinism' of fixture 'a.b' is 'random', expected deterministic or seeded")]
  [TestCase("tolerance: exact", "tolerance: loose", "property 'tolerance' of fixture 'a.b' is 'loose', expected exact, declared or VERIFICATION.tolerance.<name>")]
  [TestCase("{ kind: embedded, source: inline }", "{ kind: guessed, source: inline }", "property 'kind' of fixture 'a.b' is 'guessed', expected embedded, file or published")]
  [TestCase("{ kind: embedded, source: inline }", "[embedded]", "property 'vectors' of fixture 'a.b' must be a mapping")]
  [TestCase("{ kind: embedded, source: inline }", "{ kind: embedded }", "missing property 'source' in the vectors of fixture 'a.b'")]
  [TestCase("run: [node, '{file}']", "run: []", "property 'run' of fixture 'a.b' must not be empty")]
  [TestCase("run: [node, '{file}']", "run: [node, x.js]", "property 'run' of fixture 'a.b' does not name '{file}'")]
  [TestCase("run: [node, '{file}']", "run: node", "property 'run' of fixture 'a.b' must be a list of non-empty strings")]
  [TestCase("run: [node, '{file}']", "run: [node, '']", "property 'run' of fixture 'a.b' must be a list of non-empty strings")]
  [TestCase("export: main", "export: ~", "property 'export' of fixture 'a.b' must be a non-empty string")]
  [TestCase("export: main", "export: [main]", "property 'export' of fixture 'a.b' must be a non-empty string")]
  [TestCase("export: main", "export: null", "property 'export' of fixture 'a.b' must be a non-empty string")]
  [TestCase("export: main", "exports: main", "unknown property 'exports' in fixture 'a.b'")]
  [TestCase("    vectors: { kind: embedded, source: inline }\n", "", "missing property 'vectors' in fixture 'a.b'")]
  [TestCase("path: 'src/a.js'", "path: [src/a.js]", "property 'path' of fixture 'a.b' must be a non-empty string")]
  [TestCase("dependencies: []", "dependencies: lib.js", "property 'dependencies' of fixture 'a.b' must be a list of non-empty strings")]
  [TestCase("id: a.b", "id: [a.b]", "property 'id' of fixture '?' must be a non-empty string")]
  [TestCase("licence: MIT\n    determinism", "licence: MIT\n    licence: MIT\n    determinism", "not YAML: Duplicate key licence")]
  public void FixtureField_Invalid_IsRefused(string from, string to, string reason) {
    var problems = Problems(Manifest(Repository(), Fixture().Replace(from, to)));
    Assert.That(problems, Has.Some.StartsWith($"corpus manifest invalid: {reason}"));
  }

  [Test]
  public void MissingFixtureField_IsNamed()
    => Assert.That(Problems(Manifest(Repository(), Fixture().Replace("    profiles: [es2022]\n", ""))), Is.EqualTo(new[] { "corpus manifest invalid: missing property 'profiles' in fixture 'a.b'" }));

  [Test]
  public void FixtureWithoutId_IsNamedByAQuestionMark()
    => Assert.That(Problems(Manifest(Repository(), Fixture().Replace("  - id: a.b\n    ", "  - "))), Does.Contain("corpus manifest invalid: missing property 'id' in fixture '?'"));

  [TestCase("schema: 1\nrepositories: []\nfixtures: []\nextra: 1\n", "unknown property 'extra' in the manifest")]
  [TestCase("schema: 2\nrepositories: []\nfixtures: []\n", "unsupported schema 2")]
  [TestCase("repositories: []\nfixtures: []\n", "missing property 'schema' in the manifest")]
  [TestCase("schema: '1'\nrepositories: []\nfixtures: []\n", "unsupported schema 1")]
  [TestCase("schema: 1\nfixtures: []\n", "missing property 'repositories' in the manifest")]
  [TestCase("schema: 1\nrepositories: {}\nfixtures: []\n", "property 'repositories' of the manifest must be a list")]
  [TestCase("schema: 1\nrepositories: [x]\nfixtures: [y]\n", "a repository must be a mapping")]
  [TestCase("schema: 1\nrepositories: []\nfixtures: [y]\n", "a fixture must be a mapping")]
  [TestCase("- a\n", "the manifest is not a YAML mapping")]
  [TestCase("schema: [1\n", "not YAML")]
  [TestCase("", "the manifest must be exactly one YAML document")]
  [TestCase("schema: 1\n---\nschema: 1\n", "the manifest must be exactly one YAML document")]
  public void Document_Invalid_IsRefused(string text, string reason)
    => Assert.That(Problems(text), Has.Some.StartsWith($"corpus manifest invalid: {reason}"));

  [Test]
  public void Load_OfAMissingFile_IsRefused() {
    var exception = Assert.Throws<CorpusException>(() => CorpusManifest.Load(Path.Combine(Path.GetTempPath(), "polypiler-no-such-corpus.yaml")))!;
    Assert.That(exception.Diagnostics.Single().Message, Does.StartWith("corpus manifest invalid: cannot read '"));
  }

  /// <summary>The repository manifest declares the seven repositories of the pin table in <c>backlog/README.md</c> at their pins.</summary>
  [Test]
  public void RepositoryManifest_DeclaresThePinTable() {
    var root = PrototypeFixture.RepositoryRoot;
    var manifest = CorpusManifest.Load(Path.Combine(root, CorpusManifest.RepositoryPath));
    var table = Regex.Matches(File.ReadAllText(Path.Combine(root, "backlog", "README.md")), @"^\| `(?<name>[^`]+)`[^|]*\| `(?<commit>[0-9a-f]{40})` \|", RegexOptions.Multiline)
      .Select(match => (match.Groups["name"].Value, match.Groups["commit"].Value))
      .ToArray();
    Assert.That(table, Has.Length.EqualTo(7));
    Assert.That(manifest.Repositories.Select(repository => (repository.Name, repository.Commit)), Is.EquivalentTo(table));
    Assert.That(manifest.Repository("Hawkynt/Hawkynt.github.io")!.LicenceFiles, Is.EqualTo(new[] { "LICENSE", "Cipher/LICENSE" }));

    var adler = manifest.Fixture("cipher.checksum.adler32")!;
    Assert.That(adler.Files, Is.EqualTo(new[] { "Cipher/algorithms/checksum/adler.js", "Cipher/OpCodes.js" }));
    Assert.That(adler.Licence, Is.EqualTo("LGPL-3.0-or-later"));
  }

}
