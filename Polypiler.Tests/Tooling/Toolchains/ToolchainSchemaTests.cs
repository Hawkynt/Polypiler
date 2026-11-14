using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using static Polypiler.Tests.Tooling.Toolchains.ToolchainTesting;

namespace Polypiler.Tests.Tooling.Toolchains;

/// <summary>
/// <c>spec/schemas/toolchains.schema.json</c> checked by JsonSchema.Net, which shares no code with the
/// loader: the schema against the JSON Schema 2020-12 meta-schema, the repository lock and the
/// fixtures against the schema.
/// </summary>
[TestFixture]
public sealed class ToolchainSchemaTests {

  private static readonly string _schemaPath = Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "toolchains.schema.json");

  private static readonly JsonSchema _schema = JsonSchema.FromText(File.ReadAllText(_schemaPath));

  /// <summary>A YAML document as JSON: plain integers become numbers, plain truth values booleans, everything else strings.</summary>
  private static JsonElement ToJson(string path) {
    var stream = new YamlStream();
    stream.Load(new StringReader(File.ReadAllText(path)));
    return JsonSerializer.SerializeToElement(Convert(stream.Documents[0].RootNode));
  }

  private static JsonNode? Convert(YamlNode node) => node switch {
    YamlMappingNode mapping => new JsonObject(mapping.Children.Select(pair => new KeyValuePair<string, JsonNode?>(((YamlScalarNode)pair.Key).Value!, Convert(pair.Value)))),
    YamlSequenceNode sequence => new JsonArray([.. sequence.Children.Select(Convert)]),
    YamlScalarNode { Style: ScalarStyle.Plain, Value: "true" or "false" } truth => JsonValue.Create(truth.Value == "true"),
    YamlScalarNode { Style: ScalarStyle.Plain } plain when int.TryParse(plain.Value, out var number) => JsonValue.Create(number),
    YamlScalarNode scalar => JsonValue.Create(scalar.Value),
    _ => null
  };

  private static bool IsValid(string path) => _schema.Evaluate(ToJson(path)).IsValid;

  [Test]
  public void Schema_IsValidAgainstTheMetaSchema() {
    using var document = JsonDocument.Parse(File.ReadAllText(_schemaPath));
    Assert.That(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
  }

  [Test]
  public void RepositoryLock_ConformsToTheSchema() => Assert.That(IsValid(RepositoryLock), Is.True);

  [TestCase("valid.lock")]
  [TestCase("one.lock")]
  [TestCase("empty.lock")]
  [TestCase("ghost.lock")]
  [TestCase("core-only.lock")]
  public void ValidFixture_ConformsToTheSchema(string fixture) => Assert.That(IsValid(Fixture(fixture)), Is.True);

  [TestCase("no-version.lock")]
  [TestCase("extra-property.lock")]
  [TestCase("no-group.lock")]
  [TestCase("schema-2.lock")]
  [TestCase("invalid-key.lock")]
  [TestCase("package-no-host.lock")]
  public void MalformedFixture_IsRefusedByTheSchema(string fixture) => Assert.That(IsValid(Fixture(fixture)), Is.False);

}
