using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Polypiler.Core.MachineSemantics.Tracing;

/// <summary>Where the expected traces of a machine vector file come from.</summary>
/// <param name="Kind">One of <c>published</c>, <c>reference-run</c>, <c>upstream-embedded</c>.</param>
/// <param name="Producer">What computed them, for example <c>hand</c> or <c>unicorn</c>; never <c>polypiler</c>.</param>
/// <param name="Source">Where they were taken from.</param>
public sealed record MachineVectorProvenance(string Kind, string Producer, string Source);

/// <summary>The budget every vector of a file runs under.</summary>
/// <param name="MaxCycles">The most cycles, or <see langword="null"/> for no bound.</param>
/// <param name="MaxSteps">The most instructions, or <see langword="null"/> for no bound.</param>
public sealed record MachineLimits(long? MaxCycles, long? MaxSteps);

/// <summary>A call of the routine under a calling convention, in place of preset registers.</summary>
/// <param name="Contract">The convention text, for example <c>test-reg (u32, u32) -&gt; u32</c>.</param>
/// <param name="Arguments">The integer arguments, in order.</param>
public sealed record MachineCall(string Contract, ImmutableArray<ulong> Arguments) {

  /// <inheritdoc/>
  public bool Equals(MachineCall? other) => other is not null && this.Contract == other.Contract && this.Arguments.SequenceEqual(other.Arguments);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Contract, this.Arguments.Length);

}

/// <summary>One vector: the state the routine starts in and the trace it must produce.</summary>
/// <param name="Name">The name, unique in its file.</param>
/// <param name="Call">The call, or <see langword="null"/> when the routine is entered with the initial registers.</param>
/// <param name="Initial">The initial state.</param>
/// <param name="Expected">The expected trace.</param>
public sealed record MachineVector(string Name, MachineCall? Call, MachineSnapshot Initial, ObservableTrace Expected);

/// <summary>
/// A machine vector file (<c>*.mvec.json</c>, schema <c>spec/schemas/machine-vector.schema.json</c>):
/// the vectors of one machine-code fixture, what of a run is observed, and the trace each vector must
/// produce. Numbers are <c>0x</c>-prefixed hexadecimal strings, byte blocks hexadecimal strings, counts
/// (lengths, widths, cycles, trap vectors) JSON integers. <see cref="Save()"/> writes the canonical
/// form: two-space indentation, LF line ends, register and flag names in ordinal order, upper-case
/// hexadecimal without leading zeros; a file in that form round-trips byte-identically.
/// </summary>
public sealed class MachineVectorFile {

  /// <summary>The one schema version this loader reads.</summary>
  public const int SchemaVersion = 1;

  /// <summary><c>PLY4030</c>: a file that breaks a rule. Arguments: the file, the reason naming the rule.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Invalid = new("PLY4030", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "machine vector file '{0}' is not valid: {1}", "machine.vectors.valid");

  /// <summary><c>PLY4032</c>: <c>observe</c> names a register or flag the profile lacks. Arguments: the file, the name, the profile.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnknownObserved = new("PLY4032", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "'{0}' observes '{1}', which machine profile '{2}' does not define", "machine.vectors.observed");

  private static readonly string[] _provenanceKinds = ["published", "reference-run", "upstream-embedded"];

  /// <summary>Creates a file.</summary>
  /// <param name="fixture">The fixture's ID.</param>
  /// <param name="isa">The machine profile's ID.</param>
  /// <param name="provenance">Where the expected traces come from.</param>
  /// <param name="observe">What is observed.</param>
  /// <param name="limits">The budget, or <see langword="null"/>.</param>
  /// <param name="vectors">The vectors.</param>
  public MachineVectorFile(string fixture, string isa, MachineVectorProvenance provenance, TraceProjection observe, MachineLimits? limits, ImmutableArray<MachineVector> vectors) {
    this.Fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
    this.Isa = isa ?? throw new ArgumentNullException(nameof(isa));
    this.Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
    this.Observe = observe ?? throw new ArgumentNullException(nameof(observe));
    this.Limits = limits;
    this.Vectors = vectors;
  }

  /// <summary>The fixture's ID.</summary>
  public string Fixture { get; }

  /// <summary>The machine profile's ID.</summary>
  public string Isa { get; }

  /// <summary>Where the expected traces come from.</summary>
  public MachineVectorProvenance Provenance { get; }

  /// <summary>What is observed.</summary>
  public TraceProjection Observe { get; }

  /// <summary>The budget every vector runs under, or <see langword="null"/>.</summary>
  public MachineLimits? Limits { get; }

  /// <summary>The vectors, in file order.</summary>
  public ImmutableArray<MachineVector> Vectors { get; }

  /// <summary>Reads and checks a file.</summary>
  /// <param name="path">The file.</param>
  /// <param name="profile">The machine profile the file must be for.</param>
  /// <returns>The file.</returns>
  /// <exception cref="MachineException">The file cannot be read or breaks a rule (<c>PLY4030</c>), or observes storage the profile lacks (<c>PLY4032</c>).</exception>
  public static MachineVectorFile Load(string path, IsaProfile profile) {
    ArgumentNullException.ThrowIfNull(path);
    string text;
    try {
      text = File.ReadAllText(path, Encoding.UTF8);
    } catch (IOException exception) {
      throw new MachineException(Diagnostic.Create(Invalid, null, path, $"{exception.Message} (rule: readable)"));
    }

    return Parse(text, profile, path);
  }

  /// <summary>Reads and checks the text of a file.</summary>
  /// <param name="text">The JSON text.</param>
  /// <param name="profile">The machine profile the file must be for.</param>
  /// <param name="file">The file's name in a diagnostic.</param>
  /// <returns>The file.</returns>
  /// <exception cref="MachineException">The text breaks a rule (<c>PLY4030</c>) or observes storage the profile lacks (<c>PLY4032</c>).</exception>
  public static MachineVectorFile Parse(string text, IsaProfile profile, string file = "<text>") {
    ArgumentNullException.ThrowIfNull(text);
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(file);
    JsonDocument document;
    try {
      document = JsonDocument.Parse(text);
    } catch (JsonException exception) {
      throw new MachineException(Diagnostic.Create(Invalid, null, file, $"{exception.Message} (rule: json)"));
    }

    using (document)
      return new Reader(file, profile).File(document.RootElement);
  }

  /// <summary>The file in its canonical form.</summary>
  /// <returns>The JSON text, ending in a line break.</returns>
  public string Save() {
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new() { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) {
      writer.WriteStartObject();
      writer.WriteNumber("schemaVersion", SchemaVersion);
      writer.WriteString("fixture", this.Fixture);
      writer.WriteString("isa", this.Isa);
      writer.WriteStartObject("provenance");
      writer.WriteString("kind", this.Provenance.Kind);
      writer.WriteString("producer", this.Provenance.Producer);
      writer.WriteString("source", this.Provenance.Source);
      writer.WriteEndObject();
      this.WriteObserve(writer);
      if (this.Limits is { } limits) {
        writer.WriteStartObject("limits");
        Optional(writer, "maxCycles", limits.MaxCycles);
        Optional(writer, "maxSteps", limits.MaxSteps);
        writer.WriteEndObject();
      }

      writer.WriteStartArray("vectors");
      foreach (var vector in this.Vectors)
        WriteVector(writer, vector);
      writer.WriteEndArray();
      writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
  }

  /// <summary>Writes the file in its canonical form.</summary>
  /// <param name="path">The file.</param>
  public void Save(string path) {
    ArgumentNullException.ThrowIfNull(path);
    File.WriteAllText(path, this.Save(), new UTF8Encoding(false));
  }

  /// <summary>A projection's mode as the file spells it.</summary>
  /// <param name="mode">The mode.</param>
  /// <returns><c>semantic</c> or <c>raw</c>.</returns>
  public static string Name(TraceMode mode) => mode == TraceMode.Raw ? "raw" : "semantic";

  private void WriteObserve(Utf8JsonWriter writer) {
    writer.WriteStartObject("observe");
    Strings(writer, "registers", this.Observe.Registers);
    Strings(writer, "flags", this.Observe.Flags);
    writer.WriteStartArray("memory");
    foreach (var span in this.Observe.Memory) {
      writer.WriteStartObject();
      writer.WriteString("address", TraceEvent.Hex(span.Address));
      writer.WriteNumber("length", span.Length);
      writer.WriteEndObject();
    }

    writer.WriteEndArray();
    Strings(writer, "events", this.Observe.EventKinds);
    writer.WriteBoolean("cycles", this.Observe.Cycles);
    writer.WriteString("mode", Name(this.Observe.Mode));
    writer.WriteEndObject();
  }

  private static void WriteVector(Utf8JsonWriter writer, MachineVector vector) {
    writer.WriteStartObject();
    writer.WriteString("name", vector.Name);
    if (vector.Call is { } call) {
      writer.WriteStartObject("call");
      writer.WriteString("contract", call.Contract);
      Strings(writer, "arguments", call.Arguments.Select(argument => TraceEvent.Hex(argument)));
      writer.WriteEndObject();
    }

    writer.WriteStartObject("initial");
    WriteState(writer, vector.Initial);
    writer.WriteEndObject();
    writer.WriteStartObject("expected");
    WriteState(writer, vector.Expected.Final);
    writer.WriteStartArray("events");
    foreach (var item in vector.Expected.Events)
      WriteEvent(writer, item);
    writer.WriteEndArray();
    writer.WriteString("outcome", TraceComparer.Name(vector.Expected.Outcome));
    Optional(writer, "cycles", vector.Expected.Cycles);
    writer.WriteEndObject();
    writer.WriteEndObject();
  }

  internal static void WriteState(Utf8JsonWriter writer, MachineSnapshot state) {
    writer.WriteStartObject("registers");
    foreach (var (name, value) in state.Registers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
      writer.WriteString(name, TraceEvent.Hex(value));
    writer.WriteEndObject();
    writer.WriteStartObject("flags");
    foreach (var (name, value) in state.Flags.OrderBy(pair => pair.Key, StringComparer.Ordinal))
      if (value is { } defined)
        writer.WriteBoolean(name, defined);
      else
        writer.WriteNull(name);
    writer.WriteEndObject();
    writer.WriteStartArray("memory");
    foreach (var block in state.Memory) {
      writer.WriteStartObject();
      writer.WriteString("address", TraceEvent.Hex(block.Address));
      writer.WriteString("hex", Convert.ToHexString(block.Bytes.AsSpan()));
      writer.WriteEndObject();
    }

    writer.WriteEndArray();
  }

  internal static void WriteEvent(Utf8JsonWriter writer, TraceEvent item) {
    writer.WriteStartObject();
    writer.WriteString("kind", item.EventKind);
    switch (item) {
      case MemoryWrite write:
        writer.WriteString("address", TraceEvent.Hex(write.Address));
        writer.WriteString("hex", Convert.ToHexString(write.Bytes.AsSpan()));
        break;
      case DeviceAccess device:
        writer.WriteString("space", device.Space);
        writer.WriteString("address", TraceEvent.Hex(device.Address));
        writer.WriteNumber("width", device.Width);
        writer.WriteBoolean("write", device.IsWrite);
        writer.WriteString("value", TraceEvent.Hex(device.Value));
        break;
      case ExternalCall call:
        writer.WriteString("symbol", call.Symbol);
        if (call.Abi is { } abi)
          writer.WriteString("abi", abi);
        Strings(writer, "arguments", call.Arguments.Select(argument => TraceEvent.Hex(argument)));
        if (call.Result is { } result)
          writer.WriteString("result", TraceEvent.Hex(result));
        break;
      case Trap trap:
        writer.WriteString("trap", trap.Kind);
        if (trap.Vector is { } number)
          writer.WriteNumber("vector", number);
        if (trap.At is { } at)
          writer.WriteString("at", TraceEvent.Hex(at));
        break;
      case Return done when done.Value is { } value:
        writer.WriteString("value", TraceEvent.Hex(value));
        break;
    }

    writer.WriteEndObject();
  }

  private static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values) {
    writer.WriteStartArray(name);
    foreach (var value in values)
      writer.WriteStringValue(value);
    writer.WriteEndArray();
  }

  private static void Optional(Utf8JsonWriter writer, string name, long? value) {
    if (value is { } some)
      writer.WriteNumber(name, some);
  }

  /// <summary>Reads one file, throwing at the first broken rule.</summary>
  private sealed class Reader(string file, IsaProfile profile) {

    private MachineException Fail(string rule, string detail) => new(Diagnostic.Create(Invalid, null, file, $"{detail} (rule: {rule})"));

    public MachineVectorFile File(JsonElement root) {
      this.Only(root, "$", ["schemaVersion", "fixture", "isa", "provenance", "observe", "vectors"], ["limits"]);
      var version = this.Count(root.GetProperty("schemaVersion"), "schemaVersion", 0);
      if (version != SchemaVersion)
        throw this.Fail("schema-version", $"schemaVersion is {version}; this loader reads {SchemaVersion}");

      var isa = this.Text(root.GetProperty("isa"), "isa");
      if (isa != profile.Id)
        throw this.Fail("isa", $"isa '{isa}' is not the machine profile '{profile.Id}' it is loaded for");

      var provenance = this.Provenance(root.GetProperty("provenance"));
      var observe = this.Observe(root.GetProperty("observe"));
      var limits = root.TryGetProperty("limits", out var limitsElement) ? this.Limits(limitsElement) : null;
      var vectors = this.Array(root.GetProperty("vectors"), "vectors").Select((element, index) => this.Vector(element, $"vectors[{index}]")).ToImmutableArray();
      var names = new HashSet<string>(StringComparer.Ordinal);
      foreach (var vector in vectors.Where(vector => !names.Add(vector.Name)))
        throw this.Fail("unique-vector-name", $"vector name '{vector.Name}' is used twice");

      return new(this.Text(root.GetProperty("fixture"), "fixture"), isa, provenance, observe, limits, vectors);
    }

    private MachineVectorProvenance Provenance(JsonElement element) {
      this.Only(element, "provenance", ["kind", "producer", "source"], []);
      var kind = this.Text(element.GetProperty("kind"), "provenance.kind");
      if (!_provenanceKinds.Contains(kind))
        throw this.Fail("provenance", $"provenance.kind '{kind}' is not one of {string.Join(", ", _provenanceKinds)}");

      var producer = this.Text(element.GetProperty("producer"), "provenance.producer");
      if (producer == "polypiler")
        throw this.Fail("provenance", "provenance.producer is 'polypiler'; a reference is never computed by the compiler under test");

      return new(kind, producer, this.Text(element.GetProperty("source"), "provenance.source"));
    }

    private TraceProjection Observe(JsonElement element) {
      this.Only(element, "observe", ["registers", "flags", "memory", "events", "cycles", "mode"], []);
      var registers = this.Array(element.GetProperty("registers"), "observe.registers").Select((item, index) => this.Text(item, $"observe.registers[{index}]")).ToImmutableArray();
      var flags = this.Array(element.GetProperty("flags"), "observe.flags").Select((item, index) => this.Text(item, $"observe.flags[{index}]")).ToImmutableArray();
      foreach (var name in registers.Where(name => profile.Register(name) is null).Concat(flags.Where(name => profile.Flag(name) is null)))
        throw new MachineException(Diagnostic.Create(UnknownObserved, null, file, name, profile.Id));

      var memory = this.Array(element.GetProperty("memory"), "observe.memory").Select((item, index) => {
        var where = $"observe.memory[{index}]";
        this.Only(item, where, ["address", "length"], []);
        return new AddressSpan(this.Number(item.GetProperty("address"), where + ".address"), this.Count(item.GetProperty("length"), where + ".length", 0));
      }).ToImmutableArray();
      var events = this.Array(element.GetProperty("events"), "observe.events").Select((item, index) => this.Kind(item, $"observe.events[{index}]")).ToImmutableArray();
      var cycles = element.GetProperty("cycles");
      if (cycles.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        throw this.Fail("shape", "observe.cycles must be true or false");

      var mode = this.Text(element.GetProperty("mode"), "observe.mode") switch {
        "semantic" => TraceMode.Semantic,
        "raw" => TraceMode.Raw,
        var other => throw this.Fail("mode", $"observe.mode '{other}' is not semantic or raw")
      };
      return new(registers, flags, memory, events, cycles.GetBoolean(), mode);
    }

    private MachineLimits Limits(JsonElement element) {
      this.Only(element, "limits", [], ["maxCycles", "maxSteps"]);
      return new(this.OptionalLong(element, "maxCycles", "limits.maxCycles"), this.OptionalLong(element, "maxSteps", "limits.maxSteps"));
    }

    private MachineVector Vector(JsonElement element, string where) {
      this.Only(element, where, ["name", "initial", "expected"], ["call"]);
      MachineCall? call = null;
      if (element.TryGetProperty("call", out var callElement)) {
        this.Only(callElement, where + ".call", ["contract", "arguments"], []);
        call = new(this.Text(callElement.GetProperty("contract"), where + ".call.contract"),
          [.. this.Array(callElement.GetProperty("arguments"), where + ".call.arguments").Select((item, index) => this.Number(item, $"{where}.call.arguments[{index}]"))]);
      }

      var initialElement = element.GetProperty("initial");
      this.Only(initialElement, where + ".initial", ["registers", "flags", "memory"], []);
      var initial = this.State(initialElement, where + ".initial");
      var expectedElement = element.GetProperty("expected");
      this.Only(expectedElement, where + ".expected", ["registers", "flags", "memory", "events", "outcome"], ["cycles"]);
      var final = this.State(expectedElement, where + ".expected");
      var events = this.Array(expectedElement.GetProperty("events"), where + ".expected.events").Select((item, index) => this.Event(item, $"{where}.expected.events[{index}]")).ToImmutableArray();
      var outcome = this.Text(expectedElement.GetProperty("outcome"), where + ".expected.outcome") switch {
        "returned" => TraceOutcome.Returned,
        "trapped" => TraceOutcome.Trapped,
        "aborted" => TraceOutcome.Aborted,
        "budget-exceeded" => TraceOutcome.BudgetExceeded,
        var other => throw this.Fail("outcome", $"{where}.expected.outcome '{other}' is not returned, trapped, aborted or budget-exceeded")
      };
      var cycles = this.OptionalLong(expectedElement, "cycles", where + ".expected.cycles");
      return new(this.Text(element.GetProperty("name"), where + ".name"), call, initial, new(final, events, outcome, cycles));
    }

    private MachineSnapshot State(JsonElement element, string where) {
      var registers = ImmutableDictionary.CreateBuilder<string, ulong>(StringComparer.Ordinal);
      foreach (var property in this.Object(element.GetProperty("registers"), where + ".registers").EnumerateObject()) {
        var at = $"{where}.registers.{property.Name}";
        if (profile.Register(property.Name) is not { } register)
          throw this.Fail("storage", $"{at} names a register machine profile '{profile.Id}' does not define");
        var value = this.Number(property.Value, at);
        if (register.Bits < 64 && value >> register.Bits != 0)
          throw this.Fail("value-width", $"{at} {TraceEvent.Hex(value)} does not fit the {register.Bits} bits of '{register.Name}'");
        registers[property.Name] = value;
      }

      var flags = ImmutableDictionary.CreateBuilder<string, bool?>(StringComparer.Ordinal);
      foreach (var property in this.Object(element.GetProperty("flags"), where + ".flags").EnumerateObject()) {
        var at = $"{where}.flags.{property.Name}";
        if (profile.Flag(property.Name) is null)
          throw this.Fail("storage", $"{at} names a flag machine profile '{profile.Id}' does not define");
        flags[property.Name] = property.Value.ValueKind switch {
          JsonValueKind.True => true,
          JsonValueKind.False => false,
          JsonValueKind.Null => null,
          _ => throw this.Fail("shape", $"{at} must be true, false or null")
        };
      }

      var memory = this.Array(element.GetProperty("memory"), where + ".memory").Select((item, index) => {
        var at = $"{where}.memory[{index}]";
        this.Only(item, at, ["address", "hex"], []);
        return new MemoryBytes(this.Number(item.GetProperty("address"), at + ".address"), this.Bytes(item.GetProperty("hex"), at + ".hex"));
      }).ToImmutableArray();
      return new(registers.ToImmutable(), flags.ToImmutable(), memory);
    }

    private TraceEvent Event(JsonElement element, string where) {
      this.Object(element, where);
      if (!element.TryGetProperty("kind", out var kindElement))
        throw this.Fail("required", $"{where} has no 'kind'");

      switch (this.Kind(kindElement, where + ".kind")) {
        case TraceEvent.MemoryWriteKind:
          this.Only(element, where, ["kind", "address", "hex"], []);
          return new MemoryWrite(this.Number(element.GetProperty("address"), where + ".address"), this.Bytes(element.GetProperty("hex"), where + ".hex"));
        case TraceEvent.DeviceKind:
          this.Only(element, where, ["kind", "space", "address", "width", "write", "value"], []);
          var write = element.GetProperty("write");
          if (write.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw this.Fail("shape", $"{where}.write must be true or false");
          return new DeviceAccess(this.Text(element.GetProperty("space"), where + ".space"), this.Number(element.GetProperty("address"), where + ".address"),
            this.Count(element.GetProperty("width"), where + ".width", 1), write.GetBoolean(), this.Number(element.GetProperty("value"), where + ".value"));
        case TraceEvent.ExternalKind:
          this.Only(element, where, ["kind", "symbol", "arguments"], ["abi", "result"]);
          return new ExternalCall(this.Text(element.GetProperty("symbol"), where + ".symbol"),
            element.TryGetProperty("abi", out var abi) ? this.Text(abi, where + ".abi") : null,
            [.. this.Array(element.GetProperty("arguments"), where + ".arguments").Select((item, index) => this.Number(item, $"{where}.arguments[{index}]"))],
            element.TryGetProperty("result", out var result) ? this.Number(result, where + ".result") : null);
        case TraceEvent.TrapKind:
          this.Only(element, where, ["kind", "trap"], ["vector", "at"]);
          return new Trap(this.Text(element.GetProperty("trap"), where + ".trap"),
            element.TryGetProperty("vector", out var vector) ? this.Count(vector, where + ".vector", 0) : null,
            element.TryGetProperty("at", out var at) ? this.Number(at, where + ".at") : null);
        default:
          this.Only(element, where, ["kind"], ["value"]);
          return new Return(element.TryGetProperty("value", out var value) ? this.Number(value, where + ".value") : null);
      }
    }

    private string Kind(JsonElement element, string where) {
      var kind = this.Text(element, where);
      if (!TraceEvent.Kinds.Contains(kind))
        throw this.Fail("event-kind", $"{where} '{kind}' is not one of {string.Join(", ", TraceEvent.Kinds)}");
      return kind;
    }

    private JsonElement Object(JsonElement element, string where)
      => element.ValueKind == JsonValueKind.Object ? element : throw this.Fail("shape", $"{where} must be an object");

    private JsonElement.ArrayEnumerator Array(JsonElement element, string where)
      => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : throw this.Fail("shape", $"{where} must be an array");

    private string Text(JsonElement element, string where)
      => element.ValueKind == JsonValueKind.String ? element.GetString()! : throw this.Fail("shape", $"{where} must be a string");

    private void Only(JsonElement element, string where, string[] required, string[] optional) {
      this.Object(element, where);
      foreach (var name in required.Where(name => !element.TryGetProperty(name, out _)))
        throw this.Fail("required", $"{where} has no '{name}'");
      foreach (var property in element.EnumerateObject().Where(property => !required.Contains(property.Name) && !optional.Contains(property.Name)))
        throw this.Fail("shape", $"{where} has the unknown property '{property.Name}'");
    }

    private int Count(JsonElement element, string where, int minimum) {
      if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        throw this.Fail("shape", $"{where} must be an integer");
      return value >= minimum ? value : throw this.Fail("count", $"{where} is {value}; it must be at least {minimum}");
    }

    private long? OptionalLong(JsonElement element, string name, string where) {
      if (!element.TryGetProperty(name, out var value))
        return null;
      if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        throw this.Fail("shape", $"{where} must be an integer");
      return number >= 0 ? number : throw this.Fail("count", $"{where} is {number}; it must be at least 0");
    }

    private ulong Number(JsonElement element, string where) {
      var text = this.Text(element, where);
      if (text.Length is < 3 or > 18 || !text.StartsWith("0x", StringComparison.Ordinal) || !text.AsSpan(2).ContainsOnlyHexDigits())
        throw this.Fail("hex-number", $"{where} '{text}' is not a 0x-prefixed hexadecimal number of at most 16 digits");
      return ulong.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    private ImmutableArray<byte> Bytes(JsonElement element, string where) {
      var text = this.Text(element, where);
      if (text.Length % 2 != 0 || !text.AsSpan().ContainsOnlyHexDigits())
        throw this.Fail("hex-bytes", $"{where} '{text}' is not an even number of hexadecimal digits");
      return [.. Convert.FromHexString(text)];
    }

  }

}

file static class HexSpans {

  public static bool ContainsOnlyHexDigits(this ReadOnlySpan<char> text) {
    foreach (var c in text)
      if (!char.IsAsciiHexDigit(c))
        return false;
    return true;
  }

}
