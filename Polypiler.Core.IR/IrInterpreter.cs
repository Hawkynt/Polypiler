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
/// <see cref="ushort"/>), as <see cref="bool"/>, and as one-dimensional arrays of the numeric types;
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
    _ => typeof(ulong)
  };

  /// <summary>A .NET value as the interpreter carries it: every integer as a <see cref="long"/>.</summary>
  private static object Internal(object value) => value switch {
    Array or bool => value,
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
    TypeCode.Int64 => value,
    _ => (ulong)(long)value
  };

  private static object? ToHost(IrType type, object? value)
    => value is null or Array or bool ? value : External(HostType(type), value);

  private sealed class Machine(Dictionary<string, IrFunction> functions) {

    public object? Invoke(IrFunction function, object[] arguments) {
      var frame = new Frame();
      for (var index = 0; index < arguments.Length; ++index)
        frame.Locals[function.Parameters[index]] = arguments[index];

      if (!this.Run(function.Body, frame) && function.ReturnType != IrType.Void)
        throw new InvalidOperationException($"'{function.Name}' ended without returning a value.");

      return frame.Result;
    }

    /// <summary>Executes a statement; true when it returned from the function.</summary>
    private bool Run(IrStmt statement, Frame frame) {
      switch (statement) {
        case IrBlock block:
          foreach (var inner in block.Statements)
            if (this.Run(inner, frame))
              return true;

          return false;
        case IrDeclare declare:
          if (declare.Initializer is not null)
            frame.Locals[declare.Local] = this.Evaluate(declare.Initializer, frame);

          return false;
        case IrAssign assign:
          frame.Locals[assign.Local] = this.Evaluate(assign.Value, frame);
          return false;
        case IrStore store:
          var array = (Array)this.Evaluate(store.Array, frame);
          var index = (long)this.Evaluate(store.Index, frame);
          var value = this.Evaluate(store.Value, frame);
          Store(array, Checked(array, index), value);
          return false;
        case IrExprStmt expression:
          this.Evaluate(expression.Expression, frame);
          return false;
        case IrIf conditional:
          if ((bool)this.Evaluate(conditional.Condition, frame))
            return this.Run(conditional.Then, frame);

          return conditional.Else is not null && this.Run(conditional.Else, frame);
        case IrWhile loop:
          while ((bool)this.Evaluate(loop.Condition, frame))
            if (this.Run(loop.Body, frame))
              return true;

          return false;
        case IrReturn result:
          frame.Result = result.Value is null ? null : this.Evaluate(result.Value, frame);
          return true;
        default:
          throw new NotSupportedException($"Unknown statement {statement.GetType().Name}.");
      }
    }

    private object Evaluate(IrExpr expression, Frame frame) {
      switch (expression) {
        case IrConst constant:
          return constant.Type == IrType.Bool ? constant.Value != 0 : constant.Value;
        case IrLocalRef reference:
          return frame.Locals[reference.Local];
        case IrUnary unary:
          var operand = this.Evaluate(unary.Operand, frame);
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
        default:
          throw new NotSupportedException($"Unknown expression {expression.GetType().Name}.");
      }
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

    private static object Convert(IrConvert conversion, object operand) {
      var value = (long)operand;
      return conversion.Contract == IrConversion.Trap
        ? Fit(conversion.Type, IrOperators.Number(conversion.Operand.Type, value))
        : IrOperators.Wrap(conversion.Type, value);
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

  private sealed class Frame {

    public Dictionary<IrLocal, object> Locals { get; } = [];

    public object? Result { get; set; }

  }

}
