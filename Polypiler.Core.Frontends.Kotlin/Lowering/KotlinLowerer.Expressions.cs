using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Kotlin.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Kotlin.Lowering;

internal sealed partial class KotlinLowerer {

  /// <summary>The expressions whose Kotlin type is <c>Char</c>, which the IR holds as the <c>u16</c> of the code unit.</summary>
  private readonly HashSet<IrExpr> _chars = new(ReferenceEqualityComparer.Instance);

  /// <summary>The locals whose Kotlin type is <c>Char</c>.</summary>
  private readonly HashSet<IrLocal> _charLocals = [];

  private T Char<T>(T expression) where T : IrExpr {
    this._chars.Add(expression);
    return expression;
  }

  private bool IsChar(IrExpr expression) => this._chars.Contains(expression) || expression is IrLocalRef reference && this._charLocals.Contains(reference.Local);

  /// <summary>Marks a local as a <c>Char</c> where its declared type, or else its value, is one.</summary>
  private void MarkChar(IrLocal local, KTypeRef? type, KExpression? value = null) {
    if (type?.Name is "Char" || type is null && value is KChar)
      this._charLocals.Add(local);
  }

  /// <summary>
  /// A value as a place of a type takes it: a constant integer that fits the type as a constant of it,
  /// as Kotlin types an integer literal by its expected type; any value of the type as itself.
  /// Kotlin converts nothing else implicitly.
  /// </summary>
  private IrExpr Coerce(IrExpr value, IrType type, KNode at) {
    if (value.Type == type)
      return value;

    if (value is IrConst constant && constant.Type.IsInteger && type.IsInteger && Fits(IrOperators.Number(constant.Type, constant.Value), type))
      return new IrConst(type, IrOperators.Wrap(type, (long)IrOperators.Number(constant.Type, constant.Value)));

    throw Refuse(KotlinFrontend.Scalar, at, $"a {value.Type} where a {type} is expected: Kotlin converts numbers only explicitly");
  }

  private static bool Fits(Int128 value, IrType type) => value >= IrOperators.Minimum(type) && value <= IrOperators.Maximum(type);

  private IrExpr Expression(KExpression expression, IrType? expected) {
    switch (expression) {
      case KInteger integer:
        return Integer(integer, expected);
      case KFloat number:
        return number.Single
          ? new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, (float)number.Value))
          : new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, number.Value));
      case KBoolean truth:
        return new IrConst(IrType.Bool, truth.Value ? 1 : 0);
      case KChar character:
        return this.Char(new IrConst(IrType.U16, character.Value));
      case KString text:
        return new IrStrConst(text.Value);
      case KTemplate template:
        return template.Parts.Select(part => this.Text(this.Expression(part, null), part)).Aggregate((left, right) => Concat(left, right));
      case KName name:
        return this.Name(name);
      case KMember member:
        return this.Member(member);
      case KCall call:
        return this.Call(call, expected);
      case KIndex index:
        return this.Index(index);
      case KPrefix prefix:
        return this.Prefix(prefix, expected);
      case KPostfix { Operator: "++" or "--" } postfix:
        return this.Step(postfix.Operand, postfix.Operator == "++", true);
      case KBinary binary:
        return this.Binary(binary, expected);
      case KIf conditional:
        return this.Conditional(conditional, expected);
      case KWhen choice:
        return this.WhenValue(choice, expected);
      case KThrowExpression raise when expected is not null && expected != IrType.Void:
        return new IrThrowExpression(expected, this.Expression(raise.Value, null));
      case KNull:
        throw Refuse(KotlinFrontend.Type, expression, "null is outside the imported subset: it has no nullable types");
      case KLambda:
        throw Refuse(KotlinFrontend.Function, expression, "a lambda is outside the imported subset where it is no argument of IntArray(n) or repeat");
      default:
        throw Refuse(KotlinFrontend.Scalar, expression, $"the expression '{expression.GetType().Name[1..]}' is outside the imported subset");
    }
  }

  /// <summary>
  /// An integer literal typed as Kotlin types it: by its suffix - <c>L</c> a <c>Long</c>, <c>u</c> a
  /// <c>UInt</c> or, when it does not fit, a <c>ULong</c>, <c>uL</c> a <c>ULong</c> - otherwise by the
  /// expected integer type it fits, else an <c>Int</c>, else a <c>Long</c>.
  /// </summary>
  private static IrConst Integer(KInteger integer, IrType? expected) {
    var value = (Int128)integer.Value;
    IrType type;
    if (integer.Unsigned)
      type = integer.Long ? IrType.U64 : expected is { IsInteger: true, IsSigned: false } && Fits(value, expected) ? expected : Fits(value, IrType.U32) ? IrType.U32 : IrType.U64;
    else if (integer.Long)
      type = Fits(value, IrType.I64) ? IrType.I64 : throw new KotlinRefusal(KotlinFrontend.Scalar, integer.At, $"the literal {integer.Value}L does not fit a Long");
    else if (expected is { IsInteger: true, IsSigned: true } && Fits(value, expected))
      type = expected;
    else
      type = Fits(value, IrType.I32) ? IrType.I32 : Fits(value, IrType.I64) ? IrType.I64 : throw new KotlinRefusal(KotlinFrontend.Scalar, integer.At, $"the literal {integer.Value} does not fit a Long");

    return new IrConst(type, IrOperators.Wrap(type, (long)integer.Value));
  }

  /// <summary>A variable, a parameter or a property of the file.</summary>
  private IrExpr Name(KName name) {
    if (this.Scope.Find(name.Name) is { } variable)
      return new IrLocalRef(variable.Local);

    if (this._properties.TryGetValue(name.Name, out var property))
      return property.Constant is { } constant ? new IrConstRef(constant) : new IrGlobalRef(property.Global!);

    throw Refuse(KotlinFrontend.Function, name, $"the name '{name.Name}' is not declared");
  }

  #region Members

  /// <summary>
  /// A member read: a constant of a number type (<c>Int.MAX_VALUE</c>, <c>Double.NaN</c>), the size of
  /// an array, the length of a string, the code of a character, the last index.
  /// </summary>
  private IrExpr Member(KMember member) {
    if (member.Safe)
      throw Refuse(KotlinFrontend.Type, member, "a safe call is outside the imported subset: it has no nullable types");

    if (member.Receiver is KName { Name: var owner } && Scalar(owner) is { } type && this.Scope.Find(owner) is null)
      return this.TypeConstant(type, member);

    var receiver = this.Expression(member.Receiver, null);
    switch (member.Name) {
      case "size" when receiver.Type.IsArray:
        return new IrArrayLength(receiver);
      case "length" when receiver.Type.IsString:
        return new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [receiver]);
      case "lastIndex" when receiver.Type.IsArray || receiver.Type.IsString:
        return new IrBinary(IrBinaryOp.Subtract, receiver.Type.IsArray ? new IrArrayLength(receiver) : new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [receiver]), new IrConst(IrType.I32, 1));
      case "code" when this.IsChar(receiver):
        return new IrConvert(IrType.I32, receiver);
      case "absoluteValue" when receiver.Type.IsNumeric:
        return Absolute(receiver, member);
      default:
        throw Refuse(KotlinFrontend.Function, member, $"the member '{member.Name}' of a {receiver.Type} is outside the imported subset");
    }
  }

  /// <summary><c>Int.MAX_VALUE</c> and its siblings, <c>SIZE_BITS</c>, <c>SIZE_BYTES</c>, and the special values of the floating-point types.</summary>
  private IrExpr TypeConstant(IrType type, KMember member) {
    if (type.IsInteger) {
      return member.Name switch {
        "MAX_VALUE" => new IrConst(type, IrOperators.Wrap(type, (long)IrOperators.Maximum(type))),
        "MIN_VALUE" => new IrConst(type, IrOperators.Wrap(type, (long)IrOperators.Minimum(type))),
        "SIZE_BITS" => new IrConst(IrType.I32, type.Bits),
        "SIZE_BYTES" => new IrConst(IrType.I32, type.Bits / 8),
        _ => throw Refuse(KotlinFrontend.Function, member, $"the member '{member.Name}' of {type} is outside the imported subset")
      };
    }

    if (type.IsFloat) {
      var value = member.Name switch {
        "NaN" => double.NaN,
        "POSITIVE_INFINITY" => double.PositiveInfinity,
        "NEGATIVE_INFINITY" => double.NegativeInfinity,
        "MAX_VALUE" => type == IrType.F32 ? float.MaxValue : double.MaxValue,
        "MIN_VALUE" => type == IrType.F32 ? float.Epsilon : double.Epsilon,
        _ => throw Refuse(KotlinFrontend.Function, member, $"the member '{member.Name}' of {type} is outside the imported subset")
      };
      return new IrConst(type, type == IrType.F32 ? IrOperators.Bits(type, (float)value) : IrOperators.Bits(type, value));
    }

    throw Refuse(KotlinFrontend.Function, member, $"the member '{member.Name}' of {type} is outside the imported subset");
  }

  /// <summary>The absolute value as Kotlin's <c>abs</c> gives it: of the smallest signed value the value itself, which the IR's checked <c>Abs</c> would trap on.</summary>
  private static IrExpr Absolute(IrExpr value, KNode at) => value.Type.IsFloat
    ? new IrIntrinsic(IrIntrinsicOp.Abs, value.Type, [value])
    : value.Type == IrType.I32 || value.Type == IrType.I64
      ? new IrIntrinsic(IrIntrinsicOp.AbsWrap, value.Type, [value])
      : throw Refuse(KotlinFrontend.Scalar, at, $"abs of a {value.Type}, which Kotlin does not have");

  #endregion

  #region Operators

  private IrExpr Prefix(KPrefix prefix, IrType? expected) {
    switch (prefix.Operator) {
      case "!":
        return new IrUnary(IrUnaryOp.LogicalNot, this.Condition(prefix.Operand));
      case "++" or "--":
        return this.Step(prefix.Operand, prefix.Operator == "++", false);
    }

    var operand = this.Expression(prefix.Operand, expected);
    if (!operand.Type.IsNumeric || operand.Type.IsInteger && !operand.Type.IsSigned)
      throw Refuse(KotlinFrontend.Scalar, prefix, $"the operator '{prefix.Operator}' of a {operand.Type}, which Kotlin does not have");

    // Kotlin's unary operators of Byte and Short give an Int.
    var type = operand.Type.IsFloat ? operand.Type : operand.Type == IrType.I64 ? IrType.I64 : IrType.I32;
    if (operand is IrConst constant && type.IsInteger) {
      var number = IrOperators.Number(constant.Type, constant.Value);
      return prefix.Operator == "+" ? new IrConst(type, (long)number) : new IrConst(type, IrOperators.Wrap(type, (long)-number));
    }

    if (operand is IrConst floating && type.IsFloat && prefix.Operator == "-")
      return new IrConst(type, type == IrType.F32
        ? IrOperators.Bits(type, -BitConverter.Int32BitsToSingle((int)floating.Value))
        : IrOperators.Bits(type, -BitConverter.Int64BitsToDouble(floating.Value)));

    var widened = Widen(operand, type);
    return prefix.Operator == "+" ? widened : new IrUnary(IrUnaryOp.Negate, widened);
  }

  /// <summary>A value of a smaller integer type in a larger one, as Kotlin's operators widen <c>Byte</c> and <c>Short</c>; a value of the type as itself.</summary>
  private static IrExpr Widen(IrExpr value, IrType type)
    => value.Type == type ? value : value is IrConst constant && !type.IsFloat ? new IrConst(type, IrOperators.Wrap(type, (long)IrOperators.Number(constant.Type, constant.Value)))
      : value is IrConst number && type.IsFloat && number.Type.IsFloat && type == IrType.F64 ? new IrConst(type, IrOperators.Bits(type, BitConverter.Int32BitsToSingle((int)number.Value)))
      : new IrConvert(type, value);

  /// <summary><c>x++</c>, <c>++x</c>: a step of a variable or an array element, which wraps as Kotlin's <c>inc</c> does; a <c>Char</c> stays one.</summary>
  private IrExpr Step(KExpression target, bool increment, bool postfix) {
    IrExpr place = target switch {
      KName name => this.Assignable(name),
      KIndex index => this.Index(index),
      _ => throw Refuse(KotlinFrontend.Scalar, target, "the step of anything but a variable or an array element")
    };
    if (place is IrIntrinsic)
      throw Refuse(KotlinFrontend.Memory, target, "a string cannot be changed: Kotlin's String has no set operator");

    if (!place.Type.IsNumeric)
      throw Refuse(KotlinFrontend.Scalar, target, $"the step of a {place.Type}");

    if (place is IrConstRef or IrGlobalRef)
      throw Refuse(KotlinFrontend.Scalar, target, "the step of a property of the file is outside the imported subset");

    var step = new IrStep(place, increment, postfix, false);
    return this.IsChar(place) ? this.Char(step) : step;
  }

  /// <summary>A name that may be assigned, as its value: a <c>var</c>, or a <c>var</c> property of the file.</summary>
  private IrExpr Assignable(KName name) {
    _ = this.Place(name);
    return this.Name(name);
  }

  private IrExpr Binary(KBinary binary, IrType? expected) {
    switch (binary.Operator) {
      case "&&" or "||":
        return new IrBinary(binary.Operator == "&&" ? IrBinaryOp.LogicalAnd : IrBinaryOp.LogicalOr, this.Condition(binary.Left), this.Condition(binary.Right));
      case "in" or "!in": {
        var value = this.Once(this.Expression(binary.Left, null), "value");
        var inside = this.Contains(binary.Right, value, binary);
        return binary.Operator == "in" ? inside : new IrUnary(IrUnaryOp.LogicalNot, inside);
      }
      case ".." or "..<" or "until" or "downTo" or "step":
        throw Refuse(KotlinFrontend.Control, binary, $"a range '{binary.Operator}' outside a for loop or an in test is outside the imported subset");
      case "?:":
        throw Refuse(KotlinFrontend.Type, binary, "the elvis operator is outside the imported subset: it has no nullable types");
    }

    var left = this.Expression(binary.Left, expected is { IsNumeric: true } ? expected : null);
    var right = this.Expression(binary.Right, binary.Operator is "shl" or "shr" or "ushr" ? IrType.I32 : left.Type.IsNumeric ? left.Type : null);
    // A literal on the left takes the type of the operand on the right.
    if (binary.Left is KInteger && left is IrConst && right.Type.IsInteger && left.Type != right.Type && !this.IsChar(right))
      left = this.Expression(binary.Left, right.Type);

    return this.Operator(left, binary.Operator, right, binary);
  }

  /// <summary>
  /// A binary operator of Kotlin on two lowered operands: the operands converted to the type Kotlin's
  /// operator gives them (<see cref="Arithmetic"/>), the comparison of strings and characters, the
  /// concatenation of a string with the text of any value, the infix bit functions of one type, and
  /// the quotient and remainder of a signed type through the module's functions that wrap the
  /// smallest value divided by -1.
  /// </summary>
  private IrExpr Operator(IrExpr left, string op, IrExpr right, KNode at) {
    if (left.Type.IsString && op == "+")
      return Concat(left, this.Text(right, at));

    if (left.Type.IsString && right.Type.IsString && op is "==" or "!=" or "<" or "<=" or ">" or ">=") {
      if (op is "==" or "!=") {
        var equal = new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [left, right]);
        return op == "==" ? equal : new IrUnary(IrUnaryOp.LogicalNot, equal);
      }

      return new IrBinary(Comparison(op), new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [left, right]), new IrConst(IrType.I32, 0));
    }

    if (this.IsChar(left))
      return this.CharOperator(left, op, right, at);

    if (left.Type == IrType.Bool && right.Type == IrType.Bool) {
      // Kotlin's and, or and xor of Booleans evaluate both operands, which the IR's bit operations of
      // 32 bits do on the truth values as 1 and 0; xor is the inequality.
      IrExpr Bit(IrExpr truth) => new IrConditional(truth, new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 0));
      IrExpr Both(IrBinaryOp bit) => new IrBinary(IrBinaryOp.NotEqual, new IrBinary(bit, Bit(left), Bit(right)), new IrConst(IrType.I32, 0));
      return op switch {
        "==" => new IrBinary(IrBinaryOp.Equal, left, right),
        "!=" or "xor" => new IrBinary(IrBinaryOp.NotEqual, left, right),
        "and" => Both(IrBinaryOp.And),
        "or" => Both(IrBinaryOp.Or),
        _ => throw Refuse(KotlinFrontend.Scalar, at, $"the operator '{op}' of two Booleans")
      };
    }

    switch (op) {
      case "shl" or "shr" or "ushr": {
        if (!(left.Type.IsWideInteger) || right.Type != IrType.I32)
          throw Refuse(KotlinFrontend.Scalar, at, $"'{op}' of a {left.Type} by a {right.Type}: Kotlin shifts Int, Long, UInt and ULong by an Int");

        var shift = op switch {
          "shl" => IrBinaryOp.ShiftLeft,
          "shr" => IrBinaryOp.ShiftRight,
          _ when left.Type.IsSigned => IrBinaryOp.ShiftRightLogical,
          _ => throw Refuse(KotlinFrontend.Scalar, at, $"'ushr' of a {left.Type}, which Kotlin does not have: shr of an unsigned type is logical")
        };
        return new IrBinary(shift, left, right);
      }
      case "and" or "or" or "xor": {
        if (!left.Type.IsInteger || left.Type != right.Type || left.Type.IsSigned && !left.Type.IsWideInteger)
          throw Refuse(KotlinFrontend.Scalar, at, $"'{op}' of a {left.Type} and a {right.Type}: Kotlin's bit functions take two values of one of Int, Long, UByte, UShort, UInt and ULong");

        var bit = op == "and" ? IrBinaryOp.And : op == "or" ? IrBinaryOp.Or : IrBinaryOp.Xor;
        // UByte and UShort have the bit functions of Kotlin; the IR has them on 32 bits, which change nothing of the low ones.
        return left.Type.IsWideInteger ? new IrBinary(bit, left, right) : new IrConvert(left.Type, new IrBinary(bit, Widen(left, IrType.U32), Widen(right, IrType.U32)));
      }
    }

    var type = Arithmetic(left.Type, right.Type)
      ?? throw Refuse(KotlinFrontend.Scalar, at, $"the operator '{op}' of a {left.Type} and a {right.Type}, which Kotlin does not have");
    var (a, b) = (Widen(left, type), Widen(right, type));
    switch (op) {
      case "+":
        return new IrBinary(IrBinaryOp.Add, a, b);
      case "-":
        return new IrBinary(IrBinaryOp.Subtract, a, b);
      case "*":
        return new IrBinary(IrBinaryOp.Multiply, a, b);
      case "/" or "%" when type.IsSigned && !(b is IrConst { Value: not -1 and not 0 }):
        return new IrCall(this.DivisionHelper(op == "/", type), type, [a, b]);
      case "/":
        return new IrBinary(IrBinaryOp.Divide, a, b);
      case "%":
        return new IrBinary(IrBinaryOp.Remainder, a, b);
      case "==" or "!=" or "<" or "<=" or ">" or ">=" or "===" or "!==":
        return new IrBinary(Comparison(op), a, b);
      default:
        throw Refuse(KotlinFrontend.Scalar, at, $"the operator or infix function '{op}' is outside the imported subset");
    }
  }

  private static IrBinaryOp Comparison(string op) => op switch {
    "==" or "===" => IrBinaryOp.Equal,
    "!=" or "!==" => IrBinaryOp.NotEqual,
    "<" => IrBinaryOp.Less,
    "<=" => IrBinaryOp.LessOrEqual,
    ">" => IrBinaryOp.Greater,
    _ => IrBinaryOp.GreaterOrEqual
  };

  /// <summary>
  /// An operator of a <c>Char</c>: the comparisons with another, the distance to another (an
  /// <c>Int</c>), and a <c>Char</c> moved by an <c>Int</c>, which wraps within the 16 bits.
  /// </summary>
  private IrExpr CharOperator(IrExpr left, string op, IrExpr right, KNode at) {
    if (this.IsChar(right) || right is IrConst { Type.Kind: IrTypeKind.U16 }) {
      // The IR compares and subtracts 32-bit values: a code unit is widened first.
      return op switch {
        "-" => new IrBinary(IrBinaryOp.Subtract, Widen(left, IrType.I32), Widen(right, IrType.I32)),
        "==" or "!=" or "<" or "<=" or ">" or ">=" => new IrBinary(Comparison(op), Widen(left, IrType.I32), Widen(right, IrType.I32)),
        _ => throw Refuse(KotlinFrontend.Scalar, at, $"the operator '{op}' of two Chars")
      };
    }

    if (right.Type == IrType.I32 && op is "+" or "-")
      return this.Char(new IrConvert(IrType.U16, new IrBinary(op == "+" ? IrBinaryOp.Add : IrBinaryOp.Subtract, new IrConvert(IrType.I32, left), right)));

    throw Refuse(KotlinFrontend.Scalar, at, $"the operator '{op}' of a Char and a {right.Type}");
  }

  /// <summary>
  /// The function of the module for Kotlin's quotient or remainder of a signed type: the IR's division
  /// traps on the smallest value divided by -1, Kotlin's wraps to the smallest value and its remainder
  /// is 0; a zero divisor throws in both.
  /// </summary>
  private string DivisionHelper(bool divide, IrType type) {
    var name = $"kotlin_{(divide ? "divide" : "remainder")}_{type}";
    if (this._helpers.ContainsKey(name))
      return name;

    var (a, b) = (new IrLocal("a", type), new IrLocal("b", type));
    IrExpr minusOne = new IrConst(type, -1);
    var body = new IrBlock([
      new IrIf(new IrBinary(IrBinaryOp.Equal, new IrLocalRef(b), minusOne),
        new IrBlock([new IrReturn(divide ? new IrUnary(IrUnaryOp.Negate, new IrLocalRef(a)) : new IrConst(type, 0))]), null),
      new IrReturn(new IrBinary(divide ? IrBinaryOp.Divide : IrBinaryOp.Remainder, new IrLocalRef(a), new IrLocalRef(b)))
    ]);
    this._irNames.Add(name);
    this._helpers[name] = new IrFunction(name, false, type, [a, b], [], body);
    return name;
  }

  /// <summary>Two strings one after the other.</summary>
  private static IrExpr Concat(IrExpr left, IrExpr right) => new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [left, right]);

  /// <summary>
  /// The text of a value as Kotlin's <c>toString</c> gives it, where the IR has the same: a string as
  /// itself, an integer in decimal, a truth value as <c>true</c> or <c>false</c>, a character as the
  /// string of its code unit. A floating-point number's text is the JVM's, which no operation of the
  /// IR writes.
  /// </summary>
  private IrExpr Text(IrExpr value, KNode at) {
    if (value.Type.IsString)
      return value;

    if (this.IsChar(value))
      return new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnit, IrType.String, [value]);

    if (value.Type == IrType.Bool)
      return new IrIntrinsic(IrIntrinsicOp.FormatBoolEcma, IrType.String, [value]);

    if (value.Type.IsInteger)
      return new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [value]);

    throw Refuse(KotlinFrontend.Memory, at, $"the text of a {value.Type} is the JVM's, which the imported subset does not write");
  }

  #endregion

  #region Conditional values

  /// <summary><c>if (c) a else b</c> as a value of one expression per branch; the branches must have one type.</summary>
  private IrExpr Conditional(KIf conditional, IrType? expected) {
    if (conditional.Else is null)
      throw Refuse(KotlinFrontend.Control, conditional, "an if without an else has no value");

    if (Last(conditional.Then) is not { } then || Last(conditional.Else) is not { } otherwise || IsStatements(conditional.Then) || IsStatements(conditional.Else))
      throw Refuse(KotlinFrontend.Control, conditional, "an if whose branches hold statements has a value only as an initializer, an assignment or a return");

    var condition = this.Condition(conditional.Condition);
    var (a, b) = this.Branches(then, otherwise, expected, conditional);
    return new IrConditional(condition, a, b);
  }

  /// <summary>Two branch values of one type: a literal of one takes the other's type.</summary>
  private (IrExpr First, IrExpr Second) Branches(KExpression first, KExpression second, IrType? expected, KNode at) {
    var a = this.Expression(first, expected);
    var b = this.Expression(second, expected ?? a.Type);
    if (a.Type != b.Type && first is KInteger)
      a = this.Expression(first, b.Type);

    if (a.Type != b.Type)
      throw Refuse(KotlinFrontend.Scalar, at, $"branches of a {a.Type} and a {b.Type}, whose common type the imported subset does not have");

    var conditional = (a, b);
    if (this.IsChar(a))
      this.Char(b);

    return conditional;
  }

  /// <summary><c>when</c> as a value of one expression per branch: a chain of conditional values.</summary>
  private IrExpr WhenValue(KWhen choice, IrType? expected) {
    if (choice.Entries.Any(entry => IsStatements(entry.Body)))
      throw Refuse(KotlinFrontend.Control, choice, "a when whose branches hold statements has a value only as an initializer, an assignment or a return");

    var otherwise = choice.Entries.FirstOrDefault(entry => entry.Conditions.Count == 0)
      ?? throw Refuse(KotlinFrontend.Control, choice, "a when whose value is used needs an else branch in the imported subset");
    var subject = choice.Subject is { } expression ? this.Once(this.Expression(expression, null), "subject") : null;
    var fallback = this.Expression(Last(otherwise.Body)!, expected);
    var type = fallback.Type;
    var result = fallback;
    foreach (var entry in choice.Entries.Where(entry => entry.Conditions.Count > 0).Reverse()) {
      var value = this.Coerce(this.Expression(Last(entry.Body)!, type), type, entry);
      var condition = entry.Conditions.Select(test => this.Test(test, subject)).Aggregate((x, y) => new IrBinary(IrBinaryOp.LogicalOr, x, y));
      result = new IrConditional(condition, value, result);
    }

    return result;
  }

  #endregion

}
