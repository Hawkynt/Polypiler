using System.IO;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core.IR.Intrinsics;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tests.Intrinsics;

/// <summary>
/// <see cref="ContractLoader"/> (#106): every property it reads, valid and invalid - one refused document
/// per property the second version of the schema added and per property the loader checks, each
/// <c>PLY0251</c> naming the file and the property.
/// </summary>
[TestFixture]
public sealed class ContractLoaderTests {

  private static JsonObject Valid()
    => (JsonObject)SpecDocument.Parse("intrinsics/registry/identity.yaml",
      File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "intrinsics", "registry", "identity.yaml"))).Root!;

  private static JsonObject Changed(string property, JsonNode? value) {
    var document = Valid();
    var spec = (JsonObject)document["spec"]!;
    if (value is null)
      spec.Remove(property);
    else
      spec[property] = value;
    return document;
  }

  private static string Refusal(JsonNode? document) {
    var failure = Assert.Throws<IntrinsicRegistryException>(() => ContractLoader.Load("x.yaml", document))!;
    Assert.That(failure.Diagnostic.Code, Is.EqualTo("PLY0251"));
    Assert.That(failure.Diagnostic.File, Is.EqualTo("x.yaml"));
    return failure.Diagnostic.Message;
  }

  [Test]
  public void TheValidFixture_IsRead() => Assert.That(ContractLoader.Load("identity.yaml", Valid()).Id, Is.EqualTo(new IntrinsicId("Abs")));

  [Test]
  public void ADocumentWithoutBody_IsRefused() {
    Assert.That(Refusal(null), Is.EqualTo("intrinsic contract 'x.yaml': property 'document': missing"));
    Assert.That(Refusal(JsonNode.Parse("[]")), Is.EqualTo("intrinsic contract 'x.yaml': property 'document': not a mapping"));
    Assert.That(Refusal(JsonNode.Parse("{}")), Is.EqualTo("intrinsic contract 'x.yaml': property 'spec': missing"));
    Assert.That(() => ContractLoader.Load(null!, Valid()), Throws.ArgumentNullException);
  }

  [TestCase("name", "\"popCount\"", "property 'name': 'popCount' does not match")]
  [TestCase("name", "3", "property 'name': not a string")]
  [TestCase("name", null, "property 'name': missing")]
  [TestCase("group", "\"Misc\"", "property 'group': group 'Misc' is none of")]
  [TestCase("typeParameters", "{}", "property 'typeParameters': not a list")]
  [TestCase("typeParameters", null, "property 'typeParameters': missing")]
  [TestCase("typeParameters", "[1]", "property 'typeParameters[0]': not a mapping")]
  [TestCase("typeParameters", "[{\"name\": \"T\", \"constraint\": \"real\"}]", "property 'typeParameters[0]': constraint 'real' is none of integer, unsigned, signed, float, any")]
  [TestCase("parameters", "[{\"name\": \"x\", \"type\": \"U\"}]", "property 'parameters[0].type': 'U' is no type parameter of the intrinsic")]
  [TestCase("parameters", "[{\"name\": \"X\", \"type\": \"T\"}]", "property 'parameters[0].name': 'X' does not match")]
  [TestCase("parameters", "[{\"name\": \"x\", \"type\": \"t[\"}]", "property 'parameters[0].type': 't[' does not match")]
  [TestCase("returns", "[]", "property 'returns': 0 results, a contract has one to four")]
  [TestCase("returns", "[{\"name\": \"a\", \"type\": \"T\"}, {\"name\": \"b\", \"type\": \"T\"}, {\"name\": \"c\", \"type\": \"T\"}, {\"name\": \"d\", \"type\": \"T\"}, {\"name\": \"e\", \"type\": \"T\"}]", "property 'returns': 5 results")]
  [TestCase("exactness", "{\"mode\": \"roughly\"}", "property 'exactness': mode 'roughly' is none of exact, tolerance")]
  [TestCase("exactness", "{\"mode\": \"tolerance\"}", "property 'exactness': mode 'tolerance' without a policy")]
  [TestCase("exactness", "{\"mode\": \"exact\", \"policy\": \"VERIFICATION.tolerance.ulp1\"}", "property 'exactness': a policy for mode 'exact'")]
  [TestCase("exactness", "{\"mode\": \"tolerance\", \"policy\": \" \"}", "property 'exactness.policy': empty")]
  [TestCase("exactness", "3", "property 'exactness': not a mapping")]
  [TestCase("effects", "[]", "property 'effects': 'pure' alone, or one or more EFFECTS. ids")]
  [TestCase("effects", "[\"pure\", \"EFFECTS.memory.read\"]", "property 'effects': 'pure' alone")]
  [TestCase("effects", "[1]", "property 'effects[0]': not a string")]
  [TestCase("reference", "{\"algorithm\": \"x\"}", "property 'reference.source': missing")]
  [TestCase("spelling", "\"memory.copy\"", "property 'spelling': 'memory.copy' does not match")]
  [TestCase("attributes", "[{\"name\": \"n\", \"type\": \"TYPES.i32\", \"low\": 8, \"high\": 7}]", "property 'attributes[0]': the range [8, 7] is empty")]
  [TestCase("attributes", "[{\"name\": \"n\", \"type\": \"TYPES.i32\", \"low\": \"0\", \"high\": 7}]", "property 'attributes[0].low': not an integer")]
  [TestCase("attributes", "[{\"name\": \"n\", \"type\": \"TYPES.i32\", \"high\": 7}]", "property 'attributes[0].low': missing")]
  [TestCase("algebra", "[\"distributive\"]", "property 'algebra[0]': 'distributive' is no algebraic law")]
  [TestCase("lowersTo", "[3]", "property 'lowersTo[0]': not a string")]
  [TestCase("facts", "{\"resultSize\": 1}", "property 'facts': 'resultSize' is no fact")]
  [TestCase("facts", "{\"resultRange\": [0]}", "property 'facts.resultRange': not a pair [low, high]")]
  [TestCase("facts", "{\"resultRange\": [0, \"1\"]}", "property 'facts.resultRange': not an integer")]
  [TestCase("facts", "{\"knownZeroBits\": \"15\"}", "property 'facts.knownZeroBits': not a hexadecimal pattern")]
  [TestCase("facts", "{\"knownOneBits\": \"0x1FFFFFFFFFFFFFFFF\"}", "property 'facts.knownOneBits': not a hexadecimal pattern")]
  [TestCase("facts", "[]", "property 'facts': not a mapping")]
  [TestCase("laneLiftable", "\"yes\"", "property 'laneLiftable': not true or false")]
  [TestCase("requiresRuntime", "1", "property 'requiresRuntime': not true or false")]
  [TestCase("legality", "[{\"text\": \"x\"}]", "property 'legality[0].id': missing")]
  [TestCase("vectors", "[{\"types\": {}, \"inputs\": [], \"outputs\": 1}]", "property 'vectors[0].outputs': not a list")]
  [TestCase("vectors", "[{\"types\": {\"T\": 1}, \"inputs\": [], \"outputs\": []}]", "property 'vectors[0].types.T': not a string")]
  [TestCase("vectors", "[{\"inputs\": [], \"outputs\": []}]", "property 'vectors[0].types': missing")]
  public void APropertyOutsideTheSchema_IsRefusedNamingIt(string property, string? json, string expected)
    => Assert.That(Refusal(Changed(property, json is null ? null : JsonNode.Parse(json))), Does.StartWith("intrinsic contract 'x.yaml': " + expected));

  [Test]
  public void TheOptionalProperties_MayBeAbsent() {
    var document = Valid();
    var spec = (JsonObject)document["spec"]!;
    foreach (var property in new[] { "attributes", "legality", "algebra", "lowersTo", "facts", "laneLiftable", "requiresRuntime", "spelling", "vectors" })
      spec.Remove(property);
    var contract = ContractLoader.Load("x.yaml", document);
    Assert.That((contract.Attributes.Count, contract.Legality.Count, contract.Algebra.Count, contract.LowersTo.Count, contract.Facts, contract.LaneLiftable, contract.RequiresRuntime, contract.Spelling, contract.Vectors.Count),
      Is.EqualTo((0, 0, 0, 0, (IntrinsicFacts?)null, false, false, "Abs", 0)));
  }

  [Test]
  public void EveryOptionalProperty_IsRead() {
    var document = Changed("attributes", JsonNode.Parse("[{\"name\": \"n\", \"type\": \"TYPES.i32\", \"low\": 0, \"high\": 7}]"));
    var spec = (JsonObject)document["spec"]!;
    spec["algebra"] = JsonNode.Parse("[\"commutative\", \"associative\", \"idempotent\", \"involution\", \"identity-element\", \"absorbing-element\"]");
    spec["spelling"] = "Test.Abs";
    spec["legality"] = JsonNode.Parse("[{\"id\": \"in-range\", \"text\": \"x\", \"fact\": \"range\"}]");
    spec["facts"] = JsonNode.Parse("{\"knownOneBits\": \"0x1\"}");
    spec["exactness"] = JsonNode.Parse("{\"mode\": \"tolerance\", \"policy\": \"VERIFICATION.tolerance.ulp1\"}");
    spec["effects"] = JsonNode.Parse("[\"EFFECTS.memory.read\"]");
    spec["typeParameters"] = JsonNode.Parse("[{\"name\": \"T\", \"constraint\": \"integer\"}, {\"name\": \"S\", \"constraint\": \"signed\"}, {\"name\": \"F\", \"constraint\": \"float\"}, {\"name\": \"A\", \"constraint\": \"any\"}, {\"name\": \"U\", \"constraint\": \"unsigned\"}]");
    spec["parameters"] = JsonNode.Parse("[{\"name\": \"x\", \"type\": \"T[]\"}, {\"name\": \"y\", \"type\": \"TYPES.i32\"}]");
    var contract = ContractLoader.Load("x.yaml", document);
    Assert.That(contract.Attributes, Is.EqualTo(new[] { new IntrinsicAttribute("n", "TYPES.i32", 0, 7) }));
    Assert.That(contract.Algebra, Has.Count.EqualTo(6));
    Assert.That((contract.Spelling, contract.Legality[0], contract.Facts, contract.Exactness, contract.TolerancePolicy, contract.Effects[0]),
      Is.EqualTo(("Test.Abs", "in-range", new IntrinsicFacts(null, null, 1), IntrinsicExactness.Tolerance, "VERIFICATION.tolerance.ulp1", "EFFECTS.memory.read")));
    Assert.That(contract.TypeParameters, Has.Count.EqualTo(5));
    Assert.That(contract.Parameters, Is.EqualTo(new[] { new IntrinsicValue("x", "T", true), new IntrinsicValue("y", "TYPES.i32", false) }));
  }

}
