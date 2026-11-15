using Acornima;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// Numbers and truth values to text and back by the rules of ECMA-262: the constructs
/// <see cref="JavaScriptNumberTextApi"/> lists. A number joined to text, a template hole,
/// <c>String(x)</c> and <c>x.toString()</c> are <c>Number::toString</c>; <c>toString(radix)</c> of a
/// proven integer writes its digits with a minus sign; <c>toFixed</c> rounds the exact value, a tie
/// up; <c>Number(s)</c>, <c>+s</c>, <c>parseFloat</c> and <c>parseInt</c> parse as ECMA-262 does. What
/// depends on the locale or a format of its own - <c>toPrecision</c>, <c>toExponential</c>,
/// <c>toLocaleString</c> - is refused by name.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>Whether a call is of a global function of a name that no local, constant or function of the file hides.</summary>
  private bool IsGlobal(CallExpression call, string name)
    => call.Callee is Identifier identifier && identifier.Name == name && !this._model.Bindings.ContainsKey(identifier) && types.Names.Callee(identifier) is null;

  /// <summary>
  /// A value as the text JavaScript makes of it: a string as it is, a number or a <c>BigInt</c> by
  /// <c>Number::toString</c> - an integer the program proves as that integer - and a truth value as
  /// <c>true</c> or <c>false</c>.
  /// </summary>
  private IrExpr Written(Node node, string role) {
    var value = this.Evaluate(node);
    switch (value.Type.Kind) {
      case JsKind.String:
        return value.Expr;
      case JsKind.Boolean:
        return new IrIntrinsic(IrIntrinsicOp.FormatBoolEcma, _str, [value.Expr]);
      case JsKind.Number or JsKind.BigInt:
        return new IrIntrinsic(IrIntrinsicOp.FormatEcma, _str, [value.Expr.Type.IsInteger ? value.Expr : this.Float(value, node)]);
      default:
        throw this.Unsupported(node, $"{value.Type.Describe()} as {role}");
    }
  }

  /// <summary>A string a parser takes; any other value is refused, which JavaScript would convert to text first.</summary>
  private IrExpr Parsed(Node node, string function) {
    var value = this.Evaluate(node);
    return value.Type.Kind == JsKind.String ? value.Expr : throw this.Unsupported(node, $"{function} of {value.Type.Describe()}");
  }

  /// <summary>The value of a numeric literal argument, or <see langword="null"/> for any other argument.</summary>
  private static double? Literal(Node node) => node is NumericLiteral literal ? literal.Value : null;

  /// <summary>A conversion between a number and text, or <see langword="null"/> for an expression that is none.</summary>
  private IrExpr? NumberTextExpression(Node node) {
    switch (node) {
      case UnaryExpression { Operator: Operator.UnaryPlus } plus when types.Of(plus.Argument).Kind == JsKind.String:
        return new IrIntrinsic(IrIntrinsicOp.ParseNumberEcma, IrType.F64, [this.Expression(plus.Argument)]);
      case CallExpression call when this.IsGlobal(call, "String"):
        return call.Arguments.Count == 1 ? this.Written(call.Arguments[0], "the argument of String") : throw this.Unsupported(call, $"String with {call.Arguments.Count} arguments");
      case CallExpression call when this.IsGlobal(call, "Number") && call.Arguments.Count == 1 && types.Of(call.Arguments[0]).Kind == JsKind.String:
        return new IrIntrinsic(IrIntrinsicOp.ParseNumberEcma, IrType.F64, [this.Expression(call.Arguments[0])]);
      case CallExpression call when this.IsGlobal(call, "parseFloat"):
        return call.Arguments.Count == 1
          ? new IrIntrinsic(IrIntrinsicOp.ParseFloatEcma, IrType.F64, [this.Parsed(call.Arguments[0], "parseFloat")])
          : throw this.Unsupported(call, $"parseFloat with {call.Arguments.Count} arguments");
      case CallExpression call when this.IsGlobal(call, "parseInt"):
        return this.ParseInt(call);
      case CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: var method } } callee } call
        when method is "toString" or "toFixed" or "toPrecision" or "toExponential" or "toLocaleString" && types.Of(callee.Object).Kind is JsKind.Number or JsKind.BigInt or JsKind.Boolean:
        return this.NumberMethod(call, callee, method);
      default:
        return null;
    }
  }

  /// <summary><c>parseInt(s)</c> and <c>parseInt(s, radix)</c> with a literal radix, which ToInt32 takes as it is.</summary>
  private IrExpr ParseInt(CallExpression call) {
    if (call.Arguments.Count is < 1 or > 2)
      throw this.Unsupported(call, $"parseInt with {call.Arguments.Count} arguments");

    var text = this.Parsed(call.Arguments[0], "parseInt");
    if (call.Arguments.Count == 1)
      return new IrIntrinsic(IrIntrinsicOp.ParseIntEcma, IrType.F64, [text]);

    return Literal(call.Arguments[1]) is { } radix && radix == System.Math.Truncate(radix) && System.Math.Abs(radix) <= int.MaxValue
      ? new IrIntrinsic(IrIntrinsicOp.ParseIntEcma, IrType.F64, [text, I32((int)radix)])
      : throw this.Unsupported(call.Arguments[1], "parseInt with a radix that is no integer literal");
  }

  /// <summary>A method of a number, a <c>BigInt</c> or a truth value that makes text of it.</summary>
  private IrExpr NumberMethod(CallExpression call, MemberExpression callee, string method) {
    var arguments = call.Arguments;
    var kind = types.Of(callee.Object).Kind;
    switch (method) {
      case "toString" when arguments.Count == 0 || (arguments.Count == 1 && Literal(arguments[0]) == 10):
        return this.Written(callee.Object, "the receiver of toString");
      case "toString" when arguments.Count == 1 && kind != JsKind.Boolean:
        if (Literal(arguments[0]) is not { } radix || radix != System.Math.Truncate(radix) || radix is < 2 or > 36)
          throw this.Unsupported(arguments[0], "Number.prototype.toString with a radix that is no integer literal from 2 to 36");

        var value = this.Evaluate(callee.Object);
        // A literal integer is the integer it is: (255).toString(16).
        if (value.Expr is IrConst { Type.IsFloat: true } constant && IrOperators.Float(constant.Type, constant.Value) is var number
            && number == System.Math.Truncate(number) && System.Math.Abs(number) <= int.MaxValue)
          value = value with { Expr = I32((int)number) };

        return value.Expr.Type.IsInteger
          ? NumberTextOperations.Radix(value.Expr, (int)radix, 0, false, RadixMode.SignMagnitude)
          : throw this.Unsupported(call, "toString with a radix of a number with a fraction");
      case "toFixed" when kind == JsKind.Number && arguments.Count <= 1:
        var digits = arguments.Count == 0 ? 0 : Literal(arguments[0]);
        if (digits is not { } count || count != System.Math.Truncate(count) || count is < 0 or > NumberTextOperations.MaxEcmaDigits)
          throw this.Unsupported(arguments[0], $"toFixed with digits that are no integer literal from 0 to {NumberTextOperations.MaxEcmaDigits}");

        return NumberTextOperations.Fixed(IrIntrinsicOp.FixedEcma, this.Float(callee.Object), (int)count);
      default:
        throw this.Unsupported(call, $"{method}{(method is "toPrecision" or "toExponential" or "toLocaleString" ? ", which is a format of its own" : $" with {arguments.Count} arguments")}");
    }
  }

}
