using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Polypiler.Core.MachineSemantics.Ports;

/// <summary>Bytes of machine code by address: a binary image, an emulator's memory, a test buffer.</summary>
public interface IMachineCodeSource {

  /// <summary>The byte at an address, or <see langword="null"/> when there is none.</summary>
  /// <param name="address">The address.</param>
  /// <returns>The byte.</returns>
  byte? ByteAt(ulong address);

}

/// <summary>How control leaves an instruction.</summary>
public enum FlowKind {

  /// <summary>To the next instruction.</summary>
  Sequential,

  /// <summary>To a known target only.</summary>
  Jump,

  /// <summary>To a known target or the next instruction.</summary>
  ConditionalJump,

  /// <summary>To a known routine, which returns to the next instruction.</summary>
  Call,

  /// <summary>Back to the caller.</summary>
  Return,

  /// <summary>To a computed target.</summary>
  IndirectJump,

  /// <summary>To a computed routine.</summary>
  IndirectCall,

  /// <summary>Into the CPU's exception mechanism.</summary>
  Trap,

  /// <summary>Nowhere: the bytes are no instruction.</summary>
  Invalid

}

/// <summary>One decoded instruction, independent of its CPU; <see cref="Payload"/> is the CPU's own record of it.</summary>
/// <param name="Address">The address of its first byte.</param>
/// <param name="Length">Its length in bytes.</param>
/// <param name="Flow">How control leaves it.</param>
/// <param name="Targets">The known targets of a jump or call.</param>
/// <param name="Returns">Whether control may come back to the next instruction.</param>
/// <param name="Text">The disassembly.</param>
/// <param name="Payload">The CPU's own instruction record.</param>
public sealed record DisassembledInstruction(ulong Address, int Length, FlowKind Flow, ImmutableArray<ulong> Targets, bool Returns, string Text, object Payload) {

  /// <inheritdoc/>
  public bool Equals(DisassembledInstruction? other)
    => other is not null && this.Address == other.Address && this.Length == other.Length && this.Flow == other.Flow
      && this.Targets.SequenceEqual(other.Targets) && this.Returns == other.Returns && this.Text == other.Text && Equals(this.Payload, other.Payload);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Address, this.Length, this.Flow, this.Text);

}

/// <summary>Decodes the instructions of one ISA.</summary>
public interface IDisassembler {

  /// <summary>The ISA, as its machine profile names it.</summary>
  string Isa { get; }

  /// <summary>Decodes the instruction at an address.</summary>
  /// <param name="source">The bytes.</param>
  /// <param name="address">The address.</param>
  /// <param name="mode">The decoding mode.</param>
  /// <param name="sink">Receives the diagnostic when the bytes are no instruction.</param>
  /// <returns>The instruction, or <see langword="null"/> after a diagnostic.</returns>
  DisassembledInstruction? Disassemble(IMachineCodeSource source, ulong address, string mode, IDiagnosticSink sink);

}

/// <summary>The MachineIR of one instruction, and how control leaves it.</summary>
/// <param name="Instructions">The instructions, without a terminator unless the instruction transfers control.</param>
/// <param name="Flow">How control leaves it.</param>
/// <param name="Targets">The known targets.</param>
public sealed record MachineFragment(ImmutableArray<MachineInstruction> Instructions, FlowKind Flow, ImmutableArray<ulong> Targets) {

  /// <inheritdoc/>
  public bool Equals(MachineFragment? other)
    => other is not null && this.Instructions.SequenceEqual(other.Instructions) && this.Flow == other.Flow && this.Targets.SequenceEqual(other.Targets);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Instructions.Length, this.Flow);

}

/// <summary>Expands a decoded instruction of one ISA into MachineIR.</summary>
public interface IInstructionSemantics {

  /// <summary>The ISA.</summary>
  string Isa { get; }

  /// <summary>Expands an instruction.</summary>
  /// <param name="instruction">The decoded instruction.</param>
  /// <param name="state">The registers and flags its MachineIR may name.</param>
  /// <param name="sink">Receives what cannot be expanded.</param>
  /// <returns>The fragment.</returns>
  MachineFragment Expand(DisassembledInstruction instruction, IMachineStateModel state, IDiagnosticSink sink);

}

/// <summary>The registers and flags of a machine; <see cref="IsaProfile"/> is the one implementation.</summary>
public interface IMachineStateModel {

  /// <summary>The ISA.</summary>
  string Isa { get; }

  /// <summary>The width of an address.</summary>
  int AddressBits { get; }

  /// <summary>The byte order of memory.</summary>
  ByteOrder ByteOrder { get; }

  /// <summary>A register by name, or <see langword="null"/>.</summary>
  /// <param name="name">The name.</param>
  /// <returns>The register.</returns>
  RegisterDescriptor? Register(string name);

  /// <summary>A flag by name, or <see langword="null"/>.</summary>
  /// <param name="name">The name.</param>
  /// <returns>The flag.</returns>
  FlagDescriptor? Flag(string name);

}

/// <summary>One line of assembly text: any part may be absent.</summary>
/// <param name="Label">The label it defines, or <see langword="null"/>.</param>
/// <param name="Mnemonic">The instruction or directive, or <see langword="null"/>.</param>
/// <param name="Operands">The operands as text.</param>
/// <param name="Comment">The comment, or <see langword="null"/>.</param>
public sealed record AssemblyLine(string? Label, string? Mnemonic, ImmutableArray<string> Operands, string? Comment) {

  /// <inheritdoc/>
  public bool Equals(AssemblyLine? other)
    => other is not null && this.Label == other.Label && this.Mnemonic == other.Mnemonic && this.Operands.SequenceEqual(other.Operands) && this.Comment == other.Comment;

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Label, this.Mnemonic, this.Operands.Length, this.Comment);

}

/// <summary>Writes assembly text in one dialect of one ISA.</summary>
public interface IAssemblerEmitter {

  /// <summary>The ISA.</summary>
  string Isa { get; }

  /// <summary>The assembler dialect, for example <c>nasm</c> or <c>gas</c>.</summary>
  string Dialect { get; }

  /// <summary>Writes a unit.</summary>
  /// <param name="unitName">The unit's name, which names the file.</param>
  /// <param name="lines">The lines.</param>
  /// <returns>The file.</returns>
  GeneratedFile Write(string unitName, IReadOnlyList<AssemblyLine> lines);

}

/// <summary>Machine code laid out for a load address, ready for a binary writer.</summary>
/// <param name="Bytes">The bytes.</param>
/// <param name="LoadAddress">The address of the first byte.</param>
/// <param name="Entry">The entry point, or <see langword="null"/>.</param>
/// <param name="Symbols">Named addresses.</param>
public sealed record MachineCodeImage(ReadOnlyMemory<byte> Bytes, ulong LoadAddress, ulong? Entry, ImmutableDictionary<string, ulong> Symbols);

/// <summary>A file of bytes a binary writer produces.</summary>
/// <param name="Path">The path relative to the output directory.</param>
/// <param name="Bytes">The content.</param>
public sealed record BinaryArtifact(string Path, ImmutableArray<byte> Bytes) {

  /// <inheritdoc/>
  public bool Equals(BinaryArtifact? other) => other is not null && this.Path == other.Path && this.Bytes.SequenceEqual(other.Bytes);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Path, this.Bytes.Length);

}

/// <summary>Writes machine code into one container format.</summary>
public interface IBinaryWriter {

  /// <summary>The container format, for example <c>elf</c>, <c>pe</c> or <c>prg</c>.</summary>
  string Format { get; }

  /// <summary>Writes an image.</summary>
  /// <param name="image">The machine code.</param>
  /// <param name="profile">The ID of the target profile the files are for.</param>
  /// <param name="sink">Receives what cannot be written.</param>
  /// <returns>The files.</returns>
  ImmutableArray<BinaryArtifact> Write(MachineCodeImage image, string profile, IDiagnosticSink sink);

}
