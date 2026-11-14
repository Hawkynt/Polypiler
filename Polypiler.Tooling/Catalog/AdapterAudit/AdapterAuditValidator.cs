using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tooling.Catalog.AdapterAudit;

/// <summary>
/// Holds an adapter licence audit against the imported corpus and the licence policy. It is read-only
/// and complete: every violation is reported, not only the first. Without touched paths it checks the
/// audit itself; with them it also requires each to have a usable entry, a restricted asset to be
/// cleared, and the clearance not to be refused.
/// </summary>
/// <param name="policy">The licence policy: its overrides take part in detection, its restricted assets decide which records are models.</param>
/// <param name="texts">The reference licence texts the detector identifies licence files by.</param>
public sealed class AdapterAuditValidator(LicencePolicy policy, LicenceTexts texts) {

  /// <summary>Every violation of an audit against the corpus below a repository root.</summary>
  /// <param name="audit">The audit.</param>
  /// <param name="root">The repository root: <c>corpus/upstream</c> is below it.</param>
  /// <param name="lockFile">The corpus lock.</param>
  /// <param name="touches">The paths and component names an adapter is about to compile, link or load; empty to check the audit alone.</param>
  /// <returns>The diagnostics, in the order the records, the components and the touches come.</returns>
  public IReadOnlyList<Diagnostic> Validate(AdapterAudit audit, string root, CorpusLock lockFile, IReadOnlyList<string> touches) {
    var problems = new List<Diagnostic>();
    var detector = new LicenceDetector(policy, texts);
    var paths = audit.Records.Select(record => record.Path).ToHashSet(StringComparer.Ordinal);
    var seen = new HashSet<string>(StringComparer.Ordinal);
    string? previous = null;
    foreach (var record in audit.Records) {
      if (!seen.Add(record.Path))
        problems.Add(AdapterAuditDiagnostics.Rule($"two records for '{record.Path}'"));
      else if (previous is not null && string.CompareOrdinal(previous, record.Path) > 0)
        problems.Add(AdapterAuditDiagnostics.Rule($"records are not sorted by path: '{record.Path}' follows '{previous}'"));
      previous = record.Path;
      this.CheckRecord(audit, record, root, lockFile, paths, detector, problems);
    }

    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var component in audit.External) {
      if (!names.Add(component.Name))
        problems.Add(AdapterAuditDiagnostics.Rule($"two components named '{component.Name}'"));
      Licence($"component '{component.Name}'", component.Licence, problems);
      if (component.Decision == AuditDecision.Blocked && !component.Obligations.Any(AuditRecord.RestrictingObligations.Contains))
        problems.Add(AdapterAuditDiagnostics.Rule($"component '{component.Name}' is blocked without a no-redistribution or research-only obligation"));
      CheckNotice(audit, $"component '{component.Name}'", component.Notice, root, lockFile, paths, problems);
    }

    if (touches.Count > 0)
      Touch(audit, touches, problems);
    return problems;
  }

  private void CheckRecord(AdapterAudit audit, AuditRecord record, string root, CorpusLock lockFile, HashSet<string> paths, LicenceDetector detector, List<Diagnostic> problems) {
    var name = $"record '{record.Path}'";
    var expression = Licence(name, record.Licence, problems);
    if (record.Origin is { } origin) {
      Licence($"the origin of {name}", origin.Licence, problems);
      CheckNotice(audit, name, origin.Notice, root, lockFile, paths, problems);
    }

    if (record.Decision != AuditDecision.Usable && record.Reason is null)
      problems.Add(AdapterAuditDiagnostics.Rule($"{name} is {AuditDecisions.Name(record.Decision)} without a reason"));
    if (record.Decision == AuditDecision.Blocked && !record.HasRestrictingObligation)
      problems.Add(AdapterAuditDiagnostics.Rule($"{name} is blocked without a no-redistribution or research-only obligation"));

    var location = $"{audit.Repository}@{audit.Commit}/{record.Path}";
    var restricted = policy.IsRestricted(location);
    if (restricted && !record.IsModel)
      problems.Add(AdapterAuditDiagnostics.Rule($"{name} is a restricted asset of the licence policy, so its kind is model"));
    else if (!restricted && record.IsModel)
      problems.Add(AdapterAuditDiagnostics.Rule($"{name} is of kind model, but the licence policy does not restrict it"));

    if (lockFile.Find(audit.Repository, audit.Commit, record.Path) is not { } locked) {
      problems.Add(Missing(audit, record.Path, "it is not in the corpus lock"));
      return;
    }

    var file = CorpusImporter.FilePath(root, audit.Repository, audit.Commit, record.Path);
    byte[] content;
    try {
      content = File.ReadAllBytes(file);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      problems.Add(Missing(audit, record.Path, $"it is not imported; run 'polypiler-tooling corpus import' ({exception.Message})"));
      return;
    }

    var sha256 = CorpusLockEntry.Hash(content);
    if (CorpusVerifier.Compare(locked, sha256, content.LongLength) is { } mismatch)
      problems.Add(mismatch);
    if (sha256 != record.Sha256 || content.LongLength != record.Bytes)
      problems.Add(AdapterAuditDiagnostics.Create($"licence audit record '{record.Path}' is stale: SHA-256 {sha256}, recorded {record.Sha256}"
        + (content.LongLength == record.Bytes ? "" : $" ({content.LongLength} bytes, recorded {record.Bytes} bytes)")));

    if (expression is not null && Contradiction(record, expression, detector.Detect(CorpusImporter.FilePath(root, audit.Repository, audit.Commit, "."), file, location, record.Licence)) is { } contradiction)
      problems.Add(AdapterAuditDiagnostics.Rule($"{name} says '{record.Licence}', {contradiction}"));
  }

  /// <summary>
  /// How the detected licence contradicts the record's first operand, or <see langword="null"/>. A header
  /// of the file or an override of the policy is evidence about the file itself: it contradicts when it
  /// does not name that operand. The nearest licence file is the repository's default: it contradicts
  /// only the repository's own file, since a notice the record cites says more about a converted or
  /// copied file than the repository licence does.
  /// </summary>
  private static string? Contradiction(AuditRecord record, SpdxExpression expression, LicenceDetection detection) {
    var first = expression.Licences.First();
    return detection.Source switch {
      LicenceSource.Header or LicenceSource.Override when detection.Expression is { } detected && !detected.Licences.Contains(first)
        => $"the licence scanner detects '{detection.Licence}' from its {LicenceReport.Name(detection.Source.Value)}",
      LicenceSource.LicenceFile when detection.Disagreement is not null && record.Origin is null
        => $"the licence scanner detects '{detection.Licence}' from the repository's licence file, and the record names no origin whose notice says otherwise",
      _ => null
    };
  }

  /// <summary>The expression of a licence, or <see langword="null"/> after reporting that it does not parse.</summary>
  private static SpdxExpression? Licence(string name, string licence, List<Diagnostic> problems) {
    try {
      return SpdxExpression.Parse(licence);
    } catch (FormatException exception) {
      problems.Add(AdapterAuditDiagnostics.Rule($"{name} has licence '{licence}', which is not an SPDX expression: {exception.Message}"));
      return null;
    }
  }

  /// <summary>Reports a notice that is neither a record nor an imported corpus file of the pin.</summary>
  private static void CheckNotice(AdapterAudit audit, string name, string notice, string root, CorpusLock lockFile, HashSet<string> paths, List<Diagnostic> problems) {
    if (paths.Contains(notice))
      return;
    if (lockFile.Find(audit.Repository, audit.Commit, notice) is null)
      problems.Add(Missing(audit, notice, $"the notice of {name} is not in the corpus lock"));
    else if (!File.Exists(CorpusImporter.FilePath(root, audit.Repository, audit.Commit, notice)))
      problems.Add(Missing(audit, notice, $"the notice of {name} is not imported; run 'polypiler-tooling corpus import'"));
  }

  private static Diagnostic Missing(AdapterAudit audit, string path, string cause)
    => CorpusDiagnostics.Path($"'{path}' does not exist in {audit.Repository} at {audit.Commit}", cause);

  private static void Touch(AdapterAudit audit, IReadOnlyList<string> touches, List<Diagnostic> problems) {
    var clearance = audit.Clearance;
    if (clearance.Status == AuditClearance.Refused)
      problems.Add(AdapterAuditDiagnostics.Create($"licence clearance was refused ({clearance.Adr})"));

    foreach (var touch in touches) {
      var record = audit.Record(touch);
      var component = record is null ? audit.External.FirstOrDefault(entry => entry.Name == touch) : null;
      if (record is null && component is null) {
        problems.Add(AdapterAuditDiagnostics.Create($"'{touch}' has no licence audit record"));
        continue;
      }

      var (decision, reason) = record is not null ? (record.Decision, record.Reason) : (component!.Decision, component.Reason);
      if (decision != AuditDecision.Usable)
        problems.Add(AdapterAuditDiagnostics.Create($"'{touch}' is {AuditDecisions.Name(decision)} in the licence audit: {reason}"));
      else if (record is { IsModel: true } && clearance.Status == AuditClearance.Pending)
        problems.Add(AdapterAuditDiagnostics.Create($"'{touch}' is a restricted asset and its licence clearance is pending"));
    }
  }

}
