using System;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrInterpreter {

  /// <summary>
  /// An object of a class on the heap: its class and the values of its fields in the order of
  /// <see cref="IrClassType.AllFields"/>, those of the base classes first.
  /// Every reference to it is this very instance, which is what gives it its identity. It crosses the
  /// boundary as itself, an opaque value a later call can be given back.
  /// </summary>
  private sealed class Instance(IrClassType type, object[] fields) {

    public IrClassType Type { get; } = type;

    public object[] Fields { get; } = fields;

  }

  /// <summary>
  /// The reference that names no object, and the value of an array field that holds no array yet. It
  /// crosses the boundary as <see langword="null"/>.
  /// </summary>
  private sealed class NullReference {

    public static NullReference Value { get; } = new();

    private NullReference() { }

  }

  /// <summary>A value that is reached through: an object or an array, never <c>null</c>, which traps.</summary>
  private static object NotNull(object value) => value is NullReference ? throw new IrTrapException(IrTrap.NullReference) : value;

  /// <summary>The value a field of a new object holds before its constructor runs; a memory is empty, as <c>default(Memory&lt;T&gt;)</c> is.</summary>
  private static object FieldDefault(IrType type)
    => type.StructType is { } nested ? Default(nested)
      : type.IsMemory ? EmptyView(type.Element!)
      : type.IsNullable || type.IsArray || type.IsString ? NullReference.Value
      : Constant(type, 0);

  /// <summary>
  /// The position of a field among the fields of an object of a class: the same in the class that
  /// declares it and in every class derived from it, since the fields of a base class come first.
  /// </summary>
  private static int ObjectPosition(IrField field, IrType owner) {
    var type = owner.ClassType!;
    var index = 0;
    foreach (var ancestor in type.Lineage.Skip(1))
      index += ancestor.Fields.Count;

    return type.OwnerOf(field) == type ? index + Position(field, owner) : ObjectPosition(field, IrType.Object(type.OwnerOf(field)!));
  }

  /// <summary>Whether a value is an object of a class or an interface.</summary>
  private static bool IsOf(object value, IrType type)
    => value is Instance instance && IrType.Object(instance.Type).IsAssignableTo(type);

  /// <summary>Whether two record objects, or two <c>null</c>s, are equal as <see cref="IrStructEquals"/> defines it for a record class.</summary>
  private static bool SameObject(object left, object right) {
    if (left is not Instance first || right is not Instance second)
      return ReferenceEquals(left, right);

    if (ReferenceEquals(first, second))
      return true;

    for (var index = 0; index < first.Fields.Length; ++index) {
      var (a, b) = (first.Fields[index], second.Fields[index]);
      if (!(a is Instance or NullReference && first.Type.Fields[index].Type.ClassType is { IsRecord: true } ? SameObject(a, b) : Same(a, b)))
        return false;
    }

    return true;
  }

  private sealed partial class Machine {

    /// <summary>A new object: its fields at their defaults, then its constructor run on it.</summary>
    private object New(IrNew creation, Frame frame) {
      var arguments = creation.Arguments.Select(argument => this.Evaluate(argument, frame)).ToArray();
      var type = creation.Type.ClassType!;
      var created = new Instance(type, [.. type.AllFields.Select(field => FieldDefault(field.Type))]);
      this.Invoke(program.Functions[creation.Constructor.Function], [created, .. arguments]);
      return created;
    }

    /// <summary>
    /// A call of a method: the receiver of an instance method evaluated and checked first, the
    /// arguments after it - except for a setter, whose value is evaluated before the check, as a
    /// field store's is.
    /// </summary>
    private object MethodCall(IrMethodCall call, Frame frame) {
      if (call.Target is not { } target)
        return this.Invoke(program.Functions[call.Method.Function], [.. call.Arguments.Select(argument => this.Evaluate(argument, frame))])!;

      var receiver = this.Evaluate(target, frame);
      if (call.Method.Kind != IrMethodKind.Setter)
        NotNull(receiver);

      var arguments = call.Arguments.Select(argument => this.Evaluate(argument, frame)).ToArray();
      var self = (Instance)NotNull(receiver);
      var method = call.Kind switch {
        IrMethodCallKind.Virtual => self.Type.Select(call.Method),
        IrMethodCallKind.Interface => self.Type.Select(self.Type.Implementation(call.Method)!),
        _ => call.Method
      };
      return this.Invoke(program.Functions[method.Function], [self, .. arguments])!;
    }

    /// <summary>A type test, a cast or an <c>as</c>: the reference evaluated, then tested against the type.</summary>
    private object Conversion(IrExpr expression, Frame frame) {
      switch (expression) {
        case IrTypeTest test:
          return IsOf(this.Evaluate(test.Value, frame), test.Target);
        case IrCast cast:
          var value = this.Evaluate(cast.Value, frame);
          return value is NullReference || IsOf(value, cast.Type) ? value : throw new IrTrapException(IrTrap.InvalidCast);
        default:
          var converted = (IrAsCast)expression;
          var reference = this.Evaluate(converted.Value, frame);
          return IsOf(reference, converted.Type) ? reference : NullReference.Value;
      }
    }

    /// <summary>A field of the object a reference names, read after the reference is checked.</summary>
    private object ObjectField(IrFieldLoad load, Frame frame) {
      var instance = (Instance)NotNull(this.Evaluate(load.Target, frame));
      return Copied(instance.Fields[ObjectPosition(load.Field, load.Target.Type)]);
    }

    /// <summary>A field of the object a reference names, written once the value is evaluated and the reference checked.</summary>
    private void StoreObjectField(IrFieldStore store, Frame frame) {
      var target = this.Evaluate(store.Target, frame);
      var value = this.Evaluate(store.Value, frame);
      ((Instance)NotNull(target)).Fields[ObjectPosition(store.Field, store.Target.Type)] = value;
    }

  }

}
