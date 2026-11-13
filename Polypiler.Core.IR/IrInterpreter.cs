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
  IntegerOverflow

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
    _ => "integer overflow"
  };

}

/// <summary>
/// Evaluates the IR directly. Values cross the boundary as the .NET type of the same width and
/// signedness (a <c>u16</c> is accepted as <see cref="ushort"/> or <see cref="char"/> and returned as
/// <see cref="ushort"/>), as <see cref="float"/>, <see cref="double"/> and <see cref="bool"/>, and as
/// one-dimensional arrays of the numeric types;
/// arrays are passed by reference, so a function can change the caller's array.
/// </summary>
public static class IrInterpreter {

  /// <summary>The functions of a module by name, kept for as long as the module lives.</summary>
  private static readonly ConditionalWeakTable<IrModule, Dictionary<string, IrFunction>> _functions = new();

  /// <summary>Calls a function of a module that <see cref="IrVerifier"/> accepted.</summary>
  /// <param name="module">The module.</param>
  /// <param name="function">The name of the function to call.</param>
  /// <param name="arguments">One value per parameter, of the parameter's type.</param>
  /// <returns>The value returned, or <see langword="null"/> for a function that returns nothing.</returns>
  /// <exception cref="ArgumentException">The function does not exist or the arguments do not fit it.</exception>
  /// <exception cref="IrTrapException">An operation trapped.</exception>
  public static object? Call(IrModule module, string function, object[] arguments) {
    var functions = _functions.GetValue(module, Index);
    if (!functions.TryGetValue(function, out var target))
      throw new ArgumentException($"The module '{module.Name}' has no function '{function}'.", nameof(function));

    if (arguments.Length != target.Parameters.Count)
      throw new ArgumentException($"'{function}' takes {target.Parameters.Count} arguments, not {arguments.Length}.", nameof(arguments));

    var values = arguments.Select((argument, index) => FromHost(target.Parameters[index], argument)).ToArray();
    return ToHost(target.ReturnType, new Machine(functions).Invoke(target, values));
  }

  private static Dictionary<string, IrFunction> Index(IrModule module) {
    var functions = new Dictionary<string, IrFunction>();
    foreach (var candidate in module.Functions)
      functions[candidate.Name] = candidate;

    return functions;
  }

  /// <summary>
  /// Takes an argument across the boundary. The type is compared exactly: to a pattern such as
  /// <c>is int[]</c> a <c>uint[]</c> would pass as well, and its elements would be read as signed.
  /// </summary>
  private static object FromHost(IrLocal parameter, object argument) {
    if (argument is null || TypeOf(argument.GetType()) != parameter.Type)
      throw new ArgumentException($"The argument for '{parameter.Name}' is not a value of {parameter.Type}.", nameof(argument));

    return Internal(argument);
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
    _ => typeof(double)
  };

  /// <summary>A .NET value as the interpreter carries it: every integer as a <see cref="long"/>.</summary>
  private static object Internal(object value) => value switch {
    Array or bool or float or double => value,
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

  private static object? ToHost(IrType type, object? value)
    => value is null or Array or bool ? value : External(HostType(type), value);

  private sealed class Machine(Dictionary<string, IrFunction> functions) {

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
        case IrStore store:
          var array = (Array)this.Evaluate(store.Array, frame);
          var index = (long)this.Evaluate(store.Index, frame);
          var value = this.Evaluate(store.Value, frame);
          Store(array, Checked(array, index), value);
          return Flow.Next;
        case IrExprStmt expression:
          this.Evaluate(expression.Expression, frame);
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
          return constant.Type.Kind switch {
            IrTypeKind.Bool => constant.Value != 0,
            IrTypeKind.F32 => (float)IrOperators.Float(constant.Type, constant.Value),
            IrTypeKind.F64 => IrOperators.Float(constant.Type, constant.Value),
            _ => constant.Value
          };
        case IrLocalRef reference:
          return frame.Locals[reference.Local];
        case IrUnary unary:
          var operand = this.Evaluate(unary.Operand, frame);
          if (unary.Type.IsFloat)
            return operand is float single ? -single : -(double)operand;

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
          var array = (Array)this.Evaluate(load.Array, frame);
          return Load(array, Checked(array, (long)this.Evaluate(load.Index, frame)));
        case IrArrayLength length:
          return (long)((Array)this.Evaluate(length.Array, frame)).Length;
        case IrNewArray creation:
          var count = (long)this.Evaluate(creation.Length, frame);
          if (count < 0)
            throw new IrTrapException(IrTrap.IntegerOverflow);

          return Array.CreateInstance(HostType(creation.Type.Element!), count);
        case IrCall call:
          var arguments = call.Arguments.Select(argument => this.Evaluate(argument, frame)).ToArray();
          return this.Invoke(functions[call.Function], arguments)!;
        case IrStep step:
          return this.Step(step, frame);
        default:
          throw new NotSupportedException($"Unknown expression {expression.GetType().Name}.");
      }
    }

    private object Step(IrStep step, Frame frame) {
      object before;
      object after;
      if (step.Target is IrArrayLoad element) {
        var array = (Array)this.Evaluate(element.Array, frame);
        var index = Checked(array, (long)this.Evaluate(element.Index, frame));
        before = Load(array, index);
        after = Stepped(step, before);
        Store(array, index, after);
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
