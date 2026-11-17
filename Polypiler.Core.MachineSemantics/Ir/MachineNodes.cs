using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Polypiler.Core.MachineSemantics.Traps;

namespace Polypiler.Core.MachineSemantics;

/// <summary>An operand of a MachineIR instruction.</summary>
public abstract record MachineOperand;

/// <summary>A block-local value, <c>%name</c>.</summary>
/// <param name="Name">The name, without <c>%</c>.</param>
public sealed record MachineValue(string Name) : MachineOperand {

  /// <inheritdoc/>
  public override string ToString() => "%" + this.Name;

}

/// <summary>An integer written in place, typed by the position it stands in.</summary>
/// <param name="Value">The bits.</param>
public sealed record MachineImmediate(ulong Value) : MachineOperand {

  /// <inheritdoc/>
  public override string ToString() => MachineSyntax.Number(this.Value);

}

/// <summary>A register or flag of the profile, <c>@name</c>.</summary>
/// <param name="Name">The name, without <c>@</c>.</param>
public sealed record MachineStorage(string Name) : MachineOperand {

  /// <inheritdoc/>
  public override string ToString() => "@" + this.Name;

}

/// <summary>A block of the same function, the target of a jump.</summary>
/// <param name="Name">The block's label.</param>
public sealed record MachineLabel(string Name) : MachineOperand {

  /// <inheritdoc/>
  public override string ToString() => this.Name;

}

/// <summary>
/// One MachineIR instruction. Values are block-local and assigned once: a value is defined before its
/// first use in the same block, and no name is defined twice in a function. Registers, flags and memory
/// are storage, read and written in program order; they are what carries state from block to block.
/// </summary>
/// <param name="Opcode">The operation.</param>
/// <param name="Type">The type it computes or accesses; <see cref="MachineType.Void"/> where it has none.</param>
/// <param name="Result">The name of the value it defines, or <see langword="null"/>.</param>
/// <param name="Operands">The operands, in the order of the text form.</param>
public sealed record MachineInstruction(MachineOpcode Opcode, MachineType Type, string? Result, ImmutableArray<MachineOperand> Operands) {

  /// <summary>Where the instruction came from in machine code, or <see langword="null"/>; every rewrite keeps it.</summary>
  public MachineOrigin? Origin { get; init; }

  /// <summary>For <see cref="MachineOpcode.IndirectJump"/>: the targets are not known, the listed ones are none.</summary>
  public bool Unresolved { get; init; }

  /// <summary>For <see cref="MachineOpcode.MachineTrap"/>: the trap's kind, vector, resumption and service contract.</summary>
  public TrapDescriptor? Trap { get; init; }

  /// <summary>Whether the instruction ends its block: a terminator opcode, or a trap that does not resume.</summary>
  public bool IsTerminator => MachineOpcodes.IsTerminator(this.Opcode) || this.Trap is { Resumes: false };

  /// <summary>The blocks the instruction may continue at, when it is a terminator.</summary>
  public IEnumerable<string> Successors => this.IsTerminator ? this.Operands.OfType<MachineLabel>().Select(label => label.Name) : [];

  /// <summary>The register or flag the instruction names, or <see langword="null"/>.</summary>
  public string? Storage => this.Operands.OfType<MachineStorage>().FirstOrDefault()?.Name;

  /// <summary>Creates an instruction.</summary>
  /// <param name="opcode">The operation.</param>
  /// <param name="type">The type.</param>
  /// <param name="result">The defined value, or <see langword="null"/>.</param>
  /// <param name="operands">The operands.</param>
  /// <returns>The instruction.</returns>
  public static MachineInstruction Create(MachineOpcode opcode, MachineType type, string? result, params MachineOperand[] operands)
    => new(opcode, type, result, [.. operands]);

  /// <inheritdoc/>
  public bool Equals(MachineInstruction? other)
    => other is not null && this.Opcode == other.Opcode && this.Type == other.Type && this.Result == other.Result
      && this.Operands.SequenceEqual(other.Operands) && Equals(this.Origin, other.Origin) && this.Unresolved == other.Unresolved
      && Equals(this.Trap, other.Trap);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Opcode, this.Type, this.Result, this.Operands.Length);

  /// <inheritdoc/>
  public override string ToString() => MachineSyntax.Print(this);

}

/// <summary>A basic block: a label and instructions, the last one a terminator.</summary>
/// <param name="Label">The label, unique in its function.</param>
/// <param name="Instructions">The instructions.</param>
public sealed record MachineBlock(string Label, ImmutableArray<MachineInstruction> Instructions) {

  /// <summary>The last instruction when it is a terminator, else <see langword="null"/>.</summary>
  public MachineInstruction? Terminator => this.Instructions.Length > 0 && this.Instructions[^1].IsTerminator ? this.Instructions[^1] : null;

  /// <inheritdoc/>
  public bool Equals(MachineBlock? other) => other is not null && this.Label == other.Label && this.Instructions.SequenceEqual(other.Instructions);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Label, this.Instructions.Length);

}

/// <summary>A routine: blocks, the first one its entry, and attributes such as <c>root-registers</c>.</summary>
/// <param name="Name">The name, without <c>@</c>.</param>
/// <param name="Blocks">The blocks; the first is the entry.</param>
public sealed record MachineFunction(string Name, ImmutableArray<MachineBlock> Blocks) {

  /// <summary>The attribute <see cref="MachineNormalizer.ToRootRegisters"/> sets: no sub-register is accessed any more.</summary>
  public const string RootRegisters = "root-registers";

  /// <summary>The attributes, in the order written.</summary>
  public ImmutableArray<string> Attributes { get; init; } = [];

  /// <summary>A block by label, or <see langword="null"/>.</summary>
  /// <param name="label">The label.</param>
  /// <returns>The block.</returns>
  public MachineBlock? Block(string label) => this.Blocks.FirstOrDefault(block => block.Label == label);

  /// <inheritdoc/>
  public bool Equals(MachineFunction? other)
    => other is not null && this.Name == other.Name && this.Blocks.SequenceEqual(other.Blocks) && this.Attributes.SequenceEqual(other.Attributes);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Name, this.Blocks.Length);

}

/// <summary>
/// A MachineIR module: the machine profile its operations refer to (<c>machine &lt;id&gt;</c>), the mode
/// and ISA its origins name, and its functions. A module without a profile may hold pure code and
/// origins, never a machine operation.
/// </summary>
/// <param name="Profile">The machine profile, or <see langword="null"/>.</param>
/// <param name="Functions">The functions.</param>
public sealed record MachineModule(IsaProfile? Profile, ImmutableArray<MachineFunction> Functions) {

  /// <summary>The default of <see cref="Mode"/>.</summary>
  public const string DefaultMode = "default";

  /// <summary>The name diagnostics give the module.</summary>
  public string Name { get; init; } = "module";

  /// <summary>The ISA the origins of the module name: the profile's ID, or the <c>isa</c> line of a module without one.</summary>
  public string? Isa { get; init; }

  /// <summary>The mode the origins of the module name: <c>mode</c> line, else <see cref="DefaultMode"/>.</summary>
  public string Mode { get; init; } = DefaultMode;

  /// <summary>The ISA of origins: <see cref="Isa"/>, else the profile's ID, else <c>unknown</c>.</summary>
  public string OriginIsa => this.Isa ?? this.Profile?.Id ?? "unknown";

  /// <summary>A function by name, or <see langword="null"/>.</summary>
  /// <param name="name">The name, without <c>@</c>.</param>
  /// <returns>The function.</returns>
  public MachineFunction? Function(string name) => this.Functions.FirstOrDefault(function => function.Name == name);

  /// <inheritdoc/>
  public bool Equals(MachineModule? other)
    => other is not null && Equals(this.Profile, other.Profile) && this.Functions.SequenceEqual(other.Functions)
      && this.Name == other.Name && this.Isa == other.Isa && this.Mode == other.Mode;

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Name, this.Functions.Length);

}
