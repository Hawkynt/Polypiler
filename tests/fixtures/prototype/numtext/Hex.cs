// Integers in hexadecimal, binary and with padded decimal digits: .NET writes the bits of the type,
// two's complement. Written for this repository.
using System;
using System.Globalization;

namespace Polypiler.Fixtures.Prototype;

public static class HexText {

  public static string Word(int value) => value.ToString("x8");

  public static string Upper(int value) => value.ToString("X");

  public static string Byte(byte value) => value.ToString("x2");

  public static string Short(short value) => value.ToString("x");

  public static string Long(long value) => value.ToString("X16");

  public static string Binary(int value) => Convert.ToString(value, 2);

  public static string Octal(long value) => Convert.ToString(value, 8);

  public static string Padded(int value) => value.ToString("D5", CultureInfo.InvariantCulture);

}
