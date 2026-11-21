using System.Collections.Generic;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>
  /// The update of the register of a cyclic redundancy check of the width of an unsigned type over a
  /// span of an array of bytes: <c>(crc, poly, reflected, data, index, count)</c>, the polynomial in its
  /// normal form without its top bit (Williams, "A Painless Guide to CRC Error Detection Algorithms",
  /// 1993, the Rocksoft parameter model). Initial value, final XOR and the reflection of the output are
  /// not part of it.
  /// </summary>
  private static IEnumerable<IntegerOperation> Crc => [
    Same(IrIntrinsicOp.CrcUpdate, _unsigned, type => [type, type, IrType.Bool, _bytes, IrType.I32, IrType.I32],
      (_, types, arguments) => CrcUpdate(types[0], Long(arguments, 0), Long(arguments, 1), (bool)arguments[2], (byte[])arguments[3], Long(arguments, 4), Long(arguments, 5)))
  ];

  /// <summary>
  /// The register of a CRC after the bytes of a span, in ascending order, one bit at a time. For a
  /// reflected CRC each byte is XORed into the bottom of the register, which then moves down eight
  /// times, XORing the polynomial reversed in the width whenever a one leaves it; for a normal one each
  /// byte is XORed into the top, and the register moves up, XORing the polynomial whenever a one
  /// leaves the top.
  /// </summary>
  /// <param name="type">An unsigned integer type: the width of the register.</param>
  /// <param name="crc">The register before the span.</param>
  /// <param name="poly">The generator polynomial in normal form without its top bit, not 0.</param>
  /// <param name="reflected">Whether the CRC is reflected: bytes enter at the bottom, lowest bit first.</param>
  /// <param name="data">The bytes.</param>
  /// <param name="index">The first byte of the span.</param>
  /// <param name="count">The number of bytes of the span.</param>
  /// <returns>The register after the span; the register itself for an empty one.</returns>
  /// <exception cref="IrTrapException">
  /// The span does not lie within the array: an index out of range, before any byte is read. A
  /// polynomial of 0: an invalid operation.
  /// </exception>
  public static long CrcUpdate(IrType type, long crc, long poly, bool reflected, byte[] data, long index, long count) {
    if (index < 0 || count < 0 || index + count > data.Length)
      throw new IrTrapException(IrTrap.IndexOutOfRange);

    var width = Width(type);
    var generator = Pattern(type, poly);
    Require(generator != 0);
    var register = Pattern(type, crc);
    var reversed = 0UL;
    for (var bit = 0; bit < width; ++bit)
      if (((generator >> bit) & 1) == 1)
        reversed |= 1UL << (width - 1 - bit);

    var top = 1UL << (width - 1);
    for (var at = index; at < index + count; ++at) {
      var octet = (ulong)data[at];
      if (reflected) {
        register ^= octet;
        for (var step = 0; step < 8; ++step)
          register = (register & 1) == 1 ? (register >> 1) ^ reversed : register >> 1;
      } else {
        register ^= octet << (width - 8);
        for (var step = 0; step < 8; ++step)
          register = ((register & top) != 0 ? (register << 1) ^ generator : register << 1) & Ones(width);
      }
    }

    return Value(type, register);
  }

}
