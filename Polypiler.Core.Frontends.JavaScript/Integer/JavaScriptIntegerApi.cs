using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>One function or method of ECMAScript's library the JavaScript frontend imports as an integer operation of the IR.</summary>
/// <param name="Member">The function as a program calls it: <c>Math.clz32</c>, <c>BigInt.asUintN</c>, <c>DataView.prototype.getUint32</c>.</param>
/// <param name="Ir">The constructs of the IR it becomes, separated by commas; forms that become different constructs separated by <c>|</c>.</param>
/// <param name="Note">What it takes and where its edges are.</param>
public sealed record JavaScriptIntegerMember(string Member, string Ir, string Note = "");

/// <summary>
/// The functions of <c>Math</c> and <c>BigInt</c> and the methods of <c>DataView</c> that the JavaScript
/// frontend maps to the integer operations of the IR - the table the tests check. A spelled-out idiom
/// such as <c>(x &lt;&lt; n) | (x &gt;&gt;&gt; (32 - n))</c> is no member; recognising it is the work of a
/// later pass.
/// </summary>
public static class JavaScriptIntegerApi {

  private const string View = "DataView.prototype.";

  /// <summary>The type each method of <c>DataView</c> reads or writes: an integer, or a binary32 or binary64 number.</summary>
  private static readonly (string Name, IrType Type)[] _views = [
    ("Int8", IrType.I8), ("Uint8", IrType.U8), ("Int16", IrType.I16), ("Uint16", IrType.U16),
    ("Int32", IrType.I32), ("Uint32", IrType.U32), ("BigInt64", IrType.I64), ("BigUint64", IrType.U64),
    ("Float32", IrType.F32), ("Float64", IrType.F64)
  ];

  /// <summary>Every member the frontend maps.</summary>
  public static IReadOnlyList<JavaScriptIntegerMember> Members { get; } = [
    new("Math.clz32", "LeadingZeroCount", "of ToUint32 of the argument"),
    new("Math.imul", "Multiply", "of ToInt32 of both arguments, as an int32"),
    new("Math.abs", "AbsWrap, Convert | AbsWrap | Abs", "on a proven integer the magnitude, of a signed one converted to the unsigned type of its width; on any other number the floating-point one"),
    new("Math.min", "Min", "on proven integers in the smallest type that holds them all; on any other numbers the floating-point one"),
    new("Math.max", "Max", "on proven integers in the smallest type that holds them all; on any other numbers the floating-point one"),
    new("Math.sign", "Sign | Min, Max", "on a proven integer as an int32; on any other number the floating-point form"),
    new("BigInt.asUintN", "ZeroExtend | Convert", "with a literal width from 1 to 64, the argument a BigInt computed modulo 2^64; 64 is the cut to uint64 itself"),
    new("BigInt.asIntN", "SignExtend | Convert", "with a literal width from 1 to 64, the argument a BigInt computed modulo 2^64; 64 is the cut to int64 itself"),
    .. _views.Select(view => new JavaScriptIntegerMember($"{View}get{view.Name}", view.Type.Size == 1 ? "ArrayLoad" : "LoadLittle | LoadBig", Note(view.Type, true))),
    .. _views.Select(view => new JavaScriptIntegerMember($"{View}set{view.Name}", view.Type.Size == 1 ? "Store" : view.Type == IrType.F32 ? "Convert, StoreLittle | Convert, StoreBig" : "StoreLittle | StoreBig", Note(view.Type, false)))
  ];

  private const string ViewNote = "on a DataView made in the same function by new DataView(bytes.buffer) or new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength) of a Uint8Array, at a proven integer index, the byte order a literal (big-endian when it is missing); the first form is read as the view of the bytes of the array, which it is unless the array is part of a larger buffer";

  /// <summary>The note of a method of <c>DataView</c>: the conditions of every method, and for a floating-point one what becomes of the number.</summary>
  private static string Note(IrType type, bool reads) => type.Kind switch {
    IrTypeKind.F32 => ViewNote + (reads
      ? "; the binary32 number read is a double exactly"
      : "; the value is a number, rounded to binary32 to the nearest, ties to even (ECMA-262 NumericToRawBytes)"),
    IrTypeKind.F64 => ViewNote + (reads ? "; the binary64 number read is the double" : "; the value is a number, stored as it is"),
    _ => ViewNote
  };

  /// <summary>The entry of a member, or <see langword="null"/> when the table has none.</summary>
  /// <param name="member">The function as a program calls it.</param>
  /// <returns>The entry.</returns>
  public static JavaScriptIntegerMember? Find(string member) => Members.FirstOrDefault(entry => entry.Member == member);

  /// <summary>The type a method of <c>DataView</c> reads or writes, or <see langword="null"/> for a name that is none of them.</summary>
  /// <param name="method">The name of the method, for example <c>getUint32</c>.</param>
  /// <returns>The type.</returns>
  internal static IrType? ViewType(string method)
    => method.Length > 3 && method[..3] is "get" or "set" && _views.FirstOrDefault(view => view.Name == method[3..]) is { Type: { } type } ? type : null;

}
