using System;
using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// Watches a run of the interpreter at its program points: before every statement of a block and at the
/// end of a block that ran through, with the variables of the call as they are there. A soundness checker
/// compares what an analysis claims of a point with the values a run observes at it.
/// </summary>
public interface IIrObserver {

  /// <summary>A run reached a point of a block.</summary>
  /// <param name="block">The block, the very node of the function; a block of a called function is reported as well.</param>
  /// <param name="index">The statement about to run, or the number of statements for the end of the block.</param>
  /// <param name="state">The variables of the call at the point, readable only during this call.</param>
  void Reached(IrBlock block, int index, IrObservation state);

}

/// <summary>
/// A pointer as a run observes it (#1331): whether it is the null pointer, its byte offset from the start
/// of its region, and the bytes the region has.
/// </summary>
/// <param name="Region">The region, an identity only: two pointers into one region carry the same object; <see langword="null"/> for the null pointer.</param>
/// <param name="Offset">The byte offset from the start of the region.</param>
/// <param name="Bytes">The bytes of the region, 0 for the null pointer.</param>
public sealed record IrObservedPointer(object? Region, long Offset, long Bytes) {

  /// <summary>Whether the pointer is based on the null pointer.</summary>
  public bool IsNull => this.Region is null;

}

/// <summary>The variables of a call at the point an <see cref="IIrObserver"/> is told of.</summary>
public sealed class IrObservation {

  private readonly Func<IrExpr, object?> _evaluate;

  internal IrObservation(Func<IrExpr, object?> evaluate) => this._evaluate = evaluate;

  /// <summary>
  /// The value of an expression at the point: an <see cref="IrConst"/> for an integer or a truth value,
  /// an <see cref="IrObservedPointer"/> for a pointer. The expression is evaluated in the call, so it
  /// should change nothing: variables, constants and the operators over them.
  /// </summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The value; <see langword="null"/> when it reads a variable without a value, traps, or is of another type.</returns>
  public object? Value(IrExpr expression) {
    ArgumentNullException.ThrowIfNull(expression);
    try {
      return Observed(expression.Type, this._evaluate(expression));
    } catch (Exception exception) when (exception is IrException or KeyNotFoundException) {
      return null;
    }
  }

  private static object? Observed(IrType type, object? value) => value switch {
    bool truth when type == IrType.Bool => new IrConst(IrType.Bool, truth ? 1 : 0),
    long number when type.IsInteger => new IrConst(type, number),
    _ when type.IsPointer => IrInterpreter.Observe(value),
    _ => null
  };

}

public static partial class IrInterpreter {

  /// <summary>
  /// Calls a function of a module without globals and tells an observer every program point the run
  /// reaches, in the order it reaches them (<see cref="IIrObserver"/>).
  /// </summary>
  /// <param name="module">The module, which declares no globals.</param>
  /// <param name="function">The name of the function to call.</param>
  /// <param name="arguments">One value per parameter, of the parameter's type.</param>
  /// <param name="observer">What is told of every point reached.</param>
  /// <param name="steps">The most steps the call may take, as for a call within a budget; by default no limit.</param>
  /// <returns>The value returned, or <see langword="null"/> for a function that returns nothing.</returns>
  /// <exception cref="ArgumentException">The module has globals, the function does not exist or the arguments do not fit it.</exception>
  /// <exception cref="IrTrapException">An operation trapped.</exception>
  /// <exception cref="IrBudgetException">The call needs more than <paramref name="steps"/> steps.</exception>
  public static object? Call(IrModule module, string function, object[] arguments, IIrObserver observer, long steps = long.MaxValue) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(observer);
    if (module.Globals.Count > 0)
      throw new ArgumentException($"The module '{module.Name}' has globals, which an observed call does not initialise.", nameof(module));

    return Call(_programs.GetValue(module, Index), [], module, function, arguments, steps, observer);
  }

  /// <summary>A pointer the interpreter carries, as an observer sees it.</summary>
  internal static IrObservedPointer Observe(object? value) {
    var pointer = AsPointer(value!);
    return new IrObservedPointer(pointer.Backing, pointer.Offset, pointer.Region?.Bytes ?? 0);
  }

  private sealed partial class Machine {

    /// <summary>What is told of every point the run reaches; <see langword="null"/> for a run nobody watches.</summary>
    public IIrObserver? Observer { get; init; }

    /// <summary>Executes a block as an unobserved run does, telling the observer of each point first.</summary>
    private Flow Observed(IrBlock block, Frame frame, IIrObserver observer) {
      var state = new IrObservation(expression => this.Evaluate(expression, frame));
      for (var index = 0; index < block.Statements.Count; ++index) {
        observer.Reached(block, index, state);
        var ended = this.Run(block.Statements[index], frame);
        if (ended != Flow.Next)
          return ended;
      }

      observer.Reached(block, block.Statements.Count, state);
      return Flow.Next;
    }

  }

}
