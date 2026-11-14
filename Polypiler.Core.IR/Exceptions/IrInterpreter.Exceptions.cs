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

}
