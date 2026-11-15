using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The operations on lazy sequences that LINQ to Objects has (#1329). A source makes a sequence of
/// something else; a lazy operation makes a sequence of another one without enumerating it - every
/// enumeration of the result enumerates its source again, element by element, and calls the functions
/// it was given once per element pulled, in the order the elements flow; a terminal enumerates its
/// sequence once, as far as it needs, and is a value. A function an operation is given runs only
/// while a sequence is enumerated, never when the operation is evaluated. The arguments come in the
/// order given, the sequence first.
/// </summary>
public enum IrSequenceOp {

  /// <summary><c>T[] → seq&lt;T&gt;</c>: the elements of the array, each read when it is reached.</summary>
  FromArray,

  /// <summary><c>list&lt;T&gt; → seq&lt;T&gt;</c>: the elements of the list, enumerated as .NET does - a change of the list traps at the next step (<see cref="IrTrap.CollectionModified"/>).</summary>
  FromList,

  /// <summary>
  /// <c>(i32 start, i32 count) → seq&lt;i32&gt;</c>: <c>start</c>, <c>start + 1</c>, … - traps with
  /// <see cref="IrTrap.ArgumentOutOfRange"/> when <c>count &lt; 0</c> or <c>start + count - 1</c>
  /// exceeds the largest <c>i32</c>, when it is evaluated rather than when it is enumerated.
  /// </summary>
  Range,

  /// <summary><c>(seq&lt;T&gt;, fn(T) → U) → seq&lt;U&gt;</c>: the function of every element, called once per element pulled.</summary>
  Select,

  /// <summary><c>(seq&lt;T&gt;, fn(T, i32) → U) → seq&lt;U&gt;</c>: the function of every element and its index.</summary>
  SelectIndexed,

  /// <summary><c>(seq&lt;T&gt;, fn(T) → bool) → seq&lt;T&gt;</c>: the elements the predicate is true of.</summary>
  Where,

  /// <summary><c>(seq&lt;T&gt;, fn(T, i32) → bool) → seq&lt;T&gt;</c>: the elements the predicate of the element and its index is true of.</summary>
  WhereIndexed,

  /// <summary><c>(seq&lt;T&gt;, fn(T) → seq&lt;U&gt;) → seq&lt;U&gt;</c>: the elements of the sequence of every element, each enumerated completely before the next element is pulled.</summary>
  SelectMany,

  /// <summary><c>(seq&lt;T&gt;, fn(T) → seq&lt;U&gt;, fn(T, U) → V) → seq&lt;V&gt;</c>: <see cref="SelectMany"/> with the result of every pair.</summary>
  SelectManyResult,

  /// <summary><c>(seq&lt;T&gt;, i32) → seq&lt;T&gt;</c>: the first n elements; it stops pulling after the n-th, and pulls none for n ≤ 0.</summary>
  Take,

  /// <summary><c>(seq&lt;T&gt;, i32) → seq&lt;T&gt;</c>: the elements after the first n; a negative n acts as 0.</summary>
  Skip,

  /// <summary>
  /// <c>(seq&lt;T&gt;, seq&lt;U&gt;, fn(T, U) → V) → seq&lt;V&gt;</c>: the function of the pairs of
  /// elements at the same position; it pulls from the first, then from the second, and stops at the
  /// shorter. The second sequence is a <see cref="FromArray"/> or a <see cref="FromList"/>.
  /// </summary>
  Zip,

  /// <summary>
  /// <c>(seq&lt;T&gt;, fn(T) → K) → seq&lt;T&gt;</c>: the elements in the ascending order of their keys,
  /// stably. On enumeration it pulls every element, then calls the key function once per element in
  /// the order pulled, then sorts. Keys compare as .NET's default comparer does
  /// (<see cref="IrGenericCompare"/>: a NaN below every number, <c>-0</c> equal to <c>0</c>); a string
  /// key compares by code unit.
  /// </summary>
  OrderBy,

  /// <summary>As <see cref="OrderBy"/>, in the descending order of the keys, still stably.</summary>
  OrderByDescending,

  /// <summary><c>(ordered seq&lt;T&gt;, fn(T) → K) → seq&lt;T&gt;</c>: a further key of an <see cref="OrderBy"/> for the elements whose keys before are equal; its keys are computed after those of the keys before.</summary>
  ThenBy,

  /// <summary>As <see cref="ThenBy"/>, descending.</summary>
  ThenByDescending,

  /// <summary><c>seq&lt;T&gt; → T[]</c>: the elements in a new array.</summary>
  ToArray,

  /// <summary><c>seq&lt;T&gt; → list&lt;T&gt;</c>: the elements in a new list.</summary>
  ToList,

  /// <summary><c>seq&lt;T&gt; → i32</c>: the number of elements; traps with <see cref="IrTrap.IntegerOverflow"/> beyond the largest <c>i32</c>.</summary>
  Count,

  /// <summary><c>(seq&lt;T&gt;, fn(T) → bool) → i32</c>: the number of elements the predicate is true of.</summary>
  CountWhere,

  /// <summary><c>seq&lt;T&gt; → bool</c>: whether there is an element; pulls one at most.</summary>
  Any,

  /// <summary><c>(seq&lt;T&gt;, fn(T) → bool) → bool</c>: whether the predicate is true of an element; stops at the first.</summary>
  AnyWhere,

  /// <summary><c>(seq&lt;T&gt;, fn(T) → bool) → bool</c>: whether the predicate is true of every element; stops at the first it is false of.</summary>
  All,

  /// <summary><c>seq&lt;T&gt; → T</c>: the first element; throws an <c>InvalidOperationException</c> where there is none.</summary>
  First,

  /// <summary><c>(seq&lt;T&gt;, fn(T) → bool) → T</c>: the first element the predicate is true of; throws an <c>InvalidOperationException</c> where there is none.</summary>
  FirstWhere,

  /// <summary><c>seq&lt;T&gt; → T</c>: the first element, or zero (<c>false</c>) where there is none.</summary>
  FirstOrDefault,

  /// <summary><c>(seq&lt;T&gt;, fn(T) → bool) → T</c>: the first element the predicate is true of, or zero (<c>false</c>).</summary>
  FirstOrDefaultWhere,

  /// <summary>
  /// <c>seq&lt;T&gt; → T</c>: the sum - of <c>i32</c> and <c>i64</c> a left fold from 0 that traps with
  /// <see cref="IrTrap.IntegerOverflow"/> at the first partial sum outside the type, of <c>f32</c> one
  /// accumulated in <c>f64</c> and rounded once at the end, of <c>f64</c> a left fold from <c>+0.0</c>.
  /// </summary>
  Sum,

  /// <summary>
  /// <c>seq&lt;T&gt; → T</c>: the smallest element; throws an <c>InvalidOperationException</c>
  /// where there is none. Of floating-point numbers a NaN is the smallest, and the first NaN ends the
  /// enumeration; <c>-0</c> and <c>0</c> are equal, the first one seen stays.
  /// </summary>
  Min,

  /// <summary>
  /// <c>seq&lt;T&gt; → T</c>: the largest element; throws an <c>InvalidOperationException</c>
  /// where there is none. Of floating-point numbers a NaN is the smallest: it is the result only when
  /// every element is one.
  /// </summary>
  Max,

  /// <summary><c>(seq&lt;T&gt;, fn(T, T) → T) → T</c>: the left fold from the first element; throws an <c>InvalidOperationException</c> where there is none.</summary>
  Aggregate,

  /// <summary><c>(seq&lt;T&gt;, A, fn(A, T) → A) → A</c>: the left fold from a seed.</summary>
  AggregateSeed,

  /// <summary><c>(seq&lt;T&gt;, T) → bool</c>: whether an element equals the value by <c>Equals</c> (<see cref="IrGenericEquals"/>: a NaN is found); stops at the first.</summary>
  Contains,

  /// <summary><c>(seq&lt;T&gt;, T) → i32</c>: the index of the first element that equals the value by <c>Equals</c>, or -1 - .NET's <c>Array.IndexOf</c>.</summary>
  IndexOf,

  /// <summary><c>(seq&lt;T&gt;, fn(T) → bool) → i32</c>: the index of the first element the predicate is true of, or -1 - .NET's <c>Array.FindIndex</c>.</summary>
  FindIndex

}

/// <summary>An operation on lazy sequences (<see cref="IrSequenceOp"/>).</summary>
/// <param name="Op">The operation.</param>
/// <param name="Type">The type of its value: a sequence for a source or a lazy operation, the result for a terminal.</param>
/// <param name="Arguments">The sequence first - for a source its operands - then the operands.</param>
public sealed record IrSequence(IrSequenceOp Op, IrType Type, IReadOnlyList<IrExpr> Arguments) : IrExpr(Type) {

  /// <summary>Whether the operation makes a sequence of something that is no sequence.</summary>
  public bool IsSource => IrSequenceOps.IsSource(this.Op);

  /// <summary>Whether the operation is a value other than a sequence, which enumerates its sequence.</summary>
  public bool IsTerminal => IrSequenceOps.IsTerminal(this.Op);

}

/// <summary>The types of the operations on lazy sequences.</summary>
public static class IrSequenceOps {

  /// <summary>The message of the <c>InvalidOperationException</c> of a terminal on a sequence without an element, as .NET words it.</summary>
  public const string NoElements = "Sequence contains no elements";

  /// <summary>The message of the <c>InvalidOperationException</c> of a terminal on a sequence without an element its predicate is true of, as .NET words it.</summary>
  public const string NoMatch = "Sequence contains no matching element";

  /// <summary>Whether an operation makes a sequence of something that is no sequence.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for <see cref="IrSequenceOp.FromArray"/>, <see cref="IrSequenceOp.FromList"/> and <see cref="IrSequenceOp.Range"/>.</returns>
  public static bool IsSource(IrSequenceOp op) => op is IrSequenceOp.FromArray or IrSequenceOp.FromList or IrSequenceOp.Range;

  /// <summary>Whether an operation is a value other than a sequence.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for <see cref="IrSequenceOp.ToArray"/> and every operation after it.</returns>
  public static bool IsTerminal(IrSequenceOp op) => op >= IrSequenceOp.ToArray;

  /// <summary>Whether an operation sorts: <see cref="IrSequenceOp.OrderBy"/> to <see cref="IrSequenceOp.ThenByDescending"/>.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for an ordering.</returns>
  public static bool IsOrdering(IrSequenceOp op) => op is >= IrSequenceOp.OrderBy and <= IrSequenceOp.ThenByDescending;

  /// <summary>Whether a type can be the key of an ordering: a number, a truth value or a string.</summary>
  /// <param name="key">The type of the key.</param>
  /// <returns><see langword="true"/> for a key the IR orders.</returns>
  public static bool IsKey(IrType key) => (key.IsNumeric && !key.IsEnum) || key == IrType.Bool || key.IsString;

  /// <summary>Whether a type has the zero of <see cref="IrSequenceOp.FirstOrDefault"/>: a number or a truth value.</summary>
  /// <param name="element">The element type.</param>
  /// <returns><see langword="true"/> when the IR has its default.</returns>
  public static bool HasDefault(IrType element) => (element.IsNumeric && !element.IsEnum) || element == IrType.Bool;

  /// <summary>
  /// The type of the value of an operation on operands of the given types, or why there is none: the
  /// rule <see cref="IrVerifier"/> checks and what a frontend gives the node.
  /// </summary>
  /// <param name="op">The operation.</param>
  /// <param name="arguments">The types of the arguments, in order.</param>
  /// <returns>The type, or the reason the operands do not fit.</returns>
  public static (IrType? Type, string? Problem) Signature(IrSequenceOp op, IReadOnlyList<IrType> arguments) {
    string Count(int expected) => $"'{op}' takes {expected} arguments, not {arguments.Count}";
    static bool Takes(IrType function, IrType result, params IrType[] parameters)
      => function.Signature is { } signature && signature.Result == result && signature.Parameters.SequenceEqual(parameters);

    switch (op) {
      case IrSequenceOp.FromArray:
        return arguments is [{ IsArray: true } array] && IrSequences.IsElement(array.Element!)
          ? (IrType.Sequence(array.Element!, true), null) : (null, "'FromArray' takes an array of elements a sequence holds");
      case IrSequenceOp.FromList:
        return arguments is [{ IsList: true } list] && IrSequences.IsElement(list.Element!)
          ? (IrType.Sequence(list.Element!, true), null) : (null, "'FromList' takes a list of elements a sequence holds");
      case IrSequenceOp.Range:
        return arguments is [var start, var count] && start == IrType.I32 && count == IrType.I32
          ? (IrType.Sequence(IrType.I32, true), null) : (null, "'Range' takes two i32");
    }

    var expectedCount = op switch {
      IrSequenceOp.ToArray or IrSequenceOp.ToList or IrSequenceOp.Count or IrSequenceOp.Any or IrSequenceOp.First or IrSequenceOp.FirstOrDefault
        or IrSequenceOp.Sum or IrSequenceOp.Min or IrSequenceOp.Max => 1,
      IrSequenceOp.SelectManyResult or IrSequenceOp.Zip or IrSequenceOp.AggregateSeed => 3,
      _ => 2
    };
    if (arguments.Count != expectedCount)
      return (null, Count(expectedCount));

    if (arguments[0] is not { IsLazySequence: true } source)
      return (null, $"'{op}' takes a sequence first, not {arguments[0]}");

    var element = source.Element!;
    var operand = arguments.Count > 1 ? arguments[1] : IrType.Void;
    IrType Lazy(IrType produced) => IrType.Sequence(produced, source.Restartable);
    (IrType?, string?) Function(string shape) => (null, $"'{op}' takes {shape}, not {operand}");
    var result = operand.Signature?.Result;

    switch (op) {
      case IrSequenceOp.Select:
        return Takes(operand, result!, element) && IrSequences.IsElement(result!) ? (Lazy(result!), null) : Function($"fn({element}) -> U");
      case IrSequenceOp.SelectIndexed:
        return Takes(operand, result!, element, IrType.I32) && IrSequences.IsElement(result!) ? (Lazy(result!), null) : Function($"fn({element}, i32) -> U");
      case IrSequenceOp.Where:
        return Takes(operand, IrType.Bool, element) ? (source, null) : Function($"fn({element}) -> bool");
      case IrSequenceOp.WhereIndexed:
        return Takes(operand, IrType.Bool, element, IrType.I32) ? (source, null) : Function($"fn({element}, i32) -> bool");
      case IrSequenceOp.SelectMany:
        return Takes(operand, result!, element) && result is { IsLazySequence: true } ? (Lazy(result.Element!), null) : Function($"fn({element}) -> seq<U>");
      case IrSequenceOp.SelectManyResult:
        var combine = arguments[2];
        return Takes(operand, result!, element) && result is { IsLazySequence: true } inner
          && combine.Signature is { } signature && Takes(combine, signature.Result, element, inner.Element!) && IrSequences.IsElement(signature.Result)
          ? (Lazy(signature.Result), null) : Function($"fn({element}) -> seq<U> and fn({element}, U) -> V");
      case IrSequenceOp.Take or IrSequenceOp.Skip:
        return operand == IrType.I32 ? (source, null) : (null, $"'{op}' takes an i32 count, not {operand}");
      case IrSequenceOp.Zip:
        var pair = arguments[2];
        return operand is { IsLazySequence: true } second && pair.Signature is { } zip && Takes(pair, zip.Result, element, second.Element!) && IrSequences.IsElement(zip.Result)
          ? (Lazy(zip.Result), null) : Function($"a second sequence and fn({element}, U) -> V");
      case IrSequenceOp.OrderBy or IrSequenceOp.OrderByDescending or IrSequenceOp.ThenBy or IrSequenceOp.ThenByDescending:
        return result is not null && Takes(operand, result, element) && IsKey(result) ? (source, null) : Function($"fn({element}) -> K of a number, a truth value or a string");
      case IrSequenceOp.ToArray:
        return (IrType.Array(element), null);
      case IrSequenceOp.ToList:
        // Every element of a sequence is one a list holds.
        return (IrType.List(element), null);
      case IrSequenceOp.Count or IrSequenceOp.Any:
        return (op == IrSequenceOp.Count ? IrType.I32 : IrType.Bool, null);
      case IrSequenceOp.CountWhere or IrSequenceOp.AnyWhere or IrSequenceOp.All or IrSequenceOp.FirstWhere or IrSequenceOp.FirstOrDefaultWhere or IrSequenceOp.FindIndex:
        if (!Takes(operand, IrType.Bool, element))
          return Function($"fn({element}) -> bool");

        if (op == IrSequenceOp.FirstOrDefaultWhere && !HasDefault(element))
          return (null, $"'{op}' of a sequence of {element}, which has no default value");

        return (op switch {
          IrSequenceOp.CountWhere or IrSequenceOp.FindIndex => IrType.I32,
          IrSequenceOp.AnyWhere or IrSequenceOp.All => IrType.Bool,
          _ => element
        }, null);
      case IrSequenceOp.First:
        return (element, null);
      case IrSequenceOp.FirstOrDefault:
        return HasDefault(element) ? (element, null) : (null, $"'{op}' of a sequence of {element}, which has no default value");
      case IrSequenceOp.Sum:
        return element.Kind is IrTypeKind.I32 or IrTypeKind.I64 or IrTypeKind.F32 or IrTypeKind.F64 && !element.IsEnum
          ? (element, null) : (null, $"'Sum' of a sequence of {element}");
      case IrSequenceOp.Min or IrSequenceOp.Max:
        return element.IsNumeric && !element.IsEnum ? (element, null) : (null, $"'{op}' of a sequence of {element}");
      case IrSequenceOp.Aggregate:
        return Takes(operand, element, element, element) && !element.IsCollection ? (element, null) : Function($"fn({element}, {element}) -> {element}");
      case IrSequenceOp.AggregateSeed:
        var step = arguments[2];
        return Takes(step, operand, operand, element) && operand.IsStorable ? (operand, null) : (null, $"'{op}' takes a seed and fn(A, {element}) -> A, not {operand} and {step}");
      default:
        // Contains and IndexOf compare an element with a value by Equals.
        return operand == element && (element.IsNumeric || element == IrType.Bool || element.IsString) && !element.IsEnum
          ? (op == IrSequenceOp.Contains ? IrType.Bool : IrType.I32, null)
          : (null, $"'{op}' takes a value of {element}, a number, a truth value or a string, not {operand}");
    }
  }

  /// <summary>The operations the lowering of the sequence operations calls, which the helper library computes.</summary>
  public static IReadOnlyList<IrIntrinsicOp> Operations { get; } = [IrIntrinsicOp.StableSort, IrIntrinsicOp.RangeCount];

  /// <summary>
  /// Whether <see cref="IrIntrinsicOp.StableSort"/> or <see cref="IrIntrinsicOp.RangeCount"/> takes the
  /// operands and yields the result; <see langword="null"/> for any other operation.
  /// </summary>
  /// <param name="op">The operation.</param>
  /// <param name="result">The type of the node.</param>
  /// <param name="arguments">The types of the operands.</param>
  /// <returns>Whether the node is well typed, or <see langword="null"/>.</returns>
  public static bool? Accepts(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> arguments) => op switch {
    IrIntrinsicOp.StableSort => result == IrType.Void && arguments is [var items, var compare] && items == IrType.Array(IrType.I32)
      && compare == IrType.Function(IrType.I32, IrType.I32, IrType.I32),
    IrIntrinsicOp.RangeCount => result == IrType.I32 && arguments is [var start, var count] && start == IrType.I32 && count == IrType.I32,
    _ => null
  };

  /// <summary>Makes the node of an operation with the type its operands give it.</summary>
  /// <param name="op">The operation.</param>
  /// <param name="arguments">The arguments.</param>
  /// <returns>The node.</returns>
  /// <exception cref="System.ArgumentException">The operands do not fit the operation.</exception>
  public static IrSequence Of(IrSequenceOp op, params IrExpr[] arguments) {
    var (type, problem) = Signature(op, [.. arguments.Select(argument => argument.Type)]);
    return type is null ? throw new System.ArgumentException(problem, nameof(arguments)) : new IrSequence(op, type, arguments);
  }

}
