using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Corpus;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Tests.Tooling.Corpus;

/// <summary>
/// <c>spec/schemas/corpus.schema.json</c> and <c>corpus.lock.schema.json</c> checked by JsonSchema.Net,
/// which shares no code with the loaders: both schemas against the meta-schema, the repository's files
/// against them, and malformed manifests refused by the schema and the loader alike.
/// </summary>
[TestFixture]
public sealed class CorpusSchemaTests {

  private static readonly string _root = PrototypeFixture.RepositoryRoot;
  private static readonly string _manifestSchemaPath = Path.Combine(_root, "spec", "schemas", "corpus.schema.json");
  private static readonly string _lockSchemaPath = Path.Combine(_root, "spec", "schemas", "corpus.lock.schema.json");
  private static readonly JsonSchema _manifestSchema = JsonSchema.FromText(File.ReadAllText(_manifestSchemaPath));
  private static readonly JsonSchema _lockSchema = JsonSchema.FromText(File.ReadAllText(_lockSchemaPath));

  private static JsonElement ToJson(string yaml) {
    var stream = new YamlStream();
    stream.Load(new StringReader(yaml));
    return JsonSerializer.SerializeToElement(Convert(stream.Documents[0].RootNode));
  }

  private static JsonNode? Convert(YamlNode node) => node switch {
    YamlMappingNode mapping => new JsonObject(mapping.Children.Select(pair => new KeyValuePair<string, JsonNode?>(((YamlScalarNode)pair.Key).Value!, Convert(pair.Value)))),
    YamlSequenceNode sequence => new JsonArray([.. sequence.Children.Select(Convert)]),
    YamlScalarNode { Style: ScalarStyle.Plain } plain when int.TryParse(plain.Value, out var number) => JsonValue.Create(number),
    YamlScalarNode scalar => JsonValue.Create(scalar.Value),
    _ => null
  };

  [TestCase("corpus.schema.json")]
  [TestCase("corpus.lock.schema.json")]
  public void Schema_IsValidAgainstTheMetaSchema(string name) {
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "spec", "schemas", name)));
    Assert.That(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
  }

  [Test]
  public void RepositoryManifest_ConformsToTheSchema()
    => Assert.That(_manifestSchema.Evaluate(ToJson(File.ReadAllText(Path.Combine(_root, CorpusManifest.RepositoryPath)))).IsValid, Is.True);

  [Test]
  public void RepositoryLock_ConformsToTheSchema() {
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, CorpusLock.RepositoryPath)));
    Assert.That(_lockSchema.Evaluate(document.RootElement).IsValid, Is.True);
  }

  private const string Fixture = """
    schema: 1
    repositories:
      - name: o/r
        url: https://example.org/o/r.git
        commit: 0123456789abcdef0123456789abcdef01234567
        licence: MIT
        licenceFiles: [LICENSE]
    fixtures:
      - id: a.b
        repository: o/r
        path: src/a.js
        export: main
        dependencies: []
        run: [node, '{file}']
        vectors: { kind: embedded, source: inline }
        licence: MIT
        determinism: deterministic
        tolerance: exact
        profiles: []
    """;

  [Test]
  public void ValidManifest_IsAcceptedByBoth() {
    Assert.That(_manifestSchema.Evaluate(ToJson(Fixture)).IsValid, Is.True);
    Assert.That(CorpusManifest.Parse(Fixture, "corpus.yaml").Fixtures, Has.Count.EqualTo(1));
  }

  [TestCase("commit: 0123456789abcdef0123456789abcdef01234567", "commit: 0123456789abcdef0123456789abcdef0123456")]
  [TestCase("commit: 0123456789abcdef0123456789abcdef01234567", "commit: 0123456789ABCDEF0123456789abcdef01234567")]
  [TestCase("path: src/a.js", "path: ../x")]
  [TestCase("path: src/a.js", "path: /etc/passwd")]
  [TestCase("path: src/a.js", "path: a//b")]
  [TestCase("path: src/a.js", "path: ./a")]
  [TestCase("id: a.b", "id: ab")]
  [TestCase("schema: 1", "schema: 2")]
  [TestCase("    export: main\n", "")]
  [TestCase("run: [node, '{file}']", "run: []")]
  [TestCase("tolerance: exact", "tolerance: loose")]
  [TestCase("determinism: deterministic", "determinism: random")]
  [TestCase("{ kind: embedded, source: inline }", "{ kind: file, source: ../v.json }")]
  [TestCase("{ kind: embedded, source: inline }", "{ kind: guessed, source: inline }")]
  [TestCase("profiles: []", "profiles: []\n    extra: 1")]
  public void InvalidManifest_IsRefusedByBoth(string from, string to) {
    // A checkout with CRLF line ends gives the raw string CRLF too.
    var fixture = Fixture.ReplaceLineEndings("\n");
    var text = fixture.Replace(from, to);
    Assert.That(text, Is.Not.EqualTo(fixture));
    Assert.That(_manifestSchema.Evaluate(ToJson(text)).IsValid, Is.False, "schema");
    Assert.That(() => CorpusManifest.Parse(text, "corpus.yaml"), Throws.InstanceOf<CorpusException>(), "loader");
  }

}
