using System.Linq;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

/// <summary>
/// The types of JavaScript's array callbacks (#1329): the callback of <c>map</c>, <c>filter</c>,
/// <c>forEach</c>, <c>some</c>, <c>every</c>, <c>find</c>, <c>findIndex</c> and <c>flatMap</c> takes
/// the element, its index and the array; that of <c>reduce</c> and <c>reduceRight</c> the accumulator
/// before them; a comparator of <c>sort</c> two elements. What each method yields follows from the
/// result of its callback.
/// </summary>
internal sealed partial class TypeInference {

  /// <summary>The methods of an array that call a function given as their first argument.</summary>
  public static bool IsCallbackMethod(string method)
    => method is "map" or "filter" or "forEach" or "some" or "every" or "find" or "findIndex" or "flatMap" or "reduce" or "reduceRight" or "sort";

  /// <summary>The array a call of a callback method is made on, with the method; <see langword="null"/> for any other call.</summary>
  private (JsType Array, string Method)? CallbackCall(CallExpression call)
    => call.Callee is MemberExpression { Computed: false, Property: Identifier { Name: var method } } member && IsCallbackMethod(method)
      && this.Of(member.Object) is { Kind: JsKind.Array, IsView: false, Element: not null } array
      ? (array, method) : null;

  /// <summary>An element as a callback receives it: a binary32 element is read as the double it is.</summary>
  private static JsType Read(JsType array) => array.Element!.Float == 32 ? JsType.F64 : array.Element!;

  /// <summary>What the callback of a call returns, joined over every function its value may be; unknown for no function.</summary>
  private JsType CallbackResult(CallExpression call)
    => call.Arguments.Count > 0 && this.Of(call.Arguments[0]) is { Kind: JsKind.Function } function
      ? function.Callable!.Members.Select(member => this.ResultOf(this.ModelOf(member))).Aggregate(JsType.Join)
      : JsType.Unknown;

  /// <summary>The accumulator of <c>reduce</c>: the initial value, or the first element, joined with what the callback returns.</summary>
  private JsType Accumulator(CallExpression call, JsType array)
    => JsType.Join(call.Arguments.Count > 1 ? this.Of(call.Arguments[1]) : Read(array), this.CallbackResult(call));

  /// <summary>The type of a call of a callback method, or <see langword="null"/> for any other call.</summary>
  private JsType? CallbackType(CallExpression call) {
    if (this.CallbackCall(call) is not var (array, method))
      return null;

    return method switch {
      "map" => JsType.ArrayOf(this.CallbackResult(call), true),
      "filter" => JsType.ArrayOf(array.Element!, true),
      "flatMap" => JsType.ArrayOf(this.CallbackResult(call) is { Kind: JsKind.Array, Element: { } inner } ? inner : JsType.Unknown, true),
      "forEach" => JsType.UndefinedValue,
      "some" or "every" => JsType.Boolean,
      "find" => JsType.Join(Read(array), JsType.UndefinedValue),
      "findIndex" => JsType.I32,
      "sort" => array,
      _ => this.Accumulator(call, array)
    };
  }

  /// <summary>
  /// Lets a callback of an array method take what the method passes it: the element, its index and
  /// the array - the accumulator before them for <c>reduce</c>, two elements for a comparator of
  /// <c>sort</c> - as far as its parameters reach. Says whether a type changed.
  /// </summary>
  private bool Callback(CallExpression call, int index) {
    if (index != 0 || this.CallbackCall(call) is not var (array, method) || this.Of(call.Arguments[0]) is not { Kind: JsKind.Function } function)
      return false;

    var element = Read(array);
    JsType[] passed = method switch {
      "reduce" or "reduceRight" => [this.Accumulator(call, array), element, JsType.I32, array],
      "sort" => [element, element],
      _ => [element, JsType.I32, array]
    };
    var changed = false;
    foreach (var member in function.Callable!.Members.ToList()) {
      var parameters = this.ModelOf(member).Parameters;
      for (var at = 0; at < parameters.Count && at < passed.Length; ++at)
        if (parameters[at] is { Declared: null } parameter)
          changed |= this.Pass(parameter, passed[at], call.Arguments[0]);
    }

    return changed;
  }

}
