using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Polypiler.Core.MachineSemantics;

/// <summary>
/// The rules of MachineIR. Structure (<c>PLY4005</c>): every block ends in exactly one terminator, labels
/// are unique and jump targets exist, a value is defined once in its function and before its use in its
/// block, and every operation has the operands and types its opcode asks for. Machine state: machine
/// operations need a profile (<c>PLY4003</c>), name its registers and flags (<c>PLY4000</c>) at their
/// width (<c>PLY4001</c>), never read a flag that only an undefining instruction reaches
/// (<c>PLY4002</c>), and name no sub-register once the function is normalized to root registers
/// (<c>PLY4004</c>).
/// </summary>
public static class MachineVerifier {

  /// <summary>Verifies a module.</summary>
  /// <param name="module">The module.</param>
  /// <returns>What is wrong, in function, block and instruction order; empty when nothing is.</returns>
  public static ImmutableArray<Diagnostic> Verify(MachineModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var problems = ImmutableArray.CreateBuilder<Diagnostic>();
    foreach (var function in module.Functions)
      problems.AddRange(Verify(function, module));
    return problems.ToImmutable();
  }

  /// <summary>Verifies one function of a module.</summary>
  /// <param name="function">The function.</param>
  /// <param name="module">The module that gives the profile.</param>
  /// <returns>What is wrong.</returns>
  public static ImmutableArray<Diagnostic> Verify(MachineFunction function, MachineModule module) {
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(module);
    var checker = new Checker(function, module);
    checker.Run();
    return checker.Problems.ToImmutable();
  }

  /// <summary>Where an instruction stands, as diagnostics name it: <c>@f entry[2]</c>, and its origin's address when it has one.</summary>
  /// <param name="function">The function.</param>
  /// <param name="block">The block.</param>
  /// <param name="index">The 0-based index in the block.</param>
  /// <returns>The text.</returns>
  public static string Location(MachineFunction function, MachineBlock block, int index) {
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(block);
    var text = $"@{function.Name} {block.Label}[{index}]";
    return block.Instructions[index].Origin is { } origin ? $"{text} (0x{origin.Address:X8})" : text;
  }

  private sealed class Checker(MachineFunction function, MachineModule module) {

    public ImmutableArray<Diagnostic>.Builder Problems { get; } = ImmutableArray.CreateBuilder<Diagnostic>();

    private readonly IsaProfile? _profile = module.Profile;
    private readonly Dictionary<string, MachineType> _values = new(StringComparer.Ordinal);

    private void Malformed(string reason) => this.Problems.Add(Diagnostic.Create(MachineDiagnostics.Malformed, null, function.Name, reason));

    public void Run() {
      if (function.Blocks.IsEmpty) {
        this.Malformed("it has no block");
        return;
      }

      var labels = new HashSet<string>(StringComparer.Ordinal);
      foreach (var block in function.Blocks)
        if (!labels.Add(block.Label))
          this.Malformed($"the label '{block.Label}' is used twice");

      var usesMachine = function.Blocks.SelectMany(block => block.Instructions).Any(instruction => MachineOpcodes.IsMachineOperation(instruction.Opcode));
      if (usesMachine && this._profile is null)
        this.Problems.Add(Diagnostic.Create(MachineDiagnostics.NoProfile, null, function.Name, module.Name));

      foreach (var block in function.Blocks) {
        if (block.Instructions.IsEmpty || !block.Instructions[^1].IsTerminator)
          this.Malformed($"the block '{block.Label}' does not end in a terminator");
        for (var index = 0; index < block.Instructions.Length; ++index) {
          var instruction = block.Instructions[index];
          var where = Location(function, block, index);
          if (instruction.IsTerminator && index < block.Instructions.Length - 1)
            this.Malformed($"an instruction follows the terminator of the block '{block.Label}' at {Location(function, block, index + 1)}");
          foreach (var target in instruction.Successors)
            if (!labels.Contains(target))
              this.Malformed($"{where} jumps to the unknown block '{target}'");
          this.Instruction(instruction, block, index, where);
        }
      }

      if (this._profile is not null && this.Problems.Count == 0)
        this.UndefinedFlags(this._profile);
    }

    private void Instruction(MachineInstruction instruction, MachineBlock block, int index, string where) {
      var defined = block.Instructions.Take(index).Select(other => other.Result).Where(name => name is not null).ToHashSet(StringComparer.Ordinal);
      var operands = instruction.Operands;
      var shapeBefore = this.Problems.Count;
      this.Shape(instruction, where);
      if (this.Problems.Count > shapeBefore)
        return;

      foreach (var value in operands.OfType<MachineValue>())
        if (!defined.Contains(value.Name))
          this.Malformed($"{where} uses %{value.Name}, which is not defined before it in the block '{block.Label}'");

      if (instruction.Result is { } result) {
        if (this._values.ContainsKey(result))
          this.Malformed($"{where} defines %{result} a second time");
        this._values[result] = ResultType(instruction);
      }

      this.Types(instruction, where);
      if (this._profile is { } profile && instruction.Storage is { } storage)
        this.Storage(instruction, storage, profile, where);
    }

    private static MachineType ResultType(MachineInstruction instruction) => MachineOpcodes.IsComparison(instruction.Opcode) ? MachineType.Bool : instruction.Type;

    /// <summary>The operand kinds and the type of an instruction, as its opcode asks.</summary>
    private void Shape(MachineInstruction instruction, string where) {
      var opcode = instruction.Opcode;
      var operands = instruction.Operands;
      bool IsData(int index) => operands[index] is MachineValue or MachineImmediate;
      bool IsImmediate(int index) => operands[index] is MachineImmediate;
      bool IsStorage(int index) => operands[index] is MachineStorage;
      bool IsLabel(int index) => operands[index] is MachineLabel;

      var (count, ok, typed) = opcode switch {
        MachineOpcode.Const => (1, operands.Length == 1 && IsImmediate(0), true),
        _ when MachineOpcodes.IsBinary(opcode) || MachineOpcodes.IsComparison(opcode) => (2, operands.Length == 2 && IsData(0) && IsData(1), true),
        MachineOpcode.Not or MachineOpcode.Neg or MachineOpcode.ZeroExtend or MachineOpcode.SignExtend or MachineOpcode.Truncate => (1, operands.Length == 1 && IsData(0), true),
        MachineOpcode.ExtractBits => (3, operands.Length == 3 && IsData(0) && IsImmediate(1) && IsImmediate(2), true),
        MachineOpcode.InsertBits => (4, operands.Length == 4 && IsData(0) && IsData(1) && IsImmediate(2) && IsImmediate(3), true),
        MachineOpcode.Select => (3, operands.Length == 3 && IsData(0) && IsData(1) && IsData(2), true),
        MachineOpcode.RegRead => (1, operands.Length == 1 && IsStorage(0), true),
        MachineOpcode.RegWrite => (2, operands.Length == 2 && IsStorage(0) && IsData(1), true),
        MachineOpcode.FlagRead => (1, operands.Length == 1 && IsStorage(0), true),
        MachineOpcode.FlagWrite => (2, operands.Length == 2 && IsStorage(0) && IsData(1), false),
        MachineOpcode.FlagUndef => (1, operands.Length == 1 && IsStorage(0), false),
        MachineOpcode.Load => (1, operands.Length == 1 && IsData(0), true),
        MachineOpcode.Store => (2, operands.Length == 2 && IsData(0) && IsData(1), true),
        MachineOpcode.Jump => (1, operands.Length == 1 && IsLabel(0), false),
        MachineOpcode.Branch => (3, operands.Length == 3 && IsData(0) && IsLabel(1) && IsLabel(2), false),
        MachineOpcode.IndirectJump => (1, operands.Length >= 1 && IsData(0) && operands.Skip(1).All(operand => operand is MachineLabel) && (operands.Length > 1) != instruction.Unresolved, false),
        _ => (0, operands.IsEmpty, false)
      };

      if (!ok)
        this.Malformed($"{where} '{instruction}' does not have the operands of {opcode}" + (opcode == MachineOpcode.IndirectJump ? " (a target list or 'unresolved')" : $" ({count})"));
      else if (typed != (instruction.Type != MachineType.Void))
        this.Malformed(typed ? $"{where} '{instruction}' needs a type" : $"{where} '{instruction}' takes no type");
      else if (opcode == MachineOpcode.FlagRead && instruction.Type != MachineType.Bool)
        this.Malformed($"{where} '{instruction}' reads a flag, which is bool");
      else if (MachineOpcodes.HasResult(opcode) != (instruction.Result is not null))
        this.Malformed($"{where} '{instruction}' " + (instruction.Result is null ? "must define a value" : "defines no value"));
    }

    private MachineType? TypeOf(MachineOperand operand) => operand is MachineValue value && this._values.TryGetValue(value.Name, out var type) ? type : null;

    /// <summary>Checks that an operand has a type; an immediate must fit it.</summary>
    private void Expect(MachineOperand operand, MachineType type, string where, string what) {
      if (operand is MachineImmediate immediate) {
        if ((immediate.Value & ~type.Mask) != 0)
          this.Malformed($"{where} {what} {immediate} does not fit {type}");
      } else if (this.TypeOf(operand) is { } actual && actual != type) {
        this.Malformed($"{where} {what} {operand} is {actual}, not {type}");
      }
    }

    private void Types(MachineInstruction instruction, string where) {
      var type = instruction.Type;
      var operands = instruction.Operands;
      var address = this._profile is { } profile ? MachineType.Of(profile.AddressBits) : (MachineType?)null;
      switch (instruction.Opcode) {
        case MachineOpcode.Const:
          this.Expect(operands[0], type, where, "the constant");
          break;
        case var opcode when MachineOpcodes.IsBinary(opcode) || MachineOpcodes.IsComparison(opcode):
          this.Expect(operands[0], type, where, "the left operand");
          this.Expect(operands[1], type, where, "the right operand");
          break;
        case MachineOpcode.Not or MachineOpcode.Neg:
          this.Expect(operands[0], type, where, "the operand");
          break;
        case MachineOpcode.ZeroExtend or MachineOpcode.SignExtend or MachineOpcode.Truncate:
          if (operands[0] is MachineImmediate)
            this.Malformed($"{where} '{instruction}' converts a value, not a constant");
          else if (this.TypeOf(operands[0]) is { } from && (instruction.Opcode == MachineOpcode.Truncate ? from.Bits <= type.Bits : from.Bits >= type.Bits))
            this.Malformed($"{where} '{instruction}' converts {from} to {type}, which is not " + (instruction.Opcode == MachineOpcode.Truncate ? "narrower" : "wider"));
          break;
        case MachineOpcode.ExtractBits or MachineOpcode.InsertBits:
          var fieldAt = instruction.Opcode == MachineOpcode.ExtractBits ? 1 : 2;
          this.Expect(operands[0], type, where, "the operand");
          if (instruction.Opcode == MachineOpcode.InsertBits)
            this.Expect(operands[1], type, where, "the inserted value");
          var offset = ((MachineImmediate)operands[fieldAt]).Value;
          var bits = ((MachineImmediate)operands[fieldAt + 1]).Value;
          if (bits < 1 || offset + bits > (ulong)type.Bits || offset >= (ulong)type.Bits)
            this.Malformed($"{where} the field {offset}..{offset + bits - 1} does not lie in {type}");
          break;
        case MachineOpcode.Select:
          this.Expect(operands[0], MachineType.Bool, where, "the condition");
          this.Expect(operands[1], type, where, "the first choice");
          this.Expect(operands[2], type, where, "the second choice");
          break;
        case MachineOpcode.RegWrite:
          this.Expect(operands[1], type, where, "the written value");
          break;
        case MachineOpcode.FlagWrite:
          this.Expect(operands[1], MachineType.Bool, where, "the written value");
          break;
        case MachineOpcode.Load or MachineOpcode.IndirectJump when address is { } width:
          this.Expect(operands[0], width, where, "the address");
          break;
        case MachineOpcode.Store when address is { } width:
          this.Expect(operands[0], width, where, "the address");
          this.Expect(operands[1], type, where, "the stored value");
          break;
        case MachineOpcode.Branch:
          this.Expect(operands[0], MachineType.Bool, where, "the condition");
          break;
      }

      if (instruction.Opcode is MachineOpcode.Load or MachineOpcode.Store && type.Bits % 8 != 0)
        this.Malformed($"{where} '{instruction}' accesses memory in {type}, which is no whole number of bytes");
    }

    private void Storage(MachineInstruction instruction, string storage, IsaProfile profile, string where) {
      var isFlag = instruction.Opcode is MachineOpcode.FlagRead or MachineOpcode.FlagWrite or MachineOpcode.FlagUndef;
      if (isFlag ? profile.Flag(storage) is null : profile.Register(storage) is null) {
        this.Problems.Add(Diagnostic.Create(MachineDiagnostics.UnknownStorage, null, storage, profile.Id));
        return;
      }

      if (isFlag)
        return;
      var register = profile.Register(storage)!;
      if (register.Bits != instruction.Type.Bits)
        this.Problems.Add(Diagnostic.Create(MachineDiagnostics.WidthMismatch, null, MachineSyntax.Print(instruction), instruction.Type, register.Name, register.Bits));
      if (!register.IsRoot && function.Attributes.Contains(MachineFunction.RootRegisters))
        this.Problems.Add(Diagnostic.Create(MachineDiagnostics.AfterNormalization, null, MachineSyntax.Print(instruction), function.Name));
    }

    /// <summary>
    /// <c>PLY4002</c>: a forward analysis of what each flag may be on entry to each block — set by the
    /// caller, written, or undefined — and a read whose flag can only be undefined.
    /// </summary>
    private void UndefinedFlags(IsaProfile profile) {
      const int FromCaller = 1, Written = 2, Undefined = 4;
      var flags = profile.Flags.Select(flag => flag.Name).ToList();
      var entry = new Dictionary<string, (int State, string? Where)[]>(StringComparer.Ordinal);
      foreach (var block in function.Blocks)
        entry[block.Label] = new (int, string?)[flags.Count];
      var start = entry[function.Blocks[0].Label];
      for (var flag = 0; flag < flags.Count; ++flag)
        start[flag] = (FromCaller, null);

      (int, string?)[] Transfer(MachineBlock block, (int State, string? Where)[] state, Action<int, string, string>? onRead) {
        var current = ((int State, string? Where)[])state.Clone();
        for (var index = 0; index < block.Instructions.Length; ++index) {
          var instruction = block.Instructions[index];
          var flag = instruction.Storage is { } name ? flags.IndexOf(name) : -1;
          if (flag < 0)
            continue;
          switch (instruction.Opcode) {
            case MachineOpcode.FlagWrite:
              current[flag] = (Written, null);
              break;
            case MachineOpcode.FlagUndef:
              current[flag] = (Undefined, Location(function, block, index));
              break;
            case MachineOpcode.FlagRead when current[flag].State == Undefined:
              onRead?.Invoke(flag, Location(function, block, index), current[flag].Where!);
              break;
          }
        }

        return current;
      }

      for (var changed = true; changed;) {
        changed = false;
        foreach (var block in function.Blocks) {
          var exit = Transfer(block, entry[block.Label], null);
          foreach (var successor in block.Terminator?.Successors ?? []) {
            var target = entry[successor];
            for (var flag = 0; flag < flags.Count; ++flag) {
              var merged = target[flag].State | exit[flag].Item1;
              if (merged == target[flag].State)
                continue;
              target[flag] = (merged, target[flag].Where ?? exit[flag].Item2);
              changed = true;
            }
          }
        }
      }

      foreach (var block in function.Blocks)
        Transfer(block, entry[block.Label], (flag, at, undefinedAt)
          => this.Problems.Add(Diagnostic.Create(MachineDiagnostics.UndefinedFlagRead, null, flags[flag], at, undefinedAt)));
    }

  }

}
