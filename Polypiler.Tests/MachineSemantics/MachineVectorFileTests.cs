using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Tracing;
using Polypiler.Core.Verification.Oracles;
using static Polypiler.Tests.MachineSemantics.TraceTesting;

namespace Polypiler.Tests.MachineSemantics;

/// <summary>
/// <see cref="MachineVectorFile"/> on the fixtures of <c>tests/fixtures/native/mach-trace</c>: the
/// byte-identical round trip, every rule with its negative fixture or mutation, the schema checked by
/// JsonSchema.Net (which shares no code with the loader), and <see cref="MachineVectorCodec"/> through
/// the byte comparison of the verification oracles.
/// </summary>
[TestFixture]
public sealed class MachineVectorFileTests {

  private static readonly Lazy<JsonSchema> _schema = new(() => JsonSchema.FromText(File.ReadAllText(SchemaFile)));

  private const string Base = """
    {"schemaVersion": 1, "fixture": "f", "isa": "test-m32", "provenance": {"kind": "published", "producer": "hand", "source": "s"},
     "observe": {"registers": ["r0"], "flags": ["c"], "memory": [], "events": ["return"], "cycles": false, "mode": "semantic"},
     "vectors": [{"name": "v", "initial": {"registers": {}, "flags": {}, "memory": []},
       "expected": {"registers": {"r0": "0x1"}, "flags": {}, "memory": [], "events": [{"kind": "return"}], "outcome": "returned"}}]}
    """;

  private static IEnumerable<string> Valid() => Directory.GetFiles(Path.Combine(TraceFixtures, "valid"), "*.mvec.json").Select(Path.GetFileName).Order(StringComparer.Ordinal)!;

  private static MachineVectorFile LoadValid(string name) => MachineVectorFile.Load(Path.Combine(TraceFixtures, "valid", name), Profile("test-m32"));

  private static Diagnostic Refusal(TestDelegate action) => Assert.Throws<MachineException>(action)!.Diagnostic;

  private static Diagnostic Mutated(Action<JsonObject> mutate, string isa = "test-m32") {
    var root = JsonNode.Parse(Base)!.AsObject();
    mutate(root);
    return Refusal(() => MachineVectorFile.Parse(root.ToJsonString(), Profile(isa)));
  }

  private static JsonObject Vector(JsonObject root) => root["vectors"]![0]!.AsObject();

  private static JsonObject Expected(JsonObject root) => Vector(root)["expected"]!.AsObject();

  [Test]
  public void TheValidFixtures_AreOnePerEventKindAndOneCombined()
    => Assert.That(Valid(), Is.EqualTo(new[] { "combined.mvec.json", "device.mvec.json", "external.mvec.json", "memory-write.mvec.json", "return.mvec.json", "trap.mvec.json" }));

  /// <summary>The measurement "round trip of all valid fixtures": byte-identical, through text and through a file.</summary>
  [TestCaseSource(nameof(Valid))]
  public void AValidFixture_RoundTripsByteIdentically(string name) {
    var path = Path.Combine(TraceFixtures, "valid", name);
    var file = LoadValid(name);
    Assert.That(file.Save(), Is.EqualTo(File.ReadAllText(path)));
    var copy = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mvec.json");
    try {
      file.Save(copy);
      Assert.That(File.ReadAllBytes(copy), Is.EqualTo(File.ReadAllBytes(path)));
    } finally {
      File.Delete(copy);
    }
  }

  /// <summary>Every valid fixture conforms to the schema.</summary>
  [TestCaseSource(nameof(Valid))]
  public void AValidFixture_ConformsToTheSchema(string name) {
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(TraceFixtures, "valid", name)));
    Assert.That(_schema.Value.Evaluate(document.RootElement).IsValid, Is.True);
  }

  [Test]
  public void TheSchema_IsValidAgainstTheMetaSchema() {
    using var document = JsonDocument.Parse(File.ReadAllText(SchemaFile));
    Assert.That(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
  }

  private static IEnumerable<TestCaseData> Invalid() {
    TestCaseData Case(string file, string code, string rule, bool schemaRejects) => new TestCaseData(file, code, rule, schemaRejects).SetName($"Invalid_{Path.GetFileNameWithoutExtension(file)}");
    yield return Case("missing-isa.mvec.json", "PLY4030", "required", true);
    yield return Case("odd-length-hex.mvec.json", "PLY4030", "hex-bytes", true);
    yield return Case("address-without-0x.mvec.json", "PLY4030", "hex-number", true);
    yield return Case("unknown-event-kind.mvec.json", "PLY4030", "event-kind", true);
    yield return Case("duplicate-vector-name.mvec.json", "PLY4030", "unique-vector-name", false);
    yield return Case("schema-version.mvec.json", "PLY4030", "schema-version", true);
    yield return Case("unknown-outcome.mvec.json", "PLY4030", "outcome", true);
    yield return Case("unknown-mode.mvec.json", "PLY4030", "mode", true);
    yield return Case("value-too-wide.mvec.json", "PLY4030", "value-width", false);
    yield return Case("isa-mismatch.mvec.json", "PLY4030", "isa", false);
    yield return Case("producer-polypiler.mvec.json", "PLY4030", "provenance", true);
    yield return Case("unknown-property.mvec.json", "PLY4030", "shape", true);
    yield return Case("negative-length.mvec.json", "PLY4030", "count", true);
    yield return Case("unknown-vector-register.mvec.json", "PLY4030", "storage", false);
    yield return Case("not-json.mvec.json", "PLY4030", "json", false);
    yield return Case("unknown-observed-register.mvec.json", "PLY4032", null!, false);
  }

  /// <summary>Test design 11 and 12: every rule has a negative fixture; the loader names the rule, the schema refuses what it can state.</summary>
  [TestCaseSource(nameof(Invalid))]
  public void AnInvalidFixture_IsRefused(string name, string code, string? rule, bool schemaRejects) {
    var path = Path.Combine(TraceFixtures, "invalid", name);
    var diagnostic = Refusal(() => MachineVectorFile.Load(path, Profile("test-m32")));
    Assert.That(diagnostic.Code, Is.EqualTo(code));
    Assert.That(diagnostic.ExitCode, Is.EqualTo(ExitCode.EnvironmentError));
    if (rule is not null)
      Assert.That(diagnostic.Message, Does.StartWith($"machine vector file '{path}' is not valid: ").And.EndWith($"(rule: {rule})"));
    if (!schemaRejects)
      return;

    using var document = JsonDocument.Parse(File.ReadAllText(path));
    Assert.That(_schema.Value.Evaluate(document.RootElement).IsValid, Is.False);
  }

  [Test]
  public void EveryInvalidFixture_HasACase()
    => Assert.That(Directory.GetFiles(Path.Combine(TraceFixtures, "invalid")).Select(Path.GetFileName).Order(StringComparer.Ordinal),
      Is.EqualTo(Invalid().Select(data => (string)data.Arguments[0]!).Order(StringComparer.Ordinal)));

  /// <summary>Test design 12 and the Gherkin: <c>eax</c> on <c>test-m32</c>.</summary>
  [Test]
  public void AnUnknownObservedRegister_IsPly4032() {
    var diagnostic = Refusal(() => MachineVectorFile.Load(Path.Combine(TraceFixtures, "invalid", "unknown-observed-register.mvec.json"), Profile("test-m32")));
    Assert.That(diagnostic.Message, Does.EndWith("observes 'eax', which machine profile 'test-m32' does not define"));
  }

  [Test]
  public void AnUnknownObservedFlag_IsPly4032()
    => Assert.That(Mutated(root => root["observe"]!["flags"] = new JsonArray("cf")).Message, Is.EqualTo("'<text>' observes 'cf', which machine profile 'test-m32' does not define"));

  [Test]
  public void AFileThatCannotBeRead_IsPly4030() {
    var diagnostic = Refusal(() => MachineVectorFile.Load(Path.Combine(TraceFixtures, "missing.mvec.json"), Profile("test-m32")));
    Assert.That(diagnostic.Code, Is.EqualTo("PLY4030"));
    Assert.That(diagnostic.Message, Does.EndWith("(rule: readable)"));
  }

  private static IEnumerable<TestCaseData> Mutations() {
    TestCaseData Case(string name, Action<JsonObject> mutate, string rule) => new TestCaseData(mutate, rule).SetName("Mutation_" + name);
    yield return Case("SchemaVersionNotAnInteger", root => root["schemaVersion"] = "1", "shape");
    yield return Case("SchemaVersionNegative", root => root["schemaVersion"] = -1, "count");
    yield return Case("FixtureNotAString", root => root["fixture"] = 5, "shape");
    yield return Case("ProvenanceKindUnknown", root => root["provenance"]!["kind"] = "guessed", "provenance");
    yield return Case("ObserveNotAnObject", root => root["observe"] = new JsonArray(), "shape");
    yield return Case("ObserveRegistersNotAnArray", root => root["observe"]!["registers"] = "r0", "shape");
    yield return Case("ObserveMemoryItemNotAnObject", root => root["observe"]!["memory"] = new JsonArray(1), "shape");
    yield return Case("ObserveCyclesNotABoolean", root => root["observe"]!["cycles"] = "yes", "shape");
    yield return Case("LimitsUnknownProperty", root => root["limits"] = new JsonObject { ["maxTime"] = 1 }, "shape");
    yield return Case("LimitsNotAnInteger", root => root["limits"] = new JsonObject { ["maxCycles"] = "100" }, "shape");
    yield return Case("LimitsNegative", root => root["limits"] = new JsonObject { ["maxSteps"] = -1 }, "count");
    yield return Case("VectorsNotAnArray", root => root["vectors"] = new JsonObject(), "shape");
    yield return Case("CallArgumentNotHex", root => Vector(root)["call"] = new JsonObject { ["contract"] = "c", ["arguments"] = new JsonArray("1") }, "hex-number");
    yield return Case("CallWithoutContract", root => Vector(root)["call"] = new JsonObject { ["arguments"] = new JsonArray() }, "required");
    yield return Case("RegistersNotAnObject", root => Expected(root)["registers"] = new JsonArray(), "shape");
    yield return Case("FlagNotABoolean", root => Expected(root)["flags"] = new JsonObject { ["c"] = 1 }, "shape");
    yield return Case("FlagUnknown", root => Expected(root)["flags"] = new JsonObject { ["cf"] = true }, "storage");
    yield return Case("EventNotAnObject", root => Expected(root)["events"] = new JsonArray("return"), "shape");
    yield return Case("EventWithoutKind", root => Expected(root)["events"] = new JsonArray(new JsonObject()), "required");
    yield return Case("EventUnknownProperty", root => Expected(root)["events"] = new JsonArray(new JsonObject { ["kind"] = "return", ["at"] = "0x1" }), "shape");
    yield return Case("DeviceWriteNotABoolean", root => Expected(root)["events"] = new JsonArray(Device(write: "yes")), "shape");
    yield return Case("DeviceWidthZero", root => Expected(root)["events"] = new JsonArray(Device(width: 0)), "count");
    yield return Case("DeviceWidthNotAnInteger", root => Expected(root)["events"] = new JsonArray(Device(width: 1.5)), "shape");
    yield return Case("TrapVectorNegative", root => Expected(root)["events"] = new JsonArray(new JsonObject { ["kind"] = "trap", ["trap"] = "t", ["vector"] = -1 }), "count");
    yield return Case("NumberWithoutDigits", root => Expected(root)["registers"] = new JsonObject { ["r0"] = "0x" }, "hex-number");
    yield return Case("NumberTooLong", root => Expected(root)["registers"] = new JsonObject { ["r0"] = "0x" + new string('0', 17) }, "hex-number");
    yield return Case("NumberNotHex", root => Expected(root)["registers"] = new JsonObject { ["r0"] = "0xZZ" }, "hex-number");
    yield return Case("BytesNotHex", root => Expected(root)["memory"] = new JsonArray(new JsonObject { ["address"] = "0x0", ["hex"] = "ZZ" }), "hex-bytes");
    yield return Case("CyclesNotAnInteger", root => Expected(root)["cycles"] = "12", "shape");
    yield return Case("NameNotAString", root => Vector(root)["name"] = 1, "shape");
    yield return Case("RootEmpty", root => root.Clear(), "required");
  }

  private static JsonObject Device(JsonNode? write = null, JsonNode? width = null)
    => new() { ["kind"] = "device", ["space"] = "io", ["address"] = "0x60", ["width"] = width ?? 8, ["write"] = write ?? true, ["value"] = "0x1" };

  /// <summary>The rules no fixture shows, each as one mutation of a valid document.</summary>
  [TestCaseSource(nameof(Mutations))]
  public void AMutation_BreaksItsRule(Action<JsonObject> mutate, string rule) {
    var diagnostic = Mutated(mutate);
    Assert.That(diagnostic.Code, Is.EqualTo("PLY4030"));
    Assert.That(diagnostic.Message, Does.EndWith($"(rule: {rule})"));
  }

  [Test]
  public void ARootThatIsNoObject_IsShape()
    => Assert.That(Refusal(() => MachineVectorFile.Parse("[]", Profile("test-m32"))).Message, Does.EndWith("$ must be an object (rule: shape)"));

  /// <summary>A 64-bit register takes every 64-bit value.</summary>
  [Test]
  public void A64BitRegister_TakesTheFullRange() {
    var text = Base.Replace("test-m32", "test-m64").Replace("\"0x1\"", "\"0xFFFFFFFFFFFFFFFF\"");
    Assert.That(MachineVectorFile.Parse(text, Profile("test-m64")).Vectors[0].Expected.Final.Registers["r0"], Is.EqualTo(ulong.MaxValue));
  }

  /// <summary>Test design 17: a vector with <c>call</c> and one without both load; the call round-trips.</summary>
  [Test]
  public void AVectorWithAndWithoutCall_Loads() {
    var file = LoadValid("return.mvec.json");
    Assert.That(file.Vectors[0].Call, Is.EqualTo(new MachineCall("test-reg (u32, u32) -> u32", [1, 2])));
    Assert.That(file.Vectors[1].Call, Is.Null);
    Assert.That(file.Save(), Does.Contain("\"contract\": \"test-reg (u32, u32) -> u32\""));
    Assert.That(file.Vectors[0].Call!.GetHashCode(), Is.EqualTo(new MachineCall("test-reg (u32, u32) -> u32", [1, 2]).GetHashCode()));
    Assert.That(file.Vectors[0].Call!.Equals(null), Is.False);
  }

  /// <summary>What the loader reads, field by field, from the combined fixture.</summary>
  [Test]
  public void TheCombinedFixture_IsReadFieldByField() {
    var file = LoadValid("combined.mvec.json");
    Assert.That(file.Fixture, Is.EqualTo("mach-trace.combined"));
    Assert.That(file.Isa, Is.EqualTo("test-m32"));
    Assert.That(file.Provenance, Is.EqualTo(new MachineVectorProvenance("reference-run", "hand", "issue 144: every event kind in one interleaved sequence, with cycles observed")));
    Assert.That(file.Limits, Is.EqualTo(new MachineLimits(null, 1000)));
    Assert.That(file.Observe, Is.EqualTo(Observe(["r0", "sp"], ["c"], [new(0x1000, 4), new(0x2000, 0)], [.. TraceEvent.Kinds], true, TraceMode.Raw)));
    var expected = Trace([("r0", 5), ("sp", 0x8000)], [("c", true)], [Bytes(0x1000, 5, 0, 0, 0)], [
      new DeviceAccess("mmio", 0xFFFF0000, 32, false, 5), Write(0x1000, 5, 0, 0, 0), new ExternalCall("notify", "test-reg", [0x1000, 4], null),
      new Trap("breakpoint", 3, null), new Return(5)], TraceOutcome.Returned, 42);
    Assert.That(file.Vectors[0].Expected, Is.EqualTo(expected));
    Assert.That(file.Vectors[0].Initial, Is.EqualTo(State([("pc", 0x400), ("r0", 5), ("sp", 0x8000)], [("c", false)], [Bytes(0x1000, 0xDE, 0xAD, 0xBE, 0xEF)])));
  }

  [Test]
  public void ConstructorAndSave_RefuseNull() {
    Assert.That(() => new MachineVectorFile(null!, "i", new("k", "p", "s"), TraceProjection.Nothing, null, []), Throws.ArgumentNullException);
    Assert.That(() => new MachineVectorFile("f", null!, new("k", "p", "s"), TraceProjection.Nothing, null, []), Throws.ArgumentNullException);
    Assert.That(() => new MachineVectorFile("f", "i", null!, TraceProjection.Nothing, null, []), Throws.ArgumentNullException);
    Assert.That(() => new MachineVectorFile("f", "i", new("k", "p", "s"), null!, null, []), Throws.ArgumentNullException);
    Assert.That(() => LoadValid("device.mvec.json").Save(null!), Throws.ArgumentNullException);
    Assert.That(() => MachineVectorFile.Load(null!, Profile("test-m32")), Throws.ArgumentNullException);
    Assert.That(() => MachineVectorFile.Parse(Base, null!), Throws.ArgumentNullException);
  }

  [TestCase(TraceMode.Semantic, "semantic")]
  [TestCase(TraceMode.Raw, "raw")]
  public void Modes_AreSpelledAsInTheFile(TraceMode mode, string name) => Assert.That(MachineVectorFile.Name(mode), Is.EqualTo(name));

  /// <summary>The canonical input bytes: sorted keys, no whitespace, the call only where there is one.</summary>
  [Test]
  public void TheInputBytes_AreCanonical() {
    var file = LoadValid("return.mvec.json");
    Assert.That(Encoding.UTF8.GetString(MachineVectorCodec.EncodeInput(file.Vectors[0])),
      Is.EqualTo("""{"call":{"arguments":["0x1","0x2"],"contract":"test-reg (u32, u32) -> u32"},"flags":{},"memory":[],"registers":{}}"""));
    Assert.That(Encoding.UTF8.GetString(MachineVectorCodec.EncodeInput(file.Vectors[1])),
      Is.EqualTo("""{"flags":{"c":false,"o":null},"memory":[],"registers":{"r0":"0xFFFFFFFF","r1":"0x1"}}"""));
  }

  /// <summary>The canonical trace bytes hold only what the projection observes, with <c>null</c> for what a side lacks.</summary>
  [Test]
  public void TheTraceBytes_HoldTheProjection() {
    var trace = Trace([("r0", 5), ("r1", 7)], [("c", true), ("z", null)], [Bytes(0x1000, 1)], [Out(0x60, 1), Write(0x1000, 1), new Return(5)], TraceOutcome.Returned, 9);
    var projection = Observe(["r0", "r2"], ["c", "z"], [new(0x1000, 2)], ["device", "memory-write"], true);
    Assert.That(Encoding.UTF8.GetString(MachineVectorCodec.EncodeTrace(trace, projection)), Is.EqualTo(
      """{"cycles":9,"events":[{"address":"0x60","kind":"device","space":"io","value":"0x1","width":8,"write":true}],"flags":{"c":true,"z":null},"memory":[1,null],"outcome":"returned","registers":{"r0":"0x5","r2":null}}"""));
    Assert.That(Encoding.UTF8.GetString(MachineVectorCodec.EncodeTrace(trace with { Cycles = null }, projection)), Does.StartWith("""{"cycles":null,"""));
    Assert.That(Encoding.UTF8.GetString(MachineVectorCodec.EncodeTrace(trace, TraceProjection.Nothing)), Is.EqualTo("""{"events":[],"flags":{},"memory":[],"outcome":"returned","registers":{}}"""));
    Assert.That(() => MachineVectorCodec.EncodeTrace(trace, null!), Throws.ArgumentNullException);
    Assert.That(() => MachineVectorCodec.EncodeInput(null!), Throws.ArgumentNullException);
    Assert.That(() => MachineVectorCodec.EncodeExpected(null!, projection), Throws.ArgumentNullException);
  }

  /// <summary>
  /// The adapter onto the verification oracles: the codec's bytes are the input and expected output of
  /// <see cref="TestVector"/>s, and <see cref="DifferentialComparer"/> compares them bit-exactly. A
  /// difference outside the projection passes, one inside it fails at the byte it changes.
  /// </summary>
  [Test]
  public void TheCodecBytes_FlowThroughTheDifferentialComparer() {
    var file = LoadValid("trap.mvec.json");
    var vectors = file.Vectors.Select(vector => new TestVector(vector.Name, MachineVectorCodec.EncodeInput(vector), MachineVectorCodec.EncodeExpected(vector, file.Observe))).ToList();
    var reference = vectors.Select(vector => vector.Expected!).ToList();
    var outside = file.Vectors.Select(vector => MachineVectorCodec.EncodeTrace(vector.Expected with { Final = vector.Expected.Final with { Registers = vector.Expected.Final.Registers.SetItem("r3", 9) } }, file.Observe)).ToList();
    Assert.That(DifferentialComparer.Compare(vectors, reference, outside, new ExactnessPolicy.Exact()).Verified, Is.True);

    var inside = file.Vectors.Select(vector => MachineVectorCodec.EncodeTrace(vector.Expected with { Outcome = TraceOutcome.Aborted }, file.Observe)).ToList();
    var report = DifferentialComparer.Compare(vectors, reference, inside, new ExactnessPolicy.Exact());
    Assert.That(report.Verified, Is.False);
    Assert.That(report.Passed, Is.Zero);
  }

  [Test]
  public void SaveOfAFileBuiltInCode_WritesEveryOptionalField() {
    var vector = new MachineVector("v", null, MachineSnapshot.Empty, Trace(events: [new ExternalCall("f", null, [], 1), new Trap("t", null, 0x10)], outcome: TraceOutcome.Aborted));
    var file = new MachineVectorFile("f", "test-m32", new("published", "hand", "s"), TraceProjection.Nothing, new(5, null), [vector]);
    var again = MachineVectorFile.Parse(file.Save(), Profile("test-m32"));
    Assert.That(again.Vectors[0], Is.EqualTo(vector));
    Assert.That(again.Limits, Is.EqualTo(new MachineLimits(5, null)));
    Assert.That(again.Save(), Is.EqualTo(file.Save()));
  }

  [Test]
  public void TheVectors_KeepFileOrder() => Assert.That(LoadValid("trap.mvec.json").Vectors.Select(vector => vector.Name), Is.EqualTo(new[] { "divide-by-zero", "endless-loop" }));

}
