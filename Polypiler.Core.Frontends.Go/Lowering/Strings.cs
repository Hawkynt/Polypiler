using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Polypiler.Core.Frontends.Go.Ast;

namespace Polypiler.Core.Frontends.Go.Lowering;

/// <summary>
/// The text of a Go string literal (The Go Programming Language Specification, "String literals"):
/// an interpreted literal with its escapes - <c>\x</c> and octal ones are bytes, <c>\u</c> and
/// <c>\U</c> code points - or a raw one between back quotes, whose bytes are the UTF-8 the string
/// holds. A string of the IR is UTF-16, so a Go string that is no valid UTF-8 has no form in it.
/// </summary>
internal static class Strings {

  /// <summary>The text a literal denotes.</summary>
  /// <param name="literal">The literal with its quotes.</param>
  /// <param name="node">The node, for the diagnostic of a string that is no UTF-8.</param>
  /// <returns>The text.</returns>
  public static string Unquote(string literal, GoNode? node = null) {
    var bytes = new List<byte>();
    if (literal.Length >= 2 && literal[0] == '`') {
      bytes.AddRange(Encoding.UTF8.GetBytes(literal[1..^1].Replace("\r", "", StringComparison.Ordinal)));
      return Decode(bytes, node);
    }

    var body = literal.Length >= 2 ? literal[1..^1] : literal;
    for (var index = 0; index < body.Length; ++index) {
      var character = body[index];
      if (character != '\\') {
        if (char.IsHighSurrogate(character) && index + 1 < body.Length) {
          bytes.AddRange(Encoding.UTF8.GetBytes(body.Substring(index, 2)));
          ++index;
        } else {
          bytes.AddRange(Encoding.UTF8.GetBytes(character.ToString()));
        }

        continue;
      }

      var escape = body[++index];
      switch (escape) {
        case 'a': bytes.Add(7); break;
        case 'b': bytes.Add(8); break;
        case 'f': bytes.Add(12); break;
        case 'n': bytes.Add(10); break;
        case 'r': bytes.Add(13); break;
        case 't': bytes.Add(9); break;
        case 'v': bytes.Add(11); break;
        case '\\': bytes.Add((byte)'\\'); break;
        case '\'': bytes.Add((byte)'\''); break;
        case '"': bytes.Add((byte)'"'); break;
        case 'x':
          bytes.Add(byte.Parse(body.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
          index += 2;
          break;
        case 'u' or 'U': {
          var length = escape == 'u' ? 4 : 8;
          var point = int.Parse(body.AsSpan(index + 1, length), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
          bytes.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(point)));
          index += length;
          break;
        }
        default:
          bytes.Add((byte)Convert.ToInt32(body.Substring(index, 3), 8));
          index += 2;
          break;
      }
    }

    return Decode(bytes, node);
  }

  private static string Decode(List<byte> bytes, GoNode? node) {
    try {
      return new UTF8Encoding(false, true).GetString([.. bytes]);
    } catch (DecoderFallbackException) {
      throw new GoUnsupportedException(GoFrontend.UnsupportedDescriptor, "'a string that is no valid UTF-8' is not supported by the Go frontend", node);
    }
  }

}
