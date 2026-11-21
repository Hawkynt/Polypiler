using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Core.MachineSemantics;

/// <summary>
/// Reads a machine profile from YAML: a spec document of the family <c>machine</c> (the metadata block,
/// the profile under <c>spec</c>) or the bare profile. The body is
/// <c>{isa, addressBits, byteOrder, interruptVectors?, registers: [{name, bits, parent?, offset?, write?, class?}], flags: [{name, register?, bit?}]}</c>,
/// as <c>spec/schemas/isa-profile.schema.json</c> states it. Every problem is <c>PLY4003</c>.
/// </summary>
public static class ProfileLoader {

  private static readonly string[] _bodyKeys = ["isa", "addressBits", "byteOrder", "interruptVectors", "registers", "flags"];
  private static readonly string[] _registerKeys = ["name", "bits", "parent", "offset", "write", "class"];
  private static readonly string[] _flagKeys = ["name", "register", "bit"];

  /// <summary>Reads and checks a profile file.</summary>
  /// <param name="path">The file.</param>
  /// <returns>The profile.</returns>
  /// <exception cref="MachineException">The file cannot be read or breaks a rule.</exception>
  public static IsaProfile Load(string path) {
    ArgumentNullException.ThrowIfNull(path);
    string text;
    try {
      text = File.ReadAllText(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      throw new MachineException(MachineDiagnostics.InvalidProfile(Path.GetFileNameWithoutExtension(path), $"cannot be read: {exception.Message}", path));
    }

    return Parse(text, path);
  }

  /// <summary>Reads and checks a profile from its text.</summary>
  /// <param name="text">The YAML.</param>
  /// <param name="file">The file it came from, for the diagnostics, or <see langword="null"/>.</param>
  /// <returns>The profile.</returns>
  /// <exception cref="MachineException">The text breaks a rule; the exception carries the first problem.</exception>
  public static IsaProfile Parse(string text, string? file = null) {
    ArgumentNullException.ThrowIfNull(text);
    var name = file is null ? "<text>" : Path.GetFileNameWithoutExtension(file);
    var stream = new YamlStream();
    try {
      stream.Load(new StringReader(text));
    } catch (YamlException exception) {
      throw new MachineException(MachineDiagnostics.InvalidProfile(name, $"not valid YAML: {exception.Message}", file));
    }

    if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
      throw new MachineException(MachineDiagnostics.InvalidProfile(name, "the file must be a mapping", file));

    var reader = new Reader(name, file);
    var body = root.Children.TryGetValue(new YamlScalarNode("spec"), out var spec) ? reader.Mapping(spec, "spec") : root;
    var fields = reader.Properties(body, "the profile", _bodyKeys);
    var id = reader.Text(reader.Required(fields, "isa"), "isa");
    reader = new Reader(id, file);
    var addressBits = (int)reader.Integer(reader.Required(fields, "addressBits"), "addressBits");
    var byteOrder = reader.Text(reader.Required(fields, "byteOrder"), "byteOrder") switch {
      "little" => ByteOrder.Little,
      "big" => ByteOrder.Big,
      var other => throw reader.Error($"byteOrder must be 'little' or 'big', not '{other}'")
    };
    int? vectors = fields.TryGetValue("interruptVectors", out var vectorNode) ? (int)reader.Integer(vectorNode, "interruptVectors") : null;
    var registers = reader.Sequence(reader.Required(fields, "registers"), "registers").Select(node => Register(reader, node)).ToImmutableArray();
    var flags = fields.TryGetValue("flags", out var flagNode)
      ? reader.Sequence(flagNode, "flags").Select(node => Flag(reader, node)).ToImmutableArray()
      : [];

    var profile = new IsaProfile(id, addressBits, byteOrder, registers, flags) { InterruptVectors = vectors };
    var problems = profile.Validate(file);
    return problems.IsEmpty ? profile : throw new MachineException(problems[0]);
  }

  private static RegisterDescriptor Register(Reader reader, YamlNode node) {
    var fields = reader.Properties(reader.Mapping(node, "a register"), "a register", _registerKeys);
    var name = reader.Text(reader.Required(fields, "name"), "name");
    var bits = (int)reader.Integer(reader.Required(fields, "bits"), $"{name}.bits");
    var parent = fields.TryGetValue("parent", out var parentNode) ? reader.Text(parentNode, $"{name}.parent") : null;
    var offset = fields.TryGetValue("offset", out var offsetNode) ? (int)reader.Integer(offsetNode, $"{name}.offset") : 0;
    var write = fields.TryGetValue("write", out var writeNode)
      ? reader.Text(writeNode, $"{name}.write") switch {
        "preserve" => SubRegisterWrite.Preserve,
        "zero-extend" => SubRegisterWrite.ZeroExtend,
        var other => throw reader.Error($"{name}.write must be 'preserve' or 'zero-extend', not '{other}'")
      }
      : SubRegisterWrite.Preserve;
    var registerClass = fields.TryGetValue("class", out var classNode)
      ? reader.Text(classNode, $"{name}.class") switch {
        "general" => RegisterClass.General,
        "stack-pointer" => RegisterClass.StackPointer,
        "program-counter" => RegisterClass.ProgramCounter,
        "segment" => RegisterClass.Segment,
        "status" => RegisterClass.Status,
        "float" => RegisterClass.Float,
        "vector" => RegisterClass.Vector,
        var other => throw reader.Error($"{name}.class '{other}' is not a register class")
      }
      : RegisterClass.General;
    if (parent is null && (offset != 0 || fields.ContainsKey("write")))
      throw reader.Error($"root register '{name}' has no parent; offset and write belong to sub-registers");
    return new(name, bits, parent, offset, write, registerClass);
  }

  private static FlagDescriptor Flag(Reader reader, YamlNode node) {
    var fields = reader.Properties(reader.Mapping(node, "a flag"), "a flag", _flagKeys);
    var name = reader.Text(reader.Required(fields, "name"), "name");
    var register = fields.TryGetValue("register", out var registerNode) ? reader.Text(registerNode, $"{name}.register") : null;
    var bit = fields.TryGetValue("bit", out var bitNode) ? (int)reader.Integer(bitNode, $"{name}.bit") : 0;
    if (register is null && fields.ContainsKey("bit"))
      throw reader.Error($"flag '{name}' has a bit but no register");
    return new(name, register, bit);
  }

  /// <summary>Field access that reports every problem as <c>PLY4003</c> of one profile.</summary>
  private sealed class Reader(string profile, string? file) {

    public MachineException Error(string reason) => new(MachineDiagnostics.InvalidProfile(profile, reason, file));

    public YamlMappingNode Mapping(YamlNode node, string what) => node as YamlMappingNode ?? throw this.Error($"{what} must be a mapping");

    public IEnumerable<YamlNode> Sequence(YamlNode node, string what) => node as YamlSequenceNode ?? throw this.Error($"{what} must be a list");

    public Dictionary<string, YamlNode> Properties(YamlMappingNode mapping, string where, string[] allowed) {
      var properties = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
      foreach (var (key, value) in mapping.Children) {
        var name = key is YamlScalarNode { Value: { } text } ? text : "";
        if (!allowed.Contains(name))
          throw this.Error($"{where} has the unknown property '{name}'");
        properties[name] = value;
      }

      return properties;
    }

    public YamlNode Required(Dictionary<string, YamlNode> properties, string name)
      => properties.TryGetValue(name, out var node) ? node : throw this.Error($"the property '{name}' is missing");

    public string Text(YamlNode node, string field)
      => node is YamlScalarNode { Value: { Length: > 0 } text } ? text : throw this.Error($"'{field}' must be text");

    public long Integer(YamlNode node, string field)
      => node is YamlScalarNode { Style: ScalarStyle.Plain, Value: { } text } && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
        ? value
        : throw this.Error($"'{field}' must be an integer");

  }

}
