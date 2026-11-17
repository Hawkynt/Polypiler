using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The canonical kinds of failure every language's failures map onto (<c>spec/ir/traps.yaml</c>, #115).
/// A trap has exactly one kind; its label (<see cref="TrapLabel"/>) names it as the source language does.
/// </summary>
public enum TrapKind {

  /// <summary>An integer result outside its type: a checked operation or conversion, or a negative length.</summary>
  Overflow,

  /// <summary>A division or remainder of integers by zero.</summary>
  DivideByZero,

  /// <summary>An index, a key or a range outside what it selects from.</summary>
  IndexOutOfRange,

  /// <summary>Two sequences that must have the same length do not.</summary>
  LengthMismatch,

  /// <summary>The first, last, smallest or only element of a sequence that has none.</summary>
  EmptySequence,

  /// <summary>Two arrays of several dimensions that must have the same shape do not.</summary>
  ShapeMismatch,

  /// <summary>A copy between ranges that overlap where the operation forbids it.</summary>
  OverlappingCopy,

  /// <summary>A reference that names nothing is followed, or an empty optional value is unwrapped.</summary>
  NullReference,

  /// <summary>A reference converted to a type its object does not have.</summary>
  InvalidCast,

  /// <summary>An operation not valid in the current state of its operands.</summary>
  InvalidOperation,

  /// <summary>An assertion of the source is false.</summary>
  AssertionFailed,

  /// <summary>A precondition, postcondition or invariant the source declares is broken.</summary>
  ContractViolation,

  /// <summary>An allocation that cannot be satisfied.</summary>
  OutOfMemory,

  /// <summary>Calls nested deeper than the stack holds.</summary>
  StackOverflow,

  /// <summary>Code the source declares unreachable is reached.</summary>
  Unreachable,

  /// <summary>A failure the program raises itself with no more specific kind: Rust's <c>panic!</c>, C's <c>abort</c>.</summary>
  User

}

/// <summary>
/// What a trap of each kind is, as <c>spec/ir/traps.yaml</c> states it: the kind of each trap the
/// operations of the IR raise (<see cref="IrTrap"/>), the exception class whose object a trap of a kind
/// throws, its message, and the kinds an operation can fail with itself.
/// </summary>
public static class TrapKinds {

  /// <summary>Every kind, in the order of <c>spec/ir/traps.yaml</c>.</summary>
  public static IReadOnlyList<TrapKind> All { get; } = Enum.GetValues<TrapKind>();

  /// <summary>Every kind but none: what an operation that can fail with anything may raise.</summary>
  private static readonly IReadOnlySet<TrapKind> _every = new HashSet<TrapKind>(All);

  private static readonly IReadOnlySet<TrapKind> _nothing = new HashSet<TrapKind>();

  /// <summary>The kind of a trap an operation of the IR raises.</summary>
  /// <param name="trap">The trap.</param>
  /// <returns>Its kind; a missing key or value and a range outside its source are <see cref="TrapKind.IndexOutOfRange"/>, a missing optional value <see cref="TrapKind.NullReference"/>.</returns>
  public static TrapKind Of(IrTrap trap) => trap switch {
    IrTrap.DivideByZero => TrapKind.DivideByZero,
    IrTrap.IntegerOverflow => TrapKind.Overflow,
    IrTrap.IndexOutOfRange or IrTrap.ArgumentOutOfRange or IrTrap.KeyNotFound => TrapKind.IndexOutOfRange,
    IrTrap.NullReference or IrTrap.MissingValue => TrapKind.NullReference,
    IrTrap.InvalidCast => TrapKind.InvalidCast,
    IrTrap.InvalidOperation or IrTrap.DuplicateKey or IrTrap.CollectionModified or IrTrap.InvalidFormat or IrTrap.UnknownName => TrapKind.InvalidOperation,
    _ => throw new ArgumentOutOfRangeException(nameof(trap), trap, "The trap has no kind.")
  };

  /// <summary>
  /// The trap of the IR an explicit trap of a kind raises, so that it fails exactly as the operation
  /// would: <see langword="null"/> for a kind no operation of the IR raises with a class of its own.
  /// </summary>
  /// <param name="kind">The kind.</param>
  /// <returns>The trap, or <see langword="null"/>.</returns>
  public static IrTrap? Canonical(TrapKind kind) => kind switch {
    TrapKind.Overflow => IrTrap.IntegerOverflow,
    TrapKind.DivideByZero => IrTrap.DivideByZero,
    TrapKind.IndexOutOfRange => IrTrap.IndexOutOfRange,
    TrapKind.NullReference => IrTrap.NullReference,
    TrapKind.InvalidCast => IrTrap.InvalidCast,
    _ => null
  };

  /// <summary>The library class whose object an explicit trap of a kind throws, as <c>spec/ir/traps.yaml</c> gives it.</summary>
  /// <param name="kind">The kind.</param>
  /// <returns>The class.</returns>
  public static IrClassType Class(TrapKind kind) => Canonical(kind) is { } trap ? IrExceptionClass.Of(trap) : kind switch {
    TrapKind.LengthMismatch or TrapKind.ShapeMismatch or TrapKind.OverlappingCopy => IrExceptionClass.ArgumentException,
    TrapKind.EmptySequence or TrapKind.InvalidOperation or TrapKind.ContractViolation or TrapKind.Unreachable => IrExceptionClass.InvalidOperationException,
    TrapKind.AssertionFailed => IrExceptionClass.AssertionException,
    _ => IrExceptionClass.Exception
  };

  /// <summary>The message of an explicit trap of a kind: that of its trap of the IR, or its own.</summary>
  /// <param name="kind">The kind.</param>
  /// <returns>The message, for example <c>empty sequence</c>.</returns>
  public static string Message(TrapKind kind) => Canonical(kind) is { } trap ? IrTrapException.Describe(trap) : kind switch {
    TrapKind.LengthMismatch => "length mismatch",
    TrapKind.EmptySequence => "empty sequence",
    TrapKind.ShapeMismatch => "shape mismatch",
    TrapKind.OverlappingCopy => "overlapping copy",
    TrapKind.InvalidOperation => "invalid operation",
    TrapKind.AssertionFailed => "assertion failed",
    TrapKind.ContractViolation => "contract violation",
    TrapKind.OutOfMemory => "out of memory",
    TrapKind.StackOverflow => "stack overflow",
    TrapKind.Unreachable => "unreachable code reached",
    _ => "user trap"
  };

  /// <summary>
  /// The kinds an operation can fail with itself, its operands aside - what a trap attribute on it may
  /// name (<see cref="IrTrapAttribute"/>). An operation of an intrinsic family or of a collection may
  /// fail with any kind until its family declares its own; a call fails with what its callee raises,
  /// under the callee's labels, so it carries no attribute.
  /// </summary>
  /// <param name="operation">The operation.</param>
  /// <returns>The kinds; empty for an operation that cannot fail.</returns>
  public static IReadOnlySet<TrapKind> Possible(IrExpr operation) {
    ArgumentNullException.ThrowIfNull(operation);
    return operation switch {
      IrBinary binary when IrOperators.Traps(binary.Op) => Kinds(TrapKind.Overflow),
      IrBinary { Op: IrBinaryOp.Divide or IrBinaryOp.Remainder } binary when binary.Left.Type.IsInteger => Kinds(TrapKind.DivideByZero, TrapKind.Overflow),
      IrBinary { Op: IrBinaryOp.Divide or IrBinaryOp.Remainder } binary when binary.Left.Type.IsBigInt => Kinds(TrapKind.DivideByZero),
      IrUnary { Op: IrUnaryOp.NegateChecked } => Kinds(TrapKind.Overflow),
      IrConvert { Contract: IrConversion.Trap } => Kinds(TrapKind.Overflow),
      IrStep { Trap: true } => Kinds(TrapKind.Overflow),
      IrArrayLoad => Kinds(TrapKind.IndexOutOfRange, TrapKind.NullReference),
      IrArrayLength => Kinds(TrapKind.NullReference),
      IrFieldLoad load when load.Target.Type.IsObject => Kinds(TrapKind.NullReference),
      IrNewArray => Kinds(TrapKind.Overflow, TrapKind.OutOfMemory),
      IrUnwrap => Kinds(TrapKind.NullReference),
      IrCast => Kinds(TrapKind.InvalidCast),
      IrIntrinsic intrinsic when !IrPointers.IsPointerNode(intrinsic) => _every,
      IrCollection => _every,
      _ => _nothing
    };
  }

  private static HashSet<TrapKind> Kinds(params TrapKind[] kinds) => [.. kinds];

  /// <summary>Whether a text is the name of a kind.</summary>
  /// <param name="name">The text.</param>
  /// <returns><see langword="true"/> for one of the names of <see cref="All"/>, spelled exactly.</returns>
  public static bool IsKindName(string name) => All.Any(kind => kind.ToString() == name);

}
