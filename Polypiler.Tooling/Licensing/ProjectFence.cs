using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Polypiler.Core;

namespace Polypiler.Tooling.Licensing;

/// <summary>
/// The fence around the compiler projects: every folder at the repository root whose name starts with
/// <c>Polypiler.</c> is LGPL-3.0-or-later and must stay so, so no GPL-derived code may enter one (owner
/// decision of 2026-11-13; a GPL source stays rung 2 of AGENTS.md there). A file of such a folder,
/// <c>bin</c>, <c>obj</c> and <c>.git</c> excluded, fails when
/// <list type="bullet">
/// <item>its <c>SPDX-License-Identifier</c> header is of strong-copyleft class, reference-only or denied;</item>
/// <item>it is a licence file (<see cref="LicenceDetector.LicenceFileNames"/>) whose text is of such a
/// licence — reported once, for the licence file, rather than for every file below it; a text shared by
/// several identifiers fails only when none of them would pass, and a text that is no known licence is
/// left to <c>licenses inventory</c>;</item>
/// <item>it is a provenance notice (<see cref="ProvenanceNotice"/>) one of whose entries names such a
/// licence, or lacks its <c>Origin:</c> or <c>Licence:</c> line (<c>PLY0029</c>).</item>
/// </list>
/// Weak-copyleft and permissive origins pass.
/// </summary>
/// <param name="policy">The policy.</param>
/// <param name="texts">The reference licence texts.</param>
public sealed class ProjectFence(LicencePolicy policy, LicenceTexts texts) {

  /// <summary>The prefix of the folder names the fence surrounds.</summary>
  public const string ProjectPrefix = "Polypiler.";

  private static readonly string[] _skipped = ["bin", "obj", ".git"];

  /// <summary>Scans every compiler project below a repository root.</summary>
  /// <param name="root">The repository root.</param>
  /// <returns>One entry per file, with the licence it states where it states one, and every violation.</returns>
  public LicenceReport Scan(string root) {
    var top = Path.GetFullPath(root);
    var entries = new List<LicenceReportEntry>();
    var diagnostics = new List<Diagnostic>();
    var projects = Directory.EnumerateDirectories(top)
      .Where(folder => Path.GetFileName(folder).StartsWith(ProjectPrefix, StringComparison.Ordinal))
      .Order(StringComparer.Ordinal);
    foreach (var project in projects) {
      var files = Directory.EnumerateFiles(project, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
        .Where(file => !Path.GetRelativePath(project, file).Split(Path.DirectorySeparatorChar).SkipLast(1).Any(_skipped.Contains))
        .Order(StringComparer.Ordinal);
      foreach (var file in files)
        entries.Add(this.Check(file, Path.GetRelativePath(top, file).Replace(Path.DirectorySeparatorChar, '/'), diagnostics));
    }

    return new(entries, diagnostics);
  }

  private LicenceReportEntry Check(string file, string path, List<Diagnostic> diagnostics) {
    var name = Path.GetFileName(file);
    var whole = ProvenanceNotice.IsNotice(name) || LicenceDetector.IsLicenceFile(name);
    string text;
    try {
      text = whole ? File.ReadAllText(file) : ReadHead(file);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      diagnostics.Add(LicenceDiagnostics.Unknown(path, $"the file cannot be read: {exception.Message}"));
      return new(path, null, null, LicenceStatus.Denied);
    }

    if (ProvenanceNotice.IsNotice(name))
      return this.Notice(path, name, text, diagnostics);
    if (LicenceDetector.IsLicenceFile(name))
      return this.LicenceFile(path, text, diagnostics);
    if (LicenceDetector.Header(text) is not { } header)
      return new(path, null, null, LicenceStatus.Allowed);
    return new(path, header, LicenceSource.Header, this.Admit(path, header, path, diagnostics) ? LicenceStatus.Allowed : LicenceStatus.Denied);
  }

  private LicenceReportEntry Notice(string path, string name, string text, List<Diagnostic> diagnostics) {
    var admitted = true;
    var licences = new List<string>();
    foreach (var entry in ProvenanceNotice.Parse(name, text)) {
      if (!entry.IsComplete) {
        diagnostics.Add(Diagnostic.Create(LicenceDiagnostics.UndeterminedDescriptor, null,
          $"'{path}': provenance entry lacks '{ProvenanceNotice.OriginKey}' or '{ProvenanceNotice.LicenceKey}' (line {entry.Line})"));
        admitted = false;
        continue;
      }

      foreach (var licence in entry.Licences) {
        licences.Add(licence.Contains(' ') ? $"({licence})" : licence);
        admitted &= this.Admit(path, licence, entry.Origins[0], diagnostics);
      }
    }

    return new(path, licences.Count == 0 ? null : string.Join(" AND ", licences.Distinct()), null, admitted ? LicenceStatus.Allowed : LicenceStatus.Denied);
  }

  private LicenceReportEntry LicenceFile(string path, string text, List<Diagnostic> diagnostics) {
    if (texts.Identify(text) is not { } candidates)
      return new(path, null, LicenceSource.LicenceFile, LicenceStatus.Allowed);

    var family = LicenceTexts.Family(candidates);
    if (candidates.Any(candidate => Passes(EffectiveLicence.ClassOf(candidate, policy))))
      return new(path, family, LicenceSource.LicenceFile, LicenceStatus.Allowed);

    var folder = path[..(path.LastIndexOf('/') + 1)];
    diagnostics.Add(Refusal(path, candidates[0], folder));
    return new(path, family, LicenceSource.LicenceFile, LicenceStatus.Denied);
  }

  /// <summary>Whether a licence may stand in a compiler project; reports why not when it may not.</summary>
  private bool Admit(string path, string licence, string origin, List<Diagnostic> diagnostics) {
    try {
      SpdxExpression.Parse(licence);
    } catch (FormatException exception) {
      diagnostics.Add(LicenceDiagnostics.Unknown(path, $"'{licence}' is not an SPDX expression: {exception.Message}"));
      return false;
    }

    if (Passes(EffectiveLicence.ClassOf(licence, policy)))
      return true;

    diagnostics.Add(Refusal(path, licence, origin));
    return false;
  }

  private static bool Passes(LicenceClass? licenceClass) => licenceClass is LicenceClass.Permissive or LicenceClass.WeakCopyleft;

  /// <summary>The refusal of a licence: the GPL fence for anything of the GPL family, the denial of the policy for anything else.</summary>
  private static Diagnostic Refusal(string path, string licence, string origin) {
    var gpl = SpdxExpression.Parse(licence).Licences.Any(identifier => LicenceClasses.Family(identifier) == LicenceClass.StrongCopyleft);
    return LicenceDiagnostics.Refused(gpl
      ? $"'{path}': GPL-derived code may not be merged into the LGPL-licensed compiler projects; keep '{origin}' as a rung 2 reference"
      : $"'{path}': licence '{licence}' is denied in {LicenceReport.Name(LicenceMode.Project)} mode");
  }

  private static string ReadHead(string file) {
    using var stream = File.OpenRead(file);
    var buffer = new byte[LicenceDetector.HeaderBytes];
    var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
    return Encoding.UTF8.GetString(buffer, 0, length);
  }

}
