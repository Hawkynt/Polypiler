using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Emulators;
using Polypiler.Core.Verification.Oracles;
using static Polypiler.Tests.Acceptance.Retro.CbmTools;

namespace Polypiler.Tests.Acceptance.Retro;

/// <summary>
/// The known-answer programs of the VICE runner and petcat on the genuine ROMs: the start-up banner of
/// each machine, which proves the ROMs and the memory configuration, keyboard input, the boundaries of
/// the screen text and of the cycle limit, and petcat's escapes and dialects.
/// </summary>
[TestFixture, Category("ExternalInterop")]
public sealed class ViceRunnerTests {

  /// <summary>A BASIC program at a load address: each line numbered 10, 20, … from its tokenized bytes, linked, and ended.</summary>
  private static byte[] Basic(ushort load, params byte[][] lines) {
    var prg = new List<byte> { (byte)load, (byte)(load >> 8) };
    var number = 0;
    foreach (var line in lines) {
      var next = load + prg.Count - 2 + 4 + line.Length + 1;
      number += 10;
      prg.AddRange([(byte)next, (byte)(next >> 8), (byte)number, (byte)(number >> 8), .. line, 0]);
    }

    prg.AddRange([0, 0]);
    return [.. prg];
  }

  private static byte[] Tokens(params object[] parts) => [.. parts.SelectMany(part => part as byte[] ?? Encoding.ASCII.GetBytes((string)part))];

  private const byte Print = 0x99;

  private static byte[] Hello(ushort load) => Basic(load, Tokens(new[] { Print }, " \"HI\""));

  /// <summary>Test design 3: each machine prints its own banner and runs the program at its BASIC start.</summary>
  [TestCase(CbmMachine.C64, (ushort)0x0801, "38911 BASIC BYTES FREE")]
  [TestCase(CbmMachine.Vic20Unexpanded, (ushort)0x1001, "3583 BYTES FREE")]
  [TestCase(CbmMachine.Vic20Plus3K, (ushort)0x0401, "6655 BYTES FREE")]
  [TestCase(CbmMachine.Vic20Plus8K, (ushort)0x1201, "11775 BYTES FREE")]
  [TestCase(CbmMachine.C128, (ushort)0x1C01, "122365 BYTES FREE")]
  public void EachMachine_RunsOnItsOwnRoms(CbmMachine machine, ushort load, string banner) {
    var result = Run(Vice(machine), Request(machine, Hello(load), 30_000_000) with { RequireReturn = true });
    Assert.That((result.Status, result.ScreenText, result.Diagnostics.Count), Is.EqualTo((ViceStatus.ReturnedToBasic, "HI", 0)), string.Join("; ", result.Diagnostics));
    Assert.That(result.Banner, Does.Contain(banner));
  }

  [Test]
  public void TheC64Hello_IsTheFixture() {
    Assert.That(Hello(0x0801), Is.EqualTo(Fixture("hello.prg")));
  }

  /// <summary>Test design 5: keyboard input reaches INPUT.</summary>
  [Test]
  public void KeyboardInput_ReachesInput() {
    var program = Basic(0x0801, Tokens(new byte[] { 0x85 }, " A$:", new[] { Print }, " A$"));
    var result = Run(Vice(CbmMachine.C64), Request(CbmMachine.C64, program, input: "X\n"));
    // INPUT prompts with "?" and a cursor-right, which the text form writes as {$1D}
    Assert.That((result.Status, result.ScreenText), Is.EqualTo((ViceStatus.ReturnedToBasic, "?{$1D}X\nX")));
  }

  /// <summary>Test design 7: an empty program prints nothing and returns.</summary>
  [Test]
  public void AnEmptyProgram_PrintsNothing() {
    var result = Run(Vice(CbmMachine.C64), Request(CbmMachine.C64, [0x01, 0x08, 0x00, 0x00]));
    Assert.That((result.Status, result.ScreenText), Is.EqualTo((ViceStatus.ReturnedToBasic, "")));
  }

  /// <summary>Test design 8: forty characters fill one screen line without a wrap in the text.</summary>
  [Test]
  public void AFullScreenLine_IsOneLine() {
    var program = Basic(0x0801, Tokens(new[] { Print }, " \"" + new string('A', 40) + "\""));
    var result = Run(Vice(CbmMachine.C64), Request(CbmMachine.C64, program));
    Assert.That((result.Status, result.ScreenText), Is.EqualTo((ViceStatus.ReturnedToBasic, new string('A', 40))));
  }

  /// <summary>Test design 9: a limit a million cycles short of the program's end is a cycle limit, a million beyond it a return.</summary>
  [Test]
  public void TheCycleLimit_AroundTheProgramsEnd() {
    var program = Basic(0x0801, Tokens(new byte[] { 0x81 }, "I", new byte[] { 0xB2 }, "1", new byte[] { 0xA4 }, "3000:", new byte[] { 0x82 }));
    var vice = Vice(CbmMachine.C64);
    var calibration = Run(vice, Request(CbmMachine.C64, program, 50_000_000));
    Assert.That(calibration.Status, Is.EqualTo(ViceStatus.ReturnedToBasic));
    var shorter = Run(vice, Request(CbmMachine.C64, program, calibration.Cycles - 1_000_000));
    var longer = Run(vice, Request(CbmMachine.C64, program, calibration.Cycles + 1_000_000));
    Assert.That((shorter.Status, longer.Status), Is.EqualTo((ViceStatus.CycleLimit, ViceStatus.ReturnedToBasic)));
    Assert.That(longer.Cycles, Is.LessThanOrEqualTo(calibration.Cycles + 1_000_000));
  }

  /// <summary>Test design 4 on the real machine: the POKE reaches the VIC-II.</summary>
  [Test]
  public void MemoryIsReadAfterTheRun() {
    var result = Run(Vice(CbmMachine.C64), Request(CbmMachine.C64, Fixture("poke.prg"), ranges: [new(0xD020, 1), new(0x0801, 4)]));
    Assert.That(result.Memory.Select(block => (block.Start, block.Bytes.Length)), Is.EqualTo(new[] { ((ushort)0xD020, 1), ((ushort)0x0801, 4) }));
    Assert.That((result.Memory[0].Bytes[0] & 0x0F, result.Memory[1].Bytes), Is.EqualTo((1, Fixture("poke.prg")[2..6])));
  }

  /// <summary>Test design 2: the fixture listing and the fixture PRG agree both ways.</summary>
  [Test]
  public async Task Petcat_ListsAndTokenizesTheFixture() {
    var petcat = Petcat();
    var listing = await petcat.DetokenizeAsync(Fixture("hello.prg"), "c64-basic-v2");
    Assert.That(listing.Listing, Is.EqualTo(Encoding.ASCII.GetString(Fixture("hello.txt")).TrimEnd('\n')));
    var prg = await petcat.TokenizeAsync(listing.Listing, "c64-basic-v2", 0x0801);
    Assert.That(prg.Prg.ToArray(), Is.EqualTo(Fixture("hello.prg")));
  }

  /// <summary>Test design 6 with petcat itself: control codes in a string round-trip through the escapes.</summary>
  [Test]
  public async Task Petcat_EscapesRoundTrip() {
    var petcat = Petcat();
    const string Listing = "10 PRINT \"{$93}{$11}HI{$C1}{$FF}\"";
    var prg = await petcat.TokenizeAsync(Listing, "c64-basic-v2", 0x0801);
    Assert.That(prg.Prg.ToArray()[6..15], Is.EqualTo(new byte[] { 0x99, 0x20, 0x22, 0x93, 0x11, 0x48, 0x49, 0xC1, 0xFF }));
    var listing = await petcat.DetokenizeAsync(prg.Prg, "c64-basic-v2");
    Assert.That(listing.Listing, Is.EqualTo(Listing));
  }

  /// <summary>The C128 dialect tokenizes BASIC 7's own keywords.</summary>
  [Test]
  public async Task Petcat_TokenizesTheC128Dialect() {
    var prg = await Petcat().TokenizeAsync("10 GRAPHIC 1", "c128-basic-7", 0x1C01);
    Assert.That(prg.Prg.ToArray(), Is.EqualTo(new byte[] { 0x01, 0x1C, 0x09, 0x1C, 0x0A, 0x00, 0xDE, 0x20, 0x31, 0x00, 0x00, 0x00 }));
  }

  /// <summary>Both oracles agree with the known answers of the fixture.</summary>
  [Test]
  public async Task TheOracles_GiveTheKnownAnswers() {
    var vice = new ViceOracle("vice", Vice(CbmMachine.C64), CbmMachine.C64, System.IO.Path.Combine(FixtureFolder, "hello.prg"), 20_000_000) { Trust = Core.Verification.Running.TrustLevel.Trusted };
    var screen = await vice.EvaluateAsync(new("cbm.hello", [new("plain", [], null)]));
    var petcat = new PetcatOracle("petcat", Petcat(), "c64-basic-v2");
    var listing = await petcat.EvaluateAsync(new("cbm.hello", [new("hello", Fixture("hello.prg"), null)]));
    Assert.That((Encoding.ASCII.GetString(screen.Outputs.Single()), Encoding.ASCII.GetString(listing.Outputs.Single())), Is.EqualTo(("HI", "10 PRINT \"HI\"")));
  }

}
