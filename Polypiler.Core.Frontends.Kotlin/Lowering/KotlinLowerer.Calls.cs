using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Kotlin.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Kotlin.Lowering;

internal sealed partial class KotlinLowerer {

  /// <summary>The factories of the primitive arrays, <c>intArrayOf</c> and its siblings, by name.</summary>
  private static readonly Dictionary<string, IrType> _arrayFactories = new(StringComparer.Ordinal) {
    ["byteArrayOf"] = IrType.I8, ["shortArrayOf"] = IrType.I16, ["intArrayOf"] = IrType.I32, ["longArrayOf"] = IrType.I64,
    ["floatArrayOf"] = IrType.F32, ["doubleArrayOf"] = IrType.F64, ["booleanArrayOf"] = IrType.Bool, ["charArrayOf"] = IrType.U16
  };

  /// <summary>The exception classes of Kotlin and <c>java.lang</c> the subset throws, with the class of the IR's library of each (<c>docs/reference/exceptions.md</c>, "Kotlin").</summary>
  private static readonly Dictionary<string, IrClassType> _exceptions = new(StringComparer.Ordinal) {
    ["Exception"] = IrExceptionClass.Exception, ["RuntimeException"] = IrExceptionClass.Exception, ["Throwable"] = IrExceptionClass.Exception,
    ["ArithmeticException"] = IrExceptionClass.ArithmeticException,
    ["IllegalArgumentException"] = IrExceptionClass.ArgumentException, ["IllegalStateException"] = IrExceptionClass.InvalidOperationException,
    ["UnsupportedOperationException"] = IrExceptionClass.InvalidOperationException,
    ["IndexOutOfBoundsException"] = IrExceptionClass.IndexOutOfRangeException, ["ArrayIndexOutOfBoundsException"] = IrExceptionClass.IndexOutOfRangeException,
    ["NullPointerException"] = IrExceptionClass.NullReferenceException, ["ClassCastException"] = IrExceptionClass.InvalidCastException,
    ["NoSuchElementException"] = IrExceptionClass.KeyNotFoundException, ["NumberFormatException"] = IrExceptionClass.FormatException
  };

  /// <summary>The functions of <c>kotlin.math</c> the subset has, with the IR operation of each; every one is correctly rounded or exact.</summary>
  private static readonly Dictionary<string, IrIntrinsicOp> _math = new(StringComparer.Ordinal) {
    ["sqrt"] = IrIntrinsicOp.Sqrt, ["floor"] = IrIntrinsicOp.Floor, ["ceil"] = IrIntrinsicOp.Ceiling, ["truncate"] = IrIntrinsicOp.Truncate,
    ["round"] = IrIntrinsicOp.RoundEven
  };

  private IrExpr Call(KCall call, IrType? expected) {
    if (call.Trailing is not null && !(call.Callee is KName { Name: var array } && array.EndsWith("Array", StringComparison.Ordinal)))
      throw Refuse(KotlinFrontend.Function, call, "a call with a lambda is outside the imported subset");

    switch (call.Callee) {
      case KName name:
        return this.Function(name.Name, call, expected);
      case KMember { Receiver: KName { Name: "kotlin" or "Math" } } or KMember { Receiver: KMember { Receiver: KName { Name: "kotlin" }, Name: "math" } }:
        return this.Function(((KMember)call.Callee).Name, call, expected, mathOnly: true);
      case KMember member:
        return this.Method(member, call);
      default:
        throw Refuse(KotlinFrontend.Function, call, "the call of anything but a function or a member is outside the imported subset");
    }
  }

  /// <summary>A call of a function by its name: one of the file, an array factory, or a function of the standard library the subset has.</summary>
  private IrExpr Function(string name, KCall call, IrType? expected, bool mathOnly = false) {
    var arguments = call.Arguments;
    if (!mathOnly && this._functions.TryGetValue(name, out var overloads))
      return this.UserCall(name, overloads, call);

    if (_math.TryGetValue(name, out var op) && arguments.Count == 1) {
      var value = this.Expression(arguments[0], null);
      if (!value.Type.IsFloat)
        throw Refuse(KotlinFrontend.Scalar, call, $"{name} of a {value.Type}: kotlin.math takes Float and Double");

      return new IrIntrinsic(op, value.Type, [value]);
    }

    if (name == "abs" && arguments.Count == 1)
      return Absolute(this.Expression(arguments[0], null), call);

    if (mathOnly)
      throw Refuse(KotlinFrontend.Function, call, $"the function '{name}' of kotlin.math or java.lang.Math is outside the imported subset");

    switch (name) {
      case "minOf" or "maxOf" when arguments.Count == 2: {
        var left = this.Expression(arguments[0], expected);
        var right = this.Expression(arguments[1], left.Type);
        if (arguments[0] is KInteger)
          left = this.Coerce(left, right.Type, arguments[0]);

        right = this.Coerce(right, left.Type, arguments[1]);
        if (!left.Type.IsNumeric)
          throw Refuse(KotlinFrontend.Scalar, call, $"{name} of a {left.Type}");

        return Extreme(name == "minOf" ? IrIntrinsicOp.Min : IrIntrinsicOp.Max, left, right);
      }
      case "Char" when arguments.Count == 1:
        return this.Char(new IrConvert(IrType.U16, this.Coerce(this.Expression(arguments[0], IrType.I32), IrType.I32, arguments[0])));
      case "require" or "check" when arguments.Count == 1:
        throw Refuse(KotlinFrontend.Function, call, $"{name} is a statement, not a value");
      case "error" when arguments.Count == 1:
        return new IrThrowExpression(expected ?? IrType.Void, this.NewException(IrExceptionClass.InvalidOperationException, this.Text(this.Expression(arguments[0], null), arguments[0])));
    }

    if (_exceptions.TryGetValue(name, out var exception) && arguments.Count <= 1)
      return arguments.Count == 0
        ? new IrNew(IrType.Object(exception), IrExceptionClass.Constructor(exception, 0), [])
        : this.NewException(exception, this.Text(this.Expression(arguments[0], IrType.String), arguments[0]));

    if (_arrayFactories.TryGetValue(name, out var element)) {
      var values = arguments.Select(argument => this.Coerce(this.Expression(argument, element), element, argument)).ToList();
      return new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(element), values);
    }

    if (name.EndsWith("Array", StringComparison.Ordinal) && Scalar(name[..^5]) is { IsNumeric: true } or { Kind: IrTypeKind.Bool } && arguments.Count == 1) {
      var arrayType = this.TypeOf(new KTypeRef(name, [], false) { At = call.At });
      var length = this.Coerce(this.Expression(arguments[0], IrType.I32), IrType.I32, arguments[0]);
      return call.Trailing is { } initializer ? this.Filled(arrayType, length, initializer) : new IrNewArray(arrayType, length);
    }

    throw Refuse(KotlinFrontend.Function, call, $"the function '{name}' is not declared");
  }

  /// <summary>
  /// <c>IntArray(n) { i -&gt; value }</c>: a new array, then a loop that stores every element's value
  /// in index order, before the statement the array is used in; the array is a temporary.
  /// </summary>
  private IrExpr Filled(IrType arrayType, IrExpr length, KLambda initializer) {
    if (initializer.Parameters.Count > 1)
      throw Refuse(KotlinFrontend.Function, initializer, "the initializer of an array takes one parameter, the index");

    var array = this.NewLocal("array", arrayType);
    this._pending.Add(new IrDeclare(array, new IrNewArray(arrayType, length)));
    var index = this.NewLocal("index", IrType.I32);
    this._pending.Add(new IrDeclare(index, new IrConst(IrType.I32, 0)));
    var statements = this.Scoped(() => {
      var parameter = this.NewLocal(initializer.Parameters.Count == 1 ? initializer.Parameters[0].Name : "it", IrType.I32);
      this.Scope.Add(parameter.Name == "it" ? "it" : initializer.Parameters[0].Name, new Variable(parameter, false));
      var body = new List<IrStmt> { new IrDeclare(parameter, new IrLocalRef(index)) };
      var saved = this._pending.ToList();
      this._pending.Clear();
      foreach (var statement in initializer.Body.Take(initializer.Body.Count - 1))
        this.Statement(statement, body);

      if (initializer.Body.LastOrDefault() is not KExpressionStatement { Expression: var last })
        throw Refuse(KotlinFrontend.Function, initializer, "the initializer of an array ends with the element's value");

      var value = this.Coerce(this.Expression(last, arrayType.Element), arrayType.Element!, last);
      this.Flush(body);
      body.Add(new IrStore(new IrLocalRef(array), new IrLocalRef(index), value));
      this._pending.AddRange(saved);
      return body;
    });
    this._pending.Add(new IrFor(new IrBinary(IrBinaryOp.Less, new IrLocalRef(index), new IrArrayLength(new IrLocalRef(array))), new IrBlock(statements),
      new IrBlock([new IrAssign(index, new IrBinary(IrBinaryOp.Add, new IrLocalRef(index), new IrConst(IrType.I32, 1)))])));
    return new IrLocalRef(array);
  }

  /// <summary>
  /// A call of a function of the file: the overload whose parameters take the arguments, the missing
  /// trailing ones filled with their defaults; the arguments are evaluated in order.
  /// </summary>
  private IrExpr UserCall(string name, List<Signature> overloads, KCall call) {
    var candidates = overloads.Where(candidate => candidate.Parameters.Count >= call.Arguments.Count).ToList();
    // One candidate is the call: what does not fit it is refused for what it is.
    if (candidates is [var only] && this.Arguments(only, call, strict: true) is { } fitting)
      return new IrCall(only.IrName, only.Result, fitting);

    foreach (var signature in candidates)
      if (this.Probe(() => this.Arguments(signature, call)) is not null)
        return new IrCall(signature.IrName, signature.Result, this.Arguments(signature, call)!);

    throw Refuse(KotlinFrontend.Function, call, $"no function '{name}' takes these {call.Arguments.Count} arguments");
  }

  /// <summary>The arguments of a call of a signature, or <see langword="null"/> where they do not fit it.</summary>
  private List<IrExpr>? Arguments(Signature signature, KCall call, bool strict = false) {
    var arguments = new List<IrExpr>();
    for (var index = 0; index < signature.Parameters.Count; ++index) {
      var argument = index < call.Arguments.Count ? call.Arguments[index] : signature.Defaults[index];
      if (argument is null)
        return null;

      try {
        arguments.Add(this.Coerce(this.Expression(argument, signature.Parameters[index]), signature.Parameters[index], argument));
      } catch (KotlinRefusal) when (!strict) {
        return null;
      }
    }

    return arguments;
  }

  /// <summary>
  /// The smaller or larger of two values of one type: of 8 or 16 bits computed on 32, which the IR's
  /// operation takes, and converted back, which changes nothing.
  /// </summary>
  private static IrExpr Extreme(IrIntrinsicOp op, IrExpr left, IrExpr right) {
    var type = left.Type;
    if (type.IsFloat || type.IsWideInteger)
      return new IrIntrinsic(op, type, [left, right]);

    var wide = type.IsSigned ? IrType.I32 : IrType.U32;
    return new IrConvert(type, new IrIntrinsic(op, wide, [Widen(left, wide), Widen(right, wide)]));
  }

  /// <summary>A new exception of a class of the IR's library with a message.</summary>
  private IrExpr NewException(IrClassType type, string message) => this.NewException(type, new IrStrConst(message));

  private IrExpr NewException(IrClassType type, IrExpr message)
    => new IrNew(IrType.Object(type), IrExceptionClass.Constructor(type, 1), [message]);

  #region Members

  /// <summary>The conversions of the number types, <c>toInt()</c> and its siblings, by name.</summary>
  private static readonly Dictionary<string, IrType> _conversions = new(StringComparer.Ordinal) {
    ["toByte"] = IrType.I8, ["toShort"] = IrType.I16, ["toInt"] = IrType.I32, ["toLong"] = IrType.I64,
    ["toUByte"] = IrType.U8, ["toUShort"] = IrType.U16, ["toUInt"] = IrType.U32, ["toULong"] = IrType.U64,
    ["toFloat"] = IrType.F32, ["toDouble"] = IrType.F64, ["toChar"] = IrType.U16
  };

  /// <summary>A call of a member: a conversion, a bit function, a function of a string or an array.</summary>
  private IrExpr Method(KMember member, KCall call) {
    if (member.Safe)
      throw Refuse(KotlinFrontend.Type, member, "a safe call is outside the imported subset: it has no nullable types");

    var arguments = call.Arguments;
    if (member.Receiver is KName { Name: "Double" or "Float" } owner && this.Scope.Find(owner.Name) is null && member.Name == "fromBits" && arguments.Count == 1) {
      var floating = owner.Name == "Double" ? IrType.F64 : IrType.F32;
      var integer = floating == IrType.F64 ? IrType.I64 : IrType.I32;
      return new IrIntrinsic(IrIntrinsicOp.BitsFloat, floating, [this.Coerce(this.Expression(arguments[0], integer), integer, arguments[0])]);
    }

    var receiver = this.Expression(member.Receiver, null);
    var type = receiver.Type;
    if (_conversions.TryGetValue(member.Name, out var target) && arguments.Count == 0)
      return this.Conversion(receiver, target, member, member.Name == "toChar");

    if (type.IsInteger && !this.IsChar(receiver))
      return this.IntegerMethod(member.Name, receiver, arguments, call);

    if (type.IsFloat)
      return member.Name switch {
        "isNaN" when arguments.Count == 0 => new IrIntrinsic(IrIntrinsicOp.IsNaN, IrType.Bool, [receiver]),
        "isInfinite" when arguments.Count == 0 => new IrIntrinsic(IrIntrinsicOp.IsInfinity, IrType.Bool, [receiver]),
        "isFinite" when arguments.Count == 0 => new IrIntrinsic(IrIntrinsicOp.IsFinite, IrType.Bool, [receiver]),
        "toRawBits" when arguments.Count == 0 => new IrIntrinsic(IrIntrinsicOp.FloatBits, type == IrType.F64 ? IrType.I64 : IrType.I32, [receiver]),
        _ => throw Refuse(KotlinFrontend.Scalar, call, $"the member '{member.Name}' of a {type} is outside the imported subset")
      };

    if (type.IsString)
      return this.StringMethod(member.Name, receiver, arguments, call);

    if (type.IsArray)
      return member.Name switch {
        "fill" when arguments.Count == 1 => new IrIntrinsic(IrIntrinsicOp.ArrayFill, IrType.Void, [receiver, this.Coerce(this.Expression(arguments[0], type.Element), type.Element!, arguments[0])]),
        _ => throw Refuse(KotlinFrontend.Memory, call, $"the member '{member.Name}' of a {type} is outside the imported subset")
      };

    throw Refuse(KotlinFrontend.Function, call, $"the member '{member.Name}' of a {type} is outside the imported subset");
  }

  /// <summary>
  /// A conversion with Kotlin's meaning: between integers the value modulo 2^width of the target, from
  /// a floating-point number to <c>Int</c>, <c>Long</c>, <c>UInt</c> and <c>ULong</c> truncated and
  /// saturated with a NaN as 0, to <c>Byte</c> and <c>Short</c> through <c>Int</c>, which wraps after
  /// it saturated; a <c>Char</c> is its code unit.
  /// </summary>
  private IrExpr Conversion(IrExpr value, IrType target, KNode at, bool isChar) {
    IrExpr converted;
    if (value.Type.IsFloat && target.IsInteger) {
      converted = target.Bits >= 32
        ? new IrConvert(target, value, IrConversion.Saturate)
        : new IrConvert(target, new IrConvert(target.IsSigned ? IrType.I32 : IrType.U32, value, IrConversion.Saturate));
    } else if (value.Type == target) {
      converted = value;
    } else if (value.Type.IsNumeric && target.IsNumeric) {
      converted = value is IrConst constant && target.IsInteger && constant.Type.IsInteger
        ? new IrConst(target, IrOperators.Wrap(target, constant.Value))
        : new IrConvert(target, value);
    } else {
      throw Refuse(KotlinFrontend.Scalar, at, $"the conversion of a {value.Type} to {target}");
    }

    return isChar ? this.Char(converted) : converted;
  }

  /// <summary>The bit functions of the integer types, which Kotlin has as members: <c>inv</c>, the counts, the rotations, the named forms of the shifts and logic.</summary>
  private IrExpr IntegerMethod(string name, IrExpr receiver, IReadOnlyList<KExpression> arguments, KCall call) {
    var type = receiver.Type;
    IrExpr Argument(IrType argumentType) => this.Coerce(this.Expression(arguments[0], argumentType), argumentType, arguments[0]);
    switch (name) {
      case "inv" when arguments.Count == 0 && type.IsWideInteger:
        return new IrUnary(IrUnaryOp.BitNot, receiver);
      case "inv" when arguments.Count == 0 && !type.IsSigned:
        return new IrConvert(type, new IrUnary(IrUnaryOp.BitNot, Widen(receiver, IrType.U32)));
      case "countOneBits" when arguments.Count == 0 && type.IsWideInteger:
        return new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [receiver]);
      case "countLeadingZeroBits" when arguments.Count == 0 && type.IsWideInteger:
        return new IrIntrinsic(IrIntrinsicOp.LeadingZeroCount, IrType.I32, [receiver]);
      case "countTrailingZeroBits" when arguments.Count == 0 && type.IsWideInteger:
        return new IrIntrinsic(IrIntrinsicOp.TrailingZeroCount, IrType.I32, [receiver]);
      case "rotateLeft" or "rotateRight" when arguments.Count == 1 && type.IsWideInteger:
        return new IrIntrinsic(name == "rotateLeft" ? IrIntrinsicOp.RotateLeft : IrIntrinsicOp.RotateRight, type, [receiver, Argument(IrType.I32)]);
      case "shl" or "shr" or "ushr" or "and" or "or" or "xor" when arguments.Count == 1:
        return this.Operator(receiver, name, this.Expression(arguments[0], name.StartsWith("sh", StringComparison.Ordinal) || name == "ushr" ? IrType.I32 : type), call);
      case "coerceIn" when arguments.Count == 2: {
        var low = Argument(type);
        var high = this.Coerce(this.Expression(arguments[1], type), type, arguments[1]);
        return Extreme(IrIntrinsicOp.Max, Extreme(IrIntrinsicOp.Min, receiver, high), low);
      }
      case "coerceAtLeast" when arguments.Count == 1:
        return Extreme(IrIntrinsicOp.Max, receiver, Argument(type));
      case "coerceAtMost" when arguments.Count == 1:
        return Extreme(IrIntrinsicOp.Min, receiver, Argument(type));
      default:
        throw Refuse(KotlinFrontend.Scalar, call, $"the member '{name}' of a {type} is outside the imported subset");
    }
  }

  /// <summary>The functions of a string the subset has.</summary>
  private IrExpr StringMethod(string name, IrExpr receiver, IReadOnlyList<KExpression> arguments, KCall call) {
    IrExpr Text(int index) => this.Expression(arguments[index], IrType.String) is { Type.IsString: true } text ? text
      : throw Refuse(KotlinFrontend.Memory, arguments[index], $"{name} of a string takes a string here");
    switch (name) {
      case "startsWith" or "endsWith" or "contains" when arguments.Count == 1:
        return new IrIntrinsic(name switch { "startsWith" => IrIntrinsicOp.StrStartsWith, "endsWith" => IrIntrinsicOp.StrEndsWith, _ => IrIntrinsicOp.StrContains }, IrType.Bool, [receiver, Text(0)]);
      case "equals" when arguments.Count == 1:
        return new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [receiver, Text(0)]);
      case "compareTo" when arguments.Count == 1:
        return new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [receiver, Text(0)]);
      case "substring" when arguments.Count is 1 or 2: {
        var text = this.Once(receiver, "text");
        var start = this.Once(this.Coerce(this.Expression(arguments[0], IrType.I32), IrType.I32, arguments[0]), "start");
        IrExpr end = arguments.Count == 2
          ? this.Coerce(this.Expression(arguments[1], IrType.I32), IrType.I32, arguments[1])
          : new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [text]);
        return new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [text, start, new IrBinary(IrBinaryOp.Subtract, end, start)]);
      }
      default:
        throw Refuse(KotlinFrontend.Memory, call, $"the member '{name}' of a String is outside the imported subset");
    }
  }

  /// <summary><c>array[index]</c>, or the <c>Char</c> of a string at an index.</summary>
  private IrExpr Index(KIndex index) {
    var receiver = this.Expression(index.Receiver, null);
    var position = this.Coerce(this.Expression(index.Index, IrType.I32), IrType.I32, index.Index);
    if (receiver.Type.IsString)
      return this.Char(new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [receiver, position]));

    if (!receiver.Type.IsArray)
      throw Refuse(KotlinFrontend.Memory, index, $"the index of a {receiver.Type}");

    var load = new IrArrayLoad(receiver, position);
    return receiver is IrLocalRef or IrConstRef && this.IsCharArray(receiver) ? this.Char(load) : load;
  }

  /// <summary>Whether an array holds characters: a <c>CharArray</c>, which the IR holds as <c>u16[]</c>.</summary>
  private bool IsCharArray(IrExpr array) => array.Type.Element == IrType.U16;

  #endregion

}
