using System.Collections.Generic;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// The construct table of the unsafe subset of C# (#1331), which <c>docs/reference/pointers.md</c> prints and the
/// tests check against the fixtures: what the frontend lowers to the bounded pointers of the IR, and what it refuses
/// with <see cref="CSharpFrontend.Unsupported"/>, naming the construct.
/// </summary>
public static class CSharpPointerApi {

  /// <summary>The syntax the frontend lowers, each as it stands in a fixture, with the IR it becomes.</summary>
  public static IReadOnlyList<(string Syntax, string Ir)> Supported { get; } = [
    ("unsafe class", "nothing: the context of the pointers"),
    ("byte*", "the pointer type u8*"),
    ("void*", "the pointer type void*"),
    ("fixed (byte* start = data)", "Pin of an array"),
    ("fixed (byte* e = &d[3])", "Pin of an element"),
    ("fixed (byte* v = window)", "Pin of a span"),
    ("&x", "AddressOf"),
    ("*p", "PointerLoad, and PointerStore as a target"),
    ("p[i]", "PointerLoad of PointerAdd"),
    ("start + data.Length", "PointerAdd"),
    ("p++", "PointerAdd by one, as a statement"),
    ("q += n", "PointerAdd"),
    ("q -= 1", "PointerAdd by the negated count"),
    ("y - x", "PointerDifference"),
    ("p < end", "PointerLess and the other comparisons"),
    ("p == null", "PointerEqual with the null pointer"),
    ("(uint*)(p + at)", "PointerCast"),
    ("sizeof(long)", "a constant"),
    ("stackalloc int[8]", "StackAlloc into a pointer")
  ];

  /// <summary>The constructs the frontend refuses, each as its diagnostic names it.</summary>
  public static IReadOnlyList<string> Diagnosed { get; } = [
    "a cast from int to byte*",
    "a cast from byte* to long",
    "delegate*",
    "char*",
    "bool*",
    "a pointer to a struct",
    "stored in a field",
    "returned",
    "as a parameter of a public method",
    "in a lambda or a local function",
    "a pointer outlives the block of its region",
    "fixed of",
    "a step of a pointer whose value is used",
    "a conditional expression of pointers",
    "[SkipLocalsInit]",
    "a pointer in an iterator function"
  ];

}
