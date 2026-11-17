using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Core.MachineSemantics;
using Polypiler.Tests.MachineSemantics.Support;
using Polypiler.Core.MachineSemantics.Traps;

namespace Polypiler.Tests.MachineSemantics;

/// <summary>
/// <c>MachineTrap</c> (#145): the nine kinds, resumption and the terminator rule, vectors against the
/// profile (<c>PLY4025</c>), service contracts and the conservative default (<c>PLY4026</c>), the
/// effect channels and interference, and the link of CPU faults to the canonical traps. Vectors and
/// resumption follow the Intel SDM vol. 3 table 6-1 and the Arm ARM exception model.
/// </summary>
[TestFixture]
public sealed class MachineTrapTests {

  private static string M32(string body) => "machine test-m32\n\n@f {\nentry:\n" + body + "\n}\n";

  private static MachineInstruction Instruction(MachineModule module, int index) => module.Functions[0].Blocks[0].Instructions[index];

  private static MachineInstruction Single(string text) => MachineTesting.Parse("machine test-m32\n@g { entry: " + text + "; Ret }").Functions[0].Blocks[0].Instructions[0];

  /// <summary>Test design 1 and the scenario "A software interrupt with a service contract reads and writes only its registers".</summary>
  [Test]
  public void ServiceContract_NarrowsTheEffect() {
    var module = MachineTesting.Parse(M32("  MachineTrap[kind=software-interrupt, vector=0x21, resumes=true, id=t1] service(\"svc.put\") reads(r0) writes(r1)\n  Ret"));
    Assert.That(MachineVerifier.Verify(module), Is.Empty);
    var trap = Instruction(module, 0);
    var profile = module.Profile!;
    var effect = MachineEffects.Of(trap, profile);
    Assert.That(effect.Writes, Does.Contain(MachineEffects.Trap));
    Assert.That(effect.Reads, Is.EqualTo(new[] { "Machine.Registers:test-m32/r0" }));
    Assert.That(effect.Writes, Is.EqualTo(new[] { "Machine.Registers:test-m32/r1", "Trap" }));
    Assert.That(MachineEffects.Interferes(trap, Single("RegWrite.u32 @r2, 5"), profile), Is.False);
    Assert.That(MachineEffects.Interferes(trap, Single("RegWrite.u32 @r0, 5"), profile), Is.True);
    Assert.That(MachineEffects.Interferes(Single("%x = RegRead.u32 @r1"), trap, profile), Is.True);
  }

  /// <summary>Test design 11 and the scenario "A software interrupt without a contract is conservative".</summary>
  [Test]
  public void NoContract_WarnsAndIsConservative() {
    var module = MachineTesting.Parse(M32("  MachineTrap[kind=software-interrupt, vector=0x10, resumes=true, id=t2]\n  Ret"));
    var problems = MachineVerifier.Verify(module);
    Assert.That(problems.Select(problem => (problem.Code, problem.Severity, problem.Message)), Is.EqualTo(new[] {
      ("PLY4026", DiagnosticSeverity.Warning, "software interrupt 0x10 at @f entry[0] has no service contract; all registers and unknown memory are assumed read and written")
    }));
    var trap = Instruction(module, 0);
    Assert.That(MachineEffects.Interferes(trap, Single("%l = Load.u8 0x1000"), module.Profile!), Is.True);
    Assert.That(MachineEffects.Interferes(trap, Single("FlagUndef @c"), module.Profile!), Is.True);
    Assert.That(MachineEffects.Interferes(trap, Single("%a = Add.u32 1, 2"), module.Profile!), Is.False);
  }

  /// <summary>Test design 2 and the scenario "An undefined-instruction trap terminates its block".</summary>
  [Test]
  public void NonResumingTrap_EndsItsBlockWithoutSuccessors() {
    var module = MachineTesting.Verified(M32("  MachineTrap[kind=undefined-instruction, vector=6, resumes=false, id=t3]"));
    var trap = Instruction(module, 0);
    Assert.That(trap.IsTerminator, Is.True);
    Assert.That(trap.Successors, Is.Empty);
    Assert.That(module.Functions[0].Blocks[0].Terminator, Is.SameAs(trap));
  }

  /// <summary>Test design 12 and the scenario "An instruction after a non-resuming trap is rejected".</summary>
  [Test]
  public void InstructionAfterANonResumingTrap_IsTheTerminatorDiagnostic() {
    var problems = MachineTesting.Problems(M32("  %a = Const.u32 1\n  %b = Const.u32 2\n  MachineTrap[kind=undefined-instruction, vector=6, resumes=false, id=t3]\n  %x = Add.u32 %a, %b\n  Ret"));
    Assert.That(problems.Select(problem => problem.ToString()), Is.EqualTo(new[] {
      "error PLY4005: '@f' is not well-formed MachineIR: an instruction follows the terminator of the block 'entry' at @f entry[3]"
    }));
  }

  /// <summary>Test design 3: each of the nine kinds prints, parses and verifies; halt and undefined-instruction do not resume by default.</summary>
  [TestCase("software-interrupt", "vector=3, ", true)]
  [TestCase("system-call", "", true)]
  [TestCase("undefined-instruction", "vector=6, ", false)]
  [TestCase("privileged-instruction", "vector=0xD, ", false)]
  [TestCase("breakpoint", "vector=3, ", true)]
  [TestCase("halt", "", false)]
  [TestCase("divide-error", "vector=0, ", false)]
  [TestCase("overflow-trap", "vector=4, ", true)]
  [TestCase("access-fault", "vector=0xE, ", false)]
  public void EveryKind_RoundTripsAndVerifies(string kind, string vector, bool resumesByDefault) {
    var written = $"MachineTrap[kind={kind}, {vector}id=k]";
    var module = MachineTesting.Parse(M32($"  {written}" + (resumesByDefault ? "\n  Ret" : "")));
    var trap = Instruction(module, 0);
    Assert.That(trap.Trap!.Resumes, Is.EqualTo(resumesByDefault));
    Assert.That(TrapDescriptor.Name(trap.Trap.Kind), Is.EqualTo(kind));
    Assert.That(MachineVerifier.Verify(module).Where(problem => problem.Severity == DiagnosticSeverity.Error), Is.Empty);
    var printed = MachineSyntax.Print(module);
    Assert.That(printed, Does.Contain($"MachineTrap[kind={kind}, {vector}resumes={(resumesByDefault ? "true" : "false")}, id=k]"));
    Assert.That(MachineSyntax.Print(MachineTesting.Parse(printed)), Is.EqualTo(printed));
  }

  [Test]
  public void ContractWithChannels_RoundTrips() {
    const string text = "machine test-m32\n\n@f {\nentry:\n  MachineTrap[kind=system-call, resumes=true] service(\"linux.write\") reads(r0, r1, r2) writes(r0) channels(\"Memory.Unknown\", \"Device:console\")\n  Ret\n}\n";
    var module = MachineTesting.Verified(text);
    Assert.That(MachineSyntax.Print(module), Is.EqualTo(text));
    var trap = Instruction(module, 0);
    Assert.That(trap.Trap!.Service, Is.EqualTo(new ServiceContract("linux.write", ["r0", "r1", "r2"], ["r0"], ["Memory.Unknown", "Device:console"])));
    Assert.That(trap.Trap.Service!.GetHashCode(), Is.EqualTo(new ServiceContract("linux.write", ["r0", "r1", "r2"], ["r0"], ["Memory.Unknown", "Device:console"]).GetHashCode()));
    Assert.That(MachineEffects.Interferes(trap, Single("Store.u8 0x2000, 1"), module.Profile!), Is.True);
    Assert.That(MachineEffects.Of(trap, module.Profile!).Reads, Does.Contain("Device:console"));
  }

  [Test]
  public void ContractWithoutRegisters_PrintsOnlyItsName() {
    const string text = "machine test-m32\n\n@f {\nentry:\n  MachineTrap[kind=software-interrupt, vector=0x20, resumes=true] service(\"dos.exit\")\n  Ret\n}\n";
    Assert.That(MachineSyntax.Print(MachineTesting.Verified(text)), Is.EqualTo(text));
  }

  /// <summary>Test design 4: a conditional fault is a trapping division, labelled through <see cref="TrapLabels"/>.</summary>
  [Test]
  public void ConditionalFault_IsATrappingDivisionWithACanonicalLabel() {
    var module = MachineTesting.Verified(M32("  %a = RegRead.u32 @r0\n  %b = RegRead.u32 @r1\n  %q = UDiv.u32 %a, %b\n  %r = URem.u32 %a, %b\n  %s = SDiv.u32 %a, %b\n  %t = SRem.u32 %a, %b\n  Ret"));
    Assert.That(module.Functions[0].Blocks[0].Instructions.Any(instruction => instruction.Opcode == MachineOpcode.MachineTrap), Is.False);
    Assert.That(TrapLabels.For(MachineTrapKind.DivideError), Is.EqualTo(IrTrap.DivideByZero));
    Assert.That(TrapLabels.For(MachineTrapKind.OverflowTrap), Is.EqualTo(IrTrap.IntegerOverflow));
    Assert.That(Enum.GetValues<MachineTrapKind>().Count(kind => TrapLabels.For(kind) is null), Is.EqualTo(7));
    Assert.That(MachineEffects.Of(Instruction(module, 2), module.Profile!).Writes, Is.EqualTo(new[] { MachineEffects.Trap }));
  }

  /// <summary>Test design 5 and 6: pure arithmetic does not interfere with a trap; two traps do.</summary>
  [Test]
  public void PureArithmetic_IsFree_TwoTraps_AreOrdered() {
    var profile = MachineTesting.Profile("test-m32");
    var first = Single("MachineTrap[kind=breakpoint, vector=3, id=t1] service(\"debug\")");
    var second = Single("MachineTrap[kind=software-interrupt, vector=0x21, id=t2] service(\"dos\")");
    Assert.That(MachineEffects.Interferes(first, Single("%x = Add.u32 1, 2"), profile), Is.False);
    Assert.That(MachineEffects.Interferes(first, second, profile), Is.True);
    Assert.That(MachineEffects.Of(Single("%x = Add.u32 1, 2"), profile).IsPure, Is.True);
  }

  /// <summary>A non-resuming trap observes every earlier effect, so no store moves behind it.</summary>
  [Test]
  public void NonResumingTrap_KeepsEarlierStoresBeforeIt() {
    var profile = MachineTesting.Profile("test-m32");
    var fault = MachineTesting.Parse(M32("  MachineTrap[kind=undefined-instruction, vector=6, id=t]")).Functions[0].Blocks[0].Instructions[0];
    Assert.That(MachineEffects.Interferes(Single("Store.u8 0x2000, 0xAA"), fault, profile), Is.True);
    Assert.That(MachineEffects.Interferes(Single("RegWrite.u8 @r0l, 1"), fault, profile), Is.True);
    Assert.That(MachineEffects.Of(fault, profile).Writes, Is.EqualTo(new[] { MachineEffects.Trap }));
  }

  /// <summary>Test design 7: vector 0 and vector 255 on a profile with 256 vectors.</summary>
  [TestCase("0")]
  [TestCase("0xFF")]
  public void VectorsAtTheEnds_AreAccepted(string vector)
    => MachineTesting.Verified(M32($"  MachineTrap[kind=breakpoint, vector={vector}, id=t] service(\"debug\")\n  Ret"));

  /// <summary>Test design 8 and the scenario "A vector outside the profile's range is rejected".</summary>
  [TestCase("256", "interrupt vector 256 is outside 0..255 for machine profile 'test-m32'")]
  [TestCase("-1", "interrupt vector -1 is outside 0..255 for machine profile 'test-m32'")]
  public void VectorsOutside_ArePly4025(string vector, string message) {
    var problems = MachineTesting.Problems(M32($"  MachineTrap[kind=software-interrupt, vector={vector}, resumes=true, id=t4] service(\"x\")\n  Ret"));
    Assert.That(problems.Select(problem => (problem.Code, problem.Message)), Is.EqualTo(new[] { ("PLY4025", message) }));
  }

  [Test]
  public void ProfileWithoutVectorCount_RefusesOnlyNegativeVectors() {
    Assert.That(MachineTesting.Problems("machine test-m64\n@f { entry: MachineTrap[kind=software-interrupt, vector=0x1000] service(\"x\"); Ret }"), Is.Empty);
    Assert.That(MachineTesting.Problems("machine test-m64\n@f { entry: MachineTrap[kind=software-interrupt, vector=-5] service(\"x\"); Ret }").Single().Message,
      Is.EqualTo("interrupt vector -5 is outside 0..unbounded for machine profile 'test-m64'"));
  }

  /// <summary>Test design 9: a kind without a vector where none is defined.</summary>
  [Test]
  public void HaltWithoutVector_IsAccepted() => MachineTesting.Verified(M32("  MachineTrap[kind=halt]"));

  /// <summary>Test design 10: a software interrupt needs a vector.</summary>
  [Test]
  public void SoftwareInterruptWithoutVector_IsPly4025()
    => Assert.That(MachineTesting.Problems(M32("  MachineTrap[kind=software-interrupt]\n  Ret")).Select(problem => (problem.Code, problem.Message)),
      Is.EqualTo(new[] { ("PLY4025", "trap kind 'software-interrupt' requires a vector") }));

  [Test]
  public void ContractRegisterUnknown_IsPly4000()
    => Assert.That(MachineTesting.Problems(M32("  MachineTrap[kind=system-call] service(\"s\") reads(r9) writes(q)\n  Ret")).Select(problem => problem.Message),
      Is.EqualTo(new[] { "'r9' is not a register or flag of machine profile 'test-m32'", "'q' is not a register or flag of machine profile 'test-m32'" }));

  [Test]
  public void TrapWithoutAProfile_IsPly4003()
    => Assert.That(MachineTesting.Codes("@f { entry: MachineTrap[kind=halt] }"), Is.EqualTo(new[] { "PLY4003" }));

  [Test]
  public void TrapWithOperands_IsMalformed()
    => Assert.That(MachineTesting.Problems(M32("  MachineTrap[kind=halt] 3")).Single().Message,
      Does.EndWith("'MachineTrap[kind=halt, resumes=false] 3' does not have the operands of MachineTrap (none, and a trap descriptor)"));

  [TestCase("MachineTrap", "expected '[' but found '}'")]
  [TestCase("MachineTrap[kind=halt, kind=halt]", "'kind' is not an attribute of MachineTrap or is given twice")]
  [TestCase("MachineTrap[colour=red]", "'colour' is not an attribute of MachineTrap or is given twice")]
  [TestCase("MachineTrap[kind=crash]", "MachineTrap needs kind= with one of the nine trap kinds")]
  [TestCase("MachineTrap[vector=1]", "MachineTrap needs kind= with one of the nine trap kinds")]
  [TestCase("MachineTrap[kind=halt, vector=x]", "'x' is not a vector")]
  [TestCase("MachineTrap[kind=halt, vector=0xFFFFFFFFFFFFFFFF]", "'0xFFFFFFFFFFFFFFFF' is not a vector")]
  [TestCase("MachineTrap[kind=halt, resumes=maybe]", "resumes must be true or false, not 'maybe'")]
  [TestCase("MachineTrap[kind=halt, id=,]", "expected the value of 'id' but found ','")]
  [TestCase("MachineTrap[kind=halt] service(3)", "expected the service name as a string but found '3'")]
  [TestCase("MachineTrap[kind=halt] service(\"s\") reads(\"r0\")", "expected a name in reads(...) but found 'r0'")]
  [TestCase("MachineTrap[kind=halt] service(\"s\") channels(r0)", "expected a name in channels(...) but found 'r0'")]
  [TestCase("Ret[kind=halt]", "'Ret' takes no attributes")]
  public void BadTrapText_IsPly4006(string text, string reason) {
    var exception = Assert.Throws<MachineException>(() => MachineTesting.Parse("machine test-m32\n@f { entry: " + text + " }"))!;
    Assert.That(exception.Diagnostic.Code, Is.EqualTo("PLY4006"));
    Assert.That(exception.Diagnostic.Message, Does.EndWith(reason));
  }

  [Test]
  public void Kinds_HaveNamesBothWays() {
    foreach (var kind in Enum.GetValues<MachineTrapKind>())
      Assert.That(TrapDescriptor.TryParseKind(TrapDescriptor.Name(kind), out var back) && back == kind, Is.True);
    Assert.That(TrapDescriptor.TryParseKind("nothing", out _), Is.False);
  }

  [Test]
  public void Descriptors_CompareByContent() {
    var a = new TrapDescriptor(MachineTrapKind.Halt, null, false, null) { Id = "h" };
    Assert.That(a, Is.EqualTo(new TrapDescriptor(MachineTrapKind.Halt, null, false, null) { Id = "h" }));
    Assert.That(a.GetHashCode(), Is.EqualTo(new TrapDescriptor(MachineTrapKind.Halt, null, false, null) { Id = "h" }.GetHashCode()));
    Assert.That(a, Is.Not.EqualTo(a with { Id = "g" }));
    Assert.That(a.Equals(null), Is.False);
    Assert.That(new ServiceContract("s", [], [], []).Equals(null), Is.False);
  }

  [Test]
  public void Channels_OverlapByPrefixAndUnknownMemory() {
    Assert.That(MachineEffects.Overlap("Device:*", "Device:vga"), Is.True);
    Assert.That(MachineEffects.Overlap("Device:vga", "Device:*"), Is.True);
    Assert.That(MachineEffects.Overlap("Memory.Unknown", "Memory:ram"), Is.True);
    Assert.That(MachineEffects.Overlap("Memory:ram", "Memory.Unknown"), Is.True);
    Assert.That(MachineEffects.Overlap("Memory:ram", "Memory:rom"), Is.False);
    Assert.That(MachineEffects.Overlap("Memory.Unknown", "Device:vga"), Is.False);
    Assert.That(MachineEffect.None.IsPure, Is.True);
    Assert.That(MachineEffect.None, Is.EqualTo(new MachineEffect([], [])));
    Assert.That(MachineEffect.None.GetHashCode(), Is.EqualTo(new MachineEffect([], []).GetHashCode()));
    Assert.That(MachineEffect.None.Equals(null), Is.False);
    Assert.Throws<ArgumentNullException>(() => MachineEffects.Overlap(null!, "a"));
    Assert.Throws<ArgumentNullException>(() => MachineEffects.Overlap("a", null!));
  }

  [Test]
  public void EffectsOfMachineOperations_NameTheirChannels() {
    var profile = MachineTesting.Profile("test-m32");
    Assert.That(MachineEffects.Of(Single("%x = RegRead.u16 @r0w"), profile).Reads, Is.EqualTo(new[] { "Machine.Registers:test-m32/r0" }));
    Assert.That(MachineEffects.Of(Single("RegWrite.u8 @r0h, 1"), profile), Is.EqualTo(new MachineEffect(["Machine.Registers:test-m32/r0"], ["Machine.Registers:test-m32/r0"])));
    Assert.That(MachineEffects.Of(Single("RegWrite.u32 @r1, 1"), profile), Is.EqualTo(new MachineEffect([], ["Machine.Registers:test-m32/r1"])));
    Assert.That(MachineEffects.Of(Single("%f = FlagRead.bool @c"), profile), Is.EqualTo(new MachineEffect(["Machine.Flags:test-m32/c"], [])));
    Assert.That(MachineEffects.Of(Single("FlagWrite @c, 1"), profile), Is.EqualTo(new MachineEffect([], ["Machine.Flags:test-m32/c"])));
    Assert.That(MachineEffects.Of(Single("Store.u8 1, 1"), profile), Is.EqualTo(new MachineEffect([], ["Memory.Unknown"])));
    Assert.Throws<ArgumentNullException>(() => MachineEffects.Of(null!, profile));
    Assert.Throws<ArgumentNullException>(() => MachineEffects.Of(Single("FlagWrite @c, 1"), null!));
    Assert.Throws<ArgumentNullException>(() => TrapRules.Check(null!, profile, "x").ToList());
    Assert.Throws<ArgumentNullException>(() => TrapRules.Check(new TrapDescriptor(MachineTrapKind.Halt, null, false, null), null!, "x").ToList());
  }

}
