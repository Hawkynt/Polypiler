using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Polypiler.Core.MachineSemantics.Ports;

namespace Polypiler.Core.MachineSemantics;

/// <summary>
/// A machine profile: the registers, sub-registers and flags of one instruction set, read from
/// <c>spec/machine/&lt;isa&gt;.yaml</c>. It is the one machine-state model of the ports
/// (<see cref="IMachineStateModel"/>): a CPU declares its registers by shipping a profile, never by
/// inventing IR.
/// </summary>
/// <param name="Id">The profile's ID, for example <c>test-m32</c>.</param>
/// <param name="AddressBits">The width of an address, 1 to 64.</param>
/// <param name="ByteOrder">How a multi-byte value is laid out in memory.</param>
/// <param name="Registers">The registers, roots and sub-registers, in declaration order.</param>
/// <param name="Flags">The flags, in declaration order.</param>
public sealed record IsaProfile(string Id, int AddressBits, ByteOrder ByteOrder, ImmutableArray<RegisterDescriptor> Registers, ImmutableArray<FlagDescriptor> Flags)
  : IMachineStateModel {

  /// <summary>How many interrupt vectors the machine has, or <see langword="null"/> when the profile does not say.</summary>
  public int? InterruptVectors { get; init; }

  /// <inheritdoc/>
  string IMachineStateModel.Isa => this.Id;

  /// <inheritdoc/>
  public bool Equals(IsaProfile? other)
    => other is not null && this.Id == other.Id && this.AddressBits == other.AddressBits && this.ByteOrder == other.ByteOrder
      && this.Registers.SequenceEqual(other.Registers) && this.Flags.SequenceEqual(other.Flags) && this.InterruptVectors == other.InterruptVectors;

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Id, this.AddressBits, this.ByteOrder, this.Registers.Length, this.Flags.Length);

  /// <summary>Reads and checks a profile file.</summary>
  /// <param name="path">The file.</param>
  /// <returns>The profile.</returns>
  /// <exception cref="MachineException">The file cannot be read or breaks a rule (<c>PLY4003</c>).</exception>
  public static IsaProfile Load(string path) => ProfileLoader.Load(path);

  /// <summary>A register by name, or <see langword="null"/>.</summary>
  /// <param name="name">The name.</param>
  /// <returns>The register.</returns>
  public RegisterDescriptor? Register(string name) => this.Registers.FirstOrDefault(register => register.Name == name);

  /// <summary>A flag by name, or <see langword="null"/>.</summary>
  /// <param name="name">The name.</param>
  /// <returns>The flag.</returns>
  public FlagDescriptor? Flag(string name) => this.Flags.FirstOrDefault(flag => flag.Name == name);

  /// <summary>The root register a register is part of; the register itself for a root.</summary>
  /// <param name="register">A register of this profile.</param>
  /// <returns>The root.</returns>
  public RegisterDescriptor Root(RegisterDescriptor register) {
    ArgumentNullException.ThrowIfNull(register);
    while (register.Parent is { } parent)
      register = this.Register(parent)!;
    return register;
  }

  /// <summary>The lowest bit a register occupies in its root.</summary>
  /// <param name="register">A register of this profile.</param>
  /// <returns>The sum of the offsets up to the root.</returns>
  public int OffsetInRoot(RegisterDescriptor register) {
    ArgumentNullException.ThrowIfNull(register);
    var offset = 0;
    while (register.Parent is { } parent) {
      offset += register.BitOffset;
      register = this.Register(parent)!;
    }

    return offset;
  }

  /// <summary>
  /// Whether a write to a register replaces its whole root: a root, a sub-register as wide as its root,
  /// and a <see cref="SubRegisterWrite.ZeroExtend"/> sub-register whose ancestors up to the root all
  /// zero-extend too.
  /// </summary>
  /// <param name="register">A register of this profile.</param>
  /// <returns>Whether the write replaces the root.</returns>
  public bool ReplacesRoot(RegisterDescriptor register) {
    ArgumentNullException.ThrowIfNull(register);
    if (register.IsRoot || register.Bits == this.Root(register).Bits)
      return true;

    for (var current = register; current.Parent is { } parent; current = this.Register(parent)!)
      if (current.Write != SubRegisterWrite.ZeroExtend)
        return false;
    return true;
  }

  /// <summary>
  /// The rules of a profile, each broken one as <c>PLY4003</c>: names unique among registers and flags,
  /// widths 1 to 64, a parent that exists and is wider or as wide, a sub-register inside its parent, no
  /// cycle of parents, a flag's register declared and wide enough for its bit, an address width of 1 to 64
  /// and a positive number of interrupt vectors.
  /// </summary>
  /// <param name="file">The file, for the diagnostics, or <see langword="null"/>.</param>
  /// <returns>What is wrong; empty when nothing is.</returns>
  public ImmutableArray<Diagnostic> Validate(string? file = null) {
    var problems = ImmutableArray.CreateBuilder<Diagnostic>();
    void Problem(string reason) => problems.Add(MachineDiagnostics.InvalidProfile(this.Id, reason, file));

    if (this.AddressBits is < 1 or > 64)
      Problem($"addressBits must be 1 to 64, not {this.AddressBits}");
    if (this.InterruptVectors is < 1)
      Problem($"interruptVectors must be at least 1, not {this.InterruptVectors}");

    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var name in this.Registers.Select(register => register.Name).Concat(this.Flags.Select(flag => flag.Name)))
      if (!names.Add(name))
        Problem($"duplicate name '{name}'");

    foreach (var register in this.Registers) {
      if (register.Bits is < 1 or > 64) {
        Problem($"register '{register.Name}' has {register.Bits} bits; a register has 1 to 64");
        continue;
      }

      if (register.Parent is null)
        continue;
      if (this.Register(register.Parent) is not { } parent) {
        Problem($"register '{register.Name}' names the unknown parent '{register.Parent}'");
        continue;
      }

      if (register.BitOffset < 0 || register.BitOffset + register.Bits > parent.Bits)
        Problem($"sub-register '{register.Name}' (bits {register.BitOffset}..{register.BitOffset + register.Bits - 1}) exceeds its parent '{parent.Name}' of {parent.Bits} bits");
    }

    foreach (var register in this.Registers.Where(register => register.Parent is not null)) {
      var seen = new HashSet<string>(StringComparer.Ordinal) { register.Name };
      for (var current = register; current.Parent is { } parentName && this.Register(parentName) is { } parent; current = parent)
        if (!seen.Add(parent.Name)) {
          Problem($"register '{register.Name}' is part of a cycle of parents");
          break;
        }
    }

    foreach (var flag in this.Flags) {
      if (flag.Register is null)
        continue;
      if (this.Register(flag.Register) is not { } holder)
        Problem($"flag '{flag.Name}' names the unknown register '{flag.Register}'");
      else if (flag.Bit < 0 || flag.Bit >= holder.Bits)
        Problem($"flag '{flag.Name}' is bit {flag.Bit} of '{holder.Name}', which has {holder.Bits} bits");
    }

    return problems.ToImmutable();
  }

}
