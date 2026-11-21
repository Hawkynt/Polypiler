using System;
using System.Globalization;

namespace Polypiler.Core.MachineSemantics;

/// <summary>
/// The type of a MachineIR value: an unsigned bit vector of 1 to 64 bits, or no value at all. A register
/// keeps the machine's width and no signedness; an operation chooses how it reads the bits. The 1-bit
/// type is written <c>bool</c>, every other <c>u&lt;bits&gt;</c>, and the type of an instruction without
/// a value is not written.
/// </summary>
/// <param name="Bits">The width: 0 for no value, else 1 to 64.</param>
public readonly record struct MachineType(int Bits) {

  /// <summary>No value: the type of a store, a write and a terminator.</summary>
  public static MachineType Void => new(0);

  /// <summary>One bit: a flag, a comparison.</summary>
  public static MachineType Bool => new(1);

  /// <summary>Eight bits.</summary>
  public static MachineType U8 => new(8);

  /// <summary>Sixteen bits.</summary>
  public static MachineType U16 => new(16);

  /// <summary>Thirty-two bits.</summary>
  public static MachineType U32 => new(32);

  /// <summary>Sixty-four bits.</summary>
  public static MachineType U64 => new(64);

  /// <summary>The bits of the type set, the rest clear.</summary>
  public ulong Mask => this.Bits >= 64 ? ulong.MaxValue : (1UL << this.Bits) - 1;

  /// <summary>An unsigned type of a width.</summary>
  /// <param name="bits">The width, 1 to 64.</param>
  /// <returns>The type.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The width is outside 1 to 64.</exception>
  public static MachineType Of(int bits) => bits is >= 1 and <= 64 ? new(bits) : throw new ArgumentOutOfRangeException(nameof(bits), bits, "a machine value has 1 to 64 bits");

  /// <summary>Reads a type name: <c>bool</c> or <c>u1</c> to <c>u64</c>.</summary>
  /// <param name="text">The name.</param>
  /// <param name="type">The type.</param>
  /// <returns>Whether the text names a type.</returns>
  public static bool TryParse(string text, out MachineType type) {
    ArgumentNullException.ThrowIfNull(text);
    type = Void;
    if (text == "bool") {
      type = Bool;
      return true;
    }

    if (text.Length < 2 || text[0] != 'u' || text[1] == '0' || !int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var bits) || bits > 64)
      return false;

    type = new(bits);
    return true;
  }

  /// <summary>The name: <c>bool</c>, <c>u8</c>, …; <c>void</c> for no value.</summary>
  public override string ToString() => this.Bits switch {
    0 => "void",
    1 => "bool",
    var bits => "u" + bits.ToString(CultureInfo.InvariantCulture)
  };

}
