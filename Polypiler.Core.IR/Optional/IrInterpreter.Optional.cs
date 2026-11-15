namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>
  /// The value of an optional value that holds none. One that holds a value is that value itself. It
  /// crosses the boundary as <see langword="null"/>.
  /// </summary>
  private sealed class Absent {

    public static Absent Value { get; } = new();

    private Absent() { }

  }

  /// <summary>An optional value taken across the boundary: <see langword="null"/> is none, anything else a value of the underlying type.</summary>
  private static object? AcceptedOptional(IrType type, object? value) => value is null ? Absent.Value : Accepted(type.Underlying!, value);

  /// <summary>An optional value given back across the boundary: none as <see langword="null"/>.</summary>
  private static object? OptionalToHost(IrType type, object value) => value is Absent ? null : ToHost(type.Underlying!, value);

  private sealed partial class Machine {

    /// <summary>The nodes of optional values and of tests for <c>null</c>.</summary>
    private object Optional(IrExpr expression, Frame frame) {
      switch (expression) {
        case IrNone:
          return Absent.Value;
        case IrSome some:
          return this.Evaluate(some.Value, frame);
        case IrIsNone test:
          return this.Evaluate(test.Value, frame) is Absent;
        case IrUnwrap unwrap:
          var held = this.Evaluate(unwrap.Value, frame);
          return held is Absent ? throw new IrTrapException(IrTrap.MissingValue) : held;
        case IrCoalesce coalesce:
          var value = this.Evaluate(coalesce.Value, frame);
          return value is Absent ? this.Evaluate(coalesce.Fallback, frame) : value;
        case IrIsNull test:
          return this.Evaluate(test.Value, frame) is NullReference;
        default:
          var bind = (IrBind)expression;
          var bound = this.Evaluate(bind.Value, frame);
          frame.Locals[bind.Local] = bound;
          return Copied(bound);
      }
    }

  }

}
