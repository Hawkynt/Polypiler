using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.CSharp;

public static partial class CSharpIntegerApi {

  private const string Span = "the bytes are a byte[], array.AsSpan(index) or array.AsSpan(index, size) with the size of the value as a constant";
  private const string Written = "; the value is one that cannot trap and has no effect, as .NET checks the span before it evaluates the value";

  /// <summary>Endian loads and stores of <c>BinaryPrimitives</c> and the loads of <c>BitConverter</c>, which is little-endian on every supported host.</summary>
  private static IEnumerable<CSharpIntegerMember> EndianAccesses => [
    .. new[] { "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64" }.SelectMany(type => (IEnumerable<CSharpIntegerMember>)[
      new(Binary, $"Read{type}LittleEndian", "LoadLittle", Span),
      new(Binary, $"Read{type}BigEndian", "LoadBig", Span),
      new(Binary, $"Write{type}LittleEndian", "StoreLittle", Span + Written),
      new(Binary, $"Write{type}BigEndian", "StoreBig", Span + Written),
      new(Converter, $"To{type}", "LoadLittle")
    ])
  ];

}
