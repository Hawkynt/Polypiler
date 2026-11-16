using System.Collections.Generic;
using System.Linq;
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

    // An Array whose length never changes is an array of the IR as a typed array is: slice copies, fill stores, without a conversion either way.
    if (receiver.IsPlain && (receiver.IsGrowable || method is "subarray" or "set"))
      throw this.Unsupported(call, $"{method} on a plain Array");

    // fill yields the array it fills, of the type the value makes it, which a call that stands as a statement drops.
    if (method == "fill" && call != this._statement)
      return this.FillValue(call, types.Of(call).Ir);

    var source = this.Expression(member.Object);
    var element = source.Type.Element!;
    var view = AsView(source);
    var arguments = call.Arguments;
    switch (method) {
      case "set":
        if (arguments.Count is not (1 or 2))
          throw this.Unsupported(call, $"set with {arguments.Count} arguments");

        var copied = this.Expression(arguments[0]);
        if (copied.Type.Element != element || !(copied.Type.IsSequence || copied.Type.IsMemory) || types.Of(arguments[0]).IsPlain)
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

  /// <summary>
  /// <c>a.fill(v)</c> as a value, an array of the type of the place it goes to - the type the value
  /// makes it where the place has none: the array, filled whole, which a function of the module yields.
  /// </summary>
  private IrExpr FillValue(CallExpression call, IrType? type) {
    if (call.Arguments.Count != 1 || type is not { IsArray: true })
      throw this.Unsupported(call, "fill of a part of an array as a value");

    var array = this.Value(((MemberExpression)call.Callee).Object, type);
    return new IrCall(this.Filled(type), type, [array, this.Value(call.Arguments[0], type.Element!)]);
  }

  /// <summary>
  /// <c>for (const x of v)</c> over a typed array or a view: a loop over its elements by their index. The
  /// index moves on before the body runs, so that a <c>continue</c> goes on with the next element; the
  /// length of a typed array never changes, so it is read again at every step as JavaScript's iterator does.
  /// </summary>
  private IrBlock ElementsOf(ForOfStatement loop, string name, Variable variable) {
    var statements = new List<IrStmt>();
    var sequence = this.Once(this.Array(loop.Right), statements);
    var at = new IrLocal($"$t{this._locals.Count}", IrType.I32);
    this._locals.Add(at);
    statements.Add(new IrDeclare(at, Zero));
    var local = new IrLocal(name, this.Storable(variable.Type, loop.Left));
    variable.Local = local;
    this._locals.Add(local);
    var element = new Typed(Read(new IrArrayLoad(sequence, new IrLocalRef(at))), types.Of(loop));
    this.AccessesElements = true;
    IrStmt[] pass = [
      new IrDeclare(local, this.To(element, local.Type, loop.Right)),
      new IrAssign(at, new IrBinary(IrBinaryOp.Add, new IrLocalRef(at), new IrConst(IrType.I32, 1)))
    ];
    var before = this._assigned;
    var body = this.Body(loop.Body);
    this._assigned = before;
    IrExpr length = sequence.Type.IsView ? new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [sequence]) : new IrArrayLength(sequence);
    statements.Add(new IrWhile(new IrBinary(IrBinaryOp.Less, new IrLocalRef(at), length), new IrBlock([.. pass, .. body.Statements])));
    return new IrBlock(statements);
  }

  /// <summary>An array, a view or a memory as a writable view: the view itself, or a view of the whole array or memory.</summary>
  private static IrExpr AsView(IrExpr sequence)
    => sequence.Type.IsView ? sequence : new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(sequence.Type.Element!), [sequence]);

  /// <summary>A memory as the span of its elements, through which they are read and written; anything else as it is.</summary>
  private static IrExpr Spanned(IrExpr value) => value.Type.IsMemory ? AsView(value) : value;

  /// <summary>
  /// A view as the memory a field keeps (#1325): the view of a typed array, or of a memory, or a part of
  /// such a view, made again as a memory of the same elements. A memory read as a span is that memory. A
  /// view that is not made where it is stored - a parameter - is refused: it could be of the stack.
  /// </summary>
  private IrExpr Memoried(IrExpr view, IrType memory, Node node) {
    // The source of a view of the frontend is an array, a memory or another view.
    if (view is IrIntrinsic { Op: IrIntrinsicOp.ViewOf or IrIntrinsicOp.Slice, Arguments: [var source, ..] } part) {
      var origin = source.Type.IsView ? this.Memoried(source, memory, node) : source;
      var rest = part.Arguments.Skip(1).ToList();
      return rest.Count == 0 && origin.Type == memory ? origin : new IrIntrinsic(origin.Type.IsArray ? IrIntrinsicOp.ViewOf : IrIntrinsicOp.Slice, memory, [origin, .. rest]);
    }

    throw this.Unsupported(node, $"{types.Text(node)} kept in a field, which is a view not made of a typed array here");
  }

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

  /// <summary>Whether evaluating an expression again gives the same value and has no effect: a variable, a field of one, a constant, wrapping arithmetic of them, a view of one.</summary>
  private static bool Repeatable(IrExpr expression) => expression switch {
    IrLocalRef or IrConst => true,
    // A field of an object in a variable, which nothing between the reads writes.
    IrFieldLoad { Target: IrLocalRef } => true,
    IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor } binary => Repeatable(binary.Left) && Repeatable(binary.Right),
    IrBinary { Op: IrBinaryOp.Divide or IrBinaryOp.Remainder, Type.IsFloat: true } quotient => Repeatable(quotient.Left) && Repeatable(quotient.Right),
    IrConvert { Contract: not IrConversion.Trap } conversion => Repeatable(conversion.Operand),
    IrIntrinsic { Op: IrIntrinsicOp.Truncate, Arguments: [var number] } => Repeatable(number),
    IrIntrinsic { Op: IrIntrinsicOp.ViewOf, Arguments: [var whole] } => Repeatable(whole),
    _ => false
  };

}
