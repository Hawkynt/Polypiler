using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.VisualBasic.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VisualBasic.Lowering;

internal sealed partial class VbaLowerer {

  private static IrConst I32(long value) => new(IrType.I32, value);

  private VbaRefusal Mismatch(int line, string operation, VbaType type) => this.Unresolved(line, $"Type mismatch: {operation} of '{type}'");

  #region Expressions

  private VbaValue Expression(VbaExpr expression) {
    switch (expression) {
      case VbaInteger integer:
        return Literal(integer);
      case VbaFloat number:
        if (number.Suffix == "@") {
          var scaled = Math.Round(number.Value * 10000, MidpointRounding.ToEven);
          if (scaled is < -9.2233720368547758E+18 or >= 9.2233720368547758E+18)
            throw this.Unresolved(number.Line, $"the Currency literal {number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}@ does not fit a Currency");
          return new(new IrConst(IrType.I64, (long)scaled), VbaType.Currency);
        }
        return number.Suffix == "!"
          ? new(new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, number.Value)), VbaType.Single)
          : new(new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, number.Value)), VbaType.Double);
      case VbaString text:
        return new(new IrStrConst(text.Value), VbaType.String);
      case VbaDate date:
        return new(new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, date.Value)), VbaType.Date);
      case VbaBoolean boolean:
        return new(new IrConst(IrType.Bool, boolean.Value ? 1 : 0), VbaType.Boolean);
      case VbaEmpty:
        return new(new IrVariantOf(IrVariantTag.Empty, null), VbaType.Variant) { Tags = VbaTags.Empty };
      case VbaNull:
        return new(new IrVariantOf(IrVariantTag.Null, null), VbaType.Variant) { Tags = VbaTags.Null };
      case VbaParenthesized parenthesized:
        return this.Expression(parenthesized.Inner);
      case VbaName name:
        return this.Name(name);
      case VbaMember member:
        return this.Member(member);
      case VbaCall call:
        return this.Call(call);
      case VbaUnary unary:
        return this.Unary(unary);
      case VbaBinary binary:
        return this.Binary(binary);
      default:
        throw this.Unsupported(expression.Line, expression.GetType().Name);
    }
  }

  /// <summary>
  /// An integer literal: its type character decides its type; without one a decimal literal is an
  /// <c>Integer</c> when it fits, a <c>Long</c> when that fits and a <c>Double</c> otherwise, and a
  /// hexadecimal or octal one of up to 16 bits an <c>Integer</c> and of up to 32 bits a <c>Long</c>,
  /// its bits read as two's complement ([MS-VBAL] 3.3.2).
  /// </summary>
  private static VbaValue Literal(VbaInteger integer) {
    var value = integer.Value;
    var type = integer.Suffix switch {
      "%" => VbaType.Integer,
      "&" => VbaType.Long,
      "^" => VbaType.LongLong,
      _ when integer.Radix => integer.Digits <= 16 ? VbaType.Integer : integer.Digits <= 32 ? VbaType.Long : VbaType.LongLong,
      _ => value is >= short.MinValue and <= short.MaxValue ? VbaType.Integer : value is >= int.MinValue and <= int.MaxValue ? VbaType.Long : VbaType.Double
    };
    if (integer.Radix)
      value = type.Kind switch { VbaKind.Integer => (short)value, VbaKind.Long => (int)value, _ => value };

    return type.Kind == VbaKind.Double
      ? new(new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, value)), type)
      : new(new IrConst(type.Ir, value), type);
  }

  private VbaValue Name(VbaName name) {
    if (this._localConstants.TryGetValue(name.Name, out var local))
      return local;
    if (this._locals.TryGetValue(name.Name, out var variable))
      return new(new IrLocalRef(variable.Local), variable.Type) { Tags = this.Flowing(name.Name, variable.Type) ?? variable.Tags };
    if (this._constants.TryGetValue(name.Name, out var constant))
      return constant;
    if (this._globals.TryGetValue(name.Name, out var global))
      return Returned(new IrGlobalRef(global.Global), global.Type);
    if (this._procedures.TryGetValue(name.Name, out var signature) && signature.Returns is not null) {
      var called = this.Call(ref signature, [], name.Line);
      return Returned(called, signature);
    }
    if (this.Builtin(name.Name, [], name.Line) is { } builtin)
      return builtin;

    throw this.Unresolved(name.Line, $"'{name.Name}' is not declared (Option Explicit)");
  }

  private VbaValue Member(VbaMember member) {
    if (member.Target is VbaName { Name: var objectName } && string.Equals(objectName, "Err", StringComparison.OrdinalIgnoreCase)) {
      if (string.Equals(member.Name, "Number", StringComparison.OrdinalIgnoreCase))
        return new(new IrGlobalRef(this.Err), VbaType.Long);
      throw this.Unsupported(member.Line, $"Err.{member.Name}");
    }

    var record = this.Expression(member.Target);
    var (field, type) = this.Field(record.Type, member.Name, member.Line);
    return new(new IrFieldLoad(record.Expr, field), type);
  }

  private VbaValue Call(VbaCall call) {
    if (call.Target is VbaName name) {
      if (this.Lookup(name.Name) is { Type.IsArray: true } || this._localConstants.ContainsKey(name.Name)) {
        if (call.Arguments.Count != 1)
          throw this.Unsupported(call.Line, "a multidimensional array");
        var array = this.Name(name);
        return Element(new IrArrayLoad(array.Expr, this.Index(call.Arguments[0], array.Type, call.Line)), array.Type.Element!);
      }

      if ((this._inference?.Candidates.Contains(name.Name) == true || this.Lookup(name.Name) is { Type.IsVariant: true }) && !this._procedures.ContainsKey(name.Name))
        throw this.Unsupported(call.Line, $"an element of the Variant '{name.Name}', which may hold no array or another kind as well");

      if (this._procedures.TryGetValue(name.Name, out var signature) || this._raw && this._helperSignatures.TryGetValue(name.Name, out signature)) {
        if (signature.Returns is null)
          throw this.Unresolved(call.Line, $"the Sub '{name.Name}' has no value");
        var called = this.Call(ref signature, call.Arguments, call.Line);
        return Returned(called, signature);
      }

      if (this.Builtin(name.Name, call.Arguments, call.Line) is { } builtin)
        return builtin;

      throw this.Unresolved(call.Line, $"'{name.Name}' is no array, function or built-in function of the subset");
    }

    // An element of an array that is a field: r.values(i).
    var target = this.Expression(call.Target);
    if (!target.Type.IsArray || call.Arguments.Count != 1)
      throw this.Unsupported(call.Line, "the call of a member");
    return Element(new IrArrayLoad(target.Expr, this.Index(call.Arguments[0], target.Type, call.Line)), target.Type.Element!);
  }

  /// <summary>An element of an array: one of a <c>Variant</c> array may hold anything.</summary>
  private static VbaValue Element(IrExpr load, VbaType element) => new(load, element) { Tags = element.IsVariant ? VbaTags.Any : VbaTags.None };

  /// <summary>An index of an array, a <c>Long</c> less the array's lower bound.</summary>
  private IrExpr Index(VbaExpr index, VbaType array, int line) {
    if (!array.IsArray)
      throw this.Mismatch(line, "an index", array);

    var position = this.Coerce(this.Expression(index), VbaType.Long, line);
    return array.LowerBound == 0 ? position : new IrBinary(IrBinaryOp.Subtract, position, I32(array.LowerBound));
  }

  /// <summary>The call of a procedure of the module: the arguments coerced to the parameters, an optional one left out given its default.</summary>
  private IrCall Call(Signature signature, IReadOnlyList<VbaExpr> arguments, int line) => this.Call(ref signature, arguments, line);

  /// <summary>
  /// The call of a procedure of the module, of the clone its arguments' kinds choose (#1564), which
  /// <paramref name="signature"/> becomes: the arguments coerced to the parameters, an optional one
  /// left out given its default.
  /// </summary>
  private IrCall Call(ref Signature signature, IReadOnlyList<VbaExpr> arguments, int line) {
    if (arguments.Count > signature.Parameters.Count)
      throw this.Unresolved(line, $"'{signature.Name}' takes {signature.Parameters.Count} arguments, not {arguments.Count}");

    var given = new List<VbaValue?>();
    for (var index = 0; index < signature.Parameters.Count; ++index) {
      var parameter = signature.Parameters[index];
      if (index < arguments.Count)
        given.Add(this.Expression(arguments[index]));
      else if (parameter.Optional)
        given.Add(parameter.Default is { } value ? this.Expression(value) : null);
      else
        throw this.Unresolved(line, $"'{signature.Name}' needs the argument '{parameter.Name}'");
    }

    signature = this.Specialised(signature, given);
    var values = new List<IrExpr>();
    for (var index = 0; index < signature.Parameters.Count; ++index) {
      var parameter = signature.Parameters[index];
      if (given[index] is not { } argument) {
        values.Add(this.Default(parameter.Type, line));
        continue;
      }

      if (index < arguments.Count && parameter.Type.IsArray && argument.Type != parameter.Type)
        throw this.Unresolved(line, $"the array argument {index + 1} of '{signature.Name}' is '{argument.Type}' from {argument.Type.LowerBound}, the parameter '{parameter.Type}' from {parameter.Type.LowerBound}");
      values.Add(this.Coerce(argument, parameter.Type, line));
    }

    if (signature.Name.StartsWith("Ply", StringComparison.Ordinal) && this._helperSignatures.ContainsKey(signature.Name))
      this._usedHelpers.Add(signature.Name);
    return new IrCall(signature.Name, signature.Returns?.Ir ?? IrType.Void, values);
  }

  private VbaValue Unary(VbaUnary unary) {
    var operand = this.Expression(unary.Operand);
    if (operand.Type.IsVariant || operand.Promoted is not null || (operand.IsVariant && unary.Operator == "-"))
      return this.VariantUnary(unary.Operator, operand, unary.Line);
    if (unary.Operator == "Not" && operand.IsVariant) {
      var not = this.Unary(unary, operand with { Tags = VbaTags.None });
      return not with { Tags = TagOf(not.Type.Kind) };
    }

    return this.Unary(unary, operand);
  }

  private VbaValue Unary(VbaUnary unary, VbaValue operand) {
    operand = this.AsNumber(operand);
    if (unary.Operator == "Not") {
      if (operand.Type.Kind == VbaKind.Boolean)
        return new(new IrUnary(IrUnaryOp.LogicalNot, operand.Expr), VbaType.Boolean);
      var type = this.IntegerOperand(operand, unary.Line, "Not");
      var wide = this.Widen(type, Wide(type.Type));
      return new(this.Narrowed(new IrUnary(IrUnaryOp.BitNot, wide), type.Type, IrConversion.Wrap), type.Type);
    }

    if (!operand.Type.IsNumeric)
      throw this.Mismatch(unary.Line, "the negation", operand.Type);
    if (operand.Type.Kind == VbaKind.Currency)
      return new(new IrUnary(IrUnaryOp.NegateChecked, operand.Expr), VbaType.Currency);
    if (operand.Type.Kind == VbaKind.Date)
      return new(this.HelperCall("PlyDateCheck", new IrUnary(IrUnaryOp.Negate, operand.Expr)), VbaType.Date);
    if (operand.Type.IsFloat)
      return new(new IrUnary(IrUnaryOp.Negate, operand.Expr), operand.Type);
    if (operand.Type.Kind is VbaKind.Long or VbaKind.LongLong)
      return new(new IrUnary(IrUnaryOp.NegateChecked, operand.Expr), operand.Type);

    // The negation of a Byte or a Boolean is an Integer; that of an Integer traps for -32768.
    var negated = new IrBinary(IrBinaryOp.Subtract, I32(0), this.Widen(operand, IrType.I32));
    return new(new IrConvert(IrType.I16, negated, IrConversion.Trap), VbaType.Integer);
  }

  private VbaValue Binary(VbaBinary binary) {
    var op = binary.Operator;
    if (op is "Like" or "Is")
      throw this.Unsupported(binary.Line, op);

    var left = this.Expression(binary.Left);
    if (op is "And" or "Or" or "Xor" or "Eqv" or "Imp")
      return this.Logical(op, left, binary.Right, binary.Line);

    var right = this.Expression(binary.Right);
    if (left.IsVariant || right.IsVariant)
      return this.VariantBinary(op, left, right, binary.Line);
    if (op is "=" or "<>" or "<" or "<=" or ">" or ">=")
      return new(this.Compare(op, left, right, binary.Line), VbaType.Boolean);
    if (op == "&")
      return new(new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [this.Text(left, binary.Line), this.Text(right, binary.Line)]), VbaType.String);
    if (op == "+" && left.Type.Kind == VbaKind.String && right.Type.Kind == VbaKind.String)
      return new(new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [left.Expr, right.Expr]), VbaType.String);

    // A String with a number, or in an operator only numbers take, is read as a Double.
    left = this.AsNumber(left);
    right = this.AsNumber(right);
    if (!left.Type.IsNumeric || !right.Type.IsNumeric)
      throw this.Mismatch(binary.Line, $"'{op}'", left.Type.IsNumeric ? right.Type : left.Type);

    return op switch {
      "+" or "-" or "*" => this.Arithmetic(op, ArithmeticType(op, left.Type, right.Type), left, right),
      "/" => this.Divide(left, right),
      "\\" or "Mod" => this.IntegerDivide(op, left, right, binary.Line),
      "^" => new(this.Pow(this.ToFloat(left, IrType.F64), this.ToFloat(right, IrType.F64)), VbaType.Double),
      _ => throw this.Unsupported(binary.Line, op)
    };
  }

  #endregion

  #region Arithmetic

  /// <summary>The type of a sum, difference or product ([MS-VBAL] 5.6.9.3): the wider operand, a <c>Boolean</c> as an <c>Integer</c>, and <c>Double</c> for a <c>Single</c> with a <c>Long</c> or a <c>LongLong</c>.</summary>
  private static VbaType Promote(VbaType left, VbaType right) {
    if (left.Kind == VbaKind.Date || right.Kind == VbaKind.Date)
      return VbaType.Date;
    if (left.Kind == VbaKind.Currency || right.Kind == VbaKind.Currency)
      return VbaType.Currency;
    if (left.Kind == VbaKind.Double || right.Kind == VbaKind.Double)
      return VbaType.Double;
    if (left.Kind == VbaKind.Single || right.Kind == VbaKind.Single) {
      var other = left.Kind == VbaKind.Single ? right : left;
      return other.Kind is VbaKind.Long or VbaKind.LongLong ? VbaType.Double : VbaType.Single;
    }

    var rank = Math.Max(left.Rank, right.Rank);
    return rank switch { 1 => VbaType.Byte, 2 => VbaType.Integer, 3 => VbaType.Long, _ => VbaType.LongLong };
  }

  /// <summary>The IR type integer arithmetic of a type is computed in: 64 bits for a <c>LongLong</c>, 32 for the rest.</summary>
  private static IrType Wide(VbaType type) => type.Kind == VbaKind.LongLong ? IrType.I64 : IrType.I32;

  /// <summary>An integer or <c>Boolean</c> operand in an IR integer type that holds it: <c>True</c> is -1.</summary>
  private IrExpr Widen(VbaValue value, IrType wide) {
    if (value.Type.Kind == VbaKind.Boolean)
      return new IrConditional(value.Expr, new IrConst(wide, -1), new IrConst(wide, 0));
    if (value.Type.Kind == VbaKind.Currency) {
      var whole = this.HelperCall("PlyCurInt", value.Expr);
      return wide == IrType.I64 ? whole : new IrConvert(wide, whole, IrConversion.Trap);
    }

    if (value.Type.IsFloat || value.Type.Kind == VbaKind.Date)
      return new IrConvert(wide, new IrIntrinsic(IrIntrinsicOp.RoundEven, value.Expr.Type, [value.Expr]), IrConversion.Trap);
    return value.Expr.Type == wide ? value.Expr : new IrConvert(wide, value.Expr);
  }

  /// <summary>A number in a floating-point type; a <c>Boolean</c> as -1 or 0.</summary>
  private IrExpr ToFloat(VbaValue value, IrType type) {
    if (value.Type.Kind == VbaKind.Boolean)
      return new IrConditional(value.Expr, new IrConst(type, IrOperators.Bits(type, -1.0)), new IrConst(type, 0));
    if (value.Type.Kind == VbaKind.Currency) {
      var amount = this.HelperCall("PlyCurDbl", value.Expr);
      return type == IrType.F64 ? amount : new IrConvert(type, amount);
    }

    return value.Expr.Type == type ? value.Expr : new IrConvert(type, value.Expr);
  }

  /// <summary>A 32- or 64-bit result back in a narrow type, trapping or wrapping where it does not fit.</summary>
  private IrExpr Narrowed(IrExpr wide, VbaType type, IrConversion contract) => wide.Type == type.Ir ? wide : new IrConvert(type.Ir, wide, contract);

  private VbaValue Arithmetic(string op, VbaType type, VbaValue left, VbaValue right) {
    if (this.ScaledArithmetic(op, type, left, right) is { } scaled)
      return scaled;
    if (type.IsFloat) {
      var a = this.ToFloat(left, type.Ir);
      var b = this.ToFloat(right, type.Ir);
      var floatOp = op switch { "+" => IrBinaryOp.Add, "-" => IrBinaryOp.Subtract, _ => IrBinaryOp.Multiply };
      return new(this.Checked(new IrBinary(floatOp, a, b)), type);
    }

    var wide = Wide(type);
    var l = this.Widen(left, wide);
    var r = this.Widen(right, wide);
    if (type.Kind is VbaKind.Long or VbaKind.LongLong) {
      var checkedOp = op switch { "+" => IrBinaryOp.AddChecked, "-" => IrBinaryOp.SubtractChecked, _ => IrBinaryOp.MultiplyChecked };
      return new(new IrBinary(checkedOp, l, r), type);
    }

    // A Byte or an Integer result fits 32 bits; outside its own type it traps.
    var wrapOp = op switch { "+" => IrBinaryOp.Add, "-" => IrBinaryOp.Subtract, _ => IrBinaryOp.Multiply };
    return new(this.Narrowed(new IrBinary(wrapOp, l, r), type, IrConversion.Trap), type);
  }

  /// <summary>A floating-point result that is infinite or no number raises error 6 or 5, as Visual Basic's arithmetic does.</summary>
  private IrExpr Checked(IrExpr value) => this._raw ? value : this.HelperCall(value.Type == IrType.F32 ? "PlyCheckS" : "PlyCheckD", value);

  /// <summary><c>/</c>: a <c>Single</c> for operands of a <c>Single</c> and the narrow integers, a <c>Double</c> otherwise; a zero divisor raises error 11, 0/0 error 6.</summary>
  private VbaValue Divide(VbaValue left, VbaValue right) {
    var narrow = new[] { left.Type, right.Type }.All(type => type.Kind is VbaKind.Single or VbaKind.Byte or VbaKind.Integer or VbaKind.Boolean)
      && (left.Type.Kind == VbaKind.Single || right.Type.Kind == VbaKind.Single);
    var type = narrow ? VbaType.Single : VbaType.Double;
    var a = this.ToFloat(left, type.Ir);
    var b = this.ToFloat(right, type.Ir);
    return this._raw ? new(new IrBinary(IrBinaryOp.Divide, a, b), type) : new(this.HelperCall(narrow ? "PlyDivS" : "PlyDivD", a, b), type);
  }

  private IrExpr Pow(IrExpr left, IrExpr right)
    => this._raw ? new IrIntrinsic(IrIntrinsicOp.Pow, IrType.F64, [left, right]) : this.HelperCall("PlyPow", left, right);

  /// <summary>An operand of <c>\</c>, <c>Mod</c> and the bitwise operators: an integer, a floating-point number rounded half to even to a <c>Long</c>.</summary>
  private VbaValue IntegerOperand(VbaValue value, int line, string operation) {
    if (value.Type.IsInteger)
      return value;
    if (value.Type.Kind == VbaKind.Boolean)
      return new(this.Widen(value, IrType.I16), VbaType.Integer);
    if (value.Type.IsFloat || value.Type.IsScaled)
      return new(this.Widen(value, IrType.I32), VbaType.Long);
    throw this.Mismatch(line, operation, value.Type);
  }

  /// <summary><c>\</c> and <c>Mod</c>: both operands as integers, the quotient truncated and the remainder of the dividend's sign, in the wider of the two.</summary>
  private VbaValue IntegerDivide(string op, VbaValue left, VbaValue right, int line) {
    var a = this.IntegerOperand(left, line, op);
    var b = this.IntegerOperand(right, line, op);
    var type = Promote(a.Type, b.Type);
    var wide = Wide(type);
    var result = new IrBinary(op == "Mod" ? IrBinaryOp.Remainder : IrBinaryOp.Divide, this.Widen(a, wide), this.Widen(b, wide));
    return new(this.Narrowed(result, type, IrConversion.Trap), type);
  }

  /// <summary>
  /// <c>And</c>, <c>Or</c>, <c>Xor</c>, <c>Eqv</c>, <c>Imp</c>: on two <c>Boolean</c>s logical, evaluating
  /// both operands as Visual Basic does; otherwise bitwise on the integers.
  /// </summary>
  private VbaValue Logical(string op, VbaValue left, VbaExpr rightExpression, int line) {
    var right = this.Expression(rightExpression);
    if (left.Type.IsVariant || right.Type.IsVariant || left.Promoted is not null || right.Promoted is not null)
      return this.VariantLogical(op, left, right, line);
    if (left.IsVariant || right.IsVariant) {
      var typed = this.Logical(op, left with { Tags = VbaTags.None }, right with { Tags = VbaTags.None }, line);
      return typed with { Tags = TagOf(typed.Type.Kind) };
    }

    return this.Logical(op, left, right, line);
  }

  private VbaValue Logical(string op, VbaValue left, VbaValue right, int line) {
    if (left.Type.Kind == VbaKind.Boolean && right.Type.Kind == VbaKind.Boolean) {
      var (l, r) = (left.Expr, right.Expr);
      var always = Pure(r);
      IrExpr True() => new IrConst(IrType.Bool, 1);
      IrExpr False() => new IrConst(IrType.Bool, 0);
      // r ? x : x evaluates r and gives x: how the right operand is evaluated where the left one decides.
      IrExpr Evaluated(IrExpr value) => always ? value : new IrConditional(r, value, value);
      IrExpr result = op switch {
        "And" => always ? new IrBinary(IrBinaryOp.LogicalAnd, l, r) : new IrConditional(l, r, Evaluated(False())),
        "Or" => always ? new IrBinary(IrBinaryOp.LogicalOr, l, r) : new IrConditional(l, Evaluated(True()), r),
        "Xor" => new IrBinary(IrBinaryOp.NotEqual, l, r),
        "Eqv" => new IrBinary(IrBinaryOp.Equal, l, r),
        _ => always ? new IrBinary(IrBinaryOp.LogicalOr, new IrUnary(IrUnaryOp.LogicalNot, l), r) : new IrConditional(l, r, Evaluated(True()))
      };
      return new(result, VbaType.Boolean);
    }

    var a = this.IntegerOperand(left, line, op);
    var b = this.IntegerOperand(right, line, op);
    var type = Promote(a.Type, b.Type);
    var wide = Wide(type);
    var (x, y) = (this.Widen(a, wide), this.Widen(b, wide));
    IrExpr bits = op switch {
      "And" => new IrBinary(IrBinaryOp.And, x, y),
      "Or" => new IrBinary(IrBinaryOp.Or, x, y),
      "Xor" => new IrBinary(IrBinaryOp.Xor, x, y),
      "Eqv" => new IrUnary(IrUnaryOp.BitNot, new IrBinary(IrBinaryOp.Xor, x, y)),
      _ => new IrBinary(IrBinaryOp.Or, new IrUnary(IrUnaryOp.BitNot, x), y)
    };
    return new(this.Narrowed(bits, type, IrConversion.Wrap), type);
  }

  /// <summary>Whether evaluating an expression can neither trap nor change anything, so that evaluating it where the IR would skip it changes nothing.</summary>
  private static bool Pure(IrExpr expression) => expression switch {
    IrConst or IrStrConst or IrLocalRef or IrGlobalRef => true,
    IrUnary { Op: IrUnaryOp.LogicalNot or IrUnaryOp.BitNot } unary => Pure(unary.Operand),
    IrBinary { Op: >= IrBinaryOp.And and <= IrBinaryOp.Xor or >= IrBinaryOp.Equal } binary when !binary.Left.Type.IsString => Pure(binary.Left) && Pure(binary.Right),
    IrConvert { Contract: IrConversion.Wrap } convert when !convert.Operand.Type.IsFloat => Pure(convert.Operand),
    IrFieldLoad load => Pure(load.Target),
    _ => false
  };

  /// <summary>A comparison: of two strings by their code units (<c>Option Compare Binary</c>), of two numbers in the wider type.</summary>
  private IrExpr Compare(string op, VbaValue left, VbaValue right, int line) {
    if (left.Type.IsVariant || right.Type.IsVariant)
      return this.VariantTest(op, left, right, line);
    var irOp = op switch {
      "=" => IrBinaryOp.Equal,
      "<>" => IrBinaryOp.NotEqual,
      "<" => IrBinaryOp.Less,
      "<=" => IrBinaryOp.LessOrEqual,
      ">" => IrBinaryOp.Greater,
      _ => IrBinaryOp.GreaterOrEqual
    };
    if (left.Type.Kind == VbaKind.String && right.Type.Kind == VbaKind.String) {
      if (op is "=" or "<>") {
        var equal = new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [left.Expr, right.Expr]);
        return op == "=" ? equal : new IrUnary(IrUnaryOp.LogicalNot, equal);
      }

      return new IrBinary(irOp, new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [left.Expr, right.Expr]), I32(0));
    }

    if (left.Type.Kind == VbaKind.Boolean && right.Type.Kind == VbaKind.Boolean && op is "=" or "<>")
      return new IrBinary(irOp, left.Expr, right.Expr);

    // A String compared with a number is read as a number.
    if ((left.Type.Kind == VbaKind.String) != (right.Type.Kind == VbaKind.String)) {
      left = this.AsNumber(left);
      right = this.AsNumber(right);
    }

    if (!left.Type.IsNumeric || !right.Type.IsNumeric)
      throw this.Mismatch(line, "a comparison", left.Type.IsNumeric ? right.Type : left.Type);

    // A Currency with a Currency or an integer compares exactly; with anything else, and a Date, as a Double.
    if (left.Type.IsScaled || right.Type.IsScaled) {
      static bool Exact(VbaType type) => type.Kind == VbaKind.Currency || type.IsInteger || type.Kind == VbaKind.Boolean;
      return Exact(left.Type) && Exact(right.Type)
        ? new IrBinary(irOp, this.ToCurrency(left), this.ToCurrency(right))
        : new IrBinary(irOp, this.ToFloat(left, IrType.F64), this.ToFloat(right, IrType.F64));
    }

    var type = Promote(left.Type, right.Type);
    if (type.IsFloat)
      return new IrBinary(irOp, this.ToFloat(left, type.Ir), this.ToFloat(right, type.Ir));
    var wide = Wide(type);
    return new IrBinary(irOp, this.Widen(left, wide), this.Widen(right, wide));
  }

  /// <summary>A condition: a <c>Boolean</c>, or a number that is not zero.</summary>
  private IrExpr Condition(VbaExpr expression) {
    var value = this.Expression(expression);
    if (value.Type.IsVariant)
      return this.VariantCondition(value, expression.Line);
    if (value.Type.Kind == VbaKind.Boolean)
      return value.Expr;
    if (value.Type.Kind == VbaKind.String)
      return this.HelperCall("PlyTextBool", value.Expr);
    if (value.Type.IsNumeric)
      return this.Compare("<>", value, new(I32(0), VbaType.Long), expression.Line);
    throw this.Mismatch(expression.Line, "a condition", value.Type);
  }

  /// <summary>
  /// A value as text, as <c>CStr</c> and <c>&amp;</c> write it: an integer in decimal, a <c>Boolean</c>
  /// as <c>True</c> or <c>False</c>, a floating-point number by <c>FormatVisualBasic</c> with the
  /// decimal separator of the locale. The result of promoting arithmetic is an integer below 10^15 or
  /// the <c>Double</c> it was promoted to, whose text is the same.
  /// </summary>
  private IrExpr Text(VbaValue value, int line) {
    if (value.Type.IsVariant)
      return this.HelperCall((value.Tags & VbaTags.Currency) != VbaTags.None ? "PlyVarCStrC" : "PlyVarCStr", value.Expr);

    if (value.Type.IsFloat)
      return NumberTextOperations.VisualBasic(value.Expr, this._locale.Decimal);

    if (value.Type.Kind == VbaKind.String)
      return value.Expr;
    if (value.Type.Kind == VbaKind.Boolean)
      return new IrConditional(value.Expr, new IrStrConst("True"), new IrStrConst("False"));
    if (value.Type.IsInteger) {
      var wide = this.Widen(value, Wide(value.Type));
      return new IrIntrinsic(IrIntrinsicOp.FormatRadix, IrType.String, [wide, I32(10), I32(1), new IrConst(IrType.Bool, 0), I32((int)RadixMode.SignMagnitude)]);
    }

    if (value.Type.Kind == VbaKind.Currency)
      return this.HelperCall("PlyCurText", value.Expr);
    if (value.Type.Kind == VbaKind.Date)
      throw this.Unsupported(line, "the text of a Date, which Visual Basic writes in the Short Date and Long Time formats of the regional settings");
    throw this.Unsupported(line, $"the text of a {value.Type}");
  }

  /// <summary>A <c>String</c> as the number arithmetic and comparisons read it ([MS-VBAL] §5.6.9.3): a <c>Double</c> by the locale, error 13 for a text of no number; any other value as it is.</summary>
  private VbaValue AsNumber(VbaValue value) => value.Type.Kind == VbaKind.String && !value.IsVariant ? new(this.HelperCall("PlyCDbl", value.Expr), VbaType.Double) : value;

  #endregion

  #region Conversions

  /// <summary>
  /// A value converted to a type as an assignment or a parameter converts it: a floating-point number
  /// to an integer rounded half to even, every number trapping where it does not fit, a <c>Boolean</c>
  /// as -1 or 0 (255 as a <c>Byte</c>), a number to <c>Boolean</c> by whether it is zero.
  /// </summary>
  private IrExpr Coerce(VbaValue value, VbaType target, int line) {
    if (value.Type == target)
      return value.Expr;
    // A dynamic value the inference knows to hold exactly the target's kind is its payload.
    if (value.Type.IsVariant && value.Promoted is null && TagOf(target.Kind) is var kind and not VbaTags.None && value.Tags == kind && VarTypeOf(target, line) is var tag)
      return new IrVariantValue(value.Expr, tag) { IsTested = true };
    if (target.IsVariant)
      return this.Box(value, line);
    if (value.Type.IsVariant)
      return this.Unbox(value, target, line);

    if (target.Kind == VbaKind.Currency && (value.Type.IsNumeric || value.Type.Kind == VbaKind.String))
      return this.ToCurrency(value);
    if (target.Kind == VbaKind.Date && (value.Type.IsNumeric || value.Type.Kind == VbaKind.String))
      return this.ToDate(value);

    if (target.Kind == VbaKind.Boolean && value.Type.IsNumeric)
      return this.Compare("<>", value, new(I32(0), VbaType.Long), line);

    if (target.IsInteger && value.Type.IsScaled) {
      var whole = this.Widen(value, IrType.I64);
      return target.Ir == IrType.I64 ? whole : new IrConvert(target.Ir, whole, IrConversion.Trap);
    }

    if (target.IsInteger && value.Type.IsNumeric) {
      if (value.Type.Kind == VbaKind.Boolean)
        return new IrConditional(value.Expr, new IrConst(target.Ir, target.Kind == VbaKind.Byte ? 255 : -1), new IrConst(target.Ir, 0));
      if (value.Type.IsFloat)
        return new IrConvert(target.Ir, new IrIntrinsic(IrIntrinsicOp.RoundEven, value.Expr.Type, [value.Expr]), IrConversion.Trap);
      return new IrConvert(target.Ir, value.Expr, IrConversion.Trap);
    }

    if (target.IsFloat && value.Type.IsNumeric) {
      var converted = this.ToFloat(value, target.Ir);
      // A Double beyond the range of a Single overflows, error 6.
      return value.Type.Kind == VbaKind.Double && target.Kind == VbaKind.Single ? this.Checked(converted) : converted;
    }

    // A String takes a number or a Boolean as CStr writes it, a number a String as CDbl reads it.
    if (target.Kind == VbaKind.String && value.Type.IsNumeric)
      return this.Text(value, line);
    if (value.Type.Kind == VbaKind.String && target.IsNumeric) {
      return target.Kind switch {
        VbaKind.Boolean => this.HelperCall("PlyTextBool", value.Expr),
        VbaKind.Single => this.HelperCall("PlyCSng", value.Expr),
        _ => this.Coerce(new(this.HelperCall("PlyCDbl", value.Expr), VbaType.Double), target, line)
      };
    }

    if (target.IsArray && value.Type.IsArray && target.Element == value.Type.Element)
      return value.Expr;

    throw this.Unresolved(line, $"Type mismatch: '{value.Type}' where '{target}' is expected");
  }

  #endregion

}
