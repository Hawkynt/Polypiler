using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Polypiler.Core.IR;

/// <summary>The ways an IR operation can fail at run time.</summary>
public enum IrTrap {

  /// <summary>A division or remainder with a zero divisor.</summary>
  DivideByZero,

  /// <summary>An array access outside the array.</summary>
  IndexOutOfRange,

  /// <summary>
  /// A result that is not a value of its integer type: the smallest signed value divided by -1 (also
  /// as a remainder), a checked operation or conversion that overflows, or an array created with a
  /// negative length.
  /// </summary>
  IntegerOverflow,

  /// <summary>An operation that has no result for its operand: the sign of a NaN.</summary>
  InvalidOperation,

  /// <summary>A range outside its source: a view or a slice that does not lie in it, or a copy into a view too short.</summary>
  ArgumentOutOfRange

}

/// <summary>Thrown by <see cref="IrInterpreter"/> when an operation traps.</summary>
public sealed class IrTrapException : Exception {

  /// <summary>Creates the exception for a trap.</summary>
  /// <param name="trap">What went wrong.</param>
  public IrTrapException(IrTrap trap) : base(Describe(trap)) => this.Trap = trap;

  /// <summary>What went wrong.</summary>
  public IrTrap Trap { get; }

  /// <summary>The message every implementation of the IR reports for a trap.</summary>
  /// <param name="trap">The trap.</param>
  /// <returns>The message, for example <c>divide by zero</c>.</returns>
  public static string Describe(IrTrap trap) => trap switch {
    IrTrap.DivideByZero => "divide by zero",
    IrTrap.IndexOutOfRange => "index out of range",
    IrTrap.InvalidOperation => "invalid operation",
    IrTrap.ArgumentOutOfRange => "argument out of range",
    _ => "integer overflow"
  };

}

/// <summary>
/// Evaluates the IR directly. Values cross the boundary as the .NET type of the same width and
/// signedness (a <c>u16</c> is accepted as <see cref="ushort"/> or <see cref="char"/> and returned as
/// <see cref="ushort"/>), as <see cref="float"/>, <see cref="double"/> and <see cref="bool"/>, and as
/// one-dimensional arrays of the numeric types;
/// arrays are passed by reference, so a function can change the caller's array. A struct crosses as
/// an <see cref="object"/> array of the values of its fields in declaration order, each crossing as
/// its own type does; inside, it is a value that every read copies, as <see cref="IrStructNew"/> states.
/// </summary>
public static partial class IrInterpreter {

  /// <summary>
  /// The functions of a module by name and its tables as arrays, kept for as long as the module lives.
  /// A table can be shared between calls because the IR cannot write to it.
  /// </summary>
  private static readonly ConditionalWeakTable<IrModule, Program> _programs = new();

  /// <summary>
  /// Calls a function of a module that <see cref="IrVerifier"/> accepted, in an instance of the module
  /// of its own: the globals are initialised for this call and forgotten after it.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <param name="function">The name of the function to call.</param>
  /// <param name="arguments">One value per parameter, of the parameter's type.</param>
  /// <returns>The value returned, or <see langword="null"/> for a function that returns nothing.</returns>
  /// <exception cref="ArgumentException">The function does not exist or the arguments do not fit it.</exception>
  /// <exception cref="IrTrapException">An operation trapped, also one of an initializer.</exception>
  public static object? Call(IrModule module, string function, object[] arguments)
    // A module without globals has no state to keep apart, and the many calls of the equivalence tests need no instance each.
    => module.Globals.Count == 0 ? Call(_programs.GetValue(module, Index), [], module, function, arguments) : Instantiate(module).Call(function, arguments);


  /// <summary>A function of the module called with values taken across the boundary, in the globals of an instance.</summary>
  private static object? Call(Program program, Dictionary<IrGlobal, object> globals, IrModule module, string function, object[] arguments) {
    var functions = program.Functions;
    if (!functions.TryGetValue(function, out var target))
      throw new ArgumentException($"The module '{module.Name}' has no function '{function}'.", nameof(function));

    if (arguments.Length != target.Parameters.Count)
      throw new ArgumentException($"'{function}' takes {target.Parameters.Count} arguments, not {arguments.Length}.", nameof(arguments));

    var values = arguments.Select((argument, index) => FromHost(target.Parameters[index], argument)).ToArray();
    return ToHost(target.ReturnType, new Machine(program, globals).Invoke(target, values));
  }

  private static Program Index(IrModule module) {
    var functions = new Dictionary<string, IrFunction>();
    foreach (var candidate in module.Functions)
      functions[candidate.Name] = candidate;

    var constants = new Dictionary<IrModuleConst, object>(ReferenceEqualityComparer.Instance);
    foreach (var constant in module.Constants)
      constants[constant] = constant.Type.Element is { } element
        ? ArrayOf(element, constant.Values.Select(value => Constant(element, value)).ToList())
        : Constant(constant.Type, constant.Values[0]);

    return new Program(functions, constants);
  }

  /// <summary>A constant as the interpreter carries a value of its type.</summary>
  private static object Constant(IrType type, long value) => type.Kind switch {
    IrTypeKind.Bool => value != 0,
    IrTypeKind.F32 => (float)IrOperators.Float(type, value),
    IrTypeKind.F64 => IrOperators.Float(type, value),
    _ => value
  };

  private static Array ArrayOf(IrType element, IReadOnlyList<object> values) {
    var array = Array.CreateInstance(HostType(element), values.Count);
    for (var index = 0; index < values.Count; ++index)
      array.SetValue(External(array.GetType().GetElementType()!, values[index]), index);

    return array;
  }

  /// <summary>
  /// Takes an argument across the boundary. The type is compared exactly: to a pattern such as
  /// <c>is int[]</c> a <c>uint[]</c> would pass as well, and its elements would be read as signed.
  /// </summary>
  private static object FromHost(IrLocal parameter, object argument)
    => Accepted(parameter.Type, argument) ?? throw new ArgumentException($"The argument for '{parameter.Name}' is not a value of {parameter.Type}.", nameof(argument));

  /// <summary>A value of a type taken across the boundary, or <see langword="null"/> when it is none.</summary>
  private static object? Accepted(IrType type, object? value) {
    if (type.IsView)
      return AcceptedView(type, value);

    if (type.StructType is { } declared)
      return value is object?[] fields && fields.Length == declared.Fields.Count && Fields(declared, fields) is { } accepted ? new Record(accepted) : null;

    return value is not null && TypeOf(value.GetType()) == type ? Internal(value) : null;
  }

  /// <summary>The fields of a struct taken across the boundary, or <see langword="null"/> when one is not a value of its field.</summary>
  private static object[]? Fields(IrStructType type, object?[] values) {
    var fields = new object[values.Length];
    for (var index = 0; index < values.Length; ++index)
      if (Accepted(type.Fields[index].Type, values[index]) is { } field)
        fields[index] = field;
      else
        return null;

    return fields;
  }

  /// <summary>The IR type a .NET type crosses the boundary as, or <see langword="null"/> when it does not.</summary>
  private static IrType? TypeOf(Type host) {
    if (host.IsSZArray)
      return TypeOf(host.GetElementType()!) is { IsNumeric: true } element ? IrType.Array(element) : null;

    return Type.GetTypeCode(host) switch {
      TypeCode.Boolean => IrType.Bool,
      TypeCode.SByte => IrType.I8,
      TypeCode.Byte => IrType.U8,
      TypeCode.Int16 => IrType.I16,
      TypeCode.UInt16 or TypeCode.Char => IrType.U16,
      TypeCode.Int32 => IrType.I32,
      TypeCode.UInt32 => IrType.U32,
      TypeCode.Int64 => IrType.I64,
      TypeCode.UInt64 => IrType.U64,
      TypeCode.Single => IrType.F32,
      TypeCode.Double => IrType.F64,
      _ => null
    };
  }

  private static Type HostType(IrType type) => type.Kind switch {
    IrTypeKind.I8 => typeof(sbyte),
    IrTypeKind.U8 => typeof(byte),
    IrTypeKind.I16 => typeof(short),
    IrTypeKind.U16 => typeof(ushort),
    IrTypeKind.I32 => typeof(int),
    IrTypeKind.U32 => typeof(uint),
    IrTypeKind.I64 => typeof(long),
    IrTypeKind.U64 => typeof(ulong),
    IrTypeKind.F32 => typeof(float),
    IrTypeKind.F64 => typeof(double),
    _ => typeof(object)
  };

  /// <summary>A .NET value as the interpreter carries it: every integer as a <see cref="long"/>.</summary>
  private static object Internal(object value) => value switch {
    Array or bool or float or double or Record => value,
    ulong large => (long)large,
    char unit => (long)unit,
    _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
  };

  /// <summary>A carried number as a .NET value of the given type.</summary>
  private static object External(Type host, object value) => Type.GetTypeCode(host) switch {
    TypeCode.SByte => (sbyte)(long)value,
    TypeCode.Byte => (byte)(long)value,
    TypeCode.Int16 => (short)(long)value,
    TypeCode.UInt16 => (ushort)(long)value,
    TypeCode.Char => (char)(long)value,
    TypeCode.Int32 => (int)(long)value,
    TypeCode.UInt32 => (uint)(long)value,
    TypeCode.UInt64 => (ulong)(long)value,
    _ => value
  };

  private static object? ToHost(IrType type, object? value) => value switch {
    View view => ToArray(view),
    Record record => record.Fields.Select((field, index) => ToHost(type.StructType!.Fields[index].Type, field)).ToArray(),
    null or Array or bool => value,
    _ => External(HostType(type), value)
  };

  private sealed record Program(Dictionary<string, IrFunction> Functions, Dictionary<IrModuleConst, object> Constants);

  private sealed partial class Machine(Program program, Dictionary<IrGlobal, object> globals) {

    public object? Invoke(IrFunction function, object[] arguments) {
      var frame = new Frame();
      for (var index = 0; index < arguments.Length; ++index)
        frame.Locals[function.Parameters[index]] = arguments[index];

      if (this.Run(function.Body, frame) != Flow.Return && function.ReturnType != IrType.Void)
        throw new InvalidOperationException($"'{function.Name}' ended without returning a value.");

      return frame.Result;
    }

    /// <summary>Executes a statement and says how it ended.</summary>
    private Flow Run(IrStmt statement, Frame frame) {
      switch (statement) {
        case IrBlock block:
          foreach (var inner in block.Statements) {
            var ended = this.Run(inner, frame);
            if (ended != Flow.Next)
              return ended;
          }

          return Flow.Next;
        case IrDeclare declare:
          if (declare.Initializer is not null)
            frame.Locals[declare.Local] = this.Evaluate(declare.Initializer, frame);

          return Flow.Next;
        case IrAssign assign:
          frame.Locals[assign.Local] = this.Evaluate(assign.Value, frame);
          return Flow.Next;
        case IrGlobalAssign assign:
          globals[assign.Global] = this.Evaluate(assign.Value, frame);
          return Flow.Next;
        case IrStore store:
          var array = this.Evaluate(store.Array, frame);
          var index = (long)this.Evaluate(store.Index, frame);
          var value = this.Evaluate(store.Value, frame);
          StoreElement(array, index, value);
          return Flow.Next;
        case IrExprStmt expression:
          this.Evaluate(expression.Expression, frame);
          return Flow.Next;
        case IrFieldStore store:
          var place = (Record)this.Place(store.Target, frame);
          place.Fields[Position(store.Field, store.Target.Type)] = this.Evaluate(store.Value, frame);
          return Flow.Next;
        case IrIf conditional:
          if ((bool)this.Evaluate(conditional.Condition, frame))
            return this.Run(conditional.Then, frame);

          return conditional.Else is null ? Flow.Next : this.Run(conditional.Else, frame);
        case IrWhile loop:
          while ((bool)this.Evaluate(loop.Condition, frame)) {
            var pass = this.Run(loop.Body, frame);
            if (pass is Flow.Break or Flow.Return)
              return pass & Flow.Return;
          }

          return Flow.Next;
        case IrDoWhile loop:
          do {
            var pass = this.Run(loop.Body, frame);
            if (pass is Flow.Break or Flow.Return)
              return pass & Flow.Return;
          } while ((bool)this.Evaluate(loop.Condition, frame));

          return Flow.Next;
        case IrFor loop:
          while ((bool)this.Evaluate(loop.Condition, frame)) {
            var pass = this.Run(loop.Body, frame);
            if (pass is Flow.Break or Flow.Return)
              return pass & Flow.Return;

            this.Run(loop.Step, frame);
          }

          return Flow.Next;
        case IrSwitch choice:
          var selector = (long)this.Evaluate(choice.Value, frame);
          var section = choice.Sections.FirstOrDefault(candidate => candidate.Labels.Contains(selector))
            ?? choice.Sections.FirstOrDefault(candidate => candidate.IsDefault);
          if (section is null)
            return Flow.Next;

          var chosen = this.Run(section.Body, frame);
          return chosen == Flow.Break ? Flow.Next : chosen;
        case IrBreak:
          return Flow.Break;
        case IrContinue:
          return Flow.Continue;
        case IrReturn result:
          frame.Result = result.Value is null ? null : this.Evaluate(result.Value, frame);
          return Flow.Return;
        default:
          throw new NotSupportedException($"Unknown statement {statement.GetType().Name}.");
      }
    }

    private object Evaluate(IrExpr expression, Frame frame) {
      switch (expression) {
        case IrConst constant:
          return Constant(constant.Type, constant.Value);
        case IrConstRef reference:
          return program.Constants[reference.Constant];
        case IrIntrinsic intrinsic:
          return this.Intrinsic(intrinsic, frame)!;
        case IrLocalRef reference:
          return Copied(frame.Locals[reference.Local]);
        case IrGlobalRef reference:
          return Copied(globals[reference.Global]);
        case IrUnary unary:
          var operand = this.Evaluate(unary.Operand, frame);
          if (unary.Type.IsFloat)
            return operand is float single ? (object)-single : -(double)operand;

          return unary.Op switch {
            IrUnaryOp.Negate => IrOperators.Wrap(unary.Type, -(long)operand),
            IrUnaryOp.NegateChecked => Fit(unary.Type, -IrOperators.Number(unary.Type, (long)operand)),
            IrUnaryOp.BitNot => IrOperators.Wrap(unary.Type, ~(long)operand),
            _ => !(bool)operand
          };
        case IrBinary binary:
          return this.Binary(binary, frame);
        case IrConvert conversion:
          return Convert(conversion, this.Evaluate(conversion.Operand, frame));
        case IrConditional choice:
          return this.Evaluate((bool)this.Evaluate(choice.Condition, frame) ? choice.WhenTrue : choice.WhenFalse, frame);
        case IrArrayLoad load:
          var array = this.Evaluate(load.Array, frame);
          return Copied(LoadElement(array, (long)this.Evaluate(load.Index, frame)));
        case IrArrayLength length:
          return (long)((Array)this.Evaluate(length.Array, frame)).Length;
        case IrNewArray creation:
          var count = (long)this.Evaluate(creation.Length, frame);
          if (count < 0)
            throw new IrTrapException(IrTrap.IntegerOverflow);

          var created = Array.CreateInstance(HostType(creation.Type.Element!), count);
          if (creation.Type.Element!.StructType is { } element)
            for (var index = 0; index < count; ++index)
              created.SetValue(Default(element), index);

          return created;
        case IrStructNew creation:
          return new Record([.. creation.Arguments.Select(argument => this.Evaluate(argument, frame))]);
        case IrStructDefault value:
          return Default(value.Type.StructType!);
        case IrFieldLoad load:
          return Copied(((Record)this.Place(load.Target, frame)).Fields[Position(load.Field, load.Target.Type)]);
        case IrStructEquals equality:
          var left = this.Evaluate(equality.Left, frame);
          return Same(left, this.Evaluate(equality.Right, frame));
        case IrCall call:
          var arguments = call.Arguments.Select(argument => this.Evaluate(argument, frame)).ToArray();
          return this.Invoke(program.Functions[call.Function], arguments)!;
        case IrStep step:
          return this.Step(step, frame);
        default:
          throw new NotSupportedException($"Unknown expression {expression.GetType().Name}.");
      }
    }

    private object? Intrinsic(IrIntrinsic intrinsic, Frame frame) {
      var arguments = intrinsic.Arguments.Select(argument => this.Evaluate(argument, frame)).ToArray();
      if (IsView(intrinsic))
        return Views(intrinsic, arguments);

      switch (intrinsic.Op) {
        case IrIntrinsicOp.ArrayOf:
          return ArrayOf(intrinsic.Type.Element!, arguments);
        case IrIntrinsicOp.ArrayCopy:
          Copy((Array)arguments[0], (long)arguments[1], (Array)arguments[2], (long)arguments[3], (long)arguments[4]);
          return null;
        case IrIntrinsicOp.ArrayFill:
          var filled = (Array)arguments[0];
          for (var index = 0; index < filled.Length; ++index)
            Store(filled, index, arguments[1]);

          return null;
        case IrIntrinsicOp.ArrayClear:
          var cleared = (Array)arguments[0];
          Array.Clear(cleared);
          return null;
        default:
          if (IntegerOperations.Evaluates(intrinsic))
            return IntegerOperations.Evaluate(intrinsic, arguments);
          if (FloatOperationTable.TryEvaluate(intrinsic.Op, arguments, out var computed))
            return computed;

          return arguments[0] switch {
            float single => Single(intrinsic.Op, single, arguments),
            double number => Double(intrinsic.Op, number, arguments),
            _ => BitsFloat(intrinsic, (long)arguments[0])
          };
      }
    }

    private static object Single(IrIntrinsicOp op, float value, object[] arguments) => op switch {
      // The arms are of different types; without the first cast the compiler would convert them all to one.
      IrIntrinsicOp.Abs => (object)MathF.Abs(value),
      IrIntrinsicOp.Min => MathF.Min(value, (float)arguments[1]),
      IrIntrinsicOp.Max => MathF.Max(value, (float)arguments[1]),
      IrIntrinsicOp.Sign => Sign(value),
      IrIntrinsicOp.Sqrt => MathF.Sqrt(value),
      IrIntrinsicOp.Floor => MathF.Floor(value),
      IrIntrinsicOp.Ceiling => MathF.Ceiling(value),
      IrIntrinsicOp.Truncate => MathF.Truncate(value),
      IrIntrinsicOp.RoundEven => MathF.Round(value),
      IrIntrinsicOp.RoundAway => MathF.Round(value, MidpointRounding.AwayFromZero),
      IrIntrinsicOp.RoundHalfUp => (float)IrOperators.RoundHalfUp(value),
      _ => (long)BitConverter.SingleToInt32Bits(value)
    };

    private static object Double(IrIntrinsicOp op, double value, object[] arguments) => op switch {
      IrIntrinsicOp.Abs => (object)Math.Abs(value),
      IrIntrinsicOp.Min => Math.Min(value, (double)arguments[1]),
      IrIntrinsicOp.Max => Math.Max(value, (double)arguments[1]),
      IrIntrinsicOp.Sign => Sign(value),
      IrIntrinsicOp.Sqrt => Math.Sqrt(value),
      IrIntrinsicOp.Floor => Math.Floor(value),
      IrIntrinsicOp.Ceiling => Math.Ceiling(value),
      IrIntrinsicOp.Truncate => Math.Truncate(value),
      IrIntrinsicOp.RoundEven => Math.Round(value),
      IrIntrinsicOp.RoundAway => Math.Round(value, MidpointRounding.AwayFromZero),
      IrIntrinsicOp.RoundHalfUp => IrOperators.RoundHalfUp(value),
      IrIntrinsicOp.ToInt32 => IrOperators.ToWord(IrType.I32, value),
      IrIntrinsicOp.ToUint32 => IrOperators.ToWord(IrType.U32, value),
      _ => BitConverter.DoubleToInt64Bits(value)
    };

    /// <summary>The sign of a floating-point number as the IR carries an <c>i32</c>; a NaN has none.</summary>
    private static long Sign(double value)
      => double.IsNaN(value) ? throw new IrTrapException(IrTrap.InvalidOperation) : Math.Sign(value);

    /// <summary>The floating-point number with the bits of an integer: the one operation on an integer that is no integer operation.</summary>
    private static object BitsFloat(IrIntrinsic intrinsic, long value)
      // The arms are of different types; without the cast the compiler would make the float a double.
      => intrinsic.Arguments[0].Type.IsLong ? BitConverter.Int64BitsToDouble(value) : (object)BitConverter.Int32BitsToSingle((int)value);

    /// <summary>
    /// Copies element by element, from the end when the target lies behind the source in the same
    /// array, which is the order that reads every element before it is overwritten.
    /// </summary>
    private static void Copy(Array source, long from, Array target, long to, long length) {
      if (from < 0 || to < 0 || length < 0 || from + length > source.Length || to + length > target.Length)
        throw new IrTrapException(IrTrap.IndexOutOfRange);

      var backwards = ReferenceEquals(source, target) && from < to;
      for (var step = 0L; step < length; ++step) {
        var offset = (int)(backwards ? length - 1 - step : step);
        Store(target, (int)to + offset, Load(source, (int)from + offset));
      }
    }

    private object Step(IrStep step, Frame frame) {
      object before;
      object after;
      if (step.Target is IrArrayLoad element) {
        var array = this.Evaluate(element.Array, frame);
        var index = (long)this.Evaluate(element.Index, frame);
        before = LoadElement(array, index);
        after = Stepped(step, before);
        StoreElement(array, index, after);
      } else if (step.Target is IrGlobalRef reference) {
        before = globals[reference.Global];
        after = Stepped(step, before);
        globals[reference.Global] = after;
      } else {
        var local = ((IrLocalRef)step.Target).Local;
        before = frame.Locals[local];
        after = Stepped(step, before);
        frame.Locals[local] = after;
      }

      return step.Postfix ? before : after;
    }

    private static object Stepped(IrStep step, object before) {
      var one = step.Increment ? 1 : -1;
      switch (before) {
        case float single:
          return single + one;
        case double number:
          return number + one;
      }

      var result = IrOperators.Number(step.Type, (long)before) + one;
      return step.Trap ? Fit(step.Type, result) : IrOperators.Wrap(step.Type, (long)result);
    }

    private object Binary(IrBinary binary, Frame frame) {
      var first = this.Evaluate(binary.Left, frame);
      switch (binary.Op) {
        case IrBinaryOp.LogicalAnd:
          return (bool)first && (bool)this.Evaluate(binary.Right, frame);
        case IrBinaryOp.LogicalOr:
          return (bool)first || (bool)this.Evaluate(binary.Right, frame);
      }

      var second = this.Evaluate(binary.Right, frame);
      if (first is bool)
        return binary.Op == IrBinaryOp.Equal ? first.Equals(second) : !first.Equals(second);

      var type = binary.Left.Type;
      if (type == IrType.F32)
        return Binary(binary.Op, (float)first, (float)second);

      if (type == IrType.F64)
        return Binary(binary.Op, (double)first, (double)second);

      var left = (long)first;
      var right = (long)second;
      var a = IrOperators.Number(type, left);
      var b = IrOperators.Number(type, right);
      var count = (int)right & (type.Bits - 1);
      switch (binary.Op) {
        case IrBinaryOp.Add:
          return IrOperators.Wrap(type, left + right);
        case IrBinaryOp.Subtract:
          return IrOperators.Wrap(type, left - right);
        case IrBinaryOp.Multiply:
          return IrOperators.Wrap(type, left * right);
        case IrBinaryOp.AddChecked:
          return Fit(type, a + b);
        case IrBinaryOp.SubtractChecked:
          return Fit(type, a - b);
        case IrBinaryOp.MultiplyChecked:
          return Fit(type, a * b);
        case IrBinaryOp.Divide:
          CheckDivision(type, a, b);
          return (long)(a / b);
        case IrBinaryOp.Remainder:
          CheckDivision(type, a, b);
          return (long)(a % b);
        case IrBinaryOp.And:
          return left & right;
        case IrBinaryOp.Or:
          return left | right;
        case IrBinaryOp.Xor:
          return left ^ right;
        case IrBinaryOp.ShiftLeft:
          return IrOperators.Wrap(type, left << count);
        case IrBinaryOp.ShiftRight when type.IsSigned:
          return left >> count;
        case IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical:
          return IrOperators.Wrap(type, (long)(((ulong)left & (ulong.MaxValue >> (64 - type.Bits))) >> count));
        case IrBinaryOp.Equal:
          return a == b;
        case IrBinaryOp.NotEqual:
          return a != b;
        case IrBinaryOp.Less:
          return a < b;
        case IrBinaryOp.LessOrEqual:
          return a <= b;
        case IrBinaryOp.Greater:
          return a > b;
        default:
          return a >= b;
      }
    }

    /// <summary>
    /// An operator on binary32 numbers. C# computes it in binary32, so every result is rounded once,
    /// to binary32, as the IR requires.
    /// </summary>
    private static object Binary(IrBinaryOp op, float left, float right) => op switch {
      IrBinaryOp.Add => left + right,
      IrBinaryOp.Subtract => left - right,
      IrBinaryOp.Multiply => left * right,
      IrBinaryOp.Divide => left / right,
      IrBinaryOp.Remainder => left % right,
      _ => Compare(op, left, right)
    };

    private static object Binary(IrBinaryOp op, double left, double right) => op switch {
      IrBinaryOp.Add => left + right,
      IrBinaryOp.Subtract => left - right,
      IrBinaryOp.Multiply => left * right,
      IrBinaryOp.Divide => left / right,
      IrBinaryOp.Remainder => left % right,
      _ => Compare(op, left, right)
    };

    /// <summary>A comparison of floating-point numbers; a binary32 pair compares the same when widened.</summary>
    private static bool Compare(IrBinaryOp op, double left, double right) => op switch {
      IrBinaryOp.Equal => left == right,
      IrBinaryOp.NotEqual => left != right,
      IrBinaryOp.Less => left < right,
      IrBinaryOp.LessOrEqual => left <= right,
      IrBinaryOp.Greater => left > right,
      _ => left >= right
    };

    private static object Convert(IrConvert conversion, object operand) {
      var from = conversion.Operand.Type;
      var to = conversion.Type;
      if (from.IsFloat) {
        var number = operand is float single ? single : (double)operand;
        if (to == IrType.F32)
          return (float)number;

        if (to == IrType.F64)
          return number;

        return Whole(to, number, conversion.Contract == IrConversion.Trap);
      }

      var value = (long)operand;
      if (to == IrType.F32)
        return from == IrType.U64 ? (float)(ulong)value : (float)value;

      if (to == IrType.F64)
        return from == IrType.U64 ? (double)(ulong)value : (double)value;

      return conversion.Contract == IrConversion.Trap
        ? Fit(to, IrOperators.Number(from, value))
        : IrOperators.Wrap(to, value);
    }

    /// <summary>
    /// A floating-point number as an integer: truncated towards zero, and when the truncated number is
    /// not a value of the type either the overflow trap or the nearest value of the type, with a NaN
    /// becoming 0. Both bounds are powers of two, which a double holds exactly.
    /// </summary>
    private static long Whole(IrType type, double number, bool trap) {
      var whole = Math.Truncate(number);
      var low = (double)IrOperators.Minimum(type);
      var limit = (double)(IrOperators.Maximum(type) + 1);
      if (whole >= low && whole < limit)
        return (long)(Int128)whole;

      if (trap)
        throw new IrTrapException(IrTrap.IntegerOverflow);

      return double.IsNaN(number) ? 0 : (long)(number < 0 ? IrOperators.Minimum(type) : IrOperators.Maximum(type));
    }

    /// <summary>The number as a value of the type, or the overflow trap when it is not one.</summary>
    private static long Fit(IrType type, Int128 value) {
      if (value < IrOperators.Minimum(type) || value > IrOperators.Maximum(type))
        throw new IrTrapException(IrTrap.IntegerOverflow);

      return (long)value;
    }

    private static void CheckDivision(IrType type, Int128 dividend, Int128 divisor) {
      if (divisor == 0)
        throw new IrTrapException(IrTrap.DivideByZero);

      if (type.IsSigned && dividend == IrOperators.Minimum(type) && divisor == -1)
        throw new IrTrapException(IrTrap.IntegerOverflow);
    }

    private static int Checked(Array array, long index) {
      if (index < 0 || index >= array.Length)
        throw new IrTrapException(IrTrap.IndexOutOfRange);

      return (int)index;
    }

    private static object Load(Array array, int index) => Internal(array.GetValue(index)!);

    private static void Store(Array array, int index, object value)
      => array.SetValue(External(array.GetType().GetElementType()!, value), index);

  }

  /// <summary>
  /// How a statement ended. <see cref="Break"/> and <see cref="Return"/> both end a loop; masking with
  /// <see cref="Return"/> tells the loop's own end apart: a break ends there, a return goes on.
  /// </summary>
  [Flags]
  private enum Flow {
    Next = 0,
    Return = 1,
    Break = 2,
    Continue = 4
  }

  private sealed class Frame {

    public Dictionary<IrLocal, object> Locals { get; } = [];

    public object? Result { get; set; }

  }

}
