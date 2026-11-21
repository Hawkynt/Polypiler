using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Polypiler.Core.MachineSemantics;

/// <summary>The effect channels a MachineIR instruction reads and writes.</summary>
/// <param name="Reads">The channels read.</param>
/// <param name="Writes">The channels written.</param>
public sealed record MachineEffect(ImmutableArray<string> Reads, ImmutableArray<string> Writes) {

  /// <summary>No effect: a pure instruction.</summary>
  public static MachineEffect None { get; } = new([], []);

  /// <summary>Whether the instruction has no effect at all.</summary>
  public bool IsPure => this.Reads.IsEmpty && this.Writes.IsEmpty;

  /// <inheritdoc/>
  public bool Equals(MachineEffect? other) => other is not null && this.Reads.SequenceEqual(other.Reads) && this.Writes.SequenceEqual(other.Writes);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Reads.Length, this.Writes.Length);

}

/// <summary>
/// The effects of MachineIR instructions as channels, and whether two instructions may be reordered.
/// Channels: <c>Machine.Registers:&lt;isa&gt;/&lt;root&gt;</c> per root register,
/// <c>Machine.Flags:&lt;isa&gt;/&lt;flag&gt;</c> per flag, <c>Memory.Unknown</c> for an access whose
/// region is not classified, <c>Trap</c> for everything that can raise a trap, and the channels a
/// service contract names. A channel ending in <c>*</c> stands for every channel it is a prefix of, and
/// <c>Memory.Unknown</c> overlaps every <c>Memory</c> channel. A trap writes <c>Trap</c>, so traps stay
/// in order; a trap that does not resume reads every register, flag, memory and device channel, so no
/// earlier effect moves behind it; a resuming trap with a contract touches exactly its contract; one
/// without a contract reads and writes all of them.
/// </summary>
public static class MachineEffects {

  /// <summary>The channel of every trap and trapping operation.</summary>
  public const string Trap = "Trap";

  /// <summary>The channel of memory whose region is unknown.</summary>
  public const string UnknownMemory = "Memory.Unknown";

  /// <summary>Every device channel.</summary>
  public const string AnyDevice = "Device:*";

  /// <summary>The channel of a root register.</summary>
  public static string Register(IsaProfile profile, string root) => $"Machine.Registers:{profile.Id}/{root}";

  /// <summary>The channel of a flag.</summary>
  public static string Flag(IsaProfile profile, string flag) => $"Machine.Flags:{profile.Id}/{flag}";

  /// <summary>The effect of an instruction.</summary>
  /// <param name="instruction">The instruction, verified against <paramref name="profile"/>.</param>
  /// <param name="profile">The machine profile.</param>
  /// <returns>Its effect.</returns>
  public static MachineEffect Of(MachineInstruction instruction, IsaProfile profile) {
    ArgumentNullException.ThrowIfNull(instruction);
    ArgumentNullException.ThrowIfNull(profile);
    var storage = instruction.Storage;
    switch (instruction.Opcode) {
      case MachineOpcode.RegRead:
        return new([Register(profile, profile.Root(profile.Register(storage!)!).Name)], []);
      case MachineOpcode.RegWrite:
        var register = profile.Register(storage!)!;
        var root = Register(profile, profile.Root(register).Name);
        return new(profile.ReplacesRoot(register) ? [] : [root], [root]);
      case MachineOpcode.FlagRead:
        return new([Flag(profile, storage!)], []);
      case MachineOpcode.FlagWrite or MachineOpcode.FlagUndef:
        return new([], [Flag(profile, storage!)]);
      case MachineOpcode.Load:
        return new([UnknownMemory], []);
      case MachineOpcode.Store:
        return new([], [UnknownMemory]);
      case var opcode when MachineOpcodes.IsDivision(opcode):
        return new([], [Trap]);
      case MachineOpcode.MachineTrap:
        return OfTrap(instruction, profile);
      default:
        return MachineEffect.None;
    }
  }

  private static MachineEffect OfTrap(MachineInstruction instruction, IsaProfile profile) {
    var trap = instruction.Trap!;
    var everything = profile.Registers.Where(register => register.IsRoot).Select(register => Register(profile, register.Name))
      .Concat(profile.Flags.Select(flag => Flag(profile, flag.Name)))
      .Append(UnknownMemory).Append(AnyDevice).ToImmutableArray();
    if (!trap.Resumes)
      return new(everything, [Trap]);
    if (trap.Service is not { } service)
      return new(everything, [.. everything, Trap]);

    string RootOf(string name) => Register(profile, profile.Root(profile.Register(name)!).Name);
    return new(
      [.. service.RegistersRead.Select(RootOf).Concat(service.Channels).Distinct(StringComparer.Ordinal)],
      [.. service.RegistersWritten.Select(RootOf).Concat(service.Channels).Append(Trap).Distinct(StringComparer.Ordinal)]);
  }

  /// <summary>Whether two channels may name the same state.</summary>
  /// <param name="a">One channel.</param>
  /// <param name="b">The other.</param>
  /// <returns>Whether they overlap.</returns>
  public static bool Overlap(string a, string b) {
    ArgumentNullException.ThrowIfNull(a);
    ArgumentNullException.ThrowIfNull(b);
    static bool Covers(string pattern, string channel) => pattern.EndsWith('*') && channel.StartsWith(pattern[..^1], StringComparison.Ordinal);
    static bool IsMemory(string channel) => channel.StartsWith("Memory", StringComparison.Ordinal);
    return a == b || Covers(a, b) || Covers(b, a)
      || (a == UnknownMemory && IsMemory(b)) || (b == UnknownMemory && IsMemory(a));
  }

  /// <summary>Whether two instructions must keep their order: one writes a channel the other reads or writes.</summary>
  /// <param name="first">One instruction.</param>
  /// <param name="second">The other.</param>
  /// <param name="profile">The machine profile.</param>
  /// <returns>Whether they interfere.</returns>
  public static bool Interferes(MachineInstruction first, MachineInstruction second, IsaProfile profile) {
    var a = Of(first, profile);
    var b = Of(second, profile);
    static bool Any(IEnumerable<string> writes, IEnumerable<string> touched) => writes.Any(write => touched.Any(other => Overlap(write, other)));
    return Any(a.Writes, b.Reads.Concat(b.Writes)) || Any(b.Writes, a.Reads);
  }

}
