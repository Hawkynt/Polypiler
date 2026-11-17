using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.MachineSemantics;
using Polypiler.Tests.MachineSemantics.Support;

namespace Polypiler.Tests.MachineSemantics;

/// <summary>
/// <see cref="MachineNormalizer"/> and <see cref="MachineInterface"/>: sub-register accesses become
/// accesses of the root under the write policy of the profile, the result verifies with the attribute
/// <c>root-registers</c>, origins survive, and the interface names what a routine takes and gives back.
/// The expected text is written by hand from the profile rules (Intel SDM vol. 1 §3.4.1); the values
/// are checked by the interpreter in <c>MachineInterpreterTests</c>.
/// </summary>
[TestFixture]
public sealed class MachineNormalizerTests {

  /// <summary>Normalizes the only function of a module, verifies the result and returns its entry block's text.</summary>
  private static string[] Normalized(string text, out MachineModule normalized) {
    var module = MachineTesting.Verified(text);
    var result = MachineNormalizer.ToRootRegisters(module);
    Assert.That(MachineVerifier.Verify(result), Is.Empty);
    Assert.That(result.Functions[0].Attributes, Does.Contain(MachineFunction.RootRegisters));
    normalized = result;
    return [.. result.Functions[0].Blocks[0].Instructions.Select(instruction => MachineSyntax.Print(instruction, result))];
  }

  private static string[] Normalized(string text) => Normalized(text, out _);

  /// <summary>Test design 1 and the scenario "A byte sub-register write preserves the other bits of its root".</summary>
  [Test]
  public void ByteWrite_InsertsIntoTheRoot()
    => Assert.That(Normalized("machine test-m32\n@f { entry: %v = Const.u8 0xFF; RegWrite.u8 @r0l, %v; Ret }"), Is.EqualTo(new[] {
      "%v = Const.u8 0xFF", "%r0 = RegRead.u32 @r0", "%v.z = ZeroExtend.u32 %v", "%r0.1 = InsertBits.u32 %r0, %v.z, 0, 8", "RegWrite.u32 @r0, %r0.1", "Ret"
    }));

  /// <summary>Test design 2: the high byte lies at offset 8.</summary>
  [Test]
  public void HighByteWrite_InsertsAtOffsetEight()
    => Assert.That(Normalized("machine test-m32\n@f { entry: RegWrite.u8 @r0h, 0xAB; Ret }"), Is.EqualTo(new[] {
      "%r0 = RegRead.u32 @r0", "%r0.1 = InsertBits.u32 %r0, 0xAB, 8, 8", "RegWrite.u32 @r0, %r0.1", "Ret"
    }));

  /// <summary>Test design 3 and the scenario "A zero-extending sub-register write replaces the whole root".</summary>
  [Test]
  public void ZeroExtendingWrite_ReplacesTheRootWithoutReadingIt() {
    var text = Normalized("machine test-m64\n@f { entry: %v = Const.u32 1; RegWrite.u32 @r0d, %v; Ret }", out var module);
    Assert.That(text, Is.EqualTo(new[] { "%v = Const.u32 1", "%v.z = ZeroExtend.u64 %v", "RegWrite.u64 @r0, %v.z", "Ret" }));
    var signature = MachineInterface.Of(module.Functions[0], module.Profile!);
    Assert.That(signature.RegistersIn, Is.Empty);
    Assert.That(signature.RegistersOut, Is.EqualTo(new[] { "r0" }));
  }

  /// <summary>Test design 4: a sub-register read extracts its field and truncates.</summary>
  [Test]
  public void SubRegisterRead_ExtractsAndTruncates()
    => Assert.That(Normalized("machine test-m32\n@f { entry: %w = RegRead.u16 @r0w; RegWrite.u32 @r1, 0; Ret }"), Is.EqualTo(new[] {
      "%r0 = RegRead.u32 @r0", "%w.field = ExtractBits.u32 %r0, 0, 16", "%w = Truncate.u16 %w.field", "RegWrite.u32 @r1, 0", "Ret"
    }));

  /// <summary>Test design 5: a register only read is an input and no output.</summary>
  [Test]
  public void RegisterOnlyRead_IsAnInputOnly() {
    var module = MachineTesting.Verified("machine test-m32\n@f { entry: %a = RegRead.u32 @r1; Ret }");
    var signature = MachineInterface.Of(module.Functions[0], module.Profile!);
    Assert.That(signature, Is.EqualTo(new MachineInterface(["r1"], [], [], [])).Using<MachineInterface>((a, b) =>
      a.RegistersIn.SequenceEqual(b.RegistersIn) && a.RegistersOut.SequenceEqual(b.RegistersOut) && a.FlagsIn.SequenceEqual(b.FlagsIn) && a.FlagsOut.SequenceEqual(b.FlagsOut)));
  }

  /// <summary>Test design 7: a 16-bit write of 0xFFFF covers bit 0 and bit 15.</summary>
  [Test]
  public void WordWrite_InsertsSixteenBits()
    => Assert.That(Normalized("machine test-m32\n@f { entry: RegWrite.u16 @r0w, 0xFFFF; Ret }")[1], Is.EqualTo("%r0.1 = InsertBits.u32 %r0, 0xFFFF, 0, 16"));

  /// <summary>Test design 8: a sub-register as wide as its root replaces it; no InsertBits.</summary>
  [Test]
  public void SubRegisterAsWideAsItsRoot_IsTheRoot() {
    var profile = ProfileLoader.Parse("isa: wide\naddressBits: 32\nbyteOrder: little\nregisters: [{name: r0, bits: 32}, {name: r0x, bits: 32, parent: r0}]");
    var module = MachineSyntax.Parse("machine wide\n@f { entry: %a = RegRead.u32 @r0x; RegWrite.u32 @r0x, %a; Ret }", id => id == "wide" ? profile : null);
    Assert.That(MachineVerifier.Verify(module), Is.Empty);
    var normalized = MachineNormalizer.ToRootRegisters(module);
    Assert.That(MachineSyntax.Print(normalized.Functions[0], normalized), Is.EqualTo("@f [root-registers] {\nentry:\n  %a = RegRead.u32 @r0\n  RegWrite.u32 @r0, %a\n  Ret\n}\n"));
  }

  /// <summary>Test design 10: an empty function stays as it is and has an empty interface.</summary>
  [Test]
  public void EmptyFunction_IsUnchanged() {
    Assert.That(Normalized("machine test-m32\n@f { entry: Ret }"), Is.EqualTo(new[] { "Ret" }));
    var module = MachineTesting.Verified("machine test-m32\n@f { entry: Ret }");
    var signature = MachineInterface.Of(module.Functions[0], module.Profile!);
    Assert.That(signature.RegistersIn.IsEmpty && signature.RegistersOut.IsEmpty && signature.FlagsIn.IsEmpty && signature.FlagsOut.IsEmpty, Is.True);
  }

  [Test]
  public void AlreadyNormalized_KeepsOneAttribute() {
    var module = MachineTesting.Verified("machine test-m32\n@f [root-registers] { entry: Ret }");
    Assert.That(MachineNormalizer.ToRootRegisters(module).Functions[0].Attributes, Is.EqualTo(new[] { MachineFunction.RootRegisters }));
  }

  /// <summary>Nested sub-registers that all preserve insert straight into the root: x86 AH inside AX inside EAX.</summary>
  [Test]
  public void NestedPreservingSubRegister_InsertsIntoTheRootAtItsOffset()
    => Assert.That(Normalized("machine x86-32\n@f { entry: %v = Const.u8 0x12; RegWrite.u8 @ah, %v; %r = RegRead.u8 @ah; Ret }"), Is.EqualTo(new[] {
      "%v = Const.u8 0x12", "%eax = RegRead.u32 @eax", "%v.z = ZeroExtend.u32 %v", "%eax.1 = InsertBits.u32 %eax, %v.z, 8, 8", "RegWrite.u32 @eax, %eax.1",
      "%eax.2 = RegRead.u32 @eax", "%r.field = ExtractBits.u32 %eax.2, 8, 8", "%r = Truncate.u8 %r.field", "Ret"
    }));

  /// <summary>x64: a 32-bit write zero-extends, a 16-bit write preserves the other 48 bits.</summary>
  [Test]
  public void X64Policies_FollowTheManual()
    => Assert.That(Normalized("machine x86-64\n@f { entry: RegWrite.u32 @eax, 1; RegWrite.u16 @ax, 2; Ret }"), Is.EqualTo(new[] {
      "RegWrite.u64 @rax, 1", "%rax = RegRead.u64 @rax", "%rax.1 = InsertBits.u64 %rax, 2, 0, 16", "RegWrite.u64 @rax, %rax.1", "Ret"
    }));

  /// <summary>A zero-extending part of a preserving part extends into its parent, which then preserves the rest of the root.</summary>
  [Test]
  public void MixedChain_ExtendsIntoTheParentThenPreserves() {
    var profile = ProfileLoader.Parse("""
      isa: mixed
      addressBits: 32
      byteOrder: little
      registers:
        - {name: r, bits: 32}
        - {name: w, bits: 16, parent: r, offset: 16, write: preserve}
        - {name: b, bits: 8, parent: w, offset: 0, write: zero-extend}
        - {name: n, bits: 4, parent: w, offset: 8, write: preserve}
        - {name: q, bits: 16, parent: r, offset: 0, write: zero-extend}
        - {name: qq, bits: 8, parent: q, offset: 0, write: preserve}
      """);
    var module = MachineSyntax.Parse("machine mixed\n@f { entry: %v = Const.u8 7; RegWrite.u8 @b, %v; RegWrite.u4 @n, 3; RegWrite.u8 @qq, %v; Ret }", id => id == "mixed" ? profile : null);
    Assert.That(MachineVerifier.Verify(module), Is.Empty);
    var normalized = MachineNormalizer.ToRootRegisters(module);
    Assert.That(MachineVerifier.Verify(normalized), Is.Empty);
    Assert.That(normalized.Functions[0].Blocks[0].Instructions.Select(instruction => MachineSyntax.Print(instruction)), Is.EqualTo(new[] {
      "%v = Const.u8 7",
      // b zero-extends into w, which preserves the low half of r.
      "%v.z = ZeroExtend.u16 %v", "%r = RegRead.u32 @r", "%v.z.z = ZeroExtend.u32 %v.z", "%r.1 = InsertBits.u32 %r, %v.z.z, 16, 16", "RegWrite.u32 @r, %r.1",
      // n preserves within w, which preserves within r: one insert at offset 24.
      "%r.2 = RegRead.u32 @r", "%r.3 = InsertBits.u32 %r.2, 3, 24, 4", "RegWrite.u32 @r, %r.3",
      // qq preserves within q, which zero-extends into r: read q, insert, write q zero-extended.
      "%r.4 = RegRead.u32 @r", "%q.field = ExtractBits.u32 %r.4, 0, 16", "%q = Truncate.u16 %q.field", "%v.z.1 = ZeroExtend.u16 %v",
      "%q.1 = InsertBits.u16 %q, %v.z.1, 0, 8", "%q.1.z = ZeroExtend.u32 %q.1", "RegWrite.u32 @r, %q.1.z",
      "Ret"
    }));
  }

  [Test]
  public void Origins_SurviveTheRewrite() {
    var normalized = MachineNormalizer.ToRootRegisters(MachineTesting.Verified("machine test-m32\n@f { entry: RegWrite.u8 @r0l, 1 !at(0x00401000, \"B0 01\"); Ret }"));
    var rewritten = normalized.Functions[0].Blocks[0].Instructions.Take(3).ToList();
    Assert.That(rewritten.Select(instruction => instruction.Origin?.Address), Is.EqualTo(new ulong?[] { 0x00401000, 0x00401000, 0x00401000 }));
  }

  /// <summary>Flags, preserving writes and loops in the interface.</summary>
  [Test]
  public void Interface_FollowsPathsAndPolicies() {
    var module = MachineTesting.Verified("""
      machine test-m32
      @f {
      entry:
        RegWrite.u8 @r0l, 1
        %c = FlagRead.bool @c
        Branch %c, a, b
      a:
        RegWrite.u32 @r2, 0
        FlagUndef @o
        Jump b
      b:
        %x = RegRead.u32 @r2
        %y = RegRead.u32 @r3
        FlagWrite @z, 1
        %again = FlagRead.bool @z
        Branch %again, b, out
      out:
        Ret
      }
      """);
    var signature = MachineInterface.Of(module.Functions[0], module.Profile!);
    Assert.That(signature.RegistersIn, Is.EqualTo(new[] { "r0", "r2", "r3" }));
    Assert.That(signature.RegistersOut, Is.EqualTo(new[] { "r0", "r2" }));
    Assert.That(signature.FlagsIn, Is.EqualTo(new[] { "c" }));
    Assert.That(signature.FlagsOut, Is.EqualTo(new[] { "z", "o" }));
  }

  [Test]
  public void Arguments_AreChecked() {
    var profile = MachineTesting.Profile("test-m32");
    Assert.Throws<ArgumentNullException>(() => MachineNormalizer.ToRootRegisters(null!, profile));
    Assert.Throws<ArgumentNullException>(() => MachineNormalizer.ToRootRegisters(null!));
    Assert.Throws<ArgumentException>(() => MachineNormalizer.ToRootRegisters(MachineTesting.Verified("@f { entry: Ret }")));
    Assert.Throws<ArgumentNullException>(() => MachineInterface.Of(null!, profile));
  }

}
