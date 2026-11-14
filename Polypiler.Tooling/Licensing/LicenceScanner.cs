using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Tooling.Corpus;

namespace Polypiler.Tooling.Licensing;

/// <summary>
/// Scans files for their licences and checks them against the policy. The scan is read-only and
/// complete: every file is listed in the report and every violation is reported, not only the first.
/// A tree is every file below its root but those inside a <c>.git</c> folder; the corpus is every file
/// of <c>tooling/corpus/corpus.lock.json</c>, under <c>corpus/upstream/</c>.
/// </summary>
/// <param name="policy">The policy.</param>
/// <param name="texts">The reference licence texts.</param>
public sealed class LicenceScanner(LicencePolicy policy, LicenceTexts texts) {

  /// <summary>The folder name whose sub-folders are vendored libraries, each of which needs a licence file of its own.</summary>
  public const string VendoredFolder = "Vendored";

  /// <summary>Scans every file below a root, with the declarations of its <see cref="LicenceDeclarations.FileName"/>.</summary>
  /// <exception cref="CorpusException">The declarations are invalid (<c>PLY0026</c>).</exception>
  public LicenceReport ScanTree(string root, LicenceMode mode) {
    var top = Path.GetFullPath(root);
    var declarations = LicenceDeclarations.Load(top);
    var detector = new LicenceDetector(policy, texts);
    var entries = new List<LicenceReportEntry>();
    var diagnostics = new List<Diagnostic>();
    var files = Directory.EnumerateFiles(top, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
      .Select(file => (Full: file, Path: Relative(top, file)))
      .Where(file => !file.Path.Split('/').Contains(".git"))
      .OrderBy(file => file.Path, StringComparer.Ordinal);
    foreach (var (full, path) in files)
      entries.Add(this.Check(detector, top, full, path, declarations.For(path), mode, diagnostics));

    foreach (var library in VendoredLibraries(top).Where(library => detector.LicenceFileOf(library) is null))
      diagnostics.Add(LicenceDiagnostics.Unknown(Relative(top, library) + "/", "a vendored library needs a licence file of its own"));
    return new(entries, diagnostics);
  }

  /// <summary>
  /// Scans every file of the lock in the imported corpus below a repository root. A file's licence
  /// file is looked for up to its repository's folder; the declared licence of the first fixture that
  /// names the file selects the variant of a shared text, the repository's for any other file. A file
  /// the lock lists that is not imported is a <c>PLY0023</c>.
  /// </summary>
  public LicenceReport ScanCorpus(string root, CorpusManifest manifest, CorpusLock lockFile, LicenceMode mode) {
    var detector = new LicenceDetector(policy, texts);
    var entries = new List<LicenceReportEntry>();
    var diagnostics = new List<Diagnostic>();
    foreach (var entry in lockFile.Entries) {
      var file = CorpusImporter.FilePath(root, entry.Repository, entry.Commit, entry.Path);
      var repository = CorpusImporter.FilePath(root, entry.Repository, entry.Commit, ".");
      if (!File.Exists(file)) {
        diagnostics.Add(CorpusDiagnostics.Mismatch($"corpus file '{entry.Name}' cannot be read, the lock records {entry.Sha256}: it is not imported; run 'corpus import'"));
        entries.Add(new(entry.Location, null, null, LicenceStatus.Denied));
        continue;
      }

      var declared = manifest.Fixtures.FirstOrDefault(fixture => fixture.Repository == entry.Repository && fixture.Files.Contains(entry.Path))?.Licence
        ?? manifest.Repository(entry.Repository)?.Licence;
      entries.Add(this.Check(detector, repository, file, entry.Location, declared, mode, diagnostics));
    }

    return new(entries, diagnostics);
  }

  private LicenceReportEntry Check(LicenceDetector detector, string root, string file, string path, string? declared, LicenceMode mode, List<Diagnostic> diagnostics) {
    var detection = detector.Detect(root, file, path, declared);
    var source = detection.Licence is null ? null : detection.Source;
    if (mode == LicenceMode.Distribution && policy.IsRestricted(path) && policy.Override(path) is null) {
      diagnostics.Add(LicenceDiagnostics.Refused($"'{path}' is a restricted asset and may not be redistributed"));
      return new(path, detection.Licence, source, LicenceStatus.Denied);
    }

    if (detection.Undetermined is not null || detection.Licence is null) {
      diagnostics.Add(LicenceDiagnostics.Unknown(path, detection.Undetermined));
      return new(path, detection.Licence, source, LicenceStatus.Denied);
    }

    if (detection.Disagreement is { } disagreement) {
      diagnostics.Add(LicenceDiagnostics.Refused($"'{path}': {disagreement}"));
      return new(path, detection.Licence, source, LicenceStatus.Denied);
    }

    var status = policy.Evaluate(detection.Expression!, mode);
    if (status == LicenceStatus.Denied)
      diagnostics.Add(LicenceDiagnostics.Refused($"'{path}': licence '{detection.Licence}' is denied in {LicenceReport.Name(mode)} mode{this.PendingNote(detection.Expression!)}"));
    return new(path, detection.Licence, source, status);
  }

  /// <summary>What the message adds for identifiers of an expression that await an owner decision.</summary>
  private string PendingNote(SpdxExpression expression) {
    var pending = expression.Licences.Distinct().Select(policy.Pending).OfType<PendingDecision>().ToList();
    return pending.Count == 0
      ? ""
      : $" ({string.Join("; ", pending.Select(entry => $"{entry.Licence} awaits an owner decision, requested by {entry.RequestedBy}"))})";
  }

  /// <summary>The sub-folders of every <see cref="VendoredFolder"/> folder at or below a root, the root itself included.</summary>
  private static IEnumerable<string> VendoredLibraries(string top) {
    var folders = Directory.EnumerateDirectories(top, VendoredFolder, new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, MatchCasing = MatchCasing.CaseSensitive });
    if (Path.GetFileName(top.TrimEnd(Path.DirectorySeparatorChar)) == VendoredFolder)
      folders = folders.Prepend(top);
    return folders.SelectMany(Directory.EnumerateDirectories).Order(StringComparer.Ordinal);
  }

  private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

}
