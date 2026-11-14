using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// One member of the base library the C# frontend imports as an integer or bit operation of the IR.
/// </summary>
/// <param name="Type">The type that declares the member as C# displays it: <c>int</c>, <c>System.Math</c>, <c>System.Numerics.BitOperations</c>.</param>
/// <param name="Member">The name of the member.</param>
/// <param name="Ir">
/// The constructs of the IR it becomes: intrinsic operations by name, or <c>Divide</c>, <c>Remainder</c>,
/// <c>Multiply</c>, <c>Convert</c>, separated by commas; forms of the member that become different
/// constructs are separated by <c>|</c>. A conversion to the type a value already has is none.
/// </param>
/// <param name="Note">What happens at its edges where that is not plain: a trap the source throws differently, a form of an argument.</param>
public sealed record CSharpIntegerMember(string Type, string Member, string Ir, string Note = "");

/// <summary>
/// The members of <c>BitOperations</c>, <c>Math</c>, <c>BinaryPrimitives</c>, <c>BitConverter</c> and
/// of the integer types that the C# frontend maps to the integer operations of the IR - the table the
/// frontend reads and the tests check. Each family lists its members in a file of its own; a member
/// of one of these types that is not listed, such as <c>BitOperations.Crc32C</c>, is refused by name.
/// An overload of a listed member is imported where the IR defines the operation on its types and
/// refused by name where it does not.
/// </summary>
public static partial class CSharpIntegerApi {

  /// <summary>The integer types of C#, as Roslyn displays them.</summary>
  private static readonly string[] _integers = ["sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong"];

  private const string Bits = "System.Numerics.BitOperations";
  private const string Math = "System.Math";
  private const string Binary = "System.Buffers.Binary.BinaryPrimitives";
  private const string Converter = "System.BitConverter";

  /// <summary>Every member the frontend maps, family by family.</summary>
  public static IReadOnlyList<CSharpIntegerMember> Members { get; } = [
    .. Rotates, .. Counts, .. Reorderings, .. Powers, .. WideMultiplies, .. Divisions, .. Bounds, .. Conversions, .. EndianAccesses
  ];

  private static readonly Dictionary<(string, string), CSharpIntegerMember> _byName = Members.ToDictionary(member => (member.Type, member.Member));

  /// <summary>The entry of a member, or <see langword="null"/> when the table has none.</summary>
  /// <param name="type">The declaring type as C# displays it.</param>
  /// <param name="member">The name of the member.</param>
  /// <returns>The entry.</returns>
  public static CSharpIntegerMember? Find(string type, string member) => _byName.GetValueOrDefault((type, member));

  /// <summary>The same member of every integer type.</summary>
  private static IEnumerable<CSharpIntegerMember> OfEveryType(string member, string ir, string note = "")
    => _integers.Select(type => new CSharpIntegerMember(type, member, ir, note));

}
