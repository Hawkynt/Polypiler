using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Oracles;
using static Polypiler.Tests.Verification.Oracles.Support.OracleTesting;

namespace Polypiler.Tests.Verification.Oracles;

/// <summary>The registry, its independence rule, and the verification that resolves an oracle through it.</summary>
[TestFixture]
public sealed class OracleRegistryTests {

  private string _directory = "";

  [SetUp]
  public void CreateDirectory() => Directory.CreateDirectory(this._directory = Path.Combine(Path.GetTempPath(), "polypiler-oracle-" + Guid.NewGuid().ToString("N")));

  [TearDown]
  public void DeleteDirectory() => Directory.Delete(this._directory, true);

  private static readonly OracleResult _adler = new([[0x02, 0x4d, 0x01, 0x27]], []);

  private static TestVector Abc(byte[]? expected = null) => new("abc", "abc"u8.ToArray(), expected);

  [Test]
  public void AnIndependentOracle_IsRegisteredAndResolved() {
    var registry = new OracleRegistry();
    var oracle = new FixedOracle(_adler, "rfc1950", AdlerVectors);
    Assert.That(registry.Register("cipher.adler32", oracle), Is.True);
    Assert.That(registry.Resolve("cipher.adler32"), Is.SameAs(oracle));
    Assert.That(registry.Resolve("cipher.crc32"), Is.Null);
    Assert.That(() => registry.Register("cipher.adler32", oracle), Throws.ArgumentException.With.Message.Contains("already has the oracle 'fixed'"));
  }

  /// <summary>Test design 18: a reference Polypiler produced is refused, by its producer or by where it lies.</summary>
  [Test]
  public void ASelfProducedReference_IsRefused() {
    var nested = Directory.CreateDirectory(Path.Combine(this._directory, "out", "deep")).FullName;
    File.WriteAllText(Path.Combine(this._directory, "out", OracleRegistry.OutputManifest), "{}");
    var registry = new OracleRegistry();
    Assert.That(registry.Register("a", new FixedOracle(_adler, "Polypiler")), Is.False);
    Assert.That(registry.Register("b", new FixedOracle(_adler, "rfc1950", Path.Combine(nested, "x.vectors.json"))), Is.False);
    Assert.That(registry.Register("c", new FixedOracle(_adler, "rfc1950", Path.Combine(this._directory, "x.vectors.json"))), Is.True, "beside the output directory, not in it");
    Assert.That(registry.Register("d", new FixedOracle(_adler, "rfc1950")), Is.True, "no file at all");
    Assert.That((registry.Resolve("a"), registry.Resolve("b")), Is.EqualTo(((IVerificationOracle?)null, (IVerificationOracle?)null)));
  }

  /// <summary>Test design 17 and 18: verifying a fixture without an oracle is PLY0063, exit 7, nothing passed.</summary>
  [Test]
  public async Task VerifyingWithoutAnOracle_IsPLY0063() {
    var registry = new OracleRegistry();
    registry.Register("cipher.adler32", new FixedOracle(_adler, OracleRegistry.SelfProducer));
    var result = await OracleVerification.VerifyAsync(registry, "cipher.adler32", [Abc()], [[1]], new ExactnessPolicy.Exact());
    Assert.That(result.Diagnostics.Single().Message, Is.EqualTo("no independent oracle is registered for 'cipher.adler32'"));
    Assert.That((result.Diagnostics[0].Code, result.ExitCode, result.Report.Passed, result.Report.Total), Is.EqualTo(("PLY0063", ExitCode.OracleFailure, 0, 1)));
  }

  [Test]
  public async Task AReferenceAgreeingWithItsVectors_IsComparedWithTheCandidate() {
    var registry = new OracleRegistry();
    registry.Register("cipher.adler32", new FixedOracle(_adler));
    var result = await OracleVerification.VerifyAsync(registry, "cipher.adler32", [Abc([0x02, 0x4d, 0x01, 0x27])], [[0x02, 0x4d, 0x01, 0x27]], new ExactnessPolicy.Exact());
    Assert.That((result.Report.Verified, result.Diagnostics.Count, result.ExitCode), Is.EqualTo((true, 0, ExitCode.Success)));
    var failing = await OracleVerification.VerifyAsync(registry, "cipher.adler32", [Abc()], [[0x02, 0x4d, 0x01, 0x28]], new ExactnessPolicy.Exact());
    Assert.That((failing.Diagnostics.Single().Code, failing.ExitCode), Is.EqualTo(("PLY0051", ExitCode.SemanticMismatch)));
  }

  /// <summary>Test design 15: a reference that disagrees with its own published vectors is broken, not the candidate.</summary>
  [Test]
  public async Task AReferenceDisagreeingWithItsVectors_IsPLY0050() {
    var registry = new OracleRegistry();
    registry.Register("cipher.adler32", new FixedOracle(_adler));
    var result = await OracleVerification.VerifyAsync(registry, "cipher.adler32", [Abc([0x02, 0x4d, 0x01, 0x28])], [[0x02, 0x4d, 0x01, 0x27]], new ExactnessPolicy.Exact());
    Assert.That(result.Diagnostics.Single().Message, Is.EqualTo("reference oracle 'fixed' failed for 'cipher.adler32': vector 'abc': the reference computed 024d0127, the published value is 024d0128"));
    Assert.That((result.ExitCode, result.Report.Passed, result.Report.Outcomes.Count), Is.EqualTo((ExitCode.OracleFailure, 0, 0)));
  }

  [Test]
  public async Task AReferenceWithTooFewOutputs_IsPLY0050() {
    var registry = new OracleRegistry();
    registry.Register("f", new FixedOracle(new OracleResult([], [])));
    var result = await OracleVerification.VerifyAsync(registry, "f", [Abc()], [[1]], new ExactnessPolicy.Exact());
    Assert.That(result.Diagnostics.Single().Message, Does.EndWith("it computed 0 outputs for 1 vectors"));
  }

  [Test]
  public async Task AFailedReference_PassesItsDiagnosticsOn() {
    var registry = new OracleRegistry();
    registry.Register("f", new FixedOracle(OracleResult.Failed("node", "f", "it crashed")));
    var result = await OracleVerification.VerifyAsync(registry, "f", [Abc()], [[1]], new ExactnessPolicy.Exact());
    Assert.That(result.Diagnostics.Single().Message, Is.EqualTo("reference oracle 'node' failed for 'f': it crashed"));
    Assert.That(result.Report.Verified, Is.False);
  }

  /// <summary>Test design 9: the published-vectors oracle answers with the file's values and fails without one.</summary>
  [Test]
  public async Task TheVectorFileOracle_AnswersWithThePublishedValues() {
    var file = VectorFile.Load(AdlerVectors);
    var oracle = new VectorFileOracle(file);
    Assert.That((oracle.Id, oracle.Kind, oracle.Producer, oracle.ReferenceFile), Is.EqualTo(("adler32.vectors.json", OracleKind.PublishedVectors, "rfc1950", AdlerVectors)));
    var result = await oracle.EvaluateAsync(new OracleRequest(file.Fixture, file.Vectors));
    Assert.That(result.Outputs.Select(output => Hex.Format(output)), Is.EqualTo(new[] { "00000001", "00620062", "024d0127", "b90f2ae4" }));
    var missing = await oracle.EvaluateAsync(new OracleRequest("f", [Abc()]));
    Assert.That((missing.Succeeded, missing.Outputs.Count, missing.Diagnostics.Single().Message), Is.EqualTo((false, 0, "reference oracle 'adler32.vectors.json' failed for 'f': vector 'abc' has no published value")));
  }

  [Test]
  public void NullArguments_AreRefused() {
    var registry = new OracleRegistry();
    Assert.That(() => registry.Register(null!, new FixedOracle(_adler)), Throws.ArgumentNullException);
    Assert.That(() => registry.Register("f", null!), Throws.ArgumentNullException);
    Assert.That(() => registry.Resolve(null!), Throws.ArgumentNullException);
    Assert.That(() => OracleRegistry.IsIndependent(null!), Throws.ArgumentNullException);
    Assert.That(() => new VectorFileOracle(null!), Throws.ArgumentNullException);
    Assert.That(() => new VectorFileOracle(VectorFile.Load(AdlerVectors)).EvaluateAsync(null!), Throws.ArgumentNullException);
    Assert.That(() => new OracleRequest(null!, []), Throws.ArgumentNullException);
    Assert.That(() => new OracleRequest("f", null!), Throws.ArgumentNullException);
    Assert.That(async () => await OracleVerification.VerifyAsync(null!, "f", [], [], new ExactnessPolicy.Exact()), Throws.ArgumentNullException);
    Assert.That(async () => await OracleVerification.VerifyAsync(registry, null!, [], [], new ExactnessPolicy.Exact()), Throws.ArgumentNullException);
    Assert.That(async () => await OracleVerification.VerifyAsync(registry, "f", null!, [], new ExactnessPolicy.Exact()), Throws.ArgumentNullException);
  }

}
