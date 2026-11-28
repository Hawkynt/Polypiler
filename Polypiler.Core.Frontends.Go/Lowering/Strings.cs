using System.Text;
using Polypiler.Core.Frontends.Go.Ast;

namespace Polypiler.Core.Frontends.Go.Lowering;

/// <summary>
/// The text of a Go string constant: its bytes are UTF-8 where Go writes valid text, but a string of
/// the IR is UTF-16, so a Go string that is no valid UTF-8 - <c>"\xff"</c> - has no form in it.
/// </summary>
internal static class Strings {

  /// <summary>The text of the bytes of a string constant.</summary>
  /// <param name="bytes">The bytes.</param>
  /// <param name="node">The node, for the diagnostic of a string that is no UTF-8.</param>
  /// <returns>The text.</returns>
  public static string Decode(byte[] bytes, GoNode? node) {
    try {
      return new UTF8Encoding(false, true).GetString(bytes);
    } catch (DecoderFallbackException) {
      throw new GoUnsupportedException(GoFrontend.UnsupportedDescriptor, "'a string that is no valid UTF-8' is not supported by the Go frontend", node);
    }
  }

}
