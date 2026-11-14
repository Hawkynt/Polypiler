using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The integer and bit operations of the IR, computed one bit at a time. A value is an array of its
/// bits, the lowest first; every operation is a loop over single bits - a ripple-carry adder, a
/// shift-and-add multiplier, a restoring divider, comparisons from the top bit down. Nothing here
/// calls <see cref="IntegerOperations"/>, the interpreter, a helper of a backend or a method of the
/// base library that computes on integers, so it is an oracle of its own for all of them.
/// </summary>
internal static class IntegerReference {

  /// <summary>Which trap an operation ends with.</summary>
  private sealed class Trapped(IrTrap trap) : Exception {
    public IrTrap Trap { get; } = trap;
  }

  /// <summary>
  /// What the operation does for host values of the argument types, in the notation of
  /// <see cref="PrototypeFixture.Outcome"/>: the result, then the contents of every array argument,
  /// or the trap. The arrays are copied first, so the arguments stay as they are.
  /// </summary>
  public static string Outcome(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> types, object[] arguments) {
    var copies = arguments.Select(argument => argument is byte[] bytes ? bytes.Clone() : argument).ToArray();
    try {
      return PrototypeFixture.Outcome(Compute(op, result, types, copies), copies);
    } catch (Trapped trapped) {
      return PrototypeFixture.Trap(trapped.Trap);
    }
  }

  private static object? Compute(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> types, object[] arguments) {
    var type = types[0];
    var width = type.Bits;
    bool[] A() => BitsOf(arguments[0], width);
    bool[] B() => BitsOf(arguments[1], width);
    int Int(int index) => (int)arguments[index];
    bool Flag(int index) => (bool)arguments[index];
    switch (op) {
      case IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight:
        return Host(type, Rotate(A(), Int(1), op == IrIntrinsicOp.RotateLeft));
      case IrIntrinsicOp.PopCount:
        return A().Count(bit => bit);
      case IrIntrinsicOp.LeadingZeroCount:
        return Enumerable.Reverse(A()).TakeWhile(bit => !bit).Count();
      case IrIntrinsicOp.TrailingZeroCount:
        return A().TakeWhile(bit => !bit).Count();
      case IrIntrinsicOp.Log2:
        return Math.Max(0, Highest(A()));
      case IrIntrinsicOp.Parity:
        return A().Aggregate(0, (parity, bit) => bit ? 1 - parity : parity);
      case IrIntrinsicOp.ByteSwap:
        return Host(type, ByteSwap(A()));
      case IrIntrinsicOp.BitReverse:
        return Host(type, Enumerable.Reverse(A()).ToArray());
      case IrIntrinsicOp.IsPow2:
        return !Negative(A(), type.IsSigned) && A().Count(bit => bit) == 1;
      case IrIntrinsicOp.RoundUpPow2:
        return Host(type, RoundUp(A()));
      case IrIntrinsicOp.BigMul:
        return Host(result, Multiply(Extend(A(), 64, type.IsSigned), Extend(B(), 64, type.IsSigned)));
      case IrIntrinsicOp.MulHigh:
        return Host(type, Multiply(Extend(A(), 2 * width, type.IsSigned), Extend(B(), 2 * width, type.IsSigned))[width..]);
      case IrIntrinsicOp.DivFloor or IrIntrinsicOp.ModFloor or IrIntrinsicOp.DivEuclid or IrIntrinsicOp.ModEuclid:
        return Host(type, Divide(op, A(), B(), type.IsSigned));
      case IrIntrinsicOp.Min or IrIntrinsicOp.Max:
        var less = Compare(A(), B(), type.IsSigned) <= 0;
        return arguments[op == IrIntrinsicOp.Min == less ? 0 : 1];
      case IrIntrinsicOp.Clamp:
        var low = B();
        var high = BitsOf(arguments[2], width);
        Require(Compare(low, high, type.IsSigned) <= 0);
        var value = A();
        return Host(type, Compare(value, low, type.IsSigned) < 0 ? low : Compare(value, high, type.IsSigned) > 0 ? high : value);
      case IrIntrinsicOp.Abs or IrIntrinsicOp.AbsWrap:
        var magnitude = A();
        if (!Negative(magnitude, type.IsSigned))
          return Host(type, magnitude);

        if (op == IrIntrinsicOp.Abs && Negative(Negate(magnitude), true))
          throw new Trapped(IrTrap.IntegerOverflow);

        return Host(type, Negate(magnitude));
      case IrIntrinsicOp.Sign:
        return A().All(bit => !bit) ? 0 : Negative(A(), type.IsSigned) ? -1 : 1;
      case IrIntrinsicOp.AddSaturate or IrIntrinsicOp.SubSaturate or IrIntrinsicOp.MulSaturate:
        var exact = Exact(op, A(), B(), type.IsSigned);
        return Host(type, Fits(exact, width, type.IsSigned) ? exact[..width] : Bound(width, type.IsSigned, exact[^1]));
      case IrIntrinsicOp.AddOverflows or IrIntrinsicOp.SubOverflows or IrIntrinsicOp.MulOverflows:
        return !Fits(Exact(op, A(), B(), type.IsSigned), width, type.IsSigned);
      case IrIntrinsicOp.AddCarry or IrIntrinsicOp.CarryOut:
        var sum = Add(Extend(A(), width + 1, false), Extend(B(), width + 1, false), Flag(2));
        return op == IrIntrinsicOp.AddCarry ? Host(type, sum[..width]) : sum[width];
      case IrIntrinsicOp.SubBorrow or IrIntrinsicOp.BorrowOut:
        var difference = Add(Extend(A(), width + 1, false), Negate(Extend(B(), width + 1, false)), false);
        if (Flag(2))
          difference = Add(difference, Ones(width + 1), false);

        return op == IrIntrinsicOp.SubBorrow ? Host(type, difference[..width]) : difference[width];
      case IrIntrinsicOp.ExtractBits:
        Require(Int(1) >= 0 && Int(2) >= 0 && (long)Int(1) + Int(2) <= width);
        return Host(type, Enumerable.Range(0, width).Select(index => index < Int(2) && A()[Int(1) + index]).ToArray());
      case IrIntrinsicOp.InsertBits:
        Require(Int(2) >= 0 && Int(3) >= 0 && (long)Int(2) + Int(3) <= width);
        var target = A();
        var field = B();
        return Host(type, Enumerable.Range(0, width).Select(index => index >= Int(2) && index < Int(2) + Int(3) ? field[index - Int(2)] : target[index]).ToArray());
      case IrIntrinsicOp.TestBit or IrIntrinsicOp.SetBit or IrIntrinsicOp.ClearBit or IrIntrinsicOp.ToggleBit:
        Require(Int(1) >= 0 && Int(1) < width);
        var bits = A();
        if (op == IrIntrinsicOp.TestBit)
          return bits[Int(1)];

        bits[Int(1)] = op == IrIntrinsicOp.SetBit || (op == IrIntrinsicOp.ToggleBit && !bits[Int(1)]);
        return Host(type, bits);
      case IrIntrinsicOp.LowMask:
        Require(Int(0) >= 0 && Int(0) <= result.Bits);
        return Host(result, Enumerable.Range(0, result.Bits).Select(index => index < Int(0)).ToArray());
      case IrIntrinsicOp.IsolateLowestSet or IrIntrinsicOp.ClearLowestSet or IrIntrinsicOp.MaskToLowestSet:
        var lowest = Array.IndexOf(A(), true);
        return Host(type, Enumerable.Range(0, width).Select(index => op switch {
          IrIntrinsicOp.IsolateLowestSet => index == lowest,
          IrIntrinsicOp.ClearLowestSet => index != lowest && A()[index],
          _ => lowest < 0 || index <= lowest
        }).ToArray());
      case IrIntrinsicOp.SignExtend or IrIntrinsicOp.ZeroExtend:
        Require(Int(1) >= 1 && Int(1) <= width);
        var source = A();
        return Host(type, Enumerable.Range(0, width).Select(index => index < Int(1) ? source[index] : op == IrIntrinsicOp.SignExtend && source[Int(1) - 1]).ToArray());
      case IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig:
        var bytes = (byte[])arguments[0];
        var size = Bytes(result, bytes, Int(1));
        var loaded = new bool[result.Bits];
        for (var k = 0; k < size; ++k)
          for (var bit = 0; bit < 8; ++bit)
            loaded[8 * k + bit] = ((bytes[Int(1) + (op == IrIntrinsicOp.LoadLittle ? k : size - 1 - k)] >> bit) & 1) == 1;

        return Host(result, loaded);
      default:
        var array = (byte[])arguments[0];
        var stored = BitsOf(arguments[2], types[2].Bits);
        var count = Bytes(types[2], array, Int(1));
        for (var k = 0; k < count; ++k) {
          var octet = 0;
          for (var bit = 7; bit >= 0; --bit)
            octet = octet * 2 + (stored[8 * k + bit] ? 1 : 0);

          array[Int(1) + (op == IrIntrinsicOp.StoreLittle ? k : count - 1 - k)] = (byte)octet;
        }

        return null;
    }
  }

  // ---------------------------------------------------------------- values as bits

  /// <summary>The bits of a host integer, the lowest first.</summary>
  private static bool[] BitsOf(object value, int width) {
    var bits = new bool[width];
    // Each bit is taken by itself; for a signed value the shift is arithmetic, which keeps the sign above the top.
    for (var index = 0; index < width; ++index)
      bits[index] = value switch {
        sbyte number => ((number >> Math.Min(index, 7)) & 1) == 1,
        short number => ((number >> Math.Min(index, 15)) & 1) == 1,
        int number => ((number >> Math.Min(index, 31)) & 1) == 1,
        long number => ((number >> Math.Min(index, 63)) & 1) == 1,
        byte number => index < 8 && ((number >> index) & 1) == 1,
        ushort number => index < 16 && ((number >> index) & 1) == 1,
        uint number => index < 32 && ((number >> index) & 1) == 1,
        _ => index < 64 && (((ulong)value >> index) & 1) == 1
      };

    return bits;
  }

  /// <summary>The host value of a type with the given bits.</summary>
  private static object Host(IrType type, bool[] bits) {
    var pattern = 0UL;
    for (var index = type.Bits - 1; index >= 0; --index)
      pattern = (pattern << 1) | (bits[index] ? 1UL : 0UL);

    var negative = type.IsSigned && bits[type.Bits - 1];
    var extended = negative ? pattern | ~(type.Bits == 64 ? ulong.MaxValue : (1UL << type.Bits) - 1) : pattern;
    return type.Kind switch {
      IrTypeKind.I8 => (sbyte)(long)extended,
      IrTypeKind.U8 => (byte)extended,
      IrTypeKind.I16 => (short)(long)extended,
      IrTypeKind.U16 => (ushort)extended,
      IrTypeKind.I32 => (int)(long)extended,
      IrTypeKind.U32 => (uint)extended,
      IrTypeKind.I64 => (long)extended,
      _ => extended
    };
  }

  private static bool[] Ones(int width) => Enumerable.Repeat(true, width).ToArray();

  /// <summary>The bits widened to a width, by copies of the top bit or by zeros.</summary>
  private static bool[] Extend(bool[] bits, int width, bool signed)
    => Enumerable.Range(0, width).Select(index => index < bits.Length ? bits[index] : signed && bits[^1]).ToArray();

  private static bool Negative(bool[] bits, bool signed) => signed && bits[^1];

  /// <summary>The position of the highest set bit, -1 when there is none.</summary>
  private static int Highest(bool[] bits) => Array.LastIndexOf(bits, true);

  // ---------------------------------------------------------------- arithmetic on bits

  /// <summary>The sum of two values of one width with an incoming carry, cut to the width: a ripple-carry adder.</summary>
  private static bool[] Add(bool[] a, bool[] b, bool carry) {
    var sum = new bool[a.Length];
    for (var index = 0; index < a.Length; ++index) {
      sum[index] = a[index] ^ b[index] ^ carry;
      carry = (a[index] && b[index]) || (carry && (a[index] ^ b[index]));
    }

    return sum;
  }

  /// <summary>The two's complement: every bit inverted, then one added.</summary>
  private static bool[] Negate(bool[] bits) => Add(bits.Select(bit => !bit).ToArray(), new bool[bits.Length], true);

  /// <summary>The product of two values of one width, cut to that width: shift and add.</summary>
  private static bool[] Multiply(bool[] a, bool[] b) {
    var product = new bool[a.Length];
    for (var index = 0; index < b.Length; ++index)
      if (b[index])
        product = Add(product, Enumerable.Range(0, a.Length).Select(position => position >= index && a[position - index]).ToArray(), false);

    return product;
  }

  /// <summary>-1, 0 or 1 as the first value is below, equal to or above the second, read from the top bit down.</summary>
  private static int Compare(bool[] a, bool[] b, bool signed) {
    if (signed && a[^1] != b[^1])
      return a[^1] ? -1 : 1;

    for (var index = a.Length - 1; index >= 0; --index)
      if (a[index] != b[index])
        return a[index] ? 1 : -1;

    return 0;
  }

  /// <summary>
  /// The exact result of a sum, a difference or a product, in a width that holds it as a signed
  /// number: two bits more than the operands for a sum or a difference, twice as many and two more
  /// for a product.
  /// </summary>
  private static bool[] Exact(IrIntrinsicOp op, bool[] a, bool[] b, bool signed) {
    var width = op is IrIntrinsicOp.MulSaturate or IrIntrinsicOp.MulOverflows ? 2 * a.Length + 2 : a.Length + 2;
    var (x, y) = (Extend(a, width, signed), Extend(b, width, signed));
    return op switch {
      IrIntrinsicOp.AddSaturate or IrIntrinsicOp.AddOverflows => Add(x, y, false),
      IrIntrinsicOp.SubSaturate or IrIntrinsicOp.SubOverflows => Add(x, Negate(y), false),
      _ => Multiply(x, y)
    };
  }

  /// <summary>Whether a signed number of a larger width is a value of a type of the given width.</summary>
  private static bool Fits(bool[] exact, int width, bool signed)
    => signed ? exact[(width - 1)..].All(bit => bit == exact[^1]) : exact[width..].All(bit => !bit);

  /// <summary>The smallest value of a type, for a negative result, or its largest.</summary>
  private static bool[] Bound(int width, bool signed, bool negative)
    => Enumerable.Range(0, width).Select(index => signed ? (index == width - 1 ? negative : !negative) : !negative).ToArray();

  /// <summary>
  /// The four divisions. Divisor and dividend are taken as magnitudes one bit wider than the type, so
  /// that the smallest signed value has one; a restoring divider gives quotient and remainder of the
  /// magnitudes; the signs and the rounding are applied after.
  /// </summary>
  private static bool[] Divide(IrIntrinsicOp op, bool[] a, bool[] b, bool signed) {
    if (b.All(bit => !bit))
      throw new Trapped(IrTrap.DivideByZero);

    if (signed && a[^1] && a[..^1].All(bit => !bit) && b.All(bit => bit))
      throw new Trapped(IrTrap.IntegerOverflow);

    var width = a.Length + 1;
    var (x, y) = (Extend(a, width, signed), Extend(b, width, signed));
    var (negativeDividend, negativeDivisor) = (x[^1], y[^1]);
    var (quotient, remainder) = Restoring(negativeDividend ? Negate(x) : x, negativeDivisor ? Negate(y) : y);
    if (negativeDividend != negativeDivisor)
      quotient = Negate(quotient);

    if (negativeDividend)
      remainder = Negate(remainder);

    var nonzero = remainder.Any(bit => bit);
    var one = Extend([true], width, false);
    var floor = op is IrIntrinsicOp.DivFloor or IrIntrinsicOp.ModFloor;
    // Floor moves the quotient down where the remainder has the other sign than the divisor;
    // Euclid moves it away from the divisor's sign where the remainder is negative.
    if (floor ? nonzero && negativeDividend != negativeDivisor : nonzero && negativeDividend) {
      var down = floor || !negativeDivisor;
      quotient = down ? Add(quotient, Negate(one), false) : Add(quotient, one, false);
      remainder = down ? Add(remainder, y, false) : Add(remainder, Negate(y), false);
    }

    return (op is IrIntrinsicOp.DivFloor or IrIntrinsicOp.DivEuclid ? quotient : remainder)[..a.Length];
  }

  /// <summary>Quotient and remainder of two magnitudes, one bit of the quotient per step.</summary>
  private static (bool[] Quotient, bool[] Remainder) Restoring(bool[] dividend, bool[] divisor) {
    var quotient = new bool[dividend.Length];
    var remainder = new bool[dividend.Length];
    for (var index = dividend.Length - 1; index >= 0; --index) {
      remainder = [dividend[index], .. remainder[..^1]];
      if (Compare(remainder, divisor, false) >= 0) {
        remainder = Add(remainder, Negate(divisor), false);
        quotient[index] = true;
      }
    }

    return (quotient, remainder);
  }

  // ---------------------------------------------------------------- reordering and powers

  private static bool[] Rotate(bool[] bits, int count, bool left) {
    var width = bits.Length;
    var by = ((count % width) + width) % width;
    return Enumerable.Range(0, width).Select(index => bits[((left ? index - by : index + by) % width + width) % width]).ToArray();
  }

  private static bool[] ByteSwap(bool[] bits) {
    var count = bits.Length / 8;
    return Enumerable.Range(0, bits.Length).Select(index => bits[8 * (count - 1 - index / 8) + index % 8]).ToArray();
  }

  /// <summary>The smallest power of two not below the bits read as unsigned, 0 for 0 and where it is 2^width.</summary>
  private static bool[] RoundUp(bool[] bits) {
    var highest = Highest(bits);
    if (highest < 0)
      return bits;

    var exponent = bits.Count(bit => bit) == 1 ? highest : highest + 1;
    return Enumerable.Range(0, bits.Length).Select(index => index == exponent).ToArray();
  }

  // ---------------------------------------------------------------- traps

  private static void Require(bool holds) {
    if (!holds)
      throw new Trapped(IrTrap.InvalidOperation);
  }

  /// <summary>The number of bytes of a type, all of which have to lie in the array at the index.</summary>
  private static int Bytes(IrType type, byte[] array, int index) {
    var size = type.Bits / 8;
    if (index < 0 || (long)index + size > array.Length)
      throw new Trapped(IrTrap.IndexOutOfRange);

    return size;
  }

}
