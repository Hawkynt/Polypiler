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
      if (this.ForeignWrite(expression) is { } foreign)
        return foreign;

      if (this.PointerWrite(expression) is { } pointer)
        return pointer;

      switch (expression) {
        case IrConst constant:
          return this.Constant(constant.Type, constant.Value);
        case IrConstRef reference:
          return (Id(this.Name(reference.Constant.Name)), true);
        case IrStrConst text:
          return (new JsString(text.Value), true);
        case IrBigConst big:
          return this.BigLiteral(big.Value);
        case IrUnary { Type.IsBigInt: true } big:
          return this.UnaryBig(big);
        case IrBinary { Left.Type.IsBigInt: true } big:
          return this.BinaryBig(big);
        case IrIntrinsic intrinsic:
          return this.Intrinsic(intrinsic);
        case IrLocalRef reference when reference.Local == this._receiver && this._detached:
          // The constructor that skips its base classes makes its object itself (#1388).
          return (Id(Self), true);
        case IrLocalRef reference when reference.Local == this._receiver && this._functionDepth > 0 && !this.Arrows:
          // A function expression has a this of its own; the method's is kept in a variable.
          this._self = true;
          return (Id(Self), true);
        case IrLocalRef reference when reference.Local == this._receiver:
          return (new JsThis(), true);
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
            : this.Halves ? this.Unlegalized(binary.Op.ToString(), true)
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

          if (element.IsFunction)
            return (this.Helper("$new", [count, new JsNull()]), true);

          return (this.Native(Storage(element)) ? new JsNew(Id(Storage(element)), [count]) : this.Helper("$new", [count, this.Zero(element)]), true);
        case IrStep step:
          return this.Step(step);
        case IrNull { Type.IsArray: true } or IrNone or IrSome or IrIsNone or IrUnwrap or IrCoalesce or IrIsNull or IrBind:
          return this.Optional(expression);
        case IrNew { Type.ClassType.IsLibrary: true } creation:
          return this.NewLibraryException(creation);
        case IrFieldLoad { Target.Type.IsObject: true } load when ReferenceEquals(load.Field, IrExceptionClass.InnerException):
          return this.InnerException(load);
        case IrThrowExpression thrown:
          return this.ThrowExpression(thrown);
        case IrNew or IrNull or IrMethodCall or IrReferenceEquals or IrFieldLoad { Target.Type.IsObject: true } or IrStructEquals { Left.Type.IsObject: true }:
          return this.Object(expression);
        case IrTypeTest or IrCast or IrAsCast:
          return this.Conversion(expression);
        case IrStructNew or IrStructDefault or IrFieldLoad or IrStructEquals:
          return this.Struct(expression);
        case IrGenericEquals equality:
          return this.GenericEquals(equality);
        case IrGenericCompare order:
          return this.GenericCompare(order);
        case IrCollection operation:
          return this.Collection(operation);
        case IrCall chained when this._owners.ContainsKey(chained.Function):
          return (this.Chained(chained), true);
        case IrLambda or IrFunctionRef or IrBoundMethod or IrInvoke:
          return this.FunctionValue(expression);
        case IrArraySequence sequence:
          return this.ArraySequence(sequence);
        case IrAwait or IrAsync:
          return this.AsyncWrite(expression);
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
        case IrIntrinsicOp.ArrayOf when intrinsic.Type.Element!.IsStruct || intrinsic.Type.Element.IsString || intrinsic.Type.Element.IsFunction:
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
      if (NumberTextOperations.IsNumberText(intrinsic.Op))
        return this.NumberText(intrinsic);
      if (EnumOperations.IsEnum(intrinsic.Op))
        return this.EnumOperation(intrinsic);

      // An operation the profile has only as a helper of the library reaches the writer only where the module was not legalized.
      if (JavaScriptLibrary.Covers(intrinsic, this._profile, this._capabilities)) {
        if (this._refused.Add(intrinsic.Op.ToString()))
          this._problems.Add(InexpressibleDescriptor.At($"'{intrinsic.Op}' needs its helper of the library in JavaScript profile '{this._profile.LevelName}'; the module was not legalized"));

        return (new JsNumber(double.NaN), true);
      }

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
        case IrIntrinsicOp.Sqrt:
          return (type == IrType.F32 ? this.Fround(Invoke("Math.sqrt", all)) : Invoke("Math.sqrt", all), true);
        case IrIntrinsicOp.Floor:
          return (Invoke("Math.floor", all), true);
        case IrIntrinsicOp.Ceiling:
          return (Invoke("Math.ceil", all), true);
        case IrIntrinsicOp.Truncate:
          // Below ECMAScript 2015 the library's truncation stands in; without it the level has no form.
          this.Native("Math.trunc");
          return (Invoke("Math.trunc", all), true);
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
      if (type.IsLong && this.Halves)
        return this.Unlegalized("a step", true);

      var post = Flag(step.Postfix);
      if (step.Target is IrArrayLoad element) {
        var (array, index) = this.Place(element);
        var arguments = new List<JsExpression> { array, index };
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

      // A checked step of a variable is the library's, which legalization put in.
      if (step.Trap)
        return this.Unlegalized("a checked step");

      var name = step.Target is IrGlobalRef global ? this._globals[global.Global] : this.Local(((IrLocalRef)step.Target).Local);
      var current = step.Postfix ? Previous : name;
      JsExpression One() => type.IsLong ? new JsBigInt(1) : Number("1");
      JsExpression Moved() => Infix(Dotted(current), step.Increment ? "+" : "-", One());
      var stepped = type == IrType.F64 ? Moved()
        : type == IrType.F32 ? this.Fround(Moved())
        : type.IsWideInteger ? Wrap(type, Moved()).Node
        : Narrow(type, Parenthesized(Moved()));
      if (!step.Postfix)
        return (new JsAssignment(Dotted(name), "=", stepped), false);

      this._previous = true;
      return (Parenthesized(new JsSequence([new JsAssignment(Id(Previous), "=", Dotted(name)), new JsAssignment(Dotted(name), "=", stepped), Id(Previous)])), true);
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

    private (JsExpression Node, bool Atomic) Unary(IrUnary unary) {
      var type = unary.Type;
      if (type.IsLong && this.Halves)
        return this.Unlegalized(unary.Op.ToString(), true);

      var operand = this.Operand(unary.Operand);
      switch (unary.Op) {
        case IrUnaryOp.Negate when type.IsFloat:
          return (new JsUnary("-", operand), false);
        case IrUnaryOp.Negate:
          return type.IsLong ? Wrap(type, new JsUnary("-", operand)) : (Infix(new JsUnary("-", operand), "|", Number("0")), false);
        case IrUnaryOp.NegateChecked:
          return this.Unlegalized(unary.Op.ToString());
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
      var signed = type.Integral == IrType.I32;
      switch (binary.Op) {
        case IrBinaryOp.Multiply:
          var left = this.Text(binary.Left);
          var product = this.Imul(left, this.Text(binary.Right));
          return signed ? (product, true) : (Infix(product, ">>>", Number("0")), false);
        case IrBinaryOp.Divide or IrBinaryOp.Remainder:
          // The division traps, which the library's helper checks for; legalization put it in.
          return this.Unlegalized(binary.Op.ToString());
      }

      var first = this.Operand(binary.Left);
      var second = this.Operand(binary.Right);
      switch (binary.Op) {
        case IrBinaryOp.Add:
          return Wrap(type, Infix(first, "+", second));
        case IrBinaryOp.Subtract:
          return Wrap(type, Infix(first, "-", second));
        case IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked:
          return this.Unlegalized(binary.Op.ToString());
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
      var signed = type.Integral == IrType.I64;
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
          return this.Unlegalized(binary.Op.ToString());
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
    /// An operation that is the call of a helper of the library once the module is legalized: one on a
    /// 64-bit integer below ECMAScript 2020, where it is a pair of halves (O0075), and one that traps on
    /// numbers, which do not. It reaches the writer only where the module was not legalized.
    /// </summary>
    /// <param name="operation">The operation, as the report names it.</param>
    /// <param name="wide">
    /// Whether it is one on a pair of halves: without the pairs as a replacement the level has no
    /// 64-bit integer at all, which the check of <c>BigInt</c> reported already.
    /// </param>
    private (JsExpression Node, bool Atomic) Unlegalized(string operation, bool wide = false) {
      if ((!wide || this._capabilities["BigInt"].Replacement is not null) && this._refused.Add((wide ? "64-bit " : "") + operation))
        this._problems.Add(InexpressibleDescriptor.At(
          $"'{operation}'{(wide ? " of a 64-bit integer" : "")} needs its helper of the library in JavaScript profile '{this._profile.LevelName}'; the module was not legalized"));

      return (new JsNumber(double.NaN), true);
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
      if (from.IsBigInt || to.IsBigInt)
        return this.ConvertBig(conversion);

      if (to.IsFloat)
        return this.ToFloat(conversion);

      if (from.IsFloat)
        return this.FromFloat(conversion);

      if ((from.IsLong || to.IsLong) && this.Halves)
        return this.ConvertHalves(conversion);

      // A conversion that traps checks the bounds with the library's helper, which legalization put in.
      if (conversion.Contract == IrConversion.Trap)
        return this.Unlegalized("checked conversion");

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

    /// <summary>
    /// A conversion between integers of which one at least is a pair of halves: the two 64-bit types
    /// share their bits, so one becomes the other as it stands, and a narrower type is the lower half
    /// cut to size. A conversion that checks, and one to a pair from a narrower type, legalization
    /// makes the calls of helpers on the limbs.
    /// </summary>
    private (JsExpression Node, bool Atomic) ConvertHalves(IrConvert conversion) {
      var to = conversion.Type;
      if (conversion.Contract == IrConversion.Trap || !conversion.Operand.Type.IsLong)
        return this.Unlegalized("conversion", true);

      if (to.IsLong)
        return this.Expression(conversion.Operand);

      var half = new JsMember(this.Operand(conversion.Operand), "lo");
      return to == IrType.U32 ? (half, true) : (Narrow(to, half), false);
    }

    /// <summary>
    /// A number converted to floating point. An integer of up to 32 bits is already a double with its
    /// value; a 64-bit integer becomes the nearest double, and for <c>f32</c> goes through a helper
    /// that rounds it once, because rounding to binary64 and then to binary32 can differ from that.
    /// </summary>
    private (JsExpression Node, bool Atomic) ToFloat(IrConvert conversion) {
      var from = conversion.Operand.Type;
      var single = conversion.Type == IrType.F32;
      if (from.IsLong && this.Halves)
        return this.Unlegalized("conversion", true);

      if (from.IsLong)
        return (single ? this.Helper("$f32", conversion.Operand) : Invoke("Number", this.Text(conversion.Operand)), true);

      return single ? (this.Fround(this.Text(conversion.Operand)), true) : this.Expression(conversion.Operand);
    }

    /// <summary>
    /// A floating-point number converted to a 64-bit <c>BigInt</c> by a helper that saturates or traps;
    /// its bounds are numbers: its smallest value and the power of two just above its largest. To a
    /// number it is the library's helper, which legalization put in.
    /// </summary>
    private (JsExpression Node, bool Atomic) FromFloat(IrConvert conversion) {
      var to = conversion.Type;
      if (to.IsLong && this.Halves)
        return this.Unlegalized("conversion", true);

      if (!to.IsLong)
        return this.Unlegalized("conversion from floating point");

      var trap = conversion.Contract == IrConversion.Trap;
      var low = Number(IrOperators.Minimum(to));
      return (this.Helper(trap ? "$f2lc" : "$f2l", [this.Text(conversion.Operand), low, Number(IrOperators.Maximum(to) + 1)]), true);
    }

  }

}
