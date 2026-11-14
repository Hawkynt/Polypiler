namespace Polypiler.Permutation.Semantics;

/// <summary>How the lanes of a <see cref="BitLayout"/> lie in memory bytes.</summary>
public enum ByteOrder {

  /// <summary>The value is not bound to memory bytes; bit numbers are all there is.</summary>
  NotApplicable,

  /// <summary>Byte 0 holds bits 0 to 7 of a lane.</summary>
  LittleEndian,

  /// <summary>Byte 0 holds the most significant eight bits of a lane.</summary>
  BigEndian

}
