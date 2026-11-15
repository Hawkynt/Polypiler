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
    private static RsBlock Bodies(RsBlock block) => RustSyntaxRewriter.Rewrite(block, node => node is RsBlock inner ? LateInitialized(inner) : node);

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
        ? new RsBlock([.. block.Statements.Take(block.Statements.Count - 1)], value)
        : block;

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
    private static RsIf? AssignedIf(RsIf choice, string name) {
      if (choice.Else is null || Names(choice.Condition, name) || Assigning(choice.Then, name) is not { } then)
        return null;

      RsExpression? otherwise = choice.Else switch {
        RsIf nested => AssignedIf(nested, name),
        RsBlockExpression { Block: var block } => Assigning(block, name) is { } assigned ? new RsBlockExpression(assigned) : null,
        _ => null
      };
      return otherwise is null ? null : choice with { Then = then, Else = otherwise };
    }

    private static RsMatch? AssignedMatch(RsMatch choice, string name) {
      if (Names(choice.Scrutinee, name))
        return null;

      var arms = new List<RsMatchArm>();
      foreach (var arm in choice.Arms) {
        if (arm.Body is not RsBlockExpression { Block: var block } || Assigning(block, name) is not { } assigned)
          return null;

        arms.Add(arm with { Body = Unwrapped(assigned) });
      }

      return choice with { Arms = arms };
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
