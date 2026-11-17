using System;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Interpreter;
using Polypiler.Core.MachineSemantics.Tracing;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tests.MachineSemantics.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Native;

/// <summary>
/// The steps of <c>tests/features/native/mach-ir.feature</c>, <c>mach-trap.feature</c> and
/// <c>mach-interp.feature</c>: MachineIR parsed, verified, normalized and interpreted in process, the
/// interpreter's vectors compared with what the gcc reference printed.
/// </summary>
[Binding]
[Scope(Feature = "MachineIR register and flag model")]
[Scope(Feature = "Machine traps")]
[Scope(Feature = "Machine-state interpreter")]
public sealed class MachineSteps {

  private MachineModule? _module;
  private MachineModule? _normalized;
  private Diagnostic[] _problems = [];
  private string? _routine;
  private MachineState? _state;
  private MachineExecution? _execution;
  private ulong _initialR0;
  private ulong _initialR1;

  private MachineModule Module => this._module ?? throw new AssertionException("no MachineIR was given");

  private MachineExecution Execution => this._execution ?? throw new AssertionException("nothing was run");

  private static ulong Hex(string text) => ulong.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

  private MachineInstruction Trap => this.Module.Functions[0].Blocks[0].Instructions[0];

  /// <summary>A module, with <c>\n</c> for line breaks.</summary>
  [Given("^the MachineIR " + StepText.Quoted + "$")]
  public void TheMachineIr(string text) => this._module = MachineTesting.Parse(StepText.Unescape(text));

  /// <summary>Normalizes the module after verifying it.</summary>
  [When("^it is normalized to root registers$")]
  public void Normalize() {
    Assert.That(MachineVerifier.Verify(this.Module), Is.Empty);
    this._normalized = MachineNormalizer.ToRootRegisters(this.Module);
    Assert.That(MachineVerifier.Verify(this._normalized), Is.Empty);
  }

  /// <summary>Verifies the module.</summary>
  [When("^it is verified$")]
  public void Verify() => this._problems = [.. MachineVerifier.Verify(this.Module)];

  /// <summary>The normalized entry block, statements separated by <c>; </c>.</summary>
  [Then("^the entry block is " + StepText.Quoted + "$")]
  public void EntryBlock(string expected)
    => Assert.That(string.Join("; ", this._normalized!.Functions[0].Blocks[0].Instructions.Select(instruction => MachineSyntax.Print(instruction, this._normalized))), Is.EqualTo(expected));

  /// <summary>An instruction of the normalized function, and that it does not take the root from its caller.</summary>
  [Then("^the function contains " + StepText.Quoted + " and (\\w+) is no input of it$")]
  public void ContainsAndNoInput(string instruction, string register) => Assert.Multiple(() => {
    Assert.That(MachineSyntax.Print(this._normalized!), Does.Contain(instruction));
    Assert.That(MachineInterface.Of(this._normalized!.Functions[0], this._normalized.Profile!).RegistersIn, Does.Not.Contain(register));
  });

  /// <summary>Runs the given form (normalized when there is one) from one register value and checks a final register.</summary>
  [Then("^with (\\w+) = (0x[0-9A-F]+) on entry the final (\\w+) is (0x[0-9A-F]+)$")]
  public void WithEntryValue(string input, string value, string output, string expected) {
    foreach (var form in new[] { this.Module, this._normalized }.OfType<MachineModule>()) {
      var state = new MachineState(form.Profile!).SetRegister(input, Hex(value));
      var execution = MachineInterpreter.Run(form, form.Functions[0].Name, state);
      Assert.That(execution.Diagnostics, Is.Empty);
      Assert.That(execution.Trace.Final.Registers[output], Is.EqualTo(Hex(expected)));
    }
  }

  /// <summary>No diagnostic at all.</summary>
  [Then("^verification succeeds$")]
  public void Succeeds() => Assert.That(this._problems, Is.Empty);

  /// <summary>Exactly one diagnostic, an error with the code and message.</summary>
  [Then("^verification fails with (PLY\\d{4}) and the message " + StepText.Quoted + "$")]
  public void Fails(string code, string message)
    => Assert.That(this._problems.Select(problem => (problem.Code, problem.Severity, problem.Message)), Is.EqualTo(new[] { (code, DiagnosticSeverity.Error, message) }));

  /// <summary>Exactly one warning.</summary>
  [Then("^the diagnostics are the warning (PLY\\d{4}) " + StepText.Quoted + "$")]
  public void Warning(string code, string message)
    => Assert.That(this._problems.Select(problem => (problem.Code, problem.Severity, problem.Message)), Is.EqualTo(new[] { (code, DiagnosticSeverity.Warning, message) }));

  /// <summary>The channels of the first instruction, a trap with a contract.</summary>
  [Then("^the trap is on channel " + StepText.Quoted + " and reads register (\\w+) and writes register (\\w+)$")]
  public void TrapChannels(string channel, string read, string written) {
    var effect = MachineEffects.Of(this.Trap, this.Module.Profile!);
    Assert.That(effect.Writes, Is.EqualTo(new[] { MachineEffects.Register(this.Module.Profile!, written), channel }));
    Assert.That(effect.Reads, Is.EqualTo(new[] { MachineEffects.Register(this.Module.Profile!, read) }));
  }

  /// <summary>Whether the first instruction, a trap, interferes with another instruction.</summary>
  [Then("^the trap interferes with " + StepText.Quoted + ": (true|false)$")]
  public void Interferes(string instruction, bool expected) {
    var other = MachineTesting.Parse($"machine {this.Module.Profile!.Id}\n@g {{ entry: {instruction}; Ret }}").Functions[0].Blocks[0].Instructions[0];
    Assert.That(MachineEffects.Interferes(this.Trap, other, this.Module.Profile!), Is.EqualTo(expected));
  }

  /// <summary>The trap is the terminator of its block.</summary>
  [Then("^the trap ends its block and has no successors$")]
  public void EndsBlock() => Assert.Multiple(() => {
    Assert.That(this.Module.Functions[0].Blocks[0].Terminator, Is.SameAs(this.Trap));
    Assert.That(this.Trap.Successors, Is.Empty);
  });

  /// <summary>A fixture routine with two registers.</summary>
  [Given("^the routine " + StepText.Quoted + " with r0 = (0x[0-9A-F]+) and r1 = (0x[0-9A-F]+)$")]
  public void RoutineWithRegisters(string routine, string r0, string r1) {
    this.Routine(routine);
    (this._initialR0, this._initialR1) = (Hex(r0), Hex(r1));
    this._state!.SetRegister("r0", this._initialR0).SetRegister("r1", this._initialR1);
  }

  /// <summary>A fixture routine with a buffer and two registers.</summary>
  [Given("^the routine " + StepText.Quoted + " with memory (0x[0-9A-F]+) = ([0-9A-F ]+), r0 = (0x[0-9A-F]+) and r1 = (0x[0-9A-F]+)$")]
  public void RoutineWithMemory(string routine, string address, string bytes, string r0, string r1) {
    this.RoutineWithRegisters(routine, r0, r1);
    this._state!.Memory.Write(Hex(address), Convert.FromHexString(bytes.Replace(" ", "", StringComparison.Ordinal)));
  }

  /// <summary>A fixture routine without inputs.</summary>
  [Given("^the routine " + StepText.Quoted + "$")]
  public void Routine(string routine) {
    this._module = InterpreterFixtures.Routines.Value;
    this._routine = routine;
    this._state = new MachineState(this._module.Profile!);
  }

  /// <summary>Runs the fixture routine.</summary>
  [When("^MachineInterpreter.Run executes it$")]
  public void RunRoutine() => this._execution = MachineInterpreter.Run(this.Module, this._routine!, this._state!);

  /// <summary>Runs a function of the given module, unverified, from an empty state.</summary>
  [When("^MachineInterpreter.Run executes \"@(\\w+)\"$")]
  public void RunFunction(string function) => this._execution = MachineInterpreter.Run(this.Module, function, new MachineState(this.Module.Profile!));

  /// <summary>A final register.</summary>
  [Then("^the final (\\w+) is (0x[0-9A-F]+)$")]
  public void FinalRegister(string register, string value) => Assert.That(this.Execution.Trace.Final.Registers[register], Is.EqualTo(Hex(value)));

  /// <summary>The four flags of test-m32.</summary>
  [Then("^the final flags are c = ([01]), z = ([01]), s = ([01]), o = ([01])$")]
  public void FinalFlags(int c, int z, int s, int o) {
    var flags = this.Execution.Trace.Final.Flags;
    Assert.That(new[] { flags["c"], flags["z"], flags["s"], flags["o"] }, Is.EqualTo(new bool?[] { c == 1, z == 1, s == 1, o == 1 }));
  }

  /// <summary>The captured gcc result of the same vector, field by field.</summary>
  [Then("^the result equals the gcc reference for that vector$")]
  public void EqualsReference() {
    var vector = InterpreterFixtures.Vectors().Single(candidate => candidate.Routine == this._routine
      && candidate.Initial.GetValueOrDefault("r1") is { } r1 && Hex("0x" + r1) == this._initialR1
      && Hex("0x" + candidate.Initial["r0"]) == this._initialR0);
    Assert.That(InterpreterFixtures.Describe(this.Execution, vector), Is.EquivalentTo(vector.Expected));
  }

  /// <summary>No store happened.</summary>
  [Then("^the trace contains no memory-write event$")]
  public void NoMemoryWrite() => Assert.That(this.Execution.Trace.Events.OfType<MemoryWrite>(), Is.Empty);

  /// <summary>The run trapped after the store.</summary>
  [Then("^the outcome is Trapped, the final byte at (0x[0-9A-F]+) is (0x[0-9A-F]+) and the last event is trap " + StepText.Quoted + "$")]
  public void Trapped(string address, string value, string kind) => Assert.Multiple(() => {
    Assert.That(this.Execution.Trace.Outcome, Is.EqualTo(TraceOutcome.Trapped));
    Assert.That(this.Execution.Trace.Final.Memory.Single(), Is.EqualTo(new MemoryBytes(Hex(address), [(byte)Hex(value)])));
    Assert.That(this.Execution.Trace.Events[^1], Is.InstanceOf<Trap>().With.Property(nameof(Trap.Kind)).EqualTo(kind));
  });

  /// <summary>The run aborted on an undefined flag.</summary>
  [Then("^the outcome is Aborted with (PLY\\d{4}) naming flag " + StepText.Quoted + "$")]
  public void Aborted(string code, string flag) => Assert.Multiple(() => {
    Assert.That(this.Execution.Trace.Outcome, Is.EqualTo(TraceOutcome.Aborted));
    Assert.That(this.Execution.Diagnostics.Single().Code, Is.EqualTo(code));
    Assert.That(this.Execution.Diagnostics.Single().Message, Does.Contain($"'{flag}'"));
  });

}
