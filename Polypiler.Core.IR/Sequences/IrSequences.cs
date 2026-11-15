using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// Produces the next value of the sequence of the iterator function it stands in and suspends the
/// function until the consumer asks for the value after it. It stands in the body of an iterator
/// function only - one whose result is a <see cref="IrTypeKind.Sequence"/> - never inside a catch
/// clause, a finally block or the body of a try that has a catch clause.
/// </summary>
/// <param name="Value">The value produced, of the element type of the sequence.</param>
public sealed record IrYield(IrExpr Value) : IrStmt;

/// <summary>
/// Ends the sequence of the iterator function it stands in: its pending finally blocks run, innermost
/// first, and the consumer is told there is no further value. C#'s <c>yield break</c>, a <c>return;</c>
/// in a JavaScript generator.
/// </summary>
public sealed record IrYieldBreak : IrStmt;

/// <summary>
/// Produces every value of another sequence, in order, as the iterator function it stands in: C#'s
/// <c>foreach (var v in s) yield return v;</c> and JavaScript's <c>yield* s</c>. The inner sequence
/// is enumerated once and closed with the outer one: when the consumer closes the outer cursor while
/// the inner one is running, the inner one is closed first.
/// </summary>
/// <param name="Sequence">The sequence whose values are produced.</param>
public sealed record IrYieldFrom(IrExpr Sequence) : IrStmt;

/// <summary>
/// A loop over the values of a sequence: before every pass the next value is assigned to
/// <paramref name="Element"/>. The sequence is evaluated once and a cursor is opened on it - a new one
/// for a restartable sequence, the one cursor of a single-pass sequence. However the loop ends - at
/// the end of the sequence, by <c>break</c>, by <c>return</c> or by an exception leaving the body - the
/// cursor is closed before control goes on: the pending finally blocks of the iterator function run,
/// innermost first, exactly once. <c>break</c> and <c>continue</c> act on this loop.
/// </summary>
/// <param name="Element">The local of the value, a local of the function in scope in the body only.</param>
/// <param name="Sequence">The sequence.</param>
/// <param name="Body">The statements of a pass.</param>
public sealed record IrForEachSequence(IrLocal Element, IrExpr Sequence, IrBlock Body) : IrStmt;

/// <summary>
/// The elements of an array as a restartable sequence: every enumeration walks the array from its
/// first element, reading the element when it is reached. C#'s conversion of a <c>T[]</c> to
/// <c>IEnumerable&lt;T&gt;</c>, an array in a JavaScript <c>yield*</c>.
/// </summary>
/// <param name="Array">The array, which is evaluated once.</param>
public sealed record IrArraySequence(IrExpr Array) : IrExpr(IrType.Sequence(Array.Type.Element ?? IrType.Void, true));

/// <summary>The rules of lazy sequences and iterator functions.</summary>
public static class IrSequences {

  /// <summary>
  /// Whether a type can be the element of a sequence: a scalar, a string, a struct, a reference to an
  /// object, a collection, an array or a function - no view, no memory, no optional value, no sequence.
  /// </summary>
  /// <param name="element">The element type.</param>
  /// <returns><see langword="true"/> when a sequence holds values of the type.</returns>
  public static bool IsElement(IrType element)
    => element.IsStorable && !element.IsView && !element.IsMemory && !element.IsOptional && !element.IsLazySequence;

  /// <summary>Whether a function is an iterator function: its result is a sequence, which a call makes without running the body.</summary>
  /// <param name="function">The function.</param>
  /// <returns><see langword="true"/> for an iterator function.</returns>
  public static bool IsIterator(IrFunction function) => function.ReturnType.IsLazySequence;

  /// <summary>
  /// Whether a statement produces values itself - holds an <see cref="IrYield"/> or an
  /// <see cref="IrYieldFrom"/> at any depth outside a lambda - so that running it can suspend.
  /// </summary>
  /// <param name="statement">The statement.</param>
  /// <returns><see langword="true"/> when the statement may suspend.</returns>
  public static bool Suspends(IrStmt statement) => statement switch {
    IrYield or IrYieldFrom => true,
    IrBlock block => block.Statements.Any(Suspends),
    _ => IrTree.Blocks(statement).Any(Suspends)
  };

  /// <summary>Every statement within a statement, the statement first, without those inside lambdas.</summary>
  /// <param name="statement">The statement.</param>
  /// <returns>The statements in pre-order.</returns>
  public static IEnumerable<IrStmt> Statements(IrStmt statement) {
    yield return statement;
    var inner = statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(nested => nested.Statements);
    foreach (var nested in inner.SelectMany(Statements))
      yield return nested;
  }

}
