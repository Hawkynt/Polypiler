using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  /// <summary><c>panic!("Trap")</c>: a trap as the panic whose message is the IR trap's name.</summary>
  private static RsMacro Panic(IrTrap trap) => new("panic", [new RsString(trap.ToString())]);

  /// <summary><c>value.expect("Trap")</c>: an <c>Option</c> or a <c>Result</c> that panics with the trap's name where it holds nothing.</summary>
  private static RsMethodCall Expect(RsExpression value, IrTrap trap) => new(value, "expect", [new RsString(trap.ToString())]) { Type = value.Type };

  /// <summary>
  /// How tightly an expression binds, on the scale of <see cref="RustPrinter.Precedence"/>: an atom or a
  /// postfix form 15, a prefix operator (also a negative literal) 13, a cast 12. An expression that
  /// ends in a block binds loosest of all, so that it is in parentheses wherever it is an operand.
  /// </summary>
  public static int Precedence(RsExpression expression) => expression switch {
    RsLiteral literal when literal.Text.StartsWith('-') => 13,
    RsLiteral or RsString or RsPath or RsParenthesized or RsCall or RsMethodCall or RsMacro or RsIndex or RsArray => 15,
    RsUnary => 13,
    RsCast => 12,
    RsBinary binary => RustPrinter.Precedence(binary.Operator),
    RsRange => 2,
    RsAssign => 1,
    _ => 0
  };

  /// <summary>A literal with its type as suffix, <c>5u32</c>, where nothing around it says the type; any other expression as it is.</summary>
  private static RsExpression Suffixed(RsExpression expression) => expression switch {
    RsLiteral { Type: { } type } literal when TypeName(type) is { } name && type != IrType.Bool => literal with { Text = literal.Text + name },
    RsIf { Then.Tail: { } then, Else: RsBlockExpression { Block.Tail: { } otherwise } elseBlock } choice
      => choice with { Then = choice.Then with { Tail = Suffixed(then) }, Else = elseBlock with { Block = elseBlock.Block with { Tail = Suffixed(otherwise) } } },
    RsIf { Then.Tail: { } then, Else: RsIf chained } choice
      => choice with { Then = choice.Then with { Tail = Suffixed(then) }, Else = Suffixed(chained) },
    RsParenthesized parenthesized => parenthesized with { Inner = Suffixed(parenthesized.Inner) },
    _ => expression
  };

  private sealed partial class Writer {

    /// <summary>An expression in parentheses unless it binds at least as tightly as a place needs.</summary>
    private RsExpression Operand(RsExpression expression, int minimum)
      => Precedence(expression) >= minimum ? expression : new RsParenthesized(expression) { Type = expression.Type };

    /// <summary>The receiver of a method call: a literal with its suffix, anything that is no atom in parentheses.</summary>
    private RsExpression Receiver(RsExpression expression) => this.Operand(Suffixed(expression), 15);

    private RsMethodCall Method(RsExpression receiver, string name, IrType? type, params IReadOnlyList<RsExpression> arguments)
      => new(this.Receiver(receiver), name, arguments) { Type = type };

    private RsCast Cast(RsExpression operand, IrType type) => new(this.Operand(Suffixed(operand), 12), TypeName(type)!) { Type = type };

    /// <summary>
    /// A binary operator with its operands in parentheses where precedence asks for them, and also
    /// where an arithmetic or comparing operator meets a shifting or bitwise one (clippy's
    /// <c>precedence</c>), and around a cast left of <c>&lt;</c>, which Rust would read as a generic.
    /// </summary>
    private RsBinary Binary(RsExpression left, string op, RsExpression right, IrType type) {
      var precedence = RustPrinter.Precedence(op);
      var comparison = precedence == 5;
      RsExpression Side(RsExpression operand, bool isLeft) {
        var minimum = isLeft && !comparison ? precedence : precedence + 1;
        if (operand is RsBinary inner && !IsLogical(op) && !IsLogical(inner.Operator) && !IsComparison(inner.Operator) && IsBitwise(op) != IsBitwise(inner.Operator))
          minimum = 15;
        if (isLeft && operand is RsCast && op is "<" or "<<" or "<=")
          minimum = 15;

        return this.Operand(operand, minimum);
      }

      var leftSide = Side(left, true);
      if (left is RsLiteral && right is RsLiteral)
        leftSide = Suffixed(leftSide);

      return new RsBinary(leftSide, op, Side(right, false)) { Type = type };
    }

    private static bool IsBitwise(string op) => op is "<<" or ">>" or "&" or "^" or "|";

    private static bool IsLogical(string op) => op is "&&" or "||";

    private static bool IsComparison(string op) => RustPrinter.Precedence(op) == 5;

    /// <summary>An IR expression.</summary>
    private RsExpression Expression(IrExpr expression) => expression switch {
      IrConst constant when TypeName(constant.Type) is not null => Literal(constant),
      IrLocalRef reference => new RsPath(this._locals[reference.Local]) { Type = reference.Type },
      IrConstRef constant when !constant.Type.IsArray && this._constants.TryGetValue(constant.Constant.Name, out var name) => new RsPath(name) { Type = constant.Type },
      IrUnary unary => this.Unary(unary),
      IrBinary binary => this.Binary(binary),
      IrConvert convert => this.Convert(convert),
      IrConditional choice => this.Conditional(choice),
      IrCall call when this._functions.TryGetValue(call.Function, out var callee)
        => new RsCall(new RsPath(callee), [.. call.Arguments.Select(this.Expression)]) { Type = call.Type },
      IrStep { Target: IrLocalRef target } step => this.StepExpression(target, step),
      IrArrayLoad { Array: IrConstRef table } load when this._constants.TryGetValue(table.Constant.Name, out var tableName)
        => new RsIndex(new RsPath(tableName), this.Index(load.Index)) { Type = load.Type },
      IrArrayLength { Array: IrConstRef table } when this._constants.TryGetValue(table.Constant.Name, out var tableName)
        => this.Cast(new RsMethodCall(new RsPath(tableName), "len", []), IrType.I32),
      IrIntrinsic intrinsic => this.Intrinsic(intrinsic),
      IrThrowExpression thrown => this.Throw(thrown.Exception),
      IrArrayLoad or IrArrayLength or IrNewArray => this.Refuse("arrays and views"),
      IrStrConst => this.Refuse("strings"),
      _ => this.Refuse(expression.GetType().Name[2..])
    };

    /// <summary>An index into a table: <c>usize</c>, a negative one becoming one no table reaches.</summary>
    private RsExpression Index(IrExpr index) => index is IrConst { Value: >= 0 } constant
      ? (RsExpression)new RsLiteral(constant.Value.ToString(CultureInfo.InvariantCulture))
      : this.Cast(this.Expression(index), IrType.U64) with { Target = "usize" };

    private RsExpression Unary(IrUnary unary) {
      var type = unary.Type;
      switch (unary.Op) {
        case IrUnaryOp.LogicalNot:
          return this.Not(unary.Operand);
        case IrUnaryOp.BitNot:
          return new RsUnary("!", this.Operand(this.Expression(unary.Operand), 13)) { Type = type };
        case IrUnaryOp.Negate when type.IsFloat: {
          var operand = this.Expression(unary.Operand);
          var minimum = operand is RsUnary { Operator: "-" } || operand is RsLiteral literal && literal.Text.StartsWith('-') ? 15 : 13;
          return new RsUnary("-", this.Operand(operand, minimum)) { Type = type };
        }
        case IrUnaryOp.Negate:
          return this.Method(this.Expression(unary.Operand), "wrapping_neg", type);
        default:
          return Expect(this.Method(this.Expression(unary.Operand), "checked_neg", type), IrTrap.IntegerOverflow);
      }
    }

    /// <summary>
    /// The negation of a condition: a comparison of integers or of truth values turned around
    /// (<c>a &gt;= b</c> for <c>!(a &lt; b)</c>, which clippy's <c>nonminimal_bool</c> asks for), an
    /// equality of floating-point numbers too, an ordering of them, which a NaN does not satisfy
    /// either way, kept under <c>!</c>; a negation undone; anything else under <c>!</c>.
    /// </summary>
    private RsExpression Not(IrExpr condition) {
      switch (condition) {
        case IrUnary { Op: IrUnaryOp.LogicalNot } inner:
          return this.Expression(inner.Operand);
        case IrConst { Type.Kind: IrTypeKind.Bool } constant:
          return Literal(new IrConst(IrType.Bool, constant.Value == 0 ? 1 : 0));
        case IrBinary { Op: IrBinaryOp.Equal or IrBinaryOp.NotEqual } equality:
          return this.Binary(equality with { Op = equality.Op == IrBinaryOp.Equal ? IrBinaryOp.NotEqual : IrBinaryOp.Equal });
        case IrBinary { Op: >= IrBinaryOp.Less and <= IrBinaryOp.GreaterOrEqual } order when !order.Left.Type.IsFloat:
          return this.Binary(order with {
            Op = order.Op switch {
              IrBinaryOp.Less => IrBinaryOp.GreaterOrEqual,
              IrBinaryOp.LessOrEqual => IrBinaryOp.Greater,
              IrBinaryOp.Greater => IrBinaryOp.LessOrEqual,
              _ => IrBinaryOp.Less
            }
          });
        default:
          return new RsUnary("!", this.Operand(this.Expression(condition), 13)) { Type = IrType.Bool };
      }
    }

    private RsExpression Binary(IrBinary binary) {
      var type = binary.Type;
      var operandType = binary.Left.Type;
      // A comparison with a truth value constant is the value itself or its negation (clippy's bool_comparison).
      if (binary.Op is IrBinaryOp.Equal or IrBinaryOp.NotEqual && operandType == IrType.Bool) {
        if (binary.Right is IrConst right)
          return (right.Value != 0) == (binary.Op == IrBinaryOp.Equal) ? this.Expression(binary.Left) : this.Not(binary.Left);
        if (binary.Left is IrConst left)
          return (left.Value != 0) == (binary.Op == IrBinaryOp.Equal) ? this.Expression(binary.Right) : this.Not(binary.Right);
      }

      var a = this.Expression(binary.Left);
      var b = this.Expression(binary.Right);
      string Infix() => binary.Op switch {
        IrBinaryOp.Add => "+",
        IrBinaryOp.Subtract => "-",
        IrBinaryOp.Multiply => "*",
        IrBinaryOp.Divide => "/",
        IrBinaryOp.Remainder => "%",
        IrBinaryOp.And => "&",
        IrBinaryOp.Or => "|",
        IrBinaryOp.Xor => "^",
        IrBinaryOp.Equal => "==",
        IrBinaryOp.NotEqual => "!=",
        IrBinaryOp.Less => "<",
        IrBinaryOp.LessOrEqual => "<=",
        IrBinaryOp.Greater => ">",
        IrBinaryOp.GreaterOrEqual => ">=",
        IrBinaryOp.LogicalAnd => "&&",
        _ => "||"
      };

      if (operandType.IsInteger) {
        switch (binary.Op) {
          case IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply:
            return this.Method(a, binary.Op switch { IrBinaryOp.Add => "wrapping_add", IrBinaryOp.Subtract => "wrapping_sub", _ => "wrapping_mul" }, type, b);
          case IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked:
            return Expect(this.Method(a, binary.Op switch { IrBinaryOp.AddChecked => "checked_add", IrBinaryOp.SubtractChecked => "checked_sub", _ => "checked_mul" }, type, b), IrTrap.IntegerOverflow);
          case IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical:
            return this.Shift(binary, a, b);
          // A constant divisor other than 0 and -1 cannot trap: the operator as it is.
          case IrBinaryOp.Divide or IrBinaryOp.Remainder when binary.Right is not IrConst { Value: not 0 and not -1 }:
            return new RsCall(new RsPath(this.Division(binary.Op == IrBinaryOp.Divide, operandType)), [a, b]) { Type = type };
        }
      }

      return this.Binary(a, Infix(), b, type);
    }

    /// <summary>
    /// A shift by the count masked to the width: <c>wrapping_shl</c> and <c>wrapping_shr</c>, which
    /// mask as the IR does; a logical shift of a signed value through its unsigned type.
    /// </summary>
    private RsExpression Shift(IrBinary binary, RsExpression value, RsExpression count) {
      var type = binary.Type;
      var amount = count is RsLiteral literal && !literal.Text.StartsWith('-') ? (RsExpression)(literal with { Type = IrType.U32 }) : this.Cast(count, IrType.U32);
      if (binary.Op == IrBinaryOp.ShiftLeft)
        return this.Method(value, "wrapping_shl", type, amount);

      if (binary.Op == IrBinaryOp.ShiftRightLogical && type.IsSigned) {
        var unsigned = type.Bits == 64 ? IrType.U64 : IrType.U32;
        return this.Cast(this.Method(this.Cast(value, unsigned), "wrapping_shr", unsigned, amount), type);
      }

      return this.Method(value, "wrapping_shr", type, amount);
    }

    /// <summary><c>if c { a } else { b }</c>; with constant truth values the condition itself or its negation.</summary>
    private RsExpression Conditional(IrConditional choice) {
      if (choice is { WhenTrue: IrConst { Type.Kind: IrTypeKind.Bool } yes, WhenFalse: IrConst { Type.Kind: IrTypeKind.Bool } no } && yes.Value != no.Value)
        return yes.Value != 0 ? this.Expression(choice.Condition) : this.Not(choice.Condition);

      // A choice in the second branch is an else if (clippy's collapsible_else_if).
      var otherwise = this.Expression(choice.WhenFalse);
      return new RsIf(this.Expression(choice.Condition), new RsBlock([], this.Expression(choice.WhenTrue)),
        otherwise is RsIf nested ? nested : new RsBlockExpression(new RsBlock([], otherwise))) { Type = choice.Type };
    }

    /// <summary>A step of a variable as an expression: a block that steps it and yields the old or the new value.</summary>
    private RsExpression StepExpression(IrLocalRef target, IrStep step) {
      var place = new RsPath(this._locals[target.Local]) { Type = target.Type };
      var assign = new RsExpressionStatement(new RsAssign(place, "=", this.Stepped(place, step)));
      if (!step.Postfix)
        return new RsBlockExpression(new RsBlock([assign], place)) { Type = target.Type };

      var old = this._locals[target.Local].TrimStart('_') + "_old";
      return new RsBlockExpression(new RsBlock([new RsLet(old, false, null, place), assign], new RsPath(old))) { Type = target.Type };
    }

    /// <summary>
    /// A conversion. Between integers: <c>T::from</c> where every value fits, otherwise <c>as</c> for
    /// the wrapping one and <c>T::try_from(x).expect("IntegerOverflow")</c> for the trapping one
    /// (a range test below Rust 1.34, which brought <c>TryFrom</c>). To floating point <c>as</c>,
    /// which rounds to nearest even, <c>f64::from</c> where it is exact. From floating point to an
    /// integer: <c>as</c> for the saturating one, which Rust's <c>as</c> is; the trapping one through
    /// the spelling function <c>ply_f2i_trap_&lt;type&gt;</c>.
    /// </summary>
    private RsExpression Convert(IrConvert convert) {
      var source = convert.Operand.Type;
      var target = convert.Type;
      if (TypeName(source) is null || TypeName(target) is null || source == IrType.Bool || target == IrType.Bool)
        return this.Refuse($"conversion from {source} to {target}");

      if (source == target)
        return this.Expression(convert.Operand);

      // A constant integer is converted here: a cast of a literal is what clippy's unnecessary_cast warns about.
      if (convert.Operand is IrConst constant && source.IsInteger && target.IsInteger && convert.Contract == IrConversion.Wrap)
        return Literal(new IrConst(target, IrOperators.Wrap(target, (long)(ulong)IrOperators.Number(source, constant.Value))));

      var operand = this.Expression(convert.Operand);
      if (source.IsInteger && target.IsInteger) {
        if (IrOperators.Contains(target, source))
          return new RsCall(new RsPath(TypeName(target)! + "::from"), [Suffixed(operand)]) { Type = target };
        if (convert.Contract != IrConversion.Trap)
          return this.Cast(operand, target);
        if (this.Has(RustCapabilities.TryFrom))
          return Expect(new RsCall(new RsPath(TypeName(target)! + "::try_from"), [operand]) { Type = target }, IrTrap.IntegerOverflow);

        return new RsCall(new RsPath(this.CheckedNarrowing(source, target)), [operand]) { Type = target };
      }

      if (target.IsFloat)
        return source == IrType.F32 && target == IrType.F64 ? new RsCall(new RsPath("f64::from"), [operand]) { Type = target } : this.Cast(operand, target);

      if (convert.Contract == IrConversion.Trap) {
        var wide = source == IrType.F32 ? new RsCall(new RsPath("f64::from"), [operand]) { Type = IrType.F64 } : operand;
        return new RsCall(new RsPath(this.CheckedFloatToInteger(target)), [wide]) { Type = target };
      }

      return this.Cast(operand, target);
    }

    /// <summary>
    /// The spelling function of a trapping conversion from <c>f64</c> to an integer type: accepts a
    /// number strictly between the smallest value minus one and the largest plus one (at or above the
    /// smallest where that minus one is no <c>f64</c>), panics with <c>IntegerOverflow</c> otherwise
    /// and on a NaN, which fails every comparison, then truncates with <c>as</c>.
    /// </summary>
    private string CheckedFloatToInteger(IrType target) {
      var typeName = TypeName(target)!;
      var name = "ply_f2i_trap_" + typeName;
      this.Spell(name, () => {
        var x = new RsPath("x") { Type = IrType.F64 };
        var minimum = (double)IrOperators.Minimum(target);
        var maximum = (double)IrOperators.Maximum(target) + 1;
        var lowExact = target != IrType.I64;
        var low = this.Binary(x, lowExact ? ">" : ">=", Literal(new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, lowExact ? minimum - 1 : minimum))), IrType.Bool);
        var high = this.Binary(x, "<", Literal(new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, maximum))), IrType.Bool);
        var test = this.Binary(low, "&&", high, IrType.Bool);
        var body = new RsBlock([], new RsIf(test, new RsBlock([], this.Cast(x, target)), new RsBlockExpression(new RsBlock([], Panic(IrTrap.IntegerOverflow)))));
        return new RsFunction(name, "", [new RsParameter("x", "f64")], typeName, body);
      });
      return name;
    }

    /// <summary>
    /// The spelling function of an integer division or remainder, whose traps carry the IR's names as
    /// every generated panic does: a zero divisor panics with <c>DivideByZero</c>, the one signed
    /// quotient that does not fit (<c>MIN / -1</c>, and the remainder beside it) with
    /// <c>IntegerOverflow</c> through <c>checked_div</c> and <c>checked_rem</c>; an unsigned one cannot
    /// overflow and is the operator.
    /// </summary>
    private string Division(bool quotient, IrType type) {
      var typeName = TypeName(type)!;
      var name = (quotient ? "ply_div_" : "ply_rem_") + typeName;
      this.Spell(name, () => {
        var a = new RsPath("a") { Type = type };
        var b = new RsPath("b") { Type = type };
        var zero = new RsExpressionStatement(new RsIf(this.Binary(b, "==", new RsLiteral("0") { Type = type }, IrType.Bool),
          new RsBlock([new RsExpressionStatement(Panic(IrTrap.DivideByZero))], null), null));
        RsExpression result = type.IsSigned
          ? Expect(this.Method(a, quotient ? "checked_div" : "checked_rem", type, b), IrTrap.IntegerOverflow)
          : this.Binary(a, quotient ? "/" : "%", b, type);
        return new RsFunction(name, "", [new RsParameter("a", typeName), new RsParameter("b", typeName)], typeName, new RsBlock([zero], result));
      });
      return name;
    }

    /// <summary>The spelling function of a trapping narrowing between integers below Rust 1.34: a range test through <c>i128</c>, then <c>as</c>.</summary>
    private string CheckedNarrowing(IrType source, IrType target) {
      var name = $"ply_i2i_trap_{TypeName(source)}_{TypeName(target)}";
      this.Spell(name, () => {
        var x = new RsPath("x") { Type = source };
        var wide = this.Cast(x, IrType.I64) with { Target = "i128" };
        var low = this.Binary(wide, ">=", new RsLiteral(IrOperators.Minimum(target).ToString(CultureInfo.InvariantCulture)), IrType.Bool);
        var high = this.Binary(wide, "<=", new RsLiteral(IrOperators.Maximum(target).ToString(CultureInfo.InvariantCulture)), IrType.Bool);
        var body = new RsBlock([], new RsIf(this.Binary(low, "&&", high, IrType.Bool), new RsBlock([], this.Cast(x, target)),
          new RsBlockExpression(new RsBlock([], Panic(IrTrap.IntegerOverflow)))));
        return new RsFunction(name, "", [new RsParameter("x", TypeName(source)!)], TypeName(target), body);
      });
      return name;
    }

    /// <summary>An intrinsic the profile writes as a method of the standard library (<see cref="Writes"/>); every other one is refused.</summary>
    private RsExpression Intrinsic(IrIntrinsic intrinsic) {
      if (!Writes(intrinsic, this._profile, this._capabilities))
        return this.Refuse(intrinsic.Op.ToString());

      var arguments = intrinsic.Arguments.Select(this.Expression).ToList();
      var type = intrinsic.Type;
      var first = arguments[0];
      var operand = intrinsic.Arguments[0].Type;
      RsExpression Call(string method, params IReadOnlyList<RsExpression> rest) => this.Method(first, method, operand, rest);
      RsExpression Count(RsExpression count) => count is RsLiteral literal && !literal.Text.StartsWith('-') ? literal with { Type = IrType.U32 } : this.Cast(count, IrType.U32);
      RsExpression As(RsExpression value, IrType from) => from == type ? value with { Type = type } : this.Cast(value with { Type = from }, type);
      return intrinsic.Op switch {
        IrIntrinsicOp.RotateLeft => Call("rotate_left", Count(arguments[1])),
        IrIntrinsicOp.RotateRight => Call("rotate_right", Count(arguments[1])),
        IrIntrinsicOp.PopCount => As(Call("count_ones"), IrType.U32),
        IrIntrinsicOp.LeadingZeroCount => As(Call("leading_zeros"), IrType.U32),
        IrIntrinsicOp.TrailingZeroCount => As(Call("trailing_zeros"), IrType.U32),
        IrIntrinsicOp.ByteSwap => Call("swap_bytes"),
        IrIntrinsicOp.BitReverse => Call("reverse_bits"),
        IrIntrinsicOp.IsPow2 => Call("is_power_of_two"),
        IrIntrinsicOp.Min => Call("min", arguments[1]),
        IrIntrinsicOp.Max => Call("max", arguments[1]),
        IrIntrinsicOp.AddSaturate => Call("saturating_add", arguments[1]),
        IrIntrinsicOp.SubSaturate => Call("saturating_sub", arguments[1]),
        IrIntrinsicOp.MulSaturate => Call("saturating_mul", arguments[1]),
        IrIntrinsicOp.AddOverflows => this.Method(Call("checked_add", arguments[1]), "is_none", IrType.Bool),
        IrIntrinsicOp.SubOverflows => this.Method(Call("checked_sub", arguments[1]), "is_none", IrType.Bool),
        IrIntrinsicOp.MulOverflows => this.Method(Call("checked_mul", arguments[1]), "is_none", IrType.Bool),
        IrIntrinsicOp.Abs when operand.IsInteger => Expect(Call("checked_abs"), IrTrap.IntegerOverflow),
        IrIntrinsicOp.AbsWrap => Call("wrapping_abs"),
        IrIntrinsicOp.Abs => Call("abs"),
        IrIntrinsicOp.Sqrt => Call("sqrt"),
        IrIntrinsicOp.Floor => Call("floor"),
        IrIntrinsicOp.Ceiling => Call("ceil"),
        IrIntrinsicOp.Truncate => Call("trunc"),
        IrIntrinsicOp.RoundAway => Call("round"),
        IrIntrinsicOp.RoundEven => Call("round_ties_even"),
        IrIntrinsicOp.CopySign => Call("copysign", arguments[1]),
        IrIntrinsicOp.FusedMultiplyAdd => Call("mul_add", arguments[1], arguments[2]),
        IrIntrinsicOp.IsNaN => Call("is_nan"),
        IrIntrinsicOp.IsInfinity => Call("is_infinite"),
        IrIntrinsicOp.IsFinite => Call("is_finite"),
        IrIntrinsicOp.IsNegative => Call("is_sign_negative"),
        IrIntrinsicOp.IsNormal => Call("is_normal"),
        IrIntrinsicOp.IsSubnormal => Call("is_subnormal"),
        IrIntrinsicOp.FloatBits => As(Call("to_bits"), operand == IrType.F32 ? IrType.U32 : IrType.U64),
        _ => new RsCall(new RsPath(TypeName(type)! + "::from_bits"), [operand.IsSigned ? this.Cast(first, operand == IrType.I32 ? IrType.U32 : IrType.U64) : first]) { Type = type }
      };
    }

  }

}
