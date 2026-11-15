using System.Collections.Generic;

namespace Polypiler.Core.IR;

public static partial class IntegerOperations {

  /// <summary>
  /// The two 32-bit limbs of a 64-bit integer: the high one taken out, and the integer joined from
  /// both. The low limb is the conversion to <c>u32</c>, which every target has. A profile without a
  /// 64-bit integer of its own builds every operation on 64 bits from these (optimization O0075).
  /// </summary>
  private static IEnumerable<IntegerOperation> Limbs => [
    Fixed(IrIntrinsicOp.HighWord, IrType.U32, [IrType.I64], _ => [IrType.I64], (_, _, arguments) => HighWord(Long(arguments, 0))),
    Fixed(IrIntrinsicOp.JoinWords, IrType.I64, [IrType.U32], _ => [IrType.U32, IrType.U32], (_, _, arguments) => JoinWords(Long(arguments, 0), Long(arguments, 1)))
  ];

  /// <summary>The upper 32 bits of a 64-bit integer, as a <c>u32</c> is carried.</summary>
  /// <param name="value">The integer.</param>
  /// <returns>The high limb.</returns>
  public static long HighWord(long value) => (long)((ulong)value >> 32);

  /// <summary>The 64-bit integer of a high and a low limb.</summary>
  /// <param name="high">The upper 32 bits, as a <c>u32</c> is carried.</param>
  /// <param name="low">The lower 32 bits, as a <c>u32</c> is carried.</param>
  /// <returns>The integer.</returns>
  public static long JoinWords(long high, long low) => (long)(((ulong)(uint)high << 32) | (uint)low);

}
