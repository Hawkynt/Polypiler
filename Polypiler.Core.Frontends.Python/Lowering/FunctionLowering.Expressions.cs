using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

internal sealed partial class FunctionLowering {

  private static readonly Dictionary<string, IrBinaryOp> _comparisons = new() {
    ["Eq"] = IrBinaryOp.Equal, ["NotEq"] = IrBinaryOp.NotEqual, ["Lt"] = IrBinaryOp.Less,
    ["LtE"] = IrBinaryOp.LessOrEqual, ["Gt"] = IrBinaryOp.Greater, ["GtE"] = IrBinaryOp.GreaterOrEqual
  };

  private static readonly Dictionary<string, string> _symbols = new() {
    ["Add"] = "+", ["Sub"] = "-", ["Mult"] = "*", ["Div"] = "/", ["FloorDiv"] = "//", ["Mod"] = "%", ["Pow"] = "**",
    ["LShift"] = "<<", ["RShift"] = ">>", ["BitAnd"] = "&", ["BitOr"] = "|", ["BitXor"] = "^", ["Invert"] = "~"
  };

  /// <summary>An expression whose value is used: a call of a function that returns <c>None</c> is refused.</summary>
  private (IrExpr Value, PyType Type) Value(PyNode expression) {
    var lowered = this.Lower(expression);
    return lowered.Type == PyType.None
      ? throw this.Refuse(expression, PythonFrontend.UnsupportedDescriptor, $"'the value None of {this._file.Quote(expression)}' is not supported by the Python frontend")
      : lowered;
  }

  /// <summary>An operation that may raise or call, counted so that a temporary never moves one before another.</summary>
  private T Effect<T>(T expression) where T : IrExpr {
    ++this._effects;
    return expression;
  }

  private (IrExpr Value, PyType Type) Lower(PyNode expression) {
    switch (expression.Kind) {
      case "Constant":
        return this.Constant(expression, expression.Text("kind") == "int" ? Constants.Integer(expression) : null);
      case "Name":
        var variable = this._function.Find(expression.Text("id")!)!;
        return (Ref(this._variables[variable.Name]), variable.Type);
      case "BinOp":
        var left = this.Value(expression.Node("left")!);
        var right = this.Value(expression.Node("right")!);
        return this.Operate(expression, expression.Operator()!, left, right);
      case "UnaryOp":
        return this.Unary(expression);
      case "Compare":
        return (this.Compare(expression), PyType.Bool);
      case "BoolOp":
        return this.Logical(expression);
      case "IfExp":
        return this.Conditional(expression);
      default:
        // A call; SubsetCheck refused every other expression.
        return this.Call(expression);
    }
  }

  /// <summary>A constant; an integer of any magnitude.</summary>
  private (IrExpr, PyType) Constant(PyNode node, BigInteger? integer) {
    switch (node.Text("kind")) {
      case "bool":
        return (Bool(node.Text("value") == "True"), PyType.Bool);
      case "float":
        return (Float(Constants.Float(node.Text("value")!)), PyType.Float);
      default:
        return (Big(integer!.Value), PyType.Int);
    }
  }

  /// <summary>A value converted to a wider type: a <c>bool</c> to 0 or 1, an <c>int</c> to the nearest <c>float</c>.</summary>
  private IrExpr Coerce((IrExpr Value, PyType Type) value, PyType type) {
    if (value.Type == type)
      return value.Value;

    this._notes.Widens = true;
    return (value.Type, type, value.Value) switch {
      (PyType.Bool, PyType.Int, IrConst constant) => Big(constant.Value),
      (PyType.Bool, PyType.Int, _) => Choose(value.Value, Big(1), Big(0)),
      (PyType.Bool, _, IrConst constant) => Float(constant.Value),
      (PyType.Bool, _, _) => Choose(value.Value, Float(1), Float(0)),
      _ => this.FloatOfInt(value.Value)
    };
  }

  /// <summary>A <c>bool</c> as the <c>int</c> it is, without counting it as a widening: Python computes it so.</summary>
  private (IrExpr, PyType) Integer((IrExpr Value, PyType Type) value)
    => value.Type == PyType.Bool ? (value.Value is IrConst constant ? Big(constant.Value) : Choose(value.Value, Big(1), Big(0)), PyType.Int) : value;

  /// <summary>An <c>int</c> or <c>bool</c> as a <c>float</c>, as an arithmetic operator with a <c>float</c> operand converts it.</summary>
  private IrExpr Floating((IrExpr Value, PyType Type) value) => value.Type switch {
    PyType.Float => value.Value,
    PyType.Bool => value.Value is IrConst constant ? Float(constant.Value) : Choose(value.Value, Float(1), Float(0)),
    _ => this.FloatOfInt(value.Value)
  };

  /// <summary>An <c>int</c> as the nearest <c>float</c>, which raises <c>OverflowError</c> beyond the largest one; a constant that converts is converted here.</summary>
  private IrExpr FloatOfInt(IrExpr value)
    => value is IrBigConst constant && IrBigInts.ToDouble(constant.Value) is var number && double.IsFinite(number)
      ? Float(number)
      : this.Effect(this._helpers.FloatOfInt(value));

  /// <summary>The truth of a value, as a condition tests it: an <c>int</c> or <c>float</c> is true when it is not zero; a NaN is true.</summary>
  private IrExpr Truth((IrExpr Value, PyType Type) value) => value.Type switch {
    PyType.Bool => value.Value,
    PyType.Float => Binary(IrBinaryOp.NotEqual, value.Value, Float(0)),
    _ => Binary(IrBinaryOp.NotEqual, value.Value, Big(0))
  };

  private (IrExpr Value, PyType Type) Operate(PyNode node, string op, (IrExpr Value, PyType Type) left, (IrExpr Value, PyType Type) right) {
    var floating = left.Type == PyType.Float || right.Type == PyType.Float;
    if (op is "LShift" or "RShift" or "BitAnd" or "BitOr" or "BitXor" && floating)
      throw this.Refuse(node, PythonFrontend.UnsupportedDescriptor, $"'{_symbols[op]} with a float operand, which raises TypeError,' is not supported by the Python frontend");

    if (op is "BitAnd" or "BitOr" or "BitXor" && left.Type == PyType.Bool && right.Type == PyType.Bool)
      return (op == "BitXor"
        ? Binary(IrBinaryOp.NotEqual, left.Value, right.Value)
        : Binary(IrBinaryOp.NotEqual, Binary(op == "BitAnd" ? IrBinaryOp.And : IrBinaryOp.Or, this.Integer(left).Item1, this.Integer(right).Item1), Big(0)), PyType.Bool);

    if (op == "Div")
      return (floating ? this.Effect(this._helpers.TrueDivideFloat(Floating(left), Floating(right))) : this.Effect(this._helpers.TrueDivideInt(this.Integer(left).Item1, this.Integer(right).Item1)), PyType.Float);

    if (op == "Pow")
      return this.Power(node, left, right, floating);

    if (floating) {
      var (a, b) = (Floating(left), Floating(right));
      return (op switch {
        "Add" => Binary(IrBinaryOp.Add, a, b),
        "Sub" => Binary(IrBinaryOp.Subtract, a, b),
        "Mult" => Binary(IrBinaryOp.Multiply, a, b),
        "FloorDiv" => this.Effect(this._helpers.FloorDivideFloat(a, b)),
        _ => this.Effect(this._helpers.ModuloFloat(a, b))
      }, PyType.Float);
    }

    var (x, y) = (this.Integer(left).Item1, this.Integer(right).Item1);
    // A constant count or a divisor that is a positive power of two needs no check: the operation is one of the IR.
    var count = y is IrBigConst { Value: var shift } && shift.Sign >= 0 && shift <= int.MaxValue ? (int?)(int)shift : null;
    var power = y is IrBigConst { Value: var divisor } && divisor.Sign > 0 && divisor.IsPowerOfTwo ? (int?)(int)(divisor.GetBitLength() - 1) : null;
    return (op switch {
      "Add" => Binary(IrBinaryOp.Add, x, y),
      "Sub" => Binary(IrBinaryOp.Subtract, x, y),
      "Mult" => Binary(IrBinaryOp.Multiply, x, y),
      // The floor of a quotient by 2^k is the arithmetic shift, the floor remainder the low k bits, also of a negative dividend.
      "FloorDiv" when power is { } k => Binary(IrBinaryOp.ShiftRight, x, Int32(k)),
      "FloorDiv" => this.Effect(this._helpers.FloorDivideInt(x, y)),
      "Mod" when power is { } k => Binary(IrBinaryOp.And, x, Big((BigInteger.One << k) - 1)),
      "Mod" => this.Effect(this._helpers.ModuloInt(x, y)),
      "BitAnd" => Binary(IrBinaryOp.And, x, y),
      "BitOr" => Binary(IrBinaryOp.Or, x, y),
      "BitXor" => Binary(IrBinaryOp.Xor, x, y),
      "RShift" when count is { } n => Binary(IrBinaryOp.ShiftRight, x, Int32(n)),
      "RShift" => this.Effect(this._helpers.ShiftRight(x, y)),
      "LShift" when count is { } n => Binary(IrBinaryOp.ShiftLeft, x, Int32(n)),
      _ => this.Effect(this._helpers.ShiftLeft(x, y))
    }, PyType.Int);
  }

  /// <summary>
  /// <c>**</c>: of two integers an <c>int</c> for an exponent that is not negative and a <c>float</c>
  /// for a negative constant one, both converted as CPython converts them; of a <c>float</c> a <c>float</c>.
  /// An integer exponent whose sign is not known gives an <c>int</c>, and a negative one raises.
  /// </summary>
  private (IrExpr, PyType) Power(PyNode node, (IrExpr Value, PyType Type) left, (IrExpr Value, PyType Type) right, bool floating) {
    if (!floating && !(right.Value is IrBigConst { Value.Sign: < 0 } && right.Type == PyType.Int)) {
      // A constant to a constant power is the constant, as far as its size stays reasonable.
      if (left.Value is IrBigConst @base && right.Value is IrBigConst exponent && exponent.Value <= 4096)
        return (Big(BigInteger.Pow(@base.Value, (int)exponent.Value)), PyType.Int);

      if (right.Value is not (IrConst or IrBigConst))
        this._notes.PowersBySignUnknown = true;

      return (this.Effect(this._helpers.PowerInt(this.Integer(left).Item1, this.Integer(right).Item1)), PyType.Int);
    }

    this._notes.PowersFloats |= floating;
    return (this.Effect(this._helpers.PowerFloat(Floating(left), Floating(right))), PyType.Float);
  }

  private (IrExpr, PyType) Unary(PyNode expression) {
    var op = expression.Operator()!;
    var operandNode = expression.Node("operand")!;
    // -<literal> is the literal of the negative value: -9223372036854775808 is in range.
    if (op == "USub" && operandNode.Is("Constant") && Constants.Integer(expression) is { } literal)
      return this.Constant(expression, literal);

    var operand = this.Value(operandNode);
    switch (op) {
      case "Not":
        return (Not(this.Truth(operand)), PyType.Bool);
      case "USub" when operand.Type == PyType.Float:
        return (new IrUnary(IrUnaryOp.Negate, operand.Value), PyType.Float);
      case "UAdd" when operand.Type == PyType.Float:
        return operand;
      case "Invert" when operand.Type == PyType.Float:
        throw this.Refuse(expression, PythonFrontend.UnsupportedDescriptor, "'~ with a float operand, which raises TypeError,' is not supported by the Python frontend");
    }

    var integer = this.Integer(operand).Item1;
    return (op switch {
      "USub" => new IrUnary(IrUnaryOp.Negate, integer),
      "Invert" => new IrUnary(IrUnaryOp.BitNot, integer),
      _ => integer
    }, PyType.Int);
  }

  /// <summary>
  /// A comparison, chained ones as Python evaluates them: <c>a &lt; b &lt; c</c> evaluates <c>b</c>
  /// once and <c>c</c> only when <c>a &lt; b</c> holds. An operand compared twice that is neither a
  /// constant nor a variable is kept in a temporary.
  /// </summary>
  private IrExpr Compare(PyNode expression) {
    var ops = expression.Nodes("ops");
    var operands = expression.Nodes("comparators");
    var left = this.Value(expression.Node("left")!);
    IrExpr? result = null;
    for (var index = 0; index < ops.Count; ++index) {
      if (index > 0)
        ++this._conditional;

      var before = this._effects;
      var right = this.Value(operands[index]);
      if (index < ops.Count - 1)
        right = this.Kept(operands[index], right, before);

      var comparison = this.Comparison(ops[index].Kind, left, right);
      result = result is null ? comparison : Binary(IrBinaryOp.LogicalAnd, result, comparison);
      left = right;
    }

    this._conditional -= ops.Count - 1;
    return result!;
  }

  /// <summary>
  /// A value used twice, as it is: a constant or a variable; otherwise in a temporary the statement
  /// declares first. A temporary would move the evaluation before whatever the statement evaluated
  /// already, so it is refused where that could be told: under a condition, or after another
  /// operation that may raise.
  /// </summary>
  private (IrExpr Value, PyType Type) Kept(PyNode node, (IrExpr Value, PyType Type) value, int before) {
    if (value.Value is IrConst or IrBigConst or IrLocalRef)
      return value;

    var reordered = this._effects > before && before > this._statementEffects;
    if (this._conditional > 0 || reordered)
      throw this.Refuse(node, PythonFrontend.UnsupportedDescriptor, $"'{this._file.Quote(node)} used twice where it cannot be evaluated first' is not supported by the Python frontend");

    var temporary = this.Fresh("value", value.Value.Type);
    this._prefix.Add(new IrDeclare(temporary, value.Value));
    return (Ref(temporary), value.Type);
  }

  private IrExpr Comparison(string op, (IrExpr Value, PyType Type) left, (IrExpr Value, PyType Type) right) {
    var code = PythonHelpers.Comparisons[op];
    if (left.Type == PyType.Bool && right.Type == PyType.Bool && op is "Eq" or "NotEq")
      return Binary(_comparisons[op], left.Value, right.Value);

    var (a, b) = (this.Integer(left), this.Integer(right));
    if (a.Item2 == b.Item2)
      return Binary(_comparisons[op], a.Item1, b.Item1);

    // An int and a float compare exactly; a constant int a float holds exactly compares as that float.
    if (a.Item2 == PyType.Int && a.Item1 is IrBigConst leftConstant && BigInteger.Abs(leftConstant.Value) <= 1L << 53)
      return Binary(_comparisons[op], Float((double)leftConstant.Value), b.Item1);

    if (b.Item2 == PyType.Int && b.Item1 is IrBigConst rightConstant && BigInteger.Abs(rightConstant.Value) <= 1L << 53)
      return Binary(_comparisons[op], a.Item1, Float((double)rightConstant.Value));

    return this.Effect(a.Item2 == PyType.Int ? this._helpers.CompareIntFloat(a.Item1, b.Item1, code) : this._helpers.CompareFloatInt(a.Item1, b.Item1, code));
  }

  /// <summary>
  /// <c>and</c> and <c>or</c> give one of their operands, not a truth value: <c>0 or 9</c> is 9. Of
  /// truth values they are the short-circuit operators; otherwise the first operand is tested and
  /// given, the rest evaluated only when it decides nothing.
  /// </summary>
  private (IrExpr, PyType) Logical(PyNode expression) {
    var values = expression.Nodes("values");
    var and = expression.Operator() == "And";
    var type = Typing.ExpressionTypes.Of(expression, this._function, this._functions);
    if (type == PyType.Unknown)
      throw this.Refuse(expression, PythonFrontend.MixedKindsDescriptor, $"'{this._file.Quote(expression)}' gives values of types no type of the import holds both of");

    return (this.Logical(values, 0, and, type), type);
  }

  private IrExpr Logical(IReadOnlyList<PyNode> values, int index, bool and, PyType type) {
    var before = this._effects;
    var first = this.Value(values[index]);
    if (index == values.Count - 1)
      return this.Coerce(first, type);

    if (type == PyType.Bool) {
      ++this._conditional;
      var rest = this.Logical(values, index + 1, and, type);
      --this._conditional;
      return Binary(and ? IrBinaryOp.LogicalAnd : IrBinaryOp.LogicalOr, first.Value, rest);
    }

    first = this.Kept(values[index], first, before);
    ++this._conditional;
    var others = this.Logical(values, index + 1, and, type);
    --this._conditional;
    var test = this.Truth(first);
    var own = this.Coerce(first, type);
    return and ? Choose(test, others, own) : Choose(test, own, others);
  }

  private (IrExpr, PyType) Conditional(PyNode expression) {
    var type = Typing.ExpressionTypes.Of(expression, this._function, this._functions);
    if (type == PyType.Unknown)
      throw this.Refuse(expression, PythonFrontend.MixedKindsDescriptor, $"'{this._file.Quote(expression)}' gives values of types no type of the import holds both of");

    var test = this.Truth(this.Value(expression.Node("test")!));
    ++this._conditional;
    var whenTrue = this.Coerce(this.Value(expression.Node("body")!), type);
    var whenFalse = this.Coerce(this.Value(expression.Node("orelse")!), type);
    --this._conditional;
    return (Choose(test, whenTrue, whenFalse), type);
  }

  private (IrExpr, PyType) Call(PyNode call) {
    var name = call.Node("func")!.Text("id")!;
    var arguments = call.Nodes("args").Select(this.Value).ToList();
    if (ScopeResolver.Resolve(this._function, this._functions, name) == Binding.Function) {
      var called = this._functions[name];
      var values = called.Parameters.Zip(arguments).Select(pair => {
        if (pair.Second.Type != pair.First.Type)
          this._notes.Widens = true;

        return this.Coerce(pair.Second, pair.First.Type);
      }).ToList();
      return (this.Effect(new IrCall(name, PyTypes.Ir(called.Returns), values)), called.Returns);
    }

    var argument = arguments[0];
    switch (name) {
      case "bool":
        return (this.Truth(argument), PyType.Bool);
      case "float":
        return (Floating(argument), PyType.Float);
      case "pow":
        return this.Operate(call, "Pow", argument, arguments[1]);
    }

    if (argument.Type != PyType.Float) {
      var integer = this.Integer(argument).Item1;
      return (name == "abs" ? this.Effect(this._helpers.AbsInt(integer)) : integer, PyType.Int);
    }

    return name switch {
      "abs" => (Intrinsic(IrIntrinsicOp.Abs, IrType.F64, argument.Value), PyType.Float),
      // round() rounds half to even, int() toward zero; both raise as int() does.
      "round" => (this.Effect(this._helpers.IntOfFloat(Intrinsic(IrIntrinsicOp.RoundEven, IrType.F64, argument.Value))), PyType.Int),
      _ => (this.Effect(this._helpers.IntOfFloat(argument.Value)), PyType.Int)
    };
  }

}
