using System.Collections.Generic;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The views of typed arrays (#1325): <c>subarray</c>, which shares the memory of its source, and the
/// methods that copy and fill through views - <c>slice</c>, <c>set</c>, <c>fill</c>. ECMA-262 clamps every
/// position of <c>subarray</c>, <c>slice</c> and <c>fill</c> into the source and counts a negative one from
/// its end (§23.2.3.29, §23.2.3.27, §23.2.3.9); that arithmetic is written out with <c>Min</c> and
/// <c>Max</c>, so that the exact slice of the IR never traps where JavaScript does not. <c>set</c> throws a
/// <c>RangeError</c> for a source that does not fit, which is the trap of a copy.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>A call of a method of the views on a typed array or a view; <see langword="null"/> for any other call.</summary>
  private IrExpr? ViewMethod(CallExpression call) {
    if (call.Callee is not MemberExpression { Computed: false, Property: Identifier { Name: "subarray" or "slice" or "set" or "fill" } property } member)
      return null;

    var method = property.Name;
    var receiver = types.Of(member.Object);
    if (receiver.Kind != JsKind.Array)
      return null;

    if (receiver.IsPlain)
      throw this.Unsupported(call, $"{method} on a plain Array");

    var source = this.Expression(member.Object);
    var element = source.Type.Element!;
    var view = AsView(source);
    var arguments = call.Arguments;
    switch (method) {
      case "set":
        if (arguments.Count is not (1 or 2))
          throw this.Unsupported(call, $"set with {arguments.Count} arguments");

        var copied = this.Expression(arguments[0]);
        if (copied.Type.Element != element || !copied.Type.IsSequence || types.Of(arguments[0]).IsPlain)
          throw this.Unsupported(arguments[0], $"set of {types.Of(arguments[0]).Describe()} into {receiver.Describe()}");

        IrExpr target = arguments.Count == 1 ? view : new IrIntrinsic(IrIntrinsicOp.Slice, view.Type, [view, Word(this.Position(arguments[1]))]);
        return new IrIntrinsic(IrIntrinsicOp.ViewCopy, IrType.Void, [AsView(copied), target]);
      case "fill":
        if (arguments.Count is < 1 or > 3)
          throw this.Unsupported(call, $"fill with {arguments.Count} arguments");

        var value = this.Value(arguments[0], element);
        var filled = arguments.Count == 1 ? view : this.Clamped(call, view, arguments[1], arguments.Count == 3 ? arguments[2] : null);
        return new IrIntrinsic(IrIntrinsicOp.ViewFill, IrType.Void, [filled, value]);
      default:
        if (arguments.Count > 2)
          throw this.Unsupported(call, $"{method} with {arguments.Count} arguments");

        var part = arguments.Count == 0 ? view : this.Clamped(call, view, arguments[0], arguments.Count == 2 ? arguments[1] : null);
        return method == "subarray" ? part : new IrIntrinsic(IrIntrinsicOp.ViewToArray, IrType.Array(element), [part]);
    }
  }

  /// <summary>An array or a view as a writable view: the view itself, or a view of the whole array.</summary>
  private static IrExpr AsView(IrExpr sequence)
    => sequence.Type.IsView ? sequence : new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(sequence.Type.Element!), [sequence]);

  /// <summary>
  /// A position as ECMA-262's <c>ToIntegerOrInfinity</c> makes it: a proven 32-bit integer as it is, any
  /// other number without its fraction as a double, a NaN becoming 0 where it is clamped.
  /// </summary>
  private IrExpr Position(Node node) {
    var position = this.Evaluate(node);
    if (position.Type.IsWord && position.Type.Within(IrType.I32))
      return Convert(position.Expr, IrType.I32);

    if (position.Type.Kind != JsKind.Number)
      throw this.Unsupported(node, $"a position of {position.Type.Describe()}");

    return new IrIntrinsic(IrIntrinsicOp.Truncate, IrType.F64, [this.Float(position, node)]);
  }

  /// <summary>A position as an <c>i32</c>: a double outside the range becomes its nearest end, a NaN 0.</summary>
  private static IrExpr Word(IrExpr position)
    => position.Type == IrType.I32 ? position : new IrConvert(IrType.I32, position, IrConversion.Saturate);

  /// <summary>
  /// The part of a view from <c>begin</c> to <c>end</c> with the positions of ECMA-262: a negative one counts
  /// from the end, both are clamped into the view, and an end before the begin leaves nothing. The view and
  /// the positions are read more than once, so each has to be a variable, a constant or arithmetic of them.
  /// </summary>
  private IrIntrinsic Clamped(CallExpression call, IrExpr view, Node begin, Node? end) {
    var first = this.Position(begin);
    var last = end is null ? null : this.Position(end);
    if (!Repeatable(view) || !Repeatable(first) || (last is not null && !Repeatable(last)))
      throw this.Unsupported(call, "a part of a typed array whose array or positions are more than variables, constants and arithmetic of them");

    // A part of a whole array is the view of that part of it, and its length is the array's.
    var array = view is IrIntrinsic { Op: IrIntrinsicOp.ViewOf, Arguments: [{ Type.IsArray: true } whole] } ? whole : null;
    IrExpr length = array is null ? new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [view]) : new IrArrayLength(array);
    var op = array is null ? IrIntrinsicOp.Slice : IrIntrinsicOp.ViewOf;
    var start = Word(Clamp(first, length));
    if (last is null)
      return new IrIntrinsic(op, view.Type, [array ?? view, start]);

    var count = new IrIntrinsic(IrIntrinsicOp.Max, IrType.I32, [new IrBinary(IrBinaryOp.Subtract, Word(Clamp(last, length)), start), Zero]);
    return new IrIntrinsic(op, view.Type, [array ?? view, start, count]);
  }

  private static readonly IrConst Zero = new(IrType.I32, 0);

  /// <summary>
  /// <c>position &lt; 0 ? Max(length + position, 0) : Min(position, length)</c>, in the type of the position: in
  /// doubles a NaN is neither below 0 nor a minimum, and stays a NaN that the conversion to <c>i32</c> makes 0.
  /// </summary>
  private static IrExpr Clamp(IrExpr position, IrExpr length) {
    // A constant has its sign: only its side of the choice is left.
    if (position is IrConst { Type.Kind: IrTypeKind.I32, Value: var constant })
      return constant < 0
        ? new IrIntrinsic(IrIntrinsicOp.Max, IrType.I32, [new IrBinary(IrBinaryOp.Add, length, position), Zero])
        : new IrIntrinsic(IrIntrinsicOp.Min, IrType.I32, [position, length]);

    var type = position.Type;
    IrExpr zero = type == IrType.I32 ? Zero : new IrConst(IrType.F64, 0);
    var measured = type == IrType.I32 ? length : new IrConvert(IrType.F64, length);
    return new IrConditional(
      new IrBinary(IrBinaryOp.Less, position, zero),
      new IrIntrinsic(IrIntrinsicOp.Max, type, [new IrBinary(IrBinaryOp.Add, measured, position), zero]),
      new IrIntrinsic(IrIntrinsicOp.Min, type, [position, measured]));
  }

  /// <summary>Whether evaluating an expression again gives the same value and has no effect: a variable, a constant, wrapping arithmetic of them, a view of one.</summary>
  private static bool Repeatable(IrExpr expression) => expression switch {
    IrLocalRef or IrConst => true,
    IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor } binary => Repeatable(binary.Left) && Repeatable(binary.Right),
    IrBinary { Op: IrBinaryOp.Divide or IrBinaryOp.Remainder, Type.IsFloat: true } quotient => Repeatable(quotient.Left) && Repeatable(quotient.Right),
    IrConvert { Contract: not IrConversion.Trap } conversion => Repeatable(conversion.Operand),
    IrIntrinsic { Op: IrIntrinsicOp.Truncate, Arguments: [var number] } => Repeatable(number),
    IrIntrinsic { Op: IrIntrinsicOp.ViewOf, Arguments: [var whole] } => Repeatable(whole),
    _ => false
  };

}
