using System;
using System.Linq;

namespace Polypiler.Core.Verification.Images;

/// <summary>The classes of a call that ends in an exception, <c>die</c>, panic or run-time error.</summary>
public enum TrapKind {

  /// <summary><c>overflow</c>: an arithmetic result does not fit.</summary>
  Overflow,

  /// <summary><c>divide-by-zero</c>.</summary>
  DivideByZero,

  /// <summary><c>bounds</c>: an index outside its array or string.</summary>
  Bounds,

  /// <summary><c>null</c>: a null, <c>undef</c>, <c>None</c> or <c>nil</c> dereferenced.</summary>
  Null,

  /// <summary><c>invalid-shift</c>: a shift count the language refuses.</summary>
  InvalidShift,

  /// <summary><c>unreachable</c>: code that must not run did.</summary>
  Unreachable,

  /// <summary><c>invalid-argument</c>: the function refused an argument.</summary>
  InvalidArgument,

  /// <summary><c>result-type</c>: the native result does not fit the declared image type.</summary>
  ResultType,

  /// <summary><c>other:&lt;native name&gt;</c>: anything the runner's table does not map; compared with its name.</summary>
  Other

}

/// <summary>
/// The outcome of a call that trapped: it has no output image, only its class. Two traps are equal
/// when their classes are, and for <see cref="TrapKind.Other"/> when their native names are too.
/// </summary>
/// <param name="Kind">The class.</param>
/// <param name="NativeName">The native name of an <see cref="TrapKind.Other"/> trap; <see langword="null"/> for every other class.</param>
public sealed record TrapClass(TrapKind Kind, string? NativeName = null) {

  private static readonly string[] _spellings = ["overflow", "divide-by-zero", "bounds", "null", "invalid-shift", "unreachable", "invalid-argument", "result-type"];

  /// <summary>The class.</summary>
  public TrapKind Kind { get; } = Validate(Kind, NativeName);

  /// <summary>The native name of an <see cref="TrapKind.Other"/> trap; <see langword="null"/> for every other class.</summary>
  public string? NativeName { get; } = NativeName;

  /// <summary>Reads a trap class: <c>overflow</c>, …, <c>result-type</c> or <c>other:&lt;native name&gt;</c>.</summary>
  /// <param name="text">The text.</param>
  /// <returns>The class.</returns>
  /// <exception cref="ImageException">The text is not a trap class.</exception>
  public static TrapClass Parse(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var index = Array.IndexOf(_spellings, text);
    if (index >= 0)
      return new((TrapKind)index);

    if (text.StartsWith("other:", StringComparison.Ordinal) && IsName(text[6..]))
      return new(TrapKind.Other, text[6..]);

    throw new ImageException($"'{text}' is not a trap class");
  }

  /// <summary>Reads the trap line of the driver protocol: <c>!&lt;class&gt; &lt;native name&gt;</c>, e.g. <c>!divide-by-zero ZeroDivisionError</c>.</summary>
  /// <param name="line">The line.</param>
  /// <param name="nativeName">The native name the driver reported.</param>
  /// <returns>The class; compare with it, not with the native name.</returns>
  /// <exception cref="ImageException">The line is not of that form.</exception>
  public static TrapClass ParseLine(string line, out string nativeName) {
    ArgumentNullException.ThrowIfNull(line);
    var space = line.IndexOf(' ', StringComparison.Ordinal);
    if (!line.StartsWith('!') || space < 0 || !IsName(line[(space + 1)..]))
      throw new ImageException($"'{line}' is not a trap line '!<class> <native name>'");

    nativeName = line[(space + 1)..];
    return Parse(line[1..space]);
  }

  /// <summary>The canonical spelling: <c>divide-by-zero</c>, <c>other:KeyError</c>.</summary>
  public override string ToString() => this.Kind == TrapKind.Other ? $"other:{this.NativeName}" : _spellings[(int)this.Kind];

  private static TrapKind Validate(TrapKind kind, string? name) {
    if (!Enum.IsDefined(kind))
      throw new ArgumentOutOfRangeException(nameof(Kind), kind, "not a trap class");
    if (kind == TrapKind.Other ? name is null || !IsName(name) : name is not null)
      throw new ArgumentException(kind == TrapKind.Other ? "an 'other' trap needs a native name without spaces" : "only an 'other' trap carries a native name", nameof(NativeName));

    return kind;
  }

  private static bool IsName(string text) => text.Length > 0 && !text.Any(char.IsWhiteSpace);

}
