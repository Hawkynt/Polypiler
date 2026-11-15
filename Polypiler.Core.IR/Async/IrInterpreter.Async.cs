using System;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>
  /// An async value as the interpreter carries it (#1330): settled already, with its result or its fault.
  /// The interpreter runs the subset of async code the IR admits in the one order .NET and JavaScript
  /// agree on - an awaited call runs to its end before the caller goes on - so a value is never pending,
  /// and no thread, task or scheduler is involved.
  /// </summary>
  /// <param name="Result">What the async function returned; <see langword="null"/> for none or a fault.</param>
  /// <param name="Fault">The exception it ended with, or <see langword="null"/> when it completed.</param>
  private sealed record Settled(object? Result, IrException? Fault);

  /// <summary>An async value taken across the boundary: its result as its type crosses, or its fault thrown, as awaiting it would.</summary>
  private static object? Settle(IrType type, Settled settled)
    => settled.Fault is { } fault ? throw fault : ToHost(type.Element!, settled.Result);

  /// <summary>The result of an awaited value, or its fault thrown where it is awaited.</summary>
  private static object Awaited(Settled settled) => settled.Fault is { } fault ? throw fault : settled.Result!;

  private sealed partial class Machine {

    /// <summary>
    /// Runs an async function to its end: a fault - also one before the first await - is kept in the
    /// value and thrown where the value is awaited, as .NET and JavaScript both do.
    /// </summary>
    private Settled Started(IrFunction function, object[] arguments) {
      var frame = new Frame();
      for (var index = 0; index < arguments.Length; ++index)
        frame.Declare(function.Parameters[index], arguments[index]);

      try {
        if (this.Run(function.Body, frame) != Flow.Return && function.ReturnType.Element != IrType.Void)
          throw new InvalidOperationException($"'{function.Name}' ended without returning a value.");
      } catch (IrException fault) {
        return new Settled(null, fault);
      }

      return new Settled(frame.Result, null);
    }

    /// <summary>
    /// An async value the IR makes: a completed one, one with a result, or the values of several calls,
    /// each run to its end in argument order, with the fault of the first that faulted.
    /// </summary>
    private Settled AsyncOperation(IrAsync operation, Frame frame) {
      var arguments = operation.Arguments.Select(argument => this.Evaluate(argument, frame)).ToList();
      switch (operation.Op) {
        case IrAsyncOp.Completed:
          return new Settled(null, null);
        case IrAsyncOp.FromResult:
          return new Settled(arguments[0], null);
        default:
          var settled = arguments.Cast<Settled>().ToList();
          if (settled.FirstOrDefault(value => value.Fault is not null) is { } faulted)
            return faulted;

          var element = operation.Type.Element!;
          return new Settled(element == IrType.Void ? null : ArrayOf(element.Element!, [.. settled.Select(value => value.Result!)]), null);
      }
    }

  }

}
