using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.CSharp;

public static partial class CSharpIntegerApi {

  private const string Span = "the bytes are a byte[], array.AsSpan(index) or array.AsSpan(index, size) with the size of the value as a constant";
  private const string Written = "; the value is one that cannot trap and has no effect, as .NET checks the span before it evaluates the value";

  private const string Encoded = "; the bytes are the IEEE 754 encoding of the number";
  private const string ArrayAndIndex = "the bytes are a byte[] and the index of the first one; a span is refused";

  /// <summary>
  /// Endian loads and stores of <c>BinaryPrimitives</c> and the loads of <c>BitConverter</c>, which is
  /// little-endian on every supported host: of the integers of more than one byte, and of <c>float</c>
  /// and <c>double</c> as their encoding.
  /// </summary>
  private static IEnumerable<CSharpIntegerMember> EndianAccesses => [
    .. new[] { "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64" }.SelectMany(type => (IEnumerable<CSharpIntegerMember>)[
      new(Binary, $"Read{type}LittleEndian", "LoadLittle", Span),
      new(Binary, $"Read{type}BigEndian", "LoadBig", Span),
      new(Binary, $"Write{type}LittleEndian", "StoreLittle", Span + Written),
      new(Binary, $"Write{type}BigEndian", "StoreBig", Span + Written),
      new(Converter, $"To{type}", "LoadLittle")
    ]),
    .. new[] { "Single", "Double" }.SelectMany(type => (IEnumerable<CSharpIntegerMember>)[
      new(Binary, $"Read{type}LittleEndian", "LoadLittle", Span + Encoded),
      new(Binary, $"Read{type}BigEndian", "LoadBig", Span + Encoded),
      new(Binary, $"Write{type}LittleEndian", "StoreLittle", Span + Written + Encoded),
      new(Binary, $"Write{type}BigEndian", "StoreBig", Span + Written + Encoded),
      new(Converter, $"To{type}", "LoadLittle", ArrayAndIndex + Encoded)
    ])
  ];

}
