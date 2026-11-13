using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>The ways an IR operation can fail at run time.</summary>
public enum IrTrap {

  /// <summary>A division or remainder with a zero divisor.</summary>
  DivideByZero,

  /// <summary>An array access outside the array.</summary>
  IndexOutOfRange,

  /// <summary>
  /// A signed quotient that does not fit 32 bits (the smallest value divided by -1, also as a
  /// remainder), or an array created with a negative length.
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
/// Evaluates the IR directly. Values cross the boundary as <see cref="int"/>, <see cref="uint"/>,
/// <see cref="byte"/>, <see cref="bool"/> and arrays of the three integer types; arrays are passed by
/// reference, so a function can change the caller's array.
/// </summary>
public static class IrInterpreter {

  /// <summary>Calls a function of a module that <see cref="IrVerifier"/> accepted.</summary>
  /// <param name="module">The module.</param>
  /// <param name="function">The name of the function to call.</param>
  /// <param name="arguments">One value per parameter, of the parameter's type.</param>
  /// <returns>The value returned, or <see langword="null"/> for a function that returns nothing.</returns>
  /// <exception cref="ArgumentException">The function does not exist or the arguments do not fit it.</exception>
  /// <exception cref="IrTrapException">An operation trapped.</exception>
  public static object? Call(IrModule module, string function, object[] arguments) {
    var functions = new Dictionary<string, IrFunction>();
    foreach (var candidate in module.Functions)
      functions[candidate.Name] = candidate;

    if (!functions.TryGetValue(function, out var target))
      throw new ArgumentException($"The module '{module.Name}' has no function '{function}'.", nameof(function));

    if (arguments.Length != target.Parameters.Count)
      throw new ArgumentException($"'{function}' takes {target.Parameters.Count} arguments, not {arguments.Length}.", nameof(arguments));

    var values = arguments.Select((argument, index) => FromHost(target.Parameters[index], argument)).ToArray();
    var result = new Machine(functions).Invoke(target, values);
    return target.ReturnType.Kind switch {
      IrTypeKind.Void => null,
      IrTypeKind.I32 => (int)(long)result!,
      IrTypeKind.U32 => (uint)(long)result!,
      IrTypeKind.U8 => (byte)(long)result!,
      _ => result
    };
  }

  /// <summary>
  /// Takes an argument across the boundary. The type is compared exactly: to a pattern such as
  /// <c>is int[]</c> a <c>uint[]</c> would pass as well, and its elements would be read as signed.
  /// </summary>
  private static object FromHost(IrLocal parameter, object argument) {
    if (argument?.GetType() != HostType(parameter.Type))
      throw new ArgumentException($"The argument for '{parameter.Name}' is not a value of {parameter.Type}.", nameof(argument));

    return argument is Array or bool ? argument : Convert.ToInt64(argument, CultureInfo.InvariantCulture);
  }

  private static Type HostType(IrType type) => type.Kind switch {
    IrTypeKind.I32 => typeof(int),
    IrTypeKind.U32 => typeof(uint),
    IrTypeKind.U8 => typeof(byte),
    IrTypeKind.Bool => typeof(bool),
    _ => HostType(type.Element!).MakeArrayType()
  };

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
          var value = (long)this.Evaluate(store.Value, frame);
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
            IrUnaryOp.BitNot => IrOperators.Wrap(unary.Type, ~(long)operand),
            _ => !(bool)operand
          };
        case IrBinary binary:
          return this.Binary(binary, frame);
        case IrConvert conversion:
          return IrOperators.Wrap(conversion.Type, (long)this.Evaluate(conversion.Operand, frame));
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

          return creation.Type.Element!.Kind switch {
            IrTypeKind.I32 => new int[count],
            IrTypeKind.U32 => new uint[count],
            _ => new byte[count]
          };
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
      var count = (int)(right & 31);
      switch (binary.Op) {
        case IrBinaryOp.Add:
          return IrOperators.Wrap(type, left + right);
        case IrBinaryOp.Subtract:
          return IrOperators.Wrap(type, left - right);
        case IrBinaryOp.Multiply:
          return IrOperators.Wrap(type, left * right);
        case IrBinaryOp.Divide:
          CheckDivision(left, right);
          return IrOperators.Wrap(type, left / right);
        case IrBinaryOp.Remainder:
          CheckDivision(left, right);
          return IrOperators.Wrap(type, left % right);
        case IrBinaryOp.And:
          return left & right;
        case IrBinaryOp.Or:
          return left | right;
        case IrBinaryOp.Xor:
          return left ^ right;
        case IrBinaryOp.ShiftLeft:
          return IrOperators.Wrap(type, left << count);
        case IrBinaryOp.ShiftRight:
          return left >> count;
        case IrBinaryOp.ShiftRightLogical:
          return IrOperators.Wrap(type, (long)((uint)left >> count));
        case IrBinaryOp.Equal:
          return left == right;
        case IrBinaryOp.NotEqual:
          return left != right;
        case IrBinaryOp.Less:
          return left < right;
        case IrBinaryOp.LessOrEqual:
          return left <= right;
        case IrBinaryOp.Greater:
          return left > right;
        default:
          return left >= right;
      }
    }

    private static void CheckDivision(long dividend, long divisor) {
      if (divisor == 0)
        throw new IrTrapException(IrTrap.DivideByZero);

      if (dividend == int.MinValue && divisor == -1)
        throw new IrTrapException(IrTrap.IntegerOverflow);
    }

    private static int Checked(Array array, long index) {
      if (index < 0 || index >= array.Length)
        throw new IrTrapException(IrTrap.IndexOutOfRange);

      return (int)index;
    }

    private static object Load(Array array, int index) {
      var element = array.GetType().GetElementType();
      if (element == typeof(int))
        return (long)((int[])array)[index];

      return element == typeof(uint) ? (long)((uint[])array)[index] : (long)((byte[])array)[index];
    }

    private static void Store(Array array, int index, long value) {
      var element = array.GetType().GetElementType();
      if (element == typeof(int))
        ((int[])array)[index] = (int)value;
      else if (element == typeof(uint))
        ((uint[])array)[index] = (uint)value;
      else
        ((byte[])array)[index] = (byte)value;
    }

  }

  private sealed class Frame {

    public Dictionary<IrLocal, object> Locals { get; } = [];

    public object? Result { get; set; }

  }

}
