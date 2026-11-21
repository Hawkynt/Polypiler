using System;
using System.Collections.Immutable;
using System.Linq;

namespace Polypiler.Core.MachineSemantics.Traps;

/// <summary>Why a machine leaves a routine through its exception mechanism.</summary>
public enum MachineTrapKind {

  /// <summary><c>INT n</c>, <c>BRK</c> with a number: asks a handler for a service and resumes.</summary>
  SoftwareInterrupt,

  /// <summary><c>SYSCALL</c>, <c>SVC</c>: asks the kernel for a service and resumes.</summary>
  SystemCall,

  /// <summary><c>UD2</c>, <c>UDF</c>, an undefined opcode: a fault that ends the path.</summary>
  UndefinedInstruction,

  /// <summary>An instruction the current privilege level may not run: a fault.</summary>
  PrivilegedInstruction,

  /// <summary><c>INT3</c>, <c>BKPT</c>: a trap to a debugger, which resumes.</summary>
  Breakpoint,

  /// <summary><c>HLT</c>, <c>STP</c>: execution stops.</summary>
  Halt,

  /// <summary>A division by zero or a quotient that does not fit, raised unconditionally (x86 vector 0).</summary>
  DivideError,

  /// <summary><c>INTO</c> with the overflow flag set (x86 vector 4): a trap that resumes.</summary>
  OverflowTrap,

  /// <summary>An access the memory system refuses: a fault.</summary>
  AccessFault

}

/// <summary>
/// The registers a service reads and writes and the effect channels it touches beyond them: a trap with a
/// contract is no wider an effect than the contract says.
/// </summary>
/// <param name="Name">The service, for example <c>dos.write-char</c>.</param>
/// <param name="RegistersRead">The registers the service reads.</param>
/// <param name="RegistersWritten">The registers the service writes.</param>
/// <param name="Channels">Further effect channels, for example <c>Memory.Unknown</c> or <c>Device:console</c>.</param>
public sealed record ServiceContract(string Name, ImmutableArray<string> RegistersRead, ImmutableArray<string> RegistersWritten, ImmutableArray<string> Channels) {

  /// <inheritdoc/>
  public bool Equals(ServiceContract? other)
    => other is not null && this.Name == other.Name && this.RegistersRead.SequenceEqual(other.RegistersRead)
      && this.RegistersWritten.SequenceEqual(other.RegistersWritten) && this.Channels.SequenceEqual(other.Channels);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Name, this.RegistersRead.Length, this.RegistersWritten.Length, this.Channels.Length);

}

/// <summary>
/// What a <see cref="MachineOpcode.MachineTrap"/> does: its kind, vector, whether execution resumes after
/// it, and the service contract when one is known. Text form:
/// <c>MachineTrap[kind=software-interrupt, vector=0x21, resumes=true, id=t1] service("dos.write-char") reads(ah, dl) writes(al)</c>.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="Vector">The interrupt vector, or <see langword="null"/> for a kind without one.</param>
/// <param name="Resumes">Whether execution continues after the trap; a trap that does not resume ends its block.</param>
/// <param name="Service">The service contract, or <see langword="null"/>.</param>
public sealed record TrapDescriptor(MachineTrapKind Kind, long? Vector, bool Resumes, ServiceContract? Service) {

  /// <summary>The trap's identity in its function, or <see langword="null"/>.</summary>
  public string? Id { get; init; }

  /// <summary>Whether a kind resumes when the text does not say: the traps (interrupts, system calls, breakpoints, <c>INTO</c>) do, the faults and <c>halt</c> do not.</summary>
  /// <param name="kind">The kind.</param>
  /// <returns>Whether it resumes.</returns>
  public static bool ResumesByDefault(MachineTrapKind kind)
    => kind is MachineTrapKind.SoftwareInterrupt or MachineTrapKind.SystemCall or MachineTrapKind.Breakpoint or MachineTrapKind.OverflowTrap;

  /// <summary>The kind's name in the text form: <c>software-interrupt</c>, <c>undefined-instruction</c>, ….</summary>
  /// <param name="kind">The kind.</param>
  /// <returns>The name.</returns>
  public static string Name(MachineTrapKind kind) => kind switch {
    MachineTrapKind.SoftwareInterrupt => "software-interrupt",
    MachineTrapKind.SystemCall => "system-call",
    MachineTrapKind.UndefinedInstruction => "undefined-instruction",
    MachineTrapKind.PrivilegedInstruction => "privileged-instruction",
    MachineTrapKind.Breakpoint => "breakpoint",
    MachineTrapKind.Halt => "halt",
    MachineTrapKind.DivideError => "divide-error",
    MachineTrapKind.OverflowTrap => "overflow-trap",
    _ => "access-fault"
  };

  /// <summary>Reads a kind's name.</summary>
  /// <param name="name">The name.</param>
  /// <param name="kind">The kind.</param>
  /// <returns>Whether the name is a kind.</returns>
  public static bool TryParseKind(string name, out MachineTrapKind kind) {
    foreach (var candidate in Enum.GetValues<MachineTrapKind>())
      if (Name(candidate) == name) {
        kind = candidate;
        return true;
      }

    kind = default;
    return false;
  }

  /// <inheritdoc/>
  public bool Equals(TrapDescriptor? other)
    => other is not null && this.Kind == other.Kind && this.Vector == other.Vector && this.Resumes == other.Resumes && Equals(this.Service, other.Service) && this.Id == other.Id;

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Kind, this.Vector, this.Resumes, this.Id);

}
