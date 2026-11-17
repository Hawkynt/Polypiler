using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Interpreter;
using Polypiler.Core.MachineSemantics.Regions;
using Polypiler.Core.MachineSemantics.Tracing;
using Polypiler.Core.MachineSemantics.Traps;
using Polypiler.Tests.MachineSemantics.Support;

namespace Polypiler.Tests.MachineSemantics;

/// <summary>
/// The machine-state interpreter (#138): every vector of <c>tests/fixtures/native/mach-interp</c> against
/// the result its C reference printed under gcc, every opcode's arithmetic, the abort paths, the
/// budget, and the values of the sub-register rules of #139 before and after normalization.
/// </summary>
[TestFixture]
public sealed class MachineInterpreterTests {

  private static IEnumerable<InterpreterFixtures.Vector> Vectors() => InterpreterFixtures.Vectors();

  /// <summary>Test design 1–7, 11 and 12: every vector matches the gcc reference, field by field.</summary>
  [TestCaseSource(nameof(Vectors))]
  public void Vector_MatchesTheGccReference(InterpreterFixtures.Vector vector) {
    var execution = InterpreterFixtures.Run(vector);
    Assert.That(execution.Diagnostics, Is.Empty);
    Assert.That(InterpreterFixtures.Describe(execution, vector), Is.EquivalentTo(vector.Expected));
  }

  [Test]
  public void TheReference_CoversEveryRoutine()
    => Assert.That(Vectors().Select(vector => vector.Routine).Distinct(), Is.EquivalentTo(InterpreterFixtures.Routines.Value.Functions.Select(function => function.Name)));

  /// <summary>Scenario "A buffer sum reads memory and returns the total": no memory-write event, a return.</summary>
  [Test]
  public void ByteSum_WritesNoMemoryAndReturns() {
    var execution = InterpreterFixtures.Run(Vectors().First(vector => vector.Routine == "bytesum"));
    Assert.That(execution.Trace.Final.Registers["r0"], Is.EqualTo(0x105UL));
    Assert.That(execution.Trace.Events, Is.EqualTo(new TraceEvent[] { new Return(null) }));
    Assert.That(execution.Trace.Outcome, Is.EqualTo(TraceOutcome.Returned));
    Assert.That(execution.Steps, Is.GreaterThan(4 * 11));
  }

  /// <summary>Scenario "A store before a trap is kept and the trap ends the run".</summary>
  [Test]
  public void Ud_KeepsTheStoreAndEndsWithTheTrap() {
    var execution = InterpreterFixtures.Run(Vectors().First(vector => vector.Routine == "ud"));
    Assert.That(execution.Trace.Outcome, Is.EqualTo(TraceOutcome.Trapped));
    Assert.That(execution.Trace.Final.Memory.Single(), Is.EqualTo(new MemoryBytes(0x2000, [0xAA])));
    Assert.That(execution.Trace.Events[^1], Is.EqualTo(new Trap("undefined-instruction", 6, 0x00401007)));
    Assert.That(execution.Trace.Events[0], Is.EqualTo(new MemoryWrite(0x2000, [0xAA])));
  }

  /// <summary>Test design 12: the store to an unmapped address changes no memory.</summary>
  [Test]
  public void Wild_ChangesNoMemory() {
    var execution = InterpreterFixtures.Run(Vectors().Single(vector => vector.Initial.GetValueOrDefault("r0") == "000C0000"));
    Assert.That(execution.Trace.Final.Memory, Is.Empty);
    Assert.That(execution.Trace.Events, Is.EqualTo(new TraceEvent[] { new Trap("access-fault", null, 0xC0000) }));
  }

  private static MachineExecution Run(string body, Action<MachineState>? setup = null, string machine = "test-m32", IAddressMap? map = null, long? steps = null) {
    var module = MachineTesting.Verified($"machine {machine}\n@f {{\n{body}\n}}\n");
    var state = new MachineState(module.Profile!, map);
    setup?.Invoke(state);
    return MachineInterpreter.Run(module, "f", state, steps is { } budget ? new(budget) : null);
  }

  private static ulong R0(MachineExecution execution) {
    Assert.That(execution.Diagnostics, Is.Empty, string.Join("\n", execution.Diagnostics));
    return execution.Trace.Final.Registers["r0"];
  }

  [TestCase("Add", 0xFFFFFFF0UL, 0x20UL, 0x10UL)]
  [TestCase("Sub", 1UL, 2UL, 0xFFFFFFFFUL)]
  [TestCase("Mul", 0x10000UL, 0x10001UL, 0x10000UL)]
  [TestCase("UDiv", 7UL, 2UL, 3UL)]
  [TestCase("URem", 7UL, 2UL, 1UL)]
  [TestCase("SDiv", 0xFFFFFFF9UL, 2UL, 0xFFFFFFFDUL)]
  [TestCase("SRem", 0xFFFFFFF9UL, 2UL, 0xFFFFFFFFUL)]
  [TestCase("And", 0xF0F0UL, 0xFF00UL, 0xF000UL)]
  [TestCase("Or", 0xF0F0UL, 0xFF00UL, 0xFFF0UL)]
  [TestCase("Xor", 0xF0F0UL, 0xFF00UL, 0x0FF0UL)]
  [TestCase("Shl", 1UL, 31UL, 0x80000000UL)]
  [TestCase("Shl", 1UL, 32UL, 0UL)]
  [TestCase("LShr", 0x80000000UL, 31UL, 1UL)]
  [TestCase("LShr", 0x80000000UL, 40UL, 0UL)]
  [TestCase("AShr", 0x80000000UL, 31UL, 0xFFFFFFFFUL)]
  [TestCase("AShr", 0x80000000UL, 99UL, 0xFFFFFFFFUL)]
  [TestCase("AShr", 0x40000000UL, 30UL, 1UL)]
  public void Binary_WrapsAtTheWidth(string opcode, ulong left, ulong right, ulong expected)
    => Assert.That(R0(Run($"entry:\n  %x = {opcode}.u32 {left}, {right}\n  RegWrite.u32 @r0, %x\n  Ret")), Is.EqualTo(expected));

  [TestCase("Eq", 5UL, 5UL, 1UL)]
  [TestCase("Ne", 5UL, 5UL, 0UL)]
  [TestCase("ULt", 1UL, 0xFFFFFFFFUL, 1UL)]
  [TestCase("ULe", 3UL, 3UL, 1UL)]
  [TestCase("SLt", 1UL, 0xFFFFFFFFUL, 0UL)]
  [TestCase("SLe", 0xFFFFFFFFUL, 0xFFFFFFFFUL, 1UL)]
  public void Comparisons_GiveABool(string opcode, ulong left, ulong right, ulong expected)
    => Assert.That(R0(Run($"entry:\n  %c = {opcode}.u32 {left}, {right}\n  %x = ZeroExtend.u32 %c\n  RegWrite.u32 @r0, %x\n  Ret")), Is.EqualTo(expected));

  [Test]
  public void UnaryConversionsFieldsAndSelect_Compute() {
    Assert.That(R0(Run("entry:\n  %a = Const.u32 5\n  %x = Not.u32 %a\n  RegWrite.u32 @r0, %x\n  Ret")), Is.EqualTo(0xFFFFFFFAUL));
    Assert.That(R0(Run("entry:\n  %a = Const.u32 5\n  %x = Neg.u32 %a\n  RegWrite.u32 @r0, %x\n  Ret")), Is.EqualTo(0xFFFFFFFBUL));
    Assert.That(R0(Run("entry:\n  %a = Const.u8 0x80\n  %x = SignExtend.u32 %a\n  RegWrite.u32 @r0, %x\n  Ret")), Is.EqualTo(0xFFFFFF80UL));
    Assert.That(R0(Run("entry:\n  %a = Const.u32 0x12345678\n  %t = Truncate.u16 %a\n  %x = ZeroExtend.u32 %t\n  RegWrite.u32 @r0, %x\n  Ret")), Is.EqualTo(0x5678UL));
    Assert.That(R0(Run("entry:\n  %a = Const.u32 0x12345678\n  %x = ExtractBits.u32 %a, 8, 12\n  RegWrite.u32 @r0, %x\n  Ret")), Is.EqualTo(0x456UL));
    Assert.That(R0(Run("entry:\n  %a = Const.u32 0x12345678\n  %x = InsertBits.u32 %a, 0xFFF, 4, 8\n  RegWrite.u32 @r0, %x\n  Ret")), Is.EqualTo(0x12345FF8UL));
    Assert.That(R0(Run("entry:\n  %c = Const.bool 0\n  %x = Select.u32 %c, 1, 2\n  RegWrite.u32 @r0, %x\n  Ret")), Is.EqualTo(2UL));
    Assert.That(R0(Run("entry:\n  %c = Const.bool 1\n  %x = Select.u32 %c, 1, 2\n  RegWrite.u32 @r0, %x\n  Ret")), Is.EqualTo(1UL));
  }

  [Test]
  public void SixtyFourBitArithmetic_UsesEveryBit() {
    var execution = Run("entry:\n  %a = Const.u64 0x8000000000000000\n  %b = AShr.u64 %a, 63\n  %c = SDiv.u64 %b, 1\n  RegWrite.u64 @r0, %c\n  Ret", machine: "test-m64");
    Assert.That(R0(execution), Is.EqualTo(ulong.MaxValue));
  }

  [TestCase("UDiv.u32 1, 0")]
  [TestCase("URem.u32 1, 0")]
  [TestCase("SDiv.u32 0x80000000, 0xFFFFFFFF")]
  [TestCase("SRem.u32 0x80000000, 0xFFFFFFFF")]
  public void DivisionFault_IsADivideErrorTrap(string division) {
    var execution = Run($"entry:\n  %x = {division} !at(0x00000010, \"F7 F1\")\n  Ret");
    Assert.That(execution.Trace.Outcome, Is.EqualTo(TraceOutcome.Trapped));
    Assert.That(execution.Trace.Events, Is.EqualTo(new TraceEvent[] { new Trap("divide-error", 0, 0x10) }));
    Assert.That(execution.Diagnostics, Is.Empty);
  }

  /// <summary>Scenario "Reading an undefined flag aborts" and test design 14, flag.</summary>
  [Test]
  public void UndefinedFlagRead_AbortsWithPly4035() {
    var execution = Run("""
      entry:
        %k = RegRead.u32 @r1
        %one = Eq.u32 %k, 1
        Branch %one, a, b
      a:
        FlagUndef @c
        Jump join
      b:
        FlagWrite @c, 1
        Jump join
      join:
        %x = FlagRead.bool @c
        Ret
      """, state => state.SetRegister("r1", 1));
    Assert.That(execution.Trace.Outcome, Is.EqualTo(TraceOutcome.Aborted));
    Assert.That(execution.Diagnostics.Select(diagnostic => diagnostic.ToString()), Is.EqualTo(new[] { "error PLY4035: undefined flag 'c' is read at @f join[0]" }));
    Assert.That(execution.Trace.Events.OfType<Return>(), Is.Empty);
    Assert.That(execution.Trace.Final.Flags["c"], Is.Null);
  }

  /// <summary>Test design 14, register, and an unwritten byte without a map.</summary>
  [Test]
  public void UninitializedState_AbortsWithPly4035() {
    Assert.That(Run("entry:\n  %x = RegRead.u32 @r3\n  Ret").Diagnostics.Single().Message, Is.EqualTo("uninitialized register 'r3' is read at @f entry[0]"));
    Assert.That(Run("entry:\n  RegWrite.u8 @r0l, 1\n  Ret").Diagnostics.Single().Message, Is.EqualTo("uninitialized register 'r0' is read at @f entry[0]"));
    Assert.That(Run("entry:\n  %x = Load.u16 0x10\n  Ret", state => state.Memory.Write(0x10, 1)).Diagnostics.Single().Message, Is.EqualTo("unwritten memory at 0x11 is read at @f entry[0]"));
    Assert.That(Run("entry:\n  %x = FlagRead.bool @z\n  Ret").Diagnostics.Single().Code, Is.EqualTo("PLY4035"));
  }

  /// <summary>Test design 17: an indirect jump to a listed target, outside its targets, and unresolved.</summary>
  [Test]
  public void IndirectJump_FollowsOnlyItsListedTargets() {
    const string body = """
      entry:
        %t = RegRead.u32 @r1
        IndirectJump %t -> one, two
      one:
        RegWrite.u32 @r0, 1 !at(0x00001000, "90")
        Ret
      two:
        RegWrite.u32 @r0, 2 !at(0x00002000, "90")
        Ret
      """;
    Assert.That(R0(Run(body, state => state.SetRegister("r1", 0x2000))), Is.EqualTo(2UL));
    var wrong = Run(body, state => state.SetRegister("r1", 0x3000));
    Assert.That(wrong.Diagnostics.Single().ToString(), Is.EqualTo("error PLY4038: indirect jump at @f entry[1] went to 0x3000, which is not among its listed targets"));
    var unresolved = Run("entry:\n  IndirectJump 0x10 -> unresolved");
    Assert.That(unresolved.Diagnostics.Single().Message, Is.EqualTo("indirect jump at @f entry[0] has unresolved targets"));
    Assert.That(unresolved.Trace.Outcome, Is.EqualTo(TraceOutcome.Aborted));
  }

  /// <summary>Test design 18: an endless loop runs out of its budget.</summary>
  [Test]
  public void EndlessLoop_ExceedsItsBudget() {
    var execution = Run("entry:\n  Jump entry", steps: 1000);
    Assert.That(execution.Trace.Outcome, Is.EqualTo(TraceOutcome.BudgetExceeded));
    Assert.That(execution.Steps, Is.EqualTo(1000));
    Assert.That(execution.Diagnostics.Single().ToString(), Is.EqualTo("error PLY4039: the interpretation of '@f' exceeded its budget of 1000 steps"));
    Assert.That(execution.Diagnostics.Single().ExitCode, Is.EqualTo(ExitCode.BudgetExceeded));
  }

  private sealed class Service : ITrapModel {
    public bool TryHandle(TrapDescriptor trap, MachineState state) {
      if (trap.Vector != 0x21)
        return false;
      state.SetRegister("r1", state.Registers["r0"] + 7);
      return true;
    }
  }

  /// <summary>Test design 10: a resuming trap runs its model; without one, or one that declines, the run aborts.</summary>
  [Test]
  public void ResumingTrap_RunsItsModel() {
    const string body = "entry:\n  MachineTrap[kind=software-interrupt, vector=0x21, id=svc] service(\"add7\") reads(r0) writes(r1)\n  %v = RegRead.u32 @r1\n  RegWrite.u32 @r0, %v\n  Ret";
    var handled = Run(body, state => { state.SetRegister("r0", 5); state.Traps = new Service(); });
    Assert.That(R0(handled), Is.EqualTo(12UL));
    Assert.That(handled.Trace.Events, Is.EqualTo(new TraceEvent[] { new Trap("software-interrupt", 0x21, null), new Return(null) }));
    var unhandled = Run(body, state => state.SetRegister("r0", 5));
    Assert.That(unhandled.Diagnostics.Single().ToString(), Is.EqualTo("error PLY4034: software-interrupt trap svc at @f entry[0] resumes but no trap model handles it"));
    var declined = Run(body.Replace("0x21", "0x22", StringComparison.Ordinal).Replace(", id=svc", "", StringComparison.Ordinal), state => { state.SetRegister("r0", 5); state.Traps = new Service(); });
    Assert.That(declined.Diagnostics.Single().Message, Does.StartWith("software-interrupt trap without id at"));
  }

  [Test]
  public void BigEndianMemory_KeepsTheMostSignificantByteFirst() {
    var profile = ProfileLoader.Parse("isa: be\naddressBits: 32\nbyteOrder: big\nregisters: [{name: d0, bits: 32}]");
    var module = MachineSyntax.Parse("machine be\n@f { entry: Store.u32 0x100, 0x11223344; %x = Load.u16 0x101; %w = ZeroExtend.u32 %x; RegWrite.u32 @d0, %w; Ret }", id => id == "be" ? profile : null);
    Assert.That(MachineVerifier.Verify(module), Is.Empty);
    var execution = MachineInterpreter.Run(module, "f", new MachineState(profile));
    Assert.That(execution.Trace.Events[0], Is.EqualTo(new MemoryWrite(0x100, [0x11, 0x22, 0x33, 0x44])));
    Assert.That(execution.Trace.Final.Registers["d0"], Is.EqualTo(0x2233UL));
  }

  [Test]
  public void LittleEndianMemory_AndSnapshotRuns() {
    var execution = Run("entry:\n  Store.u16 0x100, 0x1122\n  Store.u8 0x105, 0x33\n  %x = Load.u16 0x100\n  %w = ZeroExtend.u32 %x\n  RegWrite.u32 @r0, %w\n  Ret");
    Assert.That(R0(execution), Is.EqualTo(0x1122UL));
    Assert.That(execution.Trace.Final.Memory, Is.EqualTo(new[] { new MemoryBytes(0x100, [0x22, 0x11]), new MemoryBytes(0x105, [0x33]) }));
  }

  [Test]
  public void UnmappedRange_FaultsAndMappedRomReads() {
    var map = new AddressMap([new("ram", 0, 0xFF, AddressKind.Ram, false), new("hole", 0x100, 0x1FF, AddressKind.Unmapped, false)], 32, "test-m32");
    var fault = Run("entry:\n  %x = Load.u16 0xFF\n  Ret", state => state.Memory.Write(0xFF, 1), map: map);
    Assert.That(fault.Trace.Events, Is.EqualTo(new TraceEvent[] { new Trap("access-fault", null, 0x100) }));
    Assert.That(R0(Run("entry:\n  %x = Load.u8 0xFF\n  %w = ZeroExtend.u32 %x\n  RegWrite.u32 @r0, %w\n  Ret", state => state.Memory.Write(0xFF, 9), map: map)), Is.EqualTo(9UL));
  }

  /// <summary>The 6502's flags are bits of P: writing P sets them, reading P composes them.</summary>
  [Test]
  public void Mos6502_ComposesItsStatusRegisterFromTheFlags() {
    var module = MachineTesting.Verified("machine mos6502\n@f { entry: FlagWrite @C, 1; FlagWrite @N, 0; %p = RegRead.u8 @P; RegWrite.u8 @A, %p; Ret }");
    var state = new MachineState(module.Profile!).SetRegister("P", 0x80);
    Assert.That(state.Flag("N"), Is.True);
    Assert.That(state.Flag("C"), Is.False);
    var execution = MachineInterpreter.Run(module, "f", state);
    Assert.That(execution.Trace.Final.Registers["A"], Is.EqualTo(0x01UL));
    Assert.That(execution.Trace.Final.Flags["C"], Is.True);
    var undefined = MachineTesting.Verified("machine mos6502\n@f { entry: FlagUndef @V; %p = RegRead.u8 @P; Ret }");
    Assert.That(MachineInterpreter.Run(undefined, "f", new MachineState(undefined.Profile!).SetRegister("P", 0)).Diagnostics.Single().Message,
      Is.EqualTo("uninitialized register 'P' is read at @f entry[1]"));
    Assert.That(new MachineState(module.Profile!).SetFlag("C", true).Flag("C"), Is.True);
  }

  /// <summary>
  /// #139 test design 1–4 and 7 and both value scenarios: every sub-register rule gives the same final
  /// state before and after normalization, and the values the bit positions say.
  /// </summary>
  [TestCase("test-m32", "RegWrite.u8 @r0l, 0xFF", 0x12345678UL, 0x123456FFUL)]
  [TestCase("test-m32", "RegWrite.u8 @r0h, 0xAB", 0x12345678UL, 0x1234AB78UL)]
  [TestCase("test-m64", "RegWrite.u32 @r0d, 1", ulong.MaxValue, 0x0000000000000001UL)]
  [TestCase("test-m32", "%w = RegRead.u16 @r0w; %z = ZeroExtend.u32 %w; RegWrite.u32 @r0, %z", 0x12345678UL, 0x5678UL)]
  [TestCase("test-m32", "RegWrite.u16 @r0w, 0xFFFF", 0UL, 0x0000FFFFUL)]
  [TestCase("x86-32", "RegWrite.u8 @ah, 0xAB", 0x12345678UL, 0x1234AB78UL)]
  [TestCase("x86-64", "RegWrite.u16 @ax, 0xBEEF", 0x1122334455667788UL, 0x112233445566BEEFUL)]
  [TestCase("x86-64", "RegWrite.u32 @eax, 0xBEEF", 0x1122334455667788UL, 0x000000000000BEEFUL)]
  public void SubRegisterRules_HoldBeforeAndAfterNormalization(string machine, string body, ulong entry, ulong expected) {
    var module = MachineTesting.Verified($"machine {machine}\n@f {{ entry: {body}; Ret }}");
    var root = module.Profile!.Registers[0].Name;
    foreach (var form in new[] { module, MachineNormalizer.ToRootRegisters(module) }) {
      var execution = MachineInterpreter.Run(form, "f", new MachineState(form.Profile!).SetRegister(root, entry));
      Assert.That(execution.Trace.Final.Registers[root], Is.EqualTo(expected), MachineSyntax.Print(form));
    }
  }

  /// <summary>#139 scenario "Flags written on two paths are read in the join": each path gives its value.</summary>
  [TestCase(1UL, 1UL)]
  [TestCase(0UL, 0UL)]
  public void FlagWrittenOnTwoPaths_IsReadInTheJoin(ulong r1, ulong expected)
    => Assert.That(R0(Run("""
      entry:
        %k = RegRead.u32 @r1
        %one = Eq.u32 %k, 1
        Branch %one, a, b
      a:
        FlagWrite @c, 1
        Jump join
      b:
        FlagWrite @c, 0
        Jump join
      join:
        %c = FlagRead.bool @c
        %x = ZeroExtend.u32 %c
        RegWrite.u32 @r0, %x
        Ret
      """, state => state.SetRegister("r1", r1))), Is.EqualTo(expected));

  [Test]
  public void StateSetters_RefuseWhatTheProfileLacks() {
    var state = new MachineState(MachineTesting.Profile("test-m32"));
    Assert.Throws<ArgumentException>(() => state.SetRegister("r9", 0));
    Assert.Throws<ArgumentException>(() => state.SetFlag("q", true));
    Assert.Throws<ArgumentException>(() => state.SetRegister("r0l", 1));
    Assert.That(state.SetRegister("r0", 0x1FF_FFFF_FFFF).Register("r0"), Is.EqualTo(0xFFFFFFFFUL));
    Assert.That(state.Register("r1"), Is.Null);
    Assert.That(state.IsFlagSet("c"), Is.False);
    Assert.That(state.Map, Is.Null);
    Assert.Throws<ArgumentNullException>(() => _ = new MachineState(null!));
    Assert.Throws<ArgumentNullException>(() => state.Memory.Write(0, null!));
    Assert.That(state.Memory.Read(5), Is.Null);
  }

  [Test]
  public void Run_ChecksItsArguments() {
    var module = MachineTesting.Verified("machine test-m32\n@f { entry: Ret }");
    var state = new MachineState(module.Profile!);
    Assert.Throws<ArgumentNullException>(() => MachineInterpreter.Run(null!, "f", state));
    Assert.Throws<ArgumentNullException>(() => MachineInterpreter.Run(module, null!, state));
    Assert.Throws<ArgumentNullException>(() => MachineInterpreter.Run(module, "f", null!));
    Assert.Throws<ArgumentException>(() => MachineInterpreter.Run(module, "g", state));
    Assert.Throws<ArgumentException>(() => MachineInterpreter.Run(MachineTesting.Verified("@f { entry: Ret }"), "f", state));
    Assert.Throws<ArgumentException>(() => MachineInterpreter.Run(module, "f", new MachineState(MachineTesting.Profile("test-m64"))));
    Assert.That(MachineInterpreter.Run(module, "f", new MachineState(ProfileLoader.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(Polypiler.Prototype.Testing.PrototypeFixture.RepositoryRoot, "spec", "machine", "test-m32.yaml"))))).Trace.Outcome,
      Is.EqualTo(TraceOutcome.Returned));
    Assert.Throws<ArgumentNullException>(() => MachineInterpreter.Snapshot(null!));
  }

  [Test]
  public void Execution_ComparesByContent() {
    var module = MachineTesting.Verified("machine test-m32\n@f { entry: Ret }");
    var a = MachineInterpreter.Run(module, "f", new MachineState(module.Profile!));
    var b = MachineInterpreter.Run(module, "f", new MachineState(module.Profile!));
    Assert.That(a, Is.EqualTo(b));
    Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
    Assert.That(a.Equals(null), Is.False);
    Assert.That(new MachineInterpreterOptions().MaxSteps, Is.EqualTo(MachineInterpreterOptions.DefaultMaxSteps));
    Assert.That(ImmutableArray<Diagnostic>.Empty, Is.EqualTo(a.Diagnostics));
  }

}
