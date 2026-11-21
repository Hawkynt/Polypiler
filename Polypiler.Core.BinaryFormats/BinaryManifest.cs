using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Regions;

namespace Polypiler.Core.BinaryFormats;

/// <summary>An entry the manifest declares.</summary>
/// <param name="Name">The name, unique in the manifest.</param>
/// <param name="Address">The address.</param>
/// <param name="Abi">The calling contract.</param>
/// <param name="Buffers">The buffer parameters.</param>
public sealed record ManifestEntry(string Name, ulong Address, AbiSignature Abi, ImmutableArray<BufferContract> Buffers) {

  /// <inheritdoc/>
  public bool Equals(ManifestEntry? other)
    => other is not null && this.Name == other.Name && this.Address == other.Address && this.Abi == other.Abi && this.Buffers.SequenceEqual(other.Buffers);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Name, this.Address, this.Abi);

}

/// <summary>A range of addresses the manifest declares, <see cref="End"/> excluded.</summary>
/// <param name="Start">The first address.</param>
/// <param name="End">The address after the last; greater than <see cref="Start"/>.</param>
public sealed record ManifestRange(ulong Start, ulong End) {

  /// <summary>Whether an address lies in the range.</summary>
  /// <param name="address">The address.</param>
  /// <returns>Whether it does.</returns>
  public bool Contains(ulong address) => address >= this.Start && address < this.End;

}

/// <summary>An import the manifest adds or confirms.</summary>
/// <param name="Slot">The address of the slot.</param>
/// <param name="Symbol">The symbol.</param>
/// <param name="Library">The library, or <see langword="null"/>.</param>
/// <param name="Abi">The calling contract, or <see langword="null"/>.</param>
public sealed record ManifestImport(ulong Slot, string Symbol, string? Library, AbiSignature? Abi);

/// <summary>
/// The binary input manifest (<c>spec/schemas/binary-manifest.schema.json</c>, <c>schemaVersion: 1</c>):
/// what a binary is, where its code starts and what its entries promise, for the facts a container
/// does not state or a raw blob cannot. Every rule it breaks is <c>PLY4055</c> naming the rule;
/// addresses are strings <c>0x…</c> and every range excludes its <c>end</c>.
/// </summary>
public sealed class BinaryManifest {

  /// <summary>The containers the field <c>container</c> may name.</summary>
  public static readonly ImmutableArray<string> Containers = ["elf", "pe", "raw"];

  /// <summary>The fields <see cref="CheckAgainst"/> compares.</summary>
  public static readonly ImmutableArray<string> CheckedFields = ["isa", "mode", "byteOrder", "container", "loadAddress"];

  private BinaryManifest(string source) => this.Source = source;

  /// <summary>The manifest file, as diagnostics name it.</summary>
  public string Source { get; }

  /// <summary>The directory relative paths of the manifest are resolved against.</summary>
  public string Directory { get; private init; } = ".";

  /// <summary>The image's path as the manifest writes it.</summary>
  public string ImagePath { get; private init; } = "";

  /// <summary>The image's path resolved against <see cref="Directory"/>.</summary>
  public string ResolvedImagePath => Path.Combine(this.Directory, this.ImagePath);

  /// <summary>The SHA-256 of the image, 64 lower-case hexadecimal digits.</summary>
  public string Sha256 { get; private init; } = "";

  /// <summary>The container, or <see langword="null"/> to detect it by its magic.</summary>
  public string? Container { get; private init; }

  /// <summary>The ISA, as its machine profile names it.</summary>
  public string Isa { get; private init; } = "";

  /// <summary>The decoding mode.</summary>
  public string Mode { get; private init; } = "";

  /// <summary>The byte order, or <see langword="null"/> when the manifest leaves it to the container.</summary>
  public ByteOrder? ByteOrder { get; private init; }

  /// <summary>The CPU features the code may use.</summary>
  public ImmutableArray<string> CpuFeatures { get; private init; } = [];

  /// <summary>The load address, or <see langword="null"/>.</summary>
  public ulong? LoadAddress { get; private init; }

  /// <summary>The entries; at least one.</summary>
  public ImmutableArray<ManifestEntry> Entries { get; private init; } = [];

  /// <summary>The ranges decoding is restricted to; empty for no restriction.</summary>
  public ImmutableArray<ManifestRange> CodeRanges { get; private init; } = [];

  /// <summary>The ranges known to hold data.</summary>
  public ImmutableArray<ManifestRange> DataRanges { get; private init; } = [];

  /// <summary>The manifest's own relocations, or <see langword="null"/> for <c>"from-container"</c> or no field.</summary>
  public ImmutableArray<ImageRelocation>? Relocations { get; private init; }

  /// <summary>The imports the manifest adds.</summary>
  public ImmutableArray<ManifestImport> Imports { get; private init; } = [];

  /// <summary>The stack size in bytes, or <see langword="null"/>.</summary>
  public ulong? StackBytes { get; private init; }

  /// <summary>The referenced memory-map file as written, or <see langword="null"/> when the map is inline or absent.</summary>
  public string? MemoryMapPath { get; private init; }

  /// <summary>The memory map's regions, read inline or from <see cref="MemoryMapPath"/>, or <see langword="null"/> without a map.</summary>
  public ImmutableArray<AddressRange>? MemoryMap { get; private init; }

  /// <summary>The observation projection, kept as the JSON it was written as, or <see langword="null"/>.</summary>
  public JsonElement? Observe { get; private init; }

  /// <summary>The toolchain key of the reference runner, or <see langword="null"/>.</summary>
  public string? ReferenceRunner { get; private init; }

  /// <summary>Whether discovery may traverse heuristic function candidates.</summary>
  public bool AcceptHeuristicFunctions { get; private init; }

  /// <summary>Reads a manifest file.</summary>
  /// <param name="path">The file.</param>
  /// <returns>The manifest, <c>PLY0004</c> for a file that cannot be read, or <c>PLY4055</c> naming the broken rule.</returns>
  public static BinaryResult<BinaryManifest> Load(string path) {
    ArgumentNullException.ThrowIfNull(path);
    if (!File.Exists(path))
      return BinaryResult<BinaryManifest>.Failure(BinaryDiagnostics.Unreadable(path));
    return Parse(File.ReadAllText(path), path, Path.GetDirectoryName(Path.GetFullPath(path))!);
  }

  /// <summary>Reads a manifest text.</summary>
  /// <param name="json">The text.</param>
  /// <param name="source">The file it came from, as diagnostics name it.</param>
  /// <param name="directory">The directory relative paths are resolved against.</param>
  /// <returns>The manifest, or <c>PLY4055</c> naming the broken rule (<c>PLY0004</c> for a memory map that cannot be read).</returns>
  public static BinaryResult<BinaryManifest> Parse(string json, string source, string directory) {
    ArgumentNullException.ThrowIfNull(json);
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(directory);
    try {
      using var document = JsonDocument.Parse(json);
      return BinaryResult<BinaryManifest>.Success(Read(document.RootElement, source, directory));
    } catch (JsonException exception) {
      return BinaryResult<BinaryManifest>.Failure(Invalid(source, $"not JSON: {exception.Message}"));
    } catch (ManifestException exception) {
      return BinaryResult<BinaryManifest>.Failure(exception.Diagnostic);
    }
  }

  /// <summary>
  /// Compares a fact another source states with the manifest: a command-line flag must not contradict
  /// it. The manifest overrides nothing and nothing overrides the manifest.
  /// </summary>
  /// <param name="field">The field, one of <see cref="CheckedFields"/>.</param>
  /// <param name="value">The other source's value.</param>
  /// <param name="other">The other source, for example <c>--from</c>.</param>
  /// <returns><see langword="null"/> when they agree or the manifest leaves the field open; else <c>PLY4057</c> with <see cref="ExitCode.UsageError"/>.</returns>
  /// <exception cref="ArgumentException"><paramref name="field"/> is not one of <see cref="CheckedFields"/>.</exception>
  public Diagnostic? CheckAgainst(string field, string value, string other) {
    ArgumentNullException.ThrowIfNull(field);
    ArgumentNullException.ThrowIfNull(value);
    ArgumentNullException.ThrowIfNull(other);
    var stated = field switch {
      "isa" => this.Isa,
      "mode" => this.Mode,
      "byteOrder" => this.ByteOrder is { } order ? Name(order) : null,
      "container" => this.Container,
      "loadAddress" => this.LoadAddress is { } address ? BinaryDiagnostics.Hex(address) : null,
      _ => throw new ArgumentException($"'{field}' is not a field the manifest compares; use one of {string.Join(", ", CheckedFields)}", nameof(field))
    };
    if (stated is null || Same(field, stated, value))
      return null;
    return Diagnostic.Create(BinaryDiagnostics.Contradicted, null, field, stated, other, value) with { ExitCode = ExitCode.UsageError };
  }

  /// <summary>The manifest's name of a byte order.</summary>
  /// <param name="order">The order.</param>
  /// <returns><c>little</c> or <c>big</c>.</returns>
  public static string Name(ByteOrder order) => order == MachineSemantics.ByteOrder.Big ? "big" : "little";

  private static bool Same(string field, string stated, string value)
    => field == "loadAddress"
      ? TryHex(value, out var address) && BinaryDiagnostics.Hex(address) == stated
      : stated == value;

  private static Diagnostic Invalid(string source, string rule) => Diagnostic.Create(BinaryDiagnostics.InvalidManifest, null, source, rule);

  private static BinaryManifest Read(JsonElement root, string source, string directory) {
    var reader = new Reader(source);
    reader.Fields(root, "the manifest", "schemaVersion", "image", "container", "isa", "mode", "byteOrder", "cpuFeatures", "loadAddress", "entries", "codeRanges", "dataRanges", "relocations", "imports", "stack", "memoryMap", "observe", "referenceRunner", "acceptHeuristicFunctions");
    if (reader.Integer(root, "schemaVersion", "the manifest", required: true) != 1)
      throw reader.Fail("'schemaVersion' must be 1");

    var image = reader.Required(root, "image", "the manifest");
    reader.Fields(image, "'image'", "path", "sha256");
    var sha = reader.Text(image, "sha256", "'image'", required: true)!;
    if (!_sha256Pattern.IsMatch(sha))
      throw reader.Fail("'image.sha256' must be 64 hexadecimal digits");

    var container = reader.Text(root, "container", "the manifest", required: false);
    if (container is not null && !Containers.Contains(container))
      throw reader.Fail($"'container' must be one of {string.Join(", ", Containers)}, not '{container}'");

    var byteOrder = reader.Text(root, "byteOrder", "the manifest", required: false) switch {
      null => (ByteOrder?)null,
      "little" => MachineSemantics.ByteOrder.Little,
      "big" => MachineSemantics.ByteOrder.Big,
      var other => throw reader.Fail($"'byteOrder' must be 'little' or 'big', not '{other}'")
    };

    var entries = reader.List(root, "entries", required: true, (element, at) => ReadEntry(reader, element, at));
    if (entries.IsEmpty)
      throw reader.Fail("the manifest needs at least one entry");
    var duplicate = entries.GroupBy(entry => entry.Name).FirstOrDefault(group => group.Count() > 1);
    if (duplicate is not null)
      throw reader.Fail($"entry name '{duplicate.Key}' is used more than once");

    var stack = root.TryGetProperty("stack", out var stackElement) ? stackElement : (JsonElement?)null;
    if (stack is { } stackValue)
      reader.Fields(stackValue, "'stack'", "bytes");

    var (memoryMapPath, memoryMap) = ReadMemoryMap(reader, root, directory);

    return new BinaryManifest(source) {
      Directory = directory,
      ImagePath = reader.Text(image, "path", "'image'", required: true)!,
      Sha256 = sha.ToLowerInvariant(),
      Container = container,
      Isa = reader.Text(root, "isa", "the manifest", required: true)!,
      Mode = reader.Text(root, "mode", "the manifest", required: true)!,
      ByteOrder = byteOrder,
      CpuFeatures = reader.List(root, "cpuFeatures", required: false, (element, at) => reader.Value(element, at)),
      LoadAddress = reader.Address(root, "loadAddress", "the manifest", required: false),
      Entries = entries,
      CodeRanges = ReadRanges(reader, root, "codeRanges"),
      DataRanges = ReadRanges(reader, root, "dataRanges"),
      Relocations = ReadRelocations(reader, root),
      Imports = reader.List(root, "imports", required: false, (element, at) => {
        reader.Fields(element, at, "slot", "symbol", "library", "abi");
        var abi = reader.Text(element, "abi", at, required: false);
        return new ManifestImport(reader.Address(element, "slot", at, required: true)!.Value, reader.Text(element, "symbol", at, required: true)!, reader.Text(element, "library", at, required: false), abi is null ? null : reader.Abi(abi, at));
      }),
      StackBytes = stack is { } bytes ? (ulong)reader.Integer(bytes, "bytes", "'stack'", required: true, minimum: 1)!.Value : null,
      MemoryMapPath = memoryMapPath,
      MemoryMap = memoryMap,
      Observe = root.TryGetProperty("observe", out var observe) ? observe.Clone() : null,
      ReferenceRunner = reader.Text(root, "referenceRunner", "the manifest", required: false),
      AcceptHeuristicFunctions = reader.Boolean(root, "acceptHeuristicFunctions", "the manifest") ?? false
    };
  }

  private static ManifestEntry ReadEntry(Reader reader, JsonElement element, string at) {
    reader.Fields(element, at, "name", "address", "abi", "buffers");
    var name = reader.Text(element, "name", at, required: true)!;
    var abi = reader.Abi(reader.Text(element, "abi", at, required: true)!, at);
    var buffers = reader.List(element, "buffers", required: false, (buffer, where) => {
      reader.Fields(buffer, where, "parameter", "length", "access");
      var parameter = (int)reader.Integer(buffer, "parameter", where, required: true)!;
      var length = reader.Required(buffer, "length", where);
      reader.Fields(length, where + ".length", "parameter", "bytes");
      var lengthParameter = (int?)reader.Integer(length, "parameter", where + ".length", required: false);
      var lengthBytes = reader.Integer(length, "bytes", where + ".length", required: false, minimum: 1);
      if (lengthParameter.HasValue == lengthBytes.HasValue)
        throw reader.Fail($"{where}.length must give exactly one of 'parameter' and 'bytes'");
      foreach (var index in new[] { parameter, lengthParameter ?? 0 })
        if (index >= abi.Parameters.Length)
          throw reader.Fail($"{where} names parameter {index}, but entry '{name}' has {abi.Parameters.Length}");
      var access = reader.Text(buffer, "access", where, required: true) switch {
        "read" => BufferAccess.Read,
        "write" => BufferAccess.Write,
        "readwrite" => BufferAccess.ReadWrite,
        var other => throw reader.Fail($"{where}.access must be 'read', 'write' or 'readwrite', not '{other}'")
      };
      return new BufferContract(parameter, lengthParameter, (ulong?)lengthBytes, access);
    }, at);
    return new ManifestEntry(name, reader.Address(element, "address", at, required: true)!.Value, abi, buffers);
  }

  private static ImmutableArray<ManifestRange> ReadRanges(Reader reader, JsonElement root, string field)
    => reader.List(root, field, required: false, (element, at) => {
      reader.Fields(element, at, "start", "end");
      var start = reader.Address(element, "start", at, required: true)!.Value;
      var end = reader.Address(element, "end", at, required: true)!.Value;
      if (end <= start)
        throw reader.Fail($"{at} must end after it starts ({BinaryDiagnostics.Hex(start)}..{BinaryDiagnostics.Hex(end)} holds no byte)");
      return new ManifestRange(start, end);
    });

  private static ImmutableArray<ImageRelocation>? ReadRelocations(Reader reader, JsonElement root) {
    if (!root.TryGetProperty("relocations", out var element))
      return null;
    if (element.ValueKind == JsonValueKind.String) {
      if (element.GetString() != "from-container")
        throw reader.Fail("'relocations' must be \"from-container\" or a list");
      return null;
    }
    return reader.List(root, "relocations", required: true, (relocation, at) => {
      reader.Fields(relocation, at, "address", "kind", "symbol", "addend");
      var kind = reader.Text(relocation, "kind", at, required: true) switch {
        "absolute" => RelocationKind.Absolute,
        "pc-relative" => RelocationKind.PcRelative,
        "import-slot" => RelocationKind.ImportSlot,
        "base-relative" => RelocationKind.BaseRelative,
        var other => throw reader.Fail($"{at}.kind must be 'absolute', 'pc-relative', 'import-slot' or 'base-relative', not '{other}'")
      };
      return new ImageRelocation(reader.Address(relocation, "address", at, required: true)!.Value, kind, reader.Text(relocation, "symbol", at, required: false), reader.Integer(relocation, "addend", at, required: false, minimum: long.MinValue) ?? 0, 0);
    });
  }

  private static (string? Path, ImmutableArray<AddressRange>? Regions) ReadMemoryMap(Reader reader, JsonElement root, string directory) {
    if (!root.TryGetProperty("memoryMap", out var element))
      return (null, null);
    if (element.ValueKind != JsonValueKind.String)
      return (null, ReadRegions(reader, element, "'memoryMap'"));

    var path = element.GetString()!;
    var full = Path.Combine(directory, path);
    if (!File.Exists(full))
      throw new ManifestException(BinaryDiagnostics.Unreadable(full));
    var mapReader = new Reader(full);
    try {
      using var document = JsonDocument.Parse(File.ReadAllText(full));
      return (path, ReadRegions(mapReader, document.RootElement, "the memory map"));
    } catch (JsonException exception) {
      throw mapReader.Fail($"not JSON: {exception.Message}");
    }
  }

  private static ImmutableArray<AddressRange> ReadRegions(Reader reader, JsonElement element, string at) {
    reader.Fields(element, at, "regions");
    return reader.List(element, "regions", required: true, (region, where) => {
      reader.Fields(region, where, "name", "start", "end", "kind", "executable");
      var start = reader.Address(region, "start", where, required: true)!.Value;
      var end = reader.Address(region, "end", where, required: true)!.Value;
      if (end <= start)
        throw reader.Fail($"{where} must end after it starts");
      var kind = reader.Text(region, "kind", where, required: true) switch {
        "ram" => AddressKind.Ram,
        "rom" => AddressKind.Rom,
        "stack" => AddressKind.Stack,
        "mmio" => AddressKind.Mmio,
        "unmapped" => AddressKind.Unmapped,
        var other => throw reader.Fail($"{where}.kind must be 'ram', 'rom', 'stack', 'mmio' or 'unmapped', not '{other}'")
      };
      return new AddressRange(reader.Text(region, "name", where, required: true)!, start, end - 1, kind, reader.Boolean(region, "executable", where) ?? false);
    });
  }

  private static bool TryHex(string text, out ulong value) {
    value = 0;
    return _addressPattern.IsMatch(text) && ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
  }

  private static readonly Regex _addressPattern = new("^0x[0-9A-Fa-f]{1,16}$", RegexOptions.CultureInvariant);
  private static readonly Regex _sha256Pattern = new("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant);

  private sealed class ManifestException(Diagnostic diagnostic) : Exception(diagnostic.Message) {

    public Diagnostic Diagnostic { get; } = diagnostic;

  }

  /// <summary>Reads typed fields, failing on the first broken rule with <c>PLY4055</c> naming it.</summary>
  private sealed class Reader(string source) {

    public Exception Fail(string rule) => new ManifestException(Invalid(source, rule));

    public void Fields(JsonElement element, string at, params string[] allowed) {
      if (element.ValueKind != JsonValueKind.Object)
        throw this.Fail($"{at} must be an object");
      foreach (var property in element.EnumerateObject())
        if (!allowed.Contains(property.Name))
          throw this.Fail($"unknown field '{property.Name}' in {at}");
    }

    public JsonElement Required(JsonElement element, string name, string at)
      => element.TryGetProperty(name, out var value) ? value : throw this.Fail($"'{name}' is required in {at}");

    public string Value(JsonElement element, string at)
      => element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text ? text : throw this.Fail($"{at} must be a non-empty string");

    public string? Text(JsonElement element, string name, string at, bool required) {
      if (element.TryGetProperty(name, out var value))
        return this.Value(value, $"'{name}' in {at}");
      if (required)
        throw this.Fail($"'{name}' is required in {at}");
      return null;
    }

    public long? Integer(JsonElement element, string name, string at, bool required, long minimum = 0) {
      if (!element.TryGetProperty(name, out var value)) {
        if (required)
          throw this.Fail($"'{name}' is required in {at}");
        return null;
      }
      if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < minimum)
        throw this.Fail($"'{name}' in {at} must be an integer of at least {minimum}");
      return number;
    }

    public bool? Boolean(JsonElement element, string name, string at) {
      if (!element.TryGetProperty(name, out var value))
        return null;
      return value.ValueKind switch {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw this.Fail($"'{name}' in {at} must be true or false")
      };
    }

    public ulong? Address(JsonElement element, string name, string at, bool required) {
      var text = this.Text(element, name, at, required);
      if (text is null)
        return null;
      return TryHex(text, out var address) ? address : throw this.Fail($"'{name}' in {at} must be an address written as 0x and 1 to 16 hexadecimal digits, not '{text}'");
    }

    public AbiSignature Abi(string text, string at)
      => AbiSignature.Parse(text) ?? throw this.Fail($"'abi' in {at} must read '<convention> (<type>, ...) -> <type>', not '{text}'");

    public ImmutableArray<T> List<T>(JsonElement element, string name, bool required, Func<JsonElement, string, T> item, string? within = null) {
      if (!element.TryGetProperty(name, out var value)) {
        if (required)
          throw this.Fail($"'{name}' is required");
        return [];
      }
      if (value.ValueKind != JsonValueKind.Array)
        throw this.Fail($"'{name}' must be a list");
      return [.. value.EnumerateArray().Select((child, index) => item(child, within is null ? $"{name}[{index}]" : $"{within}.{name}[{index}]"))];
    }

  }

}
