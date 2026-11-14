using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tests.Tooling.Licensing;

/// <summary>
/// <see cref="LicencePolicy"/>: the shipped policy and its decisions, the status of an identifier in
/// each mode, globs, and every way a policy is refused with <c>PLY0026</c>.
/// </summary>
[TestFixture]
public sealed class LicencePolicyTests {

  /// <summary>The shipped policy file.</summary>
  public static string Shipped { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, LicencePolicy.RepositoryPath.Replace('/', Path.DirectorySeparatorChar));

  /// <summary>A minimal valid policy whose lists are the given YAML values.</summary>
  public static string Policy(string allowed = "[MIT]", string referenceOnly = "[AGPL-3.0-only]", string exceptions = "[]", string restricted = "['*.bin']", string overrides = "[]", string pending = "[]")
    => $"schema: 1\nallowed: {allowed}\nreferenceOnly: {referenceOnly}\nallowedExceptions: {exceptions}\nrestrictedAssets: {restricted}\noverrides: {overrides}\npendingDecisions: {pending}\n";

  /// <summary>Decisions for the identifiers of <see cref="Policy"/>'s defaults.</summary>
  public const string Decisions = """
    | Identifier | List | Date | Reason | Asked by |
    | --- | --- | --- | --- | --- |
    | `MIT` | allowed | 2026-11-13 | test | T |
    | `AGPL-3.0-only` | referenceOnly | 2026-11-13 | test | T |
    | `Classpath-exception-2.0` | allowedExceptions | 2026-11-13 | test | T |
    """;

  private static string Refusal(string text, string decisions = Decisions)
    => string.Join("\n", Assert.Throws<CorpusException>(() => LicencePolicy.Parse(text, "p.yaml", decisions))!.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

  [Test]
  public void ShippedPolicy_Has17Allowed3ReferenceOnlyAndNothingPending_EachWithItsDecision() {
    var policy = LicencePolicy.Load(Shipped);
    Assert.That(policy.Allowed, Is.EquivalentTo(new[] {
      "LGPL-3.0-or-later", "LGPL-3.0-only", "LGPL-2.1-or-later", "LGPL-2.0-or-later", "LGPL-2.0-only", "GPL-3.0-or-later", "GPL-3.0-only",
      "GPL-2.0-or-later", "MIT", "BSD-2-Clause", "BSD-3-Clause", "Apache-2.0", "ISC", "Zlib", "0BSD", "Unlicense", "CC0-1.0" }));
    Assert.That(policy.ReferenceOnly, Is.EquivalentTo(new[] { "GPL-2.0-only", "AGPL-3.0-only", "AGPL-3.0-or-later" }));
    Assert.That(policy.PendingDecisions, Is.Empty);
    Assert.That(policy.AllowedExceptions, Is.Empty);
    Assert.That(policy.Overrides, Is.Empty);
    Assert.That(policy.RestrictedAssets.Select(glob => glob.Text), Is.EqualTo(new[] { "*.onnx", "*.param", "*.bin", "*.pt", "*.safetensors", "*.mlmodel" }));
  }

  [TestCase("MIT", LicenceMode.Corpus, LicenceStatus.Allowed)]
  [TestCase("MIT", LicenceMode.Distribution, LicenceStatus.Allowed)]
  [TestCase("AGPL-3.0-only", LicenceMode.Corpus, LicenceStatus.ReferenceOnly)]
  [TestCase("AGPL-3.0-only", LicenceMode.Distribution, LicenceStatus.Denied)]
  [TestCase("EUPL-1.2", LicenceMode.Corpus, LicenceStatus.Denied)]
  [TestCase("EUPL-1.2", LicenceMode.Distribution, LicenceStatus.Denied)]
  public void StatusOf_DeniesReferenceOnlyInADistribution(string licence, LicenceMode mode, LicenceStatus expected)
    => Assert.That(LicencePolicy.Parse(Policy(), "p.yaml", Decisions).StatusOf(licence, mode), Is.EqualTo(expected));

  [Test]
  public void Evaluate_AcceptsAWithOnlyForAListedException() {
    var policy = LicencePolicy.Parse(Policy(exceptions: "[Classpath-exception-2.0]"), "p.yaml", Decisions);
    Assert.That(policy.Evaluate(SpdxExpression.Parse("MIT WITH Classpath-exception-2.0"), LicenceMode.Distribution), Is.EqualTo(LicenceStatus.Allowed));
    Assert.That(policy.Evaluate(SpdxExpression.Parse("MIT WITH Other-exception"), LicenceMode.Distribution), Is.EqualTo(LicenceStatus.Denied));
  }

  [Test]
  public void OverridesRestrictedAndPending_AreFoundByPath() {
    var policy = LicencePolicy.Parse(
      Policy(overrides: "[{ glob: 'legacy/*.asm', licence: BSD-3-Clause, reason: 'header lost in 1998', reviewer: owner }]", pending: "[{ licence: EUPL-1.2, requestedBy: '#999', question: 'allow EUPL?' }]"),
      "p.yaml", Decisions);
    Assert.That(policy.Override("legacy/a.asm"), Is.EqualTo(policy.Overrides[0]));
    Assert.That(policy.Overrides[0] with { Glob = null! }, Is.EqualTo(new LicenceOverride(null!, "BSD-3-Clause", "header lost in 1998", "owner")));
    Assert.That(policy.Override("legacy/sub/a.asm"), Is.Null);
    Assert.That(policy.IsRestricted("models/deep/model.bin"), Is.True);
    Assert.That(policy.IsRestricted("model.binary"), Is.False);
    Assert.That(policy.Pending("EUPL-1.2"), Is.EqualTo(new PendingDecision("EUPL-1.2", "#999", "allow EUPL?")));
    Assert.That(policy.Pending("MIT"), Is.Null);
  }

  [TestCase("*.bin", "a/b/c.bin", true)]
  [TestCase("*.bin", "c.bin", true)]
  [TestCase("sub/**", "sub/x.c", true)]
  [TestCase("sub/**", "sub/deep/y.c", true)]
  [TestCase("sub/**", "other/sub/x.c", false)]
  [TestCase("**", "x", true)]
  public void Matches_TakesTheFileNameForAGlobWithoutSlash(string glob, string path, bool expected)
    => Assert.That(LicencePolicy.Matches(GlobPattern.Parse(glob), path), Is.EqualTo(expected));

  [Test]
  public void MalformedList_IsPly0026()
    => Assert.That(Refusal(Policy(allowed: "3")), Is.EqualTo("PLY0026: corpus manifest invalid: licence policy: 'allowed' must be a list of non-empty strings"));

  [Test]
  public void IdentifierAllowedAndPending_IsRefused()
    => Assert.That(Refusal(Policy(pending: "[{ licence: MIT, requestedBy: '#1', question: q }]")), Does.Contain("licence policy: 'MIT' awaits a decision but is listed in 'allowed'"));

  [Test]
  public void AllowedWithoutDecision_IsRefused()
    => Assert.That(Refusal(Policy(allowed: "[MIT, ISC]")), Does.Contain("licence policy: 'ISC' in 'allowed' has no row in DECISIONS.md"));

  [Test]
  public void DecisionNamingAnotherList_IsRefused_AndTheLastRowCounts() {
    Assert.That(Refusal(Policy(allowed: "[MIT, AGPL-3.0-only]", referenceOnly: "[]")), Does.Contain("'AGPL-3.0-only' is in 'allowed' but DECISIONS.md decided 'referenceOnly'"));
    var later = Decisions + "\n| `AGPL-3.0-only` | allowed | 2026-12-01 | changed | T |\n";
    Assert.That(LicencePolicy.Parse(Policy(allowed: "[MIT, AGPL-3.0-only]", referenceOnly: "[]"), "p.yaml", later).Allowed, Has.Count.EqualTo(2));
  }

  [Test]
  public void IdentifierInTwoLists_IsRefused()
    => Assert.That(Refusal(Policy(referenceOnly: "[AGPL-3.0-only, MIT]")), Does.Contain("'MIT' is listed in both 'allowed' and 'referenceOnly'"));

  [Test]
  public void EveryProblemIsReported_WithItsPlace() {
    var problems = Assert.Throws<CorpusException>(() => LicencePolicy.Parse(
      "schema: 2\nallowed: [MIT, 'not an id']\nreferenceOnly: []\nallowedExceptions: []\nrestrictedAssets: []\noverrides: [3, { glob: x, licence: 'MIT OR', reason: r, reviewer: v }, { glob: x, licence: MIT, reason: r }, { glob: x, licence: MIT, reason: '', reviewer: v, extra: 1 }]\npendingDecisions: {}\nsurplus: 1\n",
      "p.yaml", Decisions))!.Diagnostics;
    Assert.That(problems.Select(problem => problem.Message.Replace("corpus manifest invalid: licence policy: ", "")), Is.EqualTo(new[] {
      "unknown property 'surplus'",
      "unsupported schema '2'",
      "'not an id' in 'allowed' is not an SPDX identifier",
      "an entry of 'overrides' must be a mapping",
      "missing property 'reviewer' in an entry of 'overrides'",
      "'reason' of an entry of 'overrides' must be a non-empty string",
      "unknown property 'extra' in an entry of 'overrides'",
      "override licence 'MIT OR' does not parse: expected a licence after 'OR' but the expression ends",
      "'pendingDecisions' must be a list",
    }));
    Assert.That(problems[0].File, Is.EqualTo("p.yaml"));
    Assert.That(problems[0].Line, Is.EqualTo(8));
  }

  [Test]
  public void MissingProperty_IsRefused()
    => Assert.That(Refusal("schema: 1\nallowed: []\nreferenceOnly: []\nallowedExceptions: []\nrestrictedAssets: []\noverrides: []\n"), Does.Contain("missing property 'pendingDecisions'"));

  [TestCase("allowed: [", "licence policy: not YAML")]
  [TestCase("- a\n- b\n", "licence policy: the policy must be one YAML mapping")]
  [TestCase("", "licence policy: the policy must be one YAML mapping")]
  public void TextThatIsNoPolicy_IsRefused(string text, string message)
    => Assert.That(Refusal(text), Does.Contain(message));

  [Test]
  public void MissingSchemaAndList_AreRefused() {
    var refusal = Refusal("referenceOnly: []\nallowedExceptions: []\nrestrictedAssets: []\noverrides: []\npendingDecisions: []\n");
    Assert.That(refusal, Does.Contain("missing property 'schema'"));
    Assert.That(refusal, Does.Contain("missing property 'allowed'"));
  }

  [TestCase("[~]")]
  [TestCase("[null]")]
  [TestCase("['']")]
  [TestCase("[[MIT]]")]
  public void NullAndEmptyEntries_AreNoStrings(string allowed)
    => Assert.That(Refusal(Policy(allowed: allowed)), Does.Contain("'allowed' must be a list of non-empty strings"));

  [Test]
  public void QuotedTilde_IsAStringButNoIdentifier_AndAComplexKeyIsUnknown() {
    Assert.That(Refusal(Policy(allowed: "['~']")), Does.Contain("'~' in 'allowed' is not an SPDX identifier"));
    Assert.That(Refusal(Policy() + "? [a]\n: 1\n"), Does.Contain("unknown property '?'"));
  }

  [Test]
  public void DecisionsOutsideATableRow_AreIgnored()
    => Assert.That(Refusal(Policy(), "`MIT` | allowed | prose\n| `AGPL-3.0-only` |\n"), Does.Contain("'MIT' in 'allowed' has no row in DECISIONS.md"));

  [Test]
  public void Load_ReadsTheDecisionsBesideThePolicy_AndAMissingFileIsRefused() {
    var folder = Scratch.Create("polypiler-licence-policy-");
    try {
      var policy = Scratch.Write(folder, "policy.yaml", Policy());
      Assert.That(() => LicencePolicy.Load(policy), Throws.TypeOf<CorpusException>().With.Message.Contains("'MIT' in 'allowed' has no row in DECISIONS.md"));
      Scratch.Write(folder, LicencePolicy.DecisionsFile, Decisions);
      Assert.That(LicencePolicy.Load(policy).Allowed, Is.EqualTo(new[] { "MIT" }));
      Assert.That(() => LicencePolicy.Load(Path.Combine(folder, "missing.yaml")), Throws.TypeOf<CorpusException>().With.Message.Contains("licence policy: cannot read"));
    } finally {
      Scratch.Delete(folder);
    }
  }

}
