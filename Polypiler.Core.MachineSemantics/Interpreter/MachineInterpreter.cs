using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Polypiler.Core.MachineSemantics.Regions;
using Polypiler.Core.MachineSemantics.Tracing;
using Polypiler.Core.MachineSemantics.Traps;

namespace Polypiler.Core.MachineSemantics.Interpreter;

/// <summary>The limits of an interpretation.</summary>
/// <param name="MaxSteps">The most instructions it executes before it stops with <see cref="TraceOutcome.BudgetExceeded"/>.</param>
public sealed record MachineInterpreterOptions(long MaxSteps = MachineInterpreterOptions.DefaultMaxSteps) {

  /// <summary>The default of <see cref="MaxSteps"/>.</summary>
  public const long DefaultMaxSteps = 10_000_000;

}

/// <summary>What an interpretation did: the trace, the instructions executed and why it stopped early.</summary>
/// <param name="Trace">The observable trace; its outcome says how the run ended.</param>
/// <param name="Steps">The instructions executed.</param>
/// <param name="Diagnostics">Why the run was aborted or ran out of budget; empty otherwise.</param>
public sealed record MachineExecution(ObservableTrace Trace, long Steps, ImmutableArray<Diagnostic> Diagnostics) {

  /// <inheritdoc/>
  public bool Equals(MachineExecution? other) => other is not null && this.Trace.Equals(other.Trace) && this.Steps == other.Steps && this.Diagnostics.SequenceEqual(other.Diagnostics);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Trace, this.Steps, this.Diagnostics.Length);

}

/// <summary>
/// Runs MachineIR on a <see cref="MachineState"/> and records what it does as an
/// <see cref="ObservableTrace"/>, the second reference of every differential test of a lifter.
/// Instructions execute in block order; registers, flags and memory change only through the machine
/// operations; arithmetic wraps at the instruction's width. Every store is a <c>MemoryWrite</c> event in
/// execution order, every trap a <c>Trap</c> event, <c>Ret</c> a <c>Return</c> event. The run ends
/// <see cref="TraceOutcome.Returned"/> at <c>Ret</c>; <see cref="TraceOutcome.Trapped"/> at a trap that
/// does not resume, a division by zero (<c>divide-error</c>, vector 0) or an access to an address the
/// map does not cover (<c>access-fault</c>); <see cref="TraceOutcome.Aborted"/> with a diagnostic when it
/// reads an uninitialized register, an undefined flag or an unwritten byte (<c>PLY4035</c>), meets a
/// resuming trap no model handles (<c>PLY4034</c>) or an indirect jump outside its targets
/// (<c>PLY4038</c>); and <see cref="TraceOutcome.BudgetExceeded"/> after
/// <see cref="MachineInterpreterOptions.MaxSteps"/> instructions (<c>PLY4039</c>). Diagnostics are
/// returned, never thrown.
/// </summary>
public static class MachineInterpreter {

  /// <summary><c>PLY4034</c>: an instruction the interpreter has no model for. Arguments: what, where.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NoModel = new("PLY4034", DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "opaque instruction {0} at {1} cannot be interpreted", "machine.interp.opaque");

  /// <summary><c>PLY4035</c>: a read of state that has no value. Arguments: the flag, where (other forms through <see cref="DiagnosticDescriptor.At"/>).</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Undefined = new("PLY4035", DiagnosticSeverity.Error, ExitCode.SemanticMismatch, "undefined flag '{0}' is read at {1}", "machine.interp.undefined");

  /// <summary><c>PLY4038</c>: an indirect jump to an address none of its targets has. Arguments: where, the address.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor WrongTarget = new("PLY4038", DiagnosticSeverity.Error, ExitCode.SemanticMismatch, "indirect jump at {0} went to {1}, which is not among its listed targets", "machine.interp.indirect-jump");

  /// <summary><c>PLY4039</c>: the step budget ran out. Arguments: the function, the budget.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Budget = new("PLY4039", DiagnosticSeverity.Error, ExitCode.BudgetExceeded, "the interpretation of '@{0}' exceeded its budget of {1} steps", "machine.interp.budget");

  /// <summary>Runs a function of a verified module.</summary>
  /// <param name="module">The module, verified, with a profile.</param>
  /// <param name="function">The function's name, without <c>@</c>.</param>
  /// <param name="initial">The state to start from; the run changes it.</param>
  /// <param name="options">The limits, or <see langword="null"/> for the defaults.</param>
  /// <returns>The execution.</returns>
  /// <exception cref="ArgumentException">The module has no profile or no such function, or the state is for another profile.</exception>
  public static MachineExecution Run(MachineModule module, string function, MachineState initial, MachineInterpreterOptions? options = null) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(initial);
    var profile = module.Profile ?? throw new ArgumentException("the module declares no machine profile", nameof(module));
    var routine = module.Function(function) ?? throw new ArgumentException($"the module has no function '@{function}'", nameof(function));
    if (!ReferenceEquals(initial.Profile, profile) && initial.Profile != profile)
      throw new ArgumentException($"the state is for '{initial.Profile.Id}', the module for '{profile.Id}'", nameof(initial));
    return new Execution(routine, initial, options ?? new()).Execute();
  }

  /// <summary>The state as a snapshot: every register and flag that holds a value or was set undefined, and every byte written, in runs.</summary>
  /// <param name="state">The state.</param>
  /// <returns>The snapshot.</returns>
  public static MachineSnapshot Snapshot(MachineState state) {
    ArgumentNullException.ThrowIfNull(state);
    var registers = state.Profile.Registers.Where(register => register.IsRoot)
      .Select(register => (register.Name, Value: state.Register(register.Name)))
      .Where(entry => entry.Value is not null)
      .ToImmutableDictionary(entry => entry.Name, entry => entry.Value!.Value, StringComparer.Ordinal);
    var flags = state.Profile.Flags.Where(flag => state.IsFlagSet(flag.Name)).ToImmutableDictionary(flag => flag.Name, flag => state.Flag(flag.Name), StringComparer.Ordinal);
    var memory = ImmutableArray.CreateBuilder<MemoryBytes>();
    ulong start = 0;
    var run = new List<byte>();
    foreach (var (address, value) in state.Memory.Bytes) {
      if (run.Count > 0 && address != start + (ulong)run.Count) {
        memory.Add(new(start, [.. run]));
        run.Clear();
      }

      if (run.Count == 0)
        start = address;
      run.Add(value);
    }

    if (run.Count > 0)
      memory.Add(new(start, [.. run]));
    return new(registers, flags, memory.ToImmutable());
  }

  /// <summary>Why a run stops early.</summary>
  private sealed class Stop(TraceOutcome outcome, Diagnostic? diagnostic) : Exception(diagnostic?.Message) {
    public TraceOutcome Outcome { get; } = outcome;
    public Diagnostic? Diagnostic { get; } = diagnostic;
  }

  private sealed class Execution(MachineFunction function, MachineState state, MachineInterpreterOptions options) {

    private readonly IsaProfile _profile = state.Profile;
    private readonly List<TraceEvent> _events = [];
    private readonly Dictionary<string, ulong> _values = new(StringComparer.Ordinal);
    private long _steps;

    public MachineExecution Execute() {
      TraceOutcome outcome;
      var diagnostics = ImmutableArray<Diagnostic>.Empty;
      try {
        this.Blocks();
        outcome = TraceOutcome.Returned;
      } catch (Stop stop) {
        outcome = stop.Outcome;
        if (stop.Diagnostic is { } diagnostic)
          diagnostics = [diagnostic];
      }

      return new(new(Snapshot(state), [.. this._events], outcome), this._steps, diagnostics);
    }

    private static Stop Abort(Diagnostic diagnostic) => new(TraceOutcome.Aborted, diagnostic);

    private void Blocks() {
      var block = function.Blocks[0];
      while (true) {
        this._values.Clear();
        string? next = null;
        for (var index = 0; index < block.Instructions.Length && next is null; ++index) {
          if (++this._steps > options.MaxSteps) {
            --this._steps;
            throw new Stop(TraceOutcome.BudgetExceeded, Diagnostic.Create(Budget, null, function.Name, options.MaxSteps));
          }

          var instruction = block.Instructions[index];
          var where = MachineVerifier.Location(function, block, index);
          next = this.Step(instruction, where);
          if (next == "") // Ret
            return;
        }

        block = function.Block(next!)!;
      }
    }

    private ulong Value(MachineOperand operand) => operand switch {
      MachineImmediate immediate => immediate.Value,
      _ => this._values[((MachineValue)operand).Name]
    };

    private static long Signed(ulong value, MachineType type) => type.Bits == 64 ? (long)value : ((long)(value << (64 - type.Bits))) >> (64 - type.Bits);

    /// <summary>Executes one instruction; returns the next block's label, <c>""</c> after <c>Ret</c>, or <see langword="null"/> to go on.</summary>
    private string? Step(MachineInstruction instruction, string where) {
      var type = instruction.Type;
      var mask = type.Mask;
      var operands = instruction.Operands;
      ulong Operand(int index) => this.Value(operands[index]);
      void Define(ulong value) => this._values[instruction.Result!] = value & (MachineOpcodes.IsComparison(instruction.Opcode) ? 1UL : mask);

      switch (instruction.Opcode) {
        case MachineOpcode.Const:
          Define(Operand(0));
          return null;
        case MachineOpcode.Add:
          Define(Operand(0) + Operand(1));
          return null;
        case MachineOpcode.Sub:
          Define(Operand(0) - Operand(1));
          return null;
        case MachineOpcode.Mul:
          Define(Operand(0) * Operand(1));
          return null;
        case MachineOpcode.UDiv or MachineOpcode.URem or MachineOpcode.SDiv or MachineOpcode.SRem:
          Define(this.Divide(instruction, Operand(0), Operand(1), where));
          return null;
        case MachineOpcode.And:
          Define(Operand(0) & Operand(1));
          return null;
        case MachineOpcode.Or:
          Define(Operand(0) | Operand(1));
          return null;
        case MachineOpcode.Xor:
          Define(Operand(0) ^ Operand(1));
          return null;
        case MachineOpcode.Shl:
          Define(Operand(1) >= (ulong)type.Bits ? 0 : Operand(0) << (int)Operand(1));
          return null;
        case MachineOpcode.LShr:
          Define(Operand(1) >= (ulong)type.Bits ? 0 : Operand(0) >> (int)Operand(1));
          return null;
        case MachineOpcode.AShr:
          Define((ulong)(Signed(Operand(0), type) >> (int)Math.Min(Operand(1), 63UL)));
          return null;
        case MachineOpcode.Not:
          Define(~Operand(0));
          return null;
        case MachineOpcode.Neg:
          Define(0 - Operand(0));
          return null;
        case MachineOpcode.Eq:
          Define(Operand(0) == Operand(1) ? 1UL : 0);
          return null;
        case MachineOpcode.Ne:
          Define(Operand(0) != Operand(1) ? 1UL : 0);
          return null;
        case MachineOpcode.ULt:
          Define(Operand(0) < Operand(1) ? 1UL : 0);
          return null;
        case MachineOpcode.ULe:
          Define(Operand(0) <= Operand(1) ? 1UL : 0);
          return null;
        case MachineOpcode.SLt:
          Define(Signed(Operand(0), type) < Signed(Operand(1), type) ? 1UL : 0);
          return null;
        case MachineOpcode.SLe:
          Define(Signed(Operand(0), type) <= Signed(Operand(1), type) ? 1UL : 0);
          return null;
        case MachineOpcode.ZeroExtend or MachineOpcode.Truncate:
          Define(Operand(0));
          return null;
        case MachineOpcode.SignExtend:
          Define((ulong)Signed(Operand(0), MachineType.Of(this.WidthOf(operands[0]))));
          return null;
        case MachineOpcode.ExtractBits:
          Define((Operand(0) >> (int)Operand(1)) & MachineType.Of((int)Operand(2)).Mask);
          return null;
        case MachineOpcode.InsertBits:
          var field = MachineType.Of((int)Operand(3)).Mask << (int)Operand(2);
          Define((Operand(0) & ~field) | ((Operand(1) << (int)Operand(2)) & field));
          return null;
        case MachineOpcode.Select:
          Define(Operand(0) != 0 ? Operand(1) : Operand(2));
          return null;
        case MachineOpcode.RegRead:
          Define(this.ReadRegister(instruction.Storage!, where));
          return null;
        case MachineOpcode.RegWrite:
          this.WriteRegister(instruction.Storage!, Operand(1), where);
          return null;
        case MachineOpcode.FlagRead:
          Define(state.Flag(instruction.Storage!) is { } flag
            ? (flag ? 1UL : 0)
            : throw Abort(Diagnostic.Create(Undefined, null, instruction.Storage!, where)));
          return null;
        case MachineOpcode.FlagWrite:
          state.SetFlag(instruction.Storage!, Operand(1) != 0);
          return null;
        case MachineOpcode.FlagUndef:
          state.SetFlag(instruction.Storage!, null);
          return null;
        case MachineOpcode.Load:
          Define(this.Load(Operand(0), type, where));
          return null;
        case MachineOpcode.Store:
          this.Store(Operand(0), Operand(1), type);
          return null;
        case MachineOpcode.MachineTrap:
          this.TakeTrap(instruction.Trap!, instruction, where);
          return null;
        case MachineOpcode.Jump:
          return ((MachineLabel)operands[0]).Name;
        case MachineOpcode.Branch:
          return ((MachineLabel)operands[Operand(0) != 0 ? 1 : 2]).Name;
        case MachineOpcode.IndirectJump:
          return this.IndirectJump(instruction, Operand(0), where);
        default:
          this._events.Add(new Return(null));
          return "";
      }
    }

    /// <summary>The width of a value operand: the type it was defined with.</summary>
    private int WidthOf(MachineOperand operand) {
      var name = ((MachineValue)operand).Name;
      return function.Blocks.SelectMany(block => block.Instructions).First(instruction => instruction.Result == name).Type.Bits;
    }

    private ulong Divide(MachineInstruction instruction, ulong left, ulong right, string where) {
      var type = instruction.Type;
      var signedOverflow = instruction.Opcode is MachineOpcode.SDiv or MachineOpcode.SRem && right == type.Mask && left == 1UL << (type.Bits - 1);
      if (right == 0 || signedOverflow) {
        this._events.Add(new Trap(TrapDescriptor.Name(MachineTrapKind.DivideError), 0, instruction.Origin?.Address));
        throw new Stop(TraceOutcome.Trapped, null);
      }

      return instruction.Opcode switch {
        MachineOpcode.UDiv => left / right,
        MachineOpcode.URem => left % right,
        MachineOpcode.SDiv => (ulong)(Signed(left, type) / Signed(right, type)),
        _ => (ulong)(Signed(left, type) % Signed(right, type))
      };
    }

    private ulong ReadRegister(string name, string where) {
      var register = this._profile.Register(name)!;
      var root = this._profile.Root(register);
      var value = state.Register(root.Name)
        ?? throw Abort(Undefined.At($"uninitialized register '{root.Name}' is read at {where}"));
      return (value >> this._profile.OffsetInRoot(register)) & MachineType.Of(register.Bits).Mask;
    }

    private void WriteRegister(string name, ulong value, string where) {
      var register = this._profile.Register(name)!;
      var root = this._profile.Root(register);
      if (!this._profile.ReplacesRoot(register) && state.Register(root.Name) is null)
        throw Abort(Undefined.At($"uninitialized register '{root.Name}' is read at {where}"));
      state.SetRegister(name, value);
    }

    /// <summary>Faults an access to bytes a given map does not cover.</summary>
    private void CheckMapped(ulong address, int bytes) {
      if (state.Map is not { } map)
        return;
      for (var offset = 0UL; offset < (ulong)bytes; ++offset)
        if (map.RangeAt(address + offset) is null or { Kind: AddressKind.Unmapped }) {
          this._events.Add(new Trap(TrapDescriptor.Name(MachineTrapKind.AccessFault), null, address + offset));
          throw new Stop(TraceOutcome.Trapped, null);
        }
    }

    private int[] ByteOrder(int count) => this._profile.ByteOrder == MachineSemantics.ByteOrder.Little
      ? [.. Enumerable.Range(0, count)]
      : [.. Enumerable.Range(0, count).Reverse()];

    private ulong Load(ulong address, MachineType type, string where) {
      var count = type.Bits / 8;
      this.CheckMapped(address, count);
      var value = 0UL;
      var order = this.ByteOrder(count);
      for (var index = 0; index < count; ++index) {
        var at = address + (ulong)index;
        var data = state.Memory.Read(at) ?? throw Abort(Undefined.At($"unwritten memory at {MachineSyntax.Number(at)} is read at {where}"));
        value |= (ulong)data << (8 * order[index]);
      }

      return value;
    }

    private void Store(ulong address, ulong value, MachineType type) {
      var count = type.Bits / 8;
      this.CheckMapped(address, count);
      var order = this.ByteOrder(count);
      var bytes = Enumerable.Range(0, count).Select(index => (byte)(value >> (8 * order[index]))).ToImmutableArray();
      state.Memory.Write(address, bytes);
      this._events.Add(new MemoryWrite(address, bytes));
    }

    private void TakeTrap(TrapDescriptor trap, MachineInstruction instruction, string where) {
      this._events.Add(new Trap(TrapDescriptor.Name(trap.Kind), trap.Vector is { } vector ? (int)vector : null, instruction.Origin?.Address));
      if (!trap.Resumes)
        throw new Stop(TraceOutcome.Trapped, null);
      if (state.Traps?.TryHandle(trap, state) != true)
        throw Abort(NoModel.At($"{TrapDescriptor.Name(trap.Kind)} trap {trap.Id ?? "without id"} at {where} resumes but no trap model handles it"));
    }

    private string IndirectJump(MachineInstruction instruction, ulong address, string where) {
      if (instruction.Unresolved)
        throw Abort(WrongTarget.At($"indirect jump at {where} has unresolved targets"));
      foreach (var label in instruction.Successors)
        if (function.Block(label)!.Instructions.FirstOrDefault()?.Origin?.Address == address)
          return label;
      throw Abort(Diagnostic.Create(WrongTarget, null, where, MachineSyntax.Number(address)));
    }

  }

}
