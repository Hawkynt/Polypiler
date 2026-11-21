using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Polypiler.Core.MachineSemantics.Regions;
using Polypiler.Core.MachineSemantics.Traps;

namespace Polypiler.Core.MachineSemantics.Interpreter;

/// <summary>Bytes by address, only those ever written; a byte never written has no value.</summary>
public sealed class SparseMemory {

  private readonly SortedDictionary<ulong, byte> _bytes = [];

  /// <summary>The byte at an address, or <see langword="null"/> when none was written.</summary>
  /// <param name="address">The address.</param>
  /// <returns>The byte.</returns>
  public byte? Read(ulong address) => this._bytes.TryGetValue(address, out var value) ? value : null;

  /// <summary>Writes a byte.</summary>
  /// <param name="address">The address.</param>
  /// <param name="value">The byte.</param>
  public void Write(ulong address, byte value) => this._bytes[address] = value;

  /// <summary>Writes bytes from an address upwards.</summary>
  /// <param name="address">The first address.</param>
  /// <param name="values">The bytes.</param>
  public void Write(ulong address, IEnumerable<byte> values) {
    ArgumentNullException.ThrowIfNull(values);
    foreach (var value in values)
      this.Write(address++, value);
  }

  /// <summary>Every byte written, by address.</summary>
  public IReadOnlyDictionary<ulong, byte> Bytes => this._bytes;

}

/// <summary>What a resuming trap does: a model of the handler or service behind it.</summary>
public interface ITrapModel {

  /// <summary>Runs the handler of a trap on the machine state.</summary>
  /// <param name="trap">The trap.</param>
  /// <param name="state">The state at the trap, changed by the handler.</param>
  /// <returns>Whether the model knows the trap; when it does not, the interpretation aborts.</returns>
  bool TryHandle(TrapDescriptor trap, MachineState state);

}

/// <summary>
/// The state a MachineIR routine runs on: root registers, flags, sparse memory and the optional address
/// map and trap model. A register never set is uninitialized and a flag never set or set undefined has
/// no value: reading either aborts, nothing undefined is given a value. A flag the profile keeps in a
/// status register is that register's bit: writing the register sets the flag, reading it composes
/// the flags into the stored value.
/// </summary>
public sealed class MachineState {

  private readonly Dictionary<string, ulong> _registers = new(StringComparer.Ordinal);
  private readonly Dictionary<string, bool?> _flags = new(StringComparer.Ordinal);

  /// <summary>Creates an empty state.</summary>
  /// <param name="profile">The machine profile.</param>
  /// <param name="map">The address map, or <see langword="null"/> for memory without regions.</param>
  public MachineState(IsaProfile profile, IAddressMap? map = null) {
    this.Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    this.Map = map;
  }

  /// <summary>The machine profile.</summary>
  public IsaProfile Profile { get; }

  /// <summary>The address map, or <see langword="null"/>.</summary>
  public IAddressMap? Map { get; }

  /// <summary>The memory.</summary>
  public SparseMemory Memory { get; } = new();

  /// <summary>The handler of resuming traps, or <see langword="null"/>.</summary>
  public ITrapModel? Traps { get; set; }

  /// <summary>The root registers that hold a value.</summary>
  public IReadOnlyDictionary<string, ulong> Registers => this._registers;

  /// <summary>The flags that were set: <see langword="null"/> for one set undefined.</summary>
  public IReadOnlyDictionary<string, bool?> Flags => this._flags;

  /// <summary>Sets a register, a sub-register under its write policy, or a status register with its flags.</summary>
  /// <param name="name">The register.</param>
  /// <param name="value">The value; bits beyond its width are ignored.</param>
  /// <returns>This state.</returns>
  /// <exception cref="ArgumentException">The profile has no such register, or a preserving sub-register's root holds no value.</exception>
  public MachineState SetRegister(string name, ulong value) {
    var register = this.Profile.Register(name) ?? throw new ArgumentException($"'{name}' is not a register of '{this.Profile.Id}'", nameof(name));
    value &= MachineType.Of(register.Bits).Mask;
    var root = this.Profile.Root(register);
    if (this.Profile.ReplacesRoot(register)) {
      this._registers[root.Name] = value;
    } else {
      if (!this._registers.TryGetValue(root.Name, out var old))
        throw new ArgumentException($"register '{root.Name}' holds no value, so '{name}' cannot be merged into it", nameof(name));
      var offset = this.Profile.OffsetInRoot(register);
      var field = MachineType.Of(register.Bits).Mask << offset;
      this._registers[root.Name] = (old & ~field) | (value << offset);
    }

    foreach (var flag in this.Profile.Flags.Where(flag => flag.Register == root.Name))
      this._flags[flag.Name] = ((this._registers[root.Name] >> flag.Bit) & 1) != 0;
    return this;
  }

  /// <summary>Sets a flag, or makes it undefined with <see langword="null"/>.</summary>
  /// <param name="name">The flag.</param>
  /// <param name="value">The value.</param>
  /// <returns>This state.</returns>
  /// <exception cref="ArgumentException">The profile has no such flag.</exception>
  public MachineState SetFlag(string name, bool? value) {
    var flag = this.Profile.Flag(name) ?? throw new ArgumentException($"'{name}' is not a flag of '{this.Profile.Id}'", nameof(name));
    this._flags[name] = value;
    if (value is { } bit && flag.Register is { } holder && this._registers.TryGetValue(holder, out var old))
      this._registers[holder] = bit ? old | (1UL << flag.Bit) : old & ~(1UL << flag.Bit);
    return this;
  }

  /// <summary>The value of a root register, its flags composed in; <see langword="null"/> when it or one of its flags has no value.</summary>
  /// <param name="root">The root register.</param>
  /// <returns>The value.</returns>
  public ulong? Register(string root) {
    if (!this._registers.TryGetValue(root, out var value))
      return null;
    foreach (var flag in this.Profile.Flags.Where(flag => flag.Register == root))
      if (this.Flag(flag.Name) is not { } bit)
        return null;
      else
        value = bit ? value | (1UL << flag.Bit) : value & ~(1UL << flag.Bit);
    return value;
  }

  /// <summary>The value of a flag, or <see langword="null"/> when it has none.</summary>
  /// <param name="name">The flag.</param>
  /// <returns>The value.</returns>
  public bool? Flag(string name) => this._flags.GetValueOrDefault(name);

  /// <summary>Whether a flag was ever set, defined or not.</summary>
  /// <param name="name">The flag.</param>
  /// <returns>Whether it was.</returns>
  public bool IsFlagSet(string name) => this._flags.ContainsKey(name);

}
