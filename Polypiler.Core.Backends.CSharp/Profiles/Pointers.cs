using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The rows of the capability table for the bounded pointers of the IR (#1331). Unsafe code is part of C# since
/// version 1 (ECMA-334, "Unsafe code"), and so is <c>stackalloc</c> into a pointer: every profile from .NET
/// Framework 2.0 to .NET 10 writes pointers as pointers, <c>fixed</c> as <c>fixed</c>, in one form. The two
/// language rows let a future profile without unsafe code refuse a module with pointers
/// (<see cref="CSharpEmitter.Inexpressible"/>) and one without <c>stackalloc</c> into a pointer pin an array instead.
/// </summary>
internal static class CSharpPointerRows {

  private static readonly Version _first = new(1, 0);

  /// <summary>The form of an operation the emitter writes itself, as operators of the language: no call, nothing that casts.</summary>
  private static readonly CSharpForm _itself = new(null);

  /// <summary>Every row of the pointers: the two of the language, then one per operation, which the emitter writes itself.</summary>
  public static IEnumerable<CSharpCapability> All => [
    CSharpCapability.OfLanguage("unsafe code", CSharpFeature.UnsafeCode, _first),
    CSharpCapability.OfLanguage("stackalloc into a pointer", CSharpFeature.StackAllocPointer, _first),
    // Found before the row of the views, which is for every operand: a stackalloc into a pointer is told apart by its type.
    CSharpCapability.OfOperation("T* p = stackalloc T[n]", CSharpProfile.Net20, IrIntrinsicOp.StackAlloc, [IrTypeKind.I32], _itself).When(intrinsic => intrinsic.Type.IsPointer),
    .. IrPointers.Operations.Select(operation => CSharpCapability.OfOperation(Name(operation), CSharpProfile.Net20, operation, [], _itself))
  ];

  /// <summary>The C# a pointer operation is written as, which names its row.</summary>
  private static string Name(IrIntrinsicOp operation) => operation switch {
    IrIntrinsicOp.Pin => "fixed (T* p = a)",
    IrIntrinsicOp.AddressOf => "&x",
    IrIntrinsicOp.PointerAdd => "p + n",
    IrIntrinsicOp.PointerDifference => "p - q",
    IrIntrinsicOp.PointerEqual => "p == q",
    IrIntrinsicOp.PointerNotEqual => "p != q",
    IrIntrinsicOp.PointerLess => "p < q",
    IrIntrinsicOp.PointerLessOrEqual => "p <= q",
    IrIntrinsicOp.PointerGreater => "p > q",
    IrIntrinsicOp.PointerGreaterOrEqual => "p >= q",
    IrIntrinsicOp.PointerLoad => "*p",
    IrIntrinsicOp.PointerStore => "*p = v",
    _ => "(U*)p"
  };

}
