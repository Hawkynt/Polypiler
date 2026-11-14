using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Tooling.Catalog.AdapterAudit;

/// <summary>What an audited file or component was converted or copied from.</summary>
/// <param name="Project">The upstream project, as its notice names it.</param>
/// <param name="Url">Where the project lives.</param>
/// <param name="Licence">The project's licence as an SPDX expression.</param>
/// <param name="Notice">The path, at the pinned commit, of the notice that says what was taken.</param>
public sealed record AuditOrigin(string Project, string Url, string Licence, string Notice);

/// <summary>One audited file of the pinned commit.</summary>
/// <param name="Path">The file, relative to the repository root.</param>
/// <param name="IsModel">Whether the file is a model (weights or a network description), a restricted asset of the policy.</param>
/// <param name="Sha256">The SHA-256 of the file at the pin, 64 lower-case hexadecimal digits.</param>
/// <param name="Bytes">The file's length at the pin.</param>
/// <param name="Licence">The file's licence as an SPDX expression.</param>
/// <param name="Origin">What the file was converted or copied from, or <see langword="null"/> for the repository's own file.</param>
/// <param name="Obligations">What using the file obliges to: <c>notice</c>, <c>attribution</c>, <c>source-offer</c>, <c>no-redistribution</c>, <c>research-only</c>.</param>
/// <param name="Decision">What the adapter may do with it.</param>
/// <param name="UsedBy">The backlog IDs of the issues that touch the file.</param>
/// <param name="Reviewer">Who read the file's header and the notices the record rests on.</param>
/// <param name="ReviewedAt">When, as <c>YYYY-MM-DD</c>.</param>
/// <param name="Reason">Why the decision is what it is; <see langword="null"/> only for a usable file.</param>
public sealed record AuditRecord(
  string Path,
  bool IsModel,
  string Sha256,
  long Bytes,
  string Licence,
  AuditOrigin? Origin,
  IReadOnlyList<string> Obligations,
  AuditDecision Decision,
  IReadOnlyList<string> UsedBy,
  string Reviewer,
  string ReviewedAt,
  string? Reason
) {

  /// <summary>The obligations of which one makes a <see cref="AuditDecision.Blocked"/> decision follow from the terms.</summary>
  public static IReadOnlyList<string> RestrictingObligations { get; } = ["no-redistribution", "research-only"];

  /// <summary>Whether an obligation restricts use: <c>no-redistribution</c> or <c>research-only</c>.</summary>
  public bool HasRestrictingObligation => this.Obligations.Any(RestrictingObligations.Contains);

}

/// <summary>A component the adapter needs that is not a file of the pinned commit: a provider or weights fetched at build or run time.</summary>
/// <param name="Name">The component's name in the audit, what <c>--touch</c> names it by.</param>
/// <param name="Kind"><c>provider</c> or <c>weights</c>.</param>
/// <param name="Licence">Its licence as an SPDX expression.</param>
/// <param name="Url">Where it comes from.</param>
/// <param name="Notice">The path, at the pinned commit, of the notice that records its terms.</param>
/// <param name="Obligations">What using it obliges to.</param>
/// <param name="Decision">What the adapter may do with it.</param>
/// <param name="Reason">Why the decision is what it is.</param>
/// <param name="Reviewer">Who read the notice.</param>
/// <param name="ReviewedAt">When, as <c>YYYY-MM-DD</c>.</param>
public sealed record AuditExternal(
  string Name,
  string Kind,
  string Licence,
  string Url,
  string Notice,
  IReadOnlyList<string> Obligations,
  AuditDecision Decision,
  string Reason,
  string Reviewer,
  string ReviewedAt
);

/// <summary>The owner's clearance of the audit's usable entries.</summary>
/// <param name="Status"><c>pending</c>, <c>granted</c> or <c>refused</c>.</param>
/// <param name="Adr">The ADR that records the decision, or <see langword="null"/> while pending.</param>
/// <param name="Date">The date of the decision, or <see langword="null"/> while pending.</param>
public sealed record AuditClearance(string Status, string? Adr, string? Date) {

  /// <summary>The status of a clearance not decided yet.</summary>
  public const string Pending = "pending";

  /// <summary>The status of a clearance the owner granted.</summary>
  public const string Granted = "granted";

  /// <summary>The status of a clearance the owner refused.</summary>
  public const string Refused = "refused";

}
