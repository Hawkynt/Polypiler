using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Polypiler.Core.IR.Intrinsics;

/// <summary>
/// Reads a contract document of <c>spec/intrinsics/</c> into an <see cref="IntrinsicContract"/>. The
/// document comes as JSON - <c>polypiler-tooling</c> and the tests turn the YAML into it -, the whole
/// document with its <c>spec</c> body. Every property the contract carries is checked as it is read; a
/// document the schema <c>spec/schemas/intrinsics.schema.json</c> refuses for one of them is refused
/// here too, with <c>PLY0251</c> naming the file and the property.
/// </summary>
public static partial class ContractLoader {

  private static readonly HashSet<string> _groups = new(StringComparer.Ordinal) {
    "IntMath", "FloatMath", "Boolean", "Bitfield", "BitCount", "Shuffle", "Saturation", "Carry", "Endian", "Trig", "Sequence", "Matrix", "Tensor", "CopyFill", "String", "Enum"
  };

  private static readonly HashSet<string> _factKeys = new(StringComparer.Ordinal) { "resultRange", "knownZeroBits", "knownOneBits" };

  /// <summary>Reads one document.</summary>
  /// <param name="file">The name of the file, which the diagnostics name.</param>
  /// <param name="document">The document: <c>id</c>, the metadata and the <c>spec</c> body.</param>
  /// <returns>The contract.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  /// <exception cref="IntrinsicRegistryException"><c>PLY0251</c>: the document is no contract.</exception>
  public static IntrinsicContract Load(string file, JsonNode? document) {
    ArgumentNullException.ThrowIfNull(file);
    var reader = new Reader(file);
    var body = reader.Object(reader.Object(document, "document")["spec"], "spec");
    var name = reader.Text(body, "name", NamePattern());
    var typeParameters = reader.List(body, "typeParameters", (item, at) => new IntrinsicTypeParameter(
      reader.Text(reader.Object(item, at), "name", NamePattern(), at), reader.Choice(reader.Object(item, at), "constraint", Enum.GetNames<IntrinsicConstraint>().Select(choice => choice.ToLowerInvariant()), at) switch {
        "integer" => IntrinsicConstraint.Integer,
        "unsigned" => IntrinsicConstraint.Unsigned,
        "signed" => IntrinsicConstraint.Signed,
        "float" => IntrinsicConstraint.Float,
        _ => IntrinsicConstraint.Any
      }));
    var declared = typeParameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
    var parameters = reader.List(body, "parameters", (item, at) => reader.Value(item, at, declared));
    var returns = reader.List(body, "returns", (item, at) => reader.Value(item, at, declared));
    if (returns.Count is < 1 or > 4)
      throw reader.Fail("returns", $"{returns.Count} results, a contract has one to four");

    var exactness = reader.Object(body["exactness"], "exactness");
    var mode = reader.Choice(exactness, "mode", ["exact", "tolerance"], "exactness");
    var policy = exactness["policy"] is null ? null : reader.Text(exactness, "policy", null, "exactness");
    if ((mode == "tolerance") != (policy is not null))
      throw reader.Fail("exactness", mode == "tolerance" ? "mode 'tolerance' without a policy" : "a policy for mode 'exact'");

    var effects = reader.List(body, "effects", (item, at) => reader.Scalar(item, at));
    if (effects.Count == 0 || (effects.Contains("pure") && effects.Count > 1))
      throw reader.Fail("effects", "'pure' alone, or one or more EFFECTS. ids");

    var reference = reader.Object(body["reference"], "reference");
    var spelling = body["spelling"] is null ? name : reader.Text(body, "spelling", SpellingPattern());
    return new IntrinsicContract {
      Id = new(name),
      Spelling = spelling,
      Group = reader.Choice(body, "group", _groups),
      TypeParameters = typeParameters,
      Parameters = parameters,
      Returns = returns,
      Attributes = body["attributes"] is null ? [] : reader.List(body, "attributes", (item, at) => reader.Attribute(item, at)),
      Legality = body["legality"] is null ? [] : reader.List(body, "legality", (item, at) => reader.Text(reader.Object(item, at), "id", null, at)),
      Effects = effects,
      Exactness = mode == "exact" ? IntrinsicExactness.Exact : IntrinsicExactness.Tolerance,
      TolerancePolicy = policy,
      Algebra = body["algebra"] is null ? [] : reader.List(body, "algebra", (item, at) => Algebra(reader, item, at)),
      LowersTo = body["lowersTo"] is null ? [] : reader.List(body, "lowersTo", (item, at) => reader.Scalar(item, at)),
      Facts = body["facts"] is null ? null : reader.Facts(body["facts"]),
      LaneLiftable = reader.Flag(body, "laneLiftable"),
      RequiresRuntime = reader.Flag(body, "requiresRuntime"),
      Algorithm = reader.Text(reference, "algorithm", null, "reference"),
      Source = reader.Text(reference, "source", null, "reference"),
      Vectors = body["vectors"] is null ? [] : reader.List(body, "vectors", (item, at) => reader.Vector(item, at))
    };
  }

  private static IntrinsicAlgebra Algebra(Reader reader, JsonNode? item, string at) => reader.Scalar(item, at) switch {
    "commutative" => IntrinsicAlgebra.Commutative,
    "associative" => IntrinsicAlgebra.Associative,
    "idempotent" => IntrinsicAlgebra.Idempotent,
    "involution" => IntrinsicAlgebra.Involution,
    "identity-element" => IntrinsicAlgebra.IdentityElement,
    "absorbing-element" => IntrinsicAlgebra.AbsorbingElement,
    var other => throw reader.Fail(at, $"'{other}' is no algebraic law")
  };

  [GeneratedRegex("^[A-Z][A-Za-z0-9]*$")]
  private static partial Regex NamePattern();

  [GeneratedRegex("^[A-Z][A-Za-z0-9]*(\\.[A-Z][A-Za-z0-9]*)*$")]
  private static partial Regex SpellingPattern();

  [GeneratedRegex("^[a-z][A-Za-z0-9]*$")]
  private static partial Regex ValueNamePattern();

  [GeneratedRegex("^([A-Z][A-Za-z0-9]*|TYPES\\.[A-Za-z0-9][A-Za-z0-9_-]*)(\\[\\])?$")]
  private static partial Regex TypePattern();

  /// <summary>Reads the properties of one document, failing with <c>PLY0251</c> at the first that is wrong.</summary>
  private sealed class Reader(string file) {

    public IntrinsicRegistryException Fail(string property, string finding)
      => new(IntrinsicDiagnostics.BadContract.At($"intrinsic contract '{file}': property '{property}': {finding}", file));

    public JsonObject Object(JsonNode? node, string property) => node as JsonObject ?? throw this.Fail(property, node is null ? "missing" : "not a mapping");

    public string Scalar(JsonNode? node, string property)
      => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw this.Fail(property, node is null ? "missing" : "not a string");

    public string Text(JsonObject owner, string property, Regex? pattern, string? at = null) {
      var where = at is null ? property : $"{at}.{property}";
      var text = this.Scalar(owner[property], where);
      return pattern is null ? text.Trim().Length > 0 ? text : throw this.Fail(where, "empty")
        : pattern.IsMatch(text) ? text : throw this.Fail(where, $"'{text}' does not match {pattern}");
    }

    public string Choice(JsonObject owner, string property, IEnumerable<string> choices, string? at = null) {
      var where = at is null ? property : $"{at}.{property}";
      var text = this.Scalar(owner[property], where);
      var list = choices.ToList();
      return list.Contains(text) ? text : throw this.Fail(at ?? property, $"{property} '{text}' is none of {string.Join(", ", list)}");
    }

    public bool Flag(JsonObject owner, string property)
      => owner[property] switch {
        null => false,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        _ => throw this.Fail(property, "not true or false")
      };

    public long Integer(JsonNode? node, string property)
      => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : throw this.Fail(property, node is null ? "missing" : "not an integer");

    public List<T> List<T>(JsonObject owner, string property, Func<JsonNode?, string, T> item)
      => owner[property] is JsonArray list ? [.. list.Select((node, index) => item(node, $"{property}[{index}]"))] : throw this.Fail(property, owner[property] is null ? "missing" : "not a list");

    public IntrinsicValue Value(JsonNode? node, string at, HashSet<string> declared) {
      var value = this.Object(node, at);
      var name = this.Text(value, "name", ValueNamePattern(), at);
      var type = this.Text(value, "type", TypePattern(), at);
      var isArray = type.EndsWith("[]", StringComparison.Ordinal);
      var element = isArray ? type[..^2] : type;
      if (!element.StartsWith("TYPES.", StringComparison.Ordinal) && !declared.Contains(element))
        throw this.Fail($"{at}.type", $"'{element}' is no type parameter of the intrinsic");
      return new(name, element, isArray);
    }

    public IntrinsicAttribute Attribute(JsonNode? node, string at) {
      var attribute = this.Object(node, at);
      var (low, high) = (this.Integer(attribute["low"], $"{at}.low"), this.Integer(attribute["high"], $"{at}.high"));
      return low <= high
        ? new(this.Text(attribute, "name", ValueNamePattern(), at), this.Text(attribute, "type", TypePattern(), at), low, high)
        : throw this.Fail(at, $"the range [{low}, {high}] is empty");
    }

    public IntrinsicFacts Facts(JsonNode? node) {
      var facts = this.Object(node, "facts");
      if (facts.Select(pair => pair.Key).FirstOrDefault(key => !_factKeys.Contains(key)) is { } unknown)
        throw this.Fail("facts", $"'{unknown}' is no fact");

      (long, long)? range = null;
      if (facts["resultRange"] is { } bounds) {
        if (bounds is not JsonArray { Count: 2 } pair)
          throw this.Fail("facts.resultRange", "not a pair [low, high]");
        range = (this.Integer(pair[0], "facts.resultRange"), this.Integer(pair[1], "facts.resultRange"));
      }

      return new(range, this.Bits(facts, "knownZeroBits"), this.Bits(facts, "knownOneBits"));
    }

    private ulong? Bits(JsonObject facts, string property)
      => facts[property] is null ? null
        : this.Scalar(facts[property], $"facts.{property}") is var text && text.StartsWith("0x", StringComparison.Ordinal)
          && ulong.TryParse(text[2..], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var bits)
          ? bits : throw this.Fail($"facts.{property}", "not a hexadecimal pattern of up to 64 bits");

    public IntrinsicVector Vector(JsonNode? node, string at) {
      var vector = this.Object(node, at);
      var types = this.Object(vector["types"], $"{at}.types").ToDictionary(pair => pair.Key, pair => this.Scalar(pair.Value, $"{at}.types.{pair.Key}"), StringComparer.Ordinal);
      IReadOnlyList<JsonNode?> Literals(string list) => vector[list] is JsonArray literals ? [.. literals] : throw this.Fail($"{at}.{list}", "not a list");
      return new(types, Literals("inputs"), Literals("outputs"));
    }

  }

}
