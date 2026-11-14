// SPDX-License-Identifier: MIT
using System.Linq;

namespace Polypiler.Core.Backends.CSharp;

internal static partial class CSharpIntegerHelpers {

  /// <summary>The quotient of two signed values rounded towards negative infinity.</summary>
  public static CSharpHelper FloorDivide { get; } = Divided("FloorDivide", [
    "long quotient = dividend / divisor;",
    "return dividend % divisor != 0L && (dividend < 0L) != (divisor < 0L) ? quotient - 1L : quotient;"
  ]);

  /// <summary>The remainder of the floor division, which has the sign of the divisor.</summary>
  public static CSharpHelper FloorModulo { get; } = Divided("FloorModulo", [
    "long remainder = dividend % divisor;",
    "return remainder != 0L && (remainder < 0L) != (divisor < 0L) ? remainder + divisor : remainder;"
  ]);

  /// <summary>The quotient of the Euclidean division of two signed values.</summary>
  public static CSharpHelper EuclidDivide { get; } = Divided("EuclidDivide", [
    "long quotient = dividend / divisor;",
    "if (dividend % divisor >= 0L) {",
    "  return quotient;",
    "}",
    "return divisor > 0L ? quotient - 1L : quotient + 1L;"
  ]);

  /// <summary>
  /// The remainder of the Euclidean division, never negative: a negative remainder plus the magnitude
  /// of the divisor, which for the smallest divisor wraps around to exactly that sum.
  /// </summary>
  public static CSharpHelper EuclidModulo { get; } = Divided("EuclidModulo", [
    "long remainder = dividend % divisor;",
    "if (remainder >= 0L) {",
    "  return remainder;",
    "}",
    "return divisor < 0L ? remainder - divisor : remainder + divisor;"
  ]);

  /// <summary>The quotient of two unsigned values; C# throws for a zero divisor by itself.</summary>
  public static CSharpHelper DivideUnsigned { get; } = new("DivideUnsigned", "ulong", "ulong dividend, ulong divisor", [
    "return dividend / divisor;"
  ]);

  /// <summary>The remainder of two unsigned values.</summary>
  public static CSharpHelper RemainderUnsigned { get; } = new("RemainderUnsigned", "ulong", "ulong dividend, ulong divisor", [
    "return dividend % divisor;"
  ]);

  /// <summary>
  /// A signed division with the traps of the division operator of the IR: a zero divisor, and the
  /// smallest value of the type - which the template passes - divided by -1.
  /// </summary>
  private static CSharpHelper Divided(string name, string[] body) => new(name, "long", "long dividend, long divisor, long smallest", [
    "if (divisor == 0L) {",
    "  throw new global::System.DivideByZeroException();",
    "}",
    "if (divisor == -1L && dividend == smallest) {",
    "  throw new global::System.OverflowException();",
    "}",
    "unchecked {",
    .. body.Select(line => "  " + line),
    "}"
  ]);

}
