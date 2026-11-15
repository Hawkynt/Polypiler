using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// The functions the lowering of sequence operations and of JavaScript's array callbacks adds to a
/// module, written in the IR itself: a stable sort of indices by a comparison, and the check of the
/// arguments of a range. Each is added once, under its name, by whatever needs it first.
/// </summary>
public static class SequenceLibrary {

  /// <summary>The name of <see cref="StableSort"/> in a module.</summary>
  public const string StableSortName = "$stableSort";

  /// <summary>The name of <see cref="RangeCount"/> in a module.</summary>
  public const string RangeCountName = "$rangeCount";

  private static IrExpr I32(long value) => new IrConst(IrType.I32, value);

  private static IrExpr Ref(IrLocal local) => new IrLocalRef(local);

  private static IrStmt Set(IrLocal local, IrExpr value) => new IrAssign(local, value);

  private static IrExpr Add(IrExpr left, IrExpr right) => new IrBinary(IrBinaryOp.Add, left, right);

  private static IrExpr Less(IrExpr left, IrExpr right) => new IrBinary(IrBinaryOp.Less, left, right);

  private static IrBlock Block(params IrStmt[] statements) => new(statements);

  /// <summary>
  /// <c>$stableSort(i32[] items, fn(i32, i32) -&gt; i32 compare)</c>: sorts the array in place so that
  /// <c>compare</c> of neighbours is never positive, and keeps the order of the elements it finds
  /// equal. It is the bottom-up merge sort of Sedgewick and Wayne, <i>Algorithms</i>, 4th ed., §2.2,
  /// which merges runs of 1, 2, 4, … elements through a buffer and takes the element of the left run on
  /// a tie - the published algorithm, written as code of this repository; nothing of the book was copied. Which pairs it compares, and in which order,
  /// is its own: the callers sort indices by keys computed before, or by a comparator that has no effect.
  /// </summary>
  /// <returns>The function.</returns>
  public static IrFunction StableSort() {
    var items = new IrLocal("items", IrType.Array(IrType.I32));
    var compare = new IrLocal("compare", IrType.Function(IrType.I32, IrType.I32, IrType.I32));
    var count = new IrLocal("n", IrType.I32);
    var buffer = new IrLocal("buffer", IrType.Array(IrType.I32));
    var width = new IrLocal("width", IrType.I32);
    var low = new IrLocal("low", IrType.I32);
    var middle = new IrLocal("middle", IrType.I32);
    var high = new IrLocal("high", IrType.I32);
    var left = new IrLocal("left", IrType.I32);
    var right = new IrLocal("right", IrType.I32);
    var index = new IrLocal("i", IrType.I32);
    IrExpr Buffered(IrLocal at) => new IrArrayLoad(Ref(buffer), Ref(at));
    IrBlock Take(IrLocal from) => Block(new IrStore(Ref(items), Ref(index), Buffered(from)), Set(from, Add(Ref(from), I32(1))));

    var merge = Block(
      Set(middle, Add(Ref(low), Ref(width))),
      // high = min(low + 2 * width, n), computed without leaving the i32 range.
      new IrIf(new IrBinary(IrBinaryOp.Greater, new IrBinary(IrBinaryOp.Subtract, Ref(count), Ref(middle)), Ref(width)),
        Block(Set(high, Add(Ref(middle), Ref(width)))), Block(Set(high, Ref(count)))),
      Set(index, Ref(low)),
      new IrWhile(Less(Ref(index), Ref(high)), Block(
        new IrStore(Ref(buffer), Ref(index), new IrArrayLoad(Ref(items), Ref(index))),
        Set(index, Add(Ref(index), I32(1))))),
      Set(left, Ref(low)),
      Set(right, Ref(middle)),
      Set(index, Ref(low)),
      new IrWhile(Less(Ref(index), Ref(high)), Block(
        new IrIf(new IrBinary(IrBinaryOp.GreaterOrEqual, Ref(left), Ref(middle)), Take(right),
          Block(new IrIf(new IrBinary(IrBinaryOp.GreaterOrEqual, Ref(right), Ref(high)), Take(left),
            Block(new IrIf(Less(new IrInvoke(IrType.I32, Ref(compare), [Buffered(right), Buffered(left)]), I32(0)), Take(right), Take(left)))))),
        Set(index, Add(Ref(index), I32(1))))),
      Set(low, Ref(high)));
    var body = Block(
      new IrDeclare(count, new IrArrayLength(Ref(items))),
      new IrDeclare(buffer, new IrNewArray(IrType.Array(IrType.I32), Ref(count))),
      new IrDeclare(width, I32(1)),
      new IrDeclare(low, I32(0)),
      new IrDeclare(middle, I32(0)),
      new IrDeclare(high, I32(0)),
      new IrDeclare(left, I32(0)),
      new IrDeclare(right, I32(0)),
      new IrDeclare(index, I32(0)),
      new IrWhile(Less(Ref(width), Ref(count)), Block(
        Set(low, I32(0)),
        new IrWhile(Less(Ref(low), new IrBinary(IrBinaryOp.Subtract, Ref(count), Ref(width))), merge),
        // The next width is twice this one; once that reaches n the array is one run.
        new IrIf(new IrBinary(IrBinaryOp.GreaterOrEqual, Ref(width), new IrBinary(IrBinaryOp.Subtract, Ref(count), Ref(width))), Block(new IrBreak()), null),
        Set(width, Add(Ref(width), Ref(width))))));
    return new IrFunction(StableSortName, false, IrType.Void, [items, compare], [count, buffer, width, low, middle, high, left, right, index], body);
  }

  /// <summary>
  /// <c>$rangeCount(i32 start, i32 count) -&gt; i32</c>: the count of a range, after the check .NET's
  /// <c>Enumerable.Range</c> makes when it is called - a negative count, or a last number beyond the
  /// largest <c>i32</c>, throws an <c>ArgumentOutOfRangeException</c>.
  /// </summary>
  /// <returns>The function.</returns>
  public static IrFunction RangeCount() {
    var start = new IrLocal("start", IrType.I32);
    var count = new IrLocal("count", IrType.I32);
    static IrExpr Wide(IrExpr local) => new IrConvert(IrType.I64, local);
    var last = new IrBinary(IrBinaryOp.Subtract, new IrBinary(IrBinaryOp.Add, Wide(Ref(start)), Wide(Ref(count))), new IrConst(IrType.I64, 1));
    var invalid = new IrBinary(IrBinaryOp.LogicalOr, Less(Ref(count), I32(0)), new IrBinary(IrBinaryOp.Greater, last, new IrConst(IrType.I64, int.MaxValue)));
    var thrown = new IrThrow(new IrNew(IrType.Object(IrExceptionClass.ArgumentOutOfRangeException), IrExceptionClass.Constructor(IrExceptionClass.ArgumentOutOfRangeException, 0), []));
    return new IrFunction(RangeCountName, false, IrType.I32, [start, count], [], Block(new IrIf(invalid, Block(thrown), null), new IrReturn(Ref(count))));
  }

  /// <summary>Adds the functions a module lacks, by name, to the functions it has.</summary>
  /// <param name="functions">The functions of the module, to which the missing ones are appended.</param>
  /// <param name="names">The names of the library functions that are needed.</param>
  public static void Require(List<IrFunction> functions, IEnumerable<string> names) {
    foreach (var name in names)
      if (!functions.Exists(function => function.Name == name))
        functions.Add(name == StableSortName ? StableSort() : RangeCount());
  }

}
