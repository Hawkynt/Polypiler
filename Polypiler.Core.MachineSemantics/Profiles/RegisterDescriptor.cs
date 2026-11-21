namespace Polypiler.Core.MachineSemantics;

/// <summary>What a write to a sub-register does to the bits of its root that it does not name.</summary>
public enum SubRegisterWrite {

  /// <summary>The other bits of the root keep their value: <c>AL</c>, <c>AX</c> on x86.</summary>
  Preserve,

  /// <summary>The whole root is replaced by the written value, zero-extended: <c>EAX</c> in 64-bit mode.</summary>
  ZeroExtend

}

/// <summary>What a register is for; the classes <see cref="Float"/> and <see cref="Vector"/> are reserved.</summary>
public enum RegisterClass {

  /// <summary>A general-purpose register.</summary>
  General,

  /// <summary>The stack pointer.</summary>
  StackPointer,

  /// <summary>The program counter.</summary>
  ProgramCounter,

  /// <summary>A segment register.</summary>
  Segment,

  /// <summary>A status register whose bits are flags.</summary>
  Status,

  /// <summary>A floating-point register; reserved, no profile declares one yet.</summary>
  Float,

  /// <summary>A vector register; reserved, no profile declares one yet.</summary>
  Vector

}

/// <summary>
/// One register of a machine profile. A root register has no <see cref="Parent"/>; a sub-register names
/// bits <see cref="BitOffset"/> to <see cref="BitOffset"/> + <see cref="Bits"/> - 1 of its parent, and
/// <see cref="Write"/> says what writing it does to the rest of its root.
/// </summary>
/// <param name="Name">The register's name, unique among registers and flags of its profile.</param>
/// <param name="Bits">Its width, 1 to 64.</param>
/// <param name="Parent">The register it is part of, or <see langword="null"/> for a root.</param>
/// <param name="BitOffset">The lowest bit it occupies in its parent; 0 for a root.</param>
/// <param name="Write">What a write does to the bits of the root outside it; <see cref="SubRegisterWrite.Preserve"/> for a root.</param>
/// <param name="Class">What the register is for.</param>
public sealed record RegisterDescriptor(string Name, int Bits, string? Parent, int BitOffset, SubRegisterWrite Write, RegisterClass Class) {

  /// <summary>Whether the register is a root, not part of another.</summary>
  public bool IsRoot => this.Parent is null;

}

/// <summary>
/// One flag of a machine profile. A flag kept in a status register names it and its bit; a flag the
/// machine keeps apart has no register.
/// </summary>
/// <param name="Name">The flag's name, unique among registers and flags of its profile.</param>
/// <param name="Register">The status register holding it, or <see langword="null"/>.</param>
/// <param name="Bit">Its bit in <paramref name="Register"/>; 0 without one.</param>
public sealed record FlagDescriptor(string Name, string? Register, int Bit);
