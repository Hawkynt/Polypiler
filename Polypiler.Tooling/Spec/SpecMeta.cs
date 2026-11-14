using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Polypiler.Tooling.Spec;

/// <summary>
/// The metadata block of a spec document (PRD §4.1, <c>spec/schemas/meta.schema.json</c>): the keys
/// before the family body under <c>spec</c>. <see cref="SpecValidator"/> hands a family only documents
/// whose block passed the schema and the metadata rules.
/// </summary>
/// <param name="Id"><c>&lt;FAMILY&gt;.&lt;name&gt;</c>, unique across <c>spec/</c>.</param>
/// <param name="Title">One to 120 characters.</param>
/// <param name="Version">At least 1; raised by every normative change.</param>
/// <param name="Status"><c>draft</c>, <c>normative</c> or <c>deprecated</c>.</param>
/// <param name="Owner">A GitHub login or team.</param>
/// <param name="Issue">A backlog issue ID or <c>#&lt;number&gt;</c>.</param>
/// <param name="Tests">Repository-relative paths of the tests that hold the document.</param>
/// <param name="IntroducedIn">The phase, <c>P0</c> to <c>P6</c>, that introduced the document.</param>
/// <param name="DeprecatedIn">The phase that deprecated it, or <see langword="null"/>.</param>
/// <param name="Compatibility"><c>none</c>, <c>additive</c> or <c>breaking</c>: the impact of this version on the previous one.</param>
/// <param name="ReviewedRevision">The last version that was reviewed, 0 when none was.</param>
/// <param name="Normative">The normative text.</param>
public sealed record SpecMeta(
  string Id,
  string Title,
  long Version,
  string Status,
  string Owner,
  string Issue,
  IReadOnlyList<string> Tests,
  string IntroducedIn,
  string? DeprecatedIn,
  string Compatibility,
  long ReviewedRevision,
  string Normative) {

  /// <summary>The block of a document whose keys have the types of the schema, or <see langword="null"/>.</summary>
  /// <param name="document">The whole document.</param>
  /// <returns>The block; <see langword="null"/> when the document is no mapping or a key is missing or of the wrong type.</returns>
  public static SpecMeta? From(JsonNode? document) {
    if (document is not JsonObject root
      || Text(root, "id") is not { } id || Text(root, "title") is not { } title || Integer(root, "version") is not { } version
      || Text(root, "status") is not { } status || Text(root, "owner") is not { } owner || Text(root, "issue") is not { } issue
      || root["tests"] is not JsonArray testList || testList.Any(test => Text(test) is null)
      || Text(root, "introducedIn") is not { } introducedIn || Text(root, "compatibility") is not { } compatibility
      || Integer(root, "reviewedRevision") is not { } reviewedRevision || Text(root, "normative") is not { } normative)
      return null;

    var deprecatedIn = Text(root, "deprecatedIn");
    if (deprecatedIn is null && root.ContainsKey("deprecatedIn"))
      return null;

    return new(id, title, version, status, owner, issue, [.. testList.Select(test => Text(test)!)], introducedIn, deprecatedIn, compatibility, reviewedRevision, normative);
  }

  private static string? Text(JsonObject root, string key) => Text(root[key]);

  private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

  /// <summary>An integer, written as such or as a number without a fraction (JSON Schema's <c>integer</c>).</summary>
  private static long? Integer(JsonObject root, string key) {
    if (root[key] is not JsonValue value)
      return null;
    if (value.TryGetValue<long>(out var number))
      return number;
    return value.TryGetValue<double>(out var real) && real == double.Floor(real) && double.Abs(real) < 1e15 ? (long)real : null;
  }

}
