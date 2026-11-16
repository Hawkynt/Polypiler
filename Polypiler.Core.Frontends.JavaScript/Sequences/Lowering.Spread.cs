using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// An array literal with spread elements (#1405): <c>[...a]</c> is a new Array of the elements of
/// <c>a</c>, <c>[x, ...a, ...b]</c> the elements of every part in order (ECMA-262 §13.2.4.1,
/// ArrayAccumulation). A part spreads an Array, a list, a typed array or a view; the copy is an Array,
/// distinct from its source, whose elements are numbers as they are - a store into the copy of a
/// typed array no longer wraps, which the element type the inference joined for the literal says. The
/// parts are joined by the <c>concat</c> function of the module; a spread of anything else - a
/// string, a <c>Map</c>, a <c>Set</c>, a generator - is refused by name.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The functions that copy an array into a new array of another element type, by the two types.</summary>
  private readonly Dictionary<(IrType From, IrType To), string> _widenings = [];

  /// <summary>An array literal with at least one spread element, as a new array of the given element type.</summary>
  private IrExpr Spread(ArrayExpression literal, IrType element) {
    var type = IrType.Array(element);
    var parts = new List<IrExpr>();
    var run = new List<IrExpr>();
    foreach (var item in literal.Elements) {
      if (item is not SpreadElement spread) {
        run.Add(this.Value(item!, element));
        continue;
      }

      if (run.Count > 0)
        parts.Add(new IrIntrinsic(IrIntrinsicOp.ArrayOf, type, run));

      run = [];
      parts.Add(this.Spread(spread, type, literal.Elements.Count == 1));
    }

    if (run.Count > 0)
      parts.Add(new IrIntrinsic(IrIntrinsicOp.ArrayOf, type, run));

    // The parts are evaluated in order, left to right; concat makes the new array of two or more.
    return parts.Aggregate((joined, next) => new IrCall(this.Concat(type), type, [joined, next]));
  }

  /// <summary>The elements one spread element adds, as an array of the type of the literal: a new one where the literal is that spread alone.</summary>
  private IrExpr Spread(SpreadElement spread, IrType type, bool alone) {
    var held = types.Of(spread.Argument);
    if (held.Kind != JsKind.Array)
      throw this.Unsupported(spread, $"a spread of {held.Describe()}");

    var source = this.Expression(spread.Argument);
    IrExpr copy;
    if (source.Type.IsList) {
      // A list of what no array holds is the element of a list literal, which refuses a spread before.
      copy = ListOp(IrCollectionOp.ListCopyToArray, source.Type, source);
    } else if (source.Type.IsArray && (source.Type != type || !alone)) {
      // The widening or the concat that follows makes the new array.
      copy = source;
    } else {
      copy = new IrIntrinsic(IrIntrinsicOp.ViewToArray, IrType.Array(source.Type.Element!), [AsView(source)]);
    }

    return copy.Type == type ? copy : new IrCall(this.Widened(copy.Type, type, held.Element!, spread), type, [copy]);
  }

  /// <summary>
  /// The function of the module that copies an array into a new array of the element type of a
  /// literal that holds its elements as well: each element read as JavaScript reads it, then held as
  /// every value of the literal is.
  /// </summary>
  private string Widened(IrType from, IrType to, JsType element, Node node) {
    if (this._widenings.TryGetValue((from, to), out var known))
      return known;

    var (source, copy, at) = (new IrLocal("a", from), new IrLocal("r", to), new IrLocal("i", IrType.I32));
    IrExpr Ref(IrLocal local) => new IrLocalRef(local);
    var read = Read(new IrArrayLoad(Ref(source), Ref(at)));
    // An array no value reaches is empty, or made by new Array(n), whose elements read 0 where JavaScript reads undefined.
    var unknown = element.Kind == JsKind.Unknown && to.Element!.IsNumeric;
    this.AccessesElements |= unknown;
    var held = !unknown ? this.To(new Typed(read, element.Float == 32 ? JsType.F64 : element), to.Element!, node)
      : read.Type.IsFloat && to.Element!.IsInteger ? new IrConvert(to.Element!, read, IrConversion.Saturate) : Convert(read, to.Element!);
    var name = $"$widen{this._widenings.Count}";
    this._widenings[(from, to)] = name;
    this.Callbacks.Add(new IrFunction(name, false, to, [source], [copy, at], new IrBlock([
      new IrDeclare(copy, new IrNewArray(to, new IrArrayLength(Ref(source)))),
      new IrDeclare(at, I32(0)),
      new IrWhile(new IrBinary(IrBinaryOp.Less, Ref(at), new IrArrayLength(Ref(source))), new IrBlock([
        new IrStore(Ref(copy), Ref(at), held),
        new IrAssign(at, new IrBinary(IrBinaryOp.Add, Ref(at), I32(1)))
      ])),
      new IrReturn(Ref(copy))
    ])));
    return name;
  }

}
