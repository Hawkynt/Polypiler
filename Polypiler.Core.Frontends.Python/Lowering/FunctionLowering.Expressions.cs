using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;
using Polypiler.Core.Frontends.Python.Typing;
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
    ["LShift"] = "<<", ["RShift"] = ">>", ["BitAnd"] = "&", ["BitOr"] = "|", ["BitXor"] = "^", ["Invert"] = "~",
    ["Eq"] = "==", ["NotEq"] = "!=", ["Lt"] = "<", ["LtE"] = "<=", ["Gt"] = ">", ["GtE"] = ">="
  };

  /// <summary>An expression whose value is used: <c>None</c> where no optional holds it, and a call of a function that returns <c>None</c>, are refused.</summary>
  private (IrExpr Value, PyType Type) Value(PyNode expression, PyType? expected = null) {
    var lowered = this.Lower(expression, expected);
    return lowered.Type.Kind == PyKind.None
      ? throw this.Unsupported(expression, $"the value None of {this.Quote(expression)}")
      : lowered;
  }

  /// <summary>An operation that may raise or call, counted so that a temporary never moves one before another.</summary>
  private T Effect<T>(T expression) where T : IrExpr {
    ++this._effects;
    return expression;
  }

  /// <summary>An expression; <paramref name="expected"/> is the type of the place it goes to, which an empty list and <c>None</c> take.</summary>
  private (IrExpr Value, PyType Type) Lower(PyNode expression, PyType? expected = null) => Exactly(this.Expression(expression, expected));

  /// <summary>An <c>int</c> read from a place that holds it as a fixed width - a field, a tuple, an optional, a table, a length - as the exact <c>int</c> it is.</summary>
  private static (IrExpr Value, PyType Type) Exactly((IrExpr Value, PyType Type) value)
    => value.Type.Kind == PyKind.Int && value.Value.Type.IsInteger ? (Exact(value.Value), value.Type) : value;

  private (IrExpr Value, PyType Type) Expression(PyNode expression, PyType? expected) {
    switch (expression.Kind) {
      case "Constant":
        return this.Constant(expression, expected);
      case "Name":
        return this.Name(expression);
      case "BinOp":
        var sequence = expected is { Kind: PyKind.List } ? expected : null;
        var left = this.Value(expression.Node("left")!, sequence);
        var right = this.Value(expression.Node("right")!, sequence);
        return this.Operate(expression, expression.Operator()!, left, right);
      case "UnaryOp":
        return this.Unary(expression);
      case "Compare":
        return (this.Compare(expression), PyType.Bool);
      case "BoolOp":
        return this.Logical(expression);
      case "IfExp":
        return this.Conditional(expression, expected);
      case "Call":
        return this.Call(expression, expected);
      case "List":
        return this.ListDisplay(expression, expected);
      case "Tuple":
        return this.TupleDisplay(expression, expected);
      case "Subscript":
        return this.Subscript(expression);
      case "Attribute":
        return this.Attribute(expression);
      case "ListComp":
        return this.ListComprehension(expression, expected);
      case "GeneratorExp":
        return this.GeneratorExpression(expression);
      case "Dict":
        return this.DictDisplay(expression, expected);
      case "Set":
        return this.SetDisplay(expression, expected);
      case "DictComp" or "SetComp":
        return this.CollectionComprehension(expression, expected);
      default:
        // NamedExpr; SubsetCheck refused every other expression.
        return this.Walrus(expression);
    }
  }

  /// <summary><c>(x := e)</c>: the value is assigned to the variable and is the value of the expression.</summary>
  private (IrExpr, PyType) Walrus(PyNode expression) {
    var target = expression.Node("target")!;
    var variable = this.Scope.Variable(target) ?? this.Function.Find(target.Text("id")!)!;
    var value = this.Value(expression.Node("value")!, variable.Type);
    return (new IrBind(this.Local(variable), this.Coerce(value, variable.Type, expression)), variable.Type);
  }

  /// <summary>A constant; an integer of any magnitude; <c>None</c> is the none of the optional it goes to.</summary>
  private (IrExpr, PyType) Constant(PyNode node, PyType? expected) {
    switch (node.ConstantKind) {
      case "bool":
        return (Bool(node.Value is true), PyType.Bool);
      case "float":
        return (Float((double)node.Value!), PyType.Float);
      case "str":
        return (new IrStrConst(Constants.Text(node)), PyType.Str);
      case "bytes":
        return (this.BytesConstant((byte[])node.Value!), PyType.Bytes);
      case "none":
        return expected is { Kind: PyKind.Optional } ? (this.None(expected), expected) : (new IrConst(IrType.Void, 0), PyType.None);
      default:
        return IntegerConstant(Constants.Integer(node)!.Value);
    }
  }

  /// <summary>An integer literal, exact at any magnitude.</summary>
  private static (IrExpr, PyType) IntegerConstant(BigInteger integer) => (Big(integer), PyType.Int);

  private (IrExpr, PyType) Name(PyNode name) {
    if (this.Scope.Variable(name) is { } variable)
      return (Ref(this.Local(variable)), variable.Type);

    var id = name.Text("id")!;
    switch (this.Scope.Resolve(name)) {
      case Binding.Constant:
        return this._module.Constants.TryGetValue(id, out var value) ? this.ConstantValue(value)
          : throw this.Refuse(name, PythonFrontend.UnresolvedDescriptor, $"'{id}' is neither part of this import nor in the Python call map: it is used before the module assigns it, which raises NameError");
      case Binding.Global:
        var global = this._module.GlobalVariables[id];
        return (new IrGlobalRef(global.Ir!), global.Type);
      case Binding.Import when LibraryCallMap.Constant(this._module.Imports[id], null) is { } constant:
        return (constant.Value, constant.Type);
      case Binding.Import when this._module.Imports[id] is { User: { } user, Name: { } imported } && user.Constants.TryGetValue(imported, out var held):
        return this.ConstantValue(held);
      default:
        throw this.Refuse(name, PythonFrontend.EscapingFunctionDescriptor, SubsetCheck.Escaping(id) + " (escaping function values: #753)");
    }
  }

  /// <summary>The value of a module-level constant: the constant itself, or the table of the module.</summary>
  private (IrExpr, PyType) ConstantValue(ConstantValue constant)
    => constant.Table is { } table ? (new IrGlobalRef(table), constant.Type) : (constant.Value!, constant.Type);

  /// <summary>
  /// A value converted to the type of the place it goes to: a <c>bool</c> to 0 or 1, an <c>int</c> to
  /// the nearest <c>float</c>, a value into the optional that holds it, <c>None</c> into an optional.
  /// Anything else is refused.
  /// </summary>
  private IrExpr Coerce((IrExpr Value, PyType Type) value, PyType type, PyNode at) {
    value = Exactly(value);
    if (value.Type == type || (value.Type.IsIncomplete && value.Value.Type == PyTypes.Ir(type)))
      return value.Value;

    if (value.Type.Kind == PyKind.None && value.Value is not IrConst)
      throw this.Unsupported(at, $"the value None of {this.Quote(at)}");

    if (type.Kind == PyKind.Optional) {
      if (value.Type.Kind == PyKind.None)
        return this.None(type);

      if (value.Type.Kind == PyKind.Optional)
        throw this.Refuse(at, PythonFrontend.MixedKindsDescriptor, $"'{this.Quote(at)}' gives a value of the type {value.Type} where one of the type {type} is held");

      var inner = this.Coerce(value, type.Element!, at);
      return PyTypes.IsReference(type.Element!) ? inner : new IrSome(Fit(inner, PyTypes.Held(type.Element!)));
    }

    if (value.Type.Kind == PyKind.Enum && value.Type.Class!.Kind == ClassKind.IntEnum && type.IsNumber)
      return this.Coerce((Convert(IrType.BigInt, Convert(IrType.I64, value.Value)), PyType.Int), type, at);

    if (!value.Type.IsNumber || !type.IsNumber || PyTypes.Join(value.Type, type) != type)
      throw this.Refuse(at, PythonFrontend.MixedKindsDescriptor, $"'{this.Quote(at)}' gives a value of the type {value.Type} where one of the type {type} is held");

    this.Notes.Widens = true;
    return (value.Type.Kind, type.Kind, value.Value) switch {
      (PyKind.Bool, PyKind.Int, IrConst constant) => Big(constant.Value),
      (PyKind.Bool, PyKind.Int, _) => Choose(value.Value, Big(1), Big(0)),
      (PyKind.Bool, _, IrConst constant) => Float(constant.Value),
      (PyKind.Bool, _, _) => Choose(value.Value, Float(1), Float(0)),
      _ => this.FloatOfInt(value.Value)
    };
  }

  /// <summary>
  /// A value as an operand of arithmetic: an optional's value, where <c>None</c> raises
  /// <c>TypeError</c> as python's operators do; an <c>IntEnum</c> member's integer.
  /// </summary>
  private (IrExpr Value, PyType Type) Plain((IrExpr Value, PyType Type) value, PyNode at) {
    if (value.Type.Kind == PyKind.Optional)
      return Exactly((this.Effect(this.Helpers.Unwrap(value.Value, PyTypes.Held(value.Type.Element!), this.DeclaredException("TypeError"), "unsupported operand type(s): 'NoneType'")), value.Type.Element!));

    // A member of a plain Enum is no number: the operators refused it before.
    return value.Type.Kind == PyKind.Enum ? (Exact(Convert(IrType.I64, value.Value)), PyType.Int) : value;
  }

  /// <summary>A <c>bool</c> as the <c>int</c> it is, without counting it as a widening: Python computes it so.</summary>
  private (IrExpr, PyType) Integer((IrExpr Value, PyType Type) value)
    => value.Type.Kind == PyKind.Bool ? (value.Value is IrConst constant ? Big(constant.Value) : Choose(value.Value, Big(1), Big(0)), PyType.Int)
      : value.Type.Kind == PyKind.Enum ? (Convert(IrType.BigInt, Convert(IrType.I64, value.Value)), PyType.Int)
      : value;

  /// <summary>An <c>int</c> or <c>bool</c> as a <c>float</c>, as an arithmetic operator with a <c>float</c> operand converts it.</summary>
  private IrExpr Floating((IrExpr Value, PyType Type) value) => value.Type.Kind switch {
    PyKind.Float => value.Value,
    PyKind.Bool => value.Value is IrConst constant ? Float(constant.Value) : Choose(value.Value, Float(1), Float(0)),
    _ => this.FloatOfInt(value.Value)
  };

  /// <summary>An <c>int</c> as the nearest <c>float</c>, which raises <c>OverflowError</c> beyond the largest one; a constant that converts is converted here.</summary>
  private IrExpr FloatOfInt(IrExpr value)
    => value is IrBigConst constant && IrBigInts.ToDouble(constant.Value) is var number && double.IsFinite(number)
      ? Float(number)
      : this.Effect(this.Helpers.FloatOfInt(value));

  /// <summary>
  /// The truth of a value, as a condition tests it: a number is true when it is not zero (a NaN is
  /// true), a sequence when it is not empty, an optional when it holds a true value, an object unless
  /// its <c>__len__</c> is zero.
  /// </summary>
  private IrExpr Truth((IrExpr Value, PyType Type) value) {
    switch (value.Type.Kind) {
      case PyKind.Bool:
        return value.Value;
      case PyKind.Float:
        return Binary(IrBinaryOp.NotEqual, value.Value, Float(0));
      case PyKind.Int:
        return Binary(IrBinaryOp.NotEqual, value.Value, Big(0));
      case PyKind.Str or PyKind.List or PyKind.Bytes or PyKind.ByteArray or PyKind.Table or PyKind.Dict or PyKind.Set:
        return Binary(IrBinaryOp.NotEqual, this.Length32(value), Int32(0));
      case PyKind.Enum when value.Type.Class!.Kind == ClassKind.IntEnum:
        return Binary(IrBinaryOp.NotEqual, Convert(IrType.I64, value.Value), Int(0));
      case PyKind.Optional:
        return this.Effect(this.Helpers.OptionalTruth(value.Value, PyTypes.Held(value.Type.Element!), element => this.Truth(Exactly((element, value.Type.Element!)))));
      case PyKind.Object when value.Type.Class!.Method("__len__") is not null:
        return Binary(IrBinaryOp.NotEqual, this.Length64(value, value.Value is IrLocalRef ? null : value.Value), Big(0));
      case PyKind.Object:
        // An object of a class without __len__ is true; the reference is never null here.
        return Not(new IrIsNull(value.Value));
      case PyKind.Tuple when value.Value is IrLocalRef or IrStructNew { Arguments.Count: 0 }:
        return Bool(value.Type.Items.Count > 0);
      default:
        throw new Refusal(this.File.At(this.Function.Node, PythonFrontend.UnsupportedDescriptor, $"'the truth of a value of the type {value.Type}' is not supported by the Python frontend"));
    }
  }

  private (IrExpr Value, PyType Type) Operate(PyNode node, string op, (IrExpr Value, PyType Type) left, (IrExpr Value, PyType Type) right) {
    if (!(ExpressionTypes.Value(left.Type).IsNumber && ExpressionTypes.Value(right.Type).IsNumber))
      return this.SequenceOperation(node, op, left, right);

    left = this.Plain(left, node);
    right = this.Plain(right, node);
    var floating = left.Type.Kind == PyKind.Float || right.Type.Kind == PyKind.Float;
    if (op is "LShift" or "RShift" or "BitAnd" or "BitOr" or "BitXor" && floating)
      throw this.Unsupported(node, $"{_symbols[op]} with a float operand, which raises TypeError,");

    if (op is "BitAnd" or "BitOr" or "BitXor" && left.Type.Kind == PyKind.Bool && right.Type.Kind == PyKind.Bool)
      return (op == "BitXor"
        ? Binary(IrBinaryOp.NotEqual, left.Value, right.Value)
        : Binary(IrBinaryOp.NotEqual, Binary(op == "BitAnd" ? IrBinaryOp.And : IrBinaryOp.Or, this.Integer(left).Item1, this.Integer(right).Item1), Big(0)), PyType.Bool);

    if (op == "Div")
      return (floating ? this.Effect(this.Helpers.TrueDivideFloat(Floating(left), Floating(right))) : this.Effect(this.Helpers.TrueDivideInt(this.Integer(left).Item1, this.Integer(right).Item1)), PyType.Float);

    if (op == "Pow")
      return this.Power(node, left, right, floating);

    if (floating) {
      var (a, b) = (Floating(left), Floating(right));
      return (op switch {
        "Add" => Binary(IrBinaryOp.Add, a, b),
        "Sub" => Binary(IrBinaryOp.Subtract, a, b),
        "Mult" => Binary(IrBinaryOp.Multiply, a, b),
        "FloorDiv" => this.Effect(this.Helpers.FloorDivideFloat(a, b)),
        _ => this.Effect(this.Helpers.ModuloFloat(a, b))
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
      "FloorDiv" => this.Effect(this.Helpers.FloorDivideInt(x, y)),
      "Mod" when power is { } k => Binary(IrBinaryOp.And, x, Big((BigInteger.One << k) - 1)),
      "Mod" => this.Effect(this.Helpers.ModuloInt(x, y)),
      "BitAnd" => Binary(IrBinaryOp.And, x, y),
      "BitOr" => Binary(IrBinaryOp.Or, x, y),
      "BitXor" => Binary(IrBinaryOp.Xor, x, y),
      "RShift" when count is { } n => Binary(IrBinaryOp.ShiftRight, x, Int32(n)),
      "RShift" => this.Effect(this.Helpers.ShiftRight(x, y)),
      "LShift" when count is { } n => Binary(IrBinaryOp.ShiftLeft, x, Int32(n)),
      _ => this.Effect(this.Helpers.ShiftLeft(x, y))
    }, PyType.Int);
  }

  /// <summary>
  /// <c>**</c>: of two integers an <c>int</c> for an exponent that is not negative and a <c>float</c>
  /// for a negative constant one, both converted as CPython converts them; of a <c>float</c> a <c>float</c>.
  /// An integer exponent whose sign is not known gives an <c>int</c>, and a negative one raises.
  /// </summary>
  private (IrExpr, PyType) Power(PyNode node, (IrExpr Value, PyType Type) left, (IrExpr Value, PyType Type) right, bool floating) {
    if (!floating && !(right.Value is IrBigConst { Value.Sign: < 0 } && right.Type.Kind == PyKind.Int)) {
      // A constant to a constant power is the constant, as far as its size stays reasonable.
      if (left.Value is IrBigConst @base && right.Value is IrBigConst exponent && exponent.Value <= 4096)
        return (Big(BigInteger.Pow(@base.Value, (int)exponent.Value)), PyType.Int);

      if (right.Value is not (IrConst or IrBigConst))
        this.Notes.PowersBySignUnknown = true;

      return (this.Effect(this.Helpers.PowerInt(this.Integer(left).Item1, this.Integer(right).Item1)), PyType.Int);
    }

    this.Notes.PowersFloats |= floating;
    return (this.Effect(this.Helpers.PowerFloat(Floating(left), Floating(right))), PyType.Float);
  }

  private (IrExpr, PyType) Unary(PyNode expression) {
    var op = expression.Operator()!;
    var operandNode = expression.Node("operand")!;
    // -<literal> is the literal of the negative value.
    if (op == "USub" && operandNode.Is("Constant") && Constants.Integer(expression) is { } literal)
      return IntegerConstant(literal);

    var operand = this.Value(operandNode);
    if (op == "Not")
      return (Not(this.Truth(operand)), PyType.Bool);

    if (!ExpressionTypes.Value(operand.Type).IsNumber)
      throw this.Unsupported(expression, $"{(op == "USub" ? "-" : op == "UAdd" ? "+" : "~")} of a value of the type {operand.Type}, which raises TypeError,");

    operand = this.Plain(operand, expression);
    switch (op) {
      case "USub" when operand.Type.Kind == PyKind.Float:
        return (new IrUnary(IrUnaryOp.Negate, operand.Value), PyType.Float);
      case "UAdd" when operand.Type.Kind == PyKind.Float:
        return operand;
      case "Invert" when operand.Type.Kind == PyKind.Float:
        throw this.Unsupported(expression, "~ with a float operand, which raises TypeError,");
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
    var left = this.Operand(expression.Node("left")!, ops[0].Kind);
    IrExpr? result = null;
    for (var index = 0; index < ops.Count; ++index) {
      if (index > 0)
        ++this._conditional;

      var before = this._effects;
      var right = this.Operand(operands[index], ops[index].Kind);
      if (index < ops.Count - 1)
        right = this.Kept(operands[index], right, before);

      var comparison = this.Comparison(expression, ops[index].Kind, left, right);
      result = result is null ? comparison : Binary(IrBinaryOp.LogicalAnd, result, comparison);
      left = right;
    }

    this._conditional -= ops.Count - 1;
    return result!;
  }

  /// <summary>An operand of a comparison: <c>None</c> stands for itself there.</summary>
  private (IrExpr Value, PyType Type) Operand(PyNode operand, string op) => this.Lower(operand);

  /// <summary>
  /// A value used twice, as it is: a constant or a variable; otherwise in a temporary the statement
  /// declares first. A temporary would move the evaluation before whatever the statement evaluated
  /// already, so it is refused where that could be told: under a condition, or after another
  /// operation that may raise.
  /// </summary>
  private (IrExpr Value, PyType Type) Kept(PyNode node, (IrExpr Value, PyType Type) value, int before) {
    if (value.Value is IrConst or IrBigConst or IrLocalRef or IrStrConst or IrConstRef)
      return value;

    // Held first, the value would run before every operation the statement evaluated inline since the last value held.
    var reordered = this._effects > before && before > System.Math.Max(this._statementEffects, this._heldEffects);
    if (this._conditional > 0 || reordered)
      throw this.Unsupported(node, $"{this.Quote(node)} used twice where it cannot be evaluated first");

    var temporary = this.Fresh("value", value.Value.Type);
    this._prefix.Add(new IrDeclare(temporary, value.Value));
    this._heldEffects = this._effects;
    return (Ref(temporary), value.Type);
  }

  /// <summary>One comparison of two values: ordering and equality, identity (<c>is</c>) and membership (<c>in</c>).</summary>
  private IrExpr Comparison(PyNode node, string op, (IrExpr Value, PyType Type) left, (IrExpr Value, PyType Type) right) {
    switch (op) {
      case "Is" or "IsNot":
        var same = this.Identity(node, left, right);
        return op == "Is" ? same : Not(same);
      case "In" or "NotIn":
        var found = this.Membership(node, left, right);
        return op == "In" ? found : Not(found);
    }

    if (left.Type.Kind == PyKind.None || right.Type.Kind == PyKind.None) {
      if (op is not ("Eq" or "NotEq"))
        throw this.Unsupported(node, $"{_symbols[op]} of None, which raises TypeError,");

      var none = this.Identity(node, left, right);
      return op == "Eq" ? none : Not(none);
    }

    var (a, b) = (ExpressionTypes.Value(left.Type), ExpressionTypes.Value(right.Type));
    if (a.IsNumber && b.IsNumber && left.Type.Kind != PyKind.Optional && right.Type.Kind != PyKind.Optional || (a.IsNumber && b.IsNumber && op is not ("Eq" or "NotEq")))
      return this.NumberComparison(op, this.Plain(left, node), this.Plain(right, node));

    if (op is not ("Eq" or "NotEq") && (a.Kind is PyKind.Dict or PyKind.Set || b.Kind is PyKind.Dict or PyKind.Set))
      throw this.DictRefusal(node, $"{_symbols[op]} of the types {left.Type} and {right.Type}", ", which compares subsets");

    if (op is not ("Eq" or "NotEq"))
      throw a.Kind == PyKind.Str && b.Kind == PyKind.Str
        ? this.Refuse(node, PythonFrontend.UnsupportedStringDescriptor, $"string operation 'ordering of strings by {_symbols[op]}' is not supported by the Python frontend")
        : this.Refuse(node, PythonFrontend.UnsupportedSequenceDescriptor, $"sequence operation '{_symbols[op]} of the types {left.Type} and {right.Type}' is not supported by the Python frontend");

    var equal = this.Equality(node, left, right);
    return op == "Eq" ? equal : Not(equal);
  }

  /// <summary>An ordering or an equality of two numbers: exact also between an <c>int</c> and a <c>float</c>.</summary>
  private IrExpr NumberComparison(string op, (IrExpr Value, PyType Type) left, (IrExpr Value, PyType Type) right) {
    var code = PythonHelpers.Comparisons[op];
    if (left.Type.Kind == PyKind.Bool && right.Type.Kind == PyKind.Bool && op is "Eq" or "NotEq")
      return Binary(_comparisons[op], left.Value, right.Value);

    var (a, b) = (this.Integer(left), this.Integer(right));
    if (a.Item2 == b.Item2)
      return Binary(_comparisons[op], a.Item1, b.Item1);

    // An int and a float compare exactly; a constant int a float holds exactly compares as that float.
    if (a.Item2.Kind == PyKind.Int && a.Item1 is IrBigConst leftConstant && BigInteger.Abs(leftConstant.Value) <= 1L << 53)
      return Binary(_comparisons[op], Float((double)leftConstant.Value), b.Item1);

    if (b.Item2.Kind == PyKind.Int && b.Item1 is IrBigConst rightConstant && BigInteger.Abs(rightConstant.Value) <= 1L << 53)
      return Binary(_comparisons[op], a.Item1, Float((double)rightConstant.Value));

    return this.Effect(a.Item2.Kind == PyKind.Int ? this.Helpers.CompareIntFloat(a.Item1, b.Item1, code) : this.Helpers.CompareFloatInt(a.Item1, b.Item1, code));
  }

  /// <summary>
  /// <c>a == b</c> as Python defines it for the types of the import: numbers by value, strings by
  /// their code points, bytes, lists and tuples element by element, objects by their class's
  /// <c>__eq__</c> or else by identity, members of an enum by identity - a member of a plain
  /// <c>Enum</c> equals no number.
  /// </summary>
  private IrExpr Equality(PyNode node, (IrExpr Value, PyType Type) left, (IrExpr Value, PyType Type) right) {
    // Two optional objects of one class compare as Python does: None equals only None.
    if ((left.Type.Kind == PyKind.Optional || right.Type.Kind == PyKind.Optional) && !(left.Type == right.Type && left.Type.Element!.Kind == PyKind.Object))
      throw this.Unsupported(node, $"== of the optional value of the type {(left.Type.Kind == PyKind.Optional ? left.Type : right.Type)}; test it with 'is None' first");

    if (left.Type.Kind == PyKind.Enum && right.Type.Kind == PyKind.Enum && !ReferenceEquals(left.Type.Class, right.Type.Class)
        || (left.Type.Kind == PyKind.Enum && left.Type.Class!.Kind == ClassKind.Enum) != (right.Type.Kind == PyKind.Enum && right.Type.Class!.Kind == ClassKind.Enum)) {
      // A member of a plain Enum equals only itself: the operands are evaluated and the answer is no.
      if (left.Value is not (IrConst or IrBigConst or IrLocalRef) || right.Value is not (IrConst or IrBigConst or IrLocalRef))
        throw this.Unsupported(node, $"== of {left.Type} and {right.Type}, which is always False,");

      return Bool(false);
    }

    if (left.Type.Kind == PyKind.Enum && right.Type.Kind == PyKind.Enum)
      return Equal(left.Value, right.Value);

    var (a, b) = (ExpressionTypes.Value(left.Type), ExpressionTypes.Value(right.Type));
    if (a.IsNumber && b.IsNumber)
      return this.NumberComparison("Eq", this.Plain(left, node), this.Plain(right, node));

    if (a.IsBytes && b.IsBytes)
      return this.Effect(this.Helpers.BytesEqual(left.Value, right.Value));

    if (left.Type != right.Type && !(left.Type.Kind == PyKind.List && right.Type.Kind == PyKind.List && PyTypes.Join(left.Type, right.Type) is not null))
      throw this.Unsupported(node, $"== of the types {left.Type} and {right.Type}");

    return this.SameTypeEquality(node, left.Value, right.Value, left.Type);
  }

  /// <summary>Python's <c>==</c> of two values of one type.</summary>
  private IrExpr SameTypeEquality(PyNode node, IrExpr left, IrExpr right, PyType type) {
    switch (type.Kind) {
      case PyKind.Str:
        return Intrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, left, right);
      case PyKind.Bytes or PyKind.ByteArray:
        return this.Effect(this.Helpers.BytesEqual(left, right));
      case PyKind.Optional when type.Element!.Kind == PyKind.Object && !type.Element.Class!.IrMethods.ContainsKey("__eq__"):
        return new IrReferenceEquals(left, right);
      case PyKind.Optional when type.Element!.Kind == PyKind.Object:
        // None equals only None; an object compares by its __eq__ only with another object.
        return this.Effect(this.Helpers.OptionalEqual(left, right, PyTypes.Ir(type), (a, b) => this.SameTypeEquality(node, a, b, type.Element!)));
      case PyKind.List:
        return this.Effect(this.Helpers.ListEqual(left, right, PyTypes.Ir(type), (x, y) => this.ElementEquality(node, x, y, type.Element!)));
      case PyKind.Tuple:
        return this.Effect(this.Helpers.TupleEqual(left, right, PyTypes.Ir(type), (x, y) => {
          var fields = PyTypes.Ir(type).StructType!.Fields;
          return fields.Select((field, index) => this.ElementEquality(node, new IrFieldLoad(x, field), new IrFieldLoad(y, field), type.Items[index]))
            .Aggregate((IrExpr)Bool(true), (all, next) => Binary(IrBinaryOp.LogicalAnd, all, next));
        }));
      case PyKind.Dict:
        return this.Effect(this.Helpers.DictEqual(left, right, PyTypes.Ir(type), (x, y) => this.ElementEquality(node, x, y, type.Element!)));
      case PyKind.Set:
        return this.Effect(this.Helpers.SetEqual(left, right, PyTypes.Ir(type)));
      case PyKind.Object:
        return type.Class!.IrMethods.TryGetValue("__eq__", out var equals)
          ? this.Effect(new IrMethodCall(IrType.Bool, equals, left, [right]))
          : new IrReferenceEquals(left, right);
      default:
        throw this.Unsupported(node, $"== of the type {type}");
    }
  }

  /// <summary>Python's <c>==</c> of two elements of one type inside a helper: numbers by value, a NaN unequal to itself.</summary>
  private IrExpr ElementEquality(PyNode node, IrExpr left, IrExpr right, PyType type)
    => type.IsNumber ? Binary(IrBinaryOp.Equal, left, right) : type.Kind == PyKind.Enum ? Equal(left, right) : this.SameTypeEquality(node, left, right, type);

  /// <summary>
  /// <c>a is b</c>: <c>None</c> against an optional or a reference, objects and bytes by identity, members
  /// of an enum and truth values by value. Identity of lists, numbers and strings is CPython's
  /// implementation detail and is refused.
  /// </summary>
  private IrExpr Identity(PyNode node, (IrExpr Value, PyType Type) left, (IrExpr Value, PyType Type) right) {
    if (right.Type.Kind == PyKind.None || left.Type.Kind == PyKind.None) {
      var other = right.Type.Kind == PyKind.None ? left : right;
      if (other.Type.Kind == PyKind.None)
        return Bool(true);

      if (other.Type.Kind == PyKind.Optional)
        return this.IsNone(other);

      if (other.Value is IrConst or IrLocalRef or IrStrConst)
        return Bool(false);

      throw this.Unsupported(node, $"is None of a value of the type {other.Type}, which is never None,");
    }

    switch (left.Type.Kind) {
      case PyKind.Object or PyKind.Bytes or PyKind.ByteArray when left.Type == right.Type || (left.Type.IsBytes && right.Type.IsBytes)
          || (left.Type.Kind == PyKind.Object && right.Type.Kind == PyKind.Object && (left.Type.Class!.DerivesFrom(right.Type.Class!) || right.Type.Class!.DerivesFrom(left.Type.Class!))):
        return new IrReferenceEquals(left.Value, right.Value);
      case PyKind.Enum or PyKind.Bool when left.Type == right.Type:
        return Equal(left.Value, right.Value);
      case PyKind.List or PyKind.Tuple:
        throw this.Refuse(node, PythonFrontend.UnsupportedSequenceDescriptor, $"sequence operation 'is between values of the types {left.Type} and {right.Type}' is not supported by the Python frontend");
      default:
        throw this.Unsupported(node, $"is between values of the types {left.Type} and {right.Type}, whose identity CPython does not define");
    }
  }

  /// <summary>Whether an optional holds none.</summary>
  private IrExpr IsNone((IrExpr Value, PyType Type) optional)
    => PyTypes.IsReference(optional.Type.Element!) ? new IrIsNull(optional.Value) : new IrIsNone(optional.Value);

  /// <summary>
  /// <c>x in s</c>: a substring in a str; an element in a list, a tuple, a table or bytes, found by
  /// Python's <c>==</c> from the first element on. The element is evaluated before the sequence.
  /// </summary>
  private IrExpr Membership(PyNode node, (IrExpr Value, PyType Type) element, (IrExpr Value, PyType Type) sequence) {
    switch (sequence.Type.Kind) {
      case PyKind.Dict or PyKind.Set:
        return this.Contains(node, element, sequence);
      case PyKind.Str when element.Type.Kind == PyKind.Str:
        return this.Effect(this.Helpers.StrContains(element.Value, sequence.Value));
      case PyKind.Bytes or PyKind.ByteArray when ExpressionTypes.Value(element.Type) is { Kind: PyKind.Int or PyKind.Bool }:
        return this.Effect(this.Helpers.BytesContains(this.Integer(this.Plain(element, node)).Item1, sequence.Value));
      case PyKind.List or PyKind.Table:
        var type = sequence.Type.Element!;
        var probe = this.Coerce(element, type, node);
        if (type.Kind == PyKind.Float)
          this.Notes.SearchesFloats = true;

        return this.Effect(this.Helpers.Contains(probe, sequence.Value, PyTypes.Ir(type), PyTypes.Ir(sequence.Type), (x, y) => this.ElementEquality(node, x, y, type)));
      case PyKind.Tuple when sequence.Type.Items.Count > 0 && sequence.Type.Items.All(item => item == sequence.Type.Items[0]):
        var item = sequence.Type.Items[0];
        return this.Effect(this.Helpers.TupleContains(this.Coerce(element, item, node), sequence.Value, PyTypes.Ir(sequence.Type), (x, y) => this.ElementEquality(node, x, y, item)));
      default:
        throw this.Refuse(node, PythonFrontend.UnsupportedSequenceDescriptor, $"sequence operation 'in of the types {element.Type} and {sequence.Type}' is not supported by the Python frontend");
    }
  }

  /// <summary>
  /// <c>and</c> and <c>or</c> give one of their operands, not a truth value: <c>0 or 9</c> is 9. Of
  /// truth values they are the short-circuit operators; otherwise the first operand is tested and
  /// given, the rest evaluated only when it decides nothing.
  /// </summary>
  private (IrExpr, PyType) Logical(PyNode expression) {
    var values = expression.Nodes("values");
    var and = expression.Operator() == "And";
    var type = this.TypeOf(expression);
    if (type.Kind == PyKind.Unknown)
      throw this.Refuse(expression, PythonFrontend.MixedKindsDescriptor, $"'{this.Quote(expression)}' gives values of types no type of the import holds both of");

    return (this.Logical(values, 0, and, type), type);
  }

  private IrExpr Logical(IReadOnlyList<PyNode> values, int index, bool and, PyType type) {
    var before = this._effects;
    var first = this.Value(values[index], type);
    if (index == values.Count - 1)
      return this.Coerce(first, type, values[index]);

    if (type.Kind == PyKind.Bool && first.Type.Kind == PyKind.Bool) {
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
    // Of `or`, an optional first operand is given only where it is true, so as its value.
    var own = !and && first.Type.Kind == PyKind.Optional && type.Kind != PyKind.Optional
      ? this.Coerce(this.Plain(first, values[index]), type, values[index])
      : this.Coerce(first, type, values[index]);
    return and ? Choose(test, others, own) : Choose(test, own, others);
  }

  private (IrExpr, PyType) Conditional(PyNode expression, PyType? expected) {
    var type = this.TypeOf(expression);
    if (type.IsIncomplete && expected is not null && PyTypes.Join(type, expected) is { } known)
      type = known;

    if (type.Kind == PyKind.Unknown)
      throw this.Refuse(expression, PythonFrontend.MixedKindsDescriptor, $"'{this.Quote(expression)}' gives values of types no type of the import holds both of");

    var test = this.Truth(this.Value(expression.Node("test")!));
    ++this._conditional;
    var whenTrue = this.Coerce(this.Lower(expression.Node("body")!, type), type, expression.Node("body")!);
    var whenFalse = this.Coerce(this.Lower(expression.Node("orelse")!, type), type, expression.Node("orelse")!);
    --this._conditional;
    return (Choose(test, whenTrue, whenFalse), type);
  }

}
