// SPDX-License-Identifier: MIT
namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The helpers of the integer and bit operations, each family in a file of its own. Like every
/// helper they are C# 2 for .NET Framework 2.0, and they compute the same whatever overflow checking
/// the consuming project sets. Most of them work on the 64 bits of a value's pattern and are told
/// its width; the template of the row converts the value to those bits and the result back. A value
/// outside what an operation takes throws what the IR's trap is in generated C#: an
/// <c>ArithmeticException</c> for an invalid operation, an <c>IndexOutOfRangeException</c> for a
/// range outside an array, and the exceptions of division and of checked arithmetic.
/// </summary>
internal static partial class CSharpIntegerHelpers {

  private const string Invalid = "throw new global::System.ArithmeticException(\"invalid operation\");";

  /// <summary>The 64 bits of a mask of the lowest <c>bits</c> bits, as a declaration of the helper's body.</summary>
  private const string Mask = "ulong mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1UL;";

}
