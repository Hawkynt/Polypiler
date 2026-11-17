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

namespace Polypiler.Tests.Optimize.Cost;

/// <summary>
/// <c>spec/schemas/cost-profile.schema.json</c> checked by JsonSchema.Net, which shares no code with the
/// loader: the schema against the meta-schema, both test profiles against it (the schema-conformance
/// measurement of #155), and the malformed fixtures refused by schema and loader alike.
/// </summary>
[TestFixture]
public sealed class CostProfileSchemaTests {

  private static readonly JsonSchema _schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "cost-profile.schema.json")));

  private static JsonElement ToJson(string yaml) {
    var stream = new YamlStream();
    stream.Load(new StringReader(yaml));
    return JsonSerializer.SerializeToElement(Convert(stream.Documents[0].RootNode));
  }

  private static JsonNode? Convert(YamlNode node) => node switch {
    YamlMappingNode mapping => new JsonObject(mapping.Children.Select(pair => new KeyValuePair<string, JsonNode?>(((YamlScalarNode)pair.Key).Value!, Convert(pair.Value)))),
    YamlSequenceNode sequence => new JsonArray([.. sequence.Children.Select(Convert)]),
    YamlScalarNode { Style: ScalarStyle.Plain } plain when long.TryParse(plain.Value, out var number) => JsonValue.Create(number),
    YamlScalarNode scalar => JsonValue.Create(scalar.Value),
    _ => null
  };

  [Test]
  public void Schema_IsValidAgainstTheMetaSchema() {
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "cost-profile.schema.json")));
    Assert.That(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
  }

  [TestCase("wide", true)]
  [TestCase("narrow", true)]
  [TestCase("invalid-negative", false)]
  [TestCase("invalid-missing-default", false)]
  public void Fixture_ConformsToTheSchemaExactlyWhenTheLoaderAcceptsIt(string name, bool valid)
    => Assert.That(_schema.Evaluate(ToJson(File.ReadAllText(CostFixtures.PathOf(name)))).IsValid, Is.EqualTo(valid));

  [Test]
  public void UnknownOperationName_IsLeftToTheLoader() {
    Assert.That(_schema.Evaluate(ToJson(File.ReadAllText(CostFixtures.PathOf("invalid-unknown-operation")))).IsValid, Is.True,
      "the schema checks the shape Op.type; only the loader knows the IR's operations");
    Assert.That(_schema.Evaluate(ToJson(CostFixtures.Minimal + "operations:\n  rotate:\n    native: { latency: 1, size: 1 }\n")).IsValid, Is.False);
  }

}
