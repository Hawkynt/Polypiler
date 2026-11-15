using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>
/// The use of a moved value, <c>rustc</c>'s error <c>E0382</c>, for the variables of a function whose
/// type is not <c>Copy</c>: a <c>Vec</c>, a struct or an enum that does not derive <c>Copy</c>, and
/// what holds one. A variable is moved where its value is used by value - an argument, an
/// initializer, an assigned value, a field of a struct expression, a returned value - and not where it
/// is borrowed, read through a field or an index, or compared; assigning it again makes it usable.
/// The check follows the control flow: a branch moves what either branch moves, a loop body runs a
/// second time to find what the first pass moved, and nothing flows on from a <c>return</c>, a
/// <c>break</c> or a <c>continue</c>. What it does not see, <c>rustc</c> does when the import runs it.
/// </summary>
internal sealed class MoveCheck {

  private readonly Typed _typed;
  private readonly List<(Binding Binding, Span At)> _errors = [];

  /// <summary>The moved variables at the current point; <see langword="null"/> where control never gets.</summary>
  private HashSet<Binding>? _moved = new(ReferenceEqualityComparer.Instance);

  /// <summary>The states at the <c>break</c>s and <c>continue</c>s of the loops being walked, innermost last.</summary>
  private readonly List<(Expr Loop, List<HashSet<Binding>> Breaks, List<HashSet<Binding>> Continues)> _loops = [];

  private MoveCheck(Typed typed) => this._typed = typed;

  /// <summary>The first use of a moved variable in a function, or <see langword="null"/>.</summary>
  public static (Binding Binding, Span At)? Run(FunctionItem function, Typed typed) {
    var check = new MoveCheck(typed);
    check.Block(function.Body);
    return check._errors.Count == 0 ? null : check._errors[0];
  }

  private static HashSet<Binding>? Union(HashSet<Binding>? first, HashSet<Binding>? second) {
    if (first is null)
      return second is null ? null : new(second, ReferenceEqualityComparer.Instance);

    var union = new HashSet<Binding>(first, ReferenceEqualityComparer.Instance);
    if (second is not null)
      union.UnionWith(second);

    return union;
  }

  private HashSet<Binding>? Copy() => this._moved is null ? null : new(this._moved, ReferenceEqualityComparer.Instance);

  /// <summary>Whether values of a type are moved, not copied.</summary>
  private static bool Moves(RType type) {
    var resolved = type.Resolved;
    return resolved.Kind is not (RKind.Reference or RKind.Variable or RKind.Closure or RKind.Iterator or RKind.Function) && !resolved.IsCopy;
  }

  // ---------------------------------------------------------------- statements

  private void Block(Block block) {
    foreach (var statement in block.Statements) {
      switch (statement) {
        case LetStatement let:
          if (let.Initializer is not null)
            this.Use(let.Initializer, true);

          if (let.Else is not null) {
            var saved = this.Copy();
            this.Block(let.Else);
            this._moved = saved;
          }

          this.Unmove(let.Pattern);
          break;
        case ExpressionStatement expression:
          this.Use(expression.Expression, false);
          break;
      }
    }

    if (block.Tail is not null)
      this.Use(block.Tail, true);
  }

  /// <summary>A new binding of a pattern is a new variable, never moved.</summary>
  private void Unmove(Pattern pattern) {
    if (pattern is IdentifierPattern identifier && this._typed.Bindings.TryGetValue(identifier, out var binding))
      this._moved?.Remove(binding);
  }

  // ---------------------------------------------------------------- expressions

  /// <summary>Walks an expression in evaluation order; <paramref name="byValue"/> says whether its value is moved where it goes.</summary>
  private void Use(Expr expression, bool byValue) {
    switch (expression) {
      case PathExpr path when this._typed.Bindings.TryGetValue(path, out var binding) && binding.Constant is null:
        if (this._moved is null)
          return;

        if (this._moved.Contains(binding)) {
          this._errors.Add((binding, path.At));
          return;
        }

        if (byValue && Moves(binding.Type))
          this._moved.Add(binding);

        return;
      case ReferenceExpr reference:
        this.Use(reference.Operand, false);
        return;
      case DerefExpr deref:
        this.Use(deref.Operand, false);
        return;
      case FieldExpr field:
        this.Use(field.Target, false);
        return;
      case IndexExpr index:
        this.Use(index.Target, false);
        this.Use(index.Index, false);
        return;
      case UnaryExpr unary:
        this.Use(unary.Operand, false);
        return;
      case CastExpr cast:
        this.Use(cast.Operand, false);
        return;
      case BinaryExpr binary when binary.Op is "&&" or "||": {
        this.Use(binary.Left, false);
        var saved = this.Copy();
        this.Use(binary.Right, false);
        this._moved = Union(saved, this._moved);
        return;
      }
      case BinaryExpr binary:
        this.Use(binary.Left, false);
        this.Use(binary.Right, false);
        return;
      case AssignExpr assign:
        this.Use(assign.Value, true);
        if (assign.Op == "=" && assign.Target is PathExpr target && this._typed.Bindings.TryGetValue(target, out var assigned))
          this._moved?.Remove(assigned);
        else
          this.Use(assign.Target, false);

        return;
      case MethodCallExpr call: {
        // A method that takes self by value moves its receiver; one that borrows does not.
        var receiverByValue = this._typed.Callees.TryGetValue(call, out var method) && method.Item.Receiver == ReceiverKind.Value
          || this._typed.Methods.TryGetValue(call, out var name) && name is "into_iter" or "Option::unwrap" or "Option::expect" or "Option::unwrap_or" or "Result::unwrap" or "Result::expect" or "Result::unwrap_or" or "Result::ok" or "Option::ok_or";
        this.Use(call.Receiver, receiverByValue);
        foreach (var argument in call.Arguments)
          this.Use(argument, true);

        return;
      }
      case CallExpr call:
        this.Use(call.Callee, false);
        foreach (var argument in call.Arguments)
          this.Use(argument, true);

        return;
      case TupleExpr tuple:
        foreach (var element in tuple.Elements)
          this.Use(element, byValue);

        return;
      case ArrayExpr array:
        foreach (var element in array.Elements)
          this.Use(element, true);

        return;
      case ArrayRepeatExpr array:
        this.Use(array.Value, true);
        this.Use(array.Count, false);
        return;
      case StructExpr structure:
        foreach (var (_, value, _) in structure.Fields)
          this.Use(value, true);

        if (structure.Base is not null)
          this.Use(structure.Base, false);

        return;
      case MacroExpr macro:
        foreach (var argument in macro.Arguments)
          this.Use(argument, macro.Name == "vec");

        return;
      case BlockExpr block:
        this.Loop(block, () => this.Block(block.Block), false);
        return;
      case IfExpr conditional: {
        this.Use(conditional.Condition, conditional.Pattern is not null);
        var saved = this.Copy();
        this.Block(conditional.Then);
        var then = this._moved;
        this._moved = saved;
        if (conditional.Else is not null)
          this.Use(conditional.Else, byValue);

        this._moved = Union(then, this._moved);
        return;
      }
      case MatchExpr match: {
        this.Use(match.Scrutinee, match.Arms.Any(arm => Binds(arm.Pattern)));
        var saved = this.Copy();
        HashSet<Binding>? after = null;
        var any = false;
        foreach (var arm in match.Arms) {
          this._moved = saved is null ? null : new(saved, ReferenceEqualityComparer.Instance);
          if (arm.Guard is not null)
            this.Use(arm.Guard, false);

          this.Use(arm.Body, byValue);
          after = any ? Union(after, this._moved) : this._moved;
          any = true;
        }

        this._moved = any ? after : saved;
        return;
      }
      case WhileExpr loop:
        this.Use(loop.Condition, loop.Pattern is not null);
        this.Loop(loop, () => {
          this.Block(loop.Body);
          this.Use(loop.Condition, loop.Pattern is not null);
        }, true);
        return;
      case LoopExpr loop:
        this.Loop(loop, () => this.Block(loop.Body), true, endless: true);
        return;
      case ForExpr loop:
        this.Use(loop.Iterable, true);
        this.Loop(loop, () => this.Block(loop.Body), true);
        return;
      case BreakExpr jump:
        if (jump.Value is not null)
          this.Use(jump.Value, true);

        this.Jump(jump, true);
        return;
      case ContinueExpr jump:
        this.Jump(jump, false);
        return;
      case ReturnExpr jump:
        if (jump.Value is not null)
          this.Use(jump.Value, true);

        this._moved = null;
        return;
      case TryExpr attempt:
        this.Use(attempt.Operand, true);
        return;
      case ClosureExpr closure:
        // A closure is checked where it is called; its captures are borrows or copies.
        _ = closure;
        return;
    }
  }

  private bool Binds(Pattern pattern) => pattern switch {
    IdentifierPattern identifier => this._typed.Bindings.TryGetValue(identifier, out var binding) && binding.Constant is null,
    OrPattern or => or.Alternatives.Any(this.Binds),
    TuplePattern tuple => tuple.Elements.Any(this.Binds),
    TupleStructPattern tupleStruct => tupleStruct.Elements.Any(this.Binds),
    StructPattern structure => structure.Fields.Any(field => this.Binds(field.Pattern)),
    _ => false
  };

  private void Jump(Expr jump, bool isBreak) {
    var target = this._typed.Targets.GetValueOrDefault(jump);
    var index = this._loops.FindLastIndex(loop => ReferenceEquals(loop.Loop, target));
    if (index >= 0 && this._moved is not null)
      (isBreak ? this._loops[index].Breaks : this._loops[index].Continues).Add(this.Copy()!);

    this._moved = null;
  }

  /// <summary>
  /// A loop or a labelled block: its body once, and for a loop a second time from what the first
  /// pass left, so that a value moved in one pass is found used in the next.
  /// </summary>
  private void Loop(Expr node, System.Action body, bool repeats, bool endless = false) {
    var entry = this.Copy();
    this._loops.Add((node, [], []));
    body();
    if (repeats) {
      var again = this._loops[^1].Continues.Aggregate(this._moved, Union);
      if (again is not null && entry is not null && !again.SetEquals(entry)) {
        this._moved = Union(entry, again);
        body();
      }
    }

    var (_, breaks, continues) = this._loops[^1];
    this._loops.RemoveAt(this._loops.Count - 1);
    // A loop that ends by its condition leaves with the states of every pass; a `loop` only by break.
    var exit = endless ? null : repeats ? Union(entry, continues.Aggregate(this._moved, Union)) : this._moved;
    this._moved = breaks.Aggregate(exit, Union);
  }

}
