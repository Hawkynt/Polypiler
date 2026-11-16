using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// <c>a.concat(b, …)</c> of JavaScript Arrays whose length never changes: a new Array of the elements
/// of <c>a</c> and then of each argument, in order (ECMA-262 §23.1.3.1). An argument that is no such
/// Array - a list, a typed array, a single value, which <c>concat</c> appends as an element - is refused
/// by name. The copy is a function of the module per element type, which makes the new array and copies
/// both into it, called once per argument.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The functions that join two arrays, by their element type.</summary>
  private readonly Dictionary<IrType, string> _concats = [];

  /// <summary>The functions that fill an array and yield it, by its type.</summary>
  private readonly Dictionary<IrType, string> _fills = [];

  /// <summary>The call the statement being lowered consists of, whose value is dropped.</summary>
  private Node? _statement;

  /// <summary>The functions that copy a part of a list, by its type.</summary>
  private readonly Dictionary<IrType, string> _slices = [];

  /// <summary>
  /// The function of the module that copies the part of a list from a start to an end into a new list,
  /// each position clamped into the list and a negative one counted from its end, as <c>slice</c> does.
  /// </summary>
  private string ListSlice(IrType type) {
    if (this._slices.TryGetValue(type, out var known))
      return known;

    var name = $"$slice{this._slices.Count}";
    this._slices[type] = name;
    var (list, start, end) = (new IrLocal("l", type), new IrLocal("s", IrType.I32), new IrLocal("e", IrType.I32));
    var (count, copy, at, last) = (new IrLocal("n", IrType.I32), new IrLocal("r", type), new IrLocal("i", IrType.I32), new IrLocal("j", IrType.I32));
    IrExpr Ref(IrLocal local) => new IrLocalRef(local);
    // A position below 0 is counted from the end, never below 0; any other is at most the length. The sum of two int32 is taken in int64.
    IrExpr Clamped(IrLocal position) => new IrConditional(new IrBinary(IrBinaryOp.Less, Ref(position), I32(0)),
      new IrConvert(IrType.I32, Str(IrIntrinsicOp.Max, IrType.I64, new IrBinary(IrBinaryOp.Add, new IrConvert(IrType.I64, Ref(count), IrConversion.Wrap), new IrConvert(IrType.I64, Ref(position), IrConversion.Wrap)), new IrConst(IrType.I64, 0)), IrConversion.Wrap),
      Min(Ref(position), Ref(count)));
    this.Callbacks.Add(new IrFunction(name, false, type, [list, start, end], [count, copy, at, last], new IrBlock([
      new IrDeclare(count, ListOp(IrCollectionOp.ListCount, type, Ref(list))),
      new IrDeclare(copy, ListOp(IrCollectionOp.ListNew, type)),
      new IrDeclare(at, Clamped(start)),
      new IrDeclare(last, Clamped(end)),
      new IrWhile(new IrBinary(IrBinaryOp.Less, Ref(at), Ref(last)), new IrBlock([
        new IrExprStmt(ListOp(IrCollectionOp.ListAdd, type, Ref(copy), ListOp(IrCollectionOp.ListGet, type, Ref(list), Ref(at)))),
        new IrAssign(at, new IrBinary(IrBinaryOp.Add, Ref(at), I32(1)))
      ])),
      new IrReturn(Ref(copy))
    ])));
    return name;
  }

  /// <summary>The function of the module that fills a whole array of a type with a value and yields the array, as <c>fill</c> does.</summary>
  private string Filled(IrType type) {
    if (this._fills.TryGetValue(type, out var known))
      return known;

    var name = $"$fill{this._fills.Count}";
    this._fills[type] = name;
    var (array, value) = (new IrLocal("a", type), new IrLocal("v", type.Element!));
    this.Callbacks.Add(new IrFunction(name, false, type, [array, value], [], new IrBlock([
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewFill, IrType.Void, [new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(type.Element!), [new IrLocalRef(array)]), new IrLocalRef(value)])),
      new IrReturn(new IrLocalRef(array))
    ])));
    return name;
  }

  /// <summary>A call of <c>concat</c> on an Array whose length never changes; <see langword="null"/> for any other call.</summary>
  private IrExpr? Concatenated(CallExpression call) {
    if (call.Callee is not MemberExpression { Computed: false, Property: Identifier { Name: "concat" } } member
        || types.Of(member.Object) is not { Kind: JsKind.Array, IsPlain: true, IsGrowable: false, IsView: false })
      return null;

    if (types.Of(call).Ir is not { IsArray: true } type)
      throw this.Unsupported(call, $"concat of {types.Of(member.Object).Describe()} with {string.Join(" and ", call.Arguments.Select(argument => types.Of(argument).Describe()))}");

    var joined = this.Value(member.Object, type);
    foreach (var argument in call.Arguments)
      joined = new IrCall(this.Concat(type), type, [joined, this.Value(argument, type)]);

    return joined;
  }

  /// <summary>The function of the module that joins two arrays of a type: <c>r = new T[a.length + b.length]</c>, both copied into it.</summary>
  private string Concat(IrType type) {
    if (this._concats.TryGetValue(type, out var known))
      return known;

    var name = $"$concat{this._concats.Count}";
    this._concats[type] = name;
    var (first, second, joined) = (new IrLocal("a", type), new IrLocal("b", type), new IrLocal("r", type));
    IrExpr View(IrExpr array) => new IrIntrinsic(IrIntrinsicOp.ViewOf, IrType.Span(type.Element!), [array]);
    var length = new IrArrayLength(new IrLocalRef(first));
    this.Callbacks.Add(new IrFunction(name, false, type, [first, second], [joined], new IrBlock([
      new IrDeclare(joined, new IrNewArray(type, new IrBinary(IrBinaryOp.Add, length, new IrArrayLength(new IrLocalRef(second))))),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewCopy, IrType.Void, [View(new IrLocalRef(first)), View(new IrLocalRef(joined))])),
      new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ViewCopy, IrType.Void, [View(new IrLocalRef(second)), new IrIntrinsic(IrIntrinsicOp.Slice, IrType.Span(type.Element!), [View(new IrLocalRef(joined)), length])])),
      new IrReturn(new IrLocalRef(joined))
    ])));
    return name;
  }

}
