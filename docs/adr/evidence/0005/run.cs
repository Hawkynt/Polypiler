// Evaluates the ADR 0005 input set with System.Math.
using System;
using System.Globalization;
using System.IO;

var input = new StreamReader(Console.OpenStandardInput());
var output = new StreamWriter(Console.OpenStandardOutput()) { NewLine = "\n" };
while (input.ReadLine() is { } line) {
  var parts = line.Split(' ');
  var v = new double[3];
  for (var i = 2; i < parts.Length; ++i)
    v[i - 2] = BitConverter.UInt64BitsToDouble(ulong.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
  double x = v[0], y = v[1], z = v[2];
  var r = parts[0] switch {
    "sin" => Math.Sin(x),
    "cos" => Math.Cos(x),
    "tan" => Math.Tan(x),
    "exp" => Math.Exp(x),
    "log" => Math.Log(x),
    "log2" => Math.Log2(x),
    "log10" => Math.Log10(x),
    "pow" => Math.Pow(x, y),
    "atan2" => Math.Atan2(x, y),
    "sinh" => Math.Sinh(x),
    "cosh" => Math.Cosh(x),
    "tanh" => Math.Tanh(x),
    "cbrt" => Math.Cbrt(x),
    "hypot" => double.Hypot(x, y),
    "add" => x + y,
    "sub" => x - y,
    "mul" => x * y,
    "div" => x / y,
    "sqrt" => Math.Sqrt(x),
    "fma" => Math.FusedMultiplyAdd(x, y, z),
    _ => throw new ArgumentException(parts[0])
  };
  output.WriteLine(BitConverter.DoubleToUInt64Bits(r).ToString("x16", CultureInfo.InvariantCulture));
}
output.Flush();
