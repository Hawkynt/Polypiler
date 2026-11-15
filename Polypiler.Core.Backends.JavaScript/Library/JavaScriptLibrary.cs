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

  private static readonly ILookup<IrIntrinsicOp, Row> _rows = Register(IntegerRows, FloatRows);

  private static ILookup<IrIntrinsicOp, Row> Register(params IEnumerable<(IrIntrinsicOp Op, Row Row)>[] families)
    => families.SelectMany(family => family).ToLookup(entry => entry.Op, entry => entry.Row);

  /// <summary>Whether a profile writes a node with a helper of the library, which legalization puts in.</summary>
  /// <param name="intrinsic">The node.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="table">The capability table.</param>
  public static bool Covers(IrIntrinsic intrinsic, JavaScriptProfile profile, JavaScriptCapabilities table) => RowOf(intrinsic, Context(profile, table)) is not null;

  /// <summary>
  /// The rewrite of legalization for a profile: every node a row covers becomes the call of its
  /// helper, and below ECMAScript 2020, which has no <c>BigInt</c>, every operation on a 64-bit
  /// integer the call of its helper on the two limbs (<see cref="WideIntegerLibrary"/>, O0075).
  /// </summary>
  /// <param name="profile">The profile.</param>
  /// <param name="table">The capability table.</param>
  /// <returns>The rewrite.</returns>
  public static LibraryRewrite Rewrite(JavaScriptProfile profile, JavaScriptCapabilities table) {
    var context = Context(profile, table);
    var limbs = context.Replaces("BigInt");
    return (expression, calls) => expression is IrIntrinsic intrinsic && RowOf(intrinsic, context) is { } row ? row.Build(intrinsic, calls)
      : limbs ? WideIntegerLibrary.Call(expression, calls)
      : null;
  }

  private static JavaScriptLibraryContext Context(JavaScriptProfile profile, JavaScriptCapabilities table)
    => new(name => table.Has(name, profile), name => table[name].Replacement is not null);

  /// <summary>The first row of a node's operation that applies to it in a profile, or <see langword="null"/>.</summary>
  private static Row? RowOf(IrIntrinsic intrinsic, JavaScriptLibraryContext context)
    => intrinsic.Arguments.Count == 0 ? null : _rows[intrinsic.Op].FirstOrDefault(row => row.Applies(intrinsic, context));

}

/// <summary>What a row of <see cref="JavaScriptLibrary"/> asks of the profile.</summary>
/// <param name="Native">Whether the profile has a capability of the table.</param>
/// <param name="Replaceable">Whether the table names a replacement for a capability, which a level that lacks it takes.</param>
internal sealed record JavaScriptLibraryContext(Func<string, bool> Native, Func<string, bool> Replaceable) {

  /// <summary>Whether 64-bit integers are <c>BigInt</c>s rather than pairs of halves.</summary>
  public bool BigInt => this.Native("BigInt");

  /// <summary>Whether the profile lacks a function of the language and the table lets the library's helper stand in for it.</summary>
  /// <param name="name">The capability.</param>
  public bool Replaces(string name) => !this.Native(name) && this.Replaceable(name);

}
