using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>
  /// A sequence as the interpreter carries it: what opens a cursor on it. A restartable sequence opens
  /// a new cursor for every enumeration; a single-pass one opens its cursor once and hands that one
  /// out every time, so that an enumeration continues where the one before stopped and, once the
  /// cursor is closed, yields nothing.
  /// </summary>
  private sealed class SequenceValue(bool restartable, Func<Cursor> open) {

    private Cursor? _shared;

    /// <summary>The source and the keys of an ordering (<see cref="IrSequenceOp.OrderBy"/>), which a further key extends; <see langword="null"/> for any other sequence.</summary>
    public Ordering? Ordering { get; init; }

    /// <summary>The cursor of an enumeration.</summary>
    public Cursor Open() => restartable ? open() : this._shared ??= open();

  }

  /// <summary>An enumeration of a sequence: the values one after another, then the end; closing it ends it early.</summary>
  private abstract class Cursor {

    /// <summary>The value the last successful <see cref="MoveNext"/> reached.</summary>
    public object Current { get; protected set; } = null!;

    /// <summary>Goes on to the next value; <see langword="false"/> at the end, and always after the cursor was closed.</summary>
    public abstract bool MoveNext();

    /// <summary>Ends the enumeration: what the producer still has pending - its finally blocks - runs, innermost first; a second close does nothing.</summary>
    public abstract void Close();

  }

  /// <summary>The elements of an array, read when they are reached.</summary>
  private sealed class ArrayCursor(Array array) : Cursor {

    private long _index;

    public override bool MoveNext() {
      if (this._index >= array.Length)
        return false;

      this.Current = Copied(LoadElement(array, this._index++));
      return true;
    }

    public override void Close() => this._index = array.Length;

  }

  /// <summary>Thrown at the yield a body waits at when its cursor is closed: it leaves the body through every pending finally block.</summary>
  private sealed class Closing : Exception;

  /// <summary>
  /// The body of an iterator function as a coroutine: it runs on a thread of its own, which the
  /// consumer and the body hand control to in turn, so that exactly one of them runs at any time.
  /// The body starts with the first <see cref="MoveNext"/>; closing it while it waits at a yield
  /// throws <see cref="Closing"/> there, which runs its pending finally blocks innermost first. An
  /// exception that leaves the body reaches the consumer from the <see cref="MoveNext"/> or the
  /// <see cref="Close"/> it happened in.
  /// </summary>
  private sealed class BodyCursor(Action<BodyCursor> body) : Cursor {

    /// <summary>The stack of a body, which runs the interpreter as deeply as a call does.</summary>
    private const int StackSize = 16 * 1024 * 1024;

    private readonly SemaphoreSlim _resumed = new(0);
    private readonly SemaphoreSlim _suspended = new(0);
    private Thread? _thread;
    private bool _finished;
    private bool _closing;
    private ExceptionDispatchInfo? _failure;

    public override bool MoveNext() {
      if (this._finished)
        return false;

      this.Run();
      return !this._finished;
    }

    /// <summary>Closes the cursor, which every loop asked for a value first: the body waits at a yield, or has ended.</summary>
    public override void Close() {
      if (this._finished)
        return;

      this._closing = true;
      this.Run();
    }

    /// <summary>Hands a value of the body to the consumer and waits until the consumer wants the next one or closes the cursor.</summary>
    public void Yield(object value) {
      this.Current = value;
      this._suspended.Release();
      this._resumed.Wait();
      if (this._closing)
        throw new Closing();
    }

    /// <summary>Lets the body run until it yields or ends; rethrows what ended it.</summary>
    private void Run() {
      if (this._thread is null) {
        this._thread = new Thread(this.Body, StackSize) { IsBackground = true, Name = "IR iterator" };
        this._thread.Start();
      } else {
        this._resumed.Release();
      }

      this._suspended.Wait();
      if (this._failure is { } failure) {
        this._failure = null;
        failure.Throw();
      }
    }

    private void Body() {
      try {
        body(this);
      } catch (Closing) {
        // The consumer closed the cursor: every pending finally block has run.
      } catch (Exception exception) {
        this._failure = ExceptionDispatchInfo.Capture(exception);
      } finally {
        this._finished = true;
        this._suspended.Release();
      }
    }

  }

  /// <summary>The elements of an array as a restartable sequence.</summary>
  private static SequenceValue ArraySequence(Array array) => new(true, () => new ArrayCursor(array));

  private sealed partial class Machine {

    /// <summary>
    /// The sequence of a call of an iterator function: the arguments are kept, and every cursor opened
    /// on it runs the body from them in a frame of its own.
    /// </summary>
    private SequenceValue Sequence(IrFunction function, object[] arguments)
      => new(function.ReturnType.Restartable, () => new BodyCursor(cursor => {
        var frame = new Frame { Cursor = cursor };
        for (var index = 0; index < arguments.Length; ++index)
          frame.Declare(function.Parameters[index], Copied(arguments[index]));

        this.Run(function.Body, frame);
      }));

    /// <summary>The statements of lazy sequences; <see langword="null"/> for any other.</summary>
    private Flow? SequenceStatement(IrStmt statement, Frame frame) {
      switch (statement) {
        case IrYield produced:
          frame.Cursor!.Yield(this.Evaluate(produced.Value, frame));
          return Flow.Next;
        case IrYieldBreak:
          return Flow.Return;
        case IrYieldFrom delegated:
          var inner = ((SequenceValue)this.Evaluate(delegated.Sequence, frame)).Open();
          try {
            while (inner.MoveNext())
              frame.Cursor!.Yield(inner.Current);
          } finally {
            // Closing the outer cursor closes the inner one first.
            inner.Close();
          }

          return Flow.Next;
        case IrForEachSequence loop:
          return this.ForEachSequence(loop, frame);
        default:
          return null;
      }
    }

    /// <summary>A loop over a sequence, which closes its cursor however it ends.</summary>
    private Flow ForEachSequence(IrForEachSequence loop, Frame frame) {
      var cursor = ((SequenceValue)this.Evaluate(loop.Sequence, frame)).Open();
      try {
        while (cursor.MoveNext()) {
          frame.Declare(loop.Element, cursor.Current);
          var pass = this.Run(loop.Body, frame);
          if (pass is Flow.Break or Flow.Return)
            return pass & Flow.Return;
        }

        return Flow.Next;
      } finally {
        cursor.Close();
      }
    }

  }

}
