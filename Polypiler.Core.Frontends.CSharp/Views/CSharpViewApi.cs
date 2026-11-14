using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>One member of the base library the C# frontend imports as an operation on views of the IR.</summary>
/// <param name="Type">The type that declares the member, as C# displays its definition: <c>System.Span&lt;T&gt;</c>, <c>System.MemoryExtensions</c>.</param>
/// <param name="Member">The name of the member.</param>
/// <param name="Arguments">The numbers of arguments it is imported with, the receiver counted.</param>
/// <param name="Ir">The construct of the IR it becomes.</param>
/// <param name="Note">What happens at its edges where that is not plain.</param>
public sealed record CSharpViewMember(string Type, string Member, IReadOnlyList<int> Arguments, string Ir, string Note = "");

/// <summary>
/// The members of <c>Span&lt;T&gt;</c>, <c>ReadOnlySpan&lt;T&gt;</c>, <c>MemoryExtensions</c> and
/// <c>MemoryMarshal</c> the C# frontend maps to the views of the IR (#1325) - the table the frontend reads
/// and the tests check - and the constructs it refuses by name. The syntax of views - the indexer,
/// <c>Length</c>, <c>IsEmpty</c>, ranges, <c>foreach</c>, <c>stackalloc</c> and the conversion of an array -
/// is listed in <see cref="Syntax"/>.
/// </summary>
public static class CSharpViewApi {

  private const string Span = "System.Span<T>";
  private const string ReadOnly = "System.ReadOnlySpan<T>";
  private const string Extensions = "System.MemoryExtensions";
  private const string Marshal = "System.Runtime.InteropServices.MemoryMarshal";
  private const string Bounds = "a range outside the source traps with ArgumentOutOfRange";

  /// <summary>Every member the frontend maps.</summary>
  public static IReadOnlyList<CSharpViewMember> Members { get; } = [
    new(Extensions, "AsSpan", [1, 2, 3], "ViewOf", "of an array, at int positions; " + Bounds),
    .. new[] { Span, ReadOnly }.SelectMany(type => (CSharpViewMember[])[
      new(type, "Slice", [2, 3], "Slice", Bounds),
      new(type, "CopyTo", [2], "ViewCopy", "memmove; a shorter destination throws ArgumentException in .NET, the trap ArgumentOutOfRange in the IR"),
      new(type, "TryCopyTo", [2], "ViewTryCopy", "false and nothing copied for a shorter destination"),
      new(type, "ToArray", [1], "ViewToArray")
    ]),
    new(Span, "Fill", [2], "ViewFill"),
    new(Span, "Clear", [1], "ViewFill", "with zero"),
    new(Extensions, "CopyTo", [2], "ViewCopy", "of an array into a span"),
    new(Extensions, "SequenceEqual", [2], "ViewEqual", "elements compared as IEquatable<T>.Equals: a NaN equals a NaN, +0.0 equals -0.0"),
    new(Marshal, "Cast", [1], "Reinterpret", "between number types; little-endian, the length truncated to whole elements"),
    new(Marshal, "AsBytes", [1], "Reinterpret", "to bytes"),
    new(Marshal, "Read", [1], "LoadLittle", "of a number type of more than one byte; a value outside the span traps with ArgumentOutOfRange"),
    new(Marshal, "Write", [2], "StoreLittle", "of a number type of more than one byte, the value free of traps and effects")
  ];

  /// <summary>The syntax of views the frontend imports, each with the construct of the IR it becomes.</summary>
  public static IReadOnlyList<(string Construct, string Ir)> Syntax { get; } = [
    ("Span<T>, ReadOnlySpan<T> of sbyte, byte, short, ushort, int, uint, long, ulong, float, double", "IrType.Span, IrType.ReadOnlySpan"),
    ("implicit conversion of an array to a span, of a span to a read-only span", "ViewOf"),
    ("new Span<T>(array), new Span<T>(array, start, length)", "ViewOf"),
    ("view[index]", "IrArrayLoad, IrStore"),
    ("view[^n]", "IrArrayLoad at ViewLength - n"),
    ("view.Length", "ViewLength"),
    ("view.IsEmpty", "ViewLength == 0"),
    ("view[a..b], [..b], [a..], [^n..], [a..^n]", "Slice"),
    ("array[a..b]", "ViewToArray of a Slice: a copy"),
    ("foreach over a view", "a counted loop"),
    ("stackalloc T[n] into a span", "StackAlloc"),
    ("BinaryPrimitives.Read…/Write… on any span of bytes", "LoadLittle, LoadBig, StoreLittle, StoreBig")
  ];

  /// <summary>The constructs of views the frontend refuses with <c>PLY1002</c>, each with the text the diagnostic names.</summary>
  public static IReadOnlyList<(string Construct, string Named)> Diagnosed { get; } = [
    ("MemoryMarshal.GetReference", "MemoryMarshal.GetReference"),
    ("MemoryMarshal.CreateSpan", "MemoryMarshal.CreateSpan"),
    ("MemoryMarshal.CreateReadOnlySpan", "MemoryMarshal.CreateReadOnlySpan"),
    ("Span<T> of a type that is not a number", "System.Span<"),
    ("Span<char>", "System.Span<char>"),
    ("a ref local", "MemoryMarshal.GetReference"),
    ("stackalloc with an initializer", "stackalloc with an initializer"),
    ("a member of the span types the table does not list, such as MemoryExtensions.Reverse", "MemoryExtensions.Reverse")
  ];

  private static readonly Dictionary<(string, string), CSharpViewMember> _byName = Members.ToDictionary(member => (member.Type, member.Member));

  /// <summary>The entry of a member called with a number of arguments, or <see langword="null"/> when the table has none.</summary>
  /// <param name="type">The declaring type as C# displays its definition.</param>
  /// <param name="member">The name of the member.</param>
  /// <param name="arguments">The number of arguments, the receiver counted.</param>
  /// <returns>The entry.</returns>
  public static CSharpViewMember? Find(string type, string member, int arguments)
    => _byName.GetValueOrDefault((type, member)) is { } found && found.Arguments.Contains(arguments) ? found : null;

}
