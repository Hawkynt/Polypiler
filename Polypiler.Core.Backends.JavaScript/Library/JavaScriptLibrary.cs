using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The operations of the IR a JavaScript profile writes as a helper of the library
/// (<see cref="HelperOperations"/>): legalization replaces each by the call of its helper and copies
/// the helper into the module, which then writes it as one of its own functions, through the path
/// every function takes. A row says for which nodes a profile lacks a form of its own and what such a
/// node becomes; every other node of the operation the writer writes itself.
/// </summary>
internal static partial class JavaScriptLibrary {

  /// <summary>A row: which nodes of an operation the library writes, and as what.</summary>
  /// <param name="Applies">Whether the profile writes a node with the library.</param>
  /// <param name="Build">The node, its operands legalized, as the call of its helper or as other operations.</param>
  private sealed record Row(Func<IrIntrinsic, JavaScriptLibraryContext, bool> Applies, Func<IrIntrinsic, LibraryCalls, IrExpr?> Build);

  private static readonly Dictionary<IrIntrinsicOp, Row> _rows = Register(IntegerRows);

  private static Dictionary<IrIntrinsicOp, Row> Register(params IEnumerable<(IrIntrinsicOp Op, Row Row)>[] families)
    => families.SelectMany(family => family).ToDictionary(entry => entry.Op, entry => entry.Row);

  /// <summary>Whether a profile writes a node with a helper of the library, which legalization puts in.</summary>
  /// <param name="intrinsic">The node.</param>
  /// <param name="native">Whether the profile has a capability of the table.</param>
  public static bool Covers(IrIntrinsic intrinsic, Func<string, bool> native)
    => intrinsic.Arguments.Count > 0 && !intrinsic.Arguments[0].Type.IsFloat && _rows.TryGetValue(intrinsic.Op, out var row) && row.Applies(intrinsic, new(native));

  /// <summary>The rewrite of legalization for a profile: every node a row covers becomes the call of its helper.</summary>
  /// <param name="native">Whether the profile has a capability of the table.</param>
  /// <returns>The rewrite.</returns>
  public static LibraryRewrite Rewrite(Func<string, bool> native)
    => (expression, calls) => expression is IrIntrinsic intrinsic && Covers(intrinsic, native) ? _rows[intrinsic.Op].Build(intrinsic, calls) : null;

}

/// <summary>What a row of <see cref="JavaScriptLibrary"/> asks of the profile.</summary>
/// <param name="Native">Whether the profile has a capability of the table.</param>
internal sealed record JavaScriptLibraryContext(Func<string, bool> Native) {

  /// <summary>Whether 64-bit integers are <c>BigInt</c>s rather than pairs of halves.</summary>
  public bool BigInt => this.Native("BigInt");

}
