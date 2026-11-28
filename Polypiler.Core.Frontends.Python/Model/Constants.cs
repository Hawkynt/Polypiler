using System.Numerics;
using Polypiler.Core.Frontends.Python.Ast;

namespace Polypiler.Core.Frontends.Python.Model;

/// <summary>The values of constants of the tree: integers of any size, and strs exactly as Python holds them.</summary>
internal static class Constants {

  /// <summary>The value of an integer constant, also negated by a unary minus; <see langword="null"/> for anything else.</summary>
  public static BigInteger? Integer(PyNode node) {
    var negated = false;
    while (node.Is("UnaryOp") && node.Operator() is "USub" or "UAdd") {
      negated ^= node.Operator() == "USub";
      node = node.Node("operand")!;
    }

    if (!node.Is("Constant") || node.Value is not BigInteger value)
      return null;

    return negated ? -value : value;
  }

  /// <summary>
  /// The text of a str constant, exactly: a lone surrogate is one UTF-16 code unit of its own, as the
  /// tree keeps it.
  /// </summary>
  public static string Text(PyNode node) => (string)node.Value!;

}
