using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0140: 2, 4 or 8 loads of consecutive bytes <c>b[i + k]</c> combined by shifts and <c>|</c> (or
/// <c>+</c>, <c>^</c>) in little- or big-endian order become one <see cref="IrIntrinsicOp.LoadLittle"/>
/// or <see cref="IrIntrinsicOp.LoadBig"/>.
/// <para>
/// The expression is evaluated as weighted bits (<see cref="WeightEvaluator"/>) whose atoms are the
/// byte loads: each byte must land, all eight bits in order, on its own slot, the slots filling the
/// whole width of the expression - so a 16-bit pair inside a 32-bit sum waits for the whole sum. The
/// array is one variable of bytes, the indices one base plus consecutive constants, the slot of
/// offset <c>k</c> is <c>k</c> (little) or <c>n - 1 - k</c> (big); a gap, a repeated byte or a byte
/// out of order is something else.
/// </para>
/// <para>
/// The combined load traps, before reading anything, exactly when one of the byte loads would: the
/// first index outside the array, or the last. The byte loads have no effect, so nothing happens in
/// between that the earlier trap could skip, and the kind of trap is the same. An index computed in
/// JavaScript's doubles traps with an overflow when it passes the largest <c>i32</c>; such indices
/// are accepted only when the load at the base itself is evaluated first and no offset is negative,
/// so that an index that large has already failed as out of range.
/// </para>
/// <para>
/// A byte read JavaScript's frontend guards (<see cref="Guards"/>) is 0 outside the array, as
/// JavaScript's <c>undefined</c> is under the coercion (#1367), so the bytes inside still count where
/// the others do not: one load is the same only where every byte lies inside. Where a read is guarded
/// the combined load stands behind one test of the whole range, computed in 64 bits -
/// <c>i + lo &gt;= 0 &amp;&amp; i + lo + n &lt;= b.Length ? LoadLittle(b, i + lo) : the byte reads</c> - and the
/// byte reads kept for the rest test their index first, a form this rule does not take again.
/// </para>
/// </summary>
public sealed class O0140EndianLoad : ExpressionRule {

  /// <summary>Creates the rule.</summary>
  public O0140EndianLoad() : base("O0140", "endian load from byte loads") { }

  /// <inheritdoc/>
  protected override IrExpr? Recognize(IrExpr expression, RuleContext context) {
    if (!Images.IsCandidate(expression) || WeightEvaluator.Evaluate(expression, context) is not { } value || value.Image.Constant != 0)
      return null;

    var image = value.Image;
    var atoms = value.Atoms.Select((atom, index) => (Atom: atom, Index: index)).Where(entry => image.Terms.Keys.Any(key => key >> 6 == entry.Index)).ToList();
    var count = atoms.Count;
    if (count is not (2 or 4 or 8) || image.Width != 8 * count || image.Terms.Count != image.Width
      || value.Atoms.Where((atom, index) => atoms.All(entry => entry.Index != index)).Any(atom => !IrShape.IsTrapFree(atom)))
      return null;

    var loads = new List<(IrArrayLoad Load, Offset Index, int Slot)>();
    var guarded = false;
    foreach (var (atom, index) in atoms) {
      var read = atom as IrArrayLoad ?? Guards.Read(atom);
      guarded |= read is not null && read != atom;
      if (read is not { Array: IrLocalRef, Array.Type.Element.Kind: IrTypeKind.U8 } load || Slot(image, index) is not { } slot)
        return null;

      loads.Add((load, Offset.Of(load.Index), slot));
    }

    var first = loads[0];
    if (loads.Any(entry => !IrShape.Same(entry.Load.Array, first.Load.Array) || !IrShape.Same(entry.Index.Base, first.Index.Base))
      || !IrShape.IsTrapFree(first.Index.Base))
      return null;

    var lowest = loads.Min(entry => entry.Index.Amount);
    var little = loads.All(entry => entry.Slot == entry.Index.Amount - lowest);
    var big = loads.All(entry => entry.Slot == count - 1 - (entry.Index.Amount - lowest));
    if (!little && !big || loads.Select(entry => entry.Index.Amount).Distinct().Count() != count)
      return null;

    // An index that can trap on overflow is safe only behind the load at the base, which fails first;
    // behind the test of the range no index traps.
    if (!guarded && loads.Any(entry => entry.Index.Traps) && (lowest != 0 || first.Index.Amount != 0 || first.Index.Traps))
      return null;

    var type = count switch {
      2 => expression.Type.IsSigned ? IrType.I16 : IrType.U16,
      4 => expression.Type.IsSigned ? IrType.I32 : IrType.U32,
      _ => expression.Type.IsSigned ? IrType.I64 : IrType.U64
    };
    var loaded = Images.Fit(Images.Call(little ? IrIntrinsicOp.LoadLittle : IrIntrinsicOp.LoadBig, type, first.Load.Array, Offset.At(first.Index.Base, lowest)), expression.Type);
    if (!guarded)
      return loaded;

    // Every byte inside: base + lowest >= 0 and base + lowest + count <= length, in 64 bits, where neither wraps.
    var start = new IrBinary(IrBinaryOp.Add, new IrConvert(IrType.I64, first.Index.Base), Images.Constant(IrType.I64, lowest));
    var array = first.Load.Array;
    IrExpr length = array.Type.IsView ? Images.Call(IrIntrinsicOp.ViewLength, IrType.I32, array) : new IrArrayLength(array);
    var inside = new IrBinary(IrBinaryOp.LogicalAnd,
      new IrBinary(IrBinaryOp.GreaterOrEqual, start, Images.Constant(IrType.I64, 0)),
      new IrBinary(IrBinaryOp.LessOrEqual, new IrBinary(IrBinaryOp.Add, start, Images.Constant(IrType.I64, count)), new IrConvert(IrType.I64, length)));
    return new IrConditional(inside, loaded, IrShape.Map(expression, Guards.Kept));
  }

  /// <summary>The byte slot of an atom when its eight bits stand there in order, or <see langword="null"/>.</summary>
  private static int? Slot(Weights image, int atom) {
    var position = image.Terms.GetValueOrDefault(atom * 64);
    if (position == 0 || (position & (position - 1)) != 0)
      return null;

    var shift = System.Numerics.BitOperations.TrailingZeroCount(position);
    if (shift % 8 != 0)
      return null;

    for (var bit = 1; bit < 8; ++bit)
      if (image.Terms.GetValueOrDefault(atom * 64 + bit) != position << bit)
        return null;

    return shift / 8;
  }

}
