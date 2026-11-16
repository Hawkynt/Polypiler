using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize;

/// <summary>
/// The range tests the JavaScript frontend puts around an element access whose index the program does
/// not prove inside the array (#1367): a read <c>(uint)a.Length &gt; (uint)i ? a[i] : 0</c>, at a double
/// index also <c>(double)(int)x == x</c>, and a store <c>if ((uint)a.Length &gt; (uint)i) a[i] = v;</c>.
/// The array is compared first; the index may be bound to a temporary in the test and read from it in
/// the access. The rules see through them where every access of the idiom carries one, and combine the
/// accesses behind one test of the whole range.
/// </summary>
internal static class Guards {

  /// <summary>
  /// The element read of a guarded read that yields 0 outside, with its index as the frontend computes
  /// it without the guard - an <c>i32</c>, or a double converted with a trap - or <see langword="null"/>
  /// for anything else.
  /// </summary>
  public static IrArrayLoad? Read(IrExpr atom) {
    if (atom is not IrConditional { WhenTrue: IrArrayLoad load, WhenFalse: IrConst { Value: 0 } } choice)
      return null;

    return Index(choice.Condition, load.Array, load.Index) is { } index ? new IrArrayLoad(load.Array, index) : null;
  }

  /// <summary>
  /// The store of a guarded store, with its index as the frontend computes it without the guard, or
  /// <see langword="null"/> for any other statement.
  /// </summary>
  public static IrStore? Store(IrStmt statement) {
    if (statement is not IrIf { Else: null, Then.Statements: [IrStore store] } guarded)
      return null;

    return Index(guarded.Condition, store.Array, store.Index) is { } index ? store with { Index = index } : null;
  }

  /// <summary>
  /// A guarded read with its test written index first, <c>(uint)i &lt; (uint)a.Length</c>: the same value,
  /// as the array is a variable, in a form <see cref="Read"/> does not take, so that the reads a rule
  /// keeps for the indices outside are not combined again.
  /// </summary>
  public static IrExpr Kept(IrExpr atom) => atom is IrConditional { Condition: var condition } choice && Read(atom) is not null
    ? choice with { Condition = Flipped(condition) }
    : atom;

  private static IrExpr Flipped(IrExpr condition) => condition switch {
    IrBinary { Op: IrBinaryOp.Greater } inside => new IrBinary(IrBinaryOp.Less, inside.Right, inside.Left),
    IrBinary { Op: IrBinaryOp.LogicalAnd } both => both with { Left = Flipped(both.Left) },
    _ => condition
  };

  /// <summary>The index a test guards an access of an array at, unbound, or <see langword="null"/> where the test is no such guard.</summary>
  private static IrExpr? Index(IrExpr condition, IrExpr array, IrExpr accessed) {
    // A double index: the element where the double is the integer of one.
    if (condition is IrBinary { Op: IrBinaryOp.LogicalAnd, Left: var bounds, Right: IrBinary { Op: IrBinaryOp.Equal, Left: IrConvert { Type.Kind: IrTypeKind.F64, Operand: var whole }, Right: var held } }
        && accessed is IrConvert { Contract: IrConversion.Saturate, Type.Kind: IrTypeKind.I32, Operand: var read } && IrShape.Same(read, held) && IrShape.Same(whole, accessed)
        && Compared(bounds, array) is IrConvert { Contract: IrConversion.Saturate, Type.Kind: IrTypeKind.I32, Operand: var tested } && Unbound(tested, held) is { } number)
      return new IrConvert(IrType.I32, number, IrConversion.Trap);

    return Compared(condition, array) is { } index && Unbound(index, accessed) is { } unbound ? unbound : null;
  }

  /// <summary>The index of <c>(uint)a.Length &gt; (uint)i</c> for the array, with its conversion to <c>u32</c> taken off.</summary>
  private static IrExpr? Compared(IrExpr condition, IrExpr array) {
    if (condition is not IrBinary { Op: IrBinaryOp.Greater, Left: IrConvert { Type.Kind: IrTypeKind.U32, Operand: var length }, Right: var index })
      return null;

    var measured = length switch {
      IrArrayLength { Array: var of } => of,
      IrIntrinsic { Op: IrIntrinsicOp.ViewLength, Arguments: [var of] } => of,
      _ => null
    };
    if (measured is null || !IrShape.Same(measured, array))
      return null;

    return index is IrConvert { Type.Kind: IrTypeKind.U32, Operand: { Type.Kind: not IrTypeKind.U32 } operand } ? operand : index;
  }

  /// <summary>The value an index stands for: the value a temporary of the test binds where the access reads that temporary, or the index itself where both are the same.</summary>
  private static IrExpr? Unbound(IrExpr tested, IrExpr accessed) => tested switch {
    IrBind { Local: var local, Value: var value } when accessed is IrLocalRef { Local: var read } && read == local => value,
    _ when IrShape.Same(tested, accessed) => tested,
    _ => null
  };

}
