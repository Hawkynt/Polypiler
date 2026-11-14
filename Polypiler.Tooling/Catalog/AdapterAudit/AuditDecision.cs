namespace Polypiler.Tooling.Catalog.AdapterAudit;

/// <summary>What an adapter may do with an audited file or component.</summary>
public enum AuditDecision {

  /// <summary>It may be compiled, linked or loaded.</summary>
  Usable,

  /// <summary>It may be read for its semantics, never compiled, linked or loaded.</summary>
  ReferenceOnly,

  /// <summary>It may not be used at all.</summary>
  Blocked

}

/// <summary>The texts of <see cref="AuditDecision"/> in the audit file and in diagnostics.</summary>
public static class AuditDecisions {

  /// <summary>The text of a decision: <c>usable</c>, <c>reference-only</c> or <c>blocked</c>.</summary>
  public static string Name(AuditDecision decision) => decision switch {
    AuditDecision.Usable => "usable",
    AuditDecision.ReferenceOnly => "reference-only",
    _ => "blocked"
  };

  /// <summary>The decision of one of the three texts the schema admits; any other text is <see cref="AuditDecision.Blocked"/>.</summary>
  public static AuditDecision Parse(string text) => text switch {
    "usable" => AuditDecision.Usable,
    "reference-only" => AuditDecision.ReferenceOnly,
    _ => AuditDecision.Blocked
  };

}
