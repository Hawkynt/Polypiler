using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Emulators;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Running;
using static Polypiler.Tests.Verification.Oracles.Support.OracleTesting;

namespace Polypiler.Tests.Verification.Emulators;

/// <summary>
/// The PC-BASIC adapter and its oracle against a scripted runner: the command lines, the code page,
/// the normalisation and every way PC-BASIC can fail. What PC-BASIC itself does is tested in the
/// acceptance tests against PC-BASIC.
/// </summary>
[TestFixture]
public sealed class PcBasicTests {

  private static readonly byte[] _print1 = [0xFF, 0x09, 0x00, 0x0A, 0x00, 0x91, 0x20, 0x12, 0x00, 0x00, 0x00, 0x1A];
  private static readonly Diagnostic _timeLimit = Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 5000L);

  private static RunResult Wrote(params byte[] bytes)
    => Run(RunOutcome.Completed, 0) with { Collected = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { ["OUT"] = bytes } };

  private static RunResult Printed(params byte[] bytes) => Run(RunOutcome.Completed, 0) with { Stdout = bytes };

  private static PcBasicException Fails(Action action) => Assert.Throws<PcBasicException>(() => action())!;

  #region listing

  [Test]
  public void ToAscii_ListsWithTheConvertOption_AndNormalisesTheListing() {
    var runner = new ScriptedRunner(Wrote([.. "10 PRINT 1\r\n20 END\r\n"u8, 0x1A]));
    Assert.That(new PcBasic(runner).ToAscii(_print1), Is.EqualTo("10 PRINT 1\n20 END"));
    var plan = runner.Plans.Single();
    Assert.That(plan.Execute.ToString(), Is.EqualTo("pcbasic PROG.BAS OUT --convert=A"));
    Assert.That((plan.Files["PROG.BAS"].ToArray(), plan.Collect, plan.Build.Count, plan.Trust, plan.Limits), Is.EqualTo((_print1, new[] { "OUT" }, 0, TrustLevel.Untrusted, RunLimits.Default)));
    Assert.That(plan.Execute.Stdin.IsEmpty);
  }

  [Test]
  public void ToAscii_DecodesCodePage437()
    => Assert.That(new PcBasic(new ScriptedRunner(Wrote(0x31, 0x30, 0x20, 0x27, 0xC4, 0x81, 0xE1))).ToAscii(_print1), Is.EqualTo("10 '─üß"));

  /// <summary>Test design 6: the empty program, with and without the end-of-file byte, lists as nothing.</summary>
  [TestCase(new byte[] { 0xFF, 0x00, 0x00, 0x1A })]
  [TestCase(new byte[] { 0xFF, 0x00, 0x00 })]
  public void ToAscii_OfTheEmptyProgram_IsEmpty(byte[] empty)
    => Assert.That(new PcBasic(new ScriptedRunner(Wrote(0x1A))).ToAscii(empty), Is.Empty);

  /// <summary>Test design 9: PC-BASIC lists a corrupt file as nothing; that is a failure, never the empty program.</summary>
  [TestCase(new byte[] { 0xFF, 0x09 })]
  [TestCase(new byte[] { 0xFF, 0x00, 0x00, 0x00 })]
  [TestCase(new byte[] { 0x1A })]
  [TestCase(new byte[0])]
  public void ToAscii_OfACorruptFileListedAsNothing_IsPLY3960(byte[] corrupt) {
    var exception = Fails(() => new PcBasic(new ScriptedRunner(Wrote(0x1A))).ToAscii(corrupt));
    Assert.That((exception.Diagnostic.Code, exception.Diagnostic.ExitCode), Is.EqualTo(("PLY3960", (ExitCode?)ExitCode.OracleFailure)));
    Assert.That(exception.Message, Is.EqualTo("PC-BASIC conversion to ASCII failed: the token file lists as nothing, but it is not the empty program"));
  }

  [Test]
  public void ToAscii_WhenNothingWasWritten_IsPLY3960() {
    Assert.That(Fails(() => new PcBasic(new ScriptedRunner(Run(RunOutcome.Completed, 0))).ToAscii(_print1)).Message,
      Is.EqualTo("PC-BASIC conversion to ASCII failed: it wrote no output"));
    Assert.That(Fails(() => new PcBasic(new ScriptedRunner(Wrote())).ToAscii(_print1)).Message,
      Is.EqualTo("PC-BASIC conversion to ASCII failed: it wrote no output"));
  }

  [TestCase(2, "", "it exited with 2")]
  [TestCase(1, " boom \n", "it exited with 1: boom")]
  public void ToAscii_WhenPcBasicExitsNonZero_IsPLY3960(int exitCode, string stderr, string cause)
    => Assert.That(Fails(() => new PcBasic(new ScriptedRunner(Run(RunOutcome.Completed, exitCode, "", stderr))).ToAscii(_print1)).Message,
      Is.EqualTo("PC-BASIC conversion to ASCII failed: " + cause));

  [Test]
  public void ToAscii_WhenPcBasicPrintsAlotOnStandardError_CutsItOff()
    => Assert.That(Fails(() => new PcBasic(new ScriptedRunner(Run(RunOutcome.Completed, 1, "", new string('e', 500)))).ToAscii(_print1)).Message,
      Does.EndWith(": " + new string('e', 200) + "..."));

  [Test]
  public void ToAscii_WhenTheRunHitsALimit_IsPLY3960NamingTheRunnersDiagnostic()
    => Assert.That(Fails(() => new PcBasic(new ScriptedRunner(Run(RunOutcome.TimedOut, -1, "", "", _timeLimit))).ToAscii(_print1)).Message,
      Is.EqualTo("PC-BASIC conversion to ASCII failed: PLY0031: time limit of 5000 ms exceeded"));

  [Test]
  public void ToAscii_WhenTheRunIsRefusedWithoutADiagnostic_NamesTheOutcome()
    => Assert.That(Fails(() => new PcBasic(new ScriptedRunner(Run(RunOutcome.Refused, -1))).ToAscii(_print1)).Message,
      Is.EqualTo("PC-BASIC conversion to ASCII failed: the run ended Refused"));

  [Test]
  public void ToAscii_UsesTheGivenLimitsAndTrust() {
    var runner = new ScriptedRunner(Wrote("10 END"u8.ToArray()));
    var limits = new RunLimits(TimeSpan.FromSeconds(3), 1 << 20, 1 << 10, 4);
    _ = new PcBasic(runner) { ConversionLimits = limits, Trust = TrustLevel.Trusted }.ToAscii(_print1);
    Assert.That((runner.Plans[0].Limits, runner.Plans[0].Trust), Is.EqualTo((limits, TrustLevel.Trusted)));
  }

  #endregion

  #region tokenizing

  [Test]
  public void ToTokenized_WritesTheListingWithCrLf_AndReturnsTheTokenFile() {
    byte[] tokens = [0xFF, 0x76, 0x12, 0x0A, 0x00, 0x91, 0x20, 0x12, 0x00, 0x00, 0x00, 0x1A];
    var runner = new ScriptedRunner(Wrote(tokens));
    Assert.That(new PcBasic(runner).ToTokenized("10 PRINT \"─\"\n20 END\n"), Is.EqualTo(tokens));
    var plan = runner.Plans.Single();
    Assert.That(plan.Execute.ToString(), Is.EqualTo("pcbasic PROG.TXT OUT --convert=B"));
    Assert.That(plan.Files["PROG.TXT"].ToArray(), Is.EqualTo((byte[])[.. "10 PRINT \""u8, 0xC4, .. "\"\r\n20 END\r\n"u8]));
  }

  [TestCase("")]
  [TestCase(" \n")]
  public void ToTokenized_OfAnEmptyListing_IsTheEmptyProgram(string listing)
    => Assert.That(new PcBasic(new ScriptedRunner(Wrote(0xFF, 0x00, 0x00, 0x1A))).ToTokenized(listing), Is.EqualTo(new byte[] { 0xFF, 0x00, 0x00, 0x1A }));

  [Test]
  public void ToTokenized_WhenAListingBecomesTheEmptyProgram_IsPLY3960()
    => Assert.That(Fails(() => new PcBasic(new ScriptedRunner(Wrote(0xFF, 0x00, 0x00, 0x1A))).ToTokenized("PRINT 1")).Message,
      Is.EqualTo("PC-BASIC conversion to a token file failed: a listing that is not empty became the empty program"));

  [Test]
  public void ToTokenized_WhenTheResultIsNoTokenFile_IsPLY3960()
    => Assert.That(Fails(() => new PcBasic(new ScriptedRunner(Wrote("10 PRINT 1"u8.ToArray()))).ToTokenized("10 PRINT 1")).Message,
      Is.EqualTo("PC-BASIC conversion to a token file failed: the result does not start with the token-file mark FF but with 31"));

  [Test]
  public void ToTokenized_WhenNothingWasWritten_IsPLY3960()
    => Assert.That(Fails(() => new PcBasic(new ScriptedRunner(Run(RunOutcome.Completed, 0))).ToTokenized("10 PRINT 1")).Message,
      Is.EqualTo("PC-BASIC conversion to a token file failed: it wrote no output"));

  [Test]
  public void ToTokenized_OfNull_Throws()
    => Assert.That(() => new PcBasic(new ScriptedRunner()).ToTokenized(null!), Throws.ArgumentNullException);

  #endregion

  #region running

  [Test]
  public void Run_StartsPcBasicHeadless_AndSendsLineEndsAsCr() {
    var runner = new ScriptedRunner(Printed([.. "? 21\r\n 42 \r\n"u8]));
    var run = new PcBasic(runner).Run(_print1, "21\r\n5\nä", TimeSpan.FromSeconds(7));
    Assert.That((run.Outcome, run.Output, run.Succeeded, run.Diagnostics.Count), Is.EqualTo((RunOutcome.Completed, (string?)"? 21\n 42 ", true, 0)));
    var plan = runner.Plans.Single();
    Assert.That(plan.Execute.ToString(), Is.EqualTo("pcbasic PROG.BAS --interface=none --quit"));
    Assert.That(plan.Execute.Stdin.ToArray(), Is.EqualTo((byte[])[.. "21\r5\r"u8, 0x84]));
    Assert.That((plan.Files["PROG.BAS"].ToArray(), plan.Limits.WallTime, plan.Limits.MemoryBytes, plan.Collect.Count), Is.EqualTo((_print1, TimeSpan.FromSeconds(7), RunLimits.Default.MemoryBytes, 0)));
  }

  [Test]
  public void Run_WithoutInput_SendsNothing() {
    var runner = new ScriptedRunner(Printed());
    Assert.That(new PcBasic(runner).Run(_print1, null, TimeSpan.FromSeconds(1)).Output, Is.Empty);
    Assert.That(runner.Plans[0].Execute.Stdin.IsEmpty);
  }

  /// <summary>Test design 7: no output and a lone line end are both empty.</summary>
  [TestCase(new byte[0])]
  [TestCase(new byte[] { 0x0D, 0x0A })]
  public void Run_WithNoOutputOrALoneLineEnd_IsEmpty(byte[] stdout)
    => Assert.That(new PcBasic(new ScriptedRunner(Printed(stdout))).Run(_print1, null, TimeSpan.FromSeconds(1)).Output, Is.Empty);

  /// <summary>Test design 8: a run cut off by its limit has no output, PLY3960 and the runner's PLY0031.</summary>
  [Test]
  public void Run_WhenItTimesOut_HasNoOutput() {
    var run = new PcBasic(new ScriptedRunner(Run(RunOutcome.TimedOut, -1, " 1 ", "", _timeLimit))).Run(_print1, null, TimeSpan.FromSeconds(5));
    Assert.That((run.Outcome, run.Output, run.Succeeded), Is.EqualTo((RunOutcome.TimedOut, (string?)null, false)));
    Assert.That(run.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY3960", "PLY0031" }));
    Assert.That(run.Diagnostics[0].Message, Is.EqualTo("PC-BASIC run failed: PLY0031: time limit of 5000 ms exceeded"));
  }

  [Test]
  public void Run_WhenPcBasicExitsNonZero_HasNoOutput() {
    var run = new PcBasic(new ScriptedRunner(Run(RunOutcome.Completed, 1, " 1 ", "Traceback"))).Run(_print1, null, TimeSpan.FromSeconds(5));
    Assert.That((run.Outcome, run.Output), Is.EqualTo((RunOutcome.Completed, (string?)null)));
    Assert.That(run.Diagnostics.Single().Message, Is.EqualTo("PC-BASIC run failed: it exited with 1: Traceback"));
  }

  [Test]
  public void Run_WithANonPositiveLimit_Throws()
    => Assert.That(() => new PcBasic(new ScriptedRunner()).Run(_print1, null, TimeSpan.Zero), Throws.TypeOf<ArgumentOutOfRangeException>());

  #endregion

  #region normalisation and construction

  [TestCase("", "")]
  [TestCase("\r\n", "")]
  [TestCase("\n", "")]
  [TestCase(" 1 \r\n", " 1 ")]
  [TestCase("a\r\nb", "a\nb")]
  [TestCase("a\r\n\r\n", "a\n")]
  [TestCase("a\rb", "a\rb")]
  public void Normalise_TurnsCrLfIntoLf_AndDropsOneFinalLineEnd(string text, string expected)
    => Assert.That(PcBasic.Normalise(text), Is.EqualTo(expected));

  [Test]
  public void Normalise_OfNull_Throws() => Assert.That(() => PcBasic.Normalise(null!), Throws.ArgumentNullException);

  [Test]
  public void Construction_WithoutARunner_Throws() => Assert.That(() => new PcBasic(null!), Throws.ArgumentNullException);

  [Test]
  public void Exception_WithoutADiagnostic_Throws() => Assert.That(() => new PcBasicException(null!), Throws.ArgumentNullException);

  [Test]
  public void CodePage437_IsTheDosCodePage()
    => Assert.That((PcBasic.CodePage437.CodePage, PcBasic.CodePage437.GetString([0xB0, 0xDB, 0xE3])), Is.EqualTo((437, "░█π")));

  #endregion

  #region the oracle

  private static string Program() {
    var path = Path.Combine(Path.GetTempPath(), $"polypiler-pcbasic-{Guid.NewGuid():N}.bas");
    File.WriteAllBytes(path, _print1);
    return path;
  }

  [Test]
  public void Oracle_IsAnIndependentEmulator() {
    var oracle = new PcBasicOracle(new PcBasic(new ScriptedRunner()), "print1.bas", TimeSpan.FromSeconds(5));
    Assert.That((oracle.Id, oracle.Kind, oracle.Producer, oracle.ReferenceFile, oracle.Limit), Is.EqualTo(("pcbasic", OracleKind.Emulator, "pcbasic", (string?)"print1.bas", TimeSpan.FromSeconds(5))));
    Assert.That(OracleRegistry.IsIndependent(oracle));
  }

  [Test]
  public async Task Oracle_RunsTheProgramOncePerVector_WithItsInputOnStandardInput() {
    var path = Program();
    try {
      var runner = new ScriptedRunner(Printed([.. " 1 \r\n"u8]), Printed([.. " "u8, 0xE3, .. "\r\n"u8]));
      var oracle = new PcBasicOracle(new PcBasic(runner), path, TimeSpan.FromSeconds(5));
      var result = await oracle.EvaluateAsync(new OracleRequest("gwbasic.print1", [new TestVector("none", [], null), new TestVector("two", "2\n"u8.ToArray(), null)]));
      Assert.That((result.Succeeded, result.Outputs), Is.EqualTo((true, new[] { " 1 "u8.ToArray(), [0x20, 0xE3] })));
      Assert.That(runner.Plans.Select(plan => plan.Execute.Stdin.ToArray()), Is.EqualTo(new[] { Array.Empty<byte>(), "2\r"u8.ToArray() }));
      Assert.That(runner.Plans.Select(plan => plan.Files["PROG.BAS"].ToArray()), Has.All.EqualTo(_print1));
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public async Task Oracle_WhenARunFails_IsPLY0050AndStops() {
    var path = Program();
    try {
      var runner = new ScriptedRunner(Run(RunOutcome.TimedOut, -1, "", "", _timeLimit));
      var result = await new PcBasicOracle(new PcBasic(runner), path, TimeSpan.FromSeconds(5)).EvaluateAsync(new OracleRequest("gwbasic.forever", [new TestVector("a", [], null), new TestVector("b", [], null)]));
      Assert.That((result.Outputs.Count, runner.Plans.Count, result.Diagnostics.Single().Code), Is.EqualTo((0, 1, "PLY0050")));
      Assert.That(result.Diagnostics[0].Message, Is.EqualTo("reference oracle 'pcbasic' failed for 'gwbasic.forever': vector 'a': PLY3960: PC-BASIC run failed: PLY0031: time limit of 5000 ms exceeded; PLY0031: time limit of 5000 ms exceeded"));
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public async Task Oracle_WithoutItsProgram_IsPLY0050() {
    var runner = new ScriptedRunner();
    var result = await new PcBasicOracle(new PcBasic(runner), "absent-polypiler.bas", TimeSpan.FromSeconds(5)).EvaluateAsync(new OracleRequest("gwbasic.absent", []));
    Assert.That(result.Diagnostics.Single().Message, Is.EqualTo("reference oracle 'pcbasic' failed for 'gwbasic.absent': the program 'absent-polypiler.bas' does not exist"));
    Assert.That(runner.Plans, Is.Empty);
  }

  [Test]
  public void Oracle_WithInvalidArguments_Throws() {
    var pcBasic = new PcBasic(new ScriptedRunner());
    Assert.Multiple(() => {
      Assert.That(() => new PcBasicOracle(null!, "p.bas", TimeSpan.FromSeconds(1)), Throws.ArgumentNullException);
      Assert.That(() => new PcBasicOracle(pcBasic, null!, TimeSpan.FromSeconds(1)), Throws.ArgumentNullException);
      Assert.That(() => new PcBasicOracle(pcBasic, "p.bas", TimeSpan.Zero), Throws.TypeOf<ArgumentOutOfRangeException>());
      Assert.That(() => new PcBasicOracle(pcBasic, "p.bas", TimeSpan.FromTicks(-1)), Throws.TypeOf<ArgumentOutOfRangeException>());
      Assert.That(() => new PcBasicOracle(pcBasic, "p.bas", TimeSpan.FromSeconds(1)).EvaluateAsync(null!), Throws.ArgumentNullException);
    });
  }

  #endregion

}
