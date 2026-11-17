using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Polypiler.Core;
using Polypiler.Core.MachineSemantics;

namespace Polypiler.Tooling.Spec.Families;

/// <summary>
/// The family <c>machine</c>: one machine profile per instruction set, <c>spec/machine/&lt;isa&gt;.yaml</c>
/// with the ID <c>MACHINE.&lt;isa&gt;</c>. The schema <c>spec/schemas/isa-profile.schema.json</c> states
/// the shape; the profile's own rules (<see cref="IsaProfile.Validate"/>, <c>PLY4003</c> of the loader)
/// are reported here as <c>PLY0046</c> with the rule <c>profile</c>, so that the spec tooling and the
/// loader never disagree about a profile.
/// </summary>
public sealed class MachineSpecFamily : ISpecFamily {

  /// <inheritdoc />
  public string Folder => "machine";

  /// <inheritdoc />
  public string SchemaPath => "spec/schemas/isa-profile.schema.json";

  /// <summary>
  /// The rules: <c>layout</c> (the file lies directly in <c>machine/</c>, its ID is
  /// <c>MACHINE.&lt;file name&gt;</c> and its <c>isa</c> the file name) and <c>profile</c> (the profile
  /// loads: unique names, declared parents, sub-registers inside their parents, no cycle of parents,
  /// flags inside declared registers).
  /// </summary>
  /// <param name="document">The document, whose body conforms to the schema.</param>
  /// <param name="tree">The tree; unused, no rule looks across files.</param>
  /// <returns>What is wrong.</returns>
  public IEnumerable<Diagnostic> Check(SpecDocument document, SpecTree tree) {
    ArgumentNullException.ThrowIfNull(document);
    var body = document.Body!;
    var name = Path.GetFileNameWithoutExtension(document.Path);
    var isa = body["isa"]!.GetValue<string>();
    if (Path.GetFileName(Path.GetDirectoryName(document.Path)) != this.Folder || document.Meta!.Id != "MACHINE." + name || isa != name)
      yield return SpecDiagnostics.Rule(document, "/spec/isa", $"a machine profile is spec/machine/<isa>.yaml with the ID MACHINE.<isa>; '{document.Meta!.Id}' with isa '{isa}' is not", "layout");

    var problem = Load(body);
    if (problem is not null)
      yield return SpecDiagnostics.Rule(document, "/spec", problem, "profile");
  }

  private static string? Load(JsonNode body) {
    try {
      ProfileLoader.Parse(body.ToJsonString());
      return null;
    } catch (MachineException exception) {
      return exception.Diagnostic.Message;
    }
  }

}
