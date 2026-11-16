using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  /// <summary>
  /// The shapes a Rust developer writes for what the statement-by-statement translation gives, each
  /// with the same meaning: the value of a function as its tail instead of a last <c>return</c>
  /// (clippy's <c>needless_return</c>), a binding initialized by the statement after its declaration
  /// or by every branch of the <c>if</c> or <c>match</c> that follows it as one initialized
  /// <c>let</c> (<c>needless_late_init</c>), a binding returned right after it is made as the value
  /// itself (<c>let_and_return</c>), and an <c>if</c> that holds nothing but another <c>if</c> as
  /// one condition joined by <c>&amp;&amp;</c> (<c>collapsible_if</c>).
  /// </summary>
  private static class Tidy {

    /// <summary>The body of a function, its last statements made its value.</summary>
    public static RsBlock Function(RsBlock body, bool isVoid) {
      var tidied = Bodies(body);
      return Tailed(tidied, isVoid) ?? tidied;
    }

    /// <summary>Every block of a block tidied: late initializations joined, bottom-up.</summary>
    private static RsBlock Bodies(RsBlock block) => RustSyntaxRewriter.Rewrite(block, node => node is RsBlock inner ? Swapped(LateInitialized(inner)) : node);

    /// <summary>
    /// Three statements that exchange two elements of one array through a binding,
    /// <c>let t = a[i]; a[i] = a[j]; a[j] = t;</c>, as <c>a.swap(i, j)</c> (clippy's <c>manual_swap</c>):
    /// where nothing after them names the binding, and the indices can neither panic nor change
    /// anything, so that evaluating each once and checking both before either store means the same.
    /// </summary>
    private static RsBlock Swapped(RsBlock block) {
      var statements = block.Statements.ToList();
      for (var index = 0; index + 2 < statements.Count; ++index) {
        if (statements[index] is not RsLet { Initializer: RsIndex { Target: RsPath array, Index: var first } } held
          || statements[index + 1] is not RsExpressionStatement { Expression: RsAssign { Operator: "=", Target: RsIndex { Target: RsPath into, Index: var at }, Value: RsIndex { Target: RsPath from, Index: var second } } }
          || statements[index + 2] is not RsExpressionStatement { Expression: RsAssign { Operator: "=", Target: RsIndex { Target: RsPath back, Index: var other }, Value: RsPath restored } }
          || new[] { into.Name, from.Name, back.Name }.Any(name => name != array.Name) || restored.Name != held.Name
          || RustPrinter.Print(first) != RustPrinter.Print(at) || RustPrinter.Print(second) != RustPrinter.Print(other)
          || !Pure(first) || !Pure(second) || Names(new RsBlock([.. statements.Skip(index + 3)], block.Tail), held.Name))
          continue;

        statements[index] = new RsExpressionStatement(new RsMethodCall(array, "swap", [first, second]));
        statements.RemoveRange(index + 1, 2);
      }

      return statements.Count == block.Statements.Count ? block : block with { Statements = statements };
    }

    /// <summary>Whether an expression can neither panic nor change anything: variables, literals, casts, wrapping arithmetic and the operators that cannot fail.</summary>
    private static bool Pure(RsExpression expression) => expression switch {
      RsPath or RsLiteral => true,
      RsCast cast => Pure(cast.Operand),
      RsParenthesized parenthesized => Pure(parenthesized.Inner),
      RsUnary { Operator: "!" } unary => Pure(unary.Operand),
      RsBinary { Operator: not ("/" or "%" or "+" or "-" or "*" or "<<" or ">>") } binary => Pure(binary.Left) && Pure(binary.Right),
      RsMethodCall call when call.Name.StartsWith("wrapping_", System.StringComparison.Ordinal) => Pure(call.Receiver) && call.Arguments.All(Pure),
      _ => false
    };

    /// <summary>
    /// A block in tail position with its last statement as its value: <c>return v;</c> as <c>v</c>, an
    /// <c>if</c> or <c>match</c> whose branches all give a value or never end as that value; in a
    /// function without a result a last <c>return;</c> dropped. <see langword="null"/> where the last
    /// statement gives no value.
    /// </summary>
    private static RsBlock? Tailed(RsBlock block, bool isVoid) {
      if (block.Tail is not null)
        return block;

      if (block.Statements.Count == 0)
        return isVoid ? block : null;

      var rest = block.Statements.Take(block.Statements.Count - 1).ToList();
      switch (block.Statements[^1]) {
        case RsExpressionStatement { Expression: RsReturn { Value: null } } when isVoid:
          return new RsBlock(rest, null);
        case RsExpressionStatement { Expression: RsReturn { Value: { } value } }:
          return LetAndReturn(new RsBlock(rest, value));
        case RsExpressionStatement { Expression: RsIf choice }: {
          var tailed = TailedIf(choice, isVoid);
          if (tailed is null)
            return isVoid ? block : null;

          return isVoid ? new RsBlock([.. rest, new RsExpressionStatement(tailed)], null) : new RsBlock(rest, tailed);
        }
        case RsExpressionStatement { Expression: RsMatch choice }: {
          var arms = new List<RsMatchArm>();
          foreach (var arm in choice.Arms) {
            if (arm.Body is not RsBlockExpression { Block: var armBlock } || Tailed(armBlock, isVoid) is not { } tailed || (!isVoid && !Gives(tailed)))
              return isVoid ? block : null;

            arms.Add(arm with { Body = Unwrapped(tailed) });
          }

          var match = choice with { Arms = arms };
          return isVoid ? new RsBlock([.. rest, new RsExpressionStatement(match)], null) : new RsBlock(rest, match);
        }
        default:
          return isVoid || RustEmitter.Writer.Diverges(block.Statements[^1]) ? block : null;
      }
    }

    /// <summary>An <c>if</c> in tail position with every branch made its value, or <see langword="null"/> where one gives none.</summary>
    private static RsIf? TailedIf(RsIf choice, bool isVoid) {
      if (choice.Else is null && !isVoid)
        return null;

      if (Tailed(choice.Then, isVoid) is not { } then || (!isVoid && !Gives(then)))
        return null;

      RsExpression? otherwise = choice.Else;
      switch (choice.Else) {
        case RsIf nested:
          otherwise = TailedIf(nested, isVoid);
          if (otherwise is null)
            return null;
          break;
        case RsBlockExpression { Block: var block }:
          if (Tailed(block, isVoid) is not { } tailed || (!isVoid && !Gives(tailed)))
            return null;
          otherwise = new RsBlockExpression(tailed);
          break;
      }

      return choice with { Then = then, Else = otherwise };
    }

    /// <summary>Whether a block gives a value or never ends.</summary>
    private static bool Gives(RsBlock block) => block.Tail is not null || (block.Statements.Count > 0 && RustEmitter.Writer.Diverges(block.Statements[^1]));

    /// <summary>The body of a match arm: a block that is only a value, the value itself.</summary>
    private static RsExpression Unwrapped(RsBlock block) => block is { Statements.Count: 0, Tail: { } value } && !RustPrinter.IsBlockLike(value) ? value : new RsBlockExpression(block);

    /// <summary>A block whose last statement makes a binding the block's value is: the value itself.</summary>
    private static RsBlock LetAndReturn(RsBlock block)
      => block is { Tail: RsPath { Name: var name }, Statements: [.., RsLet { Initializer: { } value } binding] } && binding.Name == name
        ? new RsBlock([.. block.Statements.Take(block.Statements.Count - 1)], Returned(value))
        : block;

    /// <summary>A value made the function's tail: a branch of a <c>match</c> or an <c>if</c> that returns gives its value instead (clippy's <c>needless_return</c>).</summary>
    private static RsExpression Returned(RsExpression value) => value switch {
      RsMatch choice => choice with { Arms = [.. choice.Arms.Select(arm => arm with { Body = arm.Body is RsReturn { Value: { } returned } ? returned : arm.Body })] },
      RsIf { Then: { Statements: [RsExpressionStatement { Expression: RsReturn { Value: { } returned } }], Tail: null } } choice
        => Returned(choice with { Then = new RsBlock([], returned) }),
      RsIf { Else: RsBlockExpression { Block: { Statements: [RsExpressionStatement { Expression: RsReturn { Value: { } returned } }], Tail: null } } } choice
        => choice with { Else = new RsBlockExpression(new RsBlock([], returned)) },
      RsIf { Else: RsIf nested } choice => choice with { Else = Returned(nested) },
      _ => value
    };

    /// <summary>
    /// A block with each binding declared without a value joined with the statement that follows it,
    /// where that statement assigns it or is an <c>if</c> or <c>match</c> every branch of which ends
    /// assigning it and names it nowhere else.
    /// </summary>
    private static RsBlock LateInitialized(RsBlock block) {
      var statements = block.Statements.ToList();
      for (var index = 0; index < statements.Count; ++index) {
        if (statements[index] is not RsLet { Initializer: null } binding)
          continue;

        // The first statement that names the binding; every one before it leaves it alone, so the declaration may move down to it.
        var first = statements.FindIndex(index + 1, statement => Names(statement, binding.Name));
        if (first < 0)
          continue;

        RsExpression? value = statements[first] switch {
          RsExpressionStatement { Expression: RsAssign { Operator: "=", Target: RsPath place, Value: var assigned } } when place.Name == binding.Name && !Names(assigned, binding.Name) => assigned,
          RsExpressionStatement { Expression: RsIf choice } => AssignedIf(choice, binding.Name),
          RsExpressionStatement { Expression: RsMatch choice } => AssignedMatch(choice, binding.Name),
          _ => null
        };
        if (value is null)
          continue;

        statements[first] = binding with { Initializer = value };
        statements.RemoveAt(index);
        --index;
      }

      return statements.Count == block.Statements.Count ? block : block with { Statements = statements };
    }

    /// <summary>An <c>if</c> every branch of which ends assigning a binding as the <c>if</c> giving the values, or <see langword="null"/>.</summary>
    private static RsIf? AssignedIf(RsIf choice, string name, bool assigned = false) {
      if (choice.Else is null || Names(choice.Condition, name))
        return null;

      // A branch that never ends gives no value and may stand beside those that do, as long as one does.
      var then = Assigning(choice.Then, name) ?? (Leaving(choice.Then, name) is not null ? choice.Then : null);
      if (then is null)
        return null;

      assigned |= then.Tail is not null;
      RsExpression? otherwise = choice.Else switch {
        RsIf nested => AssignedIf(nested, name, assigned),
        RsBlockExpression { Block: var block } when Assigning(block, name) is { } value => new RsBlockExpression(value),
        RsBlockExpression { Block: var block } when assigned && Leaving(block, name) is not null => new RsBlockExpression(block),
        _ => null
      };
      return otherwise is null ? null : choice with { Then = then, Else = otherwise };
    }

    private static RsMatch? AssignedMatch(RsMatch choice, string name) {
      if (Names(choice.Scrutinee, name))
        return null;

      var arms = new List<RsMatchArm>();
      foreach (var arm in choice.Arms) {
        if (arm.Body is not RsBlockExpression { Block: var block })
          return null;

        if (Assigning(block, name) is { } assigned)
          arms.Add(arm with { Body = Unwrapped(assigned) });
        else if (Leaving(block, name) is { } left)
          arms.Add(arm with { Body = left });
        else
          return null;
      }

      return arms.Any(arm => arm.Body is not (RsReturn or RsBreak or RsContinue or RsMacro { Name: "panic" or "unreachable" })) ? choice with { Arms = arms } : null;
    }

    /// <summary>
    /// A block that never ends and names a binding nowhere, as the value of a branch that gives none:
    /// its one statement itself, or the block; otherwise <see langword="null"/>.
    /// </summary>
    private static RsExpression? Leaving(RsBlock block, string name) {
      if (block.Tail is not null || block.Statements.Count == 0 || !RustEmitter.Writer.Diverges(block.Statements[^1]) || Names(block, name))
        return null;

      return block.Statements is [RsExpressionStatement { Expression: var only }] ? only : new RsBlockExpression(block);
    }

    /// <summary>A block that ends assigning a binding and names it nowhere before, as the block giving the value; otherwise <see langword="null"/>.</summary>
    private static RsBlock? Assigning(RsBlock block, string name)
      => block is { Tail: null, Statements: [.., RsExpressionStatement { Expression: RsAssign { Operator: "=", Target: RsPath place, Value: var value } }] }
        && place.Name == name && !block.Statements.Take(block.Statements.Count - 1).Any(statement => Names(statement, name)) && !Names(value, name)
        ? new RsBlock([.. block.Statements.Take(block.Statements.Count - 1)], value)
        : null;

    private static bool Names(RsNode node, string name) => RustSyntaxRewriter.DescendantsAndSelf(node).Any(item => item is RsPath path && path.Name == name);

    /// <summary>An <c>if</c> without <c>else</c> holding only an <c>if</c> without <c>else</c>, as one <c>if</c> of both conditions.</summary>
    public static RsIf Collapsed(RsIf choice) {
      if (choice is not { Else: null, Then: { Tail: null, Statements: [RsExpressionStatement { Expression: RsIf { Else: null } inner }] } })
        return choice;

      RsExpression Operand(RsExpression condition) => condition is RsBinary { Operator: "||" } || Precedence(condition) < 4 ? new RsParenthesized(condition) { Type = IrType.Bool } : condition;
      return new RsIf(new RsBinary(Operand(choice.Condition), "&&", Operand(inner.Condition)) { Type = IrType.Bool }, inner.Then, null);
    }

  }

}
