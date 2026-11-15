using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Verification.Images;

namespace Polypiler.Core.Verification.Runners;

/// <summary>
/// The trap class of a Rust panic, read from its message: a panic has no type, so its first line is
/// all there is. The rows are tried from the top; the messages are those of the Rust Reference's panic
/// conditions and of the standard library, and the IR's trap names the generated code panics with.
/// </summary>
public static class RustTraps {

  /// <summary>The IR trap names with the class each is, as the generated code writes them (<c>panic!("IntegerOverflow")</c>).</summary>
  private static readonly Dictionary<string, TrapKind> _irTraps = new(StringComparer.Ordinal) {
    ["IntegerOverflow"] = TrapKind.Overflow,
    ["DivideByZero"] = TrapKind.DivideByZero,
    ["IndexOutOfRange"] = TrapKind.Bounds,
    ["NullReference"] = TrapKind.Null,
    ["Unreachable"] = TrapKind.Unreachable,
    ["ArgumentOutOfRange"] = TrapKind.InvalidArgument
  };

  /// <summary>
  /// The IR trap a message stands for: the name a generated panic carries, or the trap a panic of Rust
  /// itself is - an overflow, a division by zero, an index outside a slice; <see langword="null"/>
  /// for any other panic.
  /// </summary>
  /// <param name="message">The panic message; only its first line is read.</param>
  /// <returns>The name of the IR trap, such as <c>IntegerOverflow</c>.</returns>
  public static string? TrapName(string message) {
    ArgumentNullException.ThrowIfNull(message);
    var line = FirstLine(message);
    var name = line.Split(": ", 2)[0];
    if (name.Length > 0 && name.All(char.IsAsciiLetter) && char.IsAsciiLetterUpper(name[0]))
      return name;

    return Classify(line).Kind switch {
      TrapKind.Overflow => "IntegerOverflow",
      TrapKind.DivideByZero => "DivideByZero",
      TrapKind.Bounds => "IndexOutOfRange",
      TrapKind.Null => "NullReference",
      TrapKind.Unreachable => "Unreachable",
      _ => null
    };
  }

  /// <summary>The class of a panic and the native name a driver reports with it.</summary>
  /// <param name="message">The panic message.</param>
  /// <returns>The class: <c>panic</c> as native name for a panic of Rust, the IR trap name for a generated one.</returns>
  public static TrapClass Classify(string message) {
    ArgumentNullException.ThrowIfNull(message);
    var line = FirstLine(message);
    if (line.StartsWith("attempt to ", StringComparison.Ordinal) && line.EndsWith(" with overflow", StringComparison.Ordinal))
      return new(TrapKind.Overflow);
    if (line is "attempt to divide by zero" or "attempt to calculate the remainder with a divisor of zero")
      return new(TrapKind.DivideByZero);
    if (new[] { "index out of bounds", "range start index", "range end index", "slice index starts at" }.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal)))
      return new(TrapKind.Bounds);
    if (line.StartsWith("called `Option::unwrap()` on a `None` value", StringComparison.Ordinal))
      return new(TrapKind.Null);
    if (line.StartsWith("internal error: entered unreachable code", StringComparison.Ordinal))
      return new(TrapKind.Unreachable);

    var name = line.Split(": ", 2)[0];
    if (name.Length > 0 && name.All(char.IsAsciiLetter) && char.IsAsciiLetterUpper(name[0]))
      return _irTraps.TryGetValue(name, out var kind) ? new(kind) : new(TrapKind.Other, name);

    return new(TrapKind.Other, "panic");
  }

  private static string FirstLine(string message) {
    var end = message.IndexOf('\n', StringComparison.Ordinal);
    return end < 0 ? message : message[..end];
  }

}
