using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// JavaScript's array callbacks (#1329): a call of <c>map</c>, <c>filter</c>, <c>forEach</c>,
/// <c>reduce</c>, <c>reduceRight</c>, <c>some</c>, <c>every</c>, <c>find</c>, <c>findIndex</c>,
/// <c>flatMap</c> or <c>sort(comparator)</c> on an array is the call of a function of the module that
/// runs it as the loop ECMA-262 specifies (<see cref="ArrayCallbacks"/>) - eager, over the whole array,
/// before anything after it. The callback is an arrow, a function expression or a function of the
/// file. <c>JavaScriptArrayApi</c> lists what is refused.
/// </summary>
internal sealed partial class Lowering {

  private readonly HashSet<string> _callbackNames = [];

  /// <summary>The operations on collections that change nothing a comparator could see afterwards.</summary>
  private static readonly HashSet<IrCollectionOp> _reads = [
    IrCollectionOp.ListNew, IrCollectionOp.ListCount, IrCollectionOp.ListGet, IrCollectionOp.ListContains, IrCollectionOp.ListIndexOfEquals,
    IrCollectionOp.ListIndexOfStrict, IrCollectionOp.ListIncludesSameValueZero, IrCollectionOp.ListCopyToArray, IrCollectionOp.ListFromArray,
    IrCollectionOp.MapNew, IrCollectionOp.MapCount, IrCollectionOp.MapGet, IrCollectionOp.MapTryGet, IrCollectionOp.MapContainsKey,
    IrCollectionOp.SetNew, IrCollectionOp.SetCount, IrCollectionOp.SetContains
  ];

  /// <summary>The functions that run the array callbacks of the module, in the order their calls were lowered.</summary>
  public List<IrFunction> Callbacks { get; } = [];

  /// <summary>Whether a comparator returns a double, which a NaN makes inconsistent: the caveat <c>js-sort-order</c>.</summary>
  public bool SortsByDifference { get; private set; }

  /// <summary>
  /// A call of an array method with a callback as the call of the function that runs it, or
  /// <see langword="null"/> for any other expression. A method of the table the subset has not is refused by name.
  /// </summary>
  private IrExpr? ArrayCallback(Node node) {
    if (node is not CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: var method } } member } call
        || types.Of(member.Object) is not { Kind: JsKind.Array } receiver)
      return null;

    if (JavaScriptArrayApi.Refusal(method) is { } refusal)
      throw this.Unsupported(call, refusal);

    if (!TypeInference.IsCallbackMethod(method))
      return null;

    // A typed array maps into one of its own kind, which converts what the callback returns; a double needs no conversion.
    if (receiver.IsView || (!receiver.IsPlain && method is "map" or "flatMap" && receiver.Element!.Ir != IrType.F64))
      throw this.Unsupported(call, $"{method} of {receiver.Describe()}");

    if (method == "sort" && call.Arguments.Count == 0)
      throw this.Unsupported(call, "sort() without a comparator, which orders the elements by their text");

    var reduces = method is "reduce" or "reduceRight";
    if (call.Arguments.Count == 0 || call.Arguments.Any(argument => argument is SpreadElement))
      throw this.Unsupported(call, $"{method} without a callback");

    if (call.Arguments.Count > (reduces ? 2 : 1))
      throw this.Unsupported(call, $"{method} with a thisArg");

    var given = call.Arguments[0];
    if (types.Of(given).Kind != JsKind.Function)
      throw this.Unsupported(given, $"{types.Text(given)} as the callback of {method}, which is no function of the file");

    var source = this.Expression(member.Object);
    var callback = this.Expression(given);
    var signature = callback.Type.Signature!;
    var maximum = reduces ? 4 : method == "sort" ? 2 : 3;
    if (signature.Parameters.Count > maximum)
      throw this.Unsupported(given, $"a callback of {method} with more than {maximum} parameters");

    var result = method == "forEach" ? IrType.Void : this.Storable(types.Of(call), call);
    // find yields undefined where nothing matches, which the IR has for a number and a reference only.
    if (method == "find" && !result.IsOptional && !(result.IsNullable && !result.IsString))
      throw this.Unsupported(call, $"find of {receiver.Describe()}, whose undefined the IR has no value for");

    this.CheckCallback(method, given, source.Type, signature, result);
    if (method == "sort") {
      this.CheckComparator(callback, given);
      this.SortsByDifference |= signature.Result.IsFloat;
    }

    var initial = reduces && call.Arguments.Count == 2 ? this.Value(call.Arguments[1], result) : null;
    var name = this.CallbackName(method);
    this.Callbacks.Add(ArrayCallbacks.Build(name, method, source.Type, callback.Type, result, initial?.Type));
    return new IrCall(name, result, initial is null ? [source, callback] : [source, callback, initial]);
  }

  /// <summary>A name for the function of a call: the function of the file it stands in, a <c>$</c> and the method.</summary>
  private string CallbackName(string method) {
    var candidate = this._model.Candidate;
    while (candidate.Parent is { } parent)
      candidate = parent.Candidate;

    var name = $"{candidate.Name}${method}";
    for (var index = 2; !this._callbackNames.Add(name); ++index)
      name = $"{candidate.Name}${method}{index}";

    return name;
  }

  /// <summary>
  /// Whether the callback takes what the method passes and returns what the method uses: the element,
  /// the index and the array, each of the type of its parameter or a number it converts to; a result
  /// tested for truth of a truth value or a number, mapped of the element type of the result.
  /// </summary>
  private void CheckCallback(string method, Node node, IrType source, IrFunctionType signature, IrType result) {
    var element = source.Element!;
    IrType[] passed = method switch {
      "reduce" or "reduceRight" => [result, element, IrType.I32, source],
      "sort" => [element, element],
      _ => [element, IrType.I32, source]
    };
    for (var index = 0; index < signature.Parameters.Count; ++index)
      if (!ArrayCallbacks.Converts(passed[index], signature.Parameters[index]))
        throw this.Unsupported(node, $"a callback of {method} whose parameter {index + 1} is {JsType.Spell(signature.Parameters[index])}, not {JsType.Spell(passed[index])}");

    var returned = signature.Result;
    var fits = method switch {
      "map" => ArrayCallbacks.Converts(returned, result.Element!),
      "filter" or "some" or "every" or "find" or "findIndex" => ArrayCallbacks.Truthy(new IrLocalRef(new IrLocal("r", returned))) is not null,
      "flatMap" => returned is { IsArray: true } or { IsList: true } && ArrayCallbacks.Converts(returned.Element!, result.Element!),
      "sort" => returned.IsNumeric,
      "forEach" => true,
      _ => ArrayCallbacks.Converts(returned, result)
    };
    if (!fits)
      throw this.Unsupported(node, $"a callback of {method} that returns {JsType.Spell(returned)}");
  }

  /// <summary>
  /// A comparator of <c>sort</c> has no effect: ECMA-262 leaves the order of its calls to the engine, so
  /// that an effect would differ between engines and the translation. It assigns to its own locals
  /// only and calls nothing; a function of the file as a comparator is refused, its body unseen here.
  /// </summary>
  private void CheckComparator(IrExpr comparator, Node node) {
    const string effects = "a sort comparator with effects, whose call order ECMA-262 leaves to the engine";
    if (comparator is not IrLambda lambda)
      throw this.Unsupported(node, effects);

    var own = new HashSet<IrLocal>(lambda.Parameters.Concat(lambda.Locals));
    var writes = IrClosures.Within(lambda.Body).Any(statement => statement switch {
      IrAssign assign => !own.Contains(assign.Local),
      IrStore or IrFieldStore or IrGlobalAssign => true,
      _ => false
    });
    var calls = IrTree.Nodes(lambda.Body).Any(expression => expression switch {
      IrCall or IrInvoke or IrMethodCall or IrNew => true,
      IrStep { Target: IrLocalRef reference } => !own.Contains(reference.Local),
      IrStep => true,
      IrCollection operation => !_reads.Contains(operation.Op),
      _ => false
    });
    if (writes || calls)
      throw this.Unsupported(node, effects);
  }

}
