using System.IO;
using System.Text.Json;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary>
/// The schemas under <c>spec/schemas/</c> that this engine reads, checked against the JSON Schema
/// 2020-12 meta-schema, and the copy of the metadata schema the tool carries.
/// </summary>
[TestFixture]
public sealed class SpecSchemaTests {

  private static string SchemaPath(string name) => Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", name);

  [TestCase("meta.schema.json")]
  [TestCase("demo.schema.json")]
  public void Schema_IsValidAgainstTheMetaSchema(string name) {
    using var document = JsonDocument.Parse(File.ReadAllText(SchemaPath(name)));
    Assert.That(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
  }

  [Test]
  public void ToolCarriesTheRepositoryMetadataSchema() {
    using var stream = typeof(SpecValidator).Assembly.GetManifestResourceStream(SpecValidator.MetaSchemaResource)!;
    using var reader = new StreamReader(stream);
    Assert.That(reader.ReadToEnd(), Is.EqualTo(File.ReadAllText(SchemaPath("meta.schema.json"))));
  }

}
