using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The rows of the capability table for the integer and bit operations of the IR, one per operation
/// and width, each family in a file of its own. A row whose operation the base library has names
/// the first profile with it and writes that call there; every other profile, and every operation
/// no framework has, gets a helper that the class declares only when it calls it. A form is a
/// template: the casts between a type and the unsigned type of its width, or the 64 bits a helper
/// works on, keep the low bits, which the emitter guarantees by writing it where overflow is not
/// checked.
/// </summary>
internal static partial class CSharpIntegerRows {

  private const string Bits = "global::System.Numerics.BitOperations.";
  private const string Binary = "global::System.Buffers.Binary.BinaryPrimitives.";
  private const string Math = "global::System.Math.";

  private static readonly IrTypeKind[] _all = [IrTypeKind.I8, IrTypeKind.U8, IrTypeKind.I16, IrTypeKind.U16, IrTypeKind.I32, IrTypeKind.U32, IrTypeKind.I64, IrTypeKind.U64];
  private static readonly IrTypeKind[] _narrow = [IrTypeKind.I8, IrTypeKind.U8, IrTypeKind.I16, IrTypeKind.U16];
  private static readonly IrTypeKind[] _unsigned = [IrTypeKind.U8, IrTypeKind.U16, IrTypeKind.U32, IrTypeKind.U64];
  private static readonly IrTypeKind[] _multibyte = [IrTypeKind.I16, IrTypeKind.U16, IrTypeKind.I32, IrTypeKind.U32, IrTypeKind.I64, IrTypeKind.U64];

  /// <summary>Every row, family by family.</summary>
  public static IEnumerable<CSharpCapability> All => [
    .. Rotate, .. Counts, .. Reordering, .. PowersOfTwo, .. WideMultiply, .. Division, .. Bounds,
    .. Saturating, .. Carry, .. Overflow, .. BitFields, .. Masks, .. EndianAccess
  ];

  /// <summary>The keyword of an integer type, or of a floating-point type an array of bytes is read as.</summary>
  private static string Type(IrTypeKind kind) => kind switch {
    IrTypeKind.F32 => "float",
    IrTypeKind.F64 => "double",
    IrTypeKind.I8 => "sbyte",
    IrTypeKind.U8 => "byte",
    IrTypeKind.I16 => "short",
    IrTypeKind.U16 => "ushort",
    IrTypeKind.I32 => "int",
    IrTypeKind.U32 => "uint",
    IrTypeKind.I64 => "long",
    _ => "ulong"
  };

  /// <summary>The keyword of the unsigned type of the width of a signed integer type.</summary>
  private static string Unsigned(IrTypeKind kind) => kind switch {
    IrTypeKind.I8 => "byte",
    IrTypeKind.I16 => "ushort",
    IrTypeKind.I32 => "uint",
    _ => "ulong"
  };

  private static bool IsSigned(IrTypeKind kind) => kind is IrTypeKind.I8 or IrTypeKind.I16 or IrTypeKind.I32 or IrTypeKind.I64;

  private static int Width(IrTypeKind kind) => kind switch {
    IrTypeKind.I8 or IrTypeKind.U8 => 8,
    IrTypeKind.I16 or IrTypeKind.U16 => 16,
    IrTypeKind.I32 or IrTypeKind.U32 => 32,
    _ => 64
  };

  private static string Digits(int number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture);

  /// <summary>The bit pattern of an argument as a <c>ulong</c>: a signed value through the unsigned type of its width, so that it is not sign-extended.</summary>
  private static string Pattern(IrTypeKind kind, string argument = "{0}")
    => IsSigned(kind) ? $"(ulong)({Unsigned(kind)}){argument}" : $"(ulong){argument}";

  /// <summary>The bit pattern of an argument as the 32 or 64 bits <c>BitOperations</c> takes.</summary>
  private static string Word(IrTypeKind kind, string argument = "{0}")
    => Width(kind) == 64 ? $"(ulong){argument}" : IsSigned(kind) ? $"(uint)({Unsigned(kind)}){argument}" : $"(uint){argument}";

  /// <summary>
  /// A row: from <paramref name="since"/> on the template <paramref name="native"/> (a call of
  /// <paramref name="method"/>, when that is not <see langword="null"/>), before it the template
  /// <paramref name="replacement"/>, which calls the helper. A row without a profile has no
  /// native form and is its replacement everywhere; a row whose native form exists in every
  /// profile has none.
  /// </summary>
  private static CSharpCapability Row(
    IrIntrinsicOp op, IrTypeKind kind, CSharpProfile? since, string? method, string? native, string? replacement = null, CSharpHelper? helper = null)
    => CSharpCapability.OfOperation(
      $"{op} of {Type(kind)}", since, op, [kind],
      native is null ? null : new CSharpForm(method, Template: native),
      replacement is null ? null : new CSharpForm(null, Template: replacement),
      helper);

  /// <summary>A row that is a helper in every profile.</summary>
  private static CSharpCapability Helped(IrIntrinsicOp op, IrTypeKind kind, string replacement, CSharpHelper helper)
    => Row(op, kind, null, null, null, replacement, helper);

  /// <summary>A row that C# writes with its operators in every profile.</summary>
  private static CSharpCapability Written(IrIntrinsicOp op, IrTypeKind kind, string native)
    => Row(op, kind, CSharpProfile.Net20, null, native);

}
