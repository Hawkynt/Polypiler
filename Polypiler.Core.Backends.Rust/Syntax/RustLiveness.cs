using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.Rust.Syntax;

/// <summary>
/// Which values of a function's bindings are never read: a backward liveness analysis over the
/// structured Rust of one function, loops to their fixed point, <c>break</c> and <c>continue</c> to
/// the loop or labelled block they name. A value written by <c>let x = …</c> or <c>x = …</c> that no
/// path reads before it is overwritten or the function ends is a dead store, which rustc warns about
/// as <c>unused_assignments</c>. What the analysis does not know, it counts as read, so it may miss a
/// dead store but never reports a live one.
/// </summary>
public sealed class RustLiveness {

  private sealed record Frame(string? Label, bool IsLoop, HashSet<string> Out, HashSet<string> Head);

  private readonly List<Frame> _frames = [];
  private readonly List<string> _dead = [];
  private bool _recording = true;

  private RustLiveness() {
  }

  /// <summary>The bindings of a function that are given a value nothing reads, each once, in the order found.</summary>
  /// <param name="function">The function.</param>
  /// <returns>The names.</returns>
  public static IReadOnlyList<string> DeadStores(RsFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    var analysis = new RustLiveness();
    analysis.Block(function.Body, []);
    return [.. analysis._dead.Distinct(StringComparer.Ordinal)];
  }

  private void Dead(string name) {
    if (this._recording && !name.StartsWith('_'))
      this._dead.Add(name);
  }

  private HashSet<string> Block(RsBlock block, HashSet<string> live) {
    if (block.Tail is { } tail)
      live = this.Expression(tail, live);

    for (var index = block.Statements.Count - 1; index >= 0; --index)
      live = block.Statements[index] switch {
        RsLet { Initializer: null } binding => Without(live, binding.Name),
        RsLet binding => this.Let(binding, live),
        RsExpressionStatement statement => this.Expression(statement.Expression, live),
        _ => live
      };

    return live;
  }

  private HashSet<string> Let(RsLet binding, HashSet<string> live) {
    if (!live.Contains(binding.Name))
      this.Dead(binding.Name);

    return this.Expression(binding.Initializer!, Without(live, binding.Name));
  }

  /// <summary>The bindings live before an expression, given those live after it.</summary>
  private HashSet<string> Expression(RsExpression expression, HashSet<string> live) {
    switch (expression) {
      case RsPath path:
        return With(live, path.Name);
      case RsAssign { Target: RsPath place } assign: {
        if (!live.Contains(place.Name))
          this.Dead(place.Name);

        var after = assign.Operator == "=" ? Without(live, place.Name) : With(live, place.Name);
        return this.Expression(assign.Value, after);
      }
      case RsBinary { Operator: "&&" or "||" } lazy:
        return this.Expression(lazy.Left, Union(this.Expression(lazy.Right, live), live));
      case RsIf choice: {
        var then = this.Block(choice.Then, live);
        var otherwise = choice.Else is null ? live : this.Expression(choice.Else, live);
        return this.Expression(choice.Condition, Union(then, otherwise));
      }
      case RsBlockExpression block:
        return this.Block(block.Block, live);
      case RsLabeledBlock labeled:
        return this.Framed(new Frame(labeled.Label, false, live, []), () => this.Block(labeled.Body, live));
      case RsMatch choice:
        return this.Expression(choice.Scrutinee, choice.Arms.Aggregate(new HashSet<string>(StringComparer.Ordinal), (all, arm) => Union(all, this.Expression(arm.Body, live))));
      case RsWhile loop:
        return this.Loop(loop.Label, live, head => this.Expression(loop.Condition, Union(this.Block(loop.Body, head), live)));
      case RsLoop loop:
        return this.Loop(loop.Label, live, head => this.Block(loop.Body, head));
      case RsFor loop: {
        var head = this.Loop(loop.Label, live, start => Without(this.Block(loop.Body, Union(start, live)), loop.Pattern));
        return this.Expression(loop.Iterator, Union(head, live));
      }
      case RsBreak exit: {
        var target = this.Target(exit.Label, false);
        var after = target?.Out ?? live;
        return exit.Value is null ? new HashSet<string>(after, StringComparer.Ordinal) : this.Expression(exit.Value, after);
      }
      case RsContinue next:
        return new HashSet<string>(this.Target(next.Label, true)?.Head ?? live, StringComparer.Ordinal);
      case RsReturn result:
        return result.Value is null ? [] : this.Expression(result.Value, []);
      case RsMacro { Name: "panic" or "unreachable" } macro:
        return macro.Arguments.Reverse().Aggregate(new HashSet<string>(StringComparer.Ordinal), (after, argument) => this.Expression(argument, after));
      default:
        // Operands are evaluated left to right, so the analysis walks them from the right.
        return RustSyntaxRewriter.Children(expression).OfType<RsExpression>().Reverse().Aggregate(live, (after, child) => this.Expression(child, after));
    }
  }

  /// <summary>The bindings live at the head of a loop: the fixed point of its body, then one more pass that records the dead stores.</summary>
  private HashSet<string> Loop(string? label, HashSet<string> live, Func<HashSet<string>, HashSet<string>> pass) {
    var head = new HashSet<string>(live, StringComparer.Ordinal);
    var recording = this._recording;
    this._recording = false;
    while (true) {
      var current = head;
      var next = this.Framed(new Frame(label, true, live, current), () => pass(current));
      next.UnionWith(current);
      if (next.SetEquals(current))
        break;

      head = next;
    }

    this._recording = recording;
    var final = this.Framed(new Frame(label, true, live, head), () => pass(head));
    final.UnionWith(head);
    return final;
  }

  private HashSet<string> Framed(Frame frame, Func<HashSet<string>> body) {
    this._frames.Add(frame);
    try {
      return body();
    } finally {
      this._frames.RemoveAt(this._frames.Count - 1);
    }
  }

  /// <summary>The loop or labelled block a <c>break</c> or <c>continue</c> leaves: the one of its label, else the innermost loop.</summary>
  private Frame? Target(string? label, bool loopOnly)
    => label is null
      ? this._frames.LastOrDefault(frame => frame.IsLoop)
      : this._frames.LastOrDefault(frame => frame.Label == label && (frame.IsLoop || !loopOnly));

  private static HashSet<string> With(HashSet<string> set, string name) => new(set, StringComparer.Ordinal) { name };

  private static HashSet<string> Without(HashSet<string> set, string name) {
    var copy = new HashSet<string>(set, StringComparer.Ordinal);
    copy.Remove(name);
    return copy;
  }

  private static HashSet<string> Union(HashSet<string> first, HashSet<string> second) {
    var union = new HashSet<string>(first, StringComparer.Ordinal);
    union.UnionWith(second);
    return union;
  }

}
