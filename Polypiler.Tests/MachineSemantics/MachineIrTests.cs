using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.MachineSemantics;
using Polypiler.Tests.MachineSemantics.Support;

namespace Polypiler.Tests.MachineSemantics;

/// <summary>
/// MachineIR itself: profiles, the text form with its exact round trip, and the verifier's rules
/// <c>PLY4000</c>–<c>PLY4006</c>. The rows of the test design of #139 are named where they are tested.
/// </summary>
[TestFixture]
public sealed class MachineIrTests {

  private static string M32(string body) => "machine test-m32\n\n" + body;

  // --- profiles -------------------------------------------------------------------------------

  [Test]
  public void TestM32_HasItsRootsSubRegistersAndFlags() {
    var profile = MachineTesting.Profile("test-m32");
    Assert.That(profile.AddressBits, Is.EqualTo(32));
    Assert.That(profile.ByteOrder, Is.EqualTo(ByteOrder.Little));
    Assert.That(profile.InterruptVectors, Is.EqualTo(256));
    Assert.That(profile.Registers.Where(register => register.IsRoot).Select(register => register.Name), Is.EqualTo(new[] { "r0", "r1", "r2", "r3", "sp", "pc" }));
    Assert.That(profile.Register("r0h"), Is.EqualTo(new RegisterDescriptor("r0h", 8, "r0", 8, SubRegisterWrite.Preserve, RegisterClass.General)));
    Assert.That(profile.Register("sp")!.Class, Is.EqualTo(RegisterClass.StackPointer));
    Assert.That(profile.Flags.Select(flag => flag.Name), Is.EqualTo(new[] { "c", "z", "s", "o" }));
    Assert.That(profile.Register("r9"), Is.Null);
    Assert.That(profile.Flag("q"), Is.Null);
  }

  [Test]
  public void TestM64_ZeroExtendsItsDoubleWord() {
    var profile = MachineTesting.Profile("test-m64");
    Assert.That(profile.Register("r0d")!.Write, Is.EqualTo(SubRegisterWrite.ZeroExtend));
    Assert.That(profile.ReplacesRoot(profile.Register("r0d")!), Is.True);
    Assert.That(profile.ReplacesRoot(profile.Register("r0l")!), Is.False);
    Assert.That(profile.InterruptVectors, Is.Null);
  }

  [Test]
  public void Mos6502_KeepsItsFlagsInP() {
    var profile = MachineTesting.Profile("mos6502");
    Assert.That(profile.AddressBits, Is.EqualTo(16));
    Assert.That(profile.Registers.Select(register => (register.Name, register.Bits)), Is.EqualTo(new[] { ("A", 8), ("X", 8), ("Y", 8), ("SP", 8), ("PC", 16), ("P", 8) }));
    Assert.That(profile.Register("P")!.Class, Is.EqualTo(RegisterClass.Status));
    Assert.That(profile.Flags.Select(flag => (flag.Name, flag.Register, flag.Bit)), Is.EqualTo(new[] {
      ("C", (string?)"P", 0), ("Z", "P", 1), ("I", "P", 2), ("D", "P", 3), ("B", "P", 4), ("V", "P", 6), ("N", "P", 7)
    }));
  }

  /// <summary>The 6502 profile and the instruction table name the same registers and flags.</summary>
  [Test]
  public void Mos6502_AgreesWithItsInstructionTable() {
    var table = File.ReadAllText(Path.Combine(Polypiler.Prototype.Testing.PrototypeFixture.RepositoryRoot, "spec", "binary", "mos6502", "instructions.yaml"));
    var profile = MachineTesting.Profile("mos6502");
    foreach (var register in profile.Registers)
      Assert.That(table, Does.Contain($"{{name: {register.Name}, bits: {register.Bits}}}"));
    Assert.That(table, Does.Contain("flags: [" + string.Join(", ", profile.Flags.Reverse().Select(flag => flag.Name)) + "]"));
  }

  [Test]
  public void X86Fixtures_NestAndFlattenSubRegisters() {
    var x86 = MachineTesting.Profile("x86-32");
    Assert.That(x86.Root(x86.Register("ah")!).Name, Is.EqualTo("eax"));
    Assert.That(x86.OffsetInRoot(x86.Register("ah")!), Is.EqualTo(8));
    var x64 = MachineTesting.Profile("x86-64");
    Assert.That(x64.ReplacesRoot(x64.Register("eax")!), Is.True);
    Assert.That(x64.ReplacesRoot(x64.Register("ax")!), Is.False);
  }

  [Test]
  public void IsaProfileLoad_ReadsAFile() {
    var profile = IsaProfile.Load(Path.Combine(MachineTesting.Fixtures, "mach-ir", "profiles", "x86-32.yaml"));
    Assert.That(profile.Id, Is.EqualTo("x86-32"));
  }

  [Test]
  public void Catalog_ReturnsNullForUnknownAndMalformedIds() {
    Assert.That(MachineTesting.Catalog.Find("nosuch"), Is.Null);
    Assert.That(MachineTesting.Catalog.Find("../spec"), Is.Null);
    Assert.That(MachineTesting.Catalog.Find(""), Is.Null);
    var first = MachineTesting.Catalog.Find("test-m32");
    Assert.That(MachineTesting.Catalog.Find("test-m32"), Is.SameAs(first));
  }

  private static string ProfileError(string yaml) {
    var exception = Assert.Throws<MachineException>(() => ProfileLoader.Parse(yaml))!;
    Assert.That(exception.Diagnostic.Code, Is.EqualTo("PLY4003"));
    return exception.Diagnostic.Message;
  }

  private const string Head = "isa: t\naddressBits: 32\nbyteOrder: little\n";

  /// <summary>Test design 9: a sub-register one bit beyond its root.</summary>
  [Test]
  public void SubRegisterBeyondItsRoot_IsRefused()
    => Assert.That(ProfileError(Head + "registers: [{name: r0, bits: 32}, {name: x, bits: 8, parent: r0, offset: 25}]"),
      Is.EqualTo("machine profile 't': sub-register 'x' (bits 25..32) exceeds its parent 'r0' of 32 bits"));

  /// <summary>Test design 9, inside: the same sub-register ending on the top bit loads.</summary>
  [Test]
  public void SubRegisterEndingOnTheTopBit_Loads()
    => Assert.That(ProfileLoader.Parse(Head + "registers: [{name: r0, bits: 32}, {name: x, bits: 8, parent: r0, offset: 24}]").Register("x")!.BitOffset, Is.EqualTo(24));

  [TestCase("registers: [{name: r0, bits: 32}, {name: r0, bits: 32}]", "duplicate name 'r0'")]
  [TestCase("registers: [{name: r0, bits: 32}]\nflags: [{name: r0}]", "duplicate name 'r0'")]
  [TestCase("registers: [{name: a, bits: 8, parent: b}, {name: b, bits: 8, parent: a}]", "register 'a' is part of a cycle of parents")]
  [TestCase("registers: [{name: a, bits: 8, parent: q}]", "register 'a' names the unknown parent 'q'")]
  [TestCase("registers: [{name: a, bits: 65}]", "register 'a' has 65 bits; a register has 1 to 64")]
  [TestCase("registers: [{name: a, bits: 0}]", "register 'a' has 0 bits; a register has 1 to 64")]
  [TestCase("registers: [{name: a, bits: 8}]\nflags: [{name: f, register: q, bit: 0}]", "flag 'f' names the unknown register 'q'")]
  [TestCase("registers: [{name: a, bits: 8}]\nflags: [{name: f, register: a, bit: 8}]", "flag 'f' is bit 8 of 'a', which has 8 bits")]
  [TestCase("registers: [{name: a, bits: 8}]\nflags: [{name: f, bit: 1}]", "flag 'f' has a bit but no register")]
  [TestCase("registers: [{name: a, bits: 8, offset: 1}]", "root register 'a' has no parent; offset and write belong to sub-registers")]
  [TestCase("registers: [{name: a, bits: 8, class: weird}]", "a.class 'weird' is not a register class")]
  [TestCase("registers: [{name: a, bits: 8}, {name: b, bits: 4, parent: a, write: sometimes}]", "b.write must be 'preserve' or 'zero-extend', not 'sometimes'")]
  [TestCase("registers: [{name: a, bits: x}]", "'a.bits' must be an integer")]
  [TestCase("registers: [{name: a, bits: 8, colour: red}]", "a register has the unknown property 'colour'")]
  [TestCase("registers: {name: a}", "registers must be a list")]
  [TestCase("registers: [3]", "a register must be a mapping")]
  [TestCase("registers: [{bits: 8}]", "the property 'name' is missing")]
  [TestCase("registers: [{name: '', bits: 8}]", "'name' must be text")]
  [TestCase("interruptVectors: 0\nregisters: [{name: a, bits: 8}]", "interruptVectors must be at least 1, not 0")]
  public void ProfileRules_AreEnforced(string rest, string reason)
    => Assert.That(ProfileError(Head + rest), Does.EndWith(reason));

  [TestCase("isa: t\naddressBits: 0\nbyteOrder: little\nregisters: [{name: a, bits: 8}]", "addressBits must be 1 to 64, not 0")]
  [TestCase("isa: t\naddressBits: 32\nbyteOrder: middle\nregisters: [{name: a, bits: 8}]", "byteOrder must be 'little' or 'big', not 'middle'")]
  [TestCase("isa: t\naddressBits: 32\nbyteOrder: big\n", "the property 'registers' is missing")]
  [TestCase("isa: t\naddressBits: 32\nbyteOrder: big\ncpu: z80\nregisters: []", "the profile has the unknown property 'cpu'")]
  [TestCase("- a\n- b", "the file must be a mapping")]
  [TestCase("spec: [1]", "spec must be a mapping")]
  [TestCase("isa: [unclosed", "not valid YAML")]
  public void ProfileShape_IsEnforced(string yaml, string reason)
    => Assert.That(ProfileError(yaml), Does.Contain(reason));

  [Test]
  public void BigEndianProfile_Loads()
    => Assert.That(ProfileLoader.Parse("isa: be\naddressBits: 24\nbyteOrder: big\nregisters: [{name: d0, bits: 32, class: general}]").ByteOrder, Is.EqualTo(ByteOrder.Big));

  [Test]
  public void UnreadableProfile_IsPly4003() {
    var exception = Assert.Throws<MachineException>(() => IsaProfile.Load(Path.Combine(MachineTesting.Fixtures, "mach-ir", "profiles", "missing.yaml")))!;
    Assert.That(exception.Diagnostic.Message, Does.StartWith("machine profile 'missing': cannot be read"));
  }

  [TestCase("program-counter", RegisterClass.ProgramCounter)]
  [TestCase("segment", RegisterClass.Segment)]
  [TestCase("float", RegisterClass.Float)]
  [TestCase("vector", RegisterClass.Vector)]
  public void RegisterClasses_AreRead(string name, RegisterClass expected)
    => Assert.That(ProfileLoader.Parse(Head + $"registers: [{{name: a, bits: 8, class: {name}}}]").Registers[0].Class, Is.EqualTo(expected));

  [Test]
  public void ConstructedProfile_ValidatesEveryRule() {
    var profile = new IsaProfile("p", 70, ByteOrder.Little, [new("a", 8, null, 0, SubRegisterWrite.Preserve, RegisterClass.General)], []);
    Assert.That(profile.Validate().Select(problem => problem.Message), Is.EqualTo(new[] { "machine profile 'p': addressBits must be 1 to 64, not 70" }));
  }

  // --- text form ------------------------------------------------------------------------------

  private const string AllOpcodes = """
    machine test-m32
    mode flat

    @all [root-registers, leaf] {
    entry:
      %k = Const.u32 0x1234
      %a = RegRead.u32 @r1 !at(0x00401000, "8B C8", "mov ecx, eax")
      %b = Add.u32 %a, %k
      %c = Sub.u32 %b, 1
      %d = Mul.u32 %c, %a
      %dq = UDiv.u32 %a, %k
      %dr = URem.u32 %a, %k
      %ds = SDiv.u32 %a, %k
      %dt = SRem.u32 %a, %k
      %e = And.u32 %d, 0xFF
      %f = Or.u32 %e, %a
      %g = Xor.u32 %f, %a
      %h = Shl.u32 %g, 2
      %i = LShr.u32 %h, 1
      %j = AShr.u32 %i, 3
      %l = Not.u32 %j
      %m = Neg.u32 %l
      %n = Eq.u32 %m, %a
      %o = Ne.u32 %m, %a
      %p = ULt.u32 %m, %a
      %q = ULe.u32 %m, %a
      %r = SLt.u32 %m, %a
      %s = SLe.u32 %m, %a
      %t = Truncate.u8 %m
      %u = ZeroExtend.u32 %t
      %v = SignExtend.u32 %t
      %w = ExtractBits.u32 %v, 4, 12
      %x = InsertBits.u32 %w, %u, 16, 8
      %y = Select.u32 %n, %x, %w
      RegWrite.u32 @r2, %y
      %fl = FlagRead.bool @c
      FlagWrite @z, %fl
      FlagUndef @o
      %ld = Load.u8 %a
      Store.u16 %a, 7
      Branch %p, left, right
    left:
      MachineTrap[kind=breakpoint, vector=3, resumes=true, id=bp] service("debug") reads(r0)
      Jump done
    right:
      %to = RegRead.u32 @r3
      IndirectJump %to -> left, done !at(arm32:thumb, 0x00008000, "47 00")
    done:
      %far = RegRead.u32 @r0
      IndirectJump %far -> unresolved
    }

    @second {
    entry:
      Ret
    }

    """;

  /// <summary>Every opcode prints, parses and prints the same text, origins and attributes included.</summary>
  [Test]
  public void EveryOpcode_RoundTrips() {
    var module = MachineTesting.Verified(AllOpcodes);
    var printed = MachineSyntax.Print(module);
    Assert.That(printed, Is.EqualTo(AllOpcodes.Replace("\r", "", StringComparison.Ordinal)));
    Assert.That(MachineSyntax.Print(MachineTesting.Parse(printed)), Is.EqualTo(printed));
    Assert.That(MachineTesting.Parse(printed), Is.EqualTo(module));
    var used = module.Functions.SelectMany(function => function.Blocks).SelectMany(block => block.Instructions).Select(instruction => instruction.Opcode).ToHashSet();
    Assert.That(Enum.GetValues<MachineOpcode>().Where(opcode => !used.Contains(opcode)), Is.Empty);
  }

  [Test]
  public void Origins_KeepIsaModeAddressBytesAndText() {
    var module = MachineTesting.Parse(AllOpcodes);
    var all = module.Function("all")!;
    Assert.That(all.Blocks[0].Instructions[1].Origin, Is.EqualTo(new MachineOrigin("test-m32", "flat", 0x00401000, [0x8B, 0xC8], "mov ecx, eax")));
    Assert.That(all.Block("right")!.Instructions[1].Origin, Is.EqualTo(new MachineOrigin("arm32", "thumb", 0x8000, [0x47, 0x00], null)));
    Assert.That(all.Block("right")!.Instructions[1].Successors, Is.EqualTo(new[] { "left", "done" }));
    Assert.That(all.Block("done")!.Instructions[1].Unresolved, Is.True);
    Assert.That(all.Block("done")!.Instructions[1].Successors, Is.Empty);
    Assert.That(module.Function("nosuch"), Is.Null);
  }

  /// <summary>Test design 18: an origin without a machine profile, with an ISA of its own.</summary>
  [Test]
  public void OriginWithoutAProfile_VerifiesAndRoundTrips() {
    const string text = "isa wasm\n\n@f {\nentry:\n  %a = Const.u32 0x2A !at(0x0000002A, \"20 00\")\n  Ret\n}\n";
    var module = MachineTesting.Verified(text);
    Assert.That(module.Profile, Is.Null);
    Assert.That(module.Functions[0].Blocks[0].Instructions[0].Origin!.Isa, Is.EqualTo("wasm"));
    Assert.That(MachineSyntax.Print(module), Is.EqualTo(text));
  }

  [Test]
  public void OriginAboveFourGigabytes_PrintsSixteenDigits() {
    const string text = "machine test-m64\n\n@f {\nentry:\n  Ret !at(0x0000000100000000, \"C3\")\n}\n";
    Assert.That(MachineSyntax.Print(MachineTesting.Verified(text)), Is.EqualTo(text));
  }

  [Test]
  public void OriginTextWithQuotes_IsEscaped() {
    const string text = "machine test-m32\n\n@f {\nentry:\n  Ret !at(0x00000001, \"C3\", \"say \\\"hi\\\" \\\\\")\n}\n";
    var module = MachineTesting.Parse(text);
    Assert.That(module.Functions[0].Blocks[0].Instructions[0].Origin!.Text, Is.EqualTo("say \"hi\" \\"));
    Assert.That(MachineSyntax.Print(module), Is.EqualTo(text));
  }

  [Test]
  public void InstructionAndFunction_PrintWithoutAModule() {
    var instruction = MachineInstruction.Create(MachineOpcode.Ret, MachineType.Void, null) with { Origin = new("x86", "real16", 0x100, [0xC3], null) };
    Assert.That(instruction.ToString(), Is.EqualTo("Ret !at(x86:real16, 0x00000100, \"C3\")"));
    var function = new MachineFunction("f", [new MachineBlock("entry", [instruction])]);
    Assert.That(MachineSyntax.Print(function), Is.EqualTo("@f {\nentry:\n  Ret !at(x86:real16, 0x00000100, \"C3\")\n}\n"));
  }

  [Test]
  public void SemicolonsAndComments_SeparateStatements() {
    var module = MachineTesting.Verified("machine test-m32 # the profile\n@f { entry: %v = Const.u8 0xFF; RegWrite.u8 @r0l, %v; Ret }");
    Assert.That(module.Functions[0].Blocks[0].Instructions.Select(instruction => instruction.Opcode), Is.EqualTo(new[] { MachineOpcode.Const, MachineOpcode.RegWrite, MachineOpcode.Ret }));
  }

  [Test]
  public void EmptyText_IsAnEmptyModule() {
    var module = MachineTesting.Parse("\n# nothing\n");
    Assert.That(module.Functions, Is.Empty);
    Assert.That(MachineSyntax.Print(module), Is.Empty);
    Assert.That(module.OriginIsa, Is.EqualTo("unknown"));
  }

  [TestCase(0UL, "0")]
  [TestCase(9UL, "9")]
  [TestCase(10UL, "0xA")]
  [TestCase(ulong.MaxValue, "0xFFFFFFFFFFFFFFFF")]
  public void Numbers_PrintDecimalBelowTenElseHex(ulong value, string expected) => Assert.That(MachineSyntax.Number(value), Is.EqualTo(expected));

  [TestCase("machine nosuch", "PLY4003", "machine profile 'nosuch': no such machine profile")]
  [TestCase("@f { entry: Ret }\n@f { entry: Ret }", "PLY4006", "the function '@f' is defined twice")]
  [TestCase("@f { entry: Ret", "PLY4006", "line 1: the function '@f' is not closed with '}'")]
  [TestCase("@f { Ret }", "PLY4006", "line 1: an instruction of '@f' stands before the first block label")]
  [TestCase("@f { entry: Frob }", "PLY4006", "line 1: 'Frob' is not a MachineIR operation")]
  [TestCase("@f { entry: ret }", "PLY4006", "line 1: 'ret' is not a MachineIR operation")]
  [TestCase("@f { entry: %a = Const.i32 1 }", "PLY4006", "line 1: 'i32' is not a machine type")]
  [TestCase("@f { entry: Const.u8 1 }", "PLY4006", "line 1: 'Const' defines a value and needs '%name ='")]
  [TestCase("@f { entry: %a = Ret }", "PLY4006", "line 1: 'Ret' defines no value")]
  [TestCase("@f { entry: %a = Const.u8 0xZZ }", "PLY4006", "line 1: '0xZZ' is not a number")]
  [TestCase("@f { entry: %a = Const.u8 0x }", "PLY4006", "line 1: '0x' is not a number")]
  [TestCase("@f { entry: Jump , }", "PLY4006", "line 1: expected an operand but found ','")]
  [TestCase("@f { entry: %a = Const.u8 1 2 }", "PLY4006", "line 1: expected the end of the statement but found '2'")]
  [TestCase("@f { entry: IndirectJump %a, b }", "PLY4006", "line 1: expected '->' but found ','")]
  [TestCase("@f { entry: IndirectJump %a -> 3 }", "PLY4006", "line 1: expected a target block but found '3'")]
  [TestCase("@f { entry: Ret !on(1) }", "PLY4006", "line 1: expected 'at' after '!' but found 'on'")]
  [TestCase("@f { entry: Ret !at(x) }", "PLY4006", "line 1: expected ':' but found ')'")]
  [TestCase("@f { entry: Ret !at(\"C3\") }", "PLY4006", "line 1: expected the address of the origin but found 'C3'")]
  [TestCase("@f { entry: Ret !at(1, 2) }", "PLY4006", "line 1: expected the bytes of the origin as a string but found '2'")]
  [TestCase("@f { entry: Ret !at(1, \"C\") }", "PLY4006", "line 1: 'C' is not a byte in hex")]
  [TestCase("@f { entry: Ret !at(1, \"C3\", 4) }", "PLY4006", "line 1: expected the disassembly as a string but found '4'")]
  [TestCase("@f { entry: Ret !at(1, \"C3 }", "PLY4006", "line 1: a string is not closed")]
  [TestCase("@f { entry: % = Ret }", "PLY4006", "line 1: '%' must be followed by a name")]
  [TestCase("@f { entry: Ret ^ }", "PLY4006", "line 1: unexpected character '^'")]
  [TestCase("@f [] { entry: Ret }", "PLY4006", "line 1: expected an attribute but found ']'")]
  [TestCase("@f { entry: %a = 3 }", "PLY4006", "line 1: expected an operation but found '3'")]
  [TestCase("ret", "PLY4006", "line 1: expected a function or a header line but found 'ret'")]
  [TestCase("@f { entry: Ret }\nmachine test-m32", "PLY4006", "line 2: expected a function or a header line but found 'machine'")]
  [TestCase("machine\n", "PLY4006", "line 1: expected the value of 'machine' but found the end of the statement")]
  [TestCase("mode", "PLY4006", "line 1: expected the value of 'mode' but found the end of the text")]
  [TestCase("@f", "PLY4006", "line 1: expected '{' but found the end of the text")]
  public void BadText_IsRefused(string text, string code, string message) {
    var exception = Assert.Throws<MachineException>(() => MachineTesting.Parse(text))!;
    Assert.That(exception.Diagnostic.Code, Is.EqualTo(code));
    Assert.That(exception.Diagnostic.Message, Does.EndWith(message));
  }

  [Test]
  public void MachineType_NamesAndParses() {
    Assert.That(MachineType.TryParse("u1", out var bit) && bit == MachineType.Bool, Is.True);
    Assert.That(MachineType.TryParse("u64", out var wide) && wide == MachineType.U64, Is.True);
    Assert.That(MachineType.TryParse("u65", out _), Is.False);
    Assert.That(MachineType.TryParse("u0", out _), Is.False);
    Assert.That(MachineType.TryParse("u", out _), Is.False);
    Assert.That(MachineType.TryParse("x8", out _), Is.False);
    Assert.That(MachineType.Void.ToString(), Is.EqualTo("void"));
    Assert.That(MachineType.U16.ToString() + MachineType.U8, Is.EqualTo("u16u8"));
    Assert.That(MachineType.U64.Mask, Is.EqualTo(ulong.MaxValue));
    Assert.That(MachineType.Of(12).Mask, Is.EqualTo(0xFFFUL));
    Assert.Throws<ArgumentOutOfRangeException>(() => MachineType.Of(65));
    Assert.Throws<ArgumentOutOfRangeException>(() => MachineType.Of(0));
  }

  [Test]
  public void Records_CompareByContent() {
    var a = MachineTesting.Parse(AllOpcodes);
    var b = MachineTesting.Parse(AllOpcodes);
    Assert.That(a, Is.EqualTo(b));
    Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
    Assert.That(a.Functions[0].GetHashCode(), Is.EqualTo(b.Functions[0].GetHashCode()));
    Assert.That(a.Functions[0].Blocks[0].GetHashCode(), Is.EqualTo(b.Functions[0].Blocks[0].GetHashCode()));
    Assert.That(a.Functions[0].Blocks[0].Instructions[1].Origin!.GetHashCode(), Is.EqualTo(b.Functions[0].Blocks[0].Instructions[1].Origin!.GetHashCode()));
    Assert.That(a, Is.Not.EqualTo(a with { Mode = "other" }));
    Assert.That(a.Functions[0], Is.Not.EqualTo(a.Functions[1]));
    Assert.That(a.Functions[0].Blocks[0], Is.Not.EqualTo(a.Functions[0].Blocks[1]));
    Assert.That(a.Functions[0].Blocks[0].Equals(null), Is.False);
    Assert.That(a.Functions[0].Equals(null), Is.False);
    Assert.That(a.Equals(null), Is.False);
    Assert.That(a.Functions[0].Blocks[0].Instructions[0].Equals(null), Is.False);
    Assert.That(new MachineOrigin("a", "b", 1, [], null).Equals(null), Is.False);
    Assert.That(new MachineValue("x").ToString() + new MachineStorage("r0") + new MachineLabel("L") + new MachineImmediate(12), Is.EqualTo("%x@r0L0xC"));
  }

  // --- verifier -------------------------------------------------------------------------------

  /// <summary>Test design 11: an unknown register, and an unknown flag.</summary>
  [TestCase("%a = RegRead.u32 @r9", "'r9' is not a register or flag of machine profile 'test-m32'")]
  [TestCase("%a = FlagRead.bool @q", "'q' is not a register or flag of machine profile 'test-m32'")]
  [TestCase("FlagUndef @r0", "'r0' is not a register or flag of machine profile 'test-m32'")]
  [TestCase("%a = RegRead.bool @c", "'c' is not a register or flag of machine profile 'test-m32'")]
  public void UnknownStorage_IsPly4000(string instruction, string message) {
    var problems = MachineTesting.Problems(M32($"@f {{\nentry:\n  {instruction}\n  Ret\n}}\n"));
    Assert.That(problems.Select(problem => (problem.Code, problem.Message)), Is.EqualTo(new[] { ("PLY4000", message) }));
  }

  /// <summary>Test design 12: a type of another width than its register.</summary>
  [Test]
  public void WrongWidth_IsPly4001() {
    var problems = MachineTesting.Problems(M32("@f {\nentry:\n  %a = RegRead.u16 @r0\n  RegWrite.u16 @r0l, %a\n  Ret\n}\n"));
    Assert.That(problems.Select(problem => problem.ToString()), Is.EqualTo(new[] {
      "error PLY4001: '%a = RegRead.u16 @r0' has type u16 but register 'r0' is 32 bits wide",
      "error PLY4001: 'RegWrite.u16 @r0l, %a' has type u16 but register 'r0l' is 8 bits wide"
    }));
  }

  /// <summary>Test design 13 and the scenario "Reading a flag that is only undefined is rejected".</summary>
  [Test]
  public void FlagReadAfterUndefInTheSameBlock_IsPly4002() {
    var problems = MachineTesting.Problems(M32("@f {\nentry:\n  FlagUndef @o\n  %x = FlagRead.bool @o\n  Ret\n}\n"));
    Assert.That(problems.Select(problem => problem.ToString()), Is.EqualTo(new[] { "error PLY4002: flag 'o' is read at @f entry[1] but is undefined after @f entry[0]" }));
  }

  [Test]
  public void FlagUndefinedOnEveryPath_IsPly4002AtTheJoin() {
    var problems = MachineTesting.Problems(M32("""
      @f {
      entry:
        %k = Const.bool 1
        Branch %k, a, b
      a:
        FlagUndef @c
        Jump join
      b:
        FlagUndef @c
        Jump join
      join:
        %x = FlagRead.bool @c
        Ret
      }
      """));
    Assert.That(problems.Select(problem => problem.Code), Is.EqualTo(new[] { "PLY4002" }));
    Assert.That(problems[0].Message, Does.StartWith("flag 'c' is read at @f join[0] but is undefined after @f a[0]"));
  }

  /// <summary>Test design 6: defined on one path, undefined on the other: the read is legal.</summary>
  [Test]
  public void FlagDefinedOnOnePath_Verifies()
    => MachineTesting.Verified(M32("""
      @f {
      entry:
        %k = Const.bool 1
        Branch %k, a, b
      a:
        %x = Const.bool 1
        FlagWrite @c, %x
        Jump join
      b:
        FlagUndef @c
        Jump join
      join:
        %r = FlagRead.bool @c
        FlagUndef @c
        Ret
      }
      """));

  [Test]
  public void FlagFromTheCaller_AndAfterALoop_Verify()
    => MachineTesting.Verified(M32("""
      @f {
      entry:
        %x = FlagRead.bool @z
        Jump loop
      loop:
        %y = FlagRead.bool @z
        FlagUndef @z
        %w = Const.bool 0
        FlagWrite @z, %w
        Branch %y, loop, out
      out:
        Ret
      unreachable:
        %u = FlagRead.bool @s
        Ret
      }
      """));

  /// <summary>Test design 14: machine operations in a module without a profile.</summary>
  [Test]
  public void MachineOperationWithoutAProfile_IsPly4003() {
    var problems = MachineTesting.Problems("@f {\nentry:\n  %a = RegRead.u32 @r0\n  Ret\n}\n");
    Assert.That(problems.Select(problem => problem.ToString()), Is.EqualTo(new[] { "error PLY4003: '@f' uses machine operations but module 'module' declares no machine profile" }));
  }

  [Test]
  public void PureCodeWithoutAProfile_Verifies() => MachineTesting.Verified("@f {\nentry:\n  %a = Const.u8 1\n  %b = Load.u8 %a\n  Ret\n}\n".Replace("  %b = Load.u8 %a\n", "", StringComparison.Ordinal));

  /// <summary>Test design 15: a sub-register access in a function normalized to root registers.</summary>
  [Test]
  public void SubRegisterAfterNormalization_IsPly4004() {
    var problems = MachineTesting.Problems(M32("@f [root-registers] {\nentry:\n  %v = Const.u8 1\n  RegWrite.u8 @r0l, %v\n  %w = RegRead.u32 @r0\n  Ret\n}\n"));
    Assert.That(problems.Select(problem => problem.ToString()), Is.EqualTo(new[] { "error PLY4004: 'RegWrite.u8 @r0l, %v' is not allowed in '@f' after normalization to root registers" }));
  }

  [TestCase("@f {\n}\n", "it has no block")]
  [TestCase("@f {\nentry:\n}\n", "the block 'entry' does not end in a terminator")]
  [TestCase("@f {\nentry:\n  %a = Const.u8 1\n}\n", "the block 'entry' does not end in a terminator")]
  [TestCase("@f {\nentry:\n  Ret\n  %a = Const.u8 1\n  Ret\n}\n", "an instruction follows the terminator of the block 'entry' at @f entry[1]")]
  [TestCase("@f {\nentry:\n  Ret\nentry:\n  Ret\n}\n", "the label 'entry' is used twice")]
  [TestCase("@f {\nentry:\n  Jump nowhere\n}\n", "@f entry[0] jumps to the unknown block 'nowhere'")]
  [TestCase("@f {\nentry:\n  %a = Add.u8 %b, 1\n  Ret\n}\n", "@f entry[0] uses %b, which is not defined before it in the block 'entry'")]
  [TestCase("@f {\nentry:\n  %a = Const.u8 1\n  Jump next\nnext:\n  %b = Add.u8 %a, 1\n  Ret\n}\n", "@f next[0] uses %a, which is not defined before it in the block 'next'")]
  [TestCase("@f {\nentry:\n  %a = Const.u8 1\n  %a = Const.u8 2\n  Ret\n}\n", "@f entry[1] defines %a a second time")]
  [TestCase("@f {\nentry:\n  %a = Const.u8 0x100\n  Ret\n}\n", "@f entry[0] the constant 0x100 does not fit u8")]
  [TestCase("@f {\nentry:\n  %a = Const.u8 1\n  %b = Add.u16 %a, 1\n  Ret\n}\n", "@f entry[1] the left operand %a is u8, not u16")]
  [TestCase("@f {\nentry:\n  %a = Const.u8 1\n  %b = Eq.u8 %a, 1\n  %c = Add.u8 %b, 1\n  Ret\n}\n", "@f entry[2] the left operand %b is bool, not u8")]
  [TestCase("@f {\nentry:\n  %a = Const.u8 1\n  %b = Not.u16 %a\n  Ret\n}\n", "@f entry[1] the operand %a is u8, not u16")]
  [TestCase("@f {\nentry:\n  %a = Const.u8 1\n  %b = ZeroExtend.u8 %a\n  Ret\n}\n", "@f entry[1] '%b = ZeroExtend.u8 %a' converts u8 to u8, which is not wider")]
  [TestCase("@f {\nentry:\n  %a = Const.u8 1\n  %b = Truncate.u16 %a\n  Ret\n}\n", "@f entry[1] '%b = Truncate.u16 %a' converts u8 to u16, which is not narrower")]
  [TestCase("@f {\nentry:\n  %b = SignExtend.u16 3\n  Ret\n}\n", "@f entry[0] '%b = SignExtend.u16 3' converts a value, not a constant")]
  [TestCase("@f {\nentry:\n  %a = Const.u8 1\n  %b = ExtractBits.u8 %a, 4, 5\n  Ret\n}\n", "@f entry[1] the field 4..8 does not lie in u8")]
  [TestCase("@f {\nentry:\n  %a = Const.u8 1\n  %b = ExtractBits.u8 %a, 0, 0\n  Ret\n}\n", "@f entry[1] the field 0..18446744073709551615 does not lie in u8")]
  [TestCase("@f {\nentry:\n  %a = Const.u8 1\n  %b = InsertBits.u8 %a, 0x100, 0, 8\n  Ret\n}\n", "@f entry[1] the inserted value 0x100 does not fit u8")]
  [TestCase("@f {\nentry:\n  %a = Const.u8 1\n  %b = Select.u8 %a, 1, 2\n  Ret\n}\n", "@f entry[1] the condition %a is u8, not bool")]
  [TestCase("@f {\nentry:\n  %a = Const.u8 1\n  Branch %a, entry, entry\n}\n", "@f entry[1] the condition %a is u8, not bool")]
  [TestCase("@f {\nentry:\n  %a = Const 1\n  Ret\n}\n", "@f entry[0] '%a = Const 1' needs a type")]
  [TestCase("@f {\nentry:\n  Ret.u8\n}\n", "@f entry[0] 'Ret.u8' takes no type")]
  [TestCase("@f {\nentry:\n  %a = Const.u8 %b\n  Ret\n}\n", "@f entry[0] '%a = Const.u8 %b' does not have the operands of Const (1)")]
  [TestCase("@f {\nentry:\n  Ret 1\n}\n", "@f entry[0] 'Ret 1' does not have the operands of Ret (0)")]
  [TestCase("@f {\nentry:\n  Jump 1\n}\n", "@f entry[0] 'Jump 1' does not have the operands of Jump (1)")]
  public void MalformedFunctions_ArePly4005(string text, string reason) {
    var problems = MachineTesting.Problems(text);
    Assert.That(problems.Select(problem => problem.Code), Has.All.EqualTo("PLY4005"));
    Assert.That(problems.Select(problem => problem.Message), Does.Contain("'@f' is not well-formed MachineIR: " + reason));
  }

  [TestCase("%a = RegRead.u32 @r0, 1", "'%a = RegRead.u32 @r0, 1' does not have the operands of RegRead (1)")]
  [TestCase("%a = FlagRead.u8 @c", "'%a = FlagRead.u8 @c' reads a flag, which is bool")]
  [TestCase("FlagWrite.bool @c, 1", "'FlagWrite.bool @c, 1' takes no type")]
  [TestCase("FlagWrite @c, 2", "the written value 2 does not fit bool")]
  [TestCase("RegWrite.u32 @r0, 0x100000000", "the written value 0x100000000 does not fit u32")]
  [TestCase("%a = Load.u8 0x100000000", "the address 0x100000000 does not fit u32")]
  [TestCase("Store.u8 0x100000000, 1", "the address 0x100000000 does not fit u32")]
  [TestCase("Store.u8 1, 0x100", "the stored value 0x100 does not fit u8")]
  [TestCase("%a = Load.u12 1", "'%a = Load.u12 1' accesses memory in u12, which is no whole number of bytes")]
  [TestCase("IndirectJump 0x100000000 -> entry", "the address 0x100000000 does not fit u32")]
  public void MalformedMachineOperations_ArePly4005(string instruction, string reason) {
    var problems = MachineTesting.Problems(M32($"@f {{\nentry:\n  {instruction}\n  Ret\n}}\n"));
    Assert.That(problems.Select(problem => problem.Message), Does.Contain("'@f' is not well-formed MachineIR: @f entry[0] " + reason));
  }

  [Test]
  public void IndirectJumpWithoutTargetsAndNotUnresolved_IsPly4005() {
    var jump = MachineInstruction.Create(MachineOpcode.IndirectJump, MachineType.Void, null, new MachineImmediate(0));
    var module = new MachineModule(MachineTesting.Profile("test-m32"), [new MachineFunction("f", [new MachineBlock("entry", [jump])])]);
    Assert.That(MachineVerifier.Verify(module).Select(problem => problem.Message), Is.EqualTo(new[] {
      "'@f' is not well-formed MachineIR: @f entry[0] 'IndirectJump 0 -> ' does not have the operands of IndirectJump (a target list or 'unresolved')"
    }));
  }

  /// <summary>An origin names its address where the verifier names the instruction.</summary>
  [Test]
  public void Location_NamesTheOriginAddress() {
    var problems = MachineTesting.Problems(M32("@f {\nentry:\n  %a = RegRead.u16 @r9 !at(0x00401000, \"66 8B C1\")\n  Ret\n}\n"));
    Assert.That(problems.Single().Code, Is.EqualTo("PLY4000"));
    var function = MachineTesting.Parse(M32("@f {\nentry:\n  Ret !at(0x00401000, \"C3\")\n}\n")).Functions[0];
    Assert.That(MachineVerifier.Location(function, function.Blocks[0], 0), Is.EqualTo("@f entry[0] (0x00401000)"));
  }

  [Test]
  public void Exception_CarriesItsDiagnostic() {
    var diagnostic = MachineDiagnostics.InvalidProfile("p", "broken", "p.yaml");
    var exception = new MachineException(diagnostic);
    Assert.That(exception.Diagnostic, Is.SameAs(diagnostic));
    Assert.That(exception.Message, Is.EqualTo("p.yaml: error PLY4003: machine profile 'p': broken"));
    Assert.Throws<ArgumentNullException>(() => _ = new MachineException(null!));
    Assert.That(diagnostic.ExitCode, Is.EqualTo(ExitCode.InvalidSource));
  }

  [Test]
  public void Arguments_AreChecked() {
    Assert.Throws<ArgumentNullException>(() => MachineVerifier.Verify(null!));
    Assert.Throws<ArgumentNullException>(() => MachineSyntax.Parse(null!, _ => null));
    Assert.Throws<ArgumentNullException>(() => MachineSyntax.Print((MachineModule)null!));
    Assert.Throws<ArgumentNullException>(() => ProfileLoader.Parse(null!));
    Assert.Throws<ArgumentNullException>(() => _ = new MachineProfileCatalog(null!));
    Assert.Throws<ArgumentNullException>(() => MachineTesting.Profile("test-m32").Root(null!));
    Assert.Throws<ArgumentNullException>(() => MachineDiagnostics.InvalidProfile(null!, "x"));
  }

}
