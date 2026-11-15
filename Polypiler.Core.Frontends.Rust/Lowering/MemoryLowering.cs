using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>
/// Arrays, slices and <c>Vec</c>: literals, range indices, the methods of the subset and the byte
/// conversions of the numbers (The Rust Reference, "Array and array index expressions"; the
/// <c>core::slice</c> and <c>alloc::vec::Vec</c> documentation). Every index and range is checked
/// and panics outside, in both overflow modes; a range may start at the length, never past its end.
/// </summary>
internal sealed partial class FunctionLowering {

  // ---------------------------------------------------------------- literals

  private IrExpr Array(ArrayExpr array) {
    var type = this.Ir(this._typed.TypeOf(array));
    var values = this.Operands([.. array.Elements]);
    return new IrIntrinsic(IrIntrinsicOp.ArrayOf, type, values);
  }

  private IrExpr ArrayRepeat(ArrayRepeatExpr array) {
    var type = this._typed.TypeOf(array);
    var ir = this.Ir(type);
    var value = this.Value(array.Value);
    var length = new IrConst(IrType.I32, type.Length);
    return this.Filled(ir, length, value);
  }

  /// <summary>A new array of a length with every element a value.</summary>
  private IrExpr Filled(IrType type, IrExpr length, IrExpr value) {
    if (value is IrConst { Value: 0 } && type.Element!.IsNumeric)
      return new IrNewArray(type, length);

    value = this.Stable(value, "element");
    var array = this.Temporary(new IrNewArray(type, length), "array");
    if (type.Element!.IsNumeric) {
      this.Emit(new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayFill, IrType.Void, [array, value])));
    } else {
      var index = this.Counter("i");
      this.Emit(new IrFor(Binary(IrBinaryOp.Less, new IrLocalRef(index), new IrArrayLength(array)),
        new IrBlock([new IrStore(array, new IrLocalRef(index), value)]), new IrBlock([Next(index)])));
    }

    return array;
  }

  /// <summary>A counter of type <c>i32</c> starting at zero.</summary>
  private IrLocal Counter(string name) {
    var local = this.NewLocal(name, IrType.I32);
    this.Emit(new IrDeclare(local, new IrConst(IrType.I32, 0)));
    return local;
  }

  private static IrAssign Next(IrLocal counter) => new(counter, Binary(IrBinaryOp.Add, new IrLocalRef(counter), new IrConst(IrType.I32, 1)));

  // ---------------------------------------------------------------- lengths and views

  /// <summary>The number of elements of an array, a view or a list, as the <c>i32</c> the IR counts in.</summary>
  private static IrExpr Count(IrExpr container) => container.Type.IsList
    ? new IrCollection(IrCollectionOp.ListCount, IrType.I32, [container])
    : container.Type.IsView ? new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [container])
    : new IrArrayLength(container);

  /// <summary>The number of elements as a <c>usize</c>.</summary>
  private static IrExpr Length(IrExpr container) => Cast(Count(container), IrType.U64);

  /// <summary>An element of an array, a view or a list at an <c>i32</c> index, checked.</summary>
  private static IrExpr Load(IrExpr container, IrExpr index) => container.Type.IsList
    ? new IrCollection(IrCollectionOp.ListGet, container.Type.Element!, [container, index])
    : new IrArrayLoad(container, index);

  /// <summary>A write of an element, checked.</summary>
  private static IrStmt Store(IrExpr container, IrExpr index, IrExpr value) => container.Type.IsList
    ? new IrExprStmt(new IrCollection(IrCollectionOp.ListSet, IrType.Void, [container, index, value]))
    : new IrStore(container, index, value);

  /// <summary>A view of all of a container: an array or a view as a view, writable where it may be written; a list as a read-only view of a copy.</summary>
  private IrExpr View(IrExpr container, bool writable) {
    if (container is IrConstRef table)
      container = this.Duplicate(table, RType.Array(RType.U8, 0));

    if (container.Type.IsView)
      return writable || container.Type.Kind == IrTypeKind.ReadOnlySpan ? container : container;

    if (container.Type.IsList)
      return new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.ReadOnlySpan(container.Type.Element!), [new IrCollection(IrCollectionOp.ListCopyToArray, IrType.Array(container.Type.Element!), [container])]);

    return new IrIntrinsic(IrIntrinsicOp.ViewOf, writable ? IrType.Span(container.Type.Element!) : IrType.ReadOnlySpan(container.Type.Element!), [container]);
  }

  /// <summary>
  /// <c>a[i..j]</c> and its open forms: the bounds are evaluated, start first; the range must not
  /// start after its end nor end past the length, else it panics; then the view of the part.
  /// </summary>
  private IrExpr RangeIndex(IndexExpr index, RangeExpr range) {
    var container = this.Container(index.Target);
    var writable = !container.Type.IsList && container is not IrConstRef && container.Type.Kind != IrTypeKind.ReadOnlySpan;
    var view = this.Stable(this.View(container, writable), "view");
    var length = this.Stable(Length(view), "length");
    var start = range.Start is null ? new IrConst(IrType.U64, 0) : this.Stable(this.Value(range.Start), "start");
    IrExpr end;
    IrExpr outside;
    if (range.End is null) {
      end = length;
      outside = Compare(IrBinaryOp.Greater, start, length);
    } else if (range.Inclusive) {
      var last = this.Stable(this.Value(range.End), "last");
      end = Binary(IrBinaryOp.Add, last, new IrConst(IrType.U64, 1));
      // ..=last ends after last, which must lie inside; usize::MAX never does.
      outside = Or(Compare(IrBinaryOp.GreaterOrEqual, last, length), Compare(IrBinaryOp.Greater, start, end));
    } else {
      end = this.Stable(this.Value(range.End), "end");
      outside = Or(Compare(IrBinaryOp.Greater, start, end), Compare(IrBinaryOp.Greater, end, length));
    }

    if (outside is not IrConst { Value: 0 })
      this.Emit(new IrIf(outside, new IrBlock([RustTraps.OutOfBounds("range out of bounds for slice")]), null));

    var stable = IsSimple(end) ? end : this.Stable(end, "end");
    return new IrIntrinsic(IrIntrinsicOp.Slice, view.Type, [view, Cast(start, IrType.I32), Cast(Binary(IrBinaryOp.Subtract, stable, start), IrType.I32)]);
  }

  // ---------------------------------------------------------------- methods

  /// <summary>The methods of arrays, slices and <c>Vec</c>.</summary>
  private IrExpr? SliceMethod(MethodCallExpr call, string name) {
    if (name is "iter" or "iter_mut" or "into_iter" or "chunks_exact" or "chunks_exact_mut") {
      this.Refuse(RustFrontend.UnsupportedIteratorMethod, $"iterator of '{name}' used as a value", call.At);
      return null;
    }

    var element = this._typed.TypeOf(call.Receiver).Dereferenced.Element!;
    var container = this.Container(call.Receiver);
    switch (name) {
      case "len":
        return Length(container);
      case "is_empty":
        return Equal(Count(container), new IrConst(IrType.I32, 0));
      case "push": {
        var value = this.Value(call.Arguments[0]);
        this.Emit(new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [container, value])));
        return null;
      }
      case "pop":
        return new IrCollection(IrCollectionOp.ListRemoveLast, IrType.Optional(container.Type.Element!), [container]);
      case "clear":
        this.Emit(new IrExprStmt(new IrCollection(IrCollectionOp.ListClear, IrType.Void, [container])));
        return null;
      case "swap": {
        var positions = this.Operands([.. call.Arguments]).Select(this.Position).ToList();
        var first = this.Temporary(Load(container, positions[0]), "first");
        var second = this.Temporary(Load(container, positions[1]), "second");
        this.Emit(Store(container, positions[0], second));
        this.Emit(Store(container, positions[1], first));
        return null;
      }
      case "reverse": {
        var count = this.Stable(Count(container), "count");
        var low = this.Counter("low");
        var high = this.Temporary(Binary(IrBinaryOp.Subtract, count, new IrConst(IrType.I32, 1)), "high");
        var saved = this.NewLocal("saved", container.Type.Element!);
        this.Emit(new IrWhile(Binary(IrBinaryOp.Less, new IrLocalRef(low), high), new IrBlock([
          new IrDeclare(saved, Load(container, new IrLocalRef(low))),
          Store(container, new IrLocalRef(low), Load(container, high)),
          Store(container, high, new IrLocalRef(saved)),
          Next(low),
          new IrAssign(high.Local, Binary(IrBinaryOp.Subtract, high, new IrConst(IrType.I32, 1)))
        ])));
        return null;
      }
      case "fill": {
        var value = this.Stable(this.Value(call.Arguments[0]), "value");
        if (container.Type.IsArray && element.IsNumber) {
          this.Emit(new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayFill, IrType.Void, [container, value])));
        } else if (container.Type.IsView) {
          this.Emit(new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewFill, IrType.Void, [container, value])));
        } else {
          var index = this.Counter("i");
          this.Emit(new IrFor(Binary(IrBinaryOp.Less, new IrLocalRef(index), Count(container)), new IrBlock([Store(container, new IrLocalRef(index), value)]), new IrBlock([Next(index)])));
        }

        return null;
      }
      case "copy_from_slice": {
        var source = this.Stable(this.View(this.Converted(this.Value(call.Arguments[0]), RType.Reference(RType.Slice(element), false), null), false), "source");
        var target = this.Stable(this.View(container, true), "target");
        this.Emit(new IrIf(Compare(IrBinaryOp.NotEqual, Count(source), Count(target)),
          new IrBlock([RustTraps.PanicStatement("source slice length does not match destination slice length")]), null));
        this.Emit(new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewCopy, IrType.Void, [source, target])));
        return null;
      }
      case "copy_within":
        return this.CopyWithin(call, container);
      case "to_vec" or "clone":
        return this.Copied(container, this._typed.TypeOf(call.Receiver).Dereferenced, name == "clone");
      case "get": {
        var index = this.Stable(this.Value(call.Arguments[0]), "index");
        return new IrConditional(Compare(IrBinaryOp.Less, index, Length(container)), new IrSome(Load(container, this.Position(index))), new IrNone(IrType.Optional(container.Type.Element!)));
      }
      case "first":
        return new IrConditional(Compare(IrBinaryOp.Greater, Count(container), new IrConst(IrType.I32, 0)), new IrSome(Load(container, new IrConst(IrType.I32, 0))), new IrNone(IrType.Optional(container.Type.Element!)));
      default: {
        // last.
        var count = this.Stable(Count(container), "count");
        return new IrConditional(Compare(IrBinaryOp.Greater, count, new IrConst(IrType.I32, 0)),
          new IrSome(Load(container, Binary(IrBinaryOp.Subtract, count, new IrConst(IrType.I32, 1)))), new IrNone(IrType.Optional(container.Type.Element!)));
      }
    }
  }

  /// <summary><c>copy_within(a..b, d)</c>: both ranges checked, then the elements moved as if through a buffer.</summary>
  private IrExpr? CopyWithin(MethodCallExpr call, IrExpr container) {
    var range = (RangeExpr)call.Arguments[0];
    var view = this.Stable(this.View(container, true), "view");
    var length = this.Stable(Length(view), "length");
    var start = range.Start is null ? new IrConst(IrType.U64, 0) : this.Stable(this.Value(range.Start), "start");
    var end = range.End is null ? length
      : range.Inclusive ? this.Stable(Binary(IrBinaryOp.Add, this.Value(range.End), new IrConst(IrType.U64, 1)), "end")
      : this.Stable(this.Value(range.End), "end");
    var destination = this.Stable(this.Value(call.Arguments[1]), "destination");
    var outside = Or(Compare(IrBinaryOp.Greater, start, end), Compare(IrBinaryOp.Greater, end, length));
    this.Emit(new IrIf(outside, new IrBlock([RustTraps.OutOfBounds("range out of bounds for slice")]), null));
    var count = this.Stable(Binary(IrBinaryOp.Subtract, end, start), "count");
    this.Emit(new IrIf(Compare(IrBinaryOp.Greater, destination, Binary(IrBinaryOp.Subtract, length, count)),
      new IrBlock([RustTraps.OutOfBounds("dest is out of bounds")]), null));
    var size = Cast(count, IrType.I32);
    this.Emit(new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewCopy, IrType.Void, [
      new IrIntrinsic(IrIntrinsicOp.Slice, view.Type, [view, Cast(start, IrType.I32), size]),
      new IrIntrinsic(IrIntrinsicOp.Slice, view.Type, [view, Cast(destination, IrType.I32), size])
    ])));
    return null;
  }

  /// <summary><c>to_vec()</c> and <c>clone()</c>: a new list, or a new array, of the elements.</summary>
  private IrExpr Copied(IrExpr container, RType type, bool keep) {
    var elements = container switch {
      { Type.IsList: true } => new IrCollection(IrCollectionOp.ListCopyToArray, IrType.Array(container.Type.Element!), [container]),
      { Type.IsView: true } => new IrIntrinsic(IrIntrinsicOp.ViewToArray, IrType.Array(container.Type.Element!), [container]),
      _ => this.Duplicate(container, RType.Array(type.Element!, type.Length))
    };
    if (keep && type.Kind == RKind.Array)
      return elements;

    return new IrCollection(IrCollectionOp.ListFromArray, IrType.List(container.Type.Element!), [elements]);
  }

  /// <summary><c>Vec::new()</c>, <c>Vec::with_capacity(n)</c> and <c>vec![...]</c>: a new list; a capacity changes nothing observable.</summary>
  private IrExpr NewVec(RType type, IReadOnlyList<Expr> capacity) {
    foreach (var argument in capacity)
      this.Effect(argument);

    return new IrCollection(IrCollectionOp.ListNew, this.Ir(type), []);
  }

  private IrExpr VecMacro(MacroExpr macro) {
    var type = this.Ir(this._typed.TypeOf(macro));
    if (!macro.Repeat) {
      if (macro.Arguments.Count == 0)
        return new IrCollection(IrCollectionOp.ListNew, type, []);

      var values = this.Operands([.. macro.Arguments]);
      return new IrCollection(IrCollectionOp.ListFromArray, type, [new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(type.Element!), values)]);
    }

    var operands = this.Operands(macro.Arguments[0], macro.Arguments[1]);
    var count = this.Stable(operands[1], "count");
    // A length beyond what the IR's arrays hold is a capacity overflow, which panics.
    this.Emit(new IrIf(Compare(IrBinaryOp.Greater, count, new IrConst(IrType.U64, int.MaxValue)), new IrBlock([RustTraps.PanicStatement("capacity overflow")]), null));
    return new IrCollection(IrCollectionOp.ListFromArray, type, [this.Filled(IrType.Array(type.Element!), Cast(count, IrType.I32), operands[0])]);
  }

  // ---------------------------------------------------------------- bytes

  /// <summary><c>to_le_bytes</c> and <c>to_be_bytes</c>: the bytes of a number in the order the name says, whatever the host's.</summary>
  private IrExpr ToBytes(IrExpr value, bool little) {
    var size = value.Type.Size;
    var array = this.Temporary(new IrNewArray(IrType.Array(IrType.U8), new IrConst(IrType.I32, size)), "bytes");
    if (size == 1)
      this.Emit(new IrStore(array, new IrConst(IrType.I32, 0), Cast(value, IrType.U8)));
    else
      this.Emit(new IrExprStmt(new IrIntrinsic(little ? IrIntrinsicOp.StoreLittle : IrIntrinsicOp.StoreBig, IrType.Void, [array, new IrConst(IrType.I32, 0), value])));

    return array;
  }

  /// <summary><c>from_le_bytes</c> and <c>from_be_bytes</c>: the number the bytes of an array encode in the order the name says.</summary>
  private IrExpr FromBytes(IrExpr bytes, IrType type, bool little) {
    if (type.Size == 1)
      return Cast(new IrArrayLoad(bytes, new IrConst(IrType.I32, 0)), type);

    return new IrIntrinsic(little ? IrIntrinsicOp.LoadLittle : IrIntrinsicOp.LoadBig, type, [bytes, new IrConst(IrType.I32, 0)]);
  }

  /// <summary><c>let (l, r) = s.split_at(m);</c>: the middle checked, then the two views.</summary>
  private void SplitAt(TuplePattern pattern, MethodCallExpr call) {
    var mutable = call.Name == "split_at_mut";
    var container = this.Container(call.Receiver);
    var view = this.Stable(this.View(container, mutable || container.Type.IsArray || container.Type.Kind == IrTypeKind.Span), "view");
    var middle = this.Stable(this.Value(call.Arguments[0]), "middle");
    this.Emit(new IrIf(Compare(IrBinaryOp.Greater, middle, Length(view)), new IrBlock([RustTraps.OutOfBounds("mid > len")]), null));
    var at = Cast(middle, IrType.I32);
    var half = this._typed.TypeOf(call).Elements[0];
    var left = this.Converted(new IrIntrinsic(IrIntrinsicOp.Slice, view.Type, [view, new IrConst(IrType.I32, 0), at]), half, null);
    var right = this.Converted(new IrIntrinsic(IrIntrinsicOp.Slice, view.Type, [view, at]), half, null);
    this.Bind(pattern.Elements[0], new ValueItem(left), half);
    this.Bind(pattern.Elements[1], new ValueItem(right), half);
  }

}
