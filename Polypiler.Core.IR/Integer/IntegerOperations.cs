using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The meaning of the integer and bit operations of the IR: one method per operation, defined on
/// every integer type the operation takes, which the interpreter calls and every backend has to
/// reproduce. Values are carried as <see cref="IrConst"/> carries them: a number within the range
/// of its type, a <c>u64</c> as its 64 bits. A count or a position is an <c>i32</c>. An operation
/// that has no result for its operands throws the <see cref="IrTrapException"/> the IR names for it.
/// <para>
/// Each family of operations - rotates, counts, reordering, powers of two, wide multiplication,
/// division, bounds, saturation, carries, overflow tests, bit fields, masks and endian access -
/// lives in a file of its own and registers its typing rules in <see cref="_families"/>.
/// </para>
/// </summary>
public static partial class IntegerOperations {

  /// <summary>The eight integer types.</summary>
  private static readonly IrType[] _integers = [IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64];

  /// <summary>The four unsigned integer types.</summary>
  private static readonly IrType[] _unsigned = [IrType.U8, IrType.U16, IrType.U32, IrType.U64];

  /// <summary>The integer types an array of bytes is read and written as.</summary>
  private static readonly IrType[] _multibyte = [IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64];

  /// <summary>An array of bytes, which the endian loads and stores work on.</summary>
  private static readonly IrType _bytes = IrType.Array(IrType.U8);

  /// <summary>
  /// Every family, one entry each; an operation stands in one family only. It is declared after the
  /// type lists, which the families read while they are registered.
  /// </summary>
  private static readonly IReadOnlyDictionary<IrIntrinsicOp, IntegerOperation> _families = Register(
    Rotate, Counts, Reordering, PowersOfTwo, WideMultiply, Division, Bounds, Saturating, Carry, Overflow, BitFields, Masks, EndianAccess);

  /// <summary>The operations of this class.</summary>
  public static IReadOnlyCollection<IrIntrinsicOp> Operations => (IReadOnlyCollection<IrIntrinsicOp>)_families.Keys;

  /// <summary>
  /// Every signature an operation of this class is defined on: the type of the node and the types of
  /// its arguments, one entry per integer width the operation takes.
  /// </summary>
  /// <param name="op">An operation of <see cref="Operations"/>.</param>
  /// <returns>The signatures, narrowest type first.</returns>
  /// <exception cref="ArgumentException">The operation is not one of this class.</exception>
  public static IReadOnlyList<(IrType Result, IReadOnlyList<IrType> Arguments)> Signatures(IrIntrinsicOp op)
    => _families.TryGetValue(op, out var operation) ? operation.Signatures : throw new ArgumentException($"{op} is not an integer operation.", nameof(op));

  /// <summary>
  /// Whether an operation of this class takes the types, or <see langword="null"/> when the class
  /// has no say: the operation is not one of it, or it is one it shares with floating-point numbers
  /// and the first argument is not an integer.
  /// </summary>
  internal static bool? Accepts(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> arguments) {
    if (!_families.TryGetValue(op, out var operation) || (operation.Shared && arguments is not [{ IsInteger: true }, ..]))
      return null;

    return operation.Signatures.Any(signature => signature.Result == result && signature.Arguments.SequenceEqual(arguments));
  }

  /// <summary>Whether the interpreter is to call this class for a node: the class accepts the node's types.</summary>
  internal static bool Evaluates(IrIntrinsic intrinsic)
    => Accepts(intrinsic.Op, intrinsic.Type, intrinsic.Arguments.Select(argument => argument.Type).ToList()) == true;

  /// <summary>Evaluates a node of a well-typed module on the values of its arguments, as the interpreter carries them.</summary>
  internal static object? Evaluate(IrIntrinsic intrinsic, object[] arguments)
    => _families[intrinsic.Op].Evaluate(intrinsic.Type, intrinsic.Arguments.Select(argument => argument.Type).ToList(), arguments);

  private static IReadOnlyDictionary<IrIntrinsicOp, IntegerOperation> Register(params IEnumerable<IntegerOperation>[] families) {
    var operations = new Dictionary<IrIntrinsicOp, IntegerOperation>();
    foreach (var operation in families.SelectMany(family => family))
      operations.Add(operation.Op, operation);

    return operations;
  }

  // ---------------------------------------------------------------- signatures

  /// <summary>An operation whose result has the type of its first argument, for every type of a list.</summary>
  private static IntegerOperation Same(IrIntrinsicOp op, IrType[] types, Func<IrType, IrType[]> arguments, Evaluation evaluate, bool shared = false)
    => new(op, [.. types.Select(type => (type, (IReadOnlyList<IrType>)arguments(type)))], evaluate, shared);

  /// <summary>An operation with a fixed type of result, for every type of a list as its first argument.</summary>
  private static IntegerOperation Fixed(IrIntrinsicOp op, IrType result, IrType[] types, Func<IrType, IrType[]> arguments, Evaluation evaluate, bool shared = false)
    => new(op, [.. types.Select(type => (result, (IReadOnlyList<IrType>)arguments(type)))], evaluate, shared);

  // ---------------------------------------------------------------- the carried values

  /// <summary>The width of an integer type in bits.</summary>
  private static int Width(IrType type) => type.Bits;

  /// <summary>The bits of a carried value: its two's complement, cut to the width of its type.</summary>
  private static ulong Pattern(IrType type, long value) => (ulong)value & Ones(type.Bits);

  /// <summary>The value of a type that has the given bits, as it is carried.</summary>
  private static long Value(IrType type, ulong pattern) => IrOperators.Wrap(type, (long)pattern);

  /// <summary>The lowest <paramref name="count"/> bits set, for 0 to 64.</summary>
  private static ulong Ones(int count) => count == 64 ? ulong.MaxValue : (1UL << count) - 1;

  /// <summary>The number a carried value stands for.</summary>
  private static Int128 Number(IrType type, long value) => IrOperators.Number(type, value);

  /// <summary>The number as a value of the type, or the overflow trap when it is none.</summary>
  private static long Fit(IrType type, Int128 number)
    => number < IrOperators.Minimum(type) || number > IrOperators.Maximum(type) ? throw new IrTrapException(IrTrap.IntegerOverflow) : (long)number;

  /// <summary>The number as a value of the type, the nearest bound when it lies outside.</summary>
  private static long Saturate(IrType type, Int128 number)
    => (long)Int128.Clamp(number, IrOperators.Minimum(type), IrOperators.Maximum(type));

  /// <summary>Throws the invalid-operation trap unless the condition holds.</summary>
  private static void Require(bool holds) {
    if (!holds)
      throw new IrTrapException(IrTrap.InvalidOperation);
  }

  /// <summary>The value of an argument as the interpreter carries an integer.</summary>
  private static long Long(object[] arguments, int index) => (long)arguments[index];

  /// <summary>The value of a count or a position, which is an <c>i32</c>.</summary>
  private static int Count(object[] arguments, int index) => (int)(long)arguments[index];

  /// <summary>How an operation computes its result from the types of node and arguments and the values of the arguments.</summary>
  private delegate object? Evaluation(IrType result, IReadOnlyList<IrType> types, object[] arguments);

  /// <summary>One operation of the class: the signatures it is defined on and how it computes.</summary>
  /// <param name="Op">The operation.</param>
  /// <param name="Signatures">The types of node and arguments, one entry per width.</param>
  /// <param name="Evaluate">The computation.</param>
  /// <param name="Shared">Whether the operation is also defined on floating-point numbers, which this class leaves to others.</param>
  private sealed record IntegerOperation(
    IrIntrinsicOp Op, IReadOnlyList<(IrType Result, IReadOnlyList<IrType> Arguments)> Signatures, Evaluation Evaluate, bool Shared);

}
