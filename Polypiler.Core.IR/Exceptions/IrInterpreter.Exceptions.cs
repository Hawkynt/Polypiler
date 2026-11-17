using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>
  /// A new object of an exception class with its message and its inner exception, and every other
  /// field at its default: what a constructor of a library class makes, and what a trap throws.
  /// </summary>
  /// <param name="type">An exception class.</param>
  /// <param name="message">The message.</param>
  /// <param name="inner">The inner exception, or <see langword="null"/> for none.</param>
  /// <returns>The object, as the interpreter carries it.</returns>
  internal static object NewException(IrClassType type, string message, object? inner = null) {
    var created = new Instance(type, [.. type.AllFields.Select(field => FieldDefault(field.Type))]);
    created.Fields[0] = message;
    created.Fields[1] = inner ?? NullReference.Value;
    return created;
  }

  private sealed partial class Machine {

    /// <summary>The exceptions the catch clauses being run caught, the innermost on top, which a rethrow throws again.</summary>
    private readonly Stack<IrException> _caught = new();

    /// <summary>
    /// A try. The catch clauses are tested in an exception filter of .NET, which runs in the first
    /// pass, before the finally blocks between the throw and the handler - the order the IR states.
    /// </summary>
    private Flow Try(IrTry attempt, Frame frame) {
      try {
        IrCatch? handler = null;
        try {
          return this.Run(attempt.Body, frame);
        } catch (IrException thrown) when ((handler = this.Handler(attempt, thrown, frame)) is not null) {
          this._caught.Push(thrown);
          try {
            return this.Run(handler.Body, frame);
          } finally {
            this._caught.Pop();
          }
        }
      } finally {
        // The verifier lets nothing jump out of a finally block, and a return's value is in the frame already.
        if (attempt.Finally is { } final)
          this.Run(final, frame);
      }
    }

    /// <summary>The first catch clause that selects an exception, with its local holding it; a filter that throws counts as false.</summary>
    private IrCatch? Handler(IrTry attempt, IrException thrown, Frame frame) {
      var exception = (Instance)thrown.Exception;
      foreach (var clause in attempt.Catches) {
        if (!exception.Type.DerivesFrom(clause.Class))
          continue;

        if (clause.Local is { } local)
          frame.Declare(local, exception);

        if (clause.Filter is null)
          return clause;

        this._filtering.Push(thrown);
        try {
          if ((bool)this.Evaluate(clause.Filter, frame))
            return clause;
        } catch (IrException) {
          // ECMA-335 Partition I, 12.4.2.4: an exception in a filter is swallowed and the filter fails.
        } finally {
          this._filtering.Pop();
        }
      }

      return null;
    }

    /// <summary>Throws the object an expression evaluates to; <c>null</c> traps.</summary>
    private IrException Throw(IrExpr exception, Frame frame) {
      var thrown = (Instance)NotNull(this.Evaluate(exception, frame));
      return IrException.Thrown(thrown, thrown.Type, (string)thrown.Fields[0]);
    }

    /// <summary>The statements of exception handling; <see langword="null"/> for any other.</summary>
    private Flow? ExceptionStatement(IrStmt statement, Frame frame) => statement switch {
      IrTry attempt => this.Try(attempt, frame),
      IrThrow thrown => throw this.Throw(thrown.Exception, frame),
      IrRethrow => throw this._caught.Peek(),
      _ => null
    };

    /// <summary>A new object of a library exception class: its message, or the class's own without one, and its inner exception.</summary>
    private object NewLibraryException(IrNew creation, Frame frame) {
      var arguments = creation.Arguments.Select(argument => this.Evaluate(argument, frame)).ToArray();
      var type = creation.Type.ClassType!;
      return NewException(type, arguments.Length > 0 ? (string)arguments[0] : IrExceptionClass.DefaultMessage(type), arguments.Length > 1 ? arguments[1] : null);
    }

    /// <summary>
    /// The call of a constructor of a library exception class by the constructor of a class derived
    /// from it: writes the message and the inner exception of the new object. Without a message,
    /// <c>Exception</c> words one for the class of the object, as .NET does, and every other library
    /// class gives its own.
    /// </summary>
    private bool LibraryConstructor(IrCall call, Frame frame) {
      if (IrExceptionClass.ConstructorOf(call.Function) is not { } constructor)
        return false;

      var arguments = call.Arguments.Select(argument => this.Evaluate(argument, frame)).ToArray();
      var created = (Instance)arguments[0];
      var owner = IrExceptionClass.All.First(type => type.Constructors.Contains(constructor));
      created.Fields[0] = arguments.Length > 1 ? arguments[1] : IrExceptionClass.DefaultMessage(owner == IrExceptionClass.Exception ? created.Type : owner);
      created.Fields[1] = arguments.Length > 2 ? arguments[2] : NullReference.Value;
      return true;
    }

  }

}
