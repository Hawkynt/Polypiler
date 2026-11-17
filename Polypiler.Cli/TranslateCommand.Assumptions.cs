using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Polypiler.Core.Analysis.Dynamic;

namespace Polypiler.Cli;

/// <summary>The <c>assumptions</c> section of the manifest of a translation (#777, PRD §10.2).</summary>
public sealed partial class TranslateCommand {

  /// <summary>
  /// Every caller contract the enforcer accepted, with whether a check or a proof stands behind it,
  /// then every value fact of <c>--assume</c>, which nothing checks; <see langword="null"/> when there
  /// is none, as the schema has the section only with an entry.
  /// </summary>
  private static JsonArray? AssumptionsOf(GlobalOptions pipeline, IReadOnlyList<ContractRecord> contracts) {
    var entries = contracts.Select(contract => Entry(contract.Target, contract.Contract, contract.Checked, contract.Origin, contract.UsedBy))
      .Concat(pipeline.Assumptions.Select(fact => Entry(fact.Subject, fact.Text[(fact.Subject.Length + 1)..], false, GlobalOptionSet.Assume, 0)))
      .ToArray<JsonNode?>();
    return entries.Length == 0 ? null : new JsonArray(entries);
  }

  private static JsonObject Entry(string target, string contract, bool isChecked, string origin, int usedBy) => new() {
    ["target"] = target,
    ["contract"] = contract,
    ["checked"] = isChecked,
    ["origin"] = origin,
    ["usedBy"] = usedBy
  };

}
