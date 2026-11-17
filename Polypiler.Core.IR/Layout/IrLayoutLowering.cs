using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// Resolves the layout of a module for one target (#110): every <see cref="IrLayoutOf"/> becomes its value under
/// the target's <see cref="LayoutProfile"/>, and every <see cref="IrFieldAddress"/> the pointer arithmetic it
/// stands for - the base moved by the field's offset (<see cref="IrIntrinsicOp.PointerAdd"/> on the <c>u8*</c>)
/// and cast to the field's pointer type (<see cref="IrIntrinsicOp.PointerCast"/>). An ordered load or store of a
/// number through a pointer to bytes (#113) becomes byte loads or stores, shifts and ors - the same in every host
/// order - where its address, and for a store its value, may be evaluated more than once: a local, a constant, or
/// pointer arithmetic on them. A store first reads its last byte, so that it traps before it writes any byte. What
/// is left are nodes every backend that has pointers writes already, so a backend needs nothing of its own for
/// records in memory; an ordered access of a <c>bigint</c>, or one whose address is computed otherwise, stays.
/// An unchecked <see cref="IrElementAddress"/> (#111) becomes the base moved by the linear position of its indices,
/// computed in <c>i64</c> and converted to <c>i32</c> with a trap where it does not fit; a checked one stays, as
/// its per-dimension bounds checks have no form among the pointer operations.
/// </summary>
public static class IrLayoutLowering {

  /// <summary>The module with its layouts resolved for a profile.</summary>
  /// <param name="module">The module, which the verifier accepted.</param>
  /// <param name="profile">The profile of the target.</param>
  /// <returns>A module without layout queries and field addresses; the module itself when it has none.</returns>
  /// <exception cref="ArgumentException">A record laid out has a problem under the profile.</exception>
  public static IrModule Lower(IrModule module, LayoutProfile profile) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(profile);
    var rewriter = new Rewriter(profile);
    var functions = module.Functions.Select(function => function with { Body = rewriter.Block(function.Body) }).ToList();
    return rewriter.Changed ? module with { Functions = functions } : module;
  }

  /// <summary>A statement with its expressions and blocks mapped.</summary>
  /// <param name="statement">The statement.</param>
  /// <param name="expression">What each expression the statement evaluates itself becomes.</param>
  /// <param name="block">What each block it holds becomes.</param>
  /// <returns>The statement rebuilt.</returns>
  public static IrStmt Map(IrStmt statement, Func<IrExpr, IrExpr> expression, Func<IrBlock, IrBlock> block) => statement switch {
    IrBlock inner => block(inner),
    IrDeclare declare => declare with { Initializer = declare.Initializer is null ? null : expression(declare.Initializer) },
    IrAssign assign => assign with { Value = expression(assign.Value) },
    IrGlobalAssign assign => assign with { Value = expression(assign.Value) },
    IrStore store => new IrStore(expression(store.Array), expression(store.Index), expression(store.Value)),
    IrFieldStore store => store with { Target = expression(store.Target), Value = expression(store.Value) },
    IrExprStmt evaluated => new IrExprStmt(expression(evaluated.Expression)),
    IrIf conditional => new IrIf(expression(conditional.Condition), block(conditional.Then), conditional.Else is null ? null : block(conditional.Else)),
    IrWhile loop => new IrWhile(expression(loop.Condition), block(loop.Body)),
    IrDoWhile loop => new IrDoWhile(block(loop.Body), expression(loop.Condition)),
    IrFor loop => new IrFor(expression(loop.Condition), block(loop.Body), block(loop.Step)) { PerIteration = loop.PerIteration },
    IrSwitch choice => new IrSwitch(expression(choice.Value), [.. choice.Sections.Select(section => section with { Body = block(section.Body) })]),
    IrReturn result => new IrReturn(result.Value is null ? null : expression(result.Value)),
    IrThrow thrown => new IrThrow(expression(thrown.Exception)),
    IrTry attempt => new IrTry(block(attempt.Body),
      [.. attempt.Catches.Select(clause => clause with { Filter = clause.Filter is null ? null : expression(clause.Filter), Body = block(clause.Body) })],
      attempt.Finally is null ? null : block(attempt.Finally)),
    IrUsing scope => scope with { Initializer = expression(scope.Initializer), Body = block(scope.Body) },
    IrForEachCollection loop => loop with { Collection = expression(loop.Collection), Body = block(loop.Body) },
    IrYield produced => new IrYield(expression(produced.Value)),
    IrYieldFrom delegated => new IrYieldFrom(expression(delegated.Sequence)),
    IrForEachSequence loop => loop with { Sequence = expression(loop.Sequence), Body = block(loop.Body) },
    _ => statement
  };

  private sealed class Rewriter(LayoutProfile profile) {

    public bool Changed { get; private set; }

    public IrBlock Block(IrBlock block) => new([.. block.Statements.Select(this.Statement)]);

    private IrStmt Statement(IrStmt statement) {
      var mapped = Map(statement, this.Expression, this.Block);
      if (mapped is not IrExprStmt { Expression: IrIntrinsic { Arguments: [var address, var value] } store } || !IrOrderedAccess.IsStore(store.Op)
        || !IrOrderedAccess.IsOrderedType(value.Type) || !Repeatable(address) || !Repeatable(value))
        return mapped;

      this.Changed = true;
      return new IrBlock([new IrExprStmt(ByteLoad(address, value.Type.Size - 1)), .. Bytes(store.Op, address, value)]);
    }

    private IrExpr Expression(IrExpr expression) {
      if (expression is IrLambda lambda)
        return lambda with { Body = this.Block(lambda.Body) };

      var operands = IrTree.Operands(expression);
      var lowered = operands.Select(this.Expression).ToList();
      if (!operands.Zip(lowered).All(pair => ReferenceEquals(pair.First, pair.Second)))
        expression = IrTree.With(expression, lowered);

      switch (expression) {
        case IrLayoutOf query:
          this.Changed = true;
          return new IrConst(IrType.I64, RecordLayout.Answer(query.Query, query.Of, query.Field, profile));
        case IrFieldAddress address:
          this.Changed = true;
          var offset = RecordLayout.OffsetOf(address.Record, address.Field, profile);
          var moved = offset == 0 ? address.Base : new IrIntrinsic(IrIntrinsicOp.PointerAdd, address.Base.Type, [address.Base, new IrConst(IrType.I32, offset)]);
          return address.Type == moved.Type ? moved : new IrIntrinsic(IrIntrinsicOp.PointerCast, address.Type, [moved]);
        case IrElementAddress { Checked: false } element:
          this.Changed = true;
          return new IrIntrinsic(IrIntrinsicOp.PointerAdd, element.Type, [element.Base, Position(element)]);
        case IrIntrinsic { Arguments: [var address] } load when IrOrderedAccess.IsOrdered(load.Op) && Repeatable(address):
          this.Changed = true;
          return Composed(load, address);
        default:
          return expression;
      }
    }

  }

  /// <summary>The linear position of the element an address names, as an <c>i32</c>: <c>Σ (i_d − lower_d) · stride_d</c>, folded where the indices are constants.</summary>
  private static IrExpr Position(IrElementAddress element) {
    var shape = element.Shape;
    if (element.Indices.All(index => index is IrConst)) {
      var linear = shape.Unchecked([.. element.Indices.Select(index => ((IrConst)index).Value)]);
      return linear is >= int.MinValue and <= int.MaxValue ? new IrConst(IrType.I32, linear) : new IrConvert(IrType.I32, new IrConst(IrType.I64, linear), IrConversion.Trap);
    }

    var strides = shape.Strides;
    IrExpr? sum = null;
    for (var dimension = 0; dimension < shape.Rank; ++dimension) {
      var index = element.Indices[dimension];
      IrExpr term = index.Type == IrType.I64 ? index : new IrConvert(IrType.I64, index, IrConversion.Wrap);
      if (shape.Dimensions[dimension].Lower != 0)
        term = new IrBinary(IrBinaryOp.Subtract, term, new IrConst(IrType.I64, shape.Dimensions[dimension].Lower));
      if (strides[dimension] != 1)
        term = new IrBinary(IrBinaryOp.Multiply, term, new IrConst(IrType.I64, strides[dimension]));

      sum = sum is null ? term : new IrBinary(IrBinaryOp.Add, sum, term);
    }

    return new IrConvert(IrType.I32, sum!, IrConversion.Trap);
  }

  /// <summary>Whether an expression may be evaluated more than once to the same effect: a local, a constant, or pointer arithmetic on them.</summary>
  private static bool Repeatable(IrExpr expression) => expression switch {
    IrLocalRef or IrConst => true,
    IrIntrinsic { Op: IrIntrinsicOp.PointerAdd or IrIntrinsicOp.PointerCast } arithmetic => arithmetic.Arguments.All(Repeatable),
    _ => false
  };

  /// <summary>The byte at an index from a pointer to bytes.</summary>
  private static IrExpr ByteLoad(IrExpr address, int index)
    => new IrIntrinsic(IrIntrinsicOp.PointerLoad, IrType.U8, [index == 0 ? address : new IrIntrinsic(IrIntrinsicOp.PointerAdd, address.Type, [address, new IrConst(IrType.I32, index)])]);

  /// <summary>The unsigned integer of 32 or 64 bits the bytes of a type are assembled in.</summary>
  private static IrType Word(IrType type) => type.Size == 8 ? IrType.U64 : IrType.U32;

  /// <summary>The significance of the byte at an index of a number of a size in an order: 0 for the lowest.</summary>
  private static int Significance(IrIntrinsicOp op, int index, int size) => IrOrderedAccess.OrderOf(op) == IrByteOrder.Little ? index : size - 1 - index;

  /// <summary>An ordered load as the or of its bytes, each shifted to its significance, converted to the loaded type.</summary>
  private static IrExpr Composed(IrIntrinsic load, IrExpr address) {
    var type = load.Type;
    var word = Word(type);
    IrExpr? pattern = null;
    for (var index = 0; index < type.Size; ++index) {
      IrExpr part = new IrConvert(word, ByteLoad(address, index), IrConversion.Wrap);
      var shift = 8 * Significance(load.Op, index, type.Size);
      if (shift > 0)
        part = new IrBinary(IrBinaryOp.ShiftLeft, part, new IrConst(IrType.I32, shift));

      pattern = pattern is null ? part : new IrBinary(IrBinaryOp.Or, pattern, part);
    }

    if (!type.IsFloat)
      return type == word ? pattern! : new IrConvert(type, pattern!, IrConversion.Wrap);

    var bits = word == IrType.U64 ? IrType.I64 : IrType.I32;
    return new IrIntrinsic(IrIntrinsicOp.BitsFloat, type, [new IrConvert(bits, pattern!, IrConversion.Wrap)]);
  }

  /// <summary>An ordered store as one byte store per byte, each the value shifted down from its significance.</summary>
  private static IEnumerable<IrStmt> Bytes(IrIntrinsicOp op, IrExpr address, IrExpr value) {
    var type = value.Type;
    var word = Word(type);
    IrExpr pattern = type.IsFloat
      ? new IrConvert(word, new IrIntrinsic(IrIntrinsicOp.FloatBits, word == IrType.U64 ? IrType.I64 : IrType.I32, [value]), IrConversion.Wrap)
      : type == word ? value : new IrConvert(word, value, IrConversion.Wrap);
    for (var index = 0; index < type.Size; ++index) {
      var shift = 8 * Significance(op, index, type.Size);
      var part = shift == 0 ? pattern : new IrBinary(IrBinaryOp.ShiftRightLogical, pattern, new IrConst(IrType.I32, shift));
      var target = index == 0 ? address : new IrIntrinsic(IrIntrinsicOp.PointerAdd, address.Type, [address, new IrConst(IrType.I32, index)]);
      yield return new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.PointerStore, IrType.Void, [target, new IrConvert(IrType.U8, part, IrConversion.Wrap)]));
    }
  }

}
