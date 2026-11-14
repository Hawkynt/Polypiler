using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize.Rules;

/// <summary>
/// O0141: 2, 4 or 8 consecutive statements that store the bytes <c>(byte)(v &gt;&gt; 8k)</c> of one
/// value into consecutive elements <c>b[i + k]</c>, in either order, become one
/// <see cref="IrIntrinsicOp.StoreLittle"/> or <see cref="IrIntrinsicOp.StoreBig"/>.
/// <para>
/// Each stored value is evaluated as weighted bits (<see cref="WeightEvaluator"/>) and must be
/// exactly byte <c>m</c> of one value <c>v</c>; the element at offset <c>k</c> receives byte
/// <c>k</c> (little) or <c>n - 1 - k</c> (big). Array, base index and value have no effect and cannot
/// trap; an index that can trap on its own is not accepted.
/// </para>
/// <para>
/// The byte stores write the elements that lie inside the array before the first one outside traps;
/// the combined store traps before writing anything. Those partial writes are visible to whoever
/// catches the trap, so the combined store runs only where every element lies inside the array -
/// <c>if (i &gt;= 0 &amp;&amp; i &lt;= b.Length - n) StoreLittle(b, i, v); else { the byte stores }</c> - and
/// the original stores keep the trap and the partial writes everywhere else.
/// </para>
/// </summary>
public sealed class O0141EndianStore : IrRule {

  /// <summary>Creates the rule.</summary>
  public O0141EndianStore() : base("O0141", "endian store from byte stores") { }

  /// <inheritdoc/>
  public override bool TryApply(IReadOnlyList<IrStmt> statements, int index, RuleContext context, [NotNullWhen(true)] out StatementRewrite? rewrite) {
    rewrite = null;

    // The byte stores this rule keeps for the elements outside the array are not combined again.
    if (context.Frames.Count > 0 && context.Frames[^1].Owner is IrIf { Then.Statements: [IrExprStmt { Expression: IrIntrinsic { Op: IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig } }] })
      return false;

    foreach (var count in new[] { 8, 4, 2 })
      if (index + count <= statements.Count && Combined(statements.Skip(index).Take(count).ToList(), context) is { } combined) {
        rewrite = new(count, [combined]);
        return true;
      }

    return false;
  }

  private static IrStmt? Combined(IReadOnlyList<IrStmt> stores, RuleContext context) {
    var count = stores.Count;
    var parts = new List<(Offset Index, IrExpr Source, int Byte)>();
    IrExpr? array = null;
    foreach (var statement in stores) {
      if (statement is not IrStore { Array: IrLocalRef { Type.Element.Kind: IrTypeKind.U8 } target } store || (array is not null && !IrShape.Same(array, target))
        || WeightEvaluator.Evaluate(store.Value, context) is not { Single: { } atom } value || Byte(value.Image) is not { } part)
        return null;

      array = target;
      parts.Add((Offset.Of(store.Index), atom, part));
    }

    var first = parts[0];
    var source = first.Source;
    if (parts.Any(part => part.Index.Traps || !IrShape.Same(part.Index.Base, first.Index.Base) || !IrShape.Same(part.Source, source))
      || !IrShape.IsTrapFree(first.Index.Base) || !IrShape.IsTrapFree(source) || source.Type.Bits < 8 * count
      || parts.Select(part => part.Index.Amount).Distinct().Count() != count)
      return null;

    var lowest = parts.Min(part => part.Index.Amount);
    var little = parts.All(part => part.Byte == part.Index.Amount - lowest);
    var big = parts.All(part => part.Byte == count - 1 - (part.Index.Amount - lowest));
    if (!little && !big)
      return null;

    var type = count switch {
      2 => source.Type.IsSigned && source.Type.Bits == 16 ? IrType.I16 : IrType.U16,
      4 => source.Type.IsSigned && source.Type.Bits == 32 ? IrType.I32 : IrType.U32,
      _ => source.Type.IsSigned ? IrType.I64 : IrType.U64
    };
    var start = Offset.At(first.Index.Base, lowest);
    var inside = new IrBinary(IrBinaryOp.LogicalAnd,
      new IrBinary(IrBinaryOp.GreaterOrEqual, start, Images.Constant(IrType.I32, 0)),
      new IrBinary(IrBinaryOp.LessOrEqual, start, new IrBinary(IrBinaryOp.Subtract, new IrArrayLength(array!), Images.Constant(IrType.I32, count))));
    var combined = Images.Call(little ? IrIntrinsicOp.StoreLittle : IrIntrinsicOp.StoreBig, IrType.Void, array!, start, Images.Fit(source, type));
    return new IrIf(inside, new IrBlock([new IrExprStmt(combined)]), new IrBlock(stores));
  }

  /// <summary>Which byte of its atom a value is - bits <c>8m</c> to <c>8m + 7</c> in order - or <see langword="null"/>.</summary>
  private static int? Byte(Weights image) {
    if (image.Width != 8 || image.Constant != 0 || image.Terms.Count != 8)
      return null;

    var low = image.Terms.Keys.Min();
    if (low % 8 != 0)
      return null;

    for (var bit = 0; bit < 8; ++bit)
      if (image.Terms.GetValueOrDefault(low + bit) != 1UL << bit)
        return null;

    return (low & 63) / 8;
  }

}
