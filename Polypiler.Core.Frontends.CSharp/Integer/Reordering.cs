using System.Collections.Generic;

namespace Polypiler.Core.Frontends.CSharp;

public static partial class CSharpIntegerApi {

  /// <summary>The byte swap of <c>BinaryPrimitives</c>.</summary>
  private static IEnumerable<CSharpIntegerMember> Reorderings => [
    new(Binary, "ReverseEndianness", "ByteSwap")
  ];

}
