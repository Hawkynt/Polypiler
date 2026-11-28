using System;
using System.Collections.Generic;
using Polypiler.Core.Verification.Images;

namespace Polypiler.Core.Verification.Runners;

/// <summary>
/// The trap class of a .NET exception the C# driver caught, read from the simple name of its type: the
/// table of the C# runner's work order. A type the table does not name is <c>other:&lt;type name&gt;</c>.
/// </summary>
public static class CSharpTraps {

  /// <summary>The exception types with a class of their own, by simple type name, in the order of the table.</summary>
  public static IReadOnlyList<(string Exception, TrapKind Kind)> Table { get; } = [
    ("OverflowException", TrapKind.Overflow),
    ("DivideByZeroException", TrapKind.DivideByZero),
    ("IndexOutOfRangeException", TrapKind.Bounds),
    ("NullReferenceException", TrapKind.Null),
    ("ArgumentException", TrapKind.InvalidArgument),
    ("ArgumentNullException", TrapKind.InvalidArgument),
    ("ArgumentOutOfRangeException", TrapKind.InvalidArgument),
    ("UnreachableException", TrapKind.Unreachable)
  ];

  /// <summary>The trap class of an exception type.</summary>
  /// <param name="exceptionTypeName">The simple name of the exception's type, such as <c>OverflowException</c>.</param>
  /// <returns>The class of the table, or <c>other:&lt;type name&gt;</c>.</returns>
  /// <exception cref="ArgumentException">The name is empty or contains white space.</exception>
  public static TrapClass Classify(string exceptionTypeName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(exceptionTypeName);
    foreach (var (exception, kind) in Table)
      if (string.Equals(exception, exceptionTypeName, StringComparison.Ordinal))
        return new(kind);

    return new(TrapKind.Other, exceptionTypeName);
  }

}
