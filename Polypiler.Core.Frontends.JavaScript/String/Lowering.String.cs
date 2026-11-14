using System.Collections.Generic;
using System.Linq;
using System.Text;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The strings of JavaScript: the constructs <see cref="JavaScriptStringApi"/> lists, each as the
/// operations of the IR it means. A JavaScript string is a sequence of UTF-16 code units, searched,
/// compared and cut by code unit; only <c>for…of</c> and <c>codePointAt</c> see code points. Where a
/// method clamps a position or answers <c>NaN</c> or <c>""</c> outside the string, the clamping and
/// the answer are spelled out; an operand read twice for that has to be free of effects.
/// </summary>
internal sealed partial class Lowering {

  private static readonly IrType _str = IrType.String;

  private static IrIntrinsic Str(IrIntrinsicOp op, IrType type, params IrExpr[] arguments) => new(op, type, arguments);

  private static IrConst I32(int value) => new(IrType.I32, value);

  private static IrExpr Min(IrExpr a, IrExpr b) => Str(IrIntrinsicOp.Min, IrType.I32, a, b);

  private static IrExpr Max(IrExpr a, IrExpr b) => Str(IrIntrinsicOp.Max, IrType.I32, a, b);

  /// <summary>A string expression, or the lowering of one: a literal, a template, an element, a method of a string, <c>String.fromCharCode</c>; <see langword="null"/> for anything else.</summary>
  private IrExpr? StringExpression(Node node) {
    switch (node) {
      case StringLiteral literal:
        return new IrStrConst(literal.Value);
      case TemplateLiteral template:
        return this.Template(template);
      case TaggedTemplateExpression tagged:
        throw this.Unsupported(tagged, $"the tagged template {types.Text(tagged.Tag)}");
      case RegExpLiteral expression:
        throw this.Unsupported(expression, "a regular expression");
      case MemberExpression member when types.Of(member.Object).Kind == JsKind.String:
        var text = this.Expression(member.Object);
        // s[i] is undefined outside the string, which no value of the IR is: the read traps there, as reading an array does.
        return member.Computed
          ? Str(IrIntrinsicOp.StrFromCodeUnit, _str, Str(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, text, this.Index(member.Property)))
          : member.Property is Identifier { Name: "length" } ? Str(IrIntrinsicOp.StrLength, IrType.I32, text) : throw this.Unsupported(member, types.Text(member));
      case CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: var method } } callee } call:
        var receiver = types.Of(callee.Object);
        if (receiver is { Kind: JsKind.Array, Element.Kind: JsKind.String } && method == "join")
          return this.Join(call, callee);

        if (receiver.Kind == JsKind.String)
          return this.Method(call, callee, method);

        return types.Names.Library(callee) is "String.fromCharCode" ? this.FromCharCode(call) : null;
      default:
        return null;
    }
  }

  /// <summary>A template literal: its cooked texts and its holes, each of which has to be a string, joined.</summary>
  private IrExpr Template(TemplateLiteral template) {
    var pieces = new List<IrExpr>();
    for (var index = 0; index < template.Quasis.Count; ++index) {
      if (template.Quasis[index].Value.Cooked is { Length: > 0 } cooked)
        pieces.Add(new IrStrConst(cooked));

      if (index < template.Expressions.Count)
        pieces.Add(this.Text(template.Expressions[index], "a template hole"));
    }

    return pieces.Count switch {
      0 => new IrStrConst(""),
      1 when pieces[0].Type.IsString => pieces[0],
      _ => Str(IrIntrinsicOp.StrConcat, _str, [.. pieces])
    };
  }

  /// <summary>A value that has to be a string; a number is refused as the conversion to text it would be.</summary>
  private IrExpr Text(Node node, string role) {
    var value = this.Evaluate(node);
    return value.Type.Kind switch {
      JsKind.String => value.Expr,
      JsKind.Number or JsKind.BigInt => throw this.Unsupported(node, $"{value.Type.Describe()} as {role}, {JavaScriptStringApi.NumberText}"),
      _ => throw this.Unsupported(node, $"{value.Type.Describe()} as {role}")
    };
  }

  /// <summary>Strings joined by <c>+</c>, flattened into one concatenation.</summary>
  private IrExpr Concatenation(Node left, Node right) {
    var pieces = new List<IrExpr>();
    void Add(Node node) {
      if (node is BinaryExpression { Operator: Operator.Addition } sum && types.Decide(Operator.Addition, sum.Left, sum.Right, Coercion.None).Plan == Plan.Concat && !this._substitutes.ContainsKey(node)) {
        Add(sum.Left);
        Add(sum.Right);
      } else {
        pieces.Add(this.Expression(node));
      }
    }

    Add(left);
    Add(right);
    return Str(IrIntrinsicOp.StrConcat, _str, [.. pieces]);
  }

  /// <summary>A comparison of two strings: equality by code unit, order by the sign of the ordinal comparison (ECMA-262 §7.2.13).</summary>
  private IrExpr StringComparison(Operator op, Node left, Node right) {
    var (first, second) = (this.Expression(left), this.Expression(right));
    return op switch {
      Operator.Equality or Operator.StrictEquality => Str(IrIntrinsicOp.StrEquals, IrType.Bool, first, second),
      Operator.Inequality or Operator.StrictInequality => new IrUnary(IrUnaryOp.LogicalNot, Str(IrIntrinsicOp.StrEquals, IrType.Bool, first, second)),
      _ => new IrBinary(Operation(op), Str(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, first, second), I32(0))
    };
  }

  /// <summary>An operand that is read twice: it has to be free of effects, so that reading it again changes nothing.</summary>
  private IrExpr Twice(IrExpr value, Node node, string method)
    => IrTree.Nodes(value).Any(part => part is IrCall or IrStep)
      ? throw this.Unsupported(node, $"{method} of an operand with an effect, which its translation reads twice")
      : value;

  /// <summary>
  /// A position a method clamps to the string: ToIntegerOrInfinity of the number - a NaN is 0, a
  /// fraction is cut off - as an <c>i32</c> that saturates; the method clamps it to the length.
  /// </summary>
  private IrExpr StringPosition(Node node) {
    var value = this.Evaluate(node);
    if (value.Type.Kind != JsKind.Number)
      throw this.Unsupported(node, $"{value.Type.Describe()} as a position");

    return value.Type.IsInteger && value.Type.Within(IrType.I32)
      ? Convert(value.Expr, IrType.I32)
      : new IrConvert(IrType.I32, this.Float(value, node), IrConversion.Saturate);
  }

  /// <summary>A position clamped to <c>[0, length]</c>.</summary>
  private static IrExpr Clamped(IrExpr position, IrExpr length) => Min(Max(position, I32(0)), length);

  /// <summary>A method of a string, as <see cref="JavaScriptStringApi"/> lists it; any other is refused by name.</summary>
  private IrExpr Method(CallExpression call, MemberExpression callee, string method) {
    var arguments = call.Arguments;
    if (arguments.Any(argument => argument is SpreadElement))
      throw this.Unsupported(call, "a spread argument");

    void Takes(int least, int most) {
      if (arguments.Count < least || arguments.Count > most)
        throw this.Unsupported(call, $"{method} with {arguments.Count} arguments");
    }

    var text = this.Expression(callee.Object);
    IrExpr Length() => Str(IrIntrinsicOp.StrLength, IrType.I32, this.Twice(text, call, method));
    IrExpr Needle(int index) => this.Needle(arguments[index], method);
    switch (method) {
      case "charCodeAt" or "charAt":
        Takes(0, 1);
        text = this.Twice(text, call, method);
        var at = arguments.Count == 0 ? I32(0) : this.Twice(this.Index(arguments[0]), call, method);
        var inside = new IrBinary(IrBinaryOp.LogicalAnd, new IrBinary(IrBinaryOp.GreaterOrEqual, at, I32(0)), new IrBinary(IrBinaryOp.Less, at, Length()));
        var unit = Str(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, text, at);
        // Outside the string charCodeAt is NaN and charAt the empty string.
        return method == "charCodeAt"
          ? new IrConditional(inside, new IrConvert(IrType.F64, unit), new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, double.NaN)))
          : new IrConditional(inside, Str(IrIntrinsicOp.StrFromCodeUnit, _str, unit), new IrStrConst(""));
      case "at":
        Takes(1, 1);
        text = this.Twice(text, call, method);
        var from = this.Twice(this.Index(arguments[0]), call, method);
        // A negative index counts from the end; outside the string at() is undefined, and the read traps there.
        var counted = new IrConditional(new IrBinary(IrBinaryOp.Less, from, I32(0)), new IrBinary(IrBinaryOp.Add, Length(), from), from);
        return Str(IrIntrinsicOp.StrFromCodeUnit, _str, Str(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, text, counted));
      case "codePointAt":
        Takes(1, 1);
        return Str(IrIntrinsicOp.StrCodePointAt, IrType.I32, text, this.Index(arguments[0]));
      case "substring":
        Takes(1, 2);
        text = this.Twice(text, call, method);
        var start = Clamped(this.StringPosition(arguments[0]), Length());
        if (arguments.Count == 1)
          return Str(IrIntrinsicOp.StrSubstring, _str, text, start);

        // substring takes the smaller of its two positions as the start: the two are swapped when they come in the other order.
        start = this.Twice(start, call, method);
        var end = this.Twice(Clamped(this.StringPosition(arguments[1]), Length()), call, method);
        return Str(IrIntrinsicOp.StrSubstring, _str, text, Min(start, end), new IrBinary(IrBinaryOp.Subtract, Max(start, end), Min(start, end)));
      case "slice":
        Takes(1, 2);
        text = this.Twice(text, call, method);
        IrExpr Relative(Node position) {
          var value = this.Twice(this.StringPosition(position), call, method);
          return new IrConditional(new IrBinary(IrBinaryOp.Less, value, I32(0)), Max(new IrBinary(IrBinaryOp.Add, Length(), value), I32(0)), Min(value, Length()));
        }

        var first = this.Twice(Relative(arguments[0]), call, method);
        var last = arguments.Count == 1 ? Length() : Relative(arguments[1]);
        return Str(IrIntrinsicOp.StrSubstring, _str, text, first, Max(new IrBinary(IrBinaryOp.Subtract, last, first), I32(0)));
      case "indexOf":
        Takes(1, 2);
        var needle = Needle(0);
        return arguments.Count == 1
          ? Str(IrIntrinsicOp.StrIndexOf, IrType.I32, text, needle)
          : Str(IrIntrinsicOp.StrIndexOf, IrType.I32, this.Twice(text, call, method), needle, Clamped(this.StringPosition(arguments[1]), Length()));
      case "lastIndexOf":
        Takes(1, 2);
        var sought = Needle(0);
        if (arguments.Count == 1)
          return Str(IrIntrinsicOp.StrLastIndexOf, IrType.I32, text, sought);

        // A position that is NaN means the end, which the saturation would make 0: only an integer is taken.
        var position = this.Evaluate(arguments[1]);
        if (!position.Type.IsInteger || !position.Type.Within(IrType.I32))
          throw this.Unsupported(arguments[1], $"lastIndexOf from {position.Type.Describe()}, which is no proven integer");

        return Str(IrIntrinsicOp.StrLastIndexOf, IrType.I32, this.Twice(text, call, method), sought, Clamped(Convert(position.Expr, IrType.I32), Length()));
      case "includes" or "startsWith" or "endsWith":
        Takes(1, 1);
        var op = method switch { "includes" => IrIntrinsicOp.StrContains, "startsWith" => IrIntrinsicOp.StrStartsWith, _ => IrIntrinsicOp.StrEndsWith };
        return Str(op, IrType.Bool, text, Needle(0));
      case "concat":
        return arguments.Count == 0 ? text : Str(IrIntrinsicOp.StrConcat, _str, [text, .. arguments.Select(argument => this.Text(argument, "an argument of concat"))]);
      case "replace" or "replaceAll":
        Takes(2, 2);
        return this.Replace(call, text, method);
      case "split":
        Takes(1, 2);
        // split takes its limit through ToUint32; one of 2^31 or more is as good as none, which -1 says.
        IrExpr limit = arguments.Count == 1 ? I32(-1) : new IrConvert(IrType.I32, this.Uint32(arguments[1]));
        return Str(IrIntrinsicOp.StrSplit, IrType.Array(_str), text, Needle(0), limit);
      case "trim" or "trimStart" or "trimEnd":
        Takes(0, 0);
        var trim = method switch { "trim" => IrIntrinsicOp.StrTrim, "trimStart" => IrIntrinsicOp.StrTrimStart, _ => IrIntrinsicOp.StrTrimEnd };
        return Str(trim, _str, text, StringOperations.Set(WhitespaceSet.Ecma));
      case "padStart" or "padEnd":
        Takes(1, 2);
        var length = this.StringPosition(arguments[0]);
        var filler = arguments.Count == 1 ? new IrStrConst(" ") : Needle(1);
        return Str(method == "padStart" ? IrIntrinsicOp.StrPadStart : IrIntrinsicOp.StrPadEnd, _str, text, length, filler);
      case "repeat":
        Takes(1, 1);
        return Str(IrIntrinsicOp.StrRepeat, _str, text, this.StringPosition(arguments[0]));
      case "localeCompare" or "toLocaleUpperCase" or "toLocaleLowerCase":
        throw this.Unsupported(call, $"{method}, which depends on the locale");
      case "toUpperCase" or "toLowerCase":
        throw this.Unsupported(call, $"{method}, which is case mapping");
      default:
        throw this.Unsupported(call, method);
    }
  }

  /// <summary>A needle or separator, which has to be a string; a regular expression is refused.</summary>
  private IrExpr Needle(Node node, string method)
    => node is RegExpLiteral ? throw this.Unsupported(node, $"{method} with a regular expression") : this.Text(node, $"an argument of {method}");

  /// <summary>
  /// <c>replace</c> and <c>replaceAll</c> with a string to find. The replacement is a pattern: its
  /// <c>$$</c> and <c>$&amp;</c> are spelled out here, which needs it to be a constant; a pattern that
  /// stands for a part of the string around the match is refused. <c>replaceAll</c> with an empty
  /// needle inserts between every two code units, which no operation does: it needs a needle that is
  /// a constant other than the empty string.
  /// </summary>
  private IrExpr Replace(CallExpression call, IrExpr text, string method) {
    var (needleNode, replacementNode) = (call.Arguments[0], call.Arguments[1]);
    var needle = this.Needle(needleNode, method);
    if (method == "replaceAll" && needle is not IrStrConst { Value.Length: > 0 })
      throw this.Unsupported(needleNode, "replaceAll with a needle that may be empty");

    var pattern = replacementNode switch {
      StringLiteral literal => literal.Value,
      TemplateLiteral { Expressions.Count: 0 } template => template.Quasis[0].Value.Cooked,
      _ => null
    } ?? throw this.Unsupported(replacementNode, $"{method} with a replacement that is no constant, which may hold a $ pattern");

    var pieces = new List<IrExpr>();
    var literalText = new StringBuilder();
    void Flush() {
      if (literalText.Length > 0)
        pieces.Add(new IrStrConst(literalText.ToString()));

      literalText.Clear();
    }

    for (var index = 0; index < pattern.Length; ++index) {
      if (pattern[index] != '$' || index + 1 == pattern.Length) {
        literalText.Append(pattern[index]);
        continue;
      }

      switch (pattern[index + 1]) {
        case '$':
          literalText.Append('$');
          ++index;
          break;
        case '&':
          Flush();
          pieces.Add(this.Twice(needle, call, method));
          ++index;
          break;
        case '`' or '\'' or >= '0' and <= '9' or '<':
          throw this.Unsupported(replacementNode, $"{method} with the pattern ${pattern[index + 1]} in its replacement");
        default:
          literalText.Append('$');
          break;
      }
    }

    Flush();
    IrExpr replacement = pieces.Count switch {
      0 => new IrStrConst(""),
      1 => pieces[0],
      _ => Str(IrIntrinsicOp.StrConcat, _str, [.. pieces])
    };
    return Str(method == "replace" ? IrIntrinsicOp.StrReplaceFirst : IrIntrinsicOp.StrReplaceAll, _str, text, needle, replacement);
  }

  /// <summary><c>join</c> on an array of strings, with the comma when no separator is given.</summary>
  private IrExpr Join(CallExpression call, MemberExpression callee) {
    if (call.Arguments.Count > 1)
      throw this.Unsupported(call, $"join with {call.Arguments.Count} arguments");

    var parts = this.Array(callee.Object);
    var separator = call.Arguments.Count == 0 ? new IrStrConst(",") : this.Text(call.Arguments[0], "the separator of join");
    return Str(IrIntrinsicOp.StrJoin, _str, separator, parts);
  }

  /// <summary><c>String.fromCharCode</c>: each argument through ToUint16, the code units joined.</summary>
  private IrExpr FromCharCode(CallExpression call) {
    var units = call.Arguments.Select(argument => (IrExpr)Str(IrIntrinsicOp.StrFromCodeUnit, _str, new IrConvert(IrType.U16, this.Uint32(argument)))).ToList();
    return units.Count switch {
      0 => new IrStrConst(""),
      1 => units[0],
      _ => Str(IrIntrinsicOp.StrConcat, _str, [.. units])
    };
  }

  /// <summary>
  /// <c>for (const ch of s)</c>: a loop over the code points of the string, each a string of one code
  /// unit or of a pair of surrogates. The position moves on before the body runs, so that a
  /// <c>continue</c> goes on with the next code point.
  /// </summary>
  private IrBlock ForOf(ForOfStatement loop) {
    if (loop.Left is not VariableDeclaration { Declarations: [{ Id: Identifier { Name: var name } id }] } || this._model.Bindings.GetValueOrDefault(id) is not Variable variable)
      throw this.Unsupported(loop.Left, "a for...of that does not declare one variable");

    if (types.Of(loop.Right).Kind != JsKind.String)
      throw this.Unsupported(loop.Right, $"a for...of over {types.Of(loop.Right).Describe()}");

    var statements = new List<IrStmt>();
    var text = this.Once(this.Expression(loop.Right), statements);
    var at = new IrLocal($"$t{this._locals.Count}", IrType.I32);
    this._locals.Add(at);
    statements.Add(new IrDeclare(at, I32(0)));
    var width = new IrLocal($"$t{this._locals.Count}", IrType.I32);
    this._locals.Add(width);
    var local = new IrLocal(name, _str);
    variable.Local = local;
    this._locals.Add(local);
    var before = this._assigned;
    var body = this.Body(loop.Body);
    this._assigned = before;
    IrStmt[] pass = [
      new IrDeclare(width, Str(IrIntrinsicOp.StrNextCodePoint, IrType.I32, text, new IrLocalRef(at))),
      new IrDeclare(local, Str(IrIntrinsicOp.StrSubstring, _str, text, new IrLocalRef(at), new IrLocalRef(width))),
      new IrAssign(at, new IrBinary(IrBinaryOp.Add, new IrLocalRef(at), new IrLocalRef(width)))
    ];
    statements.Add(new IrWhile(new IrBinary(IrBinaryOp.Less, new IrLocalRef(at), Str(IrIntrinsicOp.StrLength, IrType.I32, text)), new IrBlock([.. pass, .. body.Statements])));
    return new IrBlock(statements);
  }

}
