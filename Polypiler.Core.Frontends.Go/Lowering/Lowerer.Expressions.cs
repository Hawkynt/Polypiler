using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Frontends.Go.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Go.Lowering;

internal sealed partial class Lowerer {

  /// <summary>
  /// An expression as a value of a type: a constant expression is the value <c>go/types</c> computed,
  /// exactly, converted to the type it takes in its context; any other is lowered with Go's meaning.
  /// </summary>
  private IrExpr Value(GoNode node, IrType? expected = null) {
    if (node.Constant is not null)
      return this.Constant(node, expected ?? this.TypeOf(node));

    var value = node.Kind switch {
      "ParenExpr" => this.Value(node.Node("X")!, expected),
      "Ident" => this.Name(node),
      "BinaryExpr" => this.Binary(node),
      "UnaryExpr" => this.Unary(node),
      "CallExpr" => this.Call(node),
      "IndexExpr" => this.Element(node),
      "SelectorExpr" => this.Selector(node),
      "CompositeLit" => this.Composite(node),
      "SliceExpr" => throw Unsupported(node, "slice expression"),
      _ => throw Unsupported(node, node.Kind)
    };
    return value;
  }

  /// <summary>A constant of a type, from the exact value <c>go/types</c> printed.</summary>
  private IrExpr Constant(GoNode node, IrType type) {
    var text = node.Constant!;
    switch (node.ConstantKind) {
      case "bool":
        return new IrConst(IrType.Bool, text == "true" ? 1 : 0);
      case "string":
        return new IrStrConst(Strings.Unquote(text, node));
      case "int" when type.IsInteger:
        return new IrConst(type, Bits(type, BigInteger.Parse(text, CultureInfo.InvariantCulture)));
      case "int" when type.IsFloat:
        return Floating(type, (double)BigInteger.Parse(text, CultureInfo.InvariantCulture));
      case "float" when node.Unsigned("float64") is { } bits:
        var number = BitConverter.UInt64BitsToDouble(bits);
        return type.IsFloat ? Floating(type, number) : type.IsInteger ? new IrConst(type, Bits(type, new BigInteger(number))) : throw UnsupportedType(node, node.Type);
      default:
        throw UnsupportedType(node, $"{node.ConstantKind} constant {node.Type}");
    }
  }

  private static IrConst Floating(IrType type, double number) => new(type, type == IrType.F32 ? IrOperators.Bits(type, (float)number) : IrOperators.Bits(type, number));

  /// <summary>The value an <see cref="IrConst"/> carries for an integer: the number, a <c>u64</c> as its 64 bits.</summary>
  private static long Bits(IrType type, BigInteger value) => type == IrType.U64 ? unchecked((long)(ulong)value) : (long)value;

  private IrExpr Name(GoNode node) {
    if (this._locals.TryGetValue(node.Declaration, out var local))
      return new IrLocalRef(local);

    if (this._globals.TryGetValue(node.Declaration, out var global))
      return new IrGlobalRef(global);

    throw Unsupported(node, node.Object switch {
      "func" => $"function value {node.Text("Name")}",
      "nil" => "nil",
      _ => $"{node.Object} {node.Text("Name")}"
    });
  }

  /// <summary>The 32-bit type a narrow integer type computes in: signed for a signed one; any other type is its own.</summary>
  private static IrType Wide(IrType type) => type.IsInteger && type.Bits < 32 ? type.IsSigned ? IrType.I32 : IrType.U32 : type;

  /// <summary>An integer of fewer than 32 bits widened to the type it computes in; the IR's arithmetic and comparisons are on 32 and 64 bits.</summary>
  private static IrExpr Comparable(IrExpr value) => Wide(value.Type) == value.Type ? value : new IrConvert(Wide(value.Type), value);

  /// <summary>A result computed in a wider type, wrapped to the operand type, as Go's arithmetic on that type is.</summary>
  private static IrExpr Wrapped(IrExpr value, IrType type) => value.Type == type ? value : new IrConvert(type, value);

  private IrExpr Binary(GoNode node) {
    var op = node.Text("Op")!;
    var left = node.Node("X")!;
    var right = node.Node("Y")!;
    switch (op) {
      case "&&":
        return new IrBinary(IrBinaryOp.LogicalAnd, this.Value(left, IrType.Bool), this.Value(right, IrType.Bool));
      case "||":
        return new IrBinary(IrBinaryOp.LogicalOr, this.Value(left, IrType.Bool), this.Value(right, IrType.Bool));
      case "<<" or ">>": {
        var type = this.TypeOf(node);
        return this.Shift(op, this.Value(left, type), right, type, node);
      }
    }

    var operand = this.TypeOf(left);
    if (op is "==" or "!=" or "<" or "<=" or ">" or ">=")
      return this.Comparison(op, this.Value(left, operand), this.Value(right, operand), node);

    return this.Arithmetic(op, this.Value(left, operand), this.Value(right, operand), operand, node);
  }

  private IrExpr Equality(IrExpr left, IrExpr right, GoNode node) => this.Comparison("==", left, right, node);

  private IrExpr Comparison(string op, IrExpr left, IrExpr right, GoNode node) {
    var type = left.Type;
    if (type.IsString) {
      var equal = new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [left, right]);
      return op switch {
        "==" => equal,
        "!=" => new IrUnary(IrUnaryOp.LogicalNot, equal),
        _ => throw Unsupported(node, "ordering of strings, which Go compares by bytes")
      };
    }

    if (type.IsStruct) {
      if (op is not "==" and not "!=" || StructFields(type).Any(field => field.Type.IsFloat || field.Type.IsArray || field.Type.IsString))
        throw Unsupported(node, $"comparison of struct {type}");

      var equal = new IrStructEquals(left, right);
      return op == "==" ? equal : new IrUnary(IrUnaryOp.LogicalNot, equal);
    }

    if (!(type.IsNumeric || type == IrType.Bool))
      throw UnsupportedType(node, type.ToString());

    var irOp = op switch {
      "==" => IrBinaryOp.Equal,
      "!=" => IrBinaryOp.NotEqual,
      "<" => IrBinaryOp.Less,
      "<=" => IrBinaryOp.LessOrEqual,
      ">" => IrBinaryOp.Greater,
      _ => IrBinaryOp.GreaterOrEqual
    };
    return new IrBinary(irOp, Comparable(left), Comparable(right));
  }

  private static IEnumerable<IrField> StructFields(IrType type)
    => type.Declaration is IrStructType structure ? structure.Fields.SelectMany(field => field.Type.IsStruct ? StructFields(field.Type).Prepend(field) : [field]) : [];

  /// <summary>
  /// An arithmetic or bitwise operator of Go on two values of one type: at the width of the type,
  /// wrapping; an integer of fewer than 32 bits computed on 32 and wrapped back. The signed division and
  /// remainder by a divisor that may be -1 go through a helper, since Go wraps the smallest value
  /// divided by -1 where the IR traps.
  /// </summary>
  private IrExpr Arithmetic(string op, IrExpr left, IrExpr right, IrType type, GoNode node) {
    if (type.IsString) {
      if (op != "+")
        throw Unsupported(node, $"operator {op} on strings");

      return new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [left, right]);
    }

    if (type.IsFloat) {
      var floatOp = op switch {
        "+" => IrBinaryOp.Add,
        "-" => IrBinaryOp.Subtract,
        "*" => IrBinaryOp.Multiply,
        "/" => IrBinaryOp.Divide,
        _ => throw Unsupported(node, $"operator {op} on {type}")
      };
      if (floatOp is IrBinaryOp.Add or IrBinaryOp.Subtract && (left is IrBinary { Op: IrBinaryOp.Multiply } || right is IrBinary { Op: IrBinaryOp.Multiply }))
        this.Caveat(GoFrontend.Fusion);

      return new IrBinary(floatOp, left, right);
    }

    if (!type.IsInteger)
      throw UnsupportedType(node, type.ToString());

    var wide = Wide(type);
    var (a, b) = (Comparable(left), Comparable(right));
    IrExpr result = op switch {
      "+" => new IrBinary(IrBinaryOp.Add, a, b),
      "-" => new IrBinary(IrBinaryOp.Subtract, a, b),
      "*" => new IrBinary(IrBinaryOp.Multiply, a, b),
      "&" => new IrBinary(IrBinaryOp.And, a, b),
      "|" => new IrBinary(IrBinaryOp.Or, a, b),
      "^" => new IrBinary(IrBinaryOp.Xor, a, b),
      "&^" => new IrBinary(IrBinaryOp.And, a, new IrUnary(IrUnaryOp.BitNot, b)),
      "/" or "%" => this.Division(op == "/", a, b, type),
      _ => throw Unsupported(node, $"operator {op}")
    };
    return Wrapped(result, type);
  }

  private IrExpr Division(bool quotient, IrExpr left, IrExpr right, IrType type) {
    var op = quotient ? IrBinaryOp.Divide : IrBinaryOp.Remainder;
    // Below 32 bits the quotient of the smallest value by -1 fits the wider type the division runs in.
    if (!type.IsSigned || type.Bits < 32 || right is IrConst { Value: not -1 and not 0 })
      return new IrBinary(op, left, right);

    return new IrCall(this.DivisionHelper(quotient, type), type, [left, right]);
  }

  /// <summary>
  /// A shift of Go: the count is not masked. A constant count below the width is the IR's shift; any
  /// other goes through a helper that gives 0 - or the sign, shifting a signed value right - for a
  /// count of the width or more, and traps on a negative one, as Go panics.
  /// </summary>
  private IrExpr Shift(string op, IrExpr value, GoNode countNode, IrType type, GoNode node) {
    if (!type.IsInteger)
      throw UnsupportedType(node, type.ToString());

    var irOp = op == "<<" ? IrBinaryOp.ShiftLeft : IrBinaryOp.ShiftRight;
    if (countNode.Constant is { } text && BigInteger.Parse(text, CultureInfo.InvariantCulture) is var constant && constant < type.Bits)
      return Wrapped(new IrBinary(irOp, Comparable(value), new IrConst(IrType.I32, (long)constant)), type);

    var countType = countNode.Constant is not null ? IrType.U64 : this.TypeOf(countNode);
    var count = countNode.Constant is not null ? new IrConst(IrType.U64, Bits(IrType.U64, BigInteger.Min(BigInteger.Parse(countNode.Constant, CultureInfo.InvariantCulture), ulong.MaxValue))) : this.Value(countNode);
    return new IrCall(this.ShiftHelper(op == "<<", type, countType), type, [value, count]);
  }

  private IrExpr Unary(GoNode node) {
    var op = node.Text("Op");
    var operand = node.Node("X")!;
    var type = this.TypeOf(node);
    switch (op) {
      case "+":
        return this.Value(operand, type);
      case "!":
        return new IrUnary(IrUnaryOp.LogicalNot, this.Value(operand, IrType.Bool));
      case "-" when type.IsFloat:
        return new IrUnary(IrUnaryOp.Negate, this.Value(operand, type));
      case "-" when type.IsInteger: {
        var value = Comparable(this.Value(operand, type));
        IrExpr negated = value.Type.IsSigned ? new IrUnary(IrUnaryOp.Negate, value) : new IrBinary(IrBinaryOp.Subtract, new IrConst(value.Type, 0), value);
        return Wrapped(negated, type);
      }
      case "^" when type.IsInteger:
        return Wrapped(new IrUnary(IrUnaryOp.BitNot, Comparable(this.Value(operand, type))), type);
      case "&":
        throw Unsupported(node, "address of");
      case "<-":
        throw Unsupported(node, "channel receive");
      default:
        throw UnsupportedType(node, type.ToString());
    }
  }

  private IrExpr Call(GoNode node) {
    var callee = Strip(node.Node("Fun")!);
    var arguments = node.Nodes("Args");
    if (node.Flag("spread"))
      throw Unsupported(node, "spread argument");

    if (callee.Mode == "type")
      return this.Conversion(arguments[0], this.TypeOf(node), node);

    if (callee.Is("Ident") && callee.Object == "builtin")
      return this.Builtin(callee.Text("Name")!, arguments, node);

    if (callee.Is("SelectorExpr") && callee.Node("X") is { Object: "pkg" } package)
      return this.Library(package.Package!, callee.Node("Sel")!.Text("Name")!, arguments, node);

    if (callee.Is("Ident") && callee.Object == "func" && this._signatures.TryGetValue(callee.Text("Name")!, out var function)) {
      if (arguments.Count != function.Parameters.Count)
        throw Unsupported(node, "a call with a call of several results as its arguments");

      return new IrCall(function.Name, function.ReturnType, [.. arguments.Select((argument, index) => this.Value(argument, function.Parameters[index].Type))]);
    }

    throw Unsupported(node, callee.Is("SelectorExpr") ? "method call" : "call of a function value");
  }

  /// <summary>
  /// A conversion: between integers it wraps; to floating point it rounds to nearest even; from
  /// floating point to an integer it truncates, and saturates outside the range, where Go leaves the
  /// result to the implementation (<see cref="GoFrontend.FloatToInteger"/>).
  /// </summary>
  private IrExpr Conversion(GoNode argument, IrType to, GoNode node) {
    var value = this.Value(argument);
    var from = value.Type;
    if (from == to)
      return value;

    if (!(from.IsNumeric && to.IsNumeric))
      throw Unsupported(node, $"conversion from {argument.Type} to {node.Type}");

    if (from.IsFloat && to.IsInteger) {
      this.Caveat(GoFrontend.FloatToInteger);
      return new IrConvert(to, value, IrConversion.Saturate);
    }

    return new IrConvert(to, value);
  }

  private IrExpr Builtin(string name, IReadOnlyList<GoNode> arguments, GoNode node) {
    var type = node.Type.Length > 0 && node.Mode != "void" ? this.TypeOf(node) : IrType.Void;
    switch (name) {
      case "len" or "cap": {
        var operand = this.Value(arguments[0]);
        if (!operand.Type.IsArray)
          throw Unsupported(node, $"{name} of {arguments[0].Type}");

        return new IrConvert(type, new IrArrayLength(operand));
      }
      case "make": {
        if (arguments.Count > 2)
          throw Unsupported(node, "make with a capacity above the length");

        var length = this.Value(arguments[1]);
        var count = length.Type == IrType.I32 ? length : new IrConvert(IrType.I32, Comparable(length), IrConversion.Trap);
        return new IrNewArray(type, count);
      }
      case "min" or "max": {
        var values = arguments.Select(argument => this.Value(argument, type)).ToList();
        var op = name == "min" ? IrIntrinsicOp.Min : IrIntrinsicOp.Max;
        return values.Skip(1).Aggregate(values[0], (all, next) => new IrIntrinsic(op, type, [all, next]));
      }
      case "panic":
        throw Unsupported(node, "panic as a value");
      default:
        throw Unsupported(node, $"built-in {name}");
    }
  }

  /// <summary>The exact functions of <c>math</c> and <c>math/bits</c>, as the operations of the IR they are.</summary>
  private IrExpr Library(string package, string name, IReadOnlyList<GoNode> arguments, GoNode node) {
    var type = this.TypeOf(node);
    IrExpr Argument(int index) => this.Value(arguments[index]);
    IrExpr Intrinsic(IrIntrinsicOp op, IrType result, params IrExpr[] values) => new IrIntrinsic(op, result, values);
    if (package == "math") {
      IrIntrinsicOp? unary = name switch {
        "Sqrt" => IrIntrinsicOp.Sqrt,
        "Abs" => IrIntrinsicOp.Abs,
        "Floor" => IrIntrinsicOp.Floor,
        "Ceil" => IrIntrinsicOp.Ceiling,
        "Trunc" => IrIntrinsicOp.Truncate,
        "RoundToEven" => IrIntrinsicOp.RoundEven,
        "Round" => IrIntrinsicOp.RoundAway,
        "Cbrt" => IrIntrinsicOp.Cbrt,
        "Sin" => IrIntrinsicOp.Sin,
        "Cos" => IrIntrinsicOp.Cos,
        "Tan" => IrIntrinsicOp.Tan,
        "Asin" => IrIntrinsicOp.Asin,
        "Acos" => IrIntrinsicOp.Acos,
        "Atan" => IrIntrinsicOp.Atan,
        "Sinh" => IrIntrinsicOp.Sinh,
        "Cosh" => IrIntrinsicOp.Cosh,
        "Tanh" => IrIntrinsicOp.Tanh,
        "Exp" => IrIntrinsicOp.Exp,
        "Exp2" => IrIntrinsicOp.Exp2,
        "Expm1" => IrIntrinsicOp.ExpM1,
        "Log" => IrIntrinsicOp.Log,
        "Log2" => IrIntrinsicOp.Log2,
        "Log10" => IrIntrinsicOp.Log10,
        "Log1p" => IrIntrinsicOp.Log1P,
        _ => null
      };
      if (unary is { } single && arguments.Count == 1)
        return Intrinsic(single, type, Argument(0));

      switch (name) {
        case "Min" or "Max":
          return Intrinsic(name == "Min" ? IrIntrinsicOp.Min : IrIntrinsicOp.Max, type, Argument(0), Argument(1));
        case "Mod":
          return new IrBinary(IrBinaryOp.Remainder, Argument(0), Argument(1));
        case "Copysign":
          return Intrinsic(IrIntrinsicOp.CopySign, type, Argument(0), Argument(1));
        case "FMA":
          return Intrinsic(IrIntrinsicOp.FusedMultiplyAdd, type, Argument(0), Argument(1), Argument(2));
        case "Pow":
          return Intrinsic(IrIntrinsicOp.Pow, type, Argument(0), Argument(1));
        case "Atan2":
          return Intrinsic(IrIntrinsicOp.Atan2, type, Argument(0), Argument(1));
        case "Hypot":
          return Intrinsic(IrIntrinsicOp.Hypot, type, Argument(0), Argument(1));
        case "Float64bits":
          return new IrConvert(IrType.U64, Intrinsic(IrIntrinsicOp.FloatBits, IrType.I64, Argument(0)));
        case "Float32bits":
          return new IrConvert(IrType.U32, Intrinsic(IrIntrinsicOp.FloatBits, IrType.I32, Argument(0)));
        case "Float64frombits":
          return Intrinsic(IrIntrinsicOp.BitsFloat, IrType.F64, new IrConvert(IrType.I64, Argument(0)));
        case "Float32frombits":
          return Intrinsic(IrIntrinsicOp.BitsFloat, IrType.F32, new IrConvert(IrType.I32, Argument(0)));
        case "IsNaN":
          return Intrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, Argument(0));
        case "NaN":
          return new IrConst(IrType.F64, unchecked((long)0x7FF8000000000001UL));
        case "Inf":
          return new IrConditional(new IrBinary(IrBinaryOp.GreaterOrEqual, Argument(0), new IrConst(IrType.I64, 0)),
            Floating(IrType.F64, double.PositiveInfinity), Floating(IrType.F64, double.NegativeInfinity));
      }
    } else if (package == "math/bits") {
      var width = new string([.. name.Reverse().TakeWhile(char.IsAsciiDigit).Reverse()]);
      var stem = name[..^width.Length];
      IrExpr Counted(IrIntrinsicOp op) => new IrConvert(IrType.I64, Intrinsic(op, IrType.I32, Argument(0)));
      switch (stem) {
        case "RotateLeft":
          return Intrinsic(IrIntrinsicOp.RotateLeft, type, Argument(0), new IrConvert(IrType.I32, Argument(1)));
        case "OnesCount":
          return Counted(IrIntrinsicOp.PopCount);
        case "LeadingZeros":
          return Counted(IrIntrinsicOp.LeadingZeroCount);
        case "TrailingZeros":
          return Counted(IrIntrinsicOp.TrailingZeroCount);
        case "Len": {
          var operand = Argument(0);
          return new IrBinary(IrBinaryOp.Subtract, new IrConst(IrType.I64, operand.Type.Bits),
            new IrConvert(IrType.I64, Intrinsic(IrIntrinsicOp.LeadingZeroCount, IrType.I32, operand)));
        }
        case "ReverseBytes" when width != "8":
          return Intrinsic(IrIntrinsicOp.ByteSwap, type, Argument(0));
        case "Reverse":
          return Intrinsic(IrIntrinsicOp.BitReverse, type, Argument(0));
      }
    }

    throw Unsupported(node, $"{package}.{name}");
  }

  /// <summary>An index of a slice as the IR's <c>i32</c>: a wider one clamped first, so that a value outside 32 bits is out of range rather than wrapped into it.</summary>
  private IrExpr Index(GoNode node) {
    var index = this.Value(node);
    if (index is IrConst { Type.IsInteger: true } constant)
      return new IrConst(IrType.I32, constant.Value is >= -1 and <= int.MaxValue ? constant.Value : -1);

    return index.Type switch {
      { Kind: IrTypeKind.I32 } => index,
      { IsInteger: true, Bits: < 32 } => new IrConvert(IrType.I32, index),
      { Kind: IrTypeKind.U32 } => new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.Min, IrType.U32, [index, new IrConst(IrType.U32, int.MaxValue)])),
      { Kind: IrTypeKind.I64 } => new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.Clamp, IrType.I64, [index, new IrConst(IrType.I64, -1), new IrConst(IrType.I64, int.MaxValue)])),
      { Kind: IrTypeKind.U64 } => new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.Min, IrType.U64, [index, new IrConst(IrType.U64, int.MaxValue)])),
      _ => throw UnsupportedType(node, node.Type)
    };
  }

  private IrExpr Element(GoNode node) {
    var target = this.Value(node.Node("X")!);
    if (!target.Type.IsArray)
      throw Unsupported(node, $"index of {node.Node("X")!.Type}");

    return new IrArrayLoad(target, this.Index(node.Node("Index")!));
  }

  private IrField FieldOf(IrType type, GoNode selector) {
    var name = selector.Node("Sel")!.Text("Name")!;
    return (type.Declaration as IrStructType)?.Field(name) ?? throw Unsupported(selector, $"field {name} of {type}");
  }

  private IrExpr Selector(GoNode node) {
    if (node.Text("selection") != "field")
      throw Unsupported(node, node.Node("X") is { Object: "pkg" } package ? $"{package.Package}.{node.Node("Sel")!.Text("Name")}" : "method value");

    var owner = this.Value(node.Node("X")!);
    return new IrFieldLoad(owner, this.FieldOf(owner.Type, node));
  }

  /// <summary>A composite literal: a struct with every field, those it leaves out zero; a slice with its elements.</summary>
  private IrExpr Composite(GoNode node) {
    var type = this.TypeOf(node);
    var elements = node.Nodes("Elts");
    if (type.Declaration is IrStructType structure) {
      var values = structure.Fields.Select(field => (IrExpr?)null).ToArray();
      for (var index = 0; index < elements.Count; ++index) {
        var element = elements[index];
        var (field, value) = element.Is("KeyValueExpr")
          ? (structure.Fields.Single(candidate => candidate.Name == element.Node("Key")!.Text("Name")), element.Node("Value")!)
          : (structure.Fields[index], element);
        values[structure.Fields.ToList().IndexOf(field)] = this.Value(value, field.Type);
      }

      return new IrStructNew(type, [.. values.Select((value, index) => value ?? this.ZeroOf(structure.Fields[index].Type, node))]);
    }

    if (type.IsArray) {
      if (elements.Any(element => element.Is("KeyValueExpr")))
        throw Unsupported(node, "slice literal with indices");

      if (elements.Count == 0)
        return new IrNewArray(type, new IrConst(IrType.I32, 0));

      return new IrIntrinsic(IrIntrinsicOp.ArrayOf, type, [.. elements.Select(element => this.Value(element, type.Element))]);
    }

    throw UnsupportedType(node, node.Type);
  }

}
