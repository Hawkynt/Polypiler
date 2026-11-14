using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

internal sealed partial class Lowering {

  /// <summary>A lowered expression and what the inference proved about its values.</summary>
  /// <param name="Expr">The IR that computes the value.</param>
  /// <param name="Type">The type of the value, which can be narrower than the type the IR computes it in.</param>
  private readonly record struct Typed(IrExpr Expr, JsType Type);

  private Typed Evaluate(Node node, Coercion coercion = Coercion.None) => new(this.Expression(node, coercion), types.Of(node, coercion));

  /// <summary>Lowers an expression where the given coercion is applied to its value.</summary>
  private IrExpr Expression(Node node, Coercion coercion = Coercion.None) {
    if (this._substitutes.TryGetValue(node, out var substitute))
      return substitute;

    if (this.StringExpression(node) is { } text)
      return text;

    switch (node) {
      case NumericLiteral literal:
        return Number(literal.Value);
      case BigIntLiteral literal:
        return this.BigNumber(literal, literal.Value);
      case BooleanLiteral literal:
        return new IrConst(IrType.Bool, literal.Value ? 1 : 0);
      case Literal literal:
        throw this.Unsupported(literal, literal.Raw);
      case Identifier identifier:
        return this.Name(identifier);
      case UpdateExpression step:
        return this.StepValue(step);
      case UnaryExpression unary:
        return this.Unary(unary, coercion);
      case LogicalExpression logical:
        return this.Logical(logical);
      case BinaryExpression binary:
        return this.Binary(binary.Operator, binary.Left, binary.Right, coercion, binary).Expr;
      case ConditionalExpression choice:
        return this.Conditional(choice);
      case MemberExpression member:
        return this.Member(member);
      case CallExpression call:
        return this.Call(call);
      case NewExpression creation:
        return this.New(creation, types.Of(creation).Ir!);
      case ArrayExpression literal:
        return this.ArrayLiteral(literal, types.Of(literal).Ir!.Element!);
      case ObjectExpression literal when types.Of(literal).Kind == JsKind.Record:
        return this.ObjectLiteral(literal, types.Of(literal).Ir!);
      case ObjectExpression literal:
        // A literal the inference gave no type has a property the subset does not take, or a key twice.
        return this.ObjectLiteral(literal, IrType.Void);
      default:
        throw this.Unsupported(node, Describe(node) ?? types.Text(node));
    }
  }

  /// <summary>A number as a constant: an <c>int32</c> when it is one, a <c>uint32</c> when it is one, otherwise a double.</summary>
  private static IrConst Number(double value) {
    var type = JsType.Literal(value).Ir!;
    return new IrConst(type, type == IrType.F64 ? IrOperators.Bits(type, value) : (long)value);
  }

  private IrConst BigNumber(Node node, System.Numerics.BigInteger value)
    => JsType.BigLiteral(value).Ir is { } type
      ? new IrConst(type, value > long.MaxValue ? (long)(ulong)value : (long)value)
      : throw this.Unbounded(node);

  private IrExpr Name(Identifier identifier) {
    switch (this._model.Bindings.GetValueOrDefault(identifier)) {
      case Variable variable when this._views.ContainsKey(variable):
        throw this.Unsupported(identifier, $"the DataView '{identifier.Name}' used other than by its methods get… and set…");
      case Variable variable:
        if (variable.IsUninitialized && !this._assigned.Contains(variable))
          throw this.Refuse(identifier, JavaScriptFrontend.ReadBeforeAssignmentDescriptor, $"'{identifier.Name}' may be read before it is assigned");

        return new IrLocalRef(variable.Local!);
      case ModuleConstant constant:
        // A table never comes here: one that is used as a value is no constant.
        this.Constants.Add(constant);
        return new IrConstRef(constant.Constant);
      default:
        throw this.Unsupported(identifier, identifier.Name);
    }
  }

  /// <summary>
  /// <c>++</c> and <c>--</c> inside an expression. A variable is stepped in doubles and yields the old
  /// or the new value. An array element yields the value from before; the new value is what the
  /// array stores, which is not what JavaScript's prefix form yields, so that form is refused.
  /// </summary>
  private IrExpr StepValue(UpdateExpression step) {
    if (step.Argument is not Identifier target)
      return step.Prefix ? throw this.Unsupported(step, "a prefix ++ or -- on an array element inside an expression") : Read(this.StepElement(step));

    var read = this.Expression(target);
    return this._model.Bindings[target] is Variable { IsConstant: false }
      ? new IrStep(read, step.Operator == Operator.Increment, !step.Prefix, false)
      : throw this.Unsupported(step, $"an assignment to the constant {target.Name}");
  }

  private IrExpr Unary(UnaryExpression unary, Coercion coercion) {
    var type = types.Of(unary, coercion);
    switch (unary.Operator) {
      case Operator.LogicalNot:
        return new IrUnary(IrUnaryOp.LogicalNot, this.Condition(unary.Argument));
      case Operator.BitwiseNot when type.Kind == JsKind.BigInt:
        return new IrUnary(IrUnaryOp.BitNot, this.Big(unary.Argument, coercion, type.Ir ?? throw this.Unbounded(unary)));
      case Operator.BitwiseNot:
        return new IrUnary(IrUnaryOp.BitNot, this.Int32(unary.Argument));
      case Operator.UnaryNegation when unary.Argument is NumericLiteral literal:
        return Number(-literal.Value);
      case Operator.UnaryNegation when unary.Argument is BigIntLiteral literal:
        return this.BigNumber(unary, -literal.Value);
      case Operator.UnaryNegation when type.Kind == JsKind.BigInt:
        var wide = type.Ir ?? throw this.Unbounded(unary);
        return new IrBinary(IrBinaryOp.Subtract, new IrConst(wide, 0), this.Big(unary.Argument, coercion, wide));
      case Operator.UnaryNegation when type.IsInteger:
        // Under a coercion the negative of a 32-bit integer is 0 - x modulo 2^32.
        return new IrBinary(IrBinaryOp.Subtract, new IrConst(type.Ir!, 0), this.Word(unary.Argument, coercion, type.Ir!));
      case Operator.UnaryNegation:
        return new IrUnary(IrUnaryOp.Negate, this.Float(unary.Argument));
      case Operator.UnaryPlus:
        var operand = this.Evaluate(unary.Argument);
        return operand.Type.Kind == JsKind.Number ? operand.Expr : throw this.Unsupported(unary, $"unary + on {operand.Type.Describe()}");
      default:
        throw this.Unsupported(unary, OperatorText(unary.Operator));
    }
  }

  /// <summary>
  /// <c>&amp;&amp;</c> and <c>||</c> as a value. Of two truth values it is the conjunction or the
  /// disjunction. Otherwise JavaScript yields one of the operands, chosen by whether the left one
  /// counts as true; the left operand is then computed twice, which is the same only without an effect.
  /// </summary>
  private IrExpr Logical(LogicalExpression logical) {
    if (logical.Operator == Operator.NullishCoalescing)
      throw this.Unsupported(logical, OperatorText(logical.Operator));

    var or = logical.Operator == Operator.LogicalOr;
    var (left, right) = (types.Of(logical.Left), types.Of(logical.Right));
    if (left.Kind == JsKind.Boolean && right.Kind == JsKind.Boolean)
      return new IrBinary(or ? IrBinaryOp.LogicalOr : IrBinaryOp.LogicalAnd, this.Expression(logical.Left), this.Expression(logical.Right));

    if (types.Of(logical).Ir is not { IsStorable: true } type) {
      this.Expression(logical.Left);
      this.Expression(logical.Right);
      throw this.Unsupported(logical, $"{OperatorText(logical.Operator)} on {left.Describe()} and {right.Describe()}");
    }

    if (Contains(logical.Left, node => node is CallExpression or NewExpression or UpdateExpression or AssignmentExpression))
      throw this.Unsupported(logical, $"{OperatorText(logical.Operator)} as a value whose left operand has an effect");

    var condition = this.Condition(logical.Left);
    var first = this.Value(logical.Left, type);
    var second = this.Value(logical.Right, type);
    return or ? new IrConditional(condition, first, second) : new IrConditional(condition, second, first);
  }

  /// <summary>Applies a binary operator the way <see cref="TypeInference.Decide"/> says it is computed; a compound assignment comes here as well.</summary>
  private Typed Binary(Operator op, Node left, Node right, Coercion coercion, Node whole) {
    var operation = types.Decide(op, left, right, coercion);
    var operand = operation.Operand!;
    IrExpr expression;
    switch (operation.Plan) {
      case Plan.Bitwise:
        // x | 0 is ToInt32(x): the coercion itself.
        expression = op == Operator.BitwiseOr && right is NumericLiteral { Value: 0 }
          ? this.Int32(left)
          : new IrBinary(Operation(op), this.Int32(left), this.Int32(right));
        break;
      case Plan.ShiftLogical:
        // x >>> 0 is ToUint32(x).
        expression = right is NumericLiteral { Value: 0 }
          ? this.Uint32(left)
          : new IrBinary(IrBinaryOp.ShiftRightLogical, this.Uint32(left), this.Int32(right));
        break;
      case Plan.WrapAdd:
        expression = new IrBinary(Operation(op), this.Word(left, coercion, operand), this.Word(right, coercion, operand));
        break;
      case Plan.IntegerDivide or Plan.BigDivide or Plan.BigShiftRight:
        this.Divides |= operation.Plan != Plan.BigShiftRight;
        var dividend = this.To(this.Evaluate(left), operand, left);
        var divisor = operation.Plan == Plan.BigShiftRight ? Count(right) : this.To(this.Evaluate(right), operand, right);
        expression = Convert(new IrBinary(Operation(op), dividend, divisor), operation.Result.Ir!);
        break;
      case Plan.Float or Plan.CompareFloat:
        expression = new IrBinary(Operation(op), this.Float(left), this.Float(right));
        break;
      case Plan.Concat:
        expression = this.Concatenation(left, right);
        break;
      case Plan.CompareString:
        expression = this.StringComparison(op, left, right);
        break;
      case Plan.Power:
        expression = new IrIntrinsic(IrIntrinsicOp.PowEcma, IrType.F64, [this.Float(left), this.Float(right)]);
        break;
      case Plan.Compare:
        expression = new IrBinary(Operation(op), this.To(this.Evaluate(left), operand, left), this.To(this.Evaluate(right), operand, right));
        break;
      case Plan.Big:
        expression = new IrBinary(Operation(op), this.Big(left, coercion, operand), op == Operator.LeftShift ? Count(right) : this.Big(right, coercion, operand));
        break;
      default:
        // An operand that is refused for what it is comes first: it is the reason the operator has no type.
        this.Expression(left);
        this.Expression(right);
        if (op is Operator.Equality or Operator.Inequality or Operator.StrictEquality or Operator.StrictInequality
            && (types.Of(left).Kind == JsKind.Record || types.Of(right).Kind == JsKind.Record))
          throw this.Unsupported(whole, $"an object literal compared by identity ({OperatorText(op)})");

        throw operation.Plan == Plan.Unbounded
          ? this.Unbounded(whole)
          : this.Unsupported(whole, $"{OperatorText(op)} on {types.Of(left).Describe()} and {types.Of(right).Describe()}");
    }

    return new Typed(expression, operation.Result);
  }

  private static IrBinaryOp Operation(Operator op) => op switch {
    Operator.BitwiseOr => IrBinaryOp.Or,
    Operator.BitwiseXor => IrBinaryOp.Xor,
    Operator.BitwiseAnd => IrBinaryOp.And,
    Operator.LeftShift => IrBinaryOp.ShiftLeft,
    Operator.RightShift => IrBinaryOp.ShiftRight,
    Operator.Addition => IrBinaryOp.Add,
    Operator.Subtraction => IrBinaryOp.Subtract,
    Operator.Multiplication => IrBinaryOp.Multiply,
    Operator.Division => IrBinaryOp.Divide,
    Operator.Remainder => IrBinaryOp.Remainder,
    Operator.Equality or Operator.StrictEquality => IrBinaryOp.Equal,
    Operator.Inequality or Operator.StrictInequality => IrBinaryOp.NotEqual,
    Operator.LessThan => IrBinaryOp.Less,
    Operator.LessThanOrEqual => IrBinaryOp.LessOrEqual,
    Operator.GreaterThan => IrBinaryOp.Greater,
    _ => IrBinaryOp.GreaterOrEqual
  };

  /// <summary>The literal count of a <c>BigInt</c> shift, which the IR takes as an <c>i32</c>.</summary>
  private static IrConst Count(Node count) => new(IrType.I32, (long)((BigIntLiteral)count).Value);

  private IrExpr Conditional(ConditionalExpression choice) {
    var condition = this.Condition(choice.Test);
    if (types.Of(choice).Ir is not { IsStorable: true } type) {
      this.Expression(choice.Consequent);
      this.Expression(choice.Alternate);
      throw this.Unsupported(choice, $"?: on {types.Of(choice.Consequent).Describe()} and {types.Of(choice.Alternate).Describe()}");
    }

    return new IrConditional(condition, this.Value(choice.Consequent, type), this.Value(choice.Alternate, type));
  }

  private IrExpr Member(MemberExpression member) {
    if (!member.Computed && types.Of(member.Object).Shape is { } shape)
      return this.FieldOf(member, shape);

    if (member.Computed) {
      var array = this.Array(member.Object);
      var index = this.Index(member.Property);
      this.AccessesElements = true;
      return Read(new IrArrayLoad(array, index));
    }

    if (types.Names.Library(member) is { } library)
      return JavaScriptFloatApi.FindConstant(library) is { } constant ? Number(constant.Value) : throw this.Unsupported(member, library);

    return member.Property is Identifier { Name: "length" }
      ? new IrArrayLength(this.Array(member.Object))
      : throw this.Unsupported(member, types.Text(member));
  }

  /// <summary>An element as JavaScript reads it: a binary32 number is the double it is.</summary>
  private static IrExpr Read(IrExpr element) => element.Type == IrType.F32 ? new IrConvert(IrType.F64, element) : element;

  /// <summary>
  /// An array to take an element or the length of. A constant table is read here and nowhere else:
  /// a table the file stores into or uses as a value is no constant, so its name is unknown there.
  /// </summary>
  private IrExpr Array(Node node) {
    if (node is Identifier identifier && this._model.Bindings.GetValueOrDefault(identifier) is ModuleConstant { Type.Kind: JsKind.Array } table) {
      this.Constants.Add(table);
      return new IrConstRef(table.Constant);
    }

    var array = this.Evaluate(node);
    return array.Type.Kind == JsKind.Array ? array.Expr : throw this.Unsupported(node, $"{types.Text(node)} as an array, which is {array.Type.Describe()}");
  }

  /// <summary>
  /// An index. JavaScript reads <c>undefined</c> at anything but the integer of an element; the
  /// imported code takes a double without its fraction and traps outside the array.
  /// </summary>
  private IrExpr Index(Node node) {
    var index = this.Evaluate(node);
    if (index.Type.Kind != JsKind.Number)
      throw this.Unsupported(node, $"{index.Type.Describe()} as an index");

    return index.Type.IsWord
      ? Convert(index.Expr, index.Type.Within(IrType.I32) ? IrType.I32 : IrType.U32)
      : new IrConvert(IrType.I32, this.Float(index, node), IrConversion.Trap);
  }

  /// <summary>
  /// The length of a new array: JavaScript drops the fraction and takes a NaN as 0, which is the
  /// saturating conversion; a negative length is refused by both.
  /// </summary>
  private IrExpr Length(Typed length, Node node) {
    if (length.Type.Kind != JsKind.Number)
      throw this.Unsupported(node, $"an array of the length {length.Type.Describe()}");

    return length.Type.IsWord
      ? Convert(length.Expr, length.Type.Within(IrType.I32) ? IrType.I32 : IrType.U32)
      : new IrConvert(IrType.I32, this.Float(length, node), IrConversion.Saturate);
  }

  private IrExpr Call(CallExpression call) {
    if (call.Arguments.Any(argument => argument is SpreadElement))
      throw this.Unsupported(call, "a spread argument");

    var arguments = call.Arguments;
    var builtin = types.Names.Builtin(call);
    void Takes(int count) {
      if (arguments.Count != count)
        throw this.Unsupported(call, $"{builtin} with {arguments.Count} arguments");
    }

    IrExpr Constant(double value) => new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, value));
    IrExpr Library(IrIntrinsicOp op, params IrExpr[] operands) => new IrIntrinsic(op, IrType.F64, operands);
    if (this.IntegerCall(call, builtin) is { } integer)
      return integer;

    switch (builtin) {
      case null:
        return this.CallFunction(call);
      case "Math.imul":
        Takes(2);
        return new IrBinary(IrBinaryOp.Multiply, this.Int32(arguments[0]), this.Int32(arguments[1]));
      case "Math.clz32":
        Takes(1);
        return new IrIntrinsic(IrIntrinsicOp.LeadingZeroCount, IrType.I32, [this.Uint32(arguments[0])]);
      case "Math.fround":
        Takes(1);
        return new IrConvert(IrType.F32, this.Float(arguments[0]));
      case "Math.trunc" or "Math.floor" or "Math.ceil" or "Math.round" or "Math.abs" or "Math.sqrt":
        Takes(1);
        return Library(builtin switch {
          "Math.trunc" => IrIntrinsicOp.Truncate,
          "Math.floor" => IrIntrinsicOp.Floor,
          "Math.ceil" => IrIntrinsicOp.Ceiling,
          "Math.round" => IrIntrinsicOp.RoundHalfUp,
          "Math.abs" => IrIntrinsicOp.Abs,
          _ => IrIntrinsicOp.Sqrt
        }, this.Float(arguments[0]));
      case "Math.sign":
        // Twice times 2^1023 takes every number but a zero and a NaN beyond 1, and leaves those two as
        // they are: clamped to -1…1 that is the sign, with the operand evaluated once.
        Takes(1);
        var huge = Constant(System.Math.ScaleB(1.0, 1023));
        var scaled = new IrBinary(IrBinaryOp.Multiply, new IrBinary(IrBinaryOp.Multiply, this.Float(arguments[0]), huge), huge);
        return Library(IrIntrinsicOp.Max, Library(IrIntrinsicOp.Min, scaled, Constant(1)), Constant(-1));
      case "Math.min" or "Math.max":
        var min = builtin == "Math.min";
        return arguments.Count == 0
          ? Constant(min ? double.PositiveInfinity : double.NegativeInfinity)
          : arguments.Select(argument => this.Float(argument)).Aggregate((first, second) => Library(min ? IrIntrinsicOp.Min : IrIntrinsicOp.Max, first, second));
      case "BigInt":
        Takes(1);
        var number = this.Evaluate(arguments[0]);
        return number.Type.IsWord ? Convert(number.Expr, IrType.I64) : throw this.Unsupported(call, $"BigInt of {number.Type.Describe()} that is no proven integer");
      default:
        return JavaScriptFloatApi.Find(builtin) is { } member ? this.Library(call, member) : throw this.Unsupported(call, builtin);
    }
  }

  /// <summary>
  /// A call of a function of the floating-point table: every argument as a double. The global
  /// <c>isNaN</c> and <c>isFinite</c> apply <c>ToNumber</c>, which the subset has only for a number.
  /// </summary>
  private IrExpr Library(CallExpression call, JavaScriptFloatMember member) {
    if (member.Arity is { } arity && call.Arguments.Count != arity)
      throw this.Unsupported(call, $"{member.Name} with {call.Arguments.Count} arguments");

    var arguments = new List<IrExpr>();
    foreach (var argument in call.Arguments) {
      var value = this.Evaluate(argument);
      if (member.Coerces && value.Type.Kind != JsKind.Number)
        throw this.Unsupported(call, $"{member.Name} of {value.Type.Describe()}");

      arguments.Add(this.Float(value, argument));
    }

    return member.Lower(arguments) ?? throw this.Unsupported(call, $"{member.Name} of anything but a variable or a constant");
  }

  private IrExpr CallFunction(CallExpression call) {
    if (types.Names.Callee(call.Callee) is not { } callee)
      throw this.Unsupported(call, $"a call of {types.Text(call.Callee)}");

    var target = types.ModelOf(callee);
    if (target.Problem is not null || target.Parameters.Exists(parameter => parameter.Entry.Kind == JsKind.Conflict || parameter.Entry.Ir is { IsStorable: false }))
      throw this.Unsupported(call, $"a call of {callee.Name}, which cannot be imported");

    if (call.Arguments.Count != target.Parameters.Count)
      throw this.Unsupported(call, $"a call of {callee.Name} with {call.Arguments.Count} arguments for {target.Parameters.Count} parameters");

    var arguments = new List<IrExpr>();
    for (var index = 0; index < call.Arguments.Count; ++index)
      arguments.Add(this.Value(call.Arguments[index], target.Parameters[index].Parameter?.Type ?? this.Storable(target.Parameters[index].Entry, call.Arguments[index])));

    return new IrCall(callee.Name, target.ReturnType, arguments);
  }

  /// <summary>A new typed array of a length or of listed elements, or a new <c>Array</c> of a length.</summary>
  private IrExpr New(NewExpression creation, IrType type) {
    var name = creation.Callee is Identifier constructor && !this._model.Bindings.ContainsKey(constructor) ? constructor.Name : null;
    var arguments = creation.Arguments;
    if (name == "Array") {
      if (arguments.Count > 1 || arguments.FirstOrDefault() is SpreadElement)
        throw this.Unsupported(creation, "new Array with anything but one length");

      if (arguments.Count == 0)
        return new IrNewArray(type, new IrConst(IrType.I32, 0));

      // new Array(1.5) throws where new Uint8Array(1.5) has one element: only a proven integer is a length here.
      var count = this.Evaluate(arguments[0]);
      return count.Type.IsWord ? new IrNewArray(type, this.Length(count, arguments[0])) : throw this.Unsupported(creation, $"new Array of a length that is no proven integer, which is {count.Type.Describe()}");
    }

    if (name is null || !TypeInference.TypedArrays.TryGetValue(name, out var element))
      throw this.Unsupported(creation, $"new {types.Text(creation.Callee)}");

    if (arguments is [ArrayExpression elements]) {
      var coercion = element.IsInteger && !element.IsLong ? Coercion.Int32 : Coercion.None;
      return new IrIntrinsic(IrIntrinsicOp.ArrayOf, type, this.Elements(elements).Select(item => this.Stored(this.Evaluate(item, coercion), element, false, item)).ToList());
    }

    if (arguments.Count != 1 || arguments[0] is SpreadElement)
      throw this.Unsupported(creation, $"new {name} with anything but one length or a list of elements");

    return new IrNewArray(type, this.Length(this.Evaluate(arguments[0]), arguments[0]));
  }

  /// <summary>An array literal as an array of the given element type.</summary>
  private IrExpr ArrayLiteral(ArrayExpression literal, IrType element)
    => new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(element), this.Elements(literal).Select(item => this.Value(item, element)).ToList());

  private List<Node> Elements(ArrayExpression literal)
    => literal.Elements.Select(item => item is null or SpreadElement ? throw this.Unsupported(literal, "an array literal with a hole or a spread element") : (Node)item).ToList();

  /// <summary>
  /// A condition. A truth value is one; a number counts as true unless it is a zero or a NaN, a
  /// <c>BigInt</c> unless it is zero. <c>&amp;&amp;</c> and <c>||</c> combine what their operands count as.
  /// </summary>
  private IrExpr Condition(Node node) {
    if (node is LogicalExpression { Operator: Operator.LogicalAnd or Operator.LogicalOr } logical)
      return new IrBinary(logical.Operator == Operator.LogicalOr ? IrBinaryOp.LogicalOr : IrBinaryOp.LogicalAnd, this.Condition(logical.Left), this.Condition(logical.Right));

    var value = this.Evaluate(node);
    switch (value.Type.Kind) {
      case JsKind.Boolean:
        return value.Expr;
      case JsKind.Number when value.Type.IsInteger:
        var word = Convert(value.Expr, value.Type.Within(IrType.I32) ? IrType.I32 : IrType.U32);
        return new IrBinary(IrBinaryOp.NotEqual, word, new IrConst(word.Type, 0));
      case JsKind.Number:
        // |x| > 0 is false for both zeros and for a NaN, and evaluates x once.
        var magnitude = new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F64, [this.Float(value, node)]);
        return new IrBinary(IrBinaryOp.Greater, magnitude, new IrConst(IrType.F64, 0));
      case JsKind.BigInt when value.Type.Ir is { } wide:
        return new IrBinary(IrBinaryOp.NotEqual, Convert(value.Expr, wide), new IrConst(wide, 0));
      default:
        throw this.Unsupported(node, $"{value.Type.Describe()} as a condition");
    }
  }

  /// <summary>A number as a double: an integer of up to 32 bits and a binary32 number are doubles exactly.</summary>
  private IrExpr Float(Typed value, Node node) {
    if (value.Type.Kind != JsKind.Number)
      throw this.Unsupported(node, $"{value.Type.Describe()} as a number");

    return Convert(value.Expr, IrType.F64);
  }

  private IrExpr Float(Node node) => this.Float(this.Evaluate(node), node);

  /// <summary><c>ToInt32</c> of a number: of a proven integer its value modulo 2^32, of a double the operation ECMA-262 defines.</summary>
  private IrExpr Int32(Typed value, Node node)
    => value.Type.IsInteger ? Convert(value.Expr, IrType.I32) : new IrIntrinsic(IrIntrinsicOp.ToInt32, IrType.I32, [this.Float(value, node)]);

  private IrExpr Int32(Node node) => this.Int32(this.Evaluate(node, Coercion.Int32), node);

  /// <summary><c>ToUint32</c> of a number.</summary>
  private IrExpr Uint32(Node node) {
    var value = this.Evaluate(node, Coercion.Uint32);
    return value.Type.IsInteger ? Convert(value.Expr, IrType.U32) : new IrIntrinsic(IrIntrinsicOp.ToUint32, IrType.U32, [this.Float(value, node)]);
  }

  /// <summary>An operand of a wrapped sum: a 32-bit integer of which only the value modulo 2^32 matters.</summary>
  private IrExpr Word(Node node, Coercion coercion, IrType type) => Convert(this.Expression(node, TypeInference.Modulo(coercion)), type);

  /// <summary>An operand of a 64-bit operation under a wrapper: a <c>BigInt</c> of which only the value modulo 2^64 matters.</summary>
  private IrExpr Big(Node node, Coercion coercion, IrType type) {
    var value = this.Evaluate(node, coercion);
    return value.Type.Kind == JsKind.BigInt ? Convert(value.Expr, type) : throw this.Unsupported(node, $"{value.Type.Describe()} as a bigint");
  }

  /// <summary>
  /// An expression where a type is fixed: a result, an argument, the value of a variable. An array
  /// literal and a new <c>Array</c> take the element type of the place they go to, which is the type
  /// the inference found for everything stored into the array.
  /// </summary>
  private IrExpr Value(Node node, IrType type) {
    if (type.IsStruct && node is ObjectExpression record && types.Of(record).Kind == JsKind.Record)
      return this.ObjectLiteral(record, type);

    if (type.IsArray && !this._substitutes.ContainsKey(node)) {
      if (node is ArrayExpression literal)
        return this.ArrayLiteral(literal, type.Element!);

      if (node is NewExpression { Callee: Identifier { Name: "Array" } constructor } creation && !this._model.Bindings.ContainsKey(constructor))
        return this.New(creation, type);
    }

    return this.To(this.Evaluate(node), type, node);
  }

  /// <summary>
  /// A value in the type of the place it goes to, where that changes nothing: an integer in an
  /// integer type that holds all its values, an integer of up to 32 bits or a binary32 number as the
  /// double it is. The inference made the type of a variable hold what is assigned to it; a type
  /// that JSDoc states does not bend, and a value it does not hold is refused, as is one of another kind.
  /// </summary>
  private IrExpr To(Typed value, IrType type, Node node) {
    var from = value.Expr.Type;
    if (from == type)
      return value.Expr;

    if (type.IsStruct && this.Reshaped(value, type, node) is { } reshaped)
      return reshaped;

    var exact = value.Type.Kind is JsKind.Number or JsKind.BigInt && type.IsNumeric && (type.IsInteger
      ? value.Type.Float == 0 && !value.Type.IsUnbounded && value.Type.Within(type) && from.IsLong == type.IsLong
      : type == IrType.F64 && value.Type.Kind == JsKind.Number);
    return exact ? Convert(value.Expr, type) : throw this.Unsupported(node, $"{JsType.Spell(value.Type.Ir ?? from)} where {JsType.Spell(type)} is expected");
  }

  /// <summary>Converts between numeric types; a constant is converted here.</summary>
  private static IrExpr Convert(IrExpr value, IrType type) {
    if (value.Type == type)
      return value;

    if (value is IrConst { Type.IsInteger: true } constant)
      return new IrConst(type, type.IsInteger ? IrOperators.Wrap(type, constant.Value) : IrOperators.Bits(type, constant.Value));

    return new IrConvert(type, value);
  }

}
