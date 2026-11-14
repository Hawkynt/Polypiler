using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Json.Schema;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tests.Tooling.Licensing;
using Polypiler.Tooling;
using Polypiler.Tooling.Catalog.AdapterAudit;
using Polypiler.Tooling.Commands;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Licensing;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tests.Adapters.Ncnn;

/// <summary>
/// The adapter licence audit (<see cref="AdapterAudit"/>, <see cref="AdapterAuditValidator"/> and
/// <c>polypiler-tooling catalog audit-adapter</c>) against a scratch corpus of one repository whose
/// files, lock and licence file the tests write, and the committed NCNN audit against the committed
/// corpus lock. The digests of the scratch corpus come from the bytes the tests write; the oracle for
/// the committed digests is sha256sum, through the lock.
/// </summary>
[TestFixture]
public sealed class AdapterAuditTests {

  private const string Repository = "o/r";
  private const string Commit = "0123456789abcdef0123456789abcdef01234567";
  private const string Reviewed = "reviewer: owner\n    reviewedAt: 2026-11-14";
  private const string Pending = "{ status: pending, adr: null, date: null }";
  private const string Granted = "{ status: granted, adr: docs/adr/0033-x.md, date: 2026-11-15 }";
  private const string Refused = "{ status: refused, adr: docs/adr/0033-x.md, date: 2026-11-15 }";
  private const string Converted = "{ project: up, url: 'https://example.org/up', licence: MIT, notice: NOTICE.txt }";

  private static readonly string _root = PrototypeFixture.RepositoryRoot;
  private static readonly LicencePolicy _shipped = LicencePolicy.Load(Path.Combine(_root, LicencePolicy.RepositoryPath));

  /// <summary>The files of the scratch repository at the pin, with the LGPL-3.0 text as its licence file.</summary>
  private static readonly Dictionary<string, string> _files = new(StringComparer.Ordinal) {
    ["LICENSE"] = LicenceDetectorTests.Lgpl3Text,
    ["NOTICE.txt"] = "up, MIT; src/conv.c is converted from it.\n",
    ["src/own.c"] = "int own(void) { return 1; }\n",
    ["src/conv.c"] = "int conv(void) { return 2; }\n",
    ["src/mit.c"] = "// SPDX-License-Identifier: MIT\nint mit(void) { return 3; }\n",
    ["src/broken.c"] = "// SPDX-License-Identifier: MIT AND\nint broken(void) { return 4; }\n",
    ["legacy/old.asm"] = "nop\n",
    ["models/m.bin"] = "weights",
    ["unimported.c"] = "never written\n"
  };

  private string _scratch = "";

  [SetUp]
  public void CreateCorpus() {
    this._scratch = Scratch.Create("polypiler-adapter-audit-");
    foreach (var (path, text) in _files.Where(file => file.Key != "unimported.c"))
      Scratch.Write(this._scratch, $"corpus/upstream/{Repository}@{Commit}/{path}", text);
    Scratch.Write(this._scratch, CorpusLock.RepositoryPath, Lock().ToJson());
    var shipped = Path.Combine(_root, "tooling", "licensing");
    foreach (var file in Directory.EnumerateFiles(shipped, "*", SearchOption.AllDirectories))
      Scratch.Write(this._scratch, Path.Combine("tooling", "licensing", Path.GetRelativePath(shipped, file)), File.ReadAllBytes(file));
  }

  [TearDown]
  public void DeleteCorpus() => Scratch.Delete(this._scratch);

  private static CorpusLock Lock() => new(_files.Select(file => Entry(file.Key)));

  private static CorpusLockEntry Entry(string path) {
    var content = Encoding.UTF8.GetBytes(_files[path]);
    return new(Repository, Commit, path, CorpusLockEntry.Hash(content), content.LongLength);
  }

  /// <summary>A record of a scratch file whose digest and length are right unless given.</summary>
  private static string Record(string path, string licence = "LGPL-3.0-or-later", string origin = "own", string obligations = "[notice]",
    string decision = "usable", string? reason = null, string kind = "code", string? sha256 = null, long? bytes = null) {
    var entry = _files.ContainsKey(path) ? Entry(path) : new(Repository, Commit, path, new string('a', 64), 1);
    return $"""

        - path: {path}
          kind: {kind}
          sha256: '{sha256 ?? entry.Sha256}'
          bytes: {bytes ?? entry.Bytes}
          licence: {licence}
          origin: {origin}
          obligations: {obligations}
          decision: {decision}
          usedBy: [ADP-NCNN-SHIM]
          {Reviewed}{(reason is null ? "" : $"\n    reason: '{reason}'")}
      """.Replace("\n  ", "\n");
  }

  private static string Component(string name, string decision = "usable", string obligations = "[notice]", string licence = "BSD-3-Clause", string notice = "NOTICE.txt")
    => $"""

        - name: {name}
          kind: provider
          licence: {licence}
          url: 'https://example.org/{name}'
          notice: {notice}
          obligations: {obligations}
          decision: {decision}
          reason: 'why {name}'
          {Reviewed}
      """.Replace("\n  ", "\n");

  private static string Audit(string records, string external = "", string clearance = Pending)
    => $"schema: 1\nrepository: {Repository}\ncommit: {Commit}\nclearance: {clearance}\nrecords:{(records.Length == 0 ? " []" : records)}\nexternal:{(external.Length == 0 ? " []" : external)}\n";

  private IReadOnlyList<Diagnostic> Validate(string text, LicencePolicy? policy = null, params string[] touches)
    => new AdapterAuditValidator(policy ?? _shipped, LicenceDetectorTests.ShippedTexts).Validate(AdapterAudit.Parse(text, "audit.yaml"), this._scratch, Lock(), touches);

  private IReadOnlyList<string> Messages(string text, LicencePolicy? policy = null, params string[] touches)
    => [.. this.Validate(text, policy, touches).Select(DiagnosticRenderer.ToText)];

  private static string Error(string message) => $"polypiler: error PLY0950: {message}";

  private static string Rule(string detail) => Error($"licence audit: {detail}");

  private static AdapterAuditException Rejected(string text) => Assert.Throws<AdapterAuditException>(() => AdapterAudit.Parse(text, "audit.yaml"))!;

  // --- the committed audit -------------------------------------------------------------------------

  private static readonly string _committedPath = Path.Combine(_root, "spec", "adapters", "ncnn", "licence-audit.yaml");

  private static AdapterAudit Committed() => AdapterAudit.Load(_committedPath);

  [Test]
  public void CommittedAudit_RecordsTheRifeFlownetParam_WithTheDigestOfTheIssue() {
    var audit = Committed();
    var record = audit.Record("app/src/main/assets/models/rife-4.25-lite/flownet.param")!;
    Assert.That(audit.File, Is.EqualTo(_committedPath));
    Assert.That((record.Bytes, record.Sha256, record.IsModel, record.Decision), Is.EqualTo((35830L, "e55b76f197b89637dc824ce89e64878df419214d3cf6a10aea0283c61b93a908", true, AuditDecision.Usable)));
    Assert.That((record.Licence, record.Origin!.Notice, record.UsedBy, record.Reviewer, record.ReviewedAt),
      Is.EqualTo(("MIT", "app/THIRD-PARTY-NOTICE.RIFE.txt", new[] { "ADP-NCNN-E2E", "ADP-NCNN-WEIGHTS" }, "Hawkynt", "2026-11-14")));
    var ncnn = audit.External.Single(component => component.Name == "ncnn");
    Assert.That((ncnn.Url, ncnn.Notice, ncnn.Reviewer, ncnn.ReviewedAt), Is.EqualTo(("https://github.com/Tencent/ncnn", "app/THIRD-PARTY-NOTICE.ncnn.txt", "Hawkynt", "2026-11-14")));
  }

  [Test]
  public void CommittedAudit_EveryRecordEqualsTheCorpusLockAtThePin_AndIsSortedAndUnique() {
    var audit = Committed();
    var lockFile = CorpusLock.Load(Path.Combine(_root, CorpusLock.RepositoryPath));
    Assert.That((audit.Repository, audit.Commit, audit.Clearance), Is.EqualTo(("Hawkynt/MediaDroid", "7f8b6b995cbe54a6afb290a900748393bf01e272", new AuditClearance(AuditClearance.Pending, null, null))));
    foreach (var record in audit.Records) {
      var locked = lockFile.Find(audit.Repository, audit.Commit, record.Path);
      Assert.That(locked, Is.Not.Null, record.Path);
      Assert.That((record.Sha256, record.Bytes), Is.EqualTo((locked!.Sha256, locked.Bytes)), record.Path);
    }

    Assert.That(audit.Records.Select(record => record.Path), Is.Ordered.Using((IComparer<string>)StringComparer.Ordinal).And.Unique);
  }

  [Test]
  public void CommittedAudit_HasARecordForEveryAuditedFile_AndEveryNoticeIsInTheLock() {
    var audit = Committed();
    var lockFile = CorpusLock.Load(Path.Combine(_root, CorpusLock.RepositoryPath));
    var audited = lockFile.Entries
      .Where(entry => entry.Repository == audit.Repository && entry.Commit == audit.Commit)
      .Select(entry => entry.Path)
      .Where(path => path.StartsWith("native/src/", StringComparison.Ordinal) || path.StartsWith("app/src/main/assets/models/", StringComparison.Ordinal)
        || path is "native/jni/interpolator.cpp" or "native/CMakeLists.txt")
      .ToList();
    Assert.That(audited, Has.Count.EqualTo(19));
    Assert.That(audited.Where(path => audit.Record(path) is null), Is.Empty);
    var notices = audit.Records.Select(record => record.Origin?.Notice).OfType<string>().Concat(audit.External.Select(component => component.Notice)).Distinct();
    Assert.That(notices.Where(notice => lockFile.Find(audit.Repository, audit.Commit, notice) is null), Is.Empty);
  }

  [Test]
  public void CommittedAudit_BlocksXvfi_AndRecordsTheWeightsItDoesNotAuditAsFiles() {
    var audit = Committed();
    var xvfi = audit.Record("native/src/xvfi.cpp")!;
    Assert.That((xvfi.Licence, xvfi.Decision, xvfi.HasRestrictingObligation), Is.EqualTo(("LicenseRef-XVFI-Research-Only", AuditDecision.Blocked, true)));
    Assert.That(xvfi.Reason, Does.Contain("research and education only"));
    Assert.That(audit.External.Select(component => (component.Name, component.Kind, component.Licence, component.Decision)), Is.EqualTo(new[] {
      ("cain-weights", "weights", "MIT", AuditDecision.ReferenceOnly),
      ("dain-weights", "weights", "MIT", AuditDecision.ReferenceOnly),
      ("flavr-weights", "weights", "Apache-2.0", AuditDecision.ReferenceOnly),
      ("ncnn", "provider", "BSD-3-Clause", AuditDecision.Usable),
      ("xvfi-weights", "weights", "LicenseRef-XVFI-Research-Only", AuditDecision.Blocked)
    }));
    Assert.That(audit.Records.Where(record => record.IsModel).Select(record => record.Path),
      Is.EqualTo(audit.Records.Where(record => _shipped.IsRestricted($"{audit.Repository}@{audit.Commit}/{record.Path}")).Select(record => record.Path)));
  }

  [Test]
  public void Schema_IsValidAgainstTheMetaSchema_AndTheCommittedAuditConformsToIt() {
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "spec", "schemas", "adapter-audit.schema.json")));
    Assert.That(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, Is.True);
    Assert.That(() => Committed(), Throws.Nothing);
  }

  [Test]
  public void SpecTree_LeavesTheAdapterAuditsToTheirOwnVerb()
    => Assert.That(SpecTree.Load(Path.Combine(_root, "spec"), _root).Documents.Where(document => document.Path.StartsWith("adapters/", StringComparison.Ordinal)), Is.Empty);

  // --- happy paths and equivalence classes ----------------------------------------------------------

  [Test]
  public void ValidAudit_WithoutTouches_HasNoViolation()
    => Assert.That(this.Messages(Audit(Record("NOTICE.txt", obligations: "[]") + Record("src/conv.c", "LGPL-3.0-or-later AND MIT", Converted) + Record("src/own.c"), Component("prov"))), Is.Empty);

  [Test]
  public void OriginOwnAndConvertedWithNotice_AreBothValid() {
    var audit = AdapterAudit.Parse(Audit(Record("src/conv.c", "MIT", Converted) + Record("src/own.c")), "audit.yaml");
    Assert.That(audit.Records.Select(record => record.Origin), Is.EqualTo(new[] { new AuditOrigin("up", "https://example.org/up", "MIT", "NOTICE.txt"), null }));
    Assert.That(this.Messages(Audit(Record("src/conv.c", "MIT", Converted) + Record("src/own.c"))), Is.Empty);
  }

  [TestCase(Pending)]
  [TestCase(Granted)]
  public void TouchOfAUsableFile_PassesUnlessTheClearanceIsRefused(string clearance)
    => Assert.That(this.Messages(Audit(Record("src/own.c"), clearance: clearance), null, "src/own.c"), Is.Empty);

  [Test]
  public void RefusedClearance_BlocksEveryTouch_OnceWhateverIsTouched()
    => Assert.That(this.Messages(Audit(Record("src/own.c"), clearance: Refused), null, "src/own.c", "src/own.c"),
      Is.EqualTo(new[] { Error("licence clearance was refused (docs/adr/0033-x.md)") }));

  [Test]
  public void RefusedClearance_WithoutTouches_IsNoViolation()
    => Assert.That(this.Messages(Audit(Record("src/own.c"), clearance: Refused)), Is.Empty);

  [TestCase("usable", null, null)]
  [TestCase("reference-only", "read only", "'src/own.c' is reference-only in the licence audit: read only")]
  [TestCase("blocked", "research and education only", "'src/own.c' is blocked in the licence audit: research and education only")]
  public void TouchedDecision(string decision, string? reason, string? expected) {
    var obligations = decision == "blocked" ? "[research-only]" : "[notice]";
    Assert.That(this.Messages(Audit(Record("src/own.c", obligations: obligations, decision: decision, reason: reason)), null, "src/own.c"),
      Is.EqualTo(expected is null ? Array.Empty<string>() : [Error(expected)]));
  }

  [Test]
  public void TouchOfAComponent_ByName_FollowsItsDecision() {
    var text = Audit("", Component("good") + Component("xvfi", "blocked", "[research-only, no-redistribution]", "LicenseRef-XVFI"));
    Assert.That(this.Messages(text, null, "good"), Is.Empty);
    Assert.That(this.Messages(text, null, "xvfi"), Is.EqualTo(new[] { Error("'xvfi' is blocked in the licence audit: why xvfi") }));
  }

  [TestCase(Pending, "'models/m.bin' is a restricted asset and its licence clearance is pending")]
  [TestCase(Granted, null)]
  public void TouchOfAUsableModel_NeedsTheClearanceGranted(string clearance, string? expected)
    => Assert.That(this.Messages(Audit(Record("models/m.bin", "MIT", Converted, kind: "model"), clearance: clearance), null, "models/m.bin"),
      Is.EqualTo(expected is null ? Array.Empty<string>() : [Error(expected)]));

  // --- boundaries ------------------------------------------------------------------------------------

  [Test]
  public void ZeroRecords_WithoutTouches_IsValid()
    => Assert.That(this.Messages(Audit("")), Is.Empty);

  [Test]
  public void ZeroRecords_WithATouch_HasNoRecord()
    => Assert.That(this.Messages(Audit(""), null, "native/jni/desktop.cpp"), Is.EqualTo(new[] { Error("'native/jni/desktop.cpp' has no licence audit record") }));

  [TestCase(63, false)]
  [TestCase(65, false)]
  [TestCase(64, true)]
  public void Sha256OfTheWrongLengthOrCase_BreaksTheSchema(int digits, bool upper) {
    var sha256 = new string('a', digits);
    var exception = Rejected(Audit(Record("src/own.c", sha256: upper ? sha256.ToUpperInvariant() : sha256)));
    Assert.That(exception.Diagnostics.Select(DiagnosticRenderer.ToText), Is.EqualTo(new[] {
      "audit.yaml(8,11): error PLY0950: licence audit: /records/0/sha256: The string value is not a match for the indicated regular expression"
    }));
  }

  [Test]
  public void BytesZero_ForANonEmptyFile_IsStale() {
    var entry = Entry("src/own.c");
    Assert.That(this.Messages(Audit(Record("src/own.c", bytes: 0))),
      Is.EqualTo(new[] { Error($"licence audit record 'src/own.c' is stale: SHA-256 {entry.Sha256}, recorded {entry.Sha256} ({entry.Bytes} bytes, recorded 0 bytes)") }));
  }

  [Test]
  public void ADigestThatDiffers_IsStale() {
    var entry = Entry("src/own.c");
    Assert.That(this.Messages(Audit(Record("src/own.c", sha256: new string('0', 64)))),
      Is.EqualTo(new[] { Error($"licence audit record 'src/own.c' is stale: SHA-256 {entry.Sha256}, recorded {new string('0', 64)}") }));
  }

  // --- exceptions --------------------------------------------------------------------------------------

  [Test]
  public void BlockedWithoutARestrictingObligation_IsARuleViolation()
    => Assert.That(this.Messages(Audit(Record("src/own.c", obligations: "[attribution]", decision: "blocked", reason: "no"), Component("c", "blocked"))), Is.EqualTo(new[] {
      Rule("record 'src/own.c' is blocked without a no-redistribution or research-only obligation"),
      Rule("component 'c' is blocked without a no-redistribution or research-only obligation")
    }));

  [TestCase("reference-only")]
  [TestCase("blocked")]
  public void NonUsableWithoutAReason_IsARuleViolation(string decision)
    => Assert.That(this.Messages(Audit(Record("src/own.c", obligations: "[no-redistribution]", decision: decision))),
      Is.EqualTo(new[] { Rule($"record 'src/own.c' is {decision} without a reason") }));

  [Test]
  public void ANoticeAbsentFromTheCorpus_IsAMissingCorpusFile() {
    var messages = this.Messages(Audit(Record("src/conv.c", "MIT", Converted.Replace("NOTICE.txt", "NOPE.txt")), Component("c", notice: "unimported.c")));
    Assert.That(messages, Is.EqualTo(new[] {
      $"polypiler: error PLY0025: 'NOPE.txt' does not exist in {Repository} at {Commit}: the notice of record 'src/conv.c' is not in the corpus lock",
      $"polypiler: error PLY0025: 'unimported.c' does not exist in {Repository} at {Commit}: the notice of component 'c' is not imported; run 'polypiler-tooling corpus import'"
    }));
  }

  [Test]
  public void ANoticeThatIsARecord_NeedsNoCorpusFileOfItsOwn()
    => Assert.That(this.Messages(Audit(Record("NOTICE.txt", obligations: "[]") + Record("src/conv.c", "MIT", Converted))), Is.Empty);

  [Test]
  public void ARecordedFileAbsentFromTheLockOrNotImported_IsAMissingCorpusFile() {
    var messages = this.Messages(Audit(Record("src/none.c") + Record("unimported.c")));
    Assert.That(messages, Has.Count.EqualTo(2));
    Assert.That(messages[0], Is.EqualTo($"polypiler: error PLY0025: 'src/none.c' does not exist in {Repository} at {Commit}: it is not in the corpus lock"));
    Assert.That(messages[1], Does.StartWith($"polypiler: error PLY0025: 'unimported.c' does not exist in {Repository} at {Commit}: it is not imported; run 'polypiler-tooling corpus import' ("));
  }

  [Test]
  public void AnImportedFileThatDiffersFromTheLock_IsAHashMismatch_AndTheRecordStale() {
    Scratch.Write(this._scratch, $"corpus/upstream/{Repository}@{Commit}/src/own.c", "changed\n");
    var messages = this.Messages(Audit(Record("src/own.c")));
    Assert.That(messages, Has.Count.EqualTo(2));
    Assert.That(messages[0], Does.StartWith($"polypiler: error PLY0023: corpus file '{Repository}:src/own.c' has SHA-256 "));
    Assert.That(messages[1], Does.Contain("licence audit record 'src/own.c' is stale"));
  }

  [Test]
  public void TheRepositorysOwnFileRecordedUnderAnotherLicence_ContradictsItsLicenceFile()
    => Assert.That(this.Messages(Audit(Record("src/own.c", "MIT"))), Is.EqualTo(new[] {
      Rule("record 'src/own.c' says 'MIT', the licence scanner detects 'LGPL-3.0' from the repository's licence file, and the record names no origin whose notice says otherwise")
    }));

  [Test]
  public void AConvertedFileRecordedUnderItsNoticesLicence_DoesNotContradictTheRepositorysLicenceFile()
    => Assert.That(this.Messages(Audit(Record("src/conv.c", "MIT", Converted))), Is.Empty);

  [TestCase("MIT", null)]
  [TestCase("MIT AND LGPL-3.0-or-later", null)]
  [TestCase("LGPL-3.0-or-later", "record 'src/mit.c' says 'LGPL-3.0-or-later', the licence scanner detects 'MIT' from its header")]
  public void AHeaderOfTheFile_ContradictsARecordWhoseFirstOperandItDoesNotName(string licence, string? expected)
    => Assert.That(this.Messages(Audit(Record("src/mit.c", licence, Converted))), Is.EqualTo(expected is null ? Array.Empty<string>() : [Rule(expected)]));

  [Test]
  public void AHeaderThatDoesNotParse_ContradictsNothing()
    => Assert.That(this.Messages(Audit(Record("src/broken.c", "LGPL-3.0-or-later", Converted))), Is.Empty);

  [Test]
  public void AnOverrideOfThePolicy_ContradictsARecordWhoseFirstOperandItDoesNotName() {
    var policy = LicencePolicy.Parse(LicencePolicyTests.Policy(overrides: "[{ glob: '**/legacy/*.asm', licence: BSD-3-Clause, reason: 'reviewed', reviewer: owner }]"), "p.yaml", LicencePolicyTests.Decisions);
    Assert.That(this.Messages(Audit(Record("legacy/old.asm")), policy), Is.EqualTo(new[] {
      Rule("record 'legacy/old.asm' says 'LGPL-3.0-or-later', the licence scanner detects 'BSD-3-Clause' from its override")
    }));
    Assert.That(this.Messages(Audit(Record("legacy/old.asm", "BSD-3-Clause")), policy), Is.Empty);
  }

  [Test]
  public void ALicenceThatIsNoSpdxExpression_IsARuleViolation() {
    var messages = this.Messages(Audit(Record("src/own.c", "'MIT AND'", Converted.Replace("licence: MIT", "licence: 'OR'")), Component("c", licence: "'(MIT'")));
    Assert.That(messages, Has.Count.EqualTo(3));
    Assert.That(messages[0], Does.StartWith(Rule("record 'src/own.c' has licence 'MIT AND', which is not an SPDX expression: ")));
    Assert.That(messages[1], Does.StartWith(Rule("the origin of record 'src/own.c' has licence 'OR', which is not an SPDX expression: ")));
    Assert.That(messages[2], Does.StartWith(Rule("component 'c' has licence '(MIT', which is not an SPDX expression: ")));
  }

  [Test]
  public void ARestrictedAssetMustBeAModel_AndAModelARestrictedAsset()
    => Assert.That(this.Messages(Audit(Record("models/m.bin", "MIT", Converted) + Record("src/conv.c", "MIT", Converted, kind: "model"))), Is.EqualTo(new[] {
      Rule("record 'models/m.bin' is a restricted asset of the licence policy, so its kind is model"),
      Rule("record 'src/conv.c' is of kind model, but the licence policy does not restrict it")
    }));

  [Test]
  public void TwoRecordsForOnePath_AndRecordsOutOfOrder_AndTwoComponentsOfOneName_AreRuleViolations()
    => Assert.That(this.Messages(Audit(Record("src/own.c") + Record("src/own.c") + Record("src/conv.c", "MIT", Converted), Component("c") + Component("c"))), Is.EqualTo(new[] {
      Rule("two records for 'src/own.c'"),
      Rule("records are not sorted by path: 'src/conv.c' follows 'src/own.c'"),
      Rule("two components named 'c'")
    }));

  [Test]
  public void EveryViolationIsReported_InOneRun()
    => Assert.That(this.Messages(Audit(Record("src/own.c", "MIT", decision: "blocked") + Record("src/zzz.c")), null, "nothing"), Has.Count.EqualTo(5));

  [Test]
  public void AuditFileMissing_IsAViolation() {
    var path = Path.Combine(this._scratch, "missing.yaml");
    var exception = Assert.Throws<AdapterAuditException>(() => AdapterAudit.Load(path))!;
    Assert.That(exception.Diagnostics.Single().Message, Does.StartWith($"licence audit: cannot read '{path}': "));
    Assert.That(exception.Diagnostics.Single().ExitCode, Is.EqualTo(ExitCode.LicenseBlocker));
  }

  [Test]
  public void AuditNotYaml_IsAViolationAtItsPosition()
    => Assert.That(DiagnosticRenderer.ToText(Rejected("schema: 1\nrecords: [\n").Diagnostics.Single()), Does.StartWith("audit.yaml(3,1): error PLY0950: licence audit: not valid YAML: "));

  [TestCase("", "/: Value is \"null\" but should be \"object\"")]
  [TestCase("schema: 1\nrepository: o/r\ncommit: x\nclearance: {}\nrecords: []\nexternal: []\nextra: 1\n", "/extra: property 'extra' is not allowed")]
  [TestCase("schema: 1\nrepository: o/r\ncommit: 0123456789abcdef0123456789abcdef01234567\nclearance: { status: pending, adr: docs/adr/0001-x.md, date: null }\nrecords: []\nexternal: []\n", "/clearance/adr: Value is \"string\" but should be \"null\"")]
  public void AuditBreakingTheSchema_IsAViolationPerError(string text, string expected) {
    var messages = Rejected(text).Diagnostics.Select(diagnostic => diagnostic.Message).ToList();
    Assert.That(messages, Does.Contain($"licence audit: {expected}"));
  }

  [Test]
  public void AnOriginThatIsAStringOtherThanOwn_BreaksTheSchema()
    => Assert.That(Rejected(Audit(Record("src/own.c", origin: "theirs"))).Diagnostics.Single().Message,
      Does.StartWith("licence audit: /records/0/origin: Expected ").And.Contain("own"));

  [Test]
  public void TheDecisionsHaveTheirTexts() {
    Assert.That(Enum.GetValues<AuditDecision>().Select(AuditDecisions.Name), Is.EqualTo(new[] { "usable", "reference-only", "blocked" }));
    Assert.That(new[] { "usable", "reference-only", "blocked" }.Select(AuditDecisions.Parse), Is.EqualTo(Enum.GetValues<AuditDecision>()));
  }

  // --- the command -------------------------------------------------------------------------------------

  private (int ExitCode, string Output, string Error) Run(string? workingDirectory, params string[] args) {
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = new CatalogCommand(workingDirectory ?? Directory.CreateDirectory(Path.Combine(this._scratch, "some", "folder")).FullName).Run(args, output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  private string WriteAudit(string text) => Scratch.Write(this._scratch, "spec/adapters/x/licence-audit.yaml", text);

  [Test]
  public void Command_ChecksTheAuditBelowTheNearestCorpusRoot() {
    this.WriteAudit(Audit(Record("src/own.c"), Component("c")));
    Assert.That(this.Run(null, "audit-adapter", "--audit", "../../spec/adapters/x/licence-audit.yaml"),
      Is.EqualTo((0, $"1 records and 1 components of {Repository}@{Commit} checked{Environment.NewLine}", "")));
    Assert.That(this.Run(null, "audit-adapter", "--audit", "../../spec/adapters/x/licence-audit.yaml", "--touch", "src/own.c").Output,
      Does.EndWith($"checked, 1 touched path usable{Environment.NewLine}"));
    Assert.That(this.Run(null, "audit-adapter", "--touch", "c", "--audit", "../../spec/adapters/x/licence-audit.yaml", "--touch", "src/own.c").Output,
      Does.EndWith($"checked, 2 touched paths usable{Environment.NewLine}"));
  }

  [Test]
  public void Command_TakesANamedCorpusRoot_RelativeToTheCurrentDirectory() {
    var audit = this.WriteAudit(Audit(Record("src/own.c")));
    var outside = Scratch.Create("polypiler-adapter-audit-outside-");
    try {
      Assert.That(this.Run(outside, "audit-adapter", "--audit", audit, "--corpus", Path.GetRelativePath(outside, this._scratch)).ExitCode, Is.Zero);
      var (exitCode, _, error) = this.Run(outside, "audit-adapter", "--audit", audit);
      Assert.That(exitCode, Is.EqualTo(1), "without a corpus root above it the current directory is the root, and it holds no policy");
      Assert.That(error, Does.Contain("error PLY0026: corpus manifest invalid: licence policy: cannot read"));
    } finally {
      Scratch.Delete(outside);
    }
  }

  [Test]
  public void Command_ReportsViolationsWithTheLicenceBlockerExitCode() {
    this.WriteAudit(Audit(Record("src/own.c", obligations: "[research-only]", decision: "blocked", reason: "research and education only")));
    var (exitCode, output, error) = this.Run(null, "audit-adapter", "--audit", Path.Combine(this._scratch, "spec/adapters/x/licence-audit.yaml"), "--touch", "src/own.c");
    Assert.That((exitCode, output), Is.EqualTo((8, "")));
    Assert.That(error, Is.EqualTo(Error("'src/own.c' is blocked in the licence audit: research and education only") + Environment.NewLine));
  }

  [Test]
  public void Command_ReportsAnAuditThatCannotBeRead_BeforeAnythingElse() {
    var (exitCode, output, error) = this.Run(null, "audit-adapter", "--audit", "missing.yaml");
    Assert.That((exitCode, output), Is.EqualTo((8, "")));
    Assert.That(error, Does.StartWith("polypiler: error PLY0950: licence audit: cannot read "));
  }

  [Test]
  public void Command_AMissingCorpusFile_IsAnEnvironmentError() {
    this.WriteAudit(Audit(Record("src/none.c")));
    Assert.That(this.Run(null, "audit-adapter", "--audit", Path.Combine(this._scratch, "spec/adapters/x/licence-audit.yaml")).ExitCode, Is.EqualTo(1));
  }

  [TestCase(new string[0], "catalog needs 'audit-adapter'")]
  [TestCase(new[] { "audit" }, "unknown catalog command 'audit'")]
  [TestCase(new[] { "audit-adapter" }, "catalog audit-adapter needs '--audit <file>'")]
  [TestCase(new[] { "audit-adapter", "--touch", "x" }, "catalog audit-adapter needs '--audit <file>'")]
  [TestCase(new[] { "audit-adapter", "--file", "x" }, "unknown option '--file'")]
  [TestCase(new[] { "audit-adapter", "--audit" }, "option '--audit' requires a value")]
  [TestCase(new[] { "audit-adapter", "--audit", "a", "--audit", "b" }, "option '--audit' is given more than once")]
  [TestCase(new[] { "audit-adapter", "--audit", "a", "--corpus", "b", "--corpus", "c" }, "option '--corpus' is given more than once")]
  public void Command_RefusesACommandLineItCannotUnderstand(string[] args, string message)
    => Assert.That(this.Run(null, args), Is.EqualTo((2, "", $"polypiler-tooling: error {ToolchainsCommand.UsageCode}: {message}{Environment.NewLine}")));

  [Test]
  public void Program_DispatchesTheVerb_AndListsItInItsUsage() {
    using var output = new StringWriter();
    using var error = new StringWriter();
    Assert.That(Program.Run(["catalog"], output, error), Is.EqualTo(Program.UsageError));
    Assert.That(error.ToString(), Does.Contain("catalog needs 'audit-adapter'"));
    Assert.That(Program.Run([], output, error), Is.EqualTo(Program.Success));
    Assert.That(output.ToString(), Does.Contain("polypiler-tooling catalog audit-adapter --audit <file> [--touch <path>]... [--corpus <dir>]"));
  }

}
