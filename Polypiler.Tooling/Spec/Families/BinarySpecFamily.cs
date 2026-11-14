using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Polypiler.Core;

namespace Polypiler.Tooling.Spec.Families;

/// <summary>
/// The family <c>binary</c> (PRD §4.1): CPU instruction tables, token tables of BASIC dialects and
/// binary file layouts, one per file at <c>spec/binary/&lt;name&gt;/&lt;kind&gt;.yaml</c> with the ID
/// <c>BINARY.&lt;name&gt;.&lt;kind&gt;</c>. The body schema is <c>spec/schemas/binary.schema.json</c>;
/// <see cref="Check"/> adds the rules it cannot state.
/// </summary>
public sealed class BinarySpecFamily : ISpecFamily {

  /// <summary>The kinds of document, each of which is the file name of its documents.</summary>
  private static readonly string[] _kinds = ["instructions", "tokens", "format"];

  /// <inheritdoc />
  public string Folder => "binary";

  /// <inheritdoc />
  public string SchemaPath => "spec/schemas/binary.schema.json";

  /// <summary>
  /// The rules of the family, the layout first: <c>layout</c> (path, file name and ID follow from
  /// the kind and the name), then for an instruction table <c>register-unique</c>,
  /// <c>opcode-unique</c>, <c>mode-declared</c>, <c>flags-declared</c> and
  /// <c>length-matches-mode</c>, for a token table <c>token-unique</c> and <c>token-in-range</c>, for
  /// a format <c>fields-ordered</c>, each as <c>PLY0046</c> at the offending value. The <c>parent</c>
  /// of a CPU names a spec document (<c>PLY0043</c>).
  /// </summary>
  /// <param name="document">The document, whose body conforms to the schema.</param>
  /// <param name="tree">The tree the references are resolved in.</param>
  /// <returns>What is wrong.</returns>
  public IEnumerable<Diagnostic> Check(SpecDocument document, SpecTree tree) {
    ArgumentNullException.ThrowIfNull(document);
    ArgumentNullException.ThrowIfNull(tree);
    var body = (JsonObject)document.Body!;
    var kind = (string)body["kind"]!;
    if (Layout(document, kind) is { } misplaced)
      return [misplaced];

    return kind switch {
      "instructions" => Instructions(document, body, tree),
      "tokens" => Tokens(document, body),
      _ => Fields(document, body)
    };
  }

  /// <summary>Rule <c>layout</c>: the file is <c>binary/&lt;name&gt;/&lt;kind&gt;.yaml</c> and its ID is <c>BINARY.&lt;name&gt;.&lt;kind&gt;</c>.</summary>
  private static Diagnostic? Layout(SpecDocument document, string kind) {
    var parts = document.Path.Split('/');
    if (parts.Length != 3 || !_kinds.Any(known => parts[2] == known + ".yaml"))
      return SpecDiagnostics.Rule(document, "", $"path '{document.Path}' does not follow binary/<name>/<kind>.yaml with kind {string.Join(", ", _kinds)}", "layout");
    if (parts[2] != kind + ".yaml")
      return SpecDiagnostics.Rule(document, "/spec/kind", $"kind '{kind}' must be in a file named {kind}.yaml, found {parts[2]}", "layout");

    var id = $"BINARY.{parts[1]}.{kind}";
    return document.Meta!.Id == id ? null : SpecDiagnostics.Rule(document, "/id", $"id '{document.Meta.Id}' must be '{id}' for {document.Path}", "layout");
  }

  private static IEnumerable<Diagnostic> Instructions(SpecDocument document, JsonObject body, SpecTree tree) {
    if (body["cpu"]!["parent"] is { } parent && tree.Resolve(document, "/spec/cpu/parent", (string)parent!) is { } unknown)
      yield return unknown;

    var registers = new HashSet<string>(StringComparer.Ordinal);
    var registerList = (JsonArray)body["registers"]!;
    for (var index = 0; index < registerList.Count; ++index) {
      var name = (string)registerList[index]!["name"]!;
      if (!registers.Add(name))
        yield return SpecDiagnostics.Rule(document, $"/spec/registers/{index}/name", $"register '{name}' is defined twice", "register-unique");
    }

    var flags = ((JsonArray)body["flags"]!).Select(flag => (string)flag!).ToList();
    var modes = new Dictionary<string, long>(StringComparer.Ordinal);
    foreach (var mode in (JsonArray)body["modes"]!)
      modes.TryAdd((string)mode!["name"]!, (long)mode["operandBytes"]!);

    var opcodes = new Dictionary<string, string>(StringComparer.Ordinal);
    var instructions = (JsonArray)body["instructions"]!;
    for (var index = 0; index < instructions.Count; ++index) {
      var instruction = instructions[index]!;
      var at = $"/spec/instructions/{index}";
      var opcode = (string)instruction["opcode"]!;
      var mode = (string)instruction["mode"]!;
      var described = $"{(string)instruction["mnemonic"]!} {mode}";
      if (!opcodes.TryAdd(opcode, described))
        yield return SpecDiagnostics.Rule(document, $"{at}/opcode", $"opcode {opcode} is defined twice ({opcodes[opcode]}, {described})", "opcode-unique");

      var affected = (JsonArray)instruction["flags"]!;
      for (var flag = 0; flag < affected.Count; ++flag)
        if (!flags.Contains((string)affected[flag]!))
          yield return SpecDiagnostics.Rule(document, $"{at}/flags/{flag}", $"instruction {opcode} affects flag '{(string)affected[flag]!}', declared flags are {string.Join(", ", flags)}", "flags-declared");

      if (!modes.TryGetValue(mode, out var operandBytes)) {
        yield return SpecDiagnostics.Rule(document, $"{at}/mode", $"instruction {opcode} uses mode '{mode}', declared modes are {string.Join(", ", modes.Keys)}", "mode-declared");
        continue;
      }

      var opcodeBytes = (opcode.Length - 2) / 2;
      var length = (long)instruction["length"]!;
      if (length != opcodeBytes + operandBytes)
        yield return SpecDiagnostics.Rule(document, $"{at}/length", $"instruction {opcode} has length {length}, opcode ({opcodeBytes}) + mode '{mode}' ({operandBytes}) is {opcodeBytes + operandBytes}", "length-matches-mode");
    }
  }

  private static IEnumerable<Diagnostic> Tokens(SpecDocument document, JsonObject body) {
    var minText = (string)body["range"]!["min"]!;
    var maxText = (string)body["range"]!["max"]!;
    var (min, max) = (Hex(minText), Hex(maxText));
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var texts = new Dictionary<string, string>(StringComparer.Ordinal);
    var tokens = (JsonArray)body["tokens"]!;
    for (var index = 0; index < tokens.Count; ++index) {
      var value = (string)tokens[index]!["value"]!;
      var text = (string)tokens[index]!["text"]!;
      var at = $"/spec/tokens/{index}";
      if (!values.TryAdd(value, text))
        yield return SpecDiagnostics.Rule(document, $"{at}/value", $"token value {value} is defined twice ({values[value]}, {text})", "token-unique");
      if (!texts.TryAdd(text, value))
        yield return SpecDiagnostics.Rule(document, $"{at}/text", $"token text '{text}' is defined twice ({texts[text]}, {value})", "token-unique");
      var number = Hex(value);
      if (number < min || number > max)
        yield return SpecDiagnostics.Rule(document, $"{at}/value", $"token {value} ({text}) is outside the range {minText}-{maxText}", "token-in-range");
    }
  }

  private static IEnumerable<Diagnostic> Fields(SpecDocument document, JsonObject body) {
    var fields = (JsonArray)body["fields"]!;
    for (var index = 1; index < fields.Count; ++index) {
      var (previous, field) = (fields[index - 1]!, fields[index]!);
      var (previousName, previousOffset, previousSize) = ((string)previous["name"]!, (long)previous["offset"]!, (long)previous["size"]!);
      var (name, offset) = ((string)field["name"]!, (long)field["offset"]!);
      var at = $"/spec/fields/{index}/offset";
      if (offset < previousOffset)
        yield return SpecDiagnostics.Rule(document, at, $"field '{name}' at offset {offset} comes before '{previousName}' (offset {previousOffset})", "fields-ordered");
      else if (offset < previousOffset + previousSize)
        yield return SpecDiagnostics.Rule(document, at, $"field '{name}' at offset {offset} overlaps '{previousName}' (offset {previousOffset}, size {previousSize})", "fields-ordered");
    }
  }

  /// <summary>The number a hex string of the schema, <c>0x</c> and one or two bytes, stands for.</summary>
  private static int Hex(string text) => int.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

}
