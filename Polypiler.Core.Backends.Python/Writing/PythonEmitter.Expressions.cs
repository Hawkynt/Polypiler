using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Python;

public static partial class PythonEmitter {

  private sealed partial class Writer {

    private static PyName Name(string name) => new(name);

    private static PyBinary Binary(PyExpression left, string op, PyExpression right) => new(left, op, right);

    private static PyCompare Compare(PyExpression left, string op, PyExpression right) => new([left, right], [op]);

    /// <summary>The negation of a truth value; of a test for identity, membership or equality the test of the opposite.</summary>
    private static PyExpression Not(PyExpression operand) => operand switch {
      PyCompare { Operators: [var op], Operands: [var left, var right] } when Opposite(op) is { } opposite => new PyCompare([left, right], [opposite]) { Type = IrType.Bool },
      PyUnary { Operator: "not", Operand: { Type: { } type } inner } when type == IrType.Bool => inner,
      _ => new PyUnary("not", operand) { Type = IrType.Bool }
    };

    /// <summary>The operator that is the opposite of a comparison for every operand, or <see langword="null"/>: an ordering is not, a NaN fails both.</summary>
    private static string? Opposite(string op) => op switch {
      "is" => "is not",
      "is not" => "is",
      "in" => "not in",
      "not in" => "in",
      "==" => "!=",
      "!=" => "==",
      _ => null
    };

    private static BigInteger Big(Int128 value) => BigInteger.Parse(value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>An integer literal; a mask or a bit pattern of more than 32 bits in hexadecimal with <c>_</c> every four digits.</summary>
    private static PyInt Integer(BigInteger value, IrType? type = null) => new(value) { Type = type };

    /// <summary>A bit pattern in hexadecimal: <c>0xFFFFFFFF</c>, or <c>0xFFFF_FFFF_FFFF_FFFF</c> beyond 32 bits.</summary>
    private static PyInt Hex(BigInteger value) {
      // A mask or a bit pattern is never zero, so a leading zero is only BigInteger's sign digit.
      var digits = value.ToString("X", CultureInfo.InvariantCulture).TrimStart('0');

      if (digits.Length > 8) {
        var grouped = new List<string>();
        for (var end = digits.Length; end > 0; end -= 4)
          grouped.Insert(0, digits[System.Math.Max(0, end - 4)..end]);

        digits = string.Join("_", grouped);
      }

      return new PyInt(value) { Raw = "0x" + digits };
    }

    /// <summary>The mask of the low bits of a width.</summary>
    private static PyInt Mask(int bits) => Hex((BigInteger.One << bits) - 1);

    /// <summary>The smallest value of an integer type, as a literal.</summary>
    private static PyInt Smallest(IrType type) => Integer(Big(IrOperators.Minimum(type)));

    /// <summary>The largest value of an integer type, as a literal.</summary>
    private static PyInt Largest(IrType type) => Integer(Big(IrOperators.Maximum(type)));

    /// <summary>
    /// An exact integer brought into an integer type modulo 2^width: masked when the type is unsigned,
    /// masked and sign-wrapped, <c>((e &amp; M) ^ H) - H</c>, when it is signed. A constant is folded.
    /// </summary>
    private static PyExpression Wrapped(PyExpression value, IrType type) {
      var bits = type.Bits;
      if (value is PyInt constant) {
        var low = constant.Value & ((BigInteger.One << bits) - 1);
        return Integer(type.IsSigned && low >= BigInteger.One << (bits - 1) ? low - (BigInteger.One << bits) : low, type);
      }

      var masked = Binary(value, "&", Mask(bits));
      return (type.IsSigned ? Binary(Binary(masked, "^", Hex(BigInteger.One << (bits - 1))), "-", Hex(BigInteger.One << (bits - 1))) : masked) with { Type = type };
    }

    /// <summary>A helper of the backend, which the module then declares.</summary>
    private PyCall Helper(string name, params PyExpression[] arguments) {
      this._helpers.Add(name);
      return new PyCall(Name(name), arguments);
    }

    /// <summary>An exact integer that has to lie in an integer type: the overflow trap otherwise.</summary>
    private PyExpression Checked(PyExpression value, IrType type) => this.Helper(PythonHelpers.Checked, value, Smallest(type), Largest(type)) with { Type = type };

    /// <summary>A double rounded to binary32.</summary>
    private PyExpression Single(PyExpression value) => this.Helper(PythonHelpers.Single, value) with { Type = IrType.F32 };

    private static PyExpression Math(string name) => new PyAttribute(Name("math"), name);

    private static PyExpression Struct(string function, params PyExpression[] arguments) => new PyCall(new PyAttribute(Name("struct"), function), arguments);

    /// <summary>Bits read as another kind of number: <c>struct.unpack("&lt;to", struct.pack("&lt;from", value))[0]</c>.</summary>
    private static PyExpression Reinterpreted(PyExpression value, string from, string to)
      => new PySubscript(Struct("unpack", new PyString("<" + to), Struct("pack", new PyString("<" + from), value)), Integer(0));

    /// <summary>A constant of a type as a literal.</summary>
    private PyExpression Literal(IrType type, long value) {
      if (type == IrType.Bool)
        return new PyBool(value != 0) { Type = type };

      if (!type.IsFloat)
        return Integer(Big(IrOperators.Number(type, value)), type);

      var number = IrOperators.Float(type, value);
      if (double.IsFinite(number))
        return new PyFloat(number) { Type = type };

      if (double.IsInfinity(number))
        return (number > 0 ? Math("inf") : new PyUnary("-", Math("inf"))) with { Type = type };

      // A NaN: math.nan where it is the quiet NaN Python makes, otherwise its very bits.
      var canonical = type == IrType.F32 ? 0x7FC00000L : 0x7FF8000000000000L;
      if (value == canonical)
        return Math("nan") with { Type = type };

      return type == IrType.F32
        ? Reinterpreted(Hex(new BigInteger((uint)value)), "I", "f") with { Type = type }
        : Reinterpreted(Hex(new BigInteger((ulong)value)), "Q", "d") with { Type = type };
    }

    /// <summary>Whether an expression is known never to be negative: an unsigned value, a constant that is not, a length, or a counter of a range loop that never goes below zero.</summary>
    private bool NonNegative(IrExpr expression) => expression switch {
      { Type.IsInteger: true, Type.IsSigned: false } => true,
      IrConst constant => IrOperators.Number(constant.Type, constant.Value) >= 0,
      IrBigConst big => big.Value.Sign >= 0,
      IrArrayLength => true,
      IrLocalRef reference => this._nonNegative.Contains(reference.Local),
      _ => false
    };

    /// <summary>
    /// The Python expression of an IR expression, which evaluates its operands in the IR's order. A
    /// mutable struct read from a place is copied, unless the expression is used in place (<see cref="Place"/>).
    /// </summary>
    private PyExpression Expression(IrExpr expression) {
      var inPlace = this._inPlace;
      this._inPlace = false;
      var written = this.Written(expression) with { Type = expression.Type };
      return inPlace ? written : this.Copied(expression, written);
    }

    private PyExpression Written(IrExpr expression) {
      if ((this.ObjectExpression(expression) ?? this.FunctionExpression(expression) ?? this.OptionalExpression(expression) ?? this.AsyncExpression(expression)) is { } special)
        return special;

      switch (expression) {
        case IrConst { Type.EnumType: { } enumeration } constant:
          return this.EnumLiteral(enumeration, constant.Value);
        case IrConst constant:
          return this.Literal(constant.Type, constant.Value);
        case IrBigConst big:
          return Integer(big.Value, IrType.BigInt);
        case IrStrConst text:
          return new PyString(text.Value);
        case IrLocalRef reference:
          return this.Read(reference.Local);
        case IrCollection operation:
          return this.CollectionOperation(operation);
        case IrArraySequence sequence:
          return this.Expression(sequence.Array);
        case IrGenericCompare comparison:
          return this.Helper(comparison.Left.Type.IsFloat ? PythonHelpers.CompareFloat : comparison.Left.Type.Bits is > 0 and < 32 && comparison.Left.Type.IsInteger ? PythonHelpers.Difference : PythonHelpers.CompareOrdinal,
            this.Expression(comparison.Left), this.Expression(comparison.Right));
        case IrGenericEquals equality when equality.Left.Type.IsFloat:
          return this.Helper(PythonHelpers.FloatEquals, this.Expression(equality.Left), this.Expression(equality.Right));
        case IrGenericEquals equality when equality.Left.Type.IsObject && equality.Left.Type.ClassType is not { IsRecord: true }:
          return Compare(this.Expression(equality.Left), "is", this.Expression(equality.Right));
        case IrGenericEquals equality:
          return Compare(this.Expression(equality.Left), "==", this.Expression(equality.Right));
        case IrConstRef reference:
          return Name(this._constants[reference.Constant.Name]);
        case IrGlobalRef reference when this._staticOwners.ContainsKey(reference.Global.Name):
          return this.StaticField(reference.Global);
        case IrGlobalRef reference:
          return Name(this._globals[reference.Global.Name]);
        case IrUnary unary:
          return this.Unary(unary);
        case IrBinary binary:
          return this.Binary(binary);
        case IrConvert conversion:
          return this.Conversion(conversion);
        case IrConditional choice:
          return new PyConditional(this.Expression(choice.Condition), this.Expression(choice.WhenTrue), this.Expression(choice.WhenFalse));
        case IrCall call when this._functions.TryGetValue(call.Function, out var callee):
          return new PyCall(Name(callee), [.. call.Arguments.Select(this.Expression)]);
        case IrCall call:
          // A call of what is no function of the module: a constructor or a method of a class.
          return this.Inexpressible($"the call of '{call.Function}'");
        case IrArrayLoad load:
          return new PySubscript(this.Expression(load.Array), this.Index(load.Index));
        case IrArrayLength length:
          return new PyCall(Name("len"), [this.Expression(length.Array)]);
        case IrNewArray { Type.Element.StructType: { } element } creation when this._mutableStructs.Contains(element):
          // Every element is a struct of its own.
          return this.Helper(PythonHelpers.NewStructs, Name(this.TypeNameOf(element)), this.NonNegative(creation.Length) ? this.Expression(creation.Length) : this.Helper(PythonHelpers.Length, this.Expression(creation.Length)));
        case IrNewArray creation:
          this.Annotation(creation.Type);
          return Binary(new PyList([this.FieldDefault(creation.Type.Element!)]), "*", this.NonNegative(creation.Length) ? this.Expression(creation.Length) : this.Helper(PythonHelpers.Length, this.Expression(creation.Length)));
        case IrStep step:
          return this.StepValue(step);
        case IrIntrinsic intrinsic:
          return this.Intrinsic(intrinsic);
        default:
          return this.Inexpressible(KindOf(expression));
      }
    }

    /// <summary>An index: as it is where it cannot be negative, otherwise checked, because Python counts a negative index from the end.</summary>
    private PyExpression Index(IrExpr index) => this.NonNegative(index) ? this.Expression(index) : this.Helper(PythonHelpers.Index, this.Expression(index));

    private PyExpression Unary(IrUnary unary) {
      var type = unary.Operand.Type;
      var operand = this.Expression(unary.Operand);
      switch (unary.Op) {
        case IrUnaryOp.LogicalNot:
          return Not(operand);
        case IrUnaryOp.BitNot when type.IsBigInt:
          return new PyUnary("~", operand);
        case IrUnaryOp.Negate when type.IsBigInt:
          return Negated(operand);
        case IrUnaryOp.BitNot:
          return type.IsSigned ? new PyUnary("~", operand) : Binary(operand, "^", Mask(type.Bits));
        case IrUnaryOp.Negate when type.IsFloat:
          return operand is PyFloat literal ? new PyFloat(-literal.Value) : new PyUnary("-", operand);
        case IrUnaryOp.Negate:
          return Wrapped(Negated(operand), type);
        default:
          return this.Checked(Negated(operand), type);
      }
    }

    /// <summary>The negation of an exact integer; a literal stays one.</summary>
    private static PyExpression Negated(PyExpression operand) => operand is PyInt literal ? Integer(-literal.Value) : new PyUnary("-", operand);

    private static readonly Dictionary<IrBinaryOp, string> _comparisons = new() {
      [IrBinaryOp.Equal] = "==", [IrBinaryOp.NotEqual] = "!=", [IrBinaryOp.Less] = "<",
      [IrBinaryOp.LessOrEqual] = "<=", [IrBinaryOp.Greater] = ">", [IrBinaryOp.GreaterOrEqual] = ">="
    };

    private PyExpression Binary(IrBinary binary) {
      var type = binary.Left.Type;
      var left = this.Expression(binary.Left);
      var right = this.Expression(binary.Right);
      if (_comparisons.TryGetValue(binary.Op, out var comparison))
        return Compare(left, comparison, right);

      switch (binary.Op) {
        case IrBinaryOp.LogicalAnd:
          return new PyBoolOp(left, "and", right);
        case IrBinaryOp.LogicalOr:
          return new PyBoolOp(left, "or", right);
        case IrBinaryOp.And:
          return Binary(left, "&", right);
        case IrBinaryOp.Or:
          return Binary(left, "|", right);
        case IrBinaryOp.Xor:
          return Binary(left, "^", right);
      }

      if (type.IsFloat)
        return this.Floating(binary.Op, type, left, right);

      if (type.IsBigInt)
        return this.BigBinary(binary, left, right);

      switch (binary.Op) {
        case IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply:
          return Wrapped(Binary(left, Arithmetic(binary.Op), right), type);
        case IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked:
          return this.Checked(Binary(left, Arithmetic(binary.Op), right), type);
        case IrBinaryOp.Divide or IrBinaryOp.Remainder:
          // Floor and truncation agree where neither operand is negative; a zero divisor raises
          // ZeroDivisionError either way.
          if (!type.IsSigned || (this.NonNegative(binary.Left) && binary.Right is IrConst { Value: > 0 }))
            return Binary(left, binary.Op == IrBinaryOp.Divide ? "//" : "%", right);

          return this.Helper(binary.Op == IrBinaryOp.Divide ? PythonHelpers.Divide : PythonHelpers.Remainder, left, right, Smallest(type));
        case IrBinaryOp.ShiftLeft:
          return Wrapped(Binary(left, "<<", this.Count(binary.Right, type)), type);
        case IrBinaryOp.ShiftRight:
          return Binary(left, ">>", this.Count(binary.Right, type));
        default:
          // A logical shift of a signed value: its bits as unsigned, shifted, read as signed again;
          // a shift by a constant of at least one leaves the sign bit clear.
          if (!type.IsSigned)
            return Binary(left, ">>", this.Count(binary.Right, type));

          var count = this.Count(binary.Right, type);
          var shifted = Binary(Binary(left, "&", Mask(type.Bits)), ">>", count);
          return count is PyInt { Value.IsZero: false } ? shifted : Wrapped(shifted, type);
      }
    }

    private static string Arithmetic(IrBinaryOp op) => op switch {
      IrBinaryOp.Add or IrBinaryOp.AddChecked => "+",
      IrBinaryOp.Subtract or IrBinaryOp.SubtractChecked => "-",
      _ => "*"
    };

    /// <summary>The count of a shift, masked to the low 5 bits, or 6 for a 64-bit value; a constant is masked here.</summary>
    private PyExpression Count(IrExpr count, IrType type) {
      var mask = type.Bits == 64 ? 63 : 31;
      return count is IrConst constant ? Integer(new BigInteger(constant.Value & mask)) : Binary(this.Expression(count), "&", Integer(mask));
    }

    /// <summary>
    /// Floating-point arithmetic: Python's own operators for doubles, IEEE 754 division and remainder
    /// through helpers, and every <c>f32</c> result rounded to binary32, which is exact because a
    /// double holds the sum, difference, product and quotient of two binary32 numbers precisely enough
    /// to round once; the remainder of two binary32 numbers is exact.
    /// </summary>
    private PyExpression Floating(IrBinaryOp op, IrType type, PyExpression left, PyExpression right) {
      if (op == IrBinaryOp.Remainder)
        return this.Helper(PythonHelpers.FloatRemainder, left, right);

      PyExpression result = op == IrBinaryOp.Divide ? this.Helper(PythonHelpers.FloatDivide, left, right) : Binary(left, Arithmetic(op), right);
      return type == IrType.F32 ? this.Single(result) : result;
    }

    private PyExpression Conversion(IrConvert conversion) {
      var (from, to) = (conversion.Operand.Type, conversion.Type);
      if (conversion.Operand is IrConst constant && from.IsInteger && to.IsInteger && conversion.Contract == IrConversion.Wrap)
        return this.Literal(to, IrOperators.Wrap(to, constant.Value));

      var operand = this.Expression(conversion.Operand);
      if (from == to)
        return operand;

      if (from.IsBigInt || to.IsBigInt)
        return this.BigConversion(conversion, operand);

      if (from.IsInteger && to.IsInteger) {
        // Between integers a conversion wraps or traps (IrConvert).
        if (IrOperators.Contains(to, from))
          return operand;

        return conversion.Contract == IrConversion.Wrap ? Wrapped(operand, to) : this.Checked(operand, to);
      }

      if (from.IsInteger && to.IsFloat)
        return to == IrType.F64 || from.Bits <= 24 ? new PyCall(Name("float"), [operand]) : this.Helper(PythonHelpers.SingleOfInteger, operand);

      if (from.IsFloat && to.IsFloat)
        return to == IrType.F32 ? this.Single(operand) : operand;

      // From floating point to an integer a conversion saturates or traps (IrConvert).
      return this.Helper(conversion.Contract == IrConversion.Trap ? PythonHelpers.Truncated : PythonHelpers.Saturated, operand, Smallest(to), Largest(to));
    }

    /// <summary>
    /// A step as a value: a local becomes <c>(x := new)</c>, or, for the value before the step,
    /// <c>(x, (x := new))[0]</c>, whose tuple is evaluated left to right. A step of an array element
    /// as a value has no expression in Python.
    /// </summary>
    private PyExpression StepValue(IrStep step) {
      if (step.Target is IrArrayLoad load)
        return this.ElementStepValue(load, step);

      var local = ((IrLocalRef)step.Target).Local;
      if (this.IsCell(local)) {
        var cell = Name(this.LocalName(local));
        var stored = this.Helper(PythonHelpers.Put, cell, this.Stepped(this.Read(local), step));
        return step.Postfix ? new PySubscript(new PyTuple([this.Read(local), stored]), Integer(0)) : stored;
      }

      var name = this.LocalName(local);
      var assigned = new PyNamedExpression(name, this.Stepped(Name(name), step));
      return step.Postfix ? new PySubscript(new PyTuple([Name(name), assigned]), Integer(0)) : assigned;
    }

    /// <summary>
    /// A step of an array element as a value: a tuple evaluated left to right that names the array,
    /// the index, the old value (the index checked as it is read) and the new one, and stores the new
    /// one with <c>__setitem__</c>; the value is the new element or, for a postfix step, the old one.
    /// </summary>
    private PyExpression ElementStepValue(IrArrayLoad load, IrStep step) {
      var (array, index, old, @new) = (this.Temporary(), this.Temporary(), this.Temporary(), this.Temporary());
      var read = new PySubscript(Name(array), this.NonNegative(load.Index) ? Name(index) : this.Helper(PythonHelpers.Index, Name(index)));
      var store = new PyCall(new PyAttribute(Name(array), "__setitem__"), [Name(index), Name(@new)]);
      var steps = new PyTuple([
        new PyNamedExpression(array, this.Expression(load.Array)),
        new PyNamedExpression(index, this.Expression(load.Index)),
        new PyNamedExpression(old, read),
        new PyNamedExpression(@new, this.Stepped(Name(old), step)),
        store
      ]);
      return new PySubscript(steps, Integer(step.Postfix ? 2 : 3));
    }

    /// <summary>The value a step stores: the target's value one up or one down, in the target's type.</summary>
    private PyExpression Stepped(PyExpression current, IrStep step) {
      var type = step.Target.Type;
      var op = step.Increment ? "+" : "-";
      if (type.IsFloat) {
        var result = Binary(current, op, new PyFloat(1.0));
        return type == IrType.F32 ? this.Single(result) : result;
      }

      var exact = Binary(current, op, Integer(1));
      return step.Trap ? this.Checked(exact, type) : Wrapped(exact, type);
    }

    /// <summary>
    /// An intrinsic Python has an expression for: the bits of a float and back through <c>struct</c>,
    /// the square root, the count of one bits from 3.10, <c>min</c> and <c>max</c> of integers, and a
    /// list of values. Every other one was replaced by legalization or has no Python form.
    /// </summary>
    private PyExpression Intrinsic(IrIntrinsic intrinsic) {
      if (StringOperations.IsString(intrinsic.Op))
        return this.StringOperation(intrinsic);

      if (EnumOperations.IsEnum(intrinsic.Op))
        return this.EnumOperation(intrinsic);

      if ((this.ViewOperation(intrinsic) ?? this.NumberText(intrinsic) ?? this.PointerOperation(intrinsic)) is { } special)
        return special;

      var arguments = intrinsic.Arguments.Select(this.Expression).ToArray();
      var type = intrinsic.Arguments.Count > 0 ? intrinsic.Arguments[0].Type : intrinsic.Type;
      switch (intrinsic.Op) {
        case IrIntrinsicOp.FloatBits:
          // The bits of an f64 are an i64, those of an f32 an i32.
          return type == IrType.F64 ? Reinterpreted(arguments[0], "d", "q") : Reinterpreted(arguments[0], "f", "i");
        case IrIntrinsicOp.BitsFloat:
          return type == IrType.I64 ? Reinterpreted(arguments[0], "q", "d") : Reinterpreted(arguments[0], "i", "f");
        case IrIntrinsicOp.Sqrt when type.IsFloat:
          var root = this.Helper(PythonHelpers.SquareRoot, arguments[0]);
          return type == IrType.F32 ? this.Single(root) : root;
        case IrIntrinsicOp.PopCount when type.IsInteger && this.Has(PythonCapabilities.BitCount):
          var bits = type.IsSigned ? Binary(arguments[0], "&", Mask(type.Bits)) : arguments[0];
          return new PyCall(new PyAttribute(bits, "bit_count"), []);
        case IrIntrinsicOp.Min or IrIntrinsicOp.Max when type.IsInteger:
          return new PyCall(Name(intrinsic.Op == IrIntrinsicOp.Min ? "min" : "max"), arguments);
        case IrIntrinsicOp.CarryOut or IrIntrinsicOp.BorrowOut when type.IsInteger && !type.IsSigned:
          // The exact sum or difference with the carry or borrow in, compared with the range of the type.
          var carry = new PyCall(Name("int"), [arguments[2]]);
          return intrinsic.Op == IrIntrinsicOp.CarryOut
            ? Compare(Binary(Binary(arguments[0], "+", arguments[1]), "+", carry), ">", Largest(type))
            : Compare(Binary(Binary(arguments[0], "-", arguments[1]), "-", carry), "<", Integer(0));
        case IrIntrinsicOp.HighWord:
          // Python's >> of a negative int is arithmetic; the mask leaves the upper 32 bits of the two's complement.
          return Binary(Binary(arguments[0], ">>", Integer(32)), "&", Mask(32)) with { Type = IrType.U32 };
        case IrIntrinsicOp.JoinWords:
          return Wrapped(Binary(Binary(arguments[0], "<<", Integer(32)), "|", arguments[1]), IrType.I64);
        case IrIntrinsicOp.Abs when type.IsFloat:
          return new PyCall(Name("abs"), arguments);
        case IrIntrinsicOp.Min or IrIntrinsicOp.Max or IrIntrinsicOp.Sign or IrIntrinsicOp.Floor or IrIntrinsicOp.Ceiling
          or IrIntrinsicOp.Truncate or IrIntrinsicOp.RoundEven or IrIntrinsicOp.RoundAway or IrIntrinsicOp.RoundHalfUp when type.IsFloat:
          // Exact operations: the result of binary32 operands is a binary32 number.
          return this.Helper(intrinsic.Op switch {
            IrIntrinsicOp.Min => PythonHelpers.Minimum,
            IrIntrinsicOp.Max => PythonHelpers.Maximum,
            IrIntrinsicOp.Sign => PythonHelpers.Sign,
            IrIntrinsicOp.Floor => PythonHelpers.Floor,
            IrIntrinsicOp.Ceiling => PythonHelpers.Ceiling,
            IrIntrinsicOp.Truncate => PythonHelpers.Truncate,
            IrIntrinsicOp.RoundEven => PythonHelpers.RoundEven,
            IrIntrinsicOp.RoundHalfUp => PythonHelpers.RoundHalfUp,
            _ => PythonHelpers.RoundAway
          }, arguments);
        case IrIntrinsicOp.ArrayOf:
          this.Annotation(intrinsic.Type);
          return new PyList(arguments);
        case IrIntrinsicOp.ToInt32:
          return this.Helper(PythonHelpers.ToInt32, arguments[0]);
        case IrIntrinsicOp.ToUint32:
          return this.Helper(PythonHelpers.ToUint32, arguments[0]);
        default:
          return this.Inexpressible(intrinsic.Op.ToString());
      }
    }

  }

}
