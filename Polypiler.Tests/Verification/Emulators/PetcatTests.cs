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
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Verification.Emulators;

/// <summary>The petcat runs and the two Commodore oracles, against a scripted runner; petcat and VICE themselves run in the acceptance tests.</summary>
[TestFixture]
public sealed class PetcatTests {

  private static readonly byte[] _hello = [0x01, 0x08, 0x0C, 0x08, 0x0A, 0x00, 0x99, 0x20, 0x22, 0x48, 0x49, 0x22, 0x00, 0x00, 0x00];

  [Test]
  public async Task Detokenize_ListsThePrgWithTheDialectsVersion_AndNormalises() {
    var runner = new ScriptedRunner(Run(RunOutcome.Completed, 0, "\n   10 print \"hi\"\n"));
    var result = await new Petcat(runner).DetokenizeAsync(_hello, "c64-basic-v2");
    Assert.That((result.Succeeded, result.Listing, result.Prg.Length), Is.EqualTo((true, "10 PRINT \"HI\"", 0)));
    var plan = runner.Plans.Single();
    Assert.That((plan.Execute.ToString(), plan.Trust, plan.Files["PROG.PRG"].ToArray()), Is.EqualTo(("petcat -2 -nh -- PROG.PRG", TrustLevel.Untrusted, _hello)));
  }

  [Test]
  public async Task Tokenize_WritesTheListingAsPetcatReadsIt_AndCollectsThePrg() {
    var runner = new ScriptedRunner(Run(RunOutcome.Completed, 0) with { Collected = new Dictionary<string, ReadOnlyMemory<byte>> { ["PROG.PRG"] = _hello } });
    var petcat = new Petcat(runner) { Trust = TrustLevel.Trusted, Limits = Limits(TimeSpan.FromSeconds(3)) };
    var result = await petcat.TokenizeAsync("10 PRINT \"{$93}HI\"", "c128-basic-7", 0x1C01);
    Assert.That((result.Succeeded, result.Listing, result.Prg.ToArray()), Is.EqualTo((true, "", _hello)));
    var plan = runner.Plans.Single();
    Assert.That(plan.Execute.ToString(), Is.EqualTo("petcat -w70 -l 1c01 -o PROG.PRG -- LIST.TXT"));
    Assert.That(Encoding.Latin1.GetString(plan.Files["LIST.TXT"].Span), Is.EqualTo("10 print \"{$93}hi\"\n"));
    Assert.That((plan.Collect.Single(), plan.Trust, plan.Limits.WallTime), Is.EqualTo(("PROG.PRG", TrustLevel.Trusted, TimeSpan.FromSeconds(3))));
  }

  /// <summary>Test design 12: a dialect petcat is not run for is PLY3936, without a run.</summary>
  [Test]
  public async Task AnUnknownDialect_IsPLY3936() {
    var runner = new ScriptedRunner();
    var petcat = new Petcat(runner);
    var listed = await petcat.DetokenizeAsync(_hello, "pet-basic-4");
    var tokenized = await petcat.TokenizeAsync("10 END", "pet-basic-4", 0x0401);
    Assert.That(listed.Diagnostics.Single().Message, Is.EqualTo("petcat detokenize failed for dialect 'pet-basic-4': petcat is run only for c128-basic-7, c64-basic-v2, vic20-basic-v2"));
    Assert.That((tokenized.Diagnostics.Single().Code, tokenized.Diagnostics.Single().ExitCode, tokenized.Succeeded, runner.Plans.Count), Is.EqualTo(("PLY3936", (ExitCode?)ExitCode.OracleFailure, false, 0)));
  }

  [TestCase(RunOutcome.Completed, 255, "", "error: line 10 - unknown control code: $C1}\"\n", "petcat exited with 255: error: line 10 - unknown control code: $C1}\"")]
  [TestCase(RunOutcome.Completed, 0, "", "\nError: line 10 - something\n", "Error: line 10 - something")]
  [TestCase(RunOutcome.TimedOut, -1, "", "", "PLY0031: time limit of 5 ms exceeded")]
  public async Task AFailingPetcat_IsPLY3936(RunOutcome outcome, int exitCode, string stdout, string stderr, string cause) {
    var diagnostics = outcome == RunOutcome.TimedOut ? new[] { Diagnostic.Create(RunnerDiagnostics.TimeLimitExceeded, null, 5L) } : [];
    var petcat = new Petcat(new ScriptedRunner(Run(outcome, exitCode, stdout, stderr, diagnostics)));
    var listed = await petcat.DetokenizeAsync(_hello, "vic20-basic-v2");
    var tokenized = await petcat.TokenizeAsync("10 END", "vic20-basic-v2", 0x1001);
    Assert.That(listed.Diagnostics.Single().Message, Is.EqualTo("petcat detokenize failed for dialect 'vic20-basic-v2': " + cause));
    Assert.That(tokenized.Diagnostics.Single().Message, Is.EqualTo("petcat tokenize failed for dialect 'vic20-basic-v2': " + cause));
  }

  [Test]
  public async Task APetcatThatWritesNoPrg_IsPLY3936() {
    var result = await new Petcat(new ScriptedRunner(Run(RunOutcome.Completed, 0))).TokenizeAsync("10 END", "c64-basic-v2", 0x0801);
    Assert.That(result.Diagnostics.Single().Message, Is.EqualTo("petcat tokenize failed for dialect 'c64-basic-v2': petcat wrote no PRG"));
  }

  [Test]
  public async Task Arguments_AreChecked() {
    var petcat = new Petcat(new ScriptedRunner());
    Assert.That(() => new Petcat(null!), Throws.ArgumentNullException);
    await Assert.ThatAsync(() => petcat.DetokenizeAsync(_hello, null!), Throws.ArgumentNullException);
    await Assert.ThatAsync(() => petcat.TokenizeAsync(null!, "c64-basic-v2", 0x0801), Throws.ArgumentNullException);
    await Assert.ThatAsync(() => petcat.TokenizeAsync("10 END", null!, 0x0801), Throws.ArgumentNullException);
    Assert.That(() => new PetcatResult("", ReadOnlyMemory<byte>.Empty, null!), Throws.ArgumentNullException);
  }

  [Test]
  public async Task ThePetcatOracle_ListsEachVectorsPrg() {
    var runner = new ScriptedRunner(Run(RunOutcome.Completed, 0, "   10 print \"hi\"\n"), Run(RunOutcome.Completed, 0, "   10 end\n"));
    var oracle = new PetcatOracle("petcat", new Petcat(runner), "c64-basic-v2");
    var result = await oracle.EvaluateAsync(new("cbm.hello", [new("hello", _hello, null), new("end", [1, 8], null)]));
    Assert.That(result.Outputs.Select(output => Encoding.ASCII.GetString(output)), Is.EqualTo(new[] { "10 PRINT \"HI\"", "10 END" }));
    Assert.That((oracle.Id, oracle.Kind, oracle.Producer, oracle.ReferenceFile, OracleRegistry.IsIndependent(oracle)), Is.EqualTo(("petcat", OracleKind.Emulator, "petcat", (string?)null, true)));
    Assert.That(runner.Plans[1].Files["PROG.PRG"].ToArray(), Is.EqualTo(new byte[] { 1, 8 }));
  }

  [Test]
  public async Task AFailingPetcatOracle_IsPLY0050() {
    var oracle = new PetcatOracle("petcat", new Petcat(new ScriptedRunner()), "pet-basic-4");
    var result = await oracle.EvaluateAsync(new("cbm.hello", [new("hello", _hello, null)]));
    Assert.That(result.Diagnostics.Single().Message, Is.EqualTo("reference oracle 'petcat' failed for 'cbm.hello': vector 'hello': PLY3936: petcat detokenize failed for dialect 'pet-basic-4': petcat is run only for c128-basic-7, c64-basic-v2, vic20-basic-v2"));
    Assert.That(() => new PetcatOracle(null!, new Petcat(new ScriptedRunner()), "c64-basic-v2"), Throws.ArgumentNullException);
    Assert.That(() => new PetcatOracle("p", null!, "c64-basic-v2"), Throws.ArgumentNullException);
    Assert.That(() => new PetcatOracle("p", new Petcat(new ScriptedRunner()), null!), Throws.ArgumentNullException);
    await Assert.ThatAsync(() => oracle.EvaluateAsync(null!), Throws.ArgumentNullException);
  }

  private static RunResult Returned(string screen) {
    var log = new StringBuilder();
    var cycle = 1000L;
    void Hit(string address, char a) => log.Append($".C:{address}  48          PHA            - A:{(int)a:X2} X:00 Y:00 SP:f5 ..-B...C    {++cycle}\n");
    Hit("a474", '\0');
    foreach (var character in "RUN\r" + screen.Replace('\n', '\r'))
      Hit("e716", character);

    Hit("a474", '\0');
    return Run(RunOutcome.Completed, 0) with { Collected = new Dictionary<string, ReadOnlyMemory<byte>> { ["monitor.log"] = Encoding.Latin1.GetBytes(log.ToString()) } };
  }

  [Test]
  public async Task TheViceOracle_RunsTheProgramOncePerVector_WithItsInputTyped() {
    var program = Path.Combine(TestContext.CurrentContext.WorkDirectory, "vice-oracle-hello.prg");
    await File.WriteAllBytesAsync(program, _hello);
    try {
      var runner = new ScriptedRunner(Returned("HI\n"), Returned("? X\nX\n"));
      var oracle = new ViceOracle("vice", new ViceRunner(runner, false), CbmMachine.C64, program, 20_000_000) { Trust = TrustLevel.Trusted };
      var result = await oracle.EvaluateAsync(new("cbm.hello", [new("plain", [], null), new("typed", "X\n"u8.ToArray(), null)]));
      Assert.That(result.Outputs.Select(output => Encoding.ASCII.GetString(output)), Is.EqualTo(new[] { "HI", "? X\nX" }));
      Assert.That(runner.Plans.Select(plan => plan.Execute.Arguments.Contains("-keybuf")), Is.EqualTo(new[] { false, true }));
      Assert.That((runner.Plans[0].Trust, runner.Plans[0].Files["PROG.PRG"].ToArray()), Is.EqualTo((TrustLevel.Trusted, _hello)));
      Assert.That((oracle.Id, oracle.Kind, oracle.Producer, oracle.ReferenceFile, OracleRegistry.IsIndependent(oracle)), Is.EqualTo(("vice", OracleKind.Emulator, "vice", program, true)));
    } finally {
      File.Delete(program);
    }
  }

  [Test]
  public async Task AViceOracle_RequiresTheReturnToBasic() {
    var program = Path.Combine(TestContext.CurrentContext.WorkDirectory, "vice-oracle-forever.prg");
    await File.WriteAllBytesAsync(program, _hello);
    try {
      var runner = new ScriptedRunner(Run(RunOutcome.Completed, 1, "Main CPU: Error - cycle limit reached."));
      var oracle = new ViceOracle("vice", new ViceRunner(runner, false), CbmMachine.C64, program, 1000);
      var result = await oracle.EvaluateAsync(new("cbm.forever", [new("plain", [], null)]));
      Assert.That(result.Diagnostics.Single().Message, Is.EqualTo("reference oracle 'vice' failed for 'cbm.forever': vector 'plain': PLY3935: VICE run of 'vice-oracle-forever.prg' failed: BASIC printed no READY. before the cycle limit of 1000"));
      var typed = await oracle.EvaluateAsync(new("cbm.forever", [new("tab", "\t"u8.ToArray(), null)]));
      Assert.That(typed.Diagnostics.Single().Message, Does.StartWith("reference oracle 'vice' failed for 'cbm.forever': vector 'tab': keyboard input is printable ASCII"));
    } finally {
      File.Delete(program);
    }
  }

  [Test]
  public async Task AViceOracleWithoutItsProgram_IsPLY0050() {
    var oracle = new ViceOracle("vice", new ViceRunner(new ScriptedRunner(), false), CbmMachine.C64, "absent.prg", 1000);
    var result = await oracle.EvaluateAsync(new("cbm.absent", [new("plain", [], null)]));
    Assert.That(result.Diagnostics.Single().Message, Is.EqualTo("reference oracle 'vice' failed for 'cbm.absent': the program 'absent.prg' does not exist"));
    Assert.That(() => new ViceOracle(null!, new ViceRunner(new ScriptedRunner(), false), CbmMachine.C64, "a.prg", 1), Throws.ArgumentNullException);
    Assert.That(() => new ViceOracle("v", null!, CbmMachine.C64, "a.prg", 1), Throws.ArgumentNullException);
    Assert.That(() => new ViceOracle("v", new ViceRunner(new ScriptedRunner(), false), CbmMachine.C64, null!, 1), Throws.ArgumentNullException);
    await Assert.ThatAsync(() => oracle.EvaluateAsync(null!), Throws.ArgumentNullException);
  }

}
