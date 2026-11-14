using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>The expression as it stands on its own: as a statement, an argument or a right-hand side.</summary>
    private JsExpression Text(IrExpr expression) => this.Expression(expression).Node;

    /// <summary>The expression as an operand: in parentheses unless it already binds tighter than any operator.</summary>
    private JsExpression Operand(IrExpr expression) => JavaScriptEmitter.Operand(this.Expression(expression));

    private (JsExpression Node, bool Atomic) Expression(IrExpr expression) {
      var outer = this._construct;
      this._construct = expression;
      var (node, atomic) = this.Write(expression);
      this._construct = outer;
      return (node.Type is null ? node with { Type = expression.Type } : node, atomic);
    }

    private (JsExpression Node, bool Atomic) Write(IrExpr expression) {
      switch (expression) {
        case IrConst constant:
          return this.Constant(constant.Type, constant.Value);
        case IrConstRef reference:
          return (Id(this.Name(reference.Constant.Name)), true);
        case IrStrConst text:
          return (new JsString(text.Value), true);
        case IrIntrinsic intrinsic:
          return this.Intrinsic(intrinsic);
        case IrLocalRef reference:
          return (this.Variable(reference.Local), true);
        case IrGlobalRef reference:
          return (this.GlobalName(reference.Global), true);
        case IrUnary unary:
          return this.Unary(unary);
        case IrBinary { Left: IrIntrinsic { Op: IrIntrinsicOp.StrCompareOrdinal } ordinal, Right: IrConst { Value: 0 } } binary:
          // The sign of an ordinal comparison against 0 is the relational comparison of the strings, by code unit (ECMA-262 §7.2.13).
          return (Infix(this.Operand(ordinal.Arguments[0]), Sign(binary.Op), this.Operand(ordinal.Arguments[1])), false);
        case IrBinary binary:
          var operands = binary.Left.Type;
          return operands.IsFloat ? this.BinaryFloat(binary)
            : !operands.IsLong ? this.Binary(binary)
            : this.Halves ? this.BinaryHalves(binary)
            : this.Binary64(binary);
        case IrConvert conversion:
          return this.Convert(conversion);
        case IrConditional choice:
          var test = this.Operand(choice.Condition);
          var whenTrue = this.Operand(choice.WhenTrue);
          return (new JsConditional(test, whenTrue, this.Operand(choice.WhenFalse)), false);
        case IrArrayLoad load when this.Plain(load.Array):
          return (this.ViewElement(load), true);
        case IrArrayLoad load:
          return (this.Helper("$get", load.Array, load.Index), true);
        case IrArrayLength length:
          return (new JsMember(this.Operand(length.Array), "length"), true);
        case IrNewArray creation:
          var element = creation.Type.Element!;
          var count = this.Helper("$len", creation.Length);
          if (element.StructType is { } elements)
            return (Invoke(this.StructHelper("newArray", elements), count), true);

          return (this.Native(Storage(element)) ? new JsNew(Id(Storage(element)), [count]) : this.Helper("$new", [count, this.Zero(element)]), true);
        case IrStep step:
          return this.Step(step);
        case IrStructNew or IrStructDefault or IrFieldLoad or IrStructEquals:
          return this.Struct(expression);
        default:
          var call = (IrCall)expression;
          return (new JsCall(Id(this.Name(call.Function)), [.. call.Arguments.Select(this.Value)]), true);
      }
    }

    private (JsExpression Node, bool Atomic) Intrinsic(IrIntrinsic intrinsic) {
      if (this.View(intrinsic) is { } view)
        return view;

      var arguments = intrinsic.Arguments;
      switch (intrinsic.Op) {
        case IrIntrinsicOp.ArrayOf when intrinsic.Type.Element!.IsStruct || intrinsic.Type.Element.IsString:
          // Typed arrays hold numbers: an array of structs or of strings is an Array of objects.
          return (new JsArray([.. arguments.Select(this.Value)]), true);
        case IrIntrinsicOp.ArrayOf:
          return (this.ArrayOf(intrinsic.Type.Element!, [.. arguments.Select(this.Text)]), true);
        case IrIntrinsicOp.ArrayCopy:
          var typed = this.Native(Storage(arguments[0].Type.Element!)) && this.Native(".set") && this.Native(".subarray");
          return (this.Helper(typed ? "$copy" : "$copya", arguments.ToArray()), true);
        case IrIntrinsicOp.ArrayFill:
          return (this.Fill(arguments[0], this.Text(arguments[1])), true);
        case IrIntrinsicOp.ArrayClear:
          return (this.Fill(arguments[0], this.Zero(arguments[0].Type.Element!)), true);
      }

      if (StringOperations.IsString(intrinsic.Op))
        return this.StringOperation(intrinsic);

      // The integer and bit operations, also those defined on floating-point numbers as well, are written by their families.
      if (this.Integer(intrinsic) is { } integer)
        return integer;

      var type = arguments[0].Type;
      var all = arguments.Select(this.Text).ToList();
      if (this.Floating(intrinsic, all) is { } floating)
        return floating;

      switch (intrinsic.Op) {
        case IrIntrinsicOp.Abs:
          return (Invoke("Math.abs", all), true);
        case IrIntrinsicOp.Min:
          return (Invoke("Math.min", all), true);
        case IrIntrinsicOp.Max:
          return (Invoke("Math.max", all), true);
        case IrIntrinsicOp.Sign:
          return (this.Helper("$fsign", all), true);
        case IrIntrinsicOp.Sqrt:
          return (type == IrType.F32 ? this.Fround(Invoke("Math.sqrt", all)) : Invoke("Math.sqrt", all), true);
        case IrIntrinsicOp.Floor:
          return (Invoke("Math.floor", all), true);
        case IrIntrinsicOp.Ceiling:
          return (Invoke("Math.ceil", all), true);
        case IrIntrinsicOp.Truncate:
          return (this.Native("Math.trunc") ? Invoke("Math.trunc", all) : this.Helper("$trunc", all), true);
        case IrIntrinsicOp.RoundEven:
          return (this.Helper("$round", all), true);
        case IrIntrinsicOp.RoundAway:
          return (this.Helper("$rounda", all), true);
        case IrIntrinsicOp.FloatBits:
          return (this.Helper(type == IrType.F64 ? "$bits64" : "$bits32", all), true);
        case IrIntrinsicOp.RoundHalfUp:
          // The result is integral, and an integral number that a binary32 operand rounds to is a binary32 number.
          this.Native("Math.round");
          return (Invoke("Math.round", all), true);
        case IrIntrinsicOp.ToInt32:
          this.Native("| 0");
          return (Infix(this.Operand(arguments[0]), "|", Number("0")), false);
        case IrIntrinsicOp.ToUint32:
          this.Native(">>> 0");
          return (Infix(this.Operand(arguments[0]), ">>>", Number("0")), false);
        default:
          return (this.Helper(type.IsLong ? "$float64" : "$float32", all), true);
      }
    }

    /// <summary>
    /// One value stored in every element of an array: the <c>fill</c> of the array, or a loop where
    /// the profile has none.
    /// </summary>
    private JsCall Fill(IrExpr array, JsExpression value)
      => this.Native(".fill") ? new JsCall(new JsMember(this.Operand(array), "fill"), [value]) : this.Helper("$fill", [this.Text(array), value]);

    /// <summary>A Boolean as the literal a helper is told it by.</summary>
    private static JsBoolean Flag(bool value) => new(value);

    /// <summary>
    /// A step by one as an expression. An array element goes through a helper: storing into a typed
    /// array wraps an integer around and rounds to binary32 by itself, a plain array has to be told,
    /// and a pair of halves is stepped by the helpers of its arithmetic. A variable is assigned inside
    /// the expression; the postfix form keeps the old value in a variable of the function.
    /// </summary>
    private (JsExpression Node, bool Atomic) Step(IrStep step) {
      var type = step.Type;
      var halves = type.IsLong && this.Halves;
      var post = Flag(step.Postfix);
      if (step.Target is IrArrayLoad element) {
        var (array, index) = this.Place(element);
        var arguments = new List<JsExpression> { array, index };
        if (halves) {
          arguments.AddRange([Flag(step.Increment), post]);
          return (this.Helper(!step.Trap ? "$step64" : type.IsSigned ? "$stepck64s" : "$stepck64u", arguments), true);
        }

        arguments.AddRange([type.IsLong ? new JsBigInt(step.Increment ? 1 : -1) : Number(step.Increment ? "1" : "-1"), post]);
        if (step.Trap) {
          arguments.Add(this.Literal(type, IrOperators.Minimum(type)));
          arguments.Add(this.Literal(type, IrOperators.Maximum(type)));
          return (this.Helper("$stepck", arguments), true);
        }

        if (type == IrType.F64 || this.Native(Storage(type)))
          return (this.Helper("$step", arguments), true);

        if (type == IrType.F32)
          return (this.Helper("$stepf", arguments), true);

        arguments.AddRange([Number((32 - type.Bits).ToString(CultureInfo.InvariantCulture)), Flag(type.IsSigned)]);
        return (this.Helper("$stepi", arguments), true);
      }

      var name = step.Target is IrGlobalRef global ? this.GlobalName(global.Global).Name : this.Local(((IrLocalRef)step.Target).Local);
      var current = step.Postfix ? Previous : name;
      JsExpression One() => type.IsLong ? new JsBigInt(1) : Number("1");
      JsExpression Moved() => Infix(Id(current), step.Increment ? "+" : "-", One());
      var stepped = halves ? this.Helper(HalvesStep(step), [Id(current), One64])
        : step.Trap ? this.Check(type, Moved()).Node
        : type == IrType.F64 ? Moved()
        : type == IrType.F32 ? this.Fround(Moved())
        : type.IsWideInteger ? Wrap(type, Moved()).Node
        : Narrow(type, Parenthesized(Moved()));
      if (!step.Postfix)
        return (new JsAssignment(Id(name), "=", stepped), false);

      this._previous = true;
      return (Parenthesized(new JsSequence([new JsAssignment(Id(Previous), "=", Id(name)), new JsAssignment(Id(name), "=", stepped), Id(Previous)])), true);
    }

    /// <summary>The helper that adds one to a pair of halves or subtracts it, wrapping around or trapping.</summary>
    private static string HalvesStep(IrStep step) {
      var operation = step.Increment ? "$add" : "$sub";
      return !step.Trap ? operation + "64" : operation + (step.Type.IsSigned ? "ck64s" : "ck64u");
    }

    /// <summary>A number cut to an integer type of up to 32 bits; the operand is written as an operand already.</summary>
    private static JsExpression Narrow(IrType type, JsExpression operand) => type.Kind switch {
      IrTypeKind.I8 => Infix(Infix(operand, "<<", Number("24")), ">>", Number("24")),
      IrTypeKind.U8 => Infix(operand, "&", Number("0xFF")),
      IrTypeKind.I16 => Infix(Infix(operand, "<<", Number("16")), ">>", Number("16")),
      IrTypeKind.U16 => Infix(operand, "&", Number("0xFFFF")),
      IrTypeKind.I32 => Infix(operand, "|", Number("0")),
      _ => Infix(operand, ">>>", Number("0"))
    };

    /// <summary>A result that may have left its type, cut back to it: modulo 2^width.</summary>
    private static (JsExpression Node, bool Atomic) Wrap(IrType type, JsExpression value) => type.Kind switch {
      IrTypeKind.I64 => (Invoke("BigInt.asIntN", Number("64"), value), true),
      IrTypeKind.U64 => (Invoke("BigInt.asUintN", Number("64"), value), true),
      IrTypeKind.I32 => (Infix(Parenthesized(value), "|", Number("0")), false),
      _ => (Infix(Parenthesized(value), ">>>", Number("0")), false)
    };

    /// <summary>
    /// A result checked against the range of its type; it traps when it is outside. A number that can
    /// come out as the negative zero an integer does not have, is made the zero it has.
    /// </summary>
    private (JsExpression Node, bool Atomic) Check(IrType type, JsExpression value, bool signedZero = false) {
      var inRange = this.Helper("$ck", [value, this.Literal(type, IrOperators.Minimum(type)), this.Literal(type, IrOperators.Maximum(type))]);
      return signedZero && type == IrType.I32 ? (Infix(inRange, "|", Number("0")), false) : (inRange, true);
    }

    private (JsExpression Node, bool Atomic) Unary(IrUnary unary) {
      var type = unary.Type;
      if (type.IsLong && this.Halves)
        return (unary.Op switch {
          IrUnaryOp.Negate => this.Helper("$neg64", unary.Operand),
          IrUnaryOp.NegateChecked => this.Helper("$subck64s", [Zero64, this.Text(unary.Operand)]),
          _ => this.Helper("$not64", unary.Operand)
        }, true);

      var operand = this.Operand(unary.Operand);
      switch (unary.Op) {
        case IrUnaryOp.Negate when type.IsFloat:
          return (new JsUnary("-", operand), false);
        case IrUnaryOp.Negate:
          return type.IsLong ? Wrap(type, new JsUnary("-", operand)) : (Infix(new JsUnary("-", operand), "|", Number("0")), false);
        case IrUnaryOp.NegateChecked:
          return this.Check(type, new JsUnary("-", operand), true);
        case IrUnaryOp.BitNot:
          return type.IsSigned ? (new JsUnary("~", operand), false)
            : type.IsLong ? Wrap(type, new JsUnary("~", operand))
            : (Infix(new JsUnary("~", operand), ">>>", Number("0")), false);
        default:
          return (new JsUnary("!", operand), false);
      }
    }

    /// <summary>An operator on numbers: 32-bit integers and truth values.</summary>
    private (JsExpression Node, bool Atomic) Binary(IrBinary binary) {
      var type = binary.Left.Type;
      var signed = type == IrType.I32;
      switch (binary.Op) {
        case IrBinaryOp.Multiply:
          var left = this.Text(binary.Left);
          var product = this.Imul(left, this.Text(binary.Right));
          return signed ? (product, true) : (Infix(product, ">>>", Number("0")), false);
        case IrBinaryOp.Divide:
          return (this.Helper(signed ? "$div" : "$udiv", binary.Left, binary.Right), true);
        case IrBinaryOp.Remainder:
          return (this.Helper(signed ? "$rem" : "$urem", binary.Left, binary.Right), true);
      }

      var first = this.Operand(binary.Left);
      var second = this.Operand(binary.Right);
      switch (binary.Op) {
        case IrBinaryOp.Add:
          return Wrap(type, Infix(first, "+", second));
        case IrBinaryOp.Subtract:
          return Wrap(type, Infix(first, "-", second));
        case IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked:
          // Sum and difference of two 32-bit integers are exact in a double. A product that is not
          // exact is beyond 2^53 and so outside the range on either side of the rounding.
          return this.Check(type, Infix(first, CheckedSign(binary.Op), second), binary.Op == IrBinaryOp.MultiplyChecked);
      }

      JsExpression Unsigned(JsExpression value) => Infix(Parenthesized(value), ">>>", Number("0"));
      return (binary.Op switch {
        IrBinaryOp.And => signed ? Infix(first, "&", second) : Unsigned(Infix(first, "&", second)),
        IrBinaryOp.Or => signed ? Infix(first, "|", second) : Unsigned(Infix(first, "|", second)),
        IrBinaryOp.Xor => signed ? Infix(first, "^", second) : Unsigned(Infix(first, "^", second)),
        IrBinaryOp.ShiftLeft => signed ? Infix(first, "<<", second) : Unsigned(Infix(first, "<<", second)),
        IrBinaryOp.ShiftRight => Infix(first, signed ? ">>" : ">>>", second),
        IrBinaryOp.ShiftRightLogical => signed
          ? Infix(Parenthesized(Infix(first, ">>>", second)), "|", Number("0"))
          : Infix(first, ">>>", second),
        _ => Infix(first, Sign(binary.Op), second)
      }, false);
    }

    /// <summary>
    /// An operator on 64-bit integers that are <c>BigInt</c>s. An unsigned value is never negative,
    /// so the bitwise operators and the shift to the right need no cutting back; the count of a shift
    /// is a number and is masked to 6 bits as C# masks it.
    /// </summary>
    private (JsExpression Node, bool Atomic) Binary64(IrBinary binary) {
      var type = binary.Left.Type;
      var signed = type == IrType.I64;
      switch (binary.Op) {
        case IrBinaryOp.Divide:
          return (this.Helper(signed ? "$div64" : "$udiv64", binary.Left, binary.Right), true);
        case IrBinaryOp.Remainder:
          return (this.Helper(signed ? "$rem64" : "$urem64", binary.Left, binary.Right), true);
      }

      var left = this.Operand(binary.Left);
      var right = this.Operand(binary.Right);
      JsExpression count = binary.Right is IrConst constant ? new JsBigInt(constant.Value & 63) : Invoke("BigInt", Infix(right, "&", Number("63")));
      switch (binary.Op) {
        case IrBinaryOp.Add:
          return Wrap(type, Infix(left, "+", right));
        case IrBinaryOp.Subtract:
          return Wrap(type, Infix(left, "-", right));
        case IrBinaryOp.Multiply:
          return Wrap(type, Infix(left, "*", right));
        case IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked:
          return this.Check(type, Infix(left, CheckedSign(binary.Op), right));
        case IrBinaryOp.ShiftLeft:
          return Wrap(type, Infix(left, "<<", count));
        case IrBinaryOp.ShiftRightLogical when signed:
          return Wrap(type, Infix(Invoke("BigInt.asUintN", Number("64"), left), ">>", count));
        case IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical:
          return (Infix(left, ">>", count), false);
        default:
          return (Infix(left, Sign(binary.Op), right), false);
      }
    }

    /// <summary>
    /// An operator on 64-bit integers that are pairs of halves: a call of the helper that computes it.
    /// An order is the sign of the helper that compares two pairs, as signed or as unsigned numbers;
    /// the count of a shift is a number, which the helper masks to 6 bits as C# masks it.
    /// </summary>
    private (JsExpression Node, bool Atomic) BinaryHalves(IrBinary binary) {
      var signed = binary.Left.Type == IrType.I64;
      var suffix = signed ? "s" : "u";
      switch (binary.Op) {
        case IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical:
          var shift = binary.Op == IrBinaryOp.ShiftLeft ? "$shl64" : binary.Op == IrBinaryOp.ShiftRight && signed ? "$sar64" : "$shr64";
          var count = binary.Right is IrConst constant ? Number((constant.Value & 63).ToString(CultureInfo.InvariantCulture)) : this.Text(binary.Right);
          return (this.Helper(shift, [this.Text(binary.Left), count]), true);
        case IrBinaryOp.NotEqual:
          return (new JsUnary("!", this.Helper("$eq64", binary.Left, binary.Right)), false);
        case IrBinaryOp.Less or IrBinaryOp.LessOrEqual or IrBinaryOp.Greater or IrBinaryOp.GreaterOrEqual:
          return (Infix(this.Helper(signed ? "$scmp64" : "$ucmp64", binary.Left, binary.Right), Sign(binary.Op), Number("0")), false);
      }

      return (this.Helper(binary.Op switch {
        IrBinaryOp.Add => "$add64",
        IrBinaryOp.Subtract => "$sub64",
        IrBinaryOp.Multiply => "$mul64",
        IrBinaryOp.Divide => signed ? "$div64" : "$udiv64",
        IrBinaryOp.Remainder => signed ? "$rem64" : "$urem64",
        IrBinaryOp.AddChecked => "$addck64" + suffix,
        IrBinaryOp.SubtractChecked => "$subck64" + suffix,
        IrBinaryOp.MultiplyChecked => "$mulck64" + suffix,
        IrBinaryOp.And => "$and64",
        IrBinaryOp.Or => "$or64",
        IrBinaryOp.Xor => "$xor64",
        _ => "$eq64"
      }, binary.Left, binary.Right), true);
    }

    /// <summary>
    /// An operator on floating-point numbers. JavaScript computes in binary64, which is the operation
    /// itself for <c>f64</c>; for <c>f32</c> the result is rounded to binary32.
    /// </summary>
    private (JsExpression Node, bool Atomic) BinaryFloat(IrBinary binary) {
      var left = this.Operand(binary.Left);
      var right = this.Operand(binary.Right);
      var sign = binary.Op switch {
        IrBinaryOp.Add => "+",
        IrBinaryOp.Subtract => "-",
        IrBinaryOp.Multiply => "*",
        IrBinaryOp.Divide => "/",
        IrBinaryOp.Remainder => "%",
        _ => Sign(binary.Op)
      };
      var result = Infix(left, sign, right);
      return binary.Type == IrType.F32 ? (this.Fround(result), true) : (result, false);
    }

    private static string CheckedSign(IrBinaryOp op) => op switch {
      IrBinaryOp.AddChecked => "+",
      IrBinaryOp.SubtractChecked => "-",
      _ => "*"
    };

    /// <summary>The operators JavaScript writes as the IR means them, whatever the type of the operands.</summary>
    private static string Sign(IrBinaryOp op) => op switch {
      IrBinaryOp.And => "&",
      IrBinaryOp.Or => "|",
      IrBinaryOp.Xor => "^",
      IrBinaryOp.Equal => "===",
      IrBinaryOp.NotEqual => "!==",
      IrBinaryOp.Less => "<",
      IrBinaryOp.LessOrEqual => "<=",
      IrBinaryOp.Greater => ">",
      IrBinaryOp.GreaterOrEqual => ">=",
      IrBinaryOp.LogicalAnd => "&&",
      _ => "||"
    };

    private (JsExpression Node, bool Atomic) Convert(IrConvert conversion) {
      var from = conversion.Operand.Type;
      var to = conversion.Type;
      if (to.IsFloat)
        return this.ToFloat(conversion);

      if (from.IsFloat)
        return (this.FromFloat(conversion), true);

      if ((from.IsLong || to.IsLong) && this.Halves)
        return this.ConvertHalves(conversion);

      if (conversion.Contract == IrConversion.Trap) {
        var (low, high) = Bounds(conversion);
        var inRange = this.Helper("$ck", [this.Text(conversion.Operand), this.Literal(from, low), this.Literal(from, high)]);
        return (from.IsLong == to.IsLong ? inRange : Invoke(to.IsLong ? "BigInt" : "Number", inRange), true);
      }

      var lossless = IrOperators.Contains(to, from);
      if (to.IsLong) {
        var big = from.IsLong ? this.Text(conversion.Operand) : Invoke("BigInt", this.Text(conversion.Operand));
        return lossless ? (big, true) : Wrap(to, big);
      }

      if (from.IsLong)
        return (Invoke("Number", Invoke(to.IsSigned ? "BigInt.asIntN" : "BigInt.asUintN", Number(to.Bits.ToString(CultureInfo.InvariantCulture)), this.Text(conversion.Operand))), true);

      if (lossless)
        return this.Expression(conversion.Operand);

      return (Narrow(to, this.Operand(conversion.Operand)), false);
    }

    /// <summary>The bounds of a checked conversion: those of the target as far as the source reaches, in the notation of the source.</summary>
    private static (Int128 Low, Int128 High) Bounds(IrConvert conversion) {
      var from = conversion.Operand.Type;
      var to = conversion.Type;
      return (Int128.Max(IrOperators.Minimum(to), IrOperators.Minimum(from)), Int128.Min(IrOperators.Maximum(to), IrOperators.Maximum(from)));
    }

    /// <summary>
    /// A conversion between integers of which one at least is a pair of halves. The two 64-bit types
    /// share their bits, so one becomes the other as it stands; a narrower type is the lower half cut
    /// to size, and becomes a pair by sign extension or with an upper half of zero.
    /// </summary>
    private (JsExpression Node, bool Atomic) ConvertHalves(IrConvert conversion) {
      var from = conversion.Operand.Type;
      var to = conversion.Type;
      if (conversion.Contract == IrConversion.Trap) {
        var (low, high) = Bounds(conversion);
        JsExpression[] bounds = [this.Text(conversion.Operand), this.Literal(from, low), this.Literal(from, high)];
        if (!from.IsLong)
          return this.Widen(from, this.Helper("$ck", bounds));

        var inRange = this.Helper(from.IsSigned ? "$ck64s" : "$ck64u", bounds);
        return to.IsLong ? (inRange, true) : to.IsSigned ? (Infix(new JsMember(inRange, "lo"), "|", Number("0")), false) : (new JsMember(inRange, "lo"), true);
      }

      if (to.IsLong)
        return from.IsLong ? this.Expression(conversion.Operand) : this.Widen(from, this.Text(conversion.Operand));

      var half = new JsMember(this.Operand(conversion.Operand), "lo");
      return to == IrType.U32 ? (half, true) : (Narrow(to, half), false);
    }

    /// <summary>A number of up to 32 bits as a pair of halves.</summary>
    private (JsExpression Node, bool Atomic) Widen(IrType from, JsExpression value)
      => (from.IsSigned ? this.Helper("$sx64", [value]) : Halves(Number("0"), value), true);

    /// <summary>
    /// A number converted to floating point. An integer of up to 32 bits is already a double with its
    /// value; a 64-bit integer becomes the nearest double, and for <c>f32</c> goes through a helper
    /// that rounds it once, because rounding to binary64 and then to binary32 can differ from that.
    /// </summary>
    private (JsExpression Node, bool Atomic) ToFloat(IrConvert conversion) {
      var from = conversion.Operand.Type;
      var single = conversion.Type == IrType.F32;
      if (from.IsLong && this.Halves)
        return (this.Helper((from.IsSigned ? "$l2" : "$ul2") + (single ? "f" : "d"), conversion.Operand), true);

      if (from.IsLong)
        return (single ? this.Helper("$f32", conversion.Operand) : Invoke("Number", this.Text(conversion.Operand)), true);

      return single ? (this.Fround(this.Text(conversion.Operand)), true) : this.Expression(conversion.Operand);
    }

    /// <summary>
    /// A floating-point number converted to an integer by a helper that saturates or traps. The bounds
    /// of a 64-bit target are numbers: its smallest value and the power of two just above its largest.
    /// </summary>
    private JsCall FromFloat(IrConvert conversion) {
      var to = conversion.Type;
      var trap = conversion.Contract == IrConversion.Trap;
      var low = Number(IrOperators.Minimum(to));
      return to.IsLong
        ? this.Helper(trap ? "$f2lc" : "$f2l", [this.Text(conversion.Operand), low, Number(IrOperators.Maximum(to) + 1)])
        : this.Helper(trap ? "$f2ic" : "$f2i", [this.Text(conversion.Operand), low, Number(IrOperators.Maximum(to))]);
    }

  }

}
