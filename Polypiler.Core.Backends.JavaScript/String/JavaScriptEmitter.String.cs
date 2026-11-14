using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// A string operation. A JavaScript string is a sequence of UTF-16 code units, searched, compared
    /// and cut by code unit, so most operations are the method of the same meaning; a code unit is a
    /// number and becomes a string with <c>String.fromCharCode</c> where a method takes a string. Where
    /// the IR traps and JavaScript would clamp or return a NaN or <c>undefined</c> - an index or range
    /// outside the string, a negative count, an empty needle to replace - a helper checks first; what
    /// a level lacks is a helper written with what ECMAScript 5 has.
    /// </summary>
    private (JsExpression Node, bool Atomic) StringOperation(IrIntrinsic intrinsic) {
      var arguments = intrinsic.Arguments;
      JsExpression Arg(int index) => this.Text(arguments[index]);
      JsExpression Target() => this.Operand(arguments[0]);
      JsCall Method(string name, params JsExpression[] values) {
        this.Native("." + name);
        return new JsCall(new JsMember(Target(), name), values);
      }

      switch (intrinsic.Op) {
        case IrIntrinsicOp.StrLength:
          return (new JsMember(Target(), "length"), true);
        case IrIntrinsicOp.StrCodeUnitAt:
          return (this.Helper("$unitAt", arguments[0], arguments[1]), true);
        case IrIntrinsicOp.StrConcat:
          var sum = this.Operand(arguments[0]);
          for (var index = 1; index < arguments.Count; ++index)
            sum = Infix(sum, "+", this.Operand(arguments[index]));

          return (sum, false);
        case IrIntrinsicOp.StrFromCodeUnit:
          return (this.Unit(arguments[0]), true);
        case IrIntrinsicOp.StrFromCodeUnits when arguments.Count == 1:
          return (this.Helper("$fromAllUnits", arguments[0]), true);
        case IrIntrinsicOp.StrFromCodeUnits:
          return (this.Helper("$fromUnits", arguments[0], arguments[1], arguments[2]), true);
        case IrIntrinsicOp.StrToCodeUnits:
          return (this.Helper("$toUnits", arguments[0]), true);
        case IrIntrinsicOp.StrRepeat:
          return (this.Helper("$repeat", [this.Needle(arguments[0]), Arg(1)]), true);
        case IrIntrinsicOp.StrSubstring when arguments.Count == 2:
          return (this.Helper("$substrFrom", arguments[0], arguments[1]), true);
        case IrIntrinsicOp.StrSubstring:
          return (this.Helper("$substr", arguments[0], arguments[1], arguments[2]), true);
        case IrIntrinsicOp.StrEquals:
          return (Infix(Target(), "===", this.Operand(arguments[1])), false);
        case IrIntrinsicOp.StrCompareOrdinal:
          // Relational comparison of two strings is ordinal by code unit (ECMA-262 §7.2.13); each operand is read once.
          return Once(arguments[0]) && Once(arguments[1])
            ? (new JsConditional(Infix(Target(), "<", this.Operand(arguments[1])), Number("-1"),
                new JsConditional(Infix(Target(), ">", this.Operand(arguments[1])), Number("1"), Number("0"))), false)
            : (this.Helper("$cmp", arguments[0], arguments[1]), true);
        case IrIntrinsicOp.StrIndexOf when arguments.Count == 2 || arguments[2] is IrConst { Value: 0 }:
          return (Method("indexOf", this.Needle(arguments[1])), true);
        case IrIntrinsicOp.StrIndexOf:
          return (this.Helper("$indexOf", [Arg(0), this.Needle(arguments[1]), Arg(2)]), true);
        case IrIntrinsicOp.StrLastIndexOf when arguments.Count == 2:
          return (Method("lastIndexOf", this.Needle(arguments[1])), true);
        case IrIntrinsicOp.StrLastIndexOf when arguments[2] is IrIntrinsic { Op: IrIntrinsicOp.StrLength } length && Same(length.Arguments[0], arguments[0]):
          return (Method("lastIndexOf", this.Needle(arguments[1])), true);
        case IrIntrinsicOp.StrLastIndexOf:
          return (this.Helper("$lastIndexOf", [Arg(0), this.Needle(arguments[1]), Arg(2)]), true);
        case IrIntrinsicOp.StrStartsWith:
          return this.Native(".startsWith") ? (Method("startsWith", this.Needle(arguments[1])), true) : (this.Helper("$startsWith", [Arg(0), this.Needle(arguments[1])]), true);
        case IrIntrinsicOp.StrEndsWith:
          return this.Native(".endsWith") ? (Method("endsWith", this.Needle(arguments[1])), true) : (this.Helper("$endsWith", [Arg(0), this.Needle(arguments[1])]), true);
        case IrIntrinsicOp.StrContains:
          if (this.Native(".includes"))
            return (Method("includes", this.Needle(arguments[1])), true);

          return (Infix(Method("indexOf", this.Needle(arguments[1])), "!==", Number("-1")), false);
        case IrIntrinsicOp.StrReplaceAll:
          return this.ReplaceAll(intrinsic);
        case IrIntrinsicOp.StrReplaceFirst when arguments[1] is IrStrConst && arguments[2] is IrStrConst replacement:
          // A replacement given as a string still has its patterns ($&, $1, …): every $ is written as $$, which stands for one.
          return (Method("replace", Arg(1), new JsString(replacement.Value.Replace("$", "$$", System.StringComparison.Ordinal))), true);
        case IrIntrinsicOp.StrReplaceFirst:
          return (this.Helper("$replaceFirst", arguments[0], arguments[1], arguments[2]), true);
        case IrIntrinsicOp.StrSplit:
          // split takes its limit through ToUint32, which makes a negative one larger than any number of parts.
          return arguments[2] is IrConst { Value: < 0 } ? (Method("split", this.Needle(arguments[1])), true) : (Method("split", this.Needle(arguments[1]), Arg(2)), true);
        case IrIntrinsicOp.StrJoin:
          this.Native(".join");
          return (new JsCall(new JsMember(this.Operand(arguments[1]), "join"), [Arg(0)]), true);
        case IrIntrinsicOp.StrTrim or IrIntrinsicOp.StrTrimStart or IrIntrinsicOp.StrTrimEnd:
          return (this.Trimmed(intrinsic), true);
        case IrIntrinsicOp.StrPadStart or IrIntrinsicOp.StrPadEnd:
          var start = intrinsic.Op == IrIntrinsicOp.StrPadStart;
          return this.Native(start ? ".padStart" : ".padEnd")
            ? (Method(start ? "padStart" : "padEnd", Arg(1), Arg(2)), true)
            : (this.Helper("$pad", [Arg(0), Arg(1), Arg(2), new JsBoolean(start)]), true);
        case IrIntrinsicOp.StrCodePointAt:
          return (this.Helper("$codePointAt", arguments[0], arguments[1]), true);
        default:
          return (this.Helper("$nextCodePoint", arguments[0], arguments[1]), true);
      }
    }

    /// <summary>A code unit as the string of it.</summary>
    private JsCall Unit(IrExpr unit) {
      this.Native("String.fromCharCode");
      return Invoke("String.fromCharCode", this.Text(unit));
    }

    /// <summary>A needle, separator or repeated value: a string as it is, a code unit as the string of it.</summary>
    private JsExpression Needle(IrExpr value) => value.Type.IsString ? this.Text(value) : this.Unit(value);

    /// <summary>Whether two operands are one variable or one constant, which reads the same value twice.</summary>
    private static bool Same(IrExpr first, IrExpr second) => (first, second) switch {
      (IrLocalRef left, IrLocalRef right) => ReferenceEquals(left.Local, right.Local),
      (IrStrConst left, IrStrConst right) => left.Value == right.Value,
      _ => false
    };

    /// <summary>Whether an operand can be read twice without being evaluated twice: a variable or a constant.</summary>
    private static bool Once(IrExpr value) => value is IrLocalRef or IrStrConst or IrConstRef;

    /// <summary>
    /// Every occurrence replaced. The parts between the occurrences joined by the replacement are the
    /// replacement of every occurrence, without the patterns <c>replace</c> gives <c>$</c>; ECMAScript
    /// 2021's <c>replaceAll</c> stands where needle and replacement are constants, its <c>$</c> written
    /// as <c>$$</c>. A needle that may be empty, on which the IR traps, goes through the helper.
    /// </summary>
    private (JsExpression Node, bool Atomic) ReplaceAll(IrIntrinsic intrinsic) {
      var (text, needle, replacement) = (intrinsic.Arguments[0], intrinsic.Arguments[1], intrinsic.Arguments[2]);
      if (needle.Type.IsString && needle is not IrStrConst { Value.Length: > 0 })
        return (this.Helper("$replaceAll", text, needle, replacement), true);

      if (needle is IrStrConst && replacement is IrStrConst constant && this._capabilities.Has(".replaceAll", this._profile))
        return (new JsCall(new JsMember(this.Operand(text), "replaceAll"), [this.Text(needle), new JsString(constant.Value.Replace("$", "$$", System.StringComparison.Ordinal))]), true);

      this.Native(".split");
      this.Native(".join");
      var parts = new JsCall(new JsMember(this.Operand(text), "split"), [this.Needle(needle)]);
      return (new JsCall(new JsMember(parts, "join"), [this.Needle(replacement)]), true);
    }

    /// <summary>A trim: ECMA-262's white space with the method of the level or the helper below it, .NET's with the helper of its table.</summary>
    private JsExpression Trimmed(IrIntrinsic intrinsic) {
      var (method, start, end) = intrinsic.Op switch {
        IrIntrinsicOp.StrTrim => ("trim", true, true),
        IrIntrinsicOp.StrTrimStart => ("trimStart", true, false),
        _ => ("trimEnd", false, true)
      };
      if (StringOperations.SetOf(intrinsic) == WhitespaceSet.Ecma && this.Native("." + method))
        return new JsCall(new JsMember(this.Operand(intrinsic.Arguments[0]), method), []);

      var helper = StringOperations.SetOf(intrinsic) == WhitespaceSet.Ecma ? "$trimEcma" : "$trimNet";
      return this.Helper(helper, [this.Text(intrinsic.Arguments[0]), new JsBoolean(start), new JsBoolean(end)]);
    }

  }

}
