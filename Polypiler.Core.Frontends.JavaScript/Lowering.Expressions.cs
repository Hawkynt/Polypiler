using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

internal sealed partial class Lowering {

  /// <summary>
  /// A lowered expression and what is known about its JavaScript value beyond the IR type.
  /// </summary>
  /// <param name="Expr">The IR that computes the value.</param>
  /// <param name="IsNumber">
  /// The value is the result of <c>+</c> or <c>-</c>, which JavaScript computes exactly and which may
  /// therefore lie outside 32 bits. <paramref name="Expr"/> is an <c>i32</c> that holds the value
  /// modulo 2^32, which is all a coercion needs; anything that needs the value itself refuses it.
  /// </param>
  /// <param name="IsByte">The value lies in 0…255 whatever the operands are: a <c>uint8</c>, or masked with a constant that small.</param>
  private readonly record struct Value(IrExpr Expr, bool IsNumber = false, bool IsByte = false) {

    public IrType Type => this.Expr.Type;

  }

  private Value Expression(Node node) {
    switch (node) {
      case NumericLiteral literal:
        return this.Number(literal, literal.Value);
      case BooleanLiteral literal:
        return new Value(new IrConst(IrType.Bool, literal.Value ? 1 : 0));
      case Literal literal:
        throw this.Unsupported(literal, literal.Raw);
      case Identifier identifier:
        return this._scope.TryGetValue(identifier.Name, out var local)
          ? new Value(new IrLocalRef(local), false, local.Type == IrType.U8)
          : throw this.Unsupported(identifier, identifier.Name);
      case UpdateExpression:
        throw this.Unsupported(node, "++ or -- inside an expression");
      case UnaryExpression unary:
        return this.Unary(unary);
      case LogicalExpression logical:
        return this.Logical(logical);
      case BinaryExpression binary:
        return this.Binary(binary);
      case ConditionalExpression choice:
        return this.Conditional(choice);
      case MemberExpression member:
        return this.Member(member);
      case CallExpression call:
        return this.Call(call);
      case NewExpression creation:
        return this.New(creation);
      default:
        throw this.Unsupported(node, Describe(node) ?? this.Text(node));
    }
  }

  /// <summary>
  /// An integer literal: an <c>int32</c> when it is one, otherwise a <c>uint32</c>. A literal that is
  /// neither is no value of the subset; nothing is rounded or wrapped to make it one.
  /// </summary>
  private Value Number(Node node, double value) {
    if (value != System.Math.Floor(value) || value < int.MinValue || value > uint.MaxValue || (value == 0 && double.IsNegative(value)))
      throw this.Unsupported(node, this.Text(node));

    return new Value(new IrConst(value <= int.MaxValue ? IrType.I32 : IrType.U32, (long)value));
  }

  private Value Unary(UnaryExpression unary) {
    switch (unary.Operator) {
      case Operator.LogicalNot:
        return new Value(new IrUnary(IrUnaryOp.LogicalNot, this.Condition(unary.Argument)));
      case Operator.BitwiseNot:
        return new Value(new IrUnary(IrUnaryOp.BitNot, this.Int32(this.Expression(unary.Argument), unary.Argument)));
      case Operator.UnaryNegation when unary.Argument is NumericLiteral literal:
        return this.Number(unary, -literal.Value);
      case Operator.UnaryNegation:
        return new Value(new IrBinary(IrBinaryOp.Subtract, new IrConst(IrType.I32, 0), this.Int32(this.Expression(unary.Argument), unary.Argument)), true);
      default:
        throw this.Unsupported(unary, OperatorText(unary.Operator));
    }
  }

  private Value Logical(LogicalExpression logical) {
    if (logical.Operator == Operator.NullishCoalescing)
      throw this.Unsupported(logical, OperatorText(logical.Operator));

    var op = logical.Operator == Operator.LogicalAnd ? IrBinaryOp.LogicalAnd : IrBinaryOp.LogicalOr;
    return new Value(new IrBinary(op, this.Condition(logical.Left), this.Condition(logical.Right)));
  }

  private Value Binary(BinaryExpression binary) {
    // (a / b) | 0 and (a / b) >>> 0 are the two ways JavaScript writes a truncating division.
    if (binary is { Operator: Operator.BitwiseOr or Operator.UnsignedRightShift, Left: BinaryExpression { Operator: Operator.Division } division, Right: NumericLiteral { Value: 0 } })
      return new Value(Convert(this.Division(division), binary.Operator == Operator.BitwiseOr ? IrType.I32 : IrType.U32));

    // Refused before its operands are looked at: a * b is the construct to name, whatever a and b are.
    if (binary.Operator == Operator.Multiplication)
      throw this.NotAnInteger(binary);

    return this.Combine(binary.Operator, this.Expression(binary.Left), binary.Left, this.Expression(binary.Right), binary.Right, binary);
  }

  /// <summary>
  /// A quotient of two integers of one signedness, truncated towards zero. A double holds the
  /// quotient of two 32-bit integers closely enough that truncating it is truncating the exact one.
  /// </summary>
  private IrExpr Division(BinaryExpression division) {
    var (left, right) = this.Unified(Operator.Division, division, this.Expression(division.Left), division.Left, this.Expression(division.Right), division.Right);
    this.Divides = true;
    return new IrBinary(IrBinaryOp.Divide, left, right);
  }

  /// <summary>Applies a binary operator to two lowered operands; a compound assignment comes here as well.</summary>
  private Value Combine(Operator op, Value left, Node leftNode, Value right, Node rightNode, Node whole) {
    var rightIsZero = right.Expr is IrConst { Value: 0 } && right.Type.IsInteger;
    switch (op) {
      // x | 0 is ToInt32(x) and x >>> 0 is ToUint32(x): the coercions themselves.
      case Operator.BitwiseOr when rightIsZero:
        return new Value(this.Int32(left, leftNode), false, left.IsByte);
      case Operator.UnsignedRightShift when rightIsZero:
        return new Value(this.Uint32(left, leftNode), false, left.IsByte);
      case Operator.BitwiseOr:
        return this.Bitwise(IrBinaryOp.Or, left, leftNode, right, rightNode, left.IsByte && right.IsByte);
      case Operator.BitwiseXor:
        return this.Bitwise(IrBinaryOp.Xor, left, leftNode, right, rightNode, left.IsByte && right.IsByte);
      case Operator.BitwiseAnd:
        return this.Bitwise(IrBinaryOp.And, left, leftNode, right, rightNode, left.IsByte || right.IsByte || IsByteMask(left) || IsByteMask(right));
      case Operator.LeftShift:
        return this.Bitwise(IrBinaryOp.ShiftLeft, left, leftNode, right, rightNode, false);
      case Operator.RightShift:
        return this.Bitwise(IrBinaryOp.ShiftRight, left, leftNode, right, rightNode, false);
      case Operator.UnsignedRightShift:
        return new Value(new IrBinary(IrBinaryOp.ShiftRightLogical, this.Uint32(left, leftNode), this.Int32(right, rightNode)));
      case Operator.Addition:
        return new Value(new IrBinary(IrBinaryOp.Add, this.Int32(left, leftNode), this.Int32(right, rightNode)), true);
      case Operator.Subtraction:
        return new Value(new IrBinary(IrBinaryOp.Subtract, this.Int32(left, leftNode), this.Int32(right, rightNode)), true);
      // a * b loses precision above 2^53 where the IR would not, and a / b is a fraction: neither is
      // an integer operation of JavaScript. Math.imul and (a / b) | 0 are.
      case Operator.Multiplication or Operator.Division:
        throw this.NotAnInteger(whole);
      case Operator.Remainder:
        var (dividend, divisor) = this.Unified(op, whole, left, leftNode, right, rightNode);
        this.Divides = true;
        return new Value(new IrBinary(IrBinaryOp.Remainder, dividend, divisor));
      case Operator.StrictEquality or Operator.StrictInequality:
        var (first, second) = this.Unified(op, whole, left, leftNode, right, rightNode, true);
        return new Value(new IrBinary(op == Operator.StrictEquality ? IrBinaryOp.Equal : IrBinaryOp.NotEqual, first, second));
      case Operator.LessThan or Operator.LessThanOrEqual or Operator.GreaterThan or Operator.GreaterThanOrEqual:
        var (smaller, larger) = this.Unified(op, whole, left, leftNode, right, rightNode);
        var comparison = op switch {
          Operator.LessThan => IrBinaryOp.Less,
          Operator.LessThanOrEqual => IrBinaryOp.LessOrEqual,
          Operator.GreaterThan => IrBinaryOp.Greater,
          _ => IrBinaryOp.GreaterOrEqual
        };
        return new Value(new IrBinary(comparison, smaller, larger));
      default:
        throw this.Unsupported(whole, OperatorText(op));
    }
  }

  /// <summary>A bitwise operator or a shift: <c>ToInt32</c> of both operands, an <c>int32</c> result.</summary>
  private Value Bitwise(IrBinaryOp op, Value left, Node leftNode, Value right, Node rightNode, bool isByte)
    => new(new IrBinary(op, this.Int32(left, leftNode), this.Int32(right, rightNode)), false, isByte);

  private static bool IsByteMask(Value value) => value.Expr is IrConst { Value: >= 0 and <= byte.MaxValue };

  /// <summary>
  /// The two operands of a comparison, a division or a remainder as values of one 32-bit type. An
  /// operand that fits either type — a <c>uint8</c>, or a constant in the range of the other operand —
  /// takes the type of the other; an <c>int32</c> against a <c>uint32</c> is refused, because the IR
  /// has no type that holds both.
  /// </summary>
  private (IrExpr Left, IrExpr Right) Unified(Operator op, Node whole, Value left, Node leftNode, Value right, Node rightNode, bool allowBoolean = false) {
    left = this.Exact(left, leftNode);
    right = this.Exact(right, rightNode);
    var (first, second) = (left.Expr, right.Expr);
    if (first.Type != second.Type) {
      if (Lossless(second, first.Type) is { } retypedRight)
        second = retypedRight;
      else if (Lossless(first, second.Type) is { } retypedLeft)
        first = retypedLeft;
    }

    if (first.Type == IrType.U8 && second.Type == IrType.U8)
      (first, second) = (Convert(first, IrType.I32), Convert(second, IrType.I32));

    if (first.Type != second.Type || !(first.Type.IsWord || (allowBoolean && first.Type == IrType.Bool)))
      throw this.Unsupported(whole, $"{OperatorText(op)} on {Spell(left.Type)} and {Spell(right.Type)}");

    return (first, second);
  }

  /// <summary>A value in another integer type when no value can change by it; otherwise <see langword="null"/>.</summary>
  private static IrExpr? Lossless(IrExpr value, IrType type) {
    if (value.Type == IrType.U8 && type.IsWord)
      return Convert(value, type);

    return value is IrConst constant && value.Type.IsInteger && type.IsInteger && IrOperators.Wrap(type, constant.Value) == constant.Value
      ? new IrConst(type, constant.Value)
      : null;
  }

  private Value Conditional(ConditionalExpression choice) {
    var condition = this.Condition(choice.Test);
    var whenTrue = this.Exact(this.Expression(choice.Consequent), choice.Consequent);
    var whenFalse = this.Exact(this.Expression(choice.Alternate), choice.Alternate);
    var (first, second) = (whenTrue.Expr, whenFalse.Expr);
    if (first.Type != second.Type) {
      if (Lossless(second, first.Type) is { } retypedSecond)
        second = retypedSecond;
      else if (Lossless(first, second.Type) is { } retypedFirst)
        first = retypedFirst;
      else
        throw this.Unsupported(choice, $"?: on {Spell(first.Type)} and {Spell(second.Type)}");
    }

    if (!first.Type.IsStorable)
      throw this.Unsupported(choice, $"?: on {Spell(first.Type)}");

    return new Value(new IrConditional(condition, first, second), false, (whenTrue.IsByte || IsByteMask(whenTrue)) && (whenFalse.IsByte || IsByteMask(whenFalse)));
  }

  private Value Member(MemberExpression member) {
    if (member.Computed) {
      var array = this.Array(member.Object);
      var index = this.Index(member.Property);
      this.AccessesElements = true;
      return new Value(new IrArrayLoad(array, index), false, array.Type.Element == IrType.U8);
    }

    if (this.Text(member.Property) == "length")
      return new Value(new IrArrayLength(this.Array(member.Object)));

    throw this.Unsupported(member, this.Text(member));
  }

  private IrExpr Array(Node node) {
    var array = this.Expression(node);
    return array.Type.IsArray ? array.Expr : throw this.Unsupported(node, $"{this.Text(node)} as an array, which is {Spell(array.Type)}");
  }

  /// <summary>An index: any integer, also an uncoerced sum, which the access takes modulo 2^32.</summary>
  private IrExpr Index(Node node) {
    var index = this.Integer(this.Expression(node), node);
    return index.Type.IsWord ? index : Convert(index, IrType.I32);
  }

  private Value Call(CallExpression call) {
    if (call.Arguments.Any(argument => argument is SpreadElement))
      throw this.Unsupported(call, "a spread argument");

    if (call.Callee is MemberExpression { Computed: false, Object: Identifier { Name: "Math" }, Property: Identifier { Name: "imul" } } && !this._scope.ContainsKey("Math")) {
      if (call.Arguments.Count != 2)
        throw this.Unsupported(call, $"Math.imul with {call.Arguments.Count} arguments");

      var left = this.Exact(this.Expression(call.Arguments[0]), call.Arguments[0]);
      var right = this.Exact(this.Expression(call.Arguments[1]), call.Arguments[1]);
      return new Value(new IrBinary(IrBinaryOp.Multiply, this.Int32(left, call.Arguments[0]), this.Int32(right, call.Arguments[1])));
    }

    if (this.Callee(call.Callee) is not { } callee)
      throw this.Unsupported(call, $"a call of {this.Text(call.Callee)}");

    Signature signature;
    try {
      signature = this.SignatureOf(callee);
    } catch (Refusal) {
      throw this.Unsupported(call, $"a call of {callee.Name}, which cannot be imported");
    }

    if (call.Arguments.Count != signature.Parameters.Count)
      throw this.Unsupported(call, $"a call of {callee.Name} with {call.Arguments.Count} arguments for {signature.Parameters.Count} parameters");

    var arguments = new List<IrExpr>();
    for (var index = 0; index < call.Arguments.Count; ++index)
      arguments.Add(this.To(this.Expression(call.Arguments[index]), signature.Parameters[index].Type, call.Arguments[index]));

    this._called.Add(callee);
    return new Value(new IrCall(callee.Name, signature.ReturnType, arguments), false, signature.ReturnType == IrType.U8);
  }

  /// <summary>
  /// The function a call names: a declaration by its name, a property through the variable its object
  /// literal is assigned to. A local of the same name hides either.
  /// </summary>
  private Candidate? Callee(Node callee) => callee switch {
    Identifier { Name: var name } when !this._scope.ContainsKey(name)
      => candidates.Find(candidate => !candidate.IsProperty && candidate.Name == name),
    MemberExpression { Computed: false, Object: Identifier { Name: var owner }, Property: Identifier { Name: var name } } when !this._scope.ContainsKey(owner)
      => candidates.Find(candidate => candidate.IsProperty && candidate.Owner == owner && candidate.Name == name),
    _ => null
  };

  private Value New(NewExpression creation) {
    IrType? element = creation.Callee is Identifier { Name: var name } && !this._scope.ContainsKey(name)
      ? name switch {
        "Uint8Array" => IrType.U8,
        "Int32Array" => IrType.I32,
        "Uint32Array" => IrType.U32,
        _ => null
      }
      : null;
    if (element is null || creation.Arguments.Count != 1 || creation.Arguments[0] is SpreadElement)
      throw this.Unsupported(creation, element is null ? $"new {this.Text(creation.Callee)}" : $"new {this.Text(creation.Callee)} with anything but one length");

    var length = this.Exact(this.Expression(creation.Arguments[0]), creation.Arguments[0]);
    if (!length.Type.IsInteger)
      throw this.Unsupported(creation, $"new {this.Text(creation.Callee)} of {Spell(length.Type)}");

    return new Value(new IrNewArray(IrType.Array(element), length.Type.IsWord ? length.Expr : Convert(length.Expr, IrType.I32)));
  }

  /// <summary>A condition: a <c>boolean</c>. A number is not one here, although JavaScript would take it.</summary>
  private IrExpr Condition(Node node) {
    var value = this.Exact(this.Expression(node), node);
    return value.Type == IrType.Bool ? value.Expr : throw this.Unsupported(node, $"{Spell(value.Type)} as a condition");
  }

  /// <summary>Refuses a number that is not coerced.</summary>
  private Value Exact(Value value, Node node) => value.IsNumber ? throw this.NotAnInteger(node) : value;

  /// <summary>An integer operand, coerced or not.</summary>
  private IrExpr Integer(Value value, Node node)
    => value.Type.IsInteger ? value.Expr : throw this.Unsupported(node, $"{Spell(value.Type)} as a number");

  /// <summary><c>ToInt32</c> of an integer operand. An uncoerced sum already is its value modulo 2^32.</summary>
  private IrExpr Int32(Value value, Node node) => Convert(this.Integer(value, node), IrType.I32);

  /// <summary><c>ToUint32</c> of an integer operand.</summary>
  private IrExpr Uint32(Value value, Node node) => Convert(this.Integer(value, node), IrType.U32);

  /// <summary>
  /// A value where a type is fixed: a result, an argument, an assignment to a local. Only what
  /// cannot change the value is done silently; an integer of another type must be coerced in the source.
  /// </summary>
  private IrExpr To(Value value, IrType type, Node node) {
    value = this.Exact(value, node);
    if (value.Type == type)
      return value.Expr;

    if (Lossless(value.Expr, type) is { } lossless)
      return lossless;

    if (type == IrType.U8 && value.IsByte)
      return Convert(value.Expr, type);

    throw value.Type.IsInteger && type.IsInteger
      ? this.NotAnInteger(node)
      : this.Unsupported(node, $"{Spell(value.Type)} where {Spell(type)} is expected");
  }

  /// <summary>Converts between integer types: the value modulo 2^width, which is what ToInt32, ToUint32 and ToUint8 are.</summary>
  private static IrExpr Convert(IrExpr value, IrType type) {
    if (value.Type == type)
      return value;

    return value is IrConst constant
      ? new IrConst(type, IrOperators.Wrap(type, constant.Value))
      : new IrConvert(type, value);
  }

}
