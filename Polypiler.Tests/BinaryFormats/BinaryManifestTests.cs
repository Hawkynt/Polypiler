using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.BinaryFormats;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Regions;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.BinaryFormats;

/// <summary>
/// The binary input manifest (MACH-BIN-MANIFEST): the fixtures read, every broken rule refused as
/// <c>PLY4055</c> naming it, the schema checked by JsonSchema.Net against the same fixtures, and the
/// comparison with command-line values (<c>PLY4057</c>, exit 2).
/// </summary>
[TestFixture]
public sealed class BinaryManifestTests {

  private static readonly JsonSchema _schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "binary-manifest.schema.json")));

  private static BinaryManifest Load(string fixture) {
    var result = BinaryManifest.Load(BinaryTesting.Fixture(fixture));
    Assert.That(result.Problems, Is.Empty);
    return result.Value!;
  }

  private static Diagnostic Refusal(JsonObject json) {
    var result = BinaryTesting.Parse(json);
    Assert.That(result.Succeeded, Is.False);
    return result.Problems.Single();
  }

  private static JsonObject Min(Action<JsonObject> change) {
    var json = BinaryTesting.Json("min.json");
    change(json);
    return json;
  }

  private static JsonObject Entry(JsonObject json) => json["entries"]![0]!.AsObject();

  /// <summary>Row 1: the minimal manifest reads with every field it gives and nothing else.</summary>
  [Test]
  public void Min_Loads() {
    var manifest = Load("min.json");
    Assert.Multiple(() => {
      Assert.That(manifest.Source, Is.EqualTo(BinaryTesting.Fixture("min.json")));
      Assert.That(manifest.ResolvedImagePath, Is.EqualTo(BinaryTesting.Fixture("blob.bin")));
      Assert.That(manifest.Sha256, Is.EqualTo(BinaryTesting.BlobSha256));
      Assert.That(manifest.Container, Is.EqualTo("raw"));
      Assert.That((manifest.Isa, manifest.Mode, manifest.ByteOrder, manifest.LoadAddress), Is.EqualTo(("x86", "protected32", (ByteOrder?)ByteOrder.Little, (ulong?)0x00401000)));
      Assert.That(manifest.Entries.Single(), Is.EqualTo(new ManifestEntry("bswap32", 0x00401000, new AbiSignature("cdecl", ["u32"], "u32"), [])));
      Assert.That(manifest.CpuFeatures, Is.Empty);
      Assert.That(manifest.CodeRanges, Is.Empty);
      Assert.That(manifest.Relocations, Is.Null);
      Assert.That(manifest.MemoryMap, Is.Null);
      Assert.That(manifest.Observe, Is.Null);
      Assert.That(manifest.StackBytes, Is.Null);
      Assert.That(manifest.ReferenceRunner, Is.Null);
      Assert.That(manifest.AcceptHeuristicFunctions, Is.False);
    });
  }

  /// <summary>Row 2: every field of the full manifest is readable.</summary>
  [Test]
  public void Full_EveryFieldReadable() {
    var manifest = Load("full.json");
    Assert.Multiple(() => {
      Assert.That(manifest.CpuFeatures, Is.EqualTo(new[] { "bswap" }));
      Assert.That(manifest.Entries.Select(entry => entry.Name), Is.EqualTo(new[] { "bswap32", "fill" }));
      Assert.That(manifest.CodeRanges.Single(), Is.EqualTo(new ManifestRange(0x00401000, 0x00401007)));
      Assert.That(manifest.DataRanges.Single(), Is.EqualTo(new ManifestRange(0x00401007, 0x00401008)));
      Assert.That(manifest.Relocations!.Value.Single(), Is.EqualTo(new ImageRelocation(0x00401003, RelocationKind.Absolute, "memcpy", 4, 0)));
      Assert.That(manifest.Imports.Single(), Is.EqualTo(new ManifestImport(0x00402000, "memcpy", "libc.so.6", manifest.Imports[0].Abi)));
      Assert.That(manifest.Imports[0].Abi, Is.EqualTo(new AbiSignature("cdecl", ["u8*", "u8*", "u32"], "u8*")));
      Assert.That(manifest.StackBytes, Is.EqualTo(4096));
      Assert.That(manifest.MemoryMapPath, Is.Null);
      Assert.That(manifest.MemoryMap!.Value, Is.EqualTo(new[] {
        new AddressRange("text", 0x00400000, 0x0040FFFF, AddressKind.Rom, true),
        new AddressRange("stack", 0x00500000, 0x0050FFFF, AddressKind.Stack, false)
      }));
      Assert.That(manifest.Observe!.Value.GetProperty("registers")[0].GetString(), Is.EqualTo("eax"));
      Assert.That(manifest.ReferenceRunner, Is.EqualTo("qemu-i386"));
      Assert.That(manifest.AcceptHeuristicFunctions, Is.True);
    });
  }

  /// <summary>Row 3: a buffer whose length is another parameter, and one whose length is a constant.</summary>
  [Test]
  public void Buffers_LengthFromParameterAndConstant() {
    var fill = Load("full.json").Entries[1];
    Assert.That(fill.Buffers, Is.EqualTo(new[] {
      new BufferContract(0, 1, null, BufferAccess.Read),
      new BufferContract(2, null, 16, BufferAccess.Write)
    }));
  }

  /// <summary>Row 4: <c>"from-container"</c> leaves the relocations to the container, a list states them.</summary>
  [Test]
  public void Relocations_FromContainerOrList() {
    var fromContainer = BinaryTesting.Parse(Min(json => json["relocations"] = "from-container"));
    var list = BinaryTesting.Parse(Min(json => json["relocations"] = new JsonArray(new JsonObject { ["address"] = "0x00401001", ["kind"] = "pc-relative" })));
    Assert.That(fromContainer.Value!.Relocations, Is.Null);
    Assert.That(list.Value!.Relocations!.Value.Single(), Is.EqualTo(new ImageRelocation(0x00401001, RelocationKind.PcRelative, null, 0, 0)));
  }

  /// <summary>Every relocation kind the manifest names.</summary>
  [TestCase("absolute", RelocationKind.Absolute)]
  [TestCase("pc-relative", RelocationKind.PcRelative)]
  [TestCase("import-slot", RelocationKind.ImportSlot)]
  [TestCase("base-relative", RelocationKind.BaseRelative)]
  public void Relocations_Kinds(string text, RelocationKind kind)
    => Assert.That(BinaryTesting.Parse(Min(json => json["relocations"] = new JsonArray(new JsonObject { ["address"] = "0x1", ["kind"] = text, ["addend"] = -8 }))).Value!.Relocations!.Value[0],
      Is.EqualTo(new ImageRelocation(1, kind, null, -8, 0)));

  /// <summary>Row 5: a memory map referenced by path reads as the same regions as the inline one.</summary>
  [Test]
  public void MemoryMap_PathEqualsInline() {
    var inline = Load("full.json");
    var referenced = BinaryManifest.Parse(Min(json => json["memoryMap"] = "memory-map.json").ToJsonString(), "test.json", BinaryTesting.Fixtures).Value!;
    Assert.That(referenced.MemoryMapPath, Is.EqualTo("memory-map.json"));
    Assert.That(referenced.MemoryMap!.Value, Is.EqualTo(inline.MemoryMap!.Value));
  }

  /// <summary>Every region kind.</summary>
  [TestCase("ram", AddressKind.Ram)]
  [TestCase("rom", AddressKind.Rom)]
  [TestCase("stack", AddressKind.Stack)]
  [TestCase("mmio", AddressKind.Mmio)]
  [TestCase("unmapped", AddressKind.Unmapped)]
  public void MemoryMap_Kinds(string text, AddressKind kind) {
    var map = new JsonObject { ["regions"] = new JsonArray(new JsonObject { ["name"] = "r", ["start"] = "0x10", ["end"] = "0x20", ["kind"] = text, ["executable"] = false }) };
    Assert.That(BinaryTesting.Parse(Min(json => json["memoryMap"] = map)).Value!.MemoryMap!.Value.Single(), Is.EqualTo(new AddressRange("r", 0x10, 0x1F, kind, false)));
  }

  /// <summary>Every buffer access.</summary>
  [TestCase("read", BufferAccess.Read)]
  [TestCase("write", BufferAccess.Write)]
  [TestCase("readwrite", BufferAccess.ReadWrite)]
  public void Buffers_Access(string text, BufferAccess access) {
    var json = Min(json => {
      Entry(json)["abi"] = "cdecl (u8*) -> void";
      Entry(json)["buffers"] = new JsonArray(new JsonObject { ["parameter"] = 0, ["length"] = new JsonObject { ["bytes"] = 4 }, ["access"] = text });
    });
    Assert.That(BinaryTesting.Parse(json).Value!.Entries[0].Buffers.Single().Access, Is.EqualTo(access));
  }

  /// <summary>Byte order <c>big</c>.</summary>
  [Test]
  public void ByteOrder_Big() => Assert.That(BinaryTesting.Parse(Min(json => json["byteOrder"] = "big")).Value!.ByteOrder, Is.EqualTo(ByteOrder.Big));

  /// <summary>Byte order and load address may be left out; the container or the codec decides whether it needs them.</summary>
  [Test]
  public void OptionalFields_MayBeAbsent() {
    var manifest = BinaryTesting.Parse(Min(json => { json.Remove("byteOrder"); json.Remove("loadAddress"); json.Remove("container"); })).Value!;
    Assert.That((manifest.ByteOrder, manifest.LoadAddress, manifest.Container), Is.EqualTo(((ByteOrder?)null, (ulong?)null, (string?)null)));
  }

  /// <summary>Row 8: exactly one entry is accepted; the boundary below is <c>invalid-no-entries.json</c>.</summary>
  [Test]
  public void Entries_ExactlyOne_Accepted() => Assert.That(Load("min.json").Entries, Has.Length.EqualTo(1));

  /// <summary>Rows 7, 8 and 9: each invalid fixture is refused naming its rule.</summary>
  [TestCase("invalid-missing-isa.json", "'isa' is required in the manifest")]
  [TestCase("invalid-missing-mode.json", "'mode' is required in the manifest")]
  [TestCase("invalid-unknown-field.json", "unknown field 'origin' in the manifest")]
  [TestCase("invalid-address-without-0x.json", "'address' in entries[0] must be an address written as 0x")]
  [TestCase("invalid-sha256-63-digits.json", "'image.sha256' must be 64 hexadecimal digits")]
  [TestCase("invalid-duplicate-entry.json", "entry name 'bswap32' is used more than once")]
  [TestCase("invalid-no-entries.json", "the manifest needs at least one entry")]
  [TestCase("invalid-zero-length-range.json", "codeRanges[0] must end after it starts")]
  [TestCase("invalid-schema-version.json", "'schemaVersion' must be 1")]
  [TestCase("invalid-container.json", "'container' must be one of elf, pe, raw, not 'macho'")]
  [TestCase("invalid-buffer-length.json", "entries[0].buffers[0].length must give exactly one of 'parameter' and 'bytes'")]
  [TestCase("invalid-abi.json", "'abi' in entries[0] must read")]
  public void InvalidFixture_IsRefusedNamingTheRule(string fixture, string rule) {
    var result = BinaryManifest.Load(BinaryTesting.Fixture(fixture));
    var problem = result.Problems.Single();
    Assert.Multiple(() => {
      Assert.That(result.Value, Is.Null);
      Assert.That(problem.Code, Is.EqualTo("PLY4055"));
      Assert.That(problem.ExitCode, Is.EqualTo(ExitCode.EnvironmentError));
      Assert.That(problem.Message, Does.StartWith($"binary manifest '{BinaryTesting.Fixture(fixture)}' is not valid: "));
      Assert.That(problem.Message, Does.Contain(rule));
    });
  }

  private static IEnumerable<TestCaseData> CodeRules() {
    TestCaseData Case(string name, Action<JsonObject> change, string rule) => new TestCaseData(change, rule).SetName("CodeRule_" + name);
    yield return Case("ImageNotObject", json => json["image"] = "blob.bin", "'image' must be an object");
    yield return Case("ImageMissing", json => json.Remove("image"), "'image' is required in the manifest");
    yield return Case("ImagePathMissing", json => json["image"]!.AsObject().Remove("path"), "'path' is required in 'image'");
    yield return Case("Sha256NotText", json => json["image"]!["sha256"] = 5, "'sha256' in 'image' must be a non-empty string");
    yield return Case("SchemaVersionMissing", json => json.Remove("schemaVersion"), "'schemaVersion' is required in the manifest");
    yield return Case("SchemaVersionFraction", json => json["schemaVersion"] = 1.5, "'schemaVersion' in the manifest must be an integer of at least 0");
    yield return Case("SchemaVersionText", json => json["schemaVersion"] = "1", "'schemaVersion' in the manifest must be an integer");
    yield return Case("ByteOrder", json => json["byteOrder"] = "middle", "'byteOrder' must be 'little' or 'big', not 'middle'");
    yield return Case("EntriesMissing", json => json.Remove("entries"), "'entries' is required");
    yield return Case("EntriesNotList", json => json["entries"] = new JsonObject(), "'entries' must be a list");
    yield return Case("EntryNotObject", json => json["entries"] = new JsonArray("x"), "entries[0] must be an object");
    yield return Case("CpuFeatureEmpty", json => json["cpuFeatures"] = new JsonArray(""), "cpuFeatures[0] must be a non-empty string");
    yield return Case("AddressTooLong", json => json["loadAddress"] = "0x00000000000000001", "'loadAddress' in the manifest must be an address");
    yield return Case("AddressNotHex", json => json["loadAddress"] = "0xZZ", "'loadAddress' in the manifest must be an address");
    yield return Case("AddressNumber", json => json["loadAddress"] = 4198400, "'loadAddress' in the manifest must be a non-empty string");
    yield return Case("StackField", json => json["stack"] = new JsonObject { ["size"] = 4 }, "unknown field 'size' in 'stack'");
    yield return Case("StackZero", json => json["stack"] = new JsonObject { ["bytes"] = 0 }, "'bytes' in 'stack' must be an integer of at least 1");
    yield return Case("BufferAccess", json => {
      Entry(json)["abi"] = "cdecl (u8*) -> void";
      Entry(json)["buffers"] = new JsonArray(new JsonObject { ["parameter"] = 0, ["length"] = new JsonObject { ["bytes"] = 4 }, ["access"] = "execute" });
    }, "entries[0].buffers[0].access must be 'read', 'write' or 'readwrite', not 'execute'");
    yield return Case("BufferParameterBeyond", json => Entry(json)["buffers"] = new JsonArray(new JsonObject { ["parameter"] = 1, ["length"] = new JsonObject { ["bytes"] = 4 }, ["access"] = "read" }),
      "entries[0].buffers[0] names parameter 1, but entry 'bswap32' has 1");
    yield return Case("BufferLengthParameterBeyond", json => Entry(json)["buffers"] = new JsonArray(new JsonObject { ["parameter"] = 0, ["length"] = new JsonObject { ["parameter"] = 3 }, ["access"] = "read" }),
      "entries[0].buffers[0] names parameter 3, but entry 'bswap32' has 1");
    yield return Case("BufferLengthMissing", json => Entry(json)["buffers"] = new JsonArray(new JsonObject { ["parameter"] = 0, ["length"] = new JsonObject(), ["access"] = "read" }),
      "entries[0].buffers[0].length must give exactly one of 'parameter' and 'bytes'");
    yield return Case("RelocationsText", json => json["relocations"] = "applied", "'relocations' must be \"from-container\" or a list");
    yield return Case("RelocationsObject", json => json["relocations"] = new JsonObject(), "'relocations' must be a list");
    yield return Case("RelocationKind", json => json["relocations"] = new JsonArray(new JsonObject { ["address"] = "0x1", ["kind"] = "got" }),
      "relocations[0].kind must be 'absolute', 'pc-relative', 'import-slot' or 'base-relative', not 'got'");
    yield return Case("ImportAbi", json => json["imports"] = new JsonArray(new JsonObject { ["slot"] = "0x1", ["symbol"] = "f", ["abi"] = "f" }), "'abi' in imports[0] must read");
    yield return Case("Heuristics", json => json["acceptHeuristicFunctions"] = "yes", "'acceptHeuristicFunctions' in the manifest must be true or false");
    yield return Case("DataRangeBackwards", json => json["dataRanges"] = new JsonArray(new JsonObject { ["start"] = "0x10", ["end"] = "0x8" }), "dataRanges[0] must end after it starts (0x00000010..0x00000008 holds no byte)");
    yield return Case("RegionBackwards", json => json["memoryMap"] = new JsonObject { ["regions"] = new JsonArray(new JsonObject { ["name"] = "r", ["start"] = "0x10", ["end"] = "0x10", ["kind"] = "ram" }) },
      "regions[0] must end after it starts");
    yield return Case("RegionKind", json => json["memoryMap"] = new JsonObject { ["regions"] = new JsonArray(new JsonObject { ["name"] = "r", ["start"] = "0x10", ["end"] = "0x20", ["kind"] = "flash" }) },
      "regions[0].kind must be 'ram', 'rom', 'stack', 'mmio' or 'unmapped', not 'flash'");
    yield return Case("MemoryMapField", json => json["memoryMap"] = new JsonObject { ["ranges"] = new JsonArray() }, "unknown field 'ranges' in 'memoryMap'");
  }

  /// <summary>Row 9: the rules the loader enforces beyond the fixtures, each refused naming it.</summary>
  [TestCaseSource(nameof(CodeRules))]
  public void CodeRule_IsRefusedNamingTheRule(Action<JsonObject> change, string rule) {
    var problem = Refusal(Min(change));
    Assert.That(problem.Code, Is.EqualTo("PLY4055"));
    Assert.That(problem.Message, Does.Contain(rule));
  }

  /// <summary>Text that is no JSON is refused as such.</summary>
  [Test]
  public void NotJson_IsRefused() {
    var problem = BinaryManifest.Parse("{ isa", "broken.json", ".").Problems.Single();
    Assert.That(problem.Message, Does.StartWith("binary manifest 'broken.json' is not valid: not JSON: "));
  }

  /// <summary>A manifest that is not an object is refused.</summary>
  [Test]
  public void NotAnObject_IsRefused()
    => Assert.That(BinaryManifest.Parse("[]", "list.json", ".").Problems.Single().Message, Does.EndWith("the manifest must be an object"));

  /// <summary>Row 14 for the manifest: a missing file is the command line's unreadable input, <c>PLY0004</c>.</summary>
  [Test]
  public void MissingFile_IsUnreadable() {
    var path = BinaryTesting.Fixture("absent.json");
    var problem = BinaryManifest.Load(path).Problems.Single();
    Assert.That((problem.Code, problem.ExitCode, problem.Message, problem.File), Is.EqualTo(("PLY0004", (ExitCode?)ExitCode.EnvironmentError, $"input '{path}' does not exist or is not readable", path)));
  }

  /// <summary>A referenced memory map that is missing is <c>PLY0004</c> naming it.</summary>
  [Test]
  public void MemoryMapFile_Missing_IsUnreadable() {
    var problem = Refusal(Min(json => json["memoryMap"] = "absent-map.json"));
    Assert.That((problem.Code, problem.File), Is.EqualTo(("PLY0004", Path.Combine(BinaryTesting.Fixtures, "absent-map.json"))));
  }

  /// <summary>A referenced memory map that is no JSON, or breaks a rule, is <c>PLY4055</c> naming that file.</summary>
  [TestCase("{", "not JSON")]
  [TestCase("{\"regions\": 3}", "'regions' must be a list")]
  public void MemoryMapFile_Invalid_NamesTheMapFile(string text, string rule) {
    using var scratch = new BinaryTesting();
    var map = scratch.Write("map.json", text);
    var problem = BinaryManifest.Parse(Min(json => json["memoryMap"] = "map.json").ToJsonString(), "test.json", scratch.Directory).Problems.Single();
    Assert.That(problem.Message, Does.StartWith($"binary manifest '{map}' is not valid: {rule}"));
  }

  /// <summary>Row 12 at the API: a command-line value that contradicts the manifest is <c>PLY4057</c> with exit 2.</summary>
  [Test]
  public void CheckAgainst_FlagContradictingTheManifest_IsUsageError() {
    var problem = Load("min.json").CheckAgainst("isa", "arm32", "--from")!;
    Assert.Multiple(() => {
      Assert.That(problem.Code, Is.EqualTo("PLY4057"));
      Assert.That(problem.ExitCode, Is.EqualTo(ExitCode.UsageError));
      Assert.That(problem.Message, Is.EqualTo("manifest states isa 'x86' but --from states 'arm32'"));
    });
  }

  /// <summary>Row 12, the agreeing and the open cases, for every field compared.</summary>
  [TestCase("isa", "x86", false)]
  [TestCase("mode", "protected32", false)]
  [TestCase("mode", "real16", true)]
  [TestCase("byteOrder", "little", false)]
  [TestCase("byteOrder", "big", true)]
  [TestCase("container", "raw", false)]
  [TestCase("container", "elf", true)]
  [TestCase("loadAddress", "0x401000", false)]
  [TestCase("loadAddress", "0x401001", true)]
  [TestCase("loadAddress", "4198400", true)]
  public void CheckAgainst_EveryField(string field, string value, bool contradicts)
    => Assert.That(Load("min.json").CheckAgainst(field, value, "the command line") is not null, Is.EqualTo(contradicts));

  /// <summary>A field the manifest leaves open is contradicted by nothing.</summary>
  [TestCase("byteOrder")]
  [TestCase("container")]
  [TestCase("loadAddress")]
  public void CheckAgainst_OpenField_AcceptsAnything(string field) {
    var manifest = BinaryTesting.Parse(Min(json => { json.Remove("byteOrder"); json.Remove("loadAddress"); json.Remove("container"); })).Value!;
    Assert.That(manifest.CheckAgainst(field, "anything", "--x"), Is.Null);
  }

  /// <summary>A field the manifest does not compare is a programming error.</summary>
  [Test]
  public void CheckAgainst_UnknownField_Throws()
    => Assert.That(() => Load("min.json").CheckAgainst("stack", "1", "--stack"), Throws.ArgumentException.With.Message.Contains("'stack' is not a field the manifest compares"));

  /// <summary>The schema is valid 2020-12.</summary>
  [Test]
  public void Schema_IsValidAgainstTheMetaSchema() {
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "binary-manifest.schema.json")));
    Assert.That(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
  }

  /// <summary>The valid fixtures and the memory map conform to the schema, which shares no code with the loader.</summary>
  [TestCase("min.json")]
  [TestCase("full.json")]
  public void Schema_AcceptsTheValidFixtures(string fixture) {
    using var document = JsonDocument.Parse(File.ReadAllText(BinaryTesting.Fixture(fixture)));
    Assert.That(_schema.Evaluate(document.RootElement).IsValid, Is.True);
  }

  /// <summary>
  /// The invalid fixtures fail the schema too, except the two rules the schema cannot state (its
  /// description lists them): unique entry names and a range that ends after it starts.
  /// </summary>
  [TestCase("invalid-missing-isa.json", false)]
  [TestCase("invalid-missing-mode.json", false)]
  [TestCase("invalid-unknown-field.json", false)]
  [TestCase("invalid-address-without-0x.json", false)]
  [TestCase("invalid-sha256-63-digits.json", false)]
  [TestCase("invalid-no-entries.json", false)]
  [TestCase("invalid-schema-version.json", false)]
  [TestCase("invalid-container.json", false)]
  [TestCase("invalid-buffer-length.json", false)]
  [TestCase("invalid-abi.json", false)]
  [TestCase("invalid-duplicate-entry.json", true)]
  [TestCase("invalid-zero-length-range.json", true)]
  public void Schema_RefusesTheInvalidFixtures(string fixture, bool onlyTheLoaderRefuses) {
    using var document = JsonDocument.Parse(File.ReadAllText(BinaryTesting.Fixture(fixture)));
    Assert.That(_schema.Evaluate(document.RootElement).IsValid, Is.EqualTo(onlyTheLoaderRefuses));
  }

  /// <summary>The fixture blob is the eight bytes of the issue, and its hash is the one <c>sha256sum</c> printed.</summary>
  [Test]
  public void Blob_IsTheIssuesBytes() {
    var bytes = File.ReadAllBytes(BinaryTesting.Fixture("blob.bin"));
    Assert.That(bytes, Is.EqualTo(BinaryTesting.Blob.ToArray()));
    Assert.That(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), Is.EqualTo(BinaryTesting.BlobSha256));
  }

  /// <summary>The <c>abi</c> text: no parameters, <c>void</c>, no arrow, and text that is not one.</summary>
  [TestCase("cdecl (u32) -> u32", "cdecl", "u32", "u32")]
  [TestCase("stdcall () -> void", "stdcall", "", null)]
  [TestCase("fastcall (void)", "fastcall", "", null)]
  [TestCase(" sysv ( u8* , u64 )->u8* ", "sysv", "u8*,u64", "u8*")]
  public void Abi_Parses(string text, string convention, string parameters, string? result) {
    var abi = AbiSignature.Parse(text)!;
    Assert.That((abi.Convention, string.Join(",", abi.Parameters), abi.Result), Is.EqualTo((convention, parameters, result)));
  }

  /// <summary>Text that is no <c>abi</c>.</summary>
  [TestCase("cdecl u32 -> u32")]
  [TestCase("(u32)")]
  [TestCase("cdecl (u32) ->")]
  public void Abi_Refuses(string text) => Assert.That(AbiSignature.Parse(text), Is.Null);

  /// <summary>Value equality of the records that hold lists.</summary>
  [Test]
  public void Records_CompareByValue() {
    var abi = new AbiSignature("cdecl", ["u32"], "u32");
    var entry = new ManifestEntry("f", 1, abi, [new BufferContract(0, null, 4, BufferAccess.Read)]);
    var contract = new EntryContract("f", 1, "cdecl", ["u32"], null, []);
    Assert.Multiple(() => {
      Assert.That(abi, Is.EqualTo(new AbiSignature("cdecl", ["u32"], "u32")));
      Assert.That(abi.GetHashCode(), Is.EqualTo(new AbiSignature("cdecl", ["u32"], "u32").GetHashCode()));
      Assert.That(abi.Equals(null), Is.False);
      Assert.That(entry, Is.EqualTo(entry with { Buffers = [new BufferContract(0, null, 4, BufferAccess.Read)] }));
      Assert.That(entry.GetHashCode(), Is.EqualTo((entry with { }).GetHashCode()));
      Assert.That(entry, Is.Not.EqualTo(entry with { Buffers = [] }));
      Assert.That(entry.Equals(null), Is.False);
      Assert.That(contract, Is.EqualTo(contract with { Parameters = ["u32"] }));
      Assert.That(contract.GetHashCode(), Is.EqualTo((contract with { }).GetHashCode()));
      Assert.That(contract, Is.Not.EqualTo(contract with { Parameters = ["u64"] }));
      Assert.That(contract.Equals(null), Is.False);
      Assert.That(new ManifestRange(1, 3).Contains(3), Is.False);
    });
  }

  /// <summary>The names of the byte orders.</summary>
  [Test]
  public void ByteOrder_Names() => Assert.That((BinaryManifest.Name(ByteOrder.Little), BinaryManifest.Name(ByteOrder.Big)), Is.EqualTo(("little", "big")));

  /// <summary>A result is a value or at least one diagnostic.</summary>
  [Test]
  public void Result_Guards() {
    Assert.That(() => BinaryResult<string>.Success(null!), Throws.ArgumentNullException);
    Assert.That(() => BinaryResult<string>.Failure([]), Throws.ArgumentException);
    Assert.That(() => BinaryResult<string>.Failure((Diagnostic)null!), Throws.ArgumentNullException);
    Assert.That(BinaryResult<string>.Success("x").Problems, Is.Empty);
  }

}
