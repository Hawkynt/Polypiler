using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.BinaryFormats;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Regions;

namespace Polypiler.Tests.BinaryFormats;

/// <summary>
/// <see cref="ImageBuilder"/> over the MACH-BIN-MANIFEST fixtures: the hash check, codec selection by
/// manifest and by magic, the merge of manifest and container, entries and ranges, the address map and
/// the entry contracts; plus <see cref="BinaryCodecRegistry"/>, <see cref="RawBinaryCodec"/> and the
/// image model.
/// </summary>
[TestFixture]
public sealed class ImageBuilderTests {

  private BinaryTesting _scratch = null!;

  [SetUp]
  public void SetUp() => this._scratch = new BinaryTesting();

  [TearDown]
  public void TearDown() => this._scratch.Dispose();

  /// <summary>A codec for ELF that states what the test gives it.</summary>
  private sealed class FakeElf(BinaryImage image) : IBinaryCodec {

    public string Id => "elf";

    public bool CanRead(ReadOnlySpan<byte> prefix) => prefix.StartsWith((byte[])[0x7F, 0x45, 0x4C, 0x46]);

    public BinaryResult<BinaryImage> Read(ReadOnlyMemory<byte> file, BinaryManifest? manifest) => BinaryResult<BinaryImage>.Success(image);

  }

  private static readonly byte[] _elf = [0x7F, 0x45, 0x4C, 0x46, 1, 1, 1, 0, 0x8B, 0x44, 0x24, 0x04, 0x0F, 0xC8, 0xC3, 0x90];

  private static BinaryImage ElfImage(string isa = "x86", string mode = "protected32", ByteOrder order = ByteOrder.Little)
    => new("elf", isa, mode, order,
      [new ImageSection(".text", 0x00401000, BinaryTesting.Blob.ToArray(), 8, true, false, true), new ImageSection(".bss", 0x00402000, ReadOnlyMemory<byte>.Empty, 0x100, true, true, false)],
      [new ImageSymbol("bswap32", 0x00401000, 7, SymbolKind.Function, null)],
      [new ImageRelocation(0x00401004, RelocationKind.ImportSlot, "puts", 0, 7)],
      [new ImageImport(0x00402000, "puts", "libc.so.6", null)],
      [0x00401000]);

  private static BinaryCodecRegistry Registry(BinaryImage image) {
    var registry = BinaryCodecRegistry.CreateDefault();
    registry.Register(new FakeElf(image));
    return registry;
  }

  private static JsonObject Min(Action<JsonObject>? change = null) {
    var json = BinaryTesting.Json("min.json");
    change?.Invoke(json);
    return json;
  }

  private static JsonObject Elf(Action<JsonObject>? change = null) => Min(json => {
    json["image"]!["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(_elf));
    json.Remove("container");
    json.Remove("loadAddress");
    change?.Invoke(json);
  });

  private BinaryResult<LoadedBinary> LoadElf(JsonObject json, BinaryImage image) {
    var manifest = this._scratch.Manifest(json);
    return ImageBuilder.Load(this._scratch.Write("elf.bin", _elf), manifest, Registry(image));
  }

  private LoadedBinary Loaded(JsonObject json, MachineProfileCatalog? catalog = null) {
    var result = ImageBuilder.Load(this._scratch.Manifest(json), catalog: catalog);
    Assert.That(result.Problems, Is.Empty);
    return result.Value!;
  }

  private Diagnostic Refusal(JsonObject json, MachineProfileCatalog? catalog = null) {
    var result = ImageBuilder.Load(this._scratch.Manifest(json), catalog: catalog);
    Assert.That(result.Value, Is.Null);
    return result.Problems.Single();
  }

  private static JsonArray Ranges(params (string Start, string End)[] ranges) => new([.. ranges.Select(range => (JsonNode)new JsonObject { ["start"] = range.Start, ["end"] = range.End })]);

  /// <summary>Row 1 and scenario 1: the minimal manifest gives one executable section, its map and one entry contract.</summary>
  [Test]
  public void Min_LoadsImageMapAndContract() {
    var loaded = ImageBuilder.Load(BinaryTesting.Fixture("blob.bin"), BinaryManifest.Load(BinaryTesting.Fixture("min.json")).Value).Value!;
    var section = loaded.Image.Sections.Single();
    Assert.Multiple(() => {
      Assert.That((loaded.Image.Container, loaded.Image.Isa, loaded.Image.Mode, loaded.Image.ByteOrder), Is.EqualTo(("raw", "x86", "protected32", ByteOrder.Little)));
      Assert.That((section.Address, section.Bytes.Length, section.Readable, section.Writable, section.Executable), Is.EqualTo((0x00401000UL, 8, true, false, true)));
      Assert.That(section.Bytes.ToArray(), Is.EqualTo(BinaryTesting.Blob.ToArray()));
      Assert.That(loaded.Image.Entries, Is.EqualTo(new[] { 0x00401000UL }));
      Assert.That(loaded.AddressMap.Ranges.Single(), Is.EqualTo(new AddressRange("raw", 0x00401000, 0x00401007, AddressKind.Rom, true)));
      Assert.That(loaded.Entries.Single(), Is.EqualTo(new EntryContract("bswap32", 0x00401000, "cdecl", ["u32"], "u32", [])));
      Assert.That(loaded.Observe, Is.Null);
      Assert.That(loaded.Image.ByteAt(0x00401007), Is.EqualTo(0x90));
    });
  }

  /// <summary>Row 2: the full manifest, every field reaching the loaded binary.</summary>
  [Test]
  public void Full_EveryFieldReachesTheLoadedBinary() {
    var loaded = ImageBuilder.Load(BinaryManifest.Load(BinaryTesting.Fixture("full.json")).Value!).Value!;
    Assert.Multiple(() => {
      Assert.That(loaded.Entries.Select(entry => entry.Name), Is.EqualTo(new[] { "bswap32", "fill" }));
      Assert.That(loaded.Entries[1], Is.EqualTo(new EntryContract("fill", 0x00401006, "cdecl", ["u8*", "u32", "u8*"], null,
        [new BufferContract(0, 1, null, BufferAccess.Read), new BufferContract(2, null, 16, BufferAccess.Write)])));
      Assert.That(loaded.Image.Entries, Is.EqualTo(new[] { 0x00401000UL, 0x00401006UL }));
      Assert.That(loaded.Image.Relocations.Single().Symbol, Is.EqualTo("memcpy"));
      Assert.That(loaded.Image.Imports.Single(), Is.EqualTo(new ImageImport(0x00402000, "memcpy", "libc.so.6", null)));
      Assert.That(loaded.AddressMap.RangeAt(0x00500000)!.Kind, Is.EqualTo(AddressKind.Stack));
      Assert.That(loaded.Observe!.Value.GetProperty("registers")[0].GetString(), Is.EqualTo("eax"));
      Assert.That(loaded.Manifest!.StackBytes, Is.EqualTo(4096));
      Assert.That(loaded.IsDecodable(0x00401006), Is.True);
      Assert.That(loaded.IsDecodable(0x00401007), Is.False, "outside the code ranges");
      Assert.That(loaded.IsDecodable(0x00403000), Is.False, "outside the image");
    });
  }

  /// <summary>Row 4: <c>"from-container"</c> keeps the container's relocations, a list replaces them.</summary>
  [Test]
  public void Relocations_FromContainerOrManifest() {
    var fromContainer = this.LoadElf(Elf(json => json["relocations"] = "from-container"), ElfImage()).Value!;
    var fromManifest = this.LoadElf(Elf(json => json["relocations"] = new JsonArray(new JsonObject { ["address"] = "0x00401001", ["kind"] = "absolute" })), ElfImage()).Value!;
    Assert.That(fromContainer.Image.Relocations.Single().RawType, Is.EqualTo(7));
    Assert.That(fromManifest.Image.Relocations.Single(), Is.EqualTo(new ImageRelocation(0x00401001, RelocationKind.Absolute, null, 0, 0)));
  }

  /// <summary>Row 5: a memory map inline and by path give the same address map.</summary>
  [Test]
  public void MemoryMap_InlineAndPath_SameAddressMap() {
    var regions = BinaryTesting.Json("memory-map.json");
    var inline = this.Loaded(Min(json => json["memoryMap"] = regions));
    this._scratch.Write("map.json", regions.ToJsonString());
    var referenced = this.Loaded(Min(json => json["memoryMap"] = "map.json"));
    Assert.That(referenced.AddressMap.Ranges, Is.EqualTo(inline.AddressMap.Ranges));
    Assert.That(referenced.AddressMap.Ranges, Has.Length.EqualTo(2));
  }

  /// <summary>Row 6: an entry at the first and the last byte is accepted, one past the end is <c>PLY4058</c>.</summary>
  [TestCase("0x00401000", true)]
  [TestCase("0x00401007", true)]
  [TestCase("0x00401008", false)]
  public void Entry_Boundaries(string address, bool accepted) {
    var result = ImageBuilder.Load(this._scratch.Manifest(Min(json => json["entries"]![0]!["address"] = address)));
    Assert.That(result.Succeeded, Is.EqualTo(accepted));
    if (!accepted)
      Assert.That(result.Problems.Single().Message, Is.EqualTo("entry 'bswap32' at 0x00401008 is not inside executable code"));
  }

  /// <summary>Row 7: a code range equal to the whole image and an empty list are accepted (the zero-length range is a manifest rule).</summary>
  [Test]
  public void CodeRanges_WholeImageOrNone() {
    var whole = this.Loaded(Min(json => json["codeRanges"] = Ranges(("0x00401000", "0x00401008"))));
    var none = this.Loaded(Min(json => json["codeRanges"] = new JsonArray()));
    Assert.That(whole.IsDecodable(0x00401007), Is.True);
    Assert.That(none.IsDecodable(0x00401007), Is.True);
  }

  /// <summary>An entry inside the image but outside every code range is <c>PLY4058</c>.</summary>
  [Test]
  public void Entry_OutsideTheCodeRanges_IsRefused() {
    var problem = this.Refusal(Min(json => json["codeRanges"] = Ranges(("0x00401004", "0x00401008"))));
    Assert.That(problem.Message, Is.EqualTo("entry 'bswap32' at 0x00401000 is not inside executable code"));
  }

  /// <summary>An entry in a section that is not executable is <c>PLY4058</c>.</summary>
  [Test]
  public void Entry_InDataSection_IsRefused() {
    var result = this.LoadElf(Elf(json => json["entries"]![0]!["address"] = "0x00402000"), ElfImage());
    Assert.That(result.Problems.Single().Code, Is.EqualTo("PLY4058"));
  }

  /// <summary>Code and data ranges no section holds are <c>PLY4058</c> in its range form; each is reported.</summary>
  [Test]
  public void Ranges_OutsideTheImage_AreRefused() {
    var result = ImageBuilder.Load(this._scratch.Manifest(Min(json => {
      json["codeRanges"] = Ranges(("0x00401000", "0x00401009"));
      json["dataRanges"] = Ranges(("0x00500000", "0x00500004"));
    })));
    Assert.That(result.Problems.Select(problem => problem.Message), Is.EqualTo(new[] {
      "range 0x00401000..0x00401009 lies outside the image",
      "range 0x00500000..0x00500004 lies outside the image"
    }));
    Assert.That(result.Problems.All(problem => problem.Code == "PLY4058" && problem.ExitCode == ExitCode.EnvironmentError), Is.True);
  }

  /// <summary>Row 10 and scenario 2: one changed byte is <c>PLY4056</c> showing both hashes.</summary>
  [Test]
  public void ChangedImage_IsRefusedShowingBothHashes() {
    var manifest = BinaryManifest.Load(BinaryTesting.Fixture("min.json")).Value!;
    var changed = this._scratch.Write("blob.bin", (byte[])[0x8B, 0x44, 0x24, 0x04, 0x0F, 0xC8, 0xC3, 0xCC]);
    var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(changed)));
    var problem = ImageBuilder.Load(changed, manifest).Problems.Single();
    Assert.That((problem.Code, problem.Message), Is.EqualTo(("PLY4056", $"'{changed}' has SHA-256 {actual}, the manifest expects {BinaryTesting.BlobSha256}")));
  }

  /// <summary>Row 11: a container that names another machine, mode or byte order is <c>PLY4057</c>, each reported.</summary>
  [Test]
  public void ContainerContradictingTheManifest_IsRefused() {
    var result = this.LoadElf(Elf(), ElfImage("x64", "long64", ByteOrder.Big));
    Assert.That(result.Problems.Select(problem => problem.Message), Is.EqualTo(new[] {
      "manifest states isa 'x86' but the container states 'x64'",
      "manifest states mode 'protected32' but the container states 'long64'",
      "manifest states byteOrder 'little' but the container states 'big'"
    }));
    Assert.That(result.Problems.All(problem => problem.Code == "PLY4057" && problem.ExitCode == ExitCode.EnvironmentError), Is.True);
  }

  /// <summary>The container supplies sections, symbols, relocations and imports; the manifest adds imports and entries.</summary>
  [Test]
  public void Merge_ContainerFactsAndManifestAdditions() {
    var loaded = this.LoadElf(Elf(json => {
      json.Remove("byteOrder");
      json["imports"] = new JsonArray(
        new JsonObject { ["slot"] = "0x00402000", ["symbol"] = "puts" },
        new JsonObject { ["slot"] = "0x00402008", ["symbol"] = "exit", ["library"] = "libc.so.6" });
      json["entries"]![0]!["address"] = "0x00401006";
    }), ElfImage()).Value!;
    Assert.Multiple(() => {
      Assert.That(loaded.Image.Container, Is.EqualTo("elf"));
      Assert.That(loaded.Image.Symbols.Single().Name, Is.EqualTo("bswap32"));
      Assert.That(loaded.Image.Imports, Is.EqualTo(new[] { new ImageImport(0x00402000, "puts", "libc.so.6", null), new ImageImport(0x00402008, "exit", "libc.so.6", null) }));
      Assert.That(loaded.Image.Entries, Is.EqualTo(new[] { 0x00401000UL, 0x00401006UL }));
      Assert.That(loaded.AddressMap.Ranges.Select(range => (range.Name, range.Kind)), Is.EqualTo(new[] { (".text", AddressKind.Rom), (".bss", AddressKind.Ram) }));
      Assert.That(loaded.Image.ByteAt(0x00402010), Is.EqualTo(0), "past the file's bytes inside the virtual size");
    });
  }

  /// <summary>A manifest import at a slot the container binds to another symbol is <c>PLY4057</c>.</summary>
  [Test]
  public void ImportContradictingTheContainer_IsRefused() {
    var result = this.LoadElf(Elf(json => json["imports"] = new JsonArray(new JsonObject { ["slot"] = "0x00402000", ["symbol"] = "printf" })), ElfImage());
    Assert.That(result.Problems.Single().Message, Is.EqualTo("manifest states import at 0x00402000 'printf' but the container states 'puts'"));
  }

  /// <summary>Without a manifest a container that states everything loads: no contracts, no projection.</summary>
  [Test]
  public void NoManifest_ContainerAlone() {
    var loaded = ImageBuilder.Load(this._scratch.Write("elf.bin", _elf), null, Registry(ElfImage())).Value!;
    Assert.Multiple(() => {
      Assert.That(loaded.Manifest, Is.Null);
      Assert.That(loaded.Entries, Is.Empty);
      Assert.That(loaded.Observe, Is.Null);
      Assert.That(loaded.IsDecodable(0x00401007), Is.True);
      Assert.That(loaded.IsDecodable(0x00402000), Is.False);
    });
  }

  /// <summary>Row 13 and scenario 5: unknown bytes and every known magic without a codec are <c>PLY4059</c>, the known ones named.</summary>
  [TestCase(new byte[] { 0x12, 0x34, 0x56, 0x78 }, null)]
  [TestCase(new byte[] { 0xCF, 0xFA, 0xED, 0xFE }, "Mach-O")]
  [TestCase(new byte[] { 0xCE, 0xFA, 0xED, 0xFE }, "Mach-O")]
  [TestCase(new byte[] { 0xFE, 0xED, 0xFA, 0xCF }, "Mach-O")]
  [TestCase(new byte[] { 0xFE, 0xED, 0xFA, 0xCE }, "Mach-O")]
  [TestCase(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }, "Mach-O universal")]
  [TestCase(new byte[] { 0x7F, 0x45, 0x4C, 0x46 }, "ELF")]
  [TestCase(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, "PE")]
  public void UnrecognizedContainer_IsRefused(byte[] magic, string? format) {
    var file = this._scratch.Write("image.bin", magic);
    var problem = ImageBuilder.Load(file, null).Problems.Single();
    Assert.That(problem.Code, Is.EqualTo("PLY4059"));
    Assert.That(problem.Message, Is.EqualTo(format is null
      ? $"'{file}' is not a recognized container; give the manifest field 'container' or use a raw profile"
      : $"'{file}' is a {format} file, which no registered codec reads; give the manifest field 'container' or use a raw profile"));
  }

  /// <summary>A manifest that names a container no codec reads is <c>PLY4059</c> naming it.</summary>
  [Test]
  public void DeclaredContainerWithoutCodec_IsRefused()
    => Assert.That(this.Refusal(Min(json => json["container"] = "pe")).Message, Does.Contain("is declared as container 'pe', which no registered codec reads"));

  /// <summary>A manifest without <c>container</c> never makes a blob raw.</summary>
  [Test]
  public void RawIsNeverGuessed() => Assert.That(this.Refusal(Min(json => json.Remove("container"))).Code, Is.EqualTo("PLY4059"));

  /// <summary>Row 14: a missing image is <c>PLY0004</c> with exit 1.</summary>
  [Test]
  public void MissingImage_IsUnreadable() {
    var problem = ImageBuilder.Load(BinaryTesting.Fixture("absent.bin"), null).Problems.Single();
    Assert.That((problem.Code, problem.ExitCode), Is.EqualTo(("PLY0004", (ExitCode?)ExitCode.EnvironmentError)));
  }

  /// <summary>With a catalog the ISA must have a machine profile; the profile's byte order and address width apply.</summary>
  [Test]
  public void Catalog_ChecksTheIsa() {
    Assert.That(this.Refusal(Min(), BinaryTesting.Catalog).Message, Does.EndWith("isa 'x86' has no machine profile"));
    Assert.That(this.Loaded(Min(json => json["isa"] = "x86-32"), BinaryTesting.Catalog).Image.Isa, Is.EqualTo("x86-32"));
    Assert.That(this.Refusal(Min(json => { json["isa"] = "x86-32"; json["byteOrder"] = "big"; }), BinaryTesting.Catalog).Message,
      Is.EqualTo("manifest states byteOrder 'big' but machine profile 'x86-32' states 'little'"));
    var outside = this.Refusal(Min(json => { json["isa"] = "x86-32"; json["loadAddress"] = "0xFFFFFFFC"; json["entries"]![0]!["address"] = "0xFFFFFFFC"; }), BinaryTesting.Catalog);
    Assert.That(outside.Code, Is.EqualTo("PLY4011"));
  }

  /// <summary>A memory map whose regions overlap is the address map's <c>PLY4009</c>.</summary>
  [Test]
  public void OverlappingMemoryMap_IsRefused() {
    var map = new JsonObject { ["regions"] = new JsonArray(
      new JsonObject { ["name"] = "a", ["start"] = "0x10", ["end"] = "0x20", ["kind"] = "ram" },
      new JsonObject { ["name"] = "b", ["start"] = "0x18", ["end"] = "0x30", ["kind"] = "ram" }) };
    Assert.That(this.Refusal(Min(json => json["memoryMap"] = map)).Code, Is.EqualTo("PLY4009"));
  }

  /// <summary>The raw codec needs every machine fact from the manifest and claims no magic.</summary>
  [Test]
  public void Raw_NeverGuesses() {
    var raw = new RawBinaryCodec();
    var manifest = this._scratch.Manifest(Min());
    Assert.Multiple(() => {
      Assert.That(raw.CanRead(BinaryTesting.Blob.AsSpan()), Is.False);
      Assert.That(raw.Read(BinaryTesting.Blob.ToArray(), null).Problems.Single().Message, Does.Contain("container 'raw' needs a manifest"));
      Assert.That(this.Refusal(Min(json => json.Remove("byteOrder"))).Message, Does.EndWith("container 'raw' needs the field 'byteOrder'"));
      Assert.That(this.Refusal(Min(json => json.Remove("loadAddress"))).Message, Does.EndWith("container 'raw' needs the field 'loadAddress'"));
      Assert.That(this.Refusal(Min(json => json["loadAddress"] = "0xFFFFFFFFFFFFFFFC")).Message, Does.EndWith("'loadAddress' 0xFFFFFFFFFFFFFFFC leaves no room for the 8 bytes of the image"));
      Assert.That(raw.Read(BinaryTesting.Blob.ToArray(), manifest).Value!.Sections.Single().Size, Is.EqualTo(8));
    });
  }

  /// <summary>The last byte of the address space is reachable, and an empty blob loads as an empty section without an address range.</summary>
  [Test]
  public void Raw_AddressSpaceEdges() {
    var top = ImageBuilder.Load(this._scratch.Manifest(Min(json => { json["loadAddress"] = "0xFFFFFFFFFFFFFFF8"; json["entries"]![0]!["address"] = "0xFFFFFFFFFFFFFFFF"; })));
    Assert.That(top.Value!.AddressMap.Ranges.Single().End, Is.EqualTo(ulong.MaxValue));
    var empty = new RawBinaryCodec().Read(ReadOnlyMemory<byte>.Empty, this._scratch.Manifest(Min())).Value!;
    Assert.That(empty.Sections.Single().Size, Is.EqualTo(0));
    var emptyFile = this._scratch.Write("empty.bin", ReadOnlySpan<byte>.Empty);
    var json = Min(json => json["image"]!["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>())));
    var problem = ImageBuilder.Load(emptyFile, this._scratch.Manifest(json)).Problems.Single();
    Assert.That(problem.Code, Is.EqualTo("PLY4058"));
  }

  /// <summary>The registry: one codec per ID, detection over the first 64 bytes, lookup by ID.</summary>
  [Test]
  public void Registry_RegistersDetectsAndFinds() {
    var registry = Registry(ElfImage());
    var longElf = new byte[200];
    _elf.CopyTo(longElf, 0);
    Assert.Multiple(() => {
      Assert.That(registry.Codecs.Select(codec => codec.Id), Is.EqualTo(new[] { "raw", "elf" }));
      Assert.That(() => registry.Register(new RawBinaryCodec()), Throws.ArgumentException.With.Message.Contains("container 'raw' is registered already"));
      Assert.That(registry.Detect(longElf)!.Id, Is.EqualTo("elf"));
      Assert.That(registry.Detect([0x4D, 0x5A]), Is.Null);
      Assert.That(registry.Find("pe"), Is.Null);
      Assert.That(BinaryCodecRegistry.KnownFormat([0x4D]), Is.Null);
      Assert.That(registry.Select(longElf, null, "a.out").Value!.Id, Is.EqualTo("elf"));
      Assert.That(registry.Select(longElf, "raw", "a.out").Value!.Id, Is.EqualTo("raw"));
    });
  }

  /// <summary>The image model: sections by address, bytes past the file read as zero, value equality.</summary>
  [Test]
  public void Image_Model() {
    var image = ElfImage();
    var section = image.Sections[1];
    Assert.Multiple(() => {
      Assert.That(image.SectionAt(0x00402000)!.Name, Is.EqualTo(".bss"));
      Assert.That(image.ByteAt(0x00403000), Is.Null);
      Assert.That(image.ByteAt(0x00400FFF), Is.Null);
      Assert.That(section.ByteAt(0x004020FF), Is.EqualTo(0));
      Assert.That(section.ByteAt(0x00402100), Is.Null);
      Assert.That(image, Is.EqualTo(ElfImage()));
      Assert.That(image.GetHashCode(), Is.EqualTo(ElfImage().GetHashCode()));
      Assert.That(image, Is.Not.EqualTo(image with { Entries = [] }));
      Assert.That(image.Equals(null), Is.False);
      Assert.That(section, Is.EqualTo(section with { Bytes = Array.Empty<byte>() }));
      Assert.That(section.GetHashCode(), Is.EqualTo((section with { }).GetHashCode()));
      Assert.That(section, Is.Not.EqualTo(section with { Bytes = new byte[] { 1 } }));
      Assert.That(section.Equals(null), Is.False);
    });
  }

  /// <summary>Two loads of the same files are equal; a different manifest object is not.</summary>
  [Test]
  public void LoadedBinary_Equality() {
    var manifest = this._scratch.Manifest(Min());
    var first = ImageBuilder.Load(manifest).Value!;
    var second = ImageBuilder.Load(manifest).Value!;
    Assert.Multiple(() => {
      Assert.That(first, Is.EqualTo(second));
      Assert.That(first.GetHashCode(), Is.EqualTo(second.GetHashCode()));
      Assert.That(first, Is.Not.EqualTo(ImageBuilder.Load(this._scratch.Manifest(Min(), "other.json")).Value));
      Assert.That(first.Equals(null), Is.False);
    });
  }

}
