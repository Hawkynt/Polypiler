using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Polypiler.Core;

namespace Polypiler.Tooling.Spec.Families;

/// <summary>
/// The family <c>ir</c> (PRD §4.1–§4.3, ADR 0001): one document per IR operation under
/// <c>spec/ir/operations/</c>, one per IR stage under <c>spec/ir/stages/</c> and the IR version in
/// <c>spec/ir/version.yaml</c>. The schema <c>spec/schemas/ir.schema.json</c> states the shape of each;
/// <see cref="Check"/> adds the rules across fields and files.
/// </summary>
public sealed class IrSpecFamily : ISpecFamily {

  private const string OperationsFolder = "operations";
  private const string StagesFolder = "stages";
  private const string VersionFile = "version.yaml";

  /// <inheritdoc />
  public string Folder => "ir";

  /// <inheritdoc />
  public string SchemaPath => "spec/schemas/ir.schema.json";

  /// <summary>
  /// The rules of the family, each <c>PLY0046</c> at the offending value: <c>layout</c> (the path follows
  /// from the kind, the ID and the mnemonic), <c>stage-range</c>, <c>example-arity</c>,
  /// <c>flag-declared</c>, <c>trap-declared</c>, <c>type-declared</c>, <c>unique-mnemonic</c> and
  /// <c>unique-stage-order</c> (both reported at the later document in path order). Every stage,
  /// effect and <c>TYPES.</c> ID must name a document (<c>PLY0043</c>).
  /// </summary>
  /// <param name="document">The document, whose body conforms to the schema.</param>
  /// <param name="tree">The tree the references and the uniqueness rules look into.</param>
  /// <returns>What is wrong.</returns>
  public IEnumerable<Diagnostic> Check(SpecDocument document, SpecTree tree) {
    ArgumentNullException.ThrowIfNull(document);
    ArgumentNullException.ThrowIfNull(tree);
    var body = (JsonObject)document.Body!;
    var kind = (string)body["kind"]!;
    var problems = new List<Diagnostic>();
    if (Layout(document, body, kind) is { } layout)
      problems.Add(layout);

    switch (kind) {
      case "operation":
        problems.AddRange(CheckOperation(document, body, tree));
        break;
      case "stage":
        if (Earlier(document, tree, "stage", "order", body["order"]!.ToJsonString()) is { } holder)
          problems.Add(SpecDiagnostics.Rule(document, "/spec/order", $"stage order {body["order"]} is already used by {holder.Id}", "unique-stage-order"));
        break;
    }

    return problems;
  }

  /// <summary>Rule <c>layout</c>: the folder holds the kind, the ID follows from the path, an operation's file is its mnemonic in lower case.</summary>
  private static Diagnostic? Layout(SpecDocument document, JsonObject body, string kind) {
    var path = document.Path;
    var shown = "spec/" + path;
    var parts = path.Split('/');
    string expectedKind, expectedId;
    switch (parts) {
      case [_, VersionFile]:
        (expectedKind, expectedId) = ("version", "IR.version");
        break;
      case [_, OperationsFolder, var file]:
        (expectedKind, expectedId) = ("operation", "IR." + file[..^5]);
        break;
      case [_, StagesFolder, var file]:
        (expectedKind, expectedId) = ("stage", "IR.stage." + file[..^5]);
        break;
      default:
        return SpecDiagnostics.Rule(document, "", $"path {shown} is outside the layout of spec/ir/: operations/<mnemonic>.yaml, stages/<name>.yaml or version.yaml", "layout");
    }

    if (kind != expectedKind)
      return SpecDiagnostics.Rule(document, "/spec/kind", $"kind '{kind}' does not match path {shown}, expected '{expectedKind}'", "layout");
    if (document.Id != expectedId)
      return SpecDiagnostics.Rule(document, "/id", $"id '{document.Id}' does not match path {shown}, expected '{expectedId}'", "layout");
    if (kind == "operation" && (string)body["mnemonic"]! is var mnemonic && "IR." + mnemonic.ToLowerInvariant() != expectedId)
      return SpecDiagnostics.Rule(document, "/spec/mnemonic", $"mnemonic '{mnemonic}' does not match path {shown}, expected the file spec/ir/operations/{mnemonic.ToLowerInvariant()}.yaml", "layout");
    return null;
  }

  private static IEnumerable<Diagnostic> CheckOperation(SpecDocument document, JsonObject body, SpecTree tree) {
    var mnemonic = (string)body["mnemonic"]!;
    if (Earlier(document, tree, "operation", "mnemonic", body["mnemonic"]!.ToJsonString()) is { } holder)
      yield return SpecDiagnostics.Rule(document, "/spec/mnemonic", $"mnemonic '{mnemonic}' is already defined by {holder.Id}", "unique-mnemonic");

    var parameters = ((JsonArray)body["typeParameters"]!).Select(parameter => (string)parameter!["name"]!).ToHashSet(StringComparer.Ordinal);
    var operands = (JsonArray)body["operands"]!;
    for (var index = 0; index < operands.Count; ++index)
      if (TypeProblem(document, tree, parameters, $"/spec/operands/{index}/type", (string)operands[index]!["type"]!, $"operand '{operands[index]!["name"]}'") is { } problem)
        yield return problem;
    if (body["result"] is JsonObject result && TypeProblem(document, tree, parameters, "/spec/result/type", (string)result["type"]!, "the result") is { } resultProblem)
      yield return resultProblem;

    var effects = (JsonArray)body["effects"]!;
    for (var index = 0; index < effects.Count; ++index)
      if ((string)effects[index]! is var effect && effect != "pure" && tree.Resolve(document, $"/spec/effects/{index}", effect) is { } broken)
        yield return broken;

    foreach (var problem in StageRange(document, body, tree))
      yield return problem;

    foreach (var problem in Examples(document, body, operands.Count))
      yield return problem;
  }

  /// <summary>Rule <c>type-declared</c>: a type is a declared type parameter or the ID of a document.</summary>
  private static Diagnostic? TypeProblem(SpecDocument document, SpecTree tree, HashSet<string> parameters, string pointer, string type, string what) {
    if (type.StartsWith("TYPES.", StringComparison.Ordinal))
      return tree.Resolve(document, pointer, type);
    return parameters.Contains(type) ? null : SpecDiagnostics.Rule(document, pointer, $"{what} has type '{type}', which is no type parameter of the operation", "type-declared");
  }

  /// <summary>Rule <c>stage-range</c>, and the references of <c>stages</c>.</summary>
  private static IEnumerable<Diagnostic> StageRange(SpecDocument document, JsonObject body, SpecTree tree) {
    var from = (string)body["stages"]!["from"]!;
    var to = (string)body["stages"]!["to"]!;
    var fromOrder = StageOrder(document, tree, "from", from, out var fromProblem);
    var toOrder = StageOrder(document, tree, "to", to, out var toProblem);
    if (fromProblem is not null)
      yield return fromProblem;
    if (toProblem is not null)
      yield return toProblem;
    if (fromOrder > toOrder)
      yield return SpecDiagnostics.Rule(document, "/spec/stages/from", $"stages.from '{from}' (order {fromOrder}) is later than stages.to '{to}' (order {toOrder})", "stage-range");
  }

  /// <summary>
  /// The order of the stage a reference names: <see langword="null"/> when the reference is broken
  /// (<c>PLY0043</c>), names a document of another kind (<c>stage-range</c>) or names a stage whose own
  /// body is invalid and reports that itself.
  /// </summary>
  private static long? StageOrder(SpecDocument document, SpecTree tree, string end, string id, out Diagnostic? problem) {
    var pointer = "/spec/stages/" + end;
    problem = tree.Resolve(document, pointer, id);
    if (problem is not null)
      return null;

    var target = tree.ById[id].Body as JsonObject;
    if (target?["kind"] is JsonValue kindValue && kindValue.TryGetValue<string>(out var kind) && kind != "stage") {
      problem = SpecDiagnostics.Rule(document, pointer, $"stages.{end} '{id}' is a document of kind '{kind}', not a stage", "stage-range");
      return null;
    }

    return target?["order"] is JsonValue orderValue && orderValue.TryGetValue<long>(out var order) ? order : null;
  }

  /// <summary>Rules <c>example-arity</c>, <c>flag-declared</c> and <c>trap-declared</c>; examples are counted from 1.</summary>
  private static IEnumerable<Diagnostic> Examples(SpecDocument document, JsonObject body, int arity) {
    var flags = ((JsonArray)body["flags"]!).Select(flag => (string)flag!).ToList();
    var traps = ((JsonArray)body["traps"]!).Select(trap => (string)trap!["kind"]!).Distinct(StringComparer.Ordinal).ToList();
    var examples = (JsonArray)body["examples"]!;
    for (var index = 0; index < examples.Count; ++index) {
      var example = (JsonObject)examples[index]!;
      var number = index + 1;
      var pointer = $"/spec/examples/{index}";
      var count = ((JsonArray)example["operands"]!).Count;
      if (count != arity)
        yield return SpecDiagnostics.Rule(document, pointer + "/operands", $"example {number} has {count} operands, the operation declares {arity}", "example-arity");

      if (example["flag"] is { } flagNode) {
        var flag = (string)flagNode!;
        if (!flags.Contains(flag))
          yield return SpecDiagnostics.Rule(document, pointer + "/flag", $"example {number} uses flag '{flag}', {Declared("flags", flags)}", "flag-declared");
      } else if (flags.Count > 0) {
        yield return SpecDiagnostics.Rule(document, pointer, $"example {number} names no flag, {Declared("flags", flags)}", "flag-declared");
      }

      if (example["trap"] is { } trapNode && (string)trapNode! is var trap && !traps.Contains(trap))
        yield return SpecDiagnostics.Rule(document, pointer + "/trap", $"example {number} traps with '{trap}', {Declared("traps", traps)}", "trap-declared");
    }
  }

  private static string Declared(string what, List<string> values)
    => values.Count == 0 ? $"the operation declares no {what}" : $"declared {what} are {string.Join(", ", values)}";

  /// <summary>
  /// The first document before this one in path order, in the same family, with the same kind and the
  /// same value of a body property: the holder a uniqueness rule names.
  /// </summary>
  private static SpecDocument? Earlier(SpecDocument document, SpecTree tree, string kind, string property, string json)
    => tree.Documents
      .TakeWhile(other => !ReferenceEquals(other, document))
      .FirstOrDefault(other => other.Family == document.Family
        && other.Body is JsonObject otherBody
        && otherBody["kind"] is JsonValue otherKind && otherKind.ToJsonString() == $"\"{kind}\""
        && otherBody[property] is { } value && value.ToJsonString() == json);

}
