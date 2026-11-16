using System.Collections.Generic;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// What JavaScript does at an index outside an array, where the imported code can do the same at the
/// cost of a test (#1367). An element read outside the array is <c>undefined</c>, which <c>ToInt32</c>
/// and <c>ToUint32</c> make 0 and <c>ToNumber</c> a NaN: a read whose value meets one of them directly
/// yields that value outside the array. A write outside a typed array is ignored: such a store is made
/// only inside the array. Nothing is tested where the program proves the index inside the array - an
/// index into a constant table that the inference bounds below its length, or the variable of a
/// <c>for</c> loop that runs from a non-negative constant up to the length of the array it indexes. Everything else keeps the trap, and
/// the caveat <c>js-oob-read</c> announces it.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The loop variables of the loops being lowered, each with the array whose length bounds it.</summary>
  private readonly List<(object Index, object Array)> _inRange = [];

  /// <summary>The element reads lowered with a trap outside the array, which the caveat <c>js-oob-read</c> announces.</summary>
  private readonly HashSet<IrArrayLoad> _trappingReads = new(ReferenceEqualityComparer.Instance);

  /// <summary>
  /// An element read whose value is taken as a 32-bit integer or as a double, with the value
  /// JavaScript's <c>undefined</c> becomes outside the array; <see langword="null"/> where the node is
  /// no element read of an array.
  /// </summary>
  /// <param name="node">The expression.</param>
  /// <param name="coercion"><see cref="Coercion.Int32"/> or <see cref="Coercion.Uint32"/> for an integer, <see cref="Coercion.None"/> for a double.</param>
  private Typed? Element(Node node, Coercion coercion) {
    if (node is not MemberExpression { Computed: true } member || this._substitutes.ContainsKey(node) || types.Of(member.Object).Kind != JsKind.Array
        || types.Of(node, coercion).Kind != JsKind.Number)
      return null;

    var value = this.Evaluate(node, coercion);
    var load = value.Expr switch {
      IrArrayLoad direct => direct,
      IrConvert { Operand: IrArrayLoad widened, Type.Kind: IrTypeKind.F64 } => widened,
      _ => null
    };
    // A read the program proves inside, or one of another kind, is as it is.
    if (load is null || load.Type.IsLong || load.Type == IrType.Bool || !this._trappingReads.Remove(load))
      return value;

    // undefined is 0 under ToInt32 and ToUint32, and a NaN as a number: the element is a double then.
    var integer = coercion != Coercion.None && load.Type.IsInteger;
    var (inside, element) = this.Guard(load.Array, load.Index);
    IrExpr read = integer || element.Type == IrType.F64 ? element : new IrConvert(IrType.F64, element);
    IrExpr outside = integer ? new IrConst(element.Type, 0) : new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, double.NaN));
    return value with { Expr = new IrConditional(inside, read, outside) };
  }

  /// <summary>
  /// Whether an index names an element of an array, the array and the index each evaluated once and
  /// in that order, and the load of that element from what they evaluated to. An index that is a
  /// double names an element where it is the integer of one: JavaScript reads <c>undefined</c> at a
  /// fraction, at a NaN and at -1 alike.
  /// </summary>
  private (IrExpr Inside, IrArrayLoad Element) Guard(IrExpr array, IrExpr index) {
    var (first, again) = Fixed(array) ? (array, array) : this.Bound(array);
    IrExpr Length(IrExpr of) => new IrConvert(IrType.U32, of.Type.IsView ? new IrIntrinsic(IrIntrinsicOp.ViewLength, IrType.I32, [of]) : new IrArrayLength(of));
    if (index is IrConvert { Contract: IrConversion.Trap, Operand: { Type.Kind: IrTypeKind.F64 } number }) {
      var (tested, held) = this.Bound(number);
      IrExpr Whole(IrExpr of) => new IrConvert(IrType.I32, of, IrConversion.Saturate);
      var inside = new IrBinary(IrBinaryOp.LogicalAnd,
        new IrBinary(IrBinaryOp.Greater, Length(first), new IrConvert(IrType.U32, Whole(tested))),
        new IrBinary(IrBinaryOp.Equal, new IrConvert(IrType.F64, Whole(held)), held));
      return (inside, new IrArrayLoad(again, Whole(held)));
    }

    var (at, atAgain) = this.Bound(index);
    // The length is compared first: the array is evaluated before the index.
    return (new IrBinary(IrBinaryOp.Greater, Length(first), at.Type == IrType.U32 ? at : new IrConvert(IrType.U32, at)), new IrArrayLoad(again, atAgain));
  }

  /// <summary>Whether an array expression is the same array wherever it is read: a variable, a constant table or an array of the module.</summary>
  private static bool Fixed(IrExpr array) => array is IrLocalRef or IrConstRef or IrGlobalRef { Type.IsArray: true };

  /// <summary>
  /// Whether the program proves an element access inside the array: an index into a constant table
  /// whose every value the inference bounds below its length - a constant, <c>x &amp; 255</c> into a
  /// table of 256 -, or a loop variable at the array whose length bounds it.
  /// </summary>
  private bool Inside(MemberExpression member, IrArrayLoad load) {
    if (load.Array is IrConstRef { Constant: var table } && types.Of(member.Property) is { IsWord: true } bounded && bounded.Low >= 0 && bounded.High < table.Values.Count)
      return true;

    return member is { Object: Identifier array, Property: Identifier index }
      && this._model.Bindings.GetValueOrDefault(array) is { } arrayBinding && this._model.Bindings.GetValueOrDefault(index) is { } indexBinding
      && this._inRange.Contains((indexBinding, arrayBinding));
  }

  /// <summary>
  /// The loop variable and the array of a <c>for (let i = c; i &lt; a.length; i++)</c> with a
  /// non-negative integer <c>c</c>, where nothing in the function but the update writes <c>i</c> and
  /// nothing writes <c>a</c>, whose length the IR never changes; <see langword="null"/> for any other loop.
  /// </summary>
  private (object Index, object Array)? Bounding(ForStatement loop) {
    if (loop is not {
      Init: VariableDeclaration { Kind: VariableDeclarationKind.Let, Declarations: [{ Id: Identifier declared, Init: NumericLiteral { Value: >= 0 and var start } }] },
      Test: BinaryExpression { Operator: Operator.LessThan, Left: Identifier tested, Right: MemberExpression { Computed: false, Object: Identifier array, Property: Identifier { Name: "length" } } },
      Update: UpdateExpression { Operator: Operator.Increment, Argument: Identifier stepped } update
    } || start != System.Math.Floor(start) || types.Of(array).Kind != JsKind.Array)
      return null;

    var bindings = this._model.Bindings;
    if (bindings.GetValueOrDefault(tested) is not Variable index || index.Declaration != declared || bindings.GetValueOrDefault(stepped) != index
        || bindings.GetValueOrDefault(array) is not { } arrayBinding)
      return null;

    // An element store writes neither the array nor its index. A write in a nested function has no
    // binding of this function: there it is told by its name.
    bool Writes(Node target, Identifier name, object binding)
      => target is not MemberExpression
        && Contains(target, node => node is Identifier written && written.Name == name.Name && (bindings.GetValueOrDefault(written) ?? binding) == binding);

    var body = this._model.Candidate.Function.Body;
    var written = Contains(body, node => node != update && node switch {
      AssignmentExpression assignment => Writes(assignment.Left, declared, index) || Writes(assignment.Left, array, arrayBinding),
      UpdateExpression step => Writes(step.Argument, declared, index) || Writes(step.Argument, array, arrayBinding),
      _ => false
    });
    return written ? null : (index, arrayBinding);
  }

  /// <summary>
  /// A store into an element of a typed array the program does not prove inside it, made only inside
  /// the array as JavaScript ignores one outside: the array, the index and the value are evaluated in
  /// that order first.
  /// </summary>
  private void TypedStore(IrExpr array, IrExpr index, IrExpr value, List<IrStmt> into) {
    array = Fixed(array) ? array : this.Once(array, into);
    index = this.Once(index is IrConvert { Contract: IrConversion.Trap, Operand.Type.Kind: IrTypeKind.F64 } number ? number.Operand : index, into);
    value = this.Once(value, into);
    // Each is a variable now, which the guard reads again without a temporary of its own.
    var (inside, store) = this.Guard(array, index.Type == IrType.F64 ? new IrConvert(IrType.I32, index, IrConversion.Trap) : index);
    into.Add(new IrIf(inside, new IrBlock([new IrStore(store.Array, store.Index, value)]), null));
  }

}
