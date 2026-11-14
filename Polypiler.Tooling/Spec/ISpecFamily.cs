using System.Collections.Generic;
using Polypiler.Core;

namespace Polypiler.Tooling.Spec;

/// <summary>
/// One family of spec documents, the folder <c>spec/&lt;Folder&gt;/</c>: the extension point of
/// <see cref="SpecValidator"/>. The engine reads the files, applies the metadata schema and rules, then
/// the family's body schema at <see cref="SchemaPath"/>, and only then calls <see cref="Check"/> — so a
/// family sees only documents whose metadata is valid (<see cref="SpecDocument.Meta"/> is set) and whose
/// body conforms to its schema. A family is added to <see cref="SpecFamilies.All"/>;
/// <c>Families/DemoSpecFamily.cs</c> is the worked example.
/// </summary>
public interface ISpecFamily {

  /// <summary>The folder under the spec root, in lower case: <c>ir</c>. The ID prefix of its documents is the folder in upper case: <c>IR.</c>.</summary>
  string Folder { get; }

  /// <summary>The JSON Schema 2020-12 file of the body under <c>spec</c>, relative to the repository root: <c>spec/schemas/ir.schema.json</c>.</summary>
  string SchemaPath { get; }

  /// <summary>
  /// The rules the schema cannot state, for one document: cross-field rules as
  /// <see cref="SpecDiagnostics.Rule"/> (<c>PLY0046</c>), references to other documents through
  /// <see cref="SpecTree.Resolve"/> (<c>PLY0043</c>).
  /// </summary>
  /// <param name="document">The document, with valid metadata and a body that conforms to the schema.</param>
  /// <param name="tree">Every document under the spec root, for the rules that look across files.</param>
  /// <returns>What is wrong; empty when nothing is.</returns>
  IEnumerable<Diagnostic> Check(SpecDocument document, SpecTree tree);

}
