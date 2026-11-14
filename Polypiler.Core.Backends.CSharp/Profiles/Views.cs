using System;
using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The rows of the capability table for the views of the IR (#1325). <c>Span&lt;T&gt;</c>,
/// <c>ReadOnlySpan&lt;T&gt;</c>, <c>MemoryExtensions</c>, <c>MemoryMarshal</c> and <c>BinaryPrimitives</c>
/// are part of .NET Standard 2.1 and absent from its 2.0 (the reference assemblies, checked on
/// 2026-11-14); below it a view is an <c>ArraySegment&lt;T&gt;</c>, which .NET Framework 2.0 has, and every
/// operation on it is a helper of <see cref="CSharpViewHelpers"/>. No operation is without a form in any
/// profile.
/// </summary>
internal static class CSharpViewRows {

  private const string Memory = "global::System.MemoryExtensions.";

  private static readonly IrTypeKind[] _any = [];

  /// <summary>Every row of the views: the language, the types, then one row per operation.</summary>
  public static IEnumerable<CSharpCapability> All => [
    CSharpCapability.OfLanguage("stackalloc in expression", CSharpFeature.StackAllocExpression, new Version(8, 0)),
    CSharpCapability.OfLanguage("range and index", CSharpFeature.Ranges, new Version(8, 0)),
    CSharpCapability.OfApi("Span<T>", CSharpProfile.NetStandard21),
    CSharpCapability.OfApi("Memory<T>", CSharpProfile.NetStandard21),
    CSharpCapability.OfApi("MemoryExtensions.SequenceEqual", CSharpProfile.NetStandard21),
    CSharpCapability.OfApi("MemoryMarshal.Cast", CSharpProfile.NetStandard21),
    CSharpCapability.OfApi("System.Index", CSharpProfile.NetStandard21),
    CSharpCapability.OfApi("System.Range", CSharpProfile.NetStandard21),
    CSharpCapability.OfApi("ArraySegment<T>", CSharpProfile.Net20),
    Row("MemoryExtensions.AsSpan", IrIntrinsicOp.ViewOf, Memory + "AsSpan", CSharpViewHelpers.ViewOf),
    Row("Span<T>.Slice", IrIntrinsicOp.Slice, "Slice", CSharpViewHelpers.Slice),
    Row("Span<T>.Length", IrIntrinsicOp.ViewLength, "Length", null),
    Row("stackalloc T[n] into a span", IrIntrinsicOp.StackAlloc, "stackalloc", null),
    Row("Span<T>.CopyTo", IrIntrinsicOp.ViewCopy, "CopyTo", CSharpViewHelpers.ViewCopy),
    Row("Span<T>.TryCopyTo", IrIntrinsicOp.ViewTryCopy, "TryCopyTo", CSharpViewHelpers.ViewTryCopy),
    Row("Span<T>.Fill", IrIntrinsicOp.ViewFill, "Fill", CSharpViewHelpers.ViewFill),
    Row("MemoryExtensions.SequenceEqual of two views", IrIntrinsicOp.ViewEqual, Memory + "SequenceEqual", CSharpViewHelpers.ViewEqual),
    Row("Span<T>.ToArray", IrIntrinsicOp.ViewToArray, "ToArray", CSharpViewHelpers.ViewToArray),
    Row("MemoryMarshal.Cast<TFrom, TTo>", IrIntrinsicOp.Reinterpret, "global::System.Runtime.InteropServices.MemoryMarshal.Cast", CSharpViewHelpers.ViewCast)
  ];

  /// <summary>
  /// The helpers a view may need besides those of its rows: element access on a segment and the endian
  /// loads and stores on a view, which share the rows of the arrays.
  /// </summary>
  public static IEnumerable<CSharpCapability> Helpers => [
    CSharpCapability.OfApi("ArraySegment<T> from an index on", CSharpProfile.Net20, CSharpViewHelpers.ViewFrom),
    CSharpCapability.OfApi("ArraySegment<T> sliced from an index on", CSharpProfile.Net20, CSharpViewHelpers.SliceFrom),
    CSharpCapability.OfApi("ArraySegment<T> element read", CSharpProfile.Net20, CSharpViewHelpers.ViewGet),
    CSharpCapability.OfApi("ArraySegment<T> element written", CSharpProfile.Net20, CSharpViewHelpers.ViewSet),
    CSharpCapability.OfApi("ArraySegment<T> element place", CSharpProfile.Net20, CSharpViewHelpers.ViewIndex),
    CSharpCapability.OfApi("BinaryPrimitives.Read… on an ArraySegment<byte>", CSharpProfile.Net20, CSharpViewHelpers.LoadView),
    CSharpCapability.OfApi("BinaryPrimitives.Write… on an ArraySegment<byte>", CSharpProfile.Net20, CSharpViewHelpers.StoreView),
    CSharpCapability.OfApi("BinaryPrimitives.Write… on a span, the value evaluated first", CSharpProfile.NetStandard21, CSharpViewHelpers.StoreSpan)
  ];

  /// <summary>An operation the profiles with spans write as the member of a span or <c>MemoryExtensions</c>, and the others as a helper or a form of <c>ArraySegment&lt;T&gt;</c>.</summary>
  private static CSharpCapability Row(string name, IrIntrinsicOp operation, string member, CSharpHelper? helper)
    => CSharpCapability.OfOperation(name, CSharpProfile.NetStandard21, operation, _any, new CSharpForm(member), new CSharpForm(helper?.Name ?? "ArraySegment<T>"), helper);

}
