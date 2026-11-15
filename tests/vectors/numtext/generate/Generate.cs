// The .NET half of tests/vectors/numtext/shortest.txt: reads what ecma.mjs wrote and puts what .NET
// writes with the invariant culture after each bit pattern. Usage: dotnet run Generate.cs < ecma.txt
using System;
using System.Globalization;

var invariant = CultureInfo.InvariantCulture;
Console.Out.NewLine = "\n";
for (var line = Console.ReadLine(); line is not null; line = Console.ReadLine()) {
  var parts = line.Split(' ');
  if (parts[0] == "f64") {
    var value = BitConverter.Int64BitsToDouble((long)ulong.Parse(parts[1], NumberStyles.HexNumber, invariant));
    Console.WriteLine($"f64 {parts[1]} {value.ToString(invariant)} {parts[2]}");
  } else {
    var value = BitConverter.Int32BitsToSingle((int)uint.Parse(parts[1], NumberStyles.HexNumber, invariant));
    Console.WriteLine($"f32 {parts[1]} {value.ToString(invariant)}");
  }
}
