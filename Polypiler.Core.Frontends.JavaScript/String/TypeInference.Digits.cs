using System;
using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

/// <summary>
/// The range of <c>parseInt(text, radix)</c> where the program proves the text short and of digits
/// only (#1413): a text of at most <c>n</c> characters, each a digit of the radix, is no number or an
/// integer from 0 to <c>radix^n - 1</c>. The text is a string literal, or <c>s.substr(i, n)</c> of a
/// string <c>s</c> that a guard at the top of the function tests against <c>/^[class]*$/</c> and leaves
/// by a throw or a return when it fails - <c>s</c> a parameter or a constant nothing assigns to.
/// No sign, no white space and no <c>0x</c> prefix gets past such a guard; an empty text, which
/// <c>parseInt</c> makes a NaN, does, and so the range is one of whole doubles.
/// </summary>
internal sealed partial class TypeInference {

  /// <summary>The largest bound a range of whole doubles takes: every integer up to it is a double exactly.</summary>
  private static readonly Int128 _exact = (Int128)1 << 53;

  /// <summary>The whole doubles <c>parseInt</c> yields of a short text of proven digits, or <see langword="null"/> where no proof applies.</summary>
  private JsType? ParsedRange(CallExpression call) {
    if (call.Arguments is not [var text, NumericLiteral { Value: var radix }] || radix != Math.Truncate(radix) || radix is < 2 or > 36
        || this.Digits(text) is not var (length, alphabet) || length == 0 || !alphabet.All(digit => IsDigit(digit, (int)radix)))
      return null;

    Int128 bound = 1;
    for (var count = 0; count < length; ++count)
      if ((bound *= (int)radix) > _exact)
        return null;

    return JsType.Whole(0, bound - 1);
  }

  /// <summary>Whether a character is a digit of a radix, as <c>parseInt</c> reads it: 0 to 9, then the letters of either case.</summary>
  private static bool IsDigit(char digit, int radix) {
    var value = digit is >= '0' and <= '9' ? digit - '0' : char.ToLowerInvariant(digit) is >= 'a' and <= 'z' and var letter ? letter - 'a' + 10 : int.MaxValue;
    return value < radix;
  }

  /// <summary>The longest a text can be and the characters it is made of, or <see langword="null"/> where neither is proven.</summary>
  private (int Length, IReadOnlySet<char> Alphabet)? Digits(Expression text) {
    switch (text) {
      case StringLiteral literal:
        return (literal.Value.Length, literal.Value.ToHashSet());
      case CallExpression { Callee: MemberExpression { Computed: false, Object: var whole, Property: Identifier { Name: "substr" } }, Arguments: [_, NumericLiteral { Value: var count }] }
          when count == Math.Truncate(count) && count is >= 0 and <= int.MaxValue && this.Of(whole).Kind == JsKind.String && this.Alphabet(whole) is { } alphabet:
        return ((int)count, alphabet);
      default:
        return null;
    }
  }

  /// <summary>
  /// The characters a guard proves a string made of where it is read, or <see langword="null"/>: the
  /// string is a variable nothing assigns to, and a statement of the function's body before the read
  /// is <c>if (!/^[class]*$/.test(s)) throw …</c> or <c>return</c>.
  /// </summary>
  private IReadOnlySet<char>? Alphabet(Expression text) {
    if (text is not Identifier name || this._scope is not { Candidate.Function.Body: FunctionBody body } scope || scope.Bindings.GetValueOrDefault(name) is not Variable variable || !this.IsFixed(variable))
      return null;

    foreach (var statement in body.Body.TakeWhile(statement => statement.End <= text.Start))
      if (statement is IfStatement {
            Test: UnaryExpression { Operator: Operator.LogicalNot, Argument: CallExpression { Callee: MemberExpression { Computed: false, Object: RegExpLiteral pattern, Property: Identifier { Name: "test" } }, Arguments: [Identifier tested] } },
            Alternate: null, Consequent: var failed }
          && scope.Bindings.GetValueOrDefault(tested) == variable && !Completes.MayComplete([failed]) && Alphabets.Of(pattern.RegExp.Pattern, pattern.RegExp.Flags) is { } alphabet)
        return alphabet;

    return null;
  }

  /// <summary>
  /// Makes the Array or the list a variable holds one of the integer elements a place JSDoc states,
  /// where every whole double it holds fits them: the place it reaches is the same array, of one type.
  /// Says whether the type of the variable changed.
  /// </summary>
  private bool Fit(FunctionModel model, Node source, JsType? place) {
    if (place is not { Kind: JsKind.Array, Element: { AdmitsWhole: true } element } || source is not Identifier name
        || model.Bindings.GetValueOrDefault(name) is not Variable { Type: { Kind: JsKind.Array, IsPlain: true, IsView: false, Element.IsWhole: true } held } variable
        || JsType.Join(element, held.Element!) != element)
      return false;

    return this.Reach(variable, held.IsGrowable ? JsType.ListOf(element) : JsType.ArrayOf(element, true), source);
  }

  /// <summary>Whether a variable keeps the value it starts with: a parameter no function of the file assigns, or a constant.</summary>
  private bool IsFixed(Variable variable)
    => variable.IsConstant || (variable.IsParameter && !this._models.Values.Any(model => model.Flows.Exists(flow => flow.Target == variable && flow.Kind is FlowKind.Assign or FlowKind.Compound or FlowKind.Step or FlowKind.Field)));

}
