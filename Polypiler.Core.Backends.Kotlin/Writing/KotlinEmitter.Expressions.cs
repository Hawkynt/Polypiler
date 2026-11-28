using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.Kotlin.Syntax;
using Polypiler.Core.IR;
using static Polypiler.Core.Backends.Kotlin.Syntax.Kt;

namespace Polypiler.Core.Backends.Kotlin;

public static partial class KotlinEmitter {

  private sealed partial class Writer {

    /// <summary>An expression whose Kotlin type is the Kotlin type of the IR expression's type.</summary>
    private KtExpression Expression(IrExpr expression) {
      switch (expression) {
        case IrConst constant:
          return this.ValueOf(constant.Type, constant.Value, typed: false);
        case IrStrConst text:
          return new KtString(text.Value);
        case IrLocalRef reference:
          return Name(this.LocalName(reference.Local));
        case IrGlobalRef reference:
          return Name(this._globals[reference.Global.Name]);
        case IrConstRef reference:
          return Name(this._constants[reference.Constant.Name]);
        case IrUnary unary:
          return this.Unary(unary);
        case IrBinary binary:
          return this.Operator(binary);
        case IrConvert conversion:
          return this.Convert(conversion);
        case IrConditional conditional:
          return new KtIfExpression(this.Expression(conditional.Condition), this.Expression(conditional.WhenTrue), this.Expression(conditional.WhenFalse));
        case IrCall call:
          return this.Invocation(call);
        case IrArrayLoad load:
          return this.Loaded(load.Array.Type.Element!, new KtIndex(this.Expression(load.Array), this.Index(load.Index)));
        case IrArrayLength length:
          return Member(this.Expression(length.Array), "size");
        case IrNewArray array:
          return this.NewArray(array);
        case IrStep step:
          return this.Step(step);
        case IrIntrinsic intrinsic:
          return this.Intrinsic(intrinsic);
        case IrStructNew structure:
          return Call(this.TypeName(structure.Type), [.. structure.Arguments.Select(this.Expression)]);
        case IrStructDefault structure:
          return Call(this.TypeName(structure.Type));
        case IrFieldLoad { Target.Type.IsStruct: true } load:
          return Member(this.Expression(load.Target), FieldName(load.Field));
        case IrStructEquals equals when equals.Left.Type.IsStruct:
          return Binary(this.Expression(equals.Left), "==", this.Expression(equals.Right));
        case IrNew { Type.ClassType: { } type } created when IrExceptionClass.IsLibrary(type):
          return this.NewException(type, created.Arguments);
        case IrBind bind:
          // An assignment as an expression: the value, stored on its way.
          return new KtCall(Member(this.Expression(bind.Value), "also"), []) { Trailing = new KtLambda([], [new KtAssignment(Name(this.LocalName(bind.Local)), "=", Name("it"))]) };
        case IrThrowExpression raise:
          return new KtThrowExpression(this.Expression(raise.Exception));
        default:
          return this.Inexpressible(KindOf(expression));
      }
    }

    /// <summary>The name of a declared type.</summary>
    private string TypeName(IrType type) => this._typeNames.TryGetValue(type.Declaration!, out var name) ? name : ((KtNamedType)this.TypeOf(type)).Name;

    #region Literals

    /// <summary>
    /// A constant of a type: a decimal integer with Kotlin's suffix (<c>5</c>, <c>5L</c>, <c>5u</c>,
    /// <c>5uL</c>), the smallest signed value by its name, an 8- or 16-bit one converted
    /// (<c>5.toByte()</c>) unless <paramref name="typed"/> says the place declares its type, where the
    /// literal alone has it; a floating-point number as its shortest round-tripping digits, a NaN,
    /// an infinity and <c>-0.0</c> by name or sign, a NaN with another payload by its bits.
    /// </summary>
    private KtExpression ValueOf(IrType type, long value, bool typed) {
      var integral = type.Integral;
      switch (integral.Kind) {
        case IrTypeKind.Bool:
          return value != 0 ? True : False;
        case IrTypeKind.F64:
          return Double(BitConverter.Int64BitsToDouble(value), value);
        case IrTypeKind.F32:
          return Single(BitConverter.Int32BitsToSingle((int)value), (int)value);
        case IrTypeKind.I32 when value == int.MinValue:
          return Member("Int", "MIN_VALUE");
        case IrTypeKind.I64 when value == long.MinValue:
          return Member("Long", "MIN_VALUE");
        case IrTypeKind.U64:
          return Literal(((ulong)value).ToString(CultureInfo.InvariantCulture) + "uL");
      }

      var digits = Literal(Math.Abs(value).ToString(CultureInfo.InvariantCulture) + (KotlinTypes.IsNarrow(integral) ? integral.IsSigned ? "" : "u" : KotlinTypes.Suffix(integral)));
      KtExpression literal = value < 0 ? Negate(digits) : digits;
      return KotlinTypes.IsNarrow(integral) && !typed ? Method(literal, KotlinTypes.To(integral)) : literal;
    }

    private static KtExpression Double(double number, long bits) {
      if (double.IsNaN(number))
        return bits == BitConverter.DoubleToInt64Bits(double.NaN) || bits == unchecked((long)0x7FF8000000000000UL)
          ? Member("Double", "NaN")
          : Method(Name("Double"), "fromBits", Literal(bits.ToString(CultureInfo.InvariantCulture) + "L"));

      if (double.IsInfinity(number))
        return Member("Double", number > 0 ? "POSITIVE_INFINITY" : "NEGATIVE_INFINITY");

      return number < 0 || (number == 0 && double.IsNegative(number)) ? Negate(Literal(KotlinHelpers.Number(-number))) : Literal(KotlinHelpers.Number(number));
    }

    private static KtExpression Single(float number, int bits) {
      if (float.IsNaN(number))
        return bits == 0x7FC00000
          ? Member("Float", "NaN")
          : Method(Name("Float"), "fromBits", Int(bits));

      if (float.IsInfinity(number))
        return Member("Float", number > 0 ? "POSITIVE_INFINITY" : "NEGATIVE_INFINITY");

      return number < 0 || (number == 0 && float.IsNegative(number)) ? Negate(Literal(KotlinHelpers.Number(-number))) : Literal(KotlinHelpers.Number(number));
    }

    /// <summary>The zero of a number type, <c>false</c>, or the default struct.</summary>
    private KtExpression Zero(IrType type) => type.IsStruct ? Call(this.TypeName(type)) : this.ValueOf(type, 0, typed: true);

    #endregion

    #region Operators

    // The IR computes on 32 and 64 bits and on floating point only (IrOperators.Accepts): an operand of
    // 8 or 16 bits is converted before it reaches an operator, so every operator below has a Kotlin
    // operator of the same type.

    private KtExpression Unary(IrUnary unary) => unary.Op switch {
      IrUnaryOp.LogicalNot => Not(this.Expression(unary.Operand)),
      IrUnaryOp.Negate => Negate(this.Expression(unary.Operand)),
      IrUnaryOp.NegateChecked => Method(Name("Math"), "negateExact", this.Expression(unary.Operand)),
      _ => Method(this.Expression(unary.Operand), "inv")
    };

    private static string Symbol(IrBinaryOp op) => op switch {
      IrBinaryOp.Add => "+",
      IrBinaryOp.Subtract => "-",
      IrBinaryOp.Multiply => "*",
      IrBinaryOp.Divide => "/",
      IrBinaryOp.Remainder => "%",
      IrBinaryOp.And => "and",
      IrBinaryOp.Or => "or",
      IrBinaryOp.Xor => "xor",
      IrBinaryOp.Equal => "==",
      IrBinaryOp.NotEqual => "!=",
      IrBinaryOp.Less => "<",
      IrBinaryOp.LessOrEqual => "<=",
      IrBinaryOp.Greater => ">",
      IrBinaryOp.GreaterOrEqual => ">=",
      IrBinaryOp.LogicalAnd => "&&",
      _ => "||"
    };

    private KtExpression Operator(IrBinary binary) {
      var (op, left, right) = (binary.Op, binary.Left, binary.Right);
      var type = left.Type.Integral;
      switch (op) {
        case IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked when type == IrType.I32 || type == IrType.I64:
          return Method(Name("Math"), op switch { IrBinaryOp.AddChecked => "addExact", IrBinaryOp.SubtractChecked => "subtractExact", _ => "multiplyExact" },
            this.Expression(left), this.Expression(right));
        case IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked:
          // The unsigned types of 32 and 64 bits, and the 8- and 16-bit steps a checked increment makes.
          return this.Helper(KotlinHelpers.Checked(op, type), this.Expression(left), this.Expression(right));
        case IrBinaryOp.Divide or IrBinaryOp.Remainder when type.IsSigned && !(right is IrConst { Value: not 0 and not -1 }):
          // Kotlin wraps the smallest value divided by -1, which the IR traps.
          return this.Helper(op == IrBinaryOp.Divide ? KotlinHelpers.Divide(type) : KotlinHelpers.Remainder(type), this.Expression(left), this.Expression(right));
        case IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical:
          // Kotlin's shl, shr and ushr mask the count to five or six bits as the IR does; shr of an unsigned type is logical.
          var name = op == IrBinaryOp.ShiftLeft ? "shl" : op == IrBinaryOp.ShiftRightLogical && type.IsSigned ? "ushr" : "shr";
          var count = right is IrConst constant ? Int(constant.Value & (type.Bits - 1)) : this.Expression(right);
          return Binary(this.Expression(left), name, count);
        case IrBinaryOp.Divide or IrBinaryOp.Remainder when right is IrConst { Value: 0 } && type.IsInteger:
          // A zero divisor throws Kotlin's ArithmeticException, the IR's division by zero; a literal 0 would be a warning of kotlinc.
          return Binary(this.Expression(left), Symbol(op), Member(KotlinTypes.Scalar(type)!, "MIN_VALUE"));
        default:
          return Binary(this.Expression(left), Symbol(op), this.Expression(right));
      }
    }

    #endregion

    #region Conversions

    private KtExpression Convert(IrConvert conversion) {
      var (from, to) = (conversion.Operand.Type.Integral, conversion.Type.Integral);
      var operand = conversion.Operand;
      if (from == to)
        return this.Expression(operand);

      if (from.IsInteger && to.IsInteger) {
        if (conversion.Contract == IrConversion.Trap && KotlinHelpers.NeedsRangeCheck(from, to))
          return this.Helper(KotlinHelpers.Narrowed(from, to), this.Expression(operand));

        return operand is IrConst constant && conversion.Contract == IrConversion.Wrap
          ? this.ValueOf(to, IrOperators.Wrap(to, constant.Value), typed: false)
          : Method(this.Expression(operand), KotlinTypes.To(to));
      }

      if (from.IsInteger && to.IsFloat)
        return from == IrType.U64 && to == IrType.F32
          ? this.Helper(KotlinHelpers.UnsignedToSingle, this.Expression(operand))
          : Method(this.Expression(operand), KotlinTypes.To(to));

      if (from.IsFloat && to.IsFloat)
        return Method(this.Expression(operand), KotlinTypes.To(to));

      if (from.IsFloat && to.IsInteger) {
        if (conversion.Contract == IrConversion.Trap)
          return this.Helper(KotlinHelpers.Truncated(from, to), this.Expression(operand));

        // Kotlin's conversions of a floating-point number to Int, Long, UInt and ULong truncate and
        // saturate, a NaN becoming 0; 8 and 16 bits are clamped from there.
        var value = this.Expression(operand);
        if (to.Bits >= 32)
          return Method(value, KotlinTypes.To(to));

        var high = (long)IrOperators.Maximum(to);
        return to.IsSigned
          ? Method(Method(Method(value, "toInt"), "coerceIn", Negate(Int(high + 1)), Int(high)), KotlinTypes.To(to))
          : Method(Method(Method(value, "toUInt"), "coerceAtMost", Literal(high.ToString(CultureInfo.InvariantCulture) + "u")), KotlinTypes.To(to));
      }

      return this.Inexpressible($"the conversion of {from} to {to}");
    }

    #endregion

    #region Calls, arrays and steps

    private KtExpression Invocation(IrCall call) {
      // The constructors of the library's exception classes are functions no module declares; only a class of the module, which is refused, calls one.
      if (!this._functions.TryGetValue(call.Function, out var name))
        return this.Inexpressible($"the call of the library's constructor '{call.Function}'");

      return Call(name, [.. call.Arguments.Select(this.Expression)]);
    }

    /// <summary>
    /// An index of an array (an <c>i32</c> or a <c>u32</c>, as the IR has it) as the <c>Int</c> Kotlin
    /// indexes with; an unsigned one above <c>Int.MAX_VALUE</c> becomes negative, which is out of range as it was.
    /// </summary>
    private KtExpression Index(IrExpr index)
      => index.Type.Integral == IrType.I32 ? this.Expression(index) : index is IrConst constant ? Int(constant.Value) : Method(this.Expression(index), "toInt");

    /// <summary>An element read from a primitive array: an unsigned one converted from the signed type of its width the array holds.</summary>
    private KtExpression Loaded(IrType element, KtExpression read)
      => KotlinTypes.IsCarried(element) && !element.IsEnum ? Method(read, KotlinTypes.To(element.Integral)) : read;

    /// <summary>A value stored into a primitive array: an unsigned one as the signed type of its width, a constant as that literal.</summary>
    private KtExpression Stored(IrType element, IrExpr value) {
      if (!KotlinTypes.IsCarried(element))
        return value is IrConst constant ? this.ValueOf(element, constant.Value, typed: false) : this.Expression(value);

      var carrier = KotlinTypes.Carrier(element);
      if (value is IrConst literal)
        return this.ValueOf(carrier, (long)IrOperators.Wrap(carrier, literal.Value), typed: false);

      return Method(this.Expression(value), KotlinTypes.To(carrier));
    }

    /// <summary>A value already written as Kotlin, stored into a primitive array.</summary>
    private KtExpression Stored(IrType element, KtExpression value, IrType type)
      => KotlinTypes.IsCarried(element) && KotlinTypes.IsCarried(type) ? Method(value, KotlinTypes.To(KotlinTypes.Carrier(element))) : value;

    /// <summary>A new primitive array of a length, every element zero; an array of structs filled with the default struct.</summary>
    private KtExpression NewArray(IrNewArray array) {
      var element = array.Type.Element!;
      var length = this.Index(array.Length);
      if (KotlinTypes.PrimitiveArray(element) is { } primitive && !element.IsEnum)
        return Call(primitive, length);

      if (element.IsStruct)
        return new KtCall(Name("Array"), [length]) { Trailing = new KtLambda([], [new KtExpressionStatement(this.Zero(element))]) };

      return this.Inexpressible($"a new array of {element}");
    }

    /// <summary>
    /// An array of the given elements: <c>intArrayOf(1, 2, 3)</c>, a constant element as a literal of
    /// the signed type the array holds, which the factory's parameter type makes a <c>Byte</c> or a
    /// <c>Long</c> of; an array of structs <c>arrayOf(…)</c>.
    /// </summary>
    private KtExpression ArrayOf(IrType element, IReadOnlyList<IrExpr> values) {
      if (KotlinTypes.PrimitiveArray(element) is not { } primitive || element.IsEnum)
        return element.IsStruct
          ? Call("arrayOf", [.. values.Select(this.Expression)])
          : this.Inexpressible($"an array of {element}");

      var carrier = KotlinTypes.Carrier(element);
      var elements = values.Select(value => value is IrConst constant && carrier.IsInteger
        ? this.ValueOf(carrier, (long)IrOperators.Wrap(carrier, constant.Value), typed: true)
        : this.Stored(element, value));
      return Call(KotlinTypes.ArrayOf(primitive), [.. elements]);
    }

    /// <summary>
    /// A step as an expression: <c>x++</c>, <c>++x</c>, whose value is the old or the new one; an
    /// unsigned element of an array converted from the signed type the array holds.
    /// </summary>
    private KtExpression Step(IrStep step) {
      if (step.Trap && !step.Type.IsFloat)
        return this.CheckedStep(step);

      var place = this.Place(step.Target);
      var symbol = step.Increment ? "++" : "--";
      KtExpression value = step.Postfix ? new KtPostfix(place, symbol) : new KtPrefix(symbol, place);
      return step.Target is IrArrayLoad load ? this.Loaded(load.Array.Type.Element!, value) : value;
    }

    /// <summary>
    /// A checked step whose value is used, of a variable or of an element whose array and index are
    /// names: <c>x.also { x = Math.addExact(x, 1) }</c> for the old value,
    /// <c>Math.addExact(x, 1).also { x = it }</c> for the new one; the checked sum traps before
    /// anything is stored.
    /// </summary>
    private KtExpression CheckedStep(IrStep step) {
      if (step.Target is IrArrayLoad { Array: var array, Index: var index } && !(Simple(array) && Simple(index)))
        return this.Inexpressible("a checked step of an array element whose array or index is computed");

      var next = this.Expression(new IrBinary(step.Increment ? IrBinaryOp.AddChecked : IrBinaryOp.SubtractChecked, step.Target, new IrConst(step.Type, 1)));
      var place = this.Place(step.Target);
      var current = this.Expression(step.Target);
      KtExpression Stored(KtExpression value) => step.Target is IrArrayLoad load ? this.Stored(load.Array.Type.Element!, value, load.Type) : value;
      return step.Postfix
        ? new KtCall(Member(current, "also"), []) { Trailing = new KtLambda([], [new KtAssignment(place, "=", Stored(next))]) }
        : new KtCall(Member(next, "also"), []) { Trailing = new KtLambda([], [new KtAssignment(place, "=", Stored(Name("it")))]) };
    }

    /// <summary>
    /// A new exception of a class of the IR's library: the JVM's class of its meaning, or the class the
    /// file declares; without a message the text .NET gives, so that its message is the IR's; an inner
    /// exception becomes the cause, <c>initCause</c>, which every JVM exception takes.
    /// </summary>
    private KtExpression NewException(IrClassType type, IReadOnlyList<IrExpr> arguments) {
      var name = this.ExceptionClass(type);
      if (arguments.Count == 0)
        return Call(name, new KtString(IrExceptionClass.DefaultMessage(type)));

      var created = Call(name, this.Expression(arguments[0]));
      if (arguments.Count == 1 || arguments[1] is IrNull)
        return created;

      return new KtCall(new KtMember(created, "also"), []) { Trailing = new KtLambda([], [new KtExpressionStatement(Method(Name("it"), "initCause", this.Expression(arguments[1])))]) };
    }

    #endregion

  }

}
