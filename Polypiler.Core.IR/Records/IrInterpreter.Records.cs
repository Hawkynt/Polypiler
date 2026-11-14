using System;

namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>
  /// A struct value: the values of its fields in declaration order. It is mutable, so that a field
  /// store changes the place it is written through; that no two places share one is what
  /// <see cref="Copied"/> sees to, as every read of a struct value goes through it.
  /// </summary>
  private sealed class Record(object[] fields) {

    public object[] Fields { get; } = fields;

    /// <summary>A copy that shares nothing with this value but its arrays, which a struct holds by reference.</summary>
    public Record Copy() => new(Array.ConvertAll(this.Fields, Copied));

  }

  /// <summary>A value as a read yields it: a struct is copied, anything else is itself.</summary>
  private static object Copied(object value) => value is Record record ? record.Copy() : value;

  /// <summary>The default value of a struct: zero, false, and the default of every struct field.</summary>
  private static Record Default(IrStructType type)
    => new(Array.ConvertAll([.. type.Fields], field => field.Type.StructType is { } nested ? Default(nested) : Constant(field.Type, 0)));

  /// <summary>
  /// The position of a field among the fields of the struct it belongs to, which the verifier made
  /// sure it does; found by identity, as a field of another struct may have the same name and type.
  /// </summary>
  private static int Position(IrField field, IrType owner) {
    var fields = owner.StructType!.Fields;
    var index = 0;
    while (!ReferenceEquals(fields[index], field))
      ++index;

    return index;
  }

  /// <summary>
  /// Whether two values of one struct type are equal as <see cref="IrStructEquals"/> defines it: a
  /// floating-point number as <c>Equals</c> compares it, a struct field by field, an array by
  /// identity, anything else by value.
  /// </summary>
  private static bool Same(object left, object right) {
    switch (left) {
      case Record record:
        var other = ((Record)right).Fields;
        for (var index = 0; index < other.Length; ++index)
          if (!Same(record.Fields[index], other[index]))
            return false;

        return true;
      case Array:
        return ReferenceEquals(left, right);
      default:
        return left.Equals(right);
    }
  }

  private sealed partial class Machine {

    /// <summary>
    /// The struct an expression names as a place, without copying it: a variable, an array element
    /// whose array and index are evaluated and checked, a field of such a place. Any other
    /// expression is evaluated, which yields a value of its own.
    /// </summary>
    private object Place(IrExpr target, Frame frame) {
      switch (target) {
        case IrLocalRef reference:
          return frame.Locals[reference.Local];
        case IrArrayLoad load:
          var array = (Array)this.Evaluate(load.Array, frame);
          return Load(array, Checked(array, (long)this.Evaluate(load.Index, frame)));
        case IrFieldLoad load:
          return ((Record)this.Place(load.Target, frame)).Fields[Position(load.Field, load.Target.Type)];
        default:
          return this.Evaluate(target, frame);
      }
    }

  }

}
