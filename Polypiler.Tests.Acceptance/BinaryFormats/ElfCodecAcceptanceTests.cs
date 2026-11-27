using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Core.BinaryFormats;
using Polypiler.Core.BinaryFormats.Elf;
using Polypiler.Core.MachineSemantics;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Acceptance.BinaryFormats;

/// <summary>Compares the ELF reader with independently built and independently described files.</summary>
[TestFixture, Layer("native"), Category("Acceptance"), Category("EndToEnd")]
[RequiresToolchain("clang"), RequiresToolchain("lld"), RequiresToolchain("llvm")]
public sealed class ElfCodecAcceptanceTests {

  [TestCase("i386-unknown-linux-gnu", "x86", "protected32", 3, false, false, -4)]
  [TestCase("x86_64-unknown-linux-gnu", "x64", "long64", 62, true, false, -4)]
  [TestCase("armv7a-unknown-linux-gnueabi", "arm32", "arm", 40, false, false, -8)]
  [TestCase("aarch64-unknown-linux-gnu", "aarch64", "a64", 183, true, false, 0)]
  [TestCase("armeb-unknown-linux-gnueabi", "arm32", "arm", 40, false, true, -8)]
  public void BuildAndCompare(string triple, string isa, string mode, int machine, bool wide, bool big, long callAddend) {
    using var fixture = new ElfFixture();
    foreach (var source in new[] { "leaf.s", "calls.s" }) {
      var executable = fixture.Build(source, triple);
      using var oracle = fixture.Oracle(executable);
      var image = ElfFixture.Read(executable);
      ElfFixture.Compare(image, oracle.RootElement[0], isa, mode, machine, wide, big);
      Assert.That(image.ObjectType, Is.EqualTo("ET_EXEC"));
      fixture.CompareTextSection(executable, image);
    }

    var relocatable = fixture.Build("calls.s", triple, "--object");
    using var objectOracle = fixture.Oracle(relocatable);
    var objectImage = ElfFixture.Read(relocatable);
    ElfFixture.Compare(objectImage, objectOracle.RootElement[0], isa, mode, machine, wide, big);
    Assert.That(objectImage.ObjectType, Is.EqualTo("ET_REL"));
    Assert.That((objectImage.Relocations.Single().TargetSection, objectImage.Relocations.Single().Addend), Is.EqualTo((".text", callAddend)));
    var disassembly = fixture.Run("llvm-objdump", "-dr", relocatable);
    Assert.That(disassembly, Does.Contain("leaf"));
  }

  [Test]
  public void SectionlessFileUsesExactlyItsLoadSegments() {
    using var fixture = new ElfFixture();
    var path = fixture.Build("leaf.s", "i386-unknown-linux-gnu");
    var sectionless = Path.Combine(fixture.Directory, "sectionless.elf");
    fixture.Run("llvm-strip", "--strip-sections", "-o", sectionless, path);
    using var oracle = fixture.Oracle(sectionless);
    var image = ElfFixture.Read(sectionless);
    var fromLlvm = oracle.RootElement[0].GetProperty("ProgramHeaders").EnumerateArray()
      .Select(item => item.GetProperty("ProgramHeader")).Where(program => Value(program, "Type") == 1).ToArray();
    Assert.That(image.Sections, Has.Length.EqualTo(fromLlvm.Length));
    for (var index = 0; index < fromLlvm.Length; ++index) {
      var program = fromLlvm[index];
      var section = image.Sections[index];
      var flags = Value(program, "Flags");
      Assert.That((section.Address, section.Bytes.Length, section.VirtualSize, section.Readable, section.Writable, section.Executable),
        Is.EqualTo((Number(program, "VirtualAddress"), (int)Number(program, "FileSize"), Number(program, "MemSize"),
          (flags & 4) != 0, (flags & 2) != 0, (flags & 1) != 0)));
    }
  }

  [Test]
  public void RealArmMappingSymbolsDescribeCodeAndDataRanges() {
    using var fixture = new ElfFixture();
    var path = fixture.Build("thumb.s", "armv7a-unknown-linux-gnueabi");
    using var oracle = fixture.Oracle(path);
    var raw = oracle.RootElement[0].GetProperty("Symbols").EnumerateArray().Select(item => item.GetProperty("Symbol"))
      .Single(symbol => Name(symbol, "Name") == "tfn");
    var rawAddress = Number(raw, "Value");
    Assert.That(rawAddress & 1, Is.EqualTo(1UL));
    var image = ElfFixture.Read(path);
    var thumb = image.Symbols.Single(symbol => symbol.Name == "tfn");
    Assert.That(thumb, Is.EqualTo(new ImageSymbol("tfn", rawAddress & ~1UL, Number(raw, "Size"), SymbolKind.Function, "thumb")));
    var arm = image.Symbols.Single(symbol => symbol.Name == "entry");
    Assert.That(image.ModeAt(arm.Address), Is.EqualTo("arm"));
    Assert.That(image.ModeAt(thumb.Address), Is.EqualTo("thumb"));
    var data = image.ModeRanges.Single(range => range.Mode == "data");
    Assert.That(image.ModeAt(data.Address), Is.EqualTo("data"));
    var loaded = ImageBuilder.Load(path, null).Value!;
    Assert.That(loaded.IsDecodable(data.Address), Is.False);
    Assert.That(loaded.IsDecodable(thumb.Address), Is.True);
  }

  [Test]
  public void UndefinedPltSymbolIsAnImportAtItsGotSlot() {
    using var fixture = new ElfFixture();
    var path = fixture.Build("imports.c", "x86_64-unknown-linux-gnu", "--shared");
    using var oracle = fixture.Oracle(path);
    var image = ElfFixture.Read(path);
    ElfFixture.Compare(image, oracle.RootElement[0], "x64", "long64", 62, true, false);
    Assert.That(image.ObjectType, Is.EqualTo("ET_DYN"));
    var gotSlot = oracle.RootElement[0].GetProperty("Relocations").EnumerateArray()
      .SelectMany(section => section.GetProperty("Relocs").EnumerateArray()).Select(item => item.GetProperty("Relocation"))
      .Single(relocation => Name(relocation, "Type") == "R_X86_64_JUMP_SLOT");
    Assert.That(image.Imports.Single(), Is.EqualTo(new ImageImport(Number(gotSlot, "Offset"), Name(gotSlot, "Symbol"), null, null)));
  }

  [Test]
  public void ThreadLocalAddressSpaceMayOverlapOrdinaryData() {
    using var fixture = new ElfFixture();
    var path = fixture.Build("tls.s", "x86_64-unknown-linux-gnu", "--shared");
    using var oracle = fixture.Oracle(path);
    var image = ElfFixture.Read(path);
    var tls = image.Sections.Single(section => section.Name == ".tbss");
    var data = image.Sections.Single(section => section.Name == ".init_array");
    Assert.That(tls.ThreadLocal, Is.True);
    Assert.That(image.Symbols.Any(symbol => symbol.Name == "thread_data"), Is.False);
    Assert.That(tls.Contains(data.Address), Is.True, "the linker assigned overlapping addresses");
    Assert.That(Value(oracle.RootElement[0].GetProperty("Sections").EnumerateArray()
      .Select(item => item.GetProperty("Section")).Single(section => Name(section, "Name") == ".tbss"), "Flags") & 0x400, Is.EqualTo(0x400));
    var loaded = ImageBuilder.Load(path, null);
    Assert.That(loaded.Problems, Is.Empty);
    Assert.That(loaded.Value!.Image.SectionAt(data.Address), Is.EqualTo(data));
  }

  [Test]
  public void UnsupportedRelocationKeepsItsNumberAndWarns() {
    using var fixture = new ElfFixture();
    var path = fixture.Build("unsupported.s", "i386-unknown-linux-gnu", "--object");
    using var oracle = fixture.Oracle(path);
    var raw = oracle.RootElement[0].GetProperty("Relocations")[0].GetProperty("Relocs")[0].GetProperty("Relocation");
    var result = new ElfCodec().Read(File.ReadAllBytes(path), null);
    Assert.That(result.Succeeded, Is.True);
    var relocation = result.Value!.Relocations.Single();
    Assert.That(relocation, Is.EqualTo(new ImageRelocation(Number(raw, "Offset"), RelocationKind.Unsupported, Name(raw, "Symbol"), 0, (int)Value(raw, "Type")) {
      TargetSection = ".text"
    }));
    Assert.That(result.Problems.Single().Code, Is.EqualTo("PLY4063"));
  }

  private static ulong Number(JsonElement value, string property) => value.GetProperty(property).GetUInt64();

  private static ulong Value(JsonElement value, string property) => Number(value.GetProperty(property), "Value");

  private static string Name(JsonElement value, string property) => value.GetProperty(property).GetProperty("Name").GetString()!;

  internal sealed class ElfFixture : IDisposable {

    private static readonly string _sourcePath = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "native", "mach-bin-elf", "src");
    private static readonly string _builder = Path.Combine(PrototypeFixture.RepositoryRoot, "tooling", "machine", "build-fixture.py");

    internal string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("polypiler-elf-").FullName;

    internal string Build(string source, string triple, params string[] flags) {
      var path = Path.Combine(this.Directory, $"{Path.GetFileNameWithoutExtension(source)}-{triple}{(flags.Contains("--object") ? ".o" : ".elf")}");
      var python = OperatingSystem.IsWindows() ? "python" : "python3";
      this.Run(python, [_builder, Path.Combine(_sourcePath, source), "--triple", triple, "--output", path, .. flags]);
      return path;
    }

    internal string Run(string tool, params string[] arguments) {
      var result = new ProcessRunner().Run([tool, .. arguments], TimeSpan.FromSeconds(30));
      Assert.That(result.TimedOut, Is.False, tool);
      Assert.That(result.ExitCode, Is.Zero, result.StandardError);
      return result.StandardOutput;
    }

    internal JsonDocument Oracle(string path)
      => JsonDocument.Parse(this.Run("llvm-readelf", "--elf-output-style=JSON", "--file-header", "--sections", "--symbols",
        "--relocs", "--program-headers", "--expand-relocs", path));

    internal static BinaryImage Read(string path) {
      var result = new ElfCodec().Read(File.ReadAllBytes(path), null, path);
      Assert.That(result.Succeeded, Is.True, string.Join(Environment.NewLine, result.Problems.Select(problem => problem.Message)));
      Assert.That(result.Problems, Is.Empty);
      return result.Value!;
    }

    internal void CompareTextSection(string path, BinaryImage image) {
      var oracleBytes = Path.Combine(this.Directory, "llvm-text.bin");
      var copied = Path.Combine(this.Directory, "llvm-copied.elf");
      this.Run("llvm-objcopy", $"--dump-section=.text={oracleBytes}", path, copied);
      Assert.That(image.Sections.Single(section => section.Name == ".text").Bytes.ToArray(), Is.EqualTo(File.ReadAllBytes(oracleBytes)));
    }

    internal static void Compare(BinaryImage image, JsonElement root, string isa, string mode, int machine, bool wide, bool big) {
      var header = root.GetProperty("ElfHeader");
      Assert.That((image.Isa, image.Mode, image.ByteOrder), Is.EqualTo((isa, mode, big ? ByteOrder.Big : ByteOrder.Little)));
      Assert.That(Value(header, "Machine"), Is.EqualTo(machine));
      var identity = header.GetProperty("Ident");
      Assert.That((Value(identity, "Class"), Value(identity, "DataEncoding")), Is.EqualTo((wide ? 2UL : 1UL, big ? 2UL : 1UL)));
      var entry = Number(header, "Entry");
      if (isa == "arm32")
        entry &= ~1UL;
      Assert.That(image.Entries, Is.EqualTo(entry == 0 ? Array.Empty<ulong>() : new[] { entry }));

      var llvmSections = root.GetProperty("Sections").EnumerateArray().Select(value => value.GetProperty("Section")).Skip(1).ToArray();
      Assert.That(image.Sections, Has.Length.EqualTo(llvmSections.Length));
      for (var index = 0; index < llvmSections.Length; ++index) {
        var actual = image.Sections[index];
        var expected = llvmSections[index];
        var flags = Value(expected, "Flags");
        Assert.That((actual.Name, actual.Address, actual.Size, actual.Allocated, actual.Writable, actual.Executable),
          Is.EqualTo((Name(expected, "Name"), Number(expected, "Address"), Number(expected, "Size"),
            (flags & 2) != 0, (flags & 1) != 0, (flags & 4) != 0)));
        var isNoBits = Name(expected, "Type") == "SHT_NOBITS";
        Assert.That(actual.Bytes.Length, Is.EqualTo(isNoBits ? 0UL : Number(expected, "Size")));
      }

      var functions = root.GetProperty("Symbols").EnumerateArray().Select(item => item.GetProperty("Symbol"))
        .Where(symbol => Value(symbol, "Type") == 2 && Value(symbol, "Section") != 0)
        .Select(symbol => {
          var raw = Number(symbol, "Value");
          var thumb = isa == "arm32" && (raw & 1UL) != 0;
          return new ImageSymbol(Name(symbol, "Name"), thumb ? raw & ~1UL : raw, Number(symbol, "Size"),
            SymbolKind.Function, thumb ? "thumb" : null);
        });
      Assert.That(image.Symbols.Where(symbol => symbol.Kind == SymbolKind.Function), Is.EquivalentTo(functions));

      var relocations = root.GetProperty("Relocations").EnumerateArray()
        .SelectMany(section => section.GetProperty("Relocs").EnumerateArray()).Select(item => item.GetProperty("Relocation")).ToArray();
      Assert.That(image.Relocations, Has.Length.EqualTo(relocations.Length));
      for (var index = 0; index < relocations.Length; ++index) {
        var actual = image.Relocations[index];
        var expected = relocations[index];
        Assert.That((actual.Address, actual.RawType, actual.Symbol), Is.EqualTo((Number(expected, "Offset"), (int)Value(expected, "Type"), Name(expected, "Symbol"))));
        if (expected.TryGetProperty("Addend", out var addend))
          Assert.That(actual.Addend, Is.EqualTo(ParseAddend(addend)));
      }
    }

    private static long ParseAddend(JsonElement value) => long.TryParse(value.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed)
      ? signed : unchecked((long)value.GetUInt64());

    public void Dispose() => System.IO.Directory.Delete(this.Directory, recursive: true);

  }

}
