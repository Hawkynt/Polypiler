using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Emulators;
using Polypiler.Core.Verification.Running;
using static Polypiler.Tests.Verification.Oracles.Support.OracleTesting;
using static Polypiler.Tests.Verification.Running.Support.RunnerTesting;

namespace Polypiler.Tests.Verification.Emulators;

/// <summary>
/// The VICE runner's plan and its reading of a run, against a scripted runner. The monitor logs are
/// written in the format VICE 3.10 logs a tracepoint in; the acceptance tests run VICE itself.
/// </summary>
[TestFixture]
public sealed class ViceRunnerTests {

  /// <summary>The 15-byte <c>10 PRINT "HI"</c>.</summary>
  private static readonly byte[] _hello = [0x01, 0x08, 0x0C, 0x08, 0x0A, 0x00, 0x99, 0x20, 0x22, 0x48, 0x49, 0x22, 0x00, 0x00, 0x00];

  private static readonly byte[] _banner = Encoding.Latin1.GetBytes("\u0093\r    **** COMMODORE 64 BASIC V2 ****\r\r 64K RAM SYSTEM  38911 BASIC BYTES FREE\r");

  private static ViceRequest Hello(CbmMachine machine = CbmMachine.C64, string? input = null, params AddressRange[] ranges) => new(machine, _hello, input, [.. ranges], 20_000_000);

  /// <summary>A monitor log line of a tracepoint on an address, with the accumulator and the cycle.</summary>
  private static string Hit(ushort address, int checkpoint, byte a, long cycle)
    => $"#{checkpoint} (Trace  exec {address:x4})  295/$127,  52/$34\n.C:{address:x4}  48          PHA            - A:{a:X2} X:38 Y:00 SP:f5 N.-B...C    {cycle.ToString(CultureInfo.InvariantCulture)}\n";

  /// <summary>A log: each byte printed at the output address, a <c>|</c> marks an entry into the ready routine, logged by two checkpoints.</summary>
  private static string Log(ushort output, ushort ready, params object[] parts) {
    var log = new StringBuilder("#1 (Stop on  exec fce2)    0/$000,   6/$06\n.C:fce2  A2 FF       LDX #$FF       - A:00 X:00 Y:00 SP:00 ..-..IZ.          6\nTRACE: 1  C:$e716  (Trace exec)\n");
    var cycle = 2_000_000L;
    foreach (var part in parts)
      if (part is byte[] bytes) {
        foreach (var value in bytes)
          log.Append(Hit(output, 1, value, cycle += 300));
      } else {
        cycle += 300;
        log.Append(Hit(ready, 2, 0x80, cycle)).Append(Hit(ready, 3, 0x80, cycle)).Append("Executing: > d7ff 00\n");
      }

    return log.ToString();
  }

  private static readonly object _ready = "|";

  private static byte[] Petscii(string text) => Encoding.Latin1.GetBytes(text.Replace('\n', '\r'));

  private static string C64Log(params object[] parts) => Log(0xE716, 0xA474, parts);

  private static RunResult Completed(int exitCode, string log, string stdout = "", params (string Name, byte[] Bytes)[] files) {
    var collected = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [ViceRunner.LogFile] = Encoding.Latin1.GetBytes(log) };
    foreach (var (name, bytes) in files)
      collected[name] = bytes;

    return Run(RunOutcome.Completed, exitCode, stdout) with { Collected = collected };
  }

  private static readonly string _helloLog = C64Log(_banner, _ready, Petscii("\n\nREADY.\n\nRUN\nHI\n"), _ready);

  /// <summary>Test design 1: the program's output between the autostart's RUN and the next READY.</summary>
  [Test]
  public async Task AReturningProgram_ReportsItsScreenText() {
    var runner = new ScriptedRunner(Completed(0, _helloLog, "DBGCART: exit(0) cycles elapsed: 2014802\n"));
    var result = await new ViceRunner(runner, false).RunAsync(Hello());
    Assert.That((result.Status, result.ScreenText, result.Succeeded, result.Memory.Length), Is.EqualTo((ViceStatus.ReturnedToBasic, "HI", true, 0)));
    Assert.That(result.Banner, Is.EqualTo("{$93}\n    **** COMMODORE 64 BASIC V2 ****\n\n 64K RAM SYSTEM  38911 BASIC BYTES FREE"));
    Assert.That(result.Cycles, Is.EqualTo(2_000_000L + 300 * (_banner.Length + 1 + 17 + 1)));
  }

  [Test]
  public async Task ThePlan_HoldsTheProgramAndTheMonitorCommands_AndCollectsTheLogAndMemory() {
    var runner = new ScriptedRunner(Completed(0, _helloLog, "", ("MEM0.BIN", [0xFE]), ("MEM1.BIN", [1, 2])));
    var request = Hello(CbmMachine.C64, null, new(0xD020, 1), new(0x0801, 2)) with { Trust = TrustLevel.Trusted, Limits = Limits(TimeSpan.FromSeconds(5)) };
    var result = await new ViceRunner(runner, false).RunAsync(request);
    var plan = runner.Plans.Single();
    Assert.That(plan.Files[ViceRunner.ProgramFile].ToArray(), Is.EqualTo(_hello));
    Assert.That(Encoding.ASCII.GetString(plan.Files[ViceRunner.MonitorFile].Span), Is.EqualTo(ViceRunner.MonitorCommands(request)));
    Assert.That((plan.Trust, plan.Limits.WallTime, plan.Build.Count), Is.EqualTo((TrustLevel.Trusted, TimeSpan.FromSeconds(5), 0)));
    Assert.That(plan.Collect, Is.EqualTo(new[] { "monitor.log", "MEM0.BIN", "MEM1.BIN" }));
    Assert.That(result.Memory.Select(block => (block.Start, Hex(block.Bytes))), Is.EqualTo(new[] { ((ushort)0xD020, "fe"), ((ushort)0x0801, "0102") }));
  }

  private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

  [Test]
  public void TheMonitorCommands_TraceOutputAndReady_SaveEachRange_AndExitThroughTheDebugCartridge() {
    var commands = ViceRunner.MonitorCommands(Hello(CbmMachine.C64, null, new(0xD020, 1), new(0x0400, 40)));
    Assert.That(commands.Split('\n'), Is.EqualTo(new[] {
      "trace exec $e716",
      "trace exec $a474",
      "trace exec $a474", "ignore 3 1", "command 3 \"bsave \\\"MEM0.BIN\\\" 0 d020 d020\"",
      "trace exec $a474", "ignore 4 1", "command 4 \"bsave \\\"MEM1.BIN\\\" 0 0400 0427\"",
      "trace exec $a474", "ignore 5 1", "command 5 \"> d7ff 00\"",
      ""
    }));
  }

  /// <summary>Test design 3: each machine is its emulator, configuration and ROM addresses.</summary>
  [TestCase(CbmMachine.C64, "vice", "", "trace exec $e716|trace exec $a474|trace exec $a474|ignore 3 1|command 3 \"> d7ff 00\"")]
  [TestCase(CbmMachine.Vic20Unexpanded, "vice-xvic", "-memory none ", "trace exec $e742|trace exec $c474|trace exec $c474|ignore 3 1|command 3 \"> 910f 00\"")]
  [TestCase(CbmMachine.Vic20Plus3K, "vice-xvic", "-memory 3k ", "trace exec $e742|trace exec $c474|trace exec $c474|ignore 3 1|command 3 \"> 910f 00\"")]
  [TestCase(CbmMachine.Vic20Plus8K, "vice-xvic", "-memory 8k ", "trace exec $e742|trace exec $c474|trace exec $c474|ignore 3 1|command 3 \"> 910f 00\"")]
  [TestCase(CbmMachine.C128, "vice-x128", "-40col ", "bank io|trace exec $c72d|trace exec $4d37|trace exec $4d37|ignore 3 1|command 3 \"> d7ff 00\"")]
  public void EachMachine_HasItsEmulatorAndAddresses(CbmMachine machine, string toolchain, string options, string commands) {
    var request = Hello(machine);
    var command = new ViceRunner(new ScriptedRunner(), false).Command(request);
    Assert.That(command.ToString(), Is.EqualTo($"{toolchain} -default -warp -sounddev dummy -debugcart {options}-autostartprgmode 1 -autostart PROG.PRG -moncommands trace.mon -monlog -monlogname monitor.log -limitcycles 20000000"));
    Assert.That(ViceRunner.MonitorCommands(request).TrimEnd('\n').Replace('\n', '|'), Is.EqualTo(commands));
  }

  [TestCase(CbmMachine.C64, "x64sc")]
  [TestCase(CbmMachine.Vic20Plus8K, "xvic")]
  [TestCase(CbmMachine.C128, "x128")]
  public void UnderAVirtualDisplay_XvfbRunStartsTheEmulator(CbmMachine machine, string binary) {
    var command = new ViceRunner(new ScriptedRunner(), true).Command(Hello(machine));
    Assert.That((command.Toolchain, command.Arguments[0], command.Arguments[1], command.Arguments[3], command.Arguments[4]), Is.EqualTo(("xvfb-run", "-a", "-n", binary, "-default")));
    Assert.That(int.Parse(command.Arguments[2], CultureInfo.InvariantCulture), Is.InRange(200, 899));
    Assert.That(command.Environment.ContainsKey("__EGL_VENDOR_LIBRARY_FILENAMES"), Is.EqualTo(File.Exists(ViceRunner.MesaEglVendor)));
  }

  [Test]
  public void TheDefaultRunner_UsesAVirtualDisplayOnLinuxOnly() {
    Assert.That(new ViceRunner(new ScriptedRunner()).VirtualDisplay, Is.EqualTo(OperatingSystem.IsLinux()));
    Assert.That(() => new ViceRunner(null!), Throws.ArgumentNullException);
  }

  /// <summary>Test design 5: keyboard input is typed through VICE's keyboard buffer.</summary>
  [Test]
  public void KeyboardInput_IsTypedAfterRun() {
    var command = new ViceRunner(new ScriptedRunner(), false).Command(Hello(CbmMachine.C64, "Xy1\n"));
    Assert.That(command.Arguments.TakeLast(2), Is.EqualTo(new[] { "-keybuf", "xY1\\n" }));
    Assert.That(new ViceRunner(new ScriptedRunner(), false).Command(Hello(CbmMachine.C64, "")).Arguments, Does.Not.Contain("-keybuf"));
  }

  [TestCase("A\\B")]
  [TestCase("\t")]
  [TestCase("é")]
  public void KeyboardInput_OutsideTheTypeableCharacters_IsRefused(string input) {
    Assert.That(() => Hello(CbmMachine.C64, input), Throws.ArgumentException.With.Property("ParamName").EqualTo("KeyboardInput"));
  }

  [Test]
  public async Task ARequest_IsChecked() {
    Assert.That(() => new ViceRequest((CbmMachine)99, _hello, null, [], 1), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(() => new ViceRequest(CbmMachine.C64, _hello, null, [], 0), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(new ViceRequest(CbmMachine.C64, _hello, null, default, 1).ReadAfterRun, Is.Empty);
    Assert.That((Hello().Name, Hello().RequireReturn, Hello().Trust), Is.EqualTo(("PROG.PRG", false, TrustLevel.Untrusted)));
    Assert.That(() => ViceRunner.MonitorCommands(null!), Throws.ArgumentNullException);
    Assert.That(() => ViceRunner.EmulatorArguments(null!), Throws.ArgumentNullException);
    Assert.That(() => ViceRunner.KeyboardBuffer(null!), Throws.ArgumentNullException);
    Assert.That(() => new ViceRunner(new ScriptedRunner(), false).Command(null!), Throws.ArgumentNullException);
    await Assert.ThatAsync(() => new ViceRunner(new ScriptedRunner(), false).RunAsync(null!), Throws.ArgumentNullException);
  }

  /// <summary>An address range holds one byte at least and ends at $FFFF at most.</summary>
  [TestCase(0x0000, 1, true)]
  [TestCase(0xFFFF, 1, true)]
  [TestCase(0x0000, 0x10000, true)]
  [TestCase(0xFFFF, 2, false)]
  [TestCase(0x0001, 0x10000, false)]
  [TestCase(0x0400, 0, false)]
  [TestCase(0x0400, -1, false)]
  public void AnAddressRange_StaysInTheAddressSpace(int start, int length, bool valid) {
    if (valid)
      Assert.That(new AddressRange((ushort)start, length).End, Is.EqualTo((ushort)(start + length - 1)));
    else
      Assert.That(() => new AddressRange((ushort)start, length), Throws.InstanceOf<ArgumentOutOfRangeException>());
  }

  [Test]
  public void AResult_AndAMemoryBlock_CheckTheirParts() {
    Assert.That(() => new ViceResult(ViceStatus.CycleLimit, "", [], null!), Throws.ArgumentNullException);
    Assert.That(() => new MemoryBlock(0, null!), Throws.ArgumentNullException);
  }

  /// <summary>Test design 7: an empty program prints nothing and returns.</summary>
  [Test]
  public async Task AnEmptyProgram_ReturnsWithNoText() {
    var runner = new ScriptedRunner(Completed(0, C64Log(_banner, _ready, Petscii("\n\nREADY.\n\nRUN\n"), _ready)));
    var result = await new ViceRunner(runner, false).RunAsync(new(CbmMachine.C64, new byte[] { 0x01, 0x08, 0x00, 0x00 }, null, [], 20_000_000));
    Assert.That((result.Status, result.ScreenText), Is.EqualTo((ViceStatus.ReturnedToBasic, "")));
  }

  /// <summary>Test design 8: forty characters on one line are one line, whatever the screen wraps.</summary>
  [Test]
  public async Task AFullScreenLine_IsOneLine() {
    var runner = new ScriptedRunner(Completed(0, C64Log(_banner, _ready, Petscii("\nRUN\n" + new string('A', 40) + "\n"), _ready)));
    var result = await new ViceRunner(runner, false).RunAsync(Hello());
    Assert.That(result.ScreenText, Is.EqualTo(new string('A', 40)));
  }

  /// <summary>Test design 10: a program that never returns ends at the cycle limit, and is never reported as returned.</summary>
  [Test]
  public async Task AProgramThatDoesNotReturn_EndsAtTheCycleLimit() {
    var log = C64Log(_banner, _ready, Petscii("\nREADY.\nRUN\nTICK\n"));
    var runner = new ScriptedRunner(Completed(1, log, "Main CPU: Error - cycle limit reached.\n"));
    var result = await new ViceRunner(runner, false).RunAsync(Hello());
    Assert.That((result.Status, result.ScreenText, result.Succeeded, result.Cycles), Is.EqualTo((ViceStatus.CycleLimit, "TICK", true, 20_000_000L)));
  }

  [Test]
  public async Task AProgramThatDoesNotReturn_IsPLY3935_WhereTheReturnIsRequired() {
    var runner = new ScriptedRunner(Completed(1, C64Log(_banner, _ready, Petscii("\nRUN\n")), "Main CPU: Error - cycle limit reached.\n"));
    var result = await new ViceRunner(runner, false).RunAsync(Hello() with { RequireReturn = true, Name = "forever.prg" });
    var diagnostic = result.Diagnostics.Single();
    Assert.That((result.Status, diagnostic.Code, diagnostic.ExitCode), Is.EqualTo((ViceStatus.CycleLimit, "PLY3935", (ExitCode?)ExitCode.OracleFailure)));
    Assert.That(diagnostic.Message, Is.EqualTo("VICE run of 'forever.prg' failed: BASIC printed no READY. before the cycle limit of 20000000"));
  }

  /// <summary>Test design 11: a PRG without a line chain is never run.</summary>
  [TestCase(new byte[] { 0x01, 0x08, 0xFF }, "the line at $0801 is cut off before its next-line address")]
  [TestCase(new byte[] { 0x01 }, "the PRG has 1 bytes, fewer than its load address needs")]
  [TestCase(new byte[] { }, "the PRG has 0 bytes, fewer than its load address needs")]
  [TestCase(new byte[] { 0x01, 0x08, 0x0C, 0x08, 0x0A }, "the line at $0801 is cut off before its end")]
  [TestCase(new byte[] { 0x01, 0x08, 0x0C, 0x08, 0x0A, 0x00, 0x99 }, "the line at $0801 is cut off before its end")]
  [TestCase(new byte[] { 0x01, 0x08, 0x01, 0x08, 0x0A, 0x00, 0x99, 0x00, 0x00, 0x00 }, "the line at $0801 links to $0801, which is not a later address in the program")]
  [TestCase(new byte[] { 0x01, 0x08, 0x00, 0x09, 0x0A, 0x00, 0x99, 0x00, 0x00, 0x00 }, "the line at $0801 links to $0900, which is not a later address in the program")]
  [TestCase(new byte[] { 0x01, 0x08, 0x08, 0x08, 0x0A, 0x00, 0x99, 0x00, 0xFF }, "the line at $0808 is cut off before its next-line address")]
  public async Task AMalformedPrg_IsPLY3935_WithoutARun(byte[] prg, string cause) {
    var runner = new ScriptedRunner();
    var result = await new ViceRunner(runner, false).RunAsync(new(CbmMachine.C64, prg, null, [], 20_000_000) { RequireReturn = true });
    Assert.That((result.Status, result.Diagnostics.Single().Code, result.Diagnostics.Single().Message, runner.Plans.Count), Is.EqualTo((ViceStatus.EmulatorFailed, "PLY3935", "VICE run of 'PROG.PRG' failed: " + cause, 0)));
  }

  [TestCase(new byte[] { 0x01, 0x08, 0x00, 0x00 })]
  [TestCase(new byte[] { 0x01, 0x08, 0x0C, 0x08, 0x0A, 0x00, 0x99, 0x20, 0x22, 0x48, 0x49, 0x22, 0x00, 0x00, 0x00 })]
  [TestCase(new byte[] { 0x01, 0x10, 0x07, 0x10, 0x0A, 0x00, 0x80, 0x00, 0x00, 0x00, 0xA9, 0x00 })]
  public void AWellFormedPrg_HasNoProblem(byte[] prg) {
    Assert.That(BasicProgram.Problem(prg), Is.Null);
  }

  /// <summary>Test design 13: a toolchain the runner cannot find is its PLY0021, followed by PLY3935.</summary>
  [Test]
  public async Task ARefusedRun_PassesTheRunnersDiagnosticOn() {
    var missing = new Diagnostic("PLY0021", DiagnosticSeverity.Error, "toolchain 'vice' is not installed");
    var runner = new ScriptedRunner(Run(RunOutcome.Refused, -1, "", "", missing));
    var result = await new ViceRunner(runner, false).RunAsync(Hello());
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0021", "PLY3935" }));
    Assert.That((result.Status, result.Diagnostics[1].Message), Is.EqualTo((ViceStatus.EmulatorFailed, "VICE run of 'PROG.PRG' failed: PLY0021: toolchain 'vice' is not installed")));
  }

  [TestCase(1, "Sound: Warning - Sound buffer overflow\nVICE: Error - ROM image not found\nSound: Warning\n", "the emulator exited with 1: VICE: Error - ROM image not found")]
  [TestCase(0, "", "the emulator exited with 0: it printed nothing")]
  [TestCase(1, "", "the emulator exited with 1: it printed nothing")]
  public async Task AnEmulatorThatEndsOtherwise_IsPLY3935(int exitCode, string stdout, string cause) {
    var runner = new ScriptedRunner(Completed(exitCode, C64Log(_banner, _ready, Petscii("\nRUN\n")), stdout));
    var result = await new ViceRunner(runner, false).RunAsync(Hello());
    Assert.That((result.Status, result.Diagnostics.Single().Message), Is.EqualTo((ViceStatus.EmulatorFailed, "VICE run of 'PROG.PRG' failed: " + cause)));
    Assert.That(result.Banner, Does.Contain("38911 BASIC BYTES FREE"));
  }

  [Test]
  public async Task AReturnWithoutRun_IsNotAReturn() {
    var runner = new ScriptedRunner(Completed(0, C64Log(_banner, _ready, Petscii("\nREADY.\n"), _ready)));
    var result = await new ViceRunner(runner, false).RunAsync(Hello());
    Assert.That((result.Status, result.ScreenText), Is.EqualTo((ViceStatus.EmulatorFailed, "")));
  }

  [Test]
  public async Task ALogWithoutReady_HasOnlyABanner() {
    var runner = new ScriptedRunner(Run(RunOutcome.Completed, 1, "cycle limit reached") with { Collected = new Dictionary<string, ReadOnlyMemory<byte>> { [ViceRunner.LogFile] = Encoding.Latin1.GetBytes(C64Log(_banner)) } });
    var result = await new ViceRunner(runner, false).RunAsync(Hello());
    Assert.That((result.Status, result.Banner.Length > 0, result.ScreenText), Is.EqualTo((ViceStatus.CycleLimit, true, "")));
  }

  [Test]
  public async Task NoLog_IsNoReturn() {
    var runner = new ScriptedRunner(Run(RunOutcome.Completed, 0, ""));
    var result = await new ViceRunner(runner, false).RunAsync(Hello());
    Assert.That((result.Status, result.Banner), Is.EqualTo((ViceStatus.EmulatorFailed, "")));
  }

  [TestCase(null)]
  [TestCase(new byte[] { 1, 2 })]
  public async Task MemoryThatWasNotSaved_IsPLY3935(byte[]? saved) {
    var files = saved is null ? Array.Empty<(string, byte[])>() : [("MEM0.BIN", saved)];
    var runner = new ScriptedRunner(Completed(0, _helloLog, "", files));
    var result = await new ViceRunner(runner, false).RunAsync(Hello(CbmMachine.C64, null, new AddressRange(0xD020, 1)));
    Assert.That((result.Status, result.Diagnostics.Single().Message), Is.EqualTo((ViceStatus.EmulatorFailed, "VICE run of 'PROG.PRG' failed: the memory at $d020 was not saved")));
  }

  [Test]
  public async Task AVic20Log_IsReadAtItsOwnAddresses() {
    var banner = Petscii("\u0093**** CBM BASIC V2 ****\n3583 BYTES FREE\n");
    var runner = new ScriptedRunner(Completed(0, Log(0xE742, 0xC474, banner, _ready, Petscii("\nRUN\nHI\n"), _ready)));
    var result = await new ViceRunner(runner, false).RunAsync(Hello(CbmMachine.Vic20Unexpanded));
    Assert.That((result.Status, result.ScreenText, result.Banner), Is.EqualTo((ViceStatus.ReturnedToBasic, "HI", "{$93}**** CBM BASIC V2 ****\n3583 BYTES FREE")));
  }

}
