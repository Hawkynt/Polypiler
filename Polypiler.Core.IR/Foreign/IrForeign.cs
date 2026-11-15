using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>How the source declared a foreign function (#1332).</summary>
public enum IrForeignOrigin {

  /// <summary><c>[DllImport]</c> on a <c>static extern</c> method: the runtime marshals at the call.</summary>
  DllImport,

  /// <summary><c>[LibraryImport]</c> on a <c>static partial</c> method: the interop source generator writes the marshalling.</summary>
  LibraryImport

}

/// <summary>The calling convention a foreign declaration names; <see cref="Default"/> when it names none.</summary>
public enum IrCallingConvention {

  /// <summary>Not given: the platform default (<c>Winapi</c>).</summary>
  Default,

  /// <summary><c>CallingConvention.Winapi</c>.</summary>
  Winapi,

  /// <summary><c>CallingConvention.Cdecl</c>.</summary>
  Cdecl,

  /// <summary><c>CallingConvention.StdCall</c>.</summary>
  StdCall,

  /// <summary><c>CallingConvention.ThisCall</c>.</summary>
  ThisCall,

  /// <summary><c>CallingConvention.FastCall</c>.</summary>
  FastCall

}

/// <summary>How a string crosses the boundary.</summary>
public enum IrStringEncoding {

  /// <summary>
  /// The platform's narrow encoding: UTF-8 on .NET on Linux and macOS, the system code page on Windows. The IR never
  /// rewrites it into another one.
  /// </summary>
  Ansi,

  /// <summary>UTF-16.</summary>
  Unicode,

  /// <summary>UTF-8 everywhere.</summary>
  Utf8

}

/// <summary>What crosses the boundary for one parameter or the result of a foreign function.</summary>
public enum IrMarshalKind {

  /// <summary>A number the native side reads as it is: an integer, a floating-point number; with <see cref="IrMarshal.NativeSize"/> one of the size of a pointer.</summary>
  Blittable,

  /// <summary>A truth value: a 4-byte <c>BOOL</c> by default, one byte with <c>UnmanagedType.U1</c> or <c>I1</c>.</summary>
  Bool,

  /// <summary>A string handed in, zero-terminated, in the <see cref="IrMarshal.Encoding"/>; never written back.</summary>
  String,

  /// <summary>An array of numbers, the native side seeing a pointer to its first element.</summary>
  Array,

  /// <summary>A number or a struct passed by reference: the IR passes a one-element array, the native side sees a pointer to it.</summary>
  ByRef,

  /// <summary>A struct passed or returned by value, laid out as its <see cref="IrStructType.Layout"/> says.</summary>
  Struct,

  /// <summary>An <c>IntPtr</c> or <c>nint</c> the IR carries as an <c>i64</c>: opaque, never dereferenced.</summary>
  Handle

}

/// <summary>Which way the data of an array or a reference goes.</summary>
public enum IrMarshalDirection {

  /// <summary>Into the native function only.</summary>
  In,

  /// <summary>Out of it only.</summary>
  Out,

  /// <summary>Both ways.</summary>
  InOut

}

/// <summary>The form of a truth value at the boundary.</summary>
public enum IrBoolForm {

  /// <summary>The 4-byte <c>BOOL</c>, the default; <c>UnmanagedType.Bool</c> where the source names it.</summary>
  Bool,

  /// <summary>One byte, <c>UnmanagedType.U1</c>.</summary>
  U1,

  /// <summary>One byte, <c>UnmanagedType.I1</c>.</summary>
  I1

}

/// <summary>The marshalling of one parameter or of the result of a foreign function.</summary>
/// <param name="Kind">What crosses the boundary.</param>
/// <param name="Direction">Which way it goes; <see cref="IrMarshalDirection.In"/> for everything but arrays and references.</param>
public sealed record IrMarshal(IrMarshalKind Kind, IrMarshalDirection Direction = IrMarshalDirection.In) {

  /// <summary>The encoding of a <see cref="IrMarshalKind.String"/>.</summary>
  public IrStringEncoding Encoding { get; init; }

  /// <summary>Whether a number, or the element of a reference, is an integer of the size of a pointer: <c>nint</c>, <c>nuint</c>, <c>IntPtr</c>, <c>UIntPtr</c>.</summary>
  public bool NativeSize { get; init; }

  /// <summary>The form of a <see cref="IrMarshalKind.Bool"/>.</summary>
  public IrBoolForm BoolForm { get; init; }

  /// <summary>Whether the source wrote <c>[MarshalAs]</c> for it, which a C# backend writes again.</summary>
  public bool Attributed { get; init; }

  /// <summary>Whether the source wrote <c>[In]</c> or <c>[Out]</c> on an array, which a C# backend writes again.</summary>
  public bool Directed { get; init; }

  /// <summary>A blittable number, a new object every time.</summary>
  public static IrMarshal Blittable => new(IrMarshalKind.Blittable);

}

/// <summary>A parameter of a foreign function.</summary>
/// <param name="Name">The name as the source wrote it.</param>
/// <param name="Type">
/// Its type in the IR: a number, a truth value, a string, an array, a struct; for a <see cref="IrMarshalKind.ByRef"/> the
/// one-element array of the referenced type, for a <see cref="IrMarshalKind.Handle"/> or a number of
/// <see cref="IrMarshal.NativeSize"/> an <c>i64</c> or <c>u64</c>.
/// </param>
/// <param name="Marshal">What crosses the boundary.</param>
public sealed record IrForeignParameter(string Name, IrType Type, IrMarshal Marshal);

/// <summary>
/// A function of a native library (#1332): its identity - the library, the symbol, the calling convention, the
/// character set - and the marshalling of every parameter and of the result. The IR has no body for it; a call of it
/// is an <see cref="IrForeignCall"/>.
/// </summary>
/// <param name="Name">The name of the declaration in the module, unique among its functions.</param>
/// <param name="IsPublic">Whether it is visible outside the module.</param>
/// <param name="Library">The library as the source names it, copied verbatim (<c>libc.so.6</c>, <c>msvcrt</c>): no name is mapped between operating systems.</param>
/// <param name="ReturnType">The type of the result, <see cref="IrType.Void"/> for none.</param>
/// <param name="Parameters">The parameters, in call order.</param>
public sealed record IrForeignFunction(string Name, bool IsPublic, string Library, IrType ReturnType, IReadOnlyList<IrForeignParameter> Parameters) {

  /// <summary>How the source declared it.</summary>
  public IrForeignOrigin Origin { get; init; }

  /// <summary>The <c>EntryPoint</c> the source wrote, or <see langword="null"/> when the symbol is the name.</summary>
  public string? EntryPoint { get; init; }

  /// <summary>The symbol of the native function: the entry point, or else the name.</summary>
  public string Symbol => this.EntryPoint ?? this.Name;

  /// <summary>The calling convention the source wrote.</summary>
  public IrCallingConvention Convention { get; init; }

  /// <summary>The encoding <c>CharSet</c> or <c>StringMarshalling</c> gave the strings, or <see langword="null"/> when the source wrote neither.</summary>
  public IrStringEncoding? CharSet { get; init; }

  /// <summary>Whether the runtime keeps the error the native function set (<c>SetLastError = true</c>); <see langword="null"/> when the source did not say.</summary>
  public bool? SetLastError { get; init; }

  /// <summary>Whether the symbol is looked up as written only (<c>ExactSpelling</c>); <see langword="null"/> when the source did not say.</summary>
  public bool? ExactSpelling { get; init; }

  /// <summary>The marshalling of the result; <see cref="IrMarshal.Blittable"/> for none.</summary>
  public IrMarshal Result { get; init; } = IrMarshal.Blittable;

  /// <summary>The types of the parameters, in order.</summary>
  public IReadOnlyList<IrType> ParameterTypes => [.. this.Parameters.Select(parameter => parameter.Type)];

}

/// <summary>
/// A call of a foreign function of the module (#1332). It has the unknown external effect of #73: it is evaluated
/// exactly once where it stands, after its arguments left to right, nothing is moved across it, nothing folds it, and
/// the interpreter never runs it - it traps <see cref="IrTrap.InvalidOperation"/>.
/// </summary>
/// <param name="Function">The name of the declaration among <see cref="IrModule.ForeignFunctions"/>.</param>
/// <param name="Type">The type of the result.</param>
/// <param name="Arguments">The arguments, one per parameter, each of its type.</param>
public sealed record IrForeignCall(string Function, IrType Type, IReadOnlyList<IrExpr> Arguments) : IrExpr(Type);

/// <summary>
/// The error the last foreign call that keeps it set, as an <c>i32</c>: <c>Marshal.GetLastWin32Error()</c>, or
/// <c>Marshal.GetLastPInvokeError()</c> where the source called that. It reads state a foreign call writes, so it is
/// no more movable than one.
/// </summary>
/// <param name="PInvoke">Whether the source called <c>GetLastPInvokeError</c>.</param>
public sealed record IrLastError(bool PInvoke) : IrExpr(IrType.I32);

/// <summary>How a struct crossing the boundary is laid out.</summary>
public enum IrLayoutKind {

  /// <summary>The fields in declaration order, each aligned (<c>LayoutKind.Sequential</c>).</summary>
  Sequential,

  /// <summary>Every field at the offset the declaration states (<c>LayoutKind.Explicit</c>).</summary>
  Explicit

}

/// <summary>The layout of a struct as <c>[StructLayout]</c> and <c>[FieldOffset]</c> state it.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Pack">The packing, 0 for the default.</param>
/// <param name="Size">The size in bytes, 0 for the size of the fields.</param>
/// <param name="Offsets">For <see cref="IrLayoutKind.Explicit"/> the offset of every field, in field order; empty otherwise.</param>
public sealed record IrStructLayout(IrLayoutKind Kind, int Pack, int Size, IReadOnlyList<int> Offsets) {

  /// <summary>Whether two layouts state the same.</summary>
  /// <param name="other">The other layout.</param>
  /// <returns><see langword="true"/> for the same kind, packing, size and offsets.</returns>
  public bool Equals(IrStructLayout? other)
    => other is not null && this.Kind == other.Kind && this.Pack == other.Pack && this.Size == other.Size && this.Offsets.SequenceEqual(other.Offsets);

  /// <inheritdoc/>
  public override int GetHashCode() => System.HashCode.Combine(this.Kind, this.Pack, this.Size, this.Offsets.Count);

}

/// <summary>The rules of foreign functions and calls (#1332), for the verifier and every consumer of the IR.</summary>
public static class IrForeign {

  /// <summary>What the interpreter traps with for a foreign call it has no binding for.</summary>
  public const string Trap = "foreign call";

  /// <summary>
  /// What is wrong with a declaration: a parameter or result whose type does not fit its marshalling - a blittable
  /// number, a truth value, a string handed in, an array of numbers, a one-element array of a number or a struct by
  /// reference, a struct by value, a handle as an <c>i64</c>.
  /// </summary>
  /// <param name="function">The declaration.</param>
  /// <returns>The problems, empty when it is well-formed.</returns>
  public static IReadOnlyList<string> Problems(IrForeignFunction function) {
    var problems = new List<string>();
    foreach (var parameter in function.Parameters)
      if (!Fits(parameter.Type, parameter.Marshal, false))
        problems.Add($"the parameter '{parameter.Name}' of the type {parameter.Type} is marshalled as {Describe(parameter.Marshal)}");

    if (function.ReturnType != IrType.Void && !Fits(function.ReturnType, function.Result, true))
      problems.Add($"the result of the type {function.ReturnType} is marshalled as {Describe(function.Result)}");

    if (function.Library.Length == 0)
      problems.Add("the library has no name");

    return problems;
  }

  /// <summary>The marshalling as the verifier names it, such as <c>ByRef(Out)</c>.</summary>
  /// <param name="marshal">The marshalling.</param>
  /// <returns>The name.</returns>
  public static string Describe(IrMarshal marshal)
    => marshal.Kind is IrMarshalKind.Array or IrMarshalKind.ByRef ? $"{marshal.Kind}({marshal.Direction})"
      : marshal.Kind == IrMarshalKind.String ? $"String({marshal.Encoding})"
      : marshal.Kind.ToString();

  /// <summary>Whether a type fits a marshalling; a string, an array and a reference are never a result.</summary>
  private static bool Fits(IrType type, IrMarshal marshal, bool result) => marshal.Kind switch {
    IrMarshalKind.Blittable => (type.IsNumeric && (!marshal.NativeSize || type.IsLong)) || (type.IsPointer && !result && !marshal.NativeSize),
    IrMarshalKind.Bool => type == IrType.Bool,
    IrMarshalKind.String => !result && type.IsString && marshal.Direction == IrMarshalDirection.In,
    IrMarshalKind.Array => !result && type.IsArray && type.Element!.IsNumeric,
    IrMarshalKind.ByRef => !result && type.IsArray && (type.Element!.IsNumeric || type.Element.IsStruct) && (!marshal.NativeSize || type.Element == IrType.I64 || type.Element == IrType.U64),
    IrMarshalKind.Struct => type.IsStruct,
    _ => type == IrType.I64
  };

}
