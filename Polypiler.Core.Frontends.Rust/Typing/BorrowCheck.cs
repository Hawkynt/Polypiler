using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>
/// Two mutable borrows of one place alive at once, <c>rustc</c>'s error <c>E0499</c>, in the form the
/// borrow checker can be sure of without a control-flow graph: <c>let r = &amp;mut p;</c>, then in a
/// later statement of the same block a second <c>&amp;mut q</c> where <c>p</c> and <c>q</c> overlap,
/// then a use of <c>r</c> in a statement after that one. Places overlap where one is the other or a
/// part of it: a variable, its fields, the elements of an index (an index borrows its whole target,
/// as <c>rustc</c> does not tell elements apart), so that <c>&amp;mut s.a</c> and <c>&amp;mut s.b</c>
/// do not. The walk stops at a statement that leaves the block - nothing after it runs - and at an
/// assignment to <c>r</c>, which ends the first borrow. Every other conflict - a borrow made by a
/// method call, a shared borrow beside a mutable one - is <c>rustc</c>'s to find when the import runs it.
/// </summary>
internal static class BorrowCheck {

  /// <summary>The first second borrow of a place a live mutable borrow holds, with the place it borrows as the source writes it (<c>s.a</c>), or <see langword="null"/>.</summary>
  public static (string Place, Span At)? Run(FunctionItem function, Typed typed) {
    foreach (var block in Nodes(function.Body).OfType<Block>())
      if (Conflict(block, typed) is { } conflict)
        return conflict;

    return null;
  }

  private static (string Place, Span At)? Conflict(Block block, Typed typed) {
    var items = block.Statements.Cast<object>().Append(block.Tail).OfType<object>().ToList();
    for (var first = 0; first < items.Count; ++first) {
      if (items[first] is not LetStatement { Pattern: IdentifierPattern name, Initializer: ReferenceExpr { Mutable: true } borrow }
        || !typed.Bindings.TryGetValue(name, out var holder) || Place(borrow.Operand, typed) is not { } held)
        continue;

      for (var second = first + 1; second < items.Count; ++second) {
        if (Nodes(items[second]).Any(node => node is AssignExpr { Target: PathExpr target } && typed.Bindings.GetValueOrDefault(target) == holder))
          break;

        var again = Nodes(items[second]).OfType<ReferenceExpr>().FirstOrDefault(reference => reference.Mutable && Place(reference.Operand, typed) is { } place && Overlap(held, place));
        if (again is not null) {
          var later = items.Skip(second + 1).TakeWhile((_, index) => !Leaves(items[second + index])).ToList();
          if (later.Any(item => Nodes(item).Any(node => node is PathExpr path && typed.Bindings.GetValueOrDefault(path) == holder)))
            return (string.Join(".", Place(again.Operand, typed)!.Select(part => part is Binding binding ? binding.Name : part)), again.At);

          break;
        }

        if (Leaves(items[second]))
          break;
      }
    }

    return null;
  }

  /// <summary>Whether a statement leaves the block, so that nothing after it runs.</summary>
  private static bool Leaves(object item) => item is ExpressionStatement { Expression: ReturnExpr or BreakExpr or ContinueExpr };

  /// <summary>A place as its variable and the fields below it; <see langword="null"/> for anything else, such as a place behind a reference.</summary>
  private static List<object>? Place(Expr expression, Typed typed) => expression switch {
    PathExpr path when typed.Bindings.TryGetValue(path, out var binding) && binding.Constant is null => [binding],
    FieldExpr field when Place(field.Target, typed) is { } target => [.. target, field.Name],
    IndexExpr index => Place(index.Target, typed),
    _ => null
  };

  /// <summary>Whether two places share memory: one is the other or lies inside it.</summary>
  private static bool Overlap(List<object> first, List<object> second) {
    var length = System.Math.Min(first.Count, second.Count);
    return first.Take(length).SequenceEqual(second.Take(length));
  }

  /// <summary>A node and everything inside it: blocks, statements and expressions, closure bodies included.</summary>
  private static IEnumerable<object> Nodes(object? node) {
    if (node is null)
      yield break;

    yield return node;
    foreach (var child in Children(node))
      foreach (var inner in Nodes(child))
        yield return inner;
  }

  private static IEnumerable<object?> Children(object node) => node switch {
    Block block => [.. block.Statements, block.Tail],
    LetStatement let => [let.Initializer, let.Else],
    ExpressionStatement statement => [statement.Expression],
    UnaryExpr unary => [unary.Operand],
    ReferenceExpr reference => [reference.Operand],
    DerefExpr deref => [deref.Operand],
    BinaryExpr binary => [binary.Left, binary.Right],
    AssignExpr assign => [assign.Target, assign.Value],
    CastExpr cast => [cast.Operand],
    MethodCallExpr call => [call.Receiver, .. call.Arguments],
    CallExpr call => [call.Callee, .. call.Arguments],
    FieldExpr field => [field.Target],
    IndexExpr index => [index.Target, index.Index],
    TupleExpr tuple => tuple.Elements,
    ArrayExpr array => array.Elements,
    ArrayRepeatExpr array => [array.Value, array.Count],
    StructExpr structure => [.. structure.Fields.Select(field => field.Value), structure.Base],
    RangeExpr range => [range.Start, range.End],
    BlockExpr block => [block.Block],
    IfExpr conditional => [conditional.Condition, conditional.Then, conditional.Else],
    MatchExpr match => [match.Scrutinee, .. match.Arms.SelectMany(arm => new object?[] { arm.Guard, arm.Body })],
    WhileExpr loop => [loop.Condition, loop.Body],
    LoopExpr loop => [loop.Body],
    ForExpr loop => [loop.Iterable, loop.Body],
    BreakExpr jump => [jump.Value],
    ReturnExpr jump => [jump.Value],
    TryExpr attempt => [attempt.Operand],
    ClosureExpr closure => [closure.Body],
    MacroExpr macro => macro.Arguments,
    _ => []
  };

}
