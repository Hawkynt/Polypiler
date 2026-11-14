using System.Collections.Generic;
using System.Text.Json.Nodes;
using Polypiler.Core;

namespace Polypiler.Tooling.Spec.Families;

/// <summary>
/// The worked example of <see cref="ISpecFamily"/>, the folder <c>demo</c>: a widget made of named
/// parts, each of which may name another spec document it uses. The repository's <c>spec/</c> holds no
/// <c>demo</c> documents; the fixtures of <c>tests/fixtures/spec/meta/</c> do. A real family is written
/// the same way: a body schema under <c>spec/schemas/</c>, the rules the schema cannot state in
/// <see cref="Check"/>, one line in <see cref="SpecFamilies.All"/>.
/// </summary>
public sealed class DemoSpecFamily : ISpecFamily {

  /// <inheritdoc />
  public string Folder => "demo";

  /// <inheritdoc />
  public string SchemaPath => "spec/schemas/demo.schema.json";

  /// <summary>
  /// Rule <c>unique-part</c>: no two parts share a name (<c>PLY0046</c> at the later one). Every
  /// <c>uses</c> names a spec document (<c>PLY0043</c>).
  /// </summary>
  /// <param name="document">The document, whose body conforms to the schema.</param>
  /// <param name="tree">The tree the references are resolved in.</param>
  /// <returns>What is wrong.</returns>
  public IEnumerable<Diagnostic> Check(SpecDocument document, SpecTree tree) {
    var parts = (JsonArray)document.Body!["parts"]!;
    var names = new HashSet<string>(System.StringComparer.Ordinal);
    for (var index = 0; index < parts.Count; ++index) {
      var part = parts[index]!;
      var name = (string)part["name"]!;
      if (!names.Add(name))
        yield return SpecDiagnostics.Rule(document, $"/spec/parts/{index}/name", $"part '{name}' is defined twice", "unique-part");

      if (part["uses"] is { } uses && tree.Resolve(document, $"/spec/parts/{index}/uses", (string)uses!) is { } broken)
        yield return broken;
    }
  }

}
