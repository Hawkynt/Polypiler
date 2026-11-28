using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Verification.Images;

namespace Polypiler.Core.Verification.Runners;

/// <summary>One row of the Kotlin trap table: a JVM exception class, optionally only with one message, and its trap class.</summary>
/// <param name="Class">The simple name of the class; a subclass of it matches too.</param>
/// <param name="Message">The message the row needs, or <see langword="null"/> for any.</param>
/// <param name="Trap">The trap class.</param>
public sealed record KotlinTrapRow(string Class, string? Message, TrapKind Trap);

/// <summary>
/// The trap class of a JVM exception, as #426 states it and <c>docs/reference/exceptions.md</c>
/// ("Kotlin") lists it: <c>ArithmeticException</c> is a division by zero for the message
/// <c>/ by zero</c> (and for none: a JVM that throws a preallocated exception from compiled code
/// leaves the message out, which only the implicit ones of <c>/</c>, <c>%</c>, a <c>null</c> and an
/// index are), otherwise an overflow; the index exceptions and <c>NegativeArraySizeException</c> are a
/// bounds trap; <c>NullPointerException</c> null; <c>IllegalArgumentException</c> an invalid
/// argument; <c>NoWhenBranchMatchedException</c> and <c>IllegalStateException</c> unreachable for the
/// message <c>unreachable</c>; every other <c>Throwable</c> <c>other:</c> and its simple name. The
/// rows are tried from the top, the first that matches wins. The generated driver tests them in the
/// same order (<see cref="KotlinDriverGenerator"/>).
/// </summary>
public static class KotlinTrapTable {

  /// <summary>The rows, in the order they are tried.</summary>
  public static IReadOnlyList<KotlinTrapRow> Rows { get; } = [
    new("ArithmeticException", "/ by zero", TrapKind.DivideByZero),
    new("ArithmeticException", null, TrapKind.Overflow),
    new("ArrayIndexOutOfBoundsException", null, TrapKind.Bounds),
    new("StringIndexOutOfBoundsException", null, TrapKind.Bounds),
    new("IndexOutOfBoundsException", null, TrapKind.Bounds),
    new("NegativeArraySizeException", null, TrapKind.Bounds),
    new("NullPointerException", null, TrapKind.Null),
    new("IllegalArgumentException", null, TrapKind.InvalidArgument),
    new("NoWhenBranchMatchedException", "unreachable", TrapKind.Unreachable),
    new("IllegalStateException", "unreachable", TrapKind.Unreachable)
  ];

  /// <summary>
  /// The trap class of an exception: the first row whose class is the exception's or one of its
  /// bases and whose message is the exception's, or <c>other:</c> and the simple name.
  /// </summary>
  /// <param name="hierarchy">The simple names of the exception's class and its bases, the class first.</param>
  /// <param name="message">The message, or <see langword="null"/>.</param>
  /// <returns>The trap class, and the native name the driver reports with it: the simple name of the exception's class.</returns>
  /// <exception cref="ArgumentException">The hierarchy is empty.</exception>
  public static (TrapClass Trap, string NativeName) Classify(IReadOnlyList<string> hierarchy, string? message) {
    ArgumentNullException.ThrowIfNull(hierarchy);
    if (hierarchy.Count == 0)
      throw new ArgumentException("an exception has a class", nameof(hierarchy));

    foreach (var row in Rows) {
      if (!hierarchy.Contains(row.Class))
        continue;

      // A division by zero thrown by compiled code may come without its message.
      var matches = row.Message is null || row.Message == message || row.Trap == TrapKind.DivideByZero && message is null;
      if (matches)
        return (new(row.Trap), hierarchy[0]);
    }

    return (new(TrapKind.Other, hierarchy[0]), hierarchy[0]);
  }

}
