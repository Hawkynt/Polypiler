using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using Polypiler.Core;

namespace Polypiler.Tooling.Spec.Families;

/// <summary>
/// The family <c>spec/verification/</c> (PRD §4.1, §7.2, §23): the input generators, the tolerance of
/// each operation compared under one (ADR 0005) and the reference oracles, each in its own folder
/// <c>generators</c>, <c>tolerances</c> or <c>oracles</c>. The schema
/// <c>spec/schemas/verification.schema.json</c> states the shapes; this class the rules across fields and
/// files. A value of a generator is a bit pattern <c>0x…</c> or a decimal integer; an operation without a
/// tolerance document is compared <c>exact</c>.
/// </summary>
public sealed class VerificationSpecFamily : ISpecFamily {

  /// <summary>The <c>implementation</c> an oracle may not name: the compiler cannot be the reference for its own output (PRD §23.1).</summary>
  public const string Self = "polypiler";

  /// <inheritdoc />
  public string Folder => "verification";

  /// <inheritdoc />
  public string SchemaPath => "spec/schemas/verification.schema.json";

  /// <summary>
  /// The rules, each <c>PLY0046</c>: <c>layout</c> for every document; <c>strategy-requirements</c>,
  /// <c>value-fits-type</c> and <c>boundary-covers-extremes</c> for a generator; <c>bound-for-mode</c> and
  /// <c>one-tolerance-per-operation</c> for a tolerance; <c>oracle-not-self</c> for an oracle. The
  /// <c>type</c> of a generator and the <c>operation</c> of a tolerance resolve to a spec document
  /// (<c>PLY0043</c>).
  /// </summary>
  /// <param name="document">The document, whose body conforms to the schema.</param>
  /// <param name="tree">The tree the references and the other tolerance documents are found in.</param>
  /// <returns>What is wrong.</returns>
  public IEnumerable<Diagnostic> Check(SpecDocument document, SpecTree tree) {
    ArgumentNullException.ThrowIfNull(document);
    ArgumentNullException.ThrowIfNull(tree);
    var body = (JsonObject)document.Body!;
    var kind = (string)body["kind"]!;
    var problems = new List<Diagnostic>();
    if (Layout(document, kind) is { } misplaced)
      problems.Add(misplaced);

    switch (kind) {
      case "generator":
        problems.AddRange(CheckGenerator(document, body, tree));
        break;
      case "tolerance":
        problems.AddRange(CheckTolerance(document, body, tree));
        break;
      default:
        var implementation = (string)body["implementation"]!;
        if (string.Equals(implementation, Self, StringComparison.OrdinalIgnoreCase))
          problems.Add(SpecDiagnostics.Rule(document, "/spec/implementation", $"an oracle must be independent of Polypiler, found implementation '{implementation}'", "oracle-not-self"));
        break;
    }

    return problems;
  }

  /// <summary>Rule <c>layout</c>: <c>verification/&lt;kind&gt;s/&lt;name&gt;.yaml</c> with the ID <c>VERIFICATION.&lt;kind&gt;.&lt;name&gt;</c>.</summary>
  private static Diagnostic? Layout(SpecDocument document, string kind) {
    var folder = $"verification/{kind}s";
    var slash = document.Path.LastIndexOf('/');
    var found = document.Path[..slash];
    if (found != folder)
      return SpecDiagnostics.Rule(document, "/spec/kind", $"kind '{kind}' must live in spec/{folder}, found spec/{found}", "layout");

    var expected = $"VERIFICATION.{kind}.{document.Path[(slash + 1)..^".yaml".Length]}";
    return document.Meta!.Id == expected
      ? null
      : SpecDiagnostics.Rule(document, "/id", $"id '{document.Meta.Id}' must be '{expected}' in spec/{document.Path}", "layout");
  }

  private static IEnumerable<Diagnostic> CheckGenerator(SpecDocument document, JsonObject body, SpecTree tree) {
    var strategy = (string)body["strategy"]!;
    var random = strategy is "uniform" or "bit-pattern";
    var problems = new List<Diagnostic>();
    foreach (var (property, required) in new[] { ("values", strategy == "boundary"), ("seed", random), ("count", random) })
      if (required && !body.ContainsKey(property))
        problems.Add(SpecDiagnostics.Rule(document, "/spec/strategy", $"strategy '{strategy}' requires '{property}'", "strategy-requirements"));
      else if (!required && body.ContainsKey(property))
        problems.Add(SpecDiagnostics.Rule(document, $"/spec/{property}", $"strategy '{strategy}' does not take '{property}'", "strategy-requirements"));

    var typeId = (string)body["type"]!;
    if (tree.Resolve(document, "/spec/type", typeId) is { } broken) {
      problems.Add(broken);
      return problems;
    }

    if (body["values"] is JsonArray values && IntegerType.Of(tree.ById[typeId]) is { } type)
      problems.AddRange(CheckValues(document, typeId, type, values, strategy == "boundary"));
    return problems;
  }

  /// <summary>Rule <c>value-fits-type</c> for every value, then, for a boundary generator whose values all fit, <c>boundary-covers-extremes</c>.</summary>
  private static IEnumerable<Diagnostic> CheckValues(SpecDocument document, string typeId, IntegerType type, JsonArray values, bool boundary) {
    var problems = new List<Diagnostic>();
    var seen = new HashSet<BigInteger>();
    for (var index = 0; index < values.Count; ++index) {
      var literal = (string)values[index]!;
      if (type.Value(literal) is { } value)
        seen.Add(value);
      else
        problems.Add(SpecDiagnostics.Rule(document, $"/spec/values/{index}", $"values[{index}] {literal} does not fit {typeId} ({type.Bits} bits)", "value-fits-type"));
    }

    if (problems.Count > 0 || !boundary || !type.Integer)
      return problems;

    foreach (var (name, extreme) in new[] { ("minimum", type.Minimum), ("maximum", type.Maximum) })
      if (!seen.Contains(extreme))
        problems.Add(SpecDiagnostics.Rule(document, "/spec/values", $"boundary generator for {typeId} lacks the {name} {type.Pattern(extreme)}", "boundary-covers-extremes"));
    return problems;
  }

  private static IEnumerable<Diagnostic> CheckTolerance(SpecDocument document, JsonObject body, SpecTree tree) {
    var mode = (string)body["mode"]!;
    var bounded = body.ContainsKey("bound");
    if (mode == "exact" && bounded)
      yield return SpecDiagnostics.Rule(document, "/spec/bound", "mode 'exact' takes no 'bound'", "bound-for-mode");
    else if (mode != "exact" && !bounded)
      yield return SpecDiagnostics.Rule(document, "/spec/mode", $"mode '{mode}' requires 'bound'", "bound-for-mode");

    var operation = (string)body["operation"]!;
    if (tree.Resolve(document, "/spec/operation", operation) is { } broken)
      yield return broken;

    // The document is itself a tolerance of the operation, so the search ends at it at the latest.
    var first = tree.Documents.First(other => ReferenceEquals(other, document) || IsToleranceOf(other, operation));
    if (!ReferenceEquals(first, document))
      yield return SpecDiagnostics.Rule(document, "/spec/operation", $"operation {operation} already has tolerance {first.Id}", "one-tolerance-per-operation");
  }

  /// <summary>Whether a document of the tree is a tolerance document for the operation; the first one in path order keeps it.</summary>
  private static bool IsToleranceOf(SpecDocument document, string operation)
    => document.Family == "verification"
      && document.Body is JsonObject body
      && body["kind"] is JsonValue kind && kind.TryGetValue<string>(out var text) && text == "tolerance"
      && body["operation"] is JsonValue named && named.TryGetValue<string>(out var other) && other == operation;

  /// <summary>
  /// The width and range of the type a generator names, as far as its document states them: <c>bits</c>
  /// and <c>signedness</c>, and whether it is of the <c>integer</c> family. A bit pattern fits any type
  /// with that many bits; a decimal value only an integer type's range.
  /// </summary>
  private sealed record IntegerType(int Bits, bool Signed, bool Integer) {

    public static IntegerType? Of(SpecDocument type) {
      if (type.Body is not JsonObject body || body["bits"] is not JsonValue bits || !bits.TryGetValue<long>(out var width) || width < 1)
        return null;

      var signed = body["signedness"] is JsonValue signedness && signedness.TryGetValue<string>(out var text) && text == "signed";
      var integer = body["family"] is JsonValue family && family.TryGetValue<string>(out var name) && name == "integer";
      return new((int)width, signed, integer);
    }

    public BigInteger Minimum => this.Signed ? -(BigInteger.One << (this.Bits - 1)) : BigInteger.Zero;

    public BigInteger Maximum => (BigInteger.One << (this.Signed ? this.Bits - 1 : this.Bits)) - 1;

    /// <summary>The value of a literal, a bit pattern read in two's complement for a signed type; <see langword="null"/> when it does not fit.</summary>
    public BigInteger? Value(string literal) {
      if (literal.StartsWith("0x", StringComparison.Ordinal)) {
        var pattern = BigInteger.Parse("0" + literal[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        if (pattern.GetBitLength() > this.Bits)
          return null;
        return this.Signed && pattern > this.Maximum ? pattern - (BigInteger.One << this.Bits) : pattern;
      }

      var value = BigInteger.Parse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
      return !this.Integer || (value >= this.Minimum && value <= this.Maximum) ? value : null;
    }

    /// <summary>A value as the bit pattern of the type's width: <c>0xFFFFFFFF</c>, <c>0x80</c>.</summary>
    public string Pattern(BigInteger value) {
      var digits = (this.Bits + 3) / 4;
      var pattern = value < 0 ? value + (BigInteger.One << this.Bits) : value;
      return "0x" + pattern.ToString("X" + (digits + 1), CultureInfo.InvariantCulture)[^digits..];
    }

  }

}
