using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Polypiler.Core.MachineSemantics;

/// <summary>
/// Rewrites every sub-register access of a function into an access of its root register, so that later
/// stages — the raise to the tree IR, the interpreter's differential checks — see one storage location
/// per root and never a sub-register as a variable of its own. A read of a sub-register becomes
/// <c>RegRead</c> of the root, <c>ExtractBits</c> of the field and <c>Truncate</c>; a write under
/// <see cref="SubRegisterWrite.Preserve"/> becomes <c>RegRead</c> of the root, <c>ZeroExtend</c>,
/// <c>InsertBits</c> and <c>RegWrite</c>; a write that replaces the root (a
/// <see cref="SubRegisterWrite.ZeroExtend"/> chain, or a sub-register as wide as its root) becomes
/// <c>ZeroExtend</c> and <c>RegWrite</c>. Every instruction produced keeps the origin of the one it
/// replaces. The result carries the attribute <c>root-registers</c>.
/// </summary>
public static class MachineNormalizer {

  /// <summary>Rewrites the sub-register accesses of a verified function.</summary>
  /// <param name="function">The function, verified against <paramref name="profile"/>.</param>
  /// <param name="profile">The machine profile.</param>
  /// <returns>The function with root-register accesses only.</returns>
  public static MachineFunction ToRootRegisters(MachineFunction function, IsaProfile profile) {
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(profile);
    var names = new Names(function);
    var blocks = function.Blocks.Select(block => new MachineBlock(block.Label, [.. block.Instructions.SelectMany(instruction => Rewrite(instruction, profile, names))])).ToImmutableArray();
    var attributes = function.Attributes.Contains(MachineFunction.RootRegisters) ? function.Attributes : function.Attributes.Add(MachineFunction.RootRegisters);
    return function with { Blocks = blocks, Attributes = attributes };
  }

  /// <summary>Rewrites the sub-register accesses of every function of a module.</summary>
  /// <param name="module">The module, verified, with a profile.</param>
  /// <returns>The module.</returns>
  /// <exception cref="ArgumentException">The module has no profile.</exception>
  public static MachineModule ToRootRegisters(MachineModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var profile = module.Profile ?? throw new ArgumentException("the module declares no machine profile", nameof(module));
    return module with { Functions = [.. module.Functions.Select(function => ToRootRegisters(function, profile))] };
  }

  private static IEnumerable<MachineInstruction> Rewrite(MachineInstruction instruction, IsaProfile profile, Names names) {
    if (instruction.Opcode is not (MachineOpcode.RegRead or MachineOpcode.RegWrite) || profile.Register(instruction.Storage!) is not { IsRoot: false } register)
      return [instruction];

    var output = new List<MachineInstruction>();
    void Emit(MachineOpcode opcode, MachineType type, string? result, params MachineOperand[] operands)
      => output.Add(MachineInstruction.Create(opcode, type, result, operands) with { Origin = instruction.Origin });

    if (instruction.Opcode == MachineOpcode.RegRead)
      Read(register, instruction.Result!, profile, names, Emit);
    else
      Write(register, instruction.Operands[1], profile, names, Emit);
    return output;
  }

  private delegate void Emitter(MachineOpcode opcode, MachineType type, string? result, params MachineOperand[] operands);

  /// <summary>Reads a register into a value of the given name.</summary>
  private static void Read(RegisterDescriptor register, string result, IsaProfile profile, Names names, Emitter emit) {
    var root = profile.Root(register);
    var rootType = MachineType.Of(root.Bits);
    if (register.IsRoot || register.Bits == root.Bits) {
      emit(MachineOpcode.RegRead, rootType, result, new MachineStorage(root.Name));
      return;
    }

    var whole = names.Fresh(root.Name);
    emit(MachineOpcode.RegRead, rootType, whole, new MachineStorage(root.Name));
    var field = names.Fresh(result + ".field");
    emit(MachineOpcode.ExtractBits, rootType, field, new MachineValue(whole), new MachineImmediate((ulong)profile.OffsetInRoot(register)), new MachineImmediate((ulong)register.Bits));
    emit(MachineOpcode.Truncate, MachineType.Of(register.Bits), result, new MachineValue(field));
  }

  /// <summary>Writes a value to a register, under the write policy of each link up to the root.</summary>
  private static void Write(RegisterDescriptor register, MachineOperand value, IsaProfile profile, Names names, Emitter emit) {
    var root = profile.Root(register);
    var rootType = MachineType.Of(root.Bits);
    if (register.IsRoot) {
      emit(MachineOpcode.RegWrite, rootType, null, new MachineStorage(root.Name), value);
      return;
    }

    if (profile.ReplacesRoot(register)) {
      emit(MachineOpcode.RegWrite, rootType, null, new MachineStorage(root.Name), Widen(value, register.Bits, rootType, names, emit));
      return;
    }

    var parent = profile.Register(register.Parent!)!;
    var parentType = MachineType.Of(parent.Bits);
    var preservesUpToRoot = true;
    for (var current = register; current.Parent is { } next; current = profile.Register(next)!)
      preservesUpToRoot &= current.Write == SubRegisterWrite.Preserve;

    if (register.Write == SubRegisterWrite.ZeroExtend) {
      Write(parent, Widen(value, register.Bits, parentType, names, emit), profile, names, emit);
      return;
    }

    var (into, intoType, offset) = preservesUpToRoot ? (root, rootType, profile.OffsetInRoot(register)) : (parent, parentType, register.BitOffset);
    var old = names.Fresh(into.Name);
    Read(into, old, profile, names, emit);
    var inserted = names.Fresh(into.Name);
    emit(MachineOpcode.InsertBits, intoType, inserted, new MachineValue(old), Widen(value, register.Bits, intoType, names, emit), new MachineImmediate((ulong)offset), new MachineImmediate((ulong)register.Bits));
    Write(into, new MachineValue(inserted), profile, names, emit);
  }

  /// <summary>Zero-extends a value to a wider type; an immediate stays an immediate.</summary>
  private static MachineOperand Widen(MachineOperand value, int bits, MachineType to, Names names, Emitter emit) {
    if (bits == to.Bits || value is MachineImmediate)
      return value;
    var widened = names.Fresh(((MachineValue)value).Name + ".z");
    emit(MachineOpcode.ZeroExtend, to, widened, value);
    return new MachineValue(widened);
  }

  /// <summary>Fresh value names that collide with none of the function's.</summary>
  private sealed class Names(MachineFunction function) {

    private readonly HashSet<string> _used = function.Blocks.SelectMany(block => block.Instructions).Select(instruction => instruction.Result).OfType<string>().ToHashSet(StringComparer.Ordinal);

    public string Fresh(string name) {
      var candidate = name;
      for (var counter = 1; !this._used.Add(candidate); ++counter)
        candidate = $"{name}.{counter}";
      return candidate;
    }

  }

}
