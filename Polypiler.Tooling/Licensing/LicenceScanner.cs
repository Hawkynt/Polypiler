using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
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

  /// <summary>
  /// The licence check of a generated package: when the root holds a <see cref="Manifest.FileName"/>,
  /// the licence of every input — by its provenance and the corpus for a pinned file, by the
  /// <see cref="LicenceDetector"/> otherwise — and of every helper is resolved, the
  /// <see cref="EffectiveLicence"/> of them computed, and the manifest's <c>licensing.outputLicence</c>
  /// held against it (<c>PLY0024</c> when it understates it). A local input inside the root is detected
  /// with the root's declarations; one outside with those of its nearest ancestor that holds a
  /// <see cref="LicenceDeclarations.FileName"/> or a <c>.git</c> entry, else of its own folder.
  /// </summary>
  /// <param name="root">The package root.</param>
  /// <param name="corpus">The corpus manifest pinned inputs are looked up in, or <see langword="null"/> when there is none.</param>
  /// <returns>The check, or <see langword="null"/> when the root holds no manifest.</returns>
  /// <exception cref="CorpusException">A declarations file is invalid (<c>PLY0026</c>).</exception>
  public PackageLicence? ScanPackage(string root, CorpusManifest? corpus) {
    var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    var file = Path.Combine(top, Manifest.FileName);
    if (!File.Exists(file))
      return null;

    var bag = new DiagnosticBag();
    if (ManifestReader.Read(file, bag) is not { } manifest)
      return new(null, null, bag.Items);

    var package = Path.GetFileName(top);
    var detector = new LicenceDetector(policy, texts);
    var declarations = LicenceDeclarations.Load(top);
    var members = new List<LicensedSource>();
    var failed = false;
    foreach (var input in manifest.Inputs) {
      if (Pinned(input, corpus) is { } pinned) {
        members.Add(pinned);
        continue;
      }

      var full = Path.GetFullPath(Path.Combine(top, input.Path));
      var (detectionRoot, rules) = full.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? (top, declarations) : DetectionRoot(full);
      var path = Relative(detectionRoot, full);
      var detection = detector.Detect(detectionRoot, full, path, rules.For(path));
      if (detection.Undetermined is not null || detection.Licence is null) {
        bag.Report(LicenceDiagnostics.Unknown(input.Path, detection.Undetermined));
        failed = true;
      } else if (detection.Disagreement is { } disagreement) {
        bag.Report(LicenceDiagnostics.Refused($"'{input.Path}': {disagreement}"));
        failed = true;
      } else {
        members.Add(new(input.Path, detection.Licence));
      }
    }

    members.AddRange(manifest.Helpers.Select(helper => new LicensedSource(helper.Symbol, helper.License)));
    var stated = manifest.Licensing.OutputLicence;
    if (members.Count == 0) {
      if (!failed)
        bag.Report(LicenceDiagnostics.Unknown(package, "the manifest lists no input and no helper"));
      return new(stated, null, bag.Items);
    }

    var effective = EffectiveLicence.Compute(members, policy, bag);
    if (failed || effective is null)
      return new(stated, null, bag.Items);

    if (EffectiveLicence.Understates(stated, effective.Licence, policy))
      bag.Report(LicenceDiagnostics.Refused($"'{package}': output licence '{stated}' understates the effective licence '{effective.Licence}' (because of {string.Join(", ", effective.Because)})"));
    return new(stated, effective, bag.Items);
  }

  /// <summary>The licence of an input the corpus pins at the commit its provenance names: the declared licence of a fixture that lists the file, else the repository's.</summary>
  private static LicensedSource? Pinned(ManifestInput input, CorpusManifest? corpus) {
    if (input.Provenance is not { } provenance || corpus?.Repository(provenance.Repository) is not { } repository || repository.Commit != provenance.Commit)
      return null;

    var licence = corpus.Fixtures.FirstOrDefault(fixture => fixture.Repository == provenance.Repository && fixture.Files.Contains(provenance.Path))?.Licence ?? repository.Licence;
    return new($"{provenance.Repository}@{provenance.Commit}:{provenance.Path}", licence);
  }

  /// <summary>The nearest ancestor of a file that holds declarations or a <c>.git</c> entry, with its declarations; the file's own folder when none does.</summary>
  private static (string Root, LicenceDeclarations Declarations) DetectionRoot(string file) {
    var own = Path.GetDirectoryName(file)!;
    for (var folder = own; folder is not null; folder = Path.GetDirectoryName(folder))
      if (File.Exists(Path.Combine(folder, LicenceDeclarations.FileName)) || Path.Exists(Path.Combine(folder, ".git")))
        return (folder, LicenceDeclarations.Load(folder));
    return (own, LicenceDeclarations.None);
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
