using System;
using System.Buffers.Binary;
using System.IO;

var result = new byte[4];
foreach (var line in File.ReadLines(args[0])) {
  var image = Convert.FromHexString(line);
  var left = BinaryPrimitives.ReadUInt32LittleEndian(image);
  var right = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(4));
  if (args[1] == "add") {
    BinaryPrimitives.WriteUInt32LittleEndian(result, unchecked(left + right));
  } else if (args[1] == "div") {
    try {
      BinaryPrimitives.WriteInt32LittleEndian(result, unchecked((int)left) / unchecked((int)right));
    } catch (DivideByZeroException error) {
      Console.WriteLine($"!divide-by-zero {error.GetType().Name}");
      continue;
    }
  } else {
    return 2;
  }

  Console.WriteLine(Convert.ToHexStringLower(result));
}

return 0;
