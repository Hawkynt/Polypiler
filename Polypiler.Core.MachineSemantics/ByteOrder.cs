namespace Polypiler.Core.MachineSemantics;

/// <summary>The order in which a machine lays the bytes of a multi-byte value out in memory.</summary>
public enum ByteOrder {

  /// <summary>The least significant byte at the lowest address (x86, 6502, ARM in its usual mode).</summary>
  Little,

  /// <summary>The most significant byte at the lowest address (68000, network order).</summary>
  Big

}
