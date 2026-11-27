using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Interpreter;
using Polypiler.Tests.MachineSemantics.Support;

namespace Polypiler.Tests.MachineSemantics;

[TestFixture]
public sealed class X64MachineProfileTests {

  private static readonly (string Root, string Doubleword, string Word, string LowByte, string? HighByte)[] _aliases = [
    ("rax", "eax", "ax", "al", "ah"),
    ("rcx", "ecx", "cx", "cl", "ch"),
    ("rdx", "edx", "dx", "dl", "dh"),
    ("rbx", "ebx", "bx", "bl", "bh"),
    ("rsp", "esp", "sp", "spl", null),
    ("rbp", "ebp", "bp", "bpl", null),
    ("rsi", "esi", "si", "sil", null),
    ("rdi", "edi", "di", "dil", null),
    ("r8", "r8d", "r8w", "r8b", null),
    ("r9", "r9d", "r9w", "r9b", null),
    ("r10", "r10d", "r10w", "r10b", null),
    ("r11", "r11d", "r11w", "r11b", null),
    ("r12", "r12d", "r12w", "r12b", null),
    ("r13", "r13d", "r13w", "r13b", null),
    ("r14", "r14d", "r14w", "r14b", null),
    ("r15", "r15d", "r15w", "r15b", null)
  ];

  [Test]
  public void CatalogDefinesTheIntegerRegisterFile() {
    var profile = Load();
    Assert.That(profile.AddressBits, Is.EqualTo(64));
    Assert.That(profile.ByteOrder, Is.EqualTo(ByteOrder.Little));
    Assert.That(profile.Registers, Has.Length.EqualTo(70));
    Assert.That(profile.Registers.Where(register => register.IsRoot).Select(register => register.Name),
      Is.EquivalentTo(_aliases.Select(alias => alias.Root).Concat(["rip", "rflags"])));
    Assert.That(profile.Registers.Where(register => register.IsRoot).Select(register => register.Bits), Is.All.EqualTo(64));
    Assert.That(profile.Register("rsp")!.Class, Is.EqualTo(RegisterClass.StackPointer));
    Assert.That(profile.Register("rip")!.Class, Is.EqualTo(RegisterClass.ProgramCounter));
    Assert.That(profile.Register("rflags")!.Class, Is.EqualTo(RegisterClass.Status));
    Assert.That(profile.Register("rbp")!.Class, Is.EqualTo(RegisterClass.General));

    foreach (var (root, doubleword, word, lowByte, highByte) in _aliases) {
      CheckAlias(profile, doubleword, root, 32, 0, SubRegisterWrite.ZeroExtend);
      CheckAlias(profile, word, root, 16, 0, SubRegisterWrite.Preserve);
      CheckAlias(profile, lowByte, root, 8, 0, SubRegisterWrite.Preserve);
      if (highByte is not null)
        CheckAlias(profile, highByte, root, 8, 8, SubRegisterWrite.Preserve);
    }
  }

  [Test]
  public void RegisterWritesMatchThePinnedNativeObservations() {
    var profile = Load();
    var directory = Path.Combine(MachineTesting.Fixtures, "x64", "registers");
    var source = Path.Combine(directory, "register-writes.c");
    var trace = Path.Combine(directory, "register-writes.txt");
    Assert.That(Sha256(source), Is.EqualTo("121b3904068e78b96c59a6bd59787bf718a301d47b98ddcc72383464bc911e2b"));
    Assert.That(Sha256(trace), Is.EqualTo("7ecd8ae92b530b97920bf4a5cd53e195e21d7641e293a6373c1d55f335ce9fef"));

    foreach (var line in File.ReadLines(trace)) {
      var fields = line.Split(' ');
      var root = fields[0];
      var alias = fields[1];
      var width = profile.Register(alias)!.Bits;
      var module = MachineTesting.Verified($"machine x64\n@f {{ entry: RegWrite.u{width} @{alias}, 0x{fields[3]}; Ret }}");
      CheckExecution(module, root, ParseHex(fields[2]), root, ParseHex(fields[4]));
    }
  }

  [TestCase("rax", "ah", 8, 0x77UL)]
  [TestCase("rax", "al", 8, 0x88UL)]
  [TestCase("r15", "r15d", 32, 0x55667788UL)]
  [TestCase("rsp", "spl", 8, 0x88UL)]
  public void RegisterReadsSelectOnlyTheAliasBits(string root, string alias, int width, ulong expected) {
    var module = MachineTesting.Verified(
      $"machine x64\n@f {{ entry: %value = RegRead.u{width} @{alias}; %wide = ZeroExtend.u64 %value; RegWrite.u64 @r10, %wide; Ret }}");
    CheckExecution(module, root, 0x1122334455667788UL, "r10", expected);
  }

  [TestCase("cf", 0)]
  [TestCase("pf", 2)]
  [TestCase("af", 4)]
  [TestCase("zf", 6)]
  [TestCase("sf", 7)]
  [TestCase("df", 10)]
  [TestCase("of", 11)]
  public void RflagsAndItsNamedFieldsAgree(string flag, int bit) {
    var profile = Load();
    Assert.That(profile.Flags, Has.Length.EqualTo(7));
    Assert.That(profile.Flag(flag), Is.EqualTo(new FlagDescriptor(flag, "rflags", bit)));

    var state = new MachineState(profile).SetRegister("rflags", (1UL << bit) | 2UL);
    Assert.That(state.Flags.Where(entry => entry.Value is true).Select(entry => entry.Key), Is.EqualTo(new[] { flag }));
    state.SetFlag(flag, false);
    Assert.That(state.Register("rflags"), Is.EqualTo(2UL));
  }

  [TestCase("r16", 64, "PLY4000")]
  [TestCase("xmm0", 64, "PLY4000")]
  [TestCase("eax", 16, "PLY4001")]
  public void InvalidRegisterAccessIsRefused(string register, int width, string code)
    => Assert.That(MachineTesting.Codes($"machine x64\n@f {{ entry: RegWrite.u{width} @{register}, 1; Ret }}"),
      Is.EqualTo(new[] { code }));

  private static IsaProfile Load() {
    var profile = MachineTesting.Catalog.Find("x64");
    Assert.That(profile, Is.Not.Null);
    return profile!;
  }

  private static void CheckAlias(IsaProfile profile, string name, string root, int width, int offset, SubRegisterWrite write) {
    var alias = profile.Register(name);
    Assert.That(alias, Is.EqualTo(new RegisterDescriptor(name, width, root, offset, write, RegisterClass.General)));
    Assert.That(profile.Root(alias!).Name, Is.EqualTo(root), name);
  }

  private static void CheckExecution(MachineModule module, string root, ulong initial, string observed, ulong expected) {
    foreach (var form in new[] { module, MachineNormalizer.ToRootRegisters(module) }) {
      var run = MachineInterpreter.Run(form, "f", new MachineState(form.Profile!).SetRegister(root, initial));
      Assert.That(run.Diagnostics, Is.Empty);
      Assert.That(run.Trace.Final.Registers[observed], Is.EqualTo(expected), MachineSyntax.Print(form));
    }
  }

  private static ulong ParseHex(string value) => ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

  private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

}
