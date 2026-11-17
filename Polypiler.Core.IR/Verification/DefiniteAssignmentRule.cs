using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// A local declared without a value is read only where some path has assigned it first: a read that
/// no path to it assigns finds no value, in the interpreter and in every target. The rule is a may-
/// analysis over the tree and so never rejects a read some run could reach with a value - a loop
/// counts what its body assigns as assigned at its top, a handler what its <c>try</c> assigns, and the
/// body of a lambda reads the locals it captures whenever it is called, which the rule does not check.
/// </summary>
internal sealed class DefiniteAssignmentRule : IVerifierRule {

  /// <inheritdoc/>
  public string Id => "ir.verifier.locals.assigned-before-read";

  /// <inheritdoc/>
  public string Family => "locals";

  /// <inheritdoc/>
  public string Description => "a local declared without a value is not read where no path has assigned it";

  /// <inheritdoc/>
  public void Check(VerifierRuleContext context) => new Walk(context, Unset(context.Function.Body)).Block(context.Function.Body, []);

  /// <summary>The locals a body declares without a value, those of the lambdas in it excluded.</summary>
  private static HashSet<IrLocal> Unset(IrBlock body) {
    var unset = new HashSet<IrLocal>();
    void Statement(IrStmt statement) {
      if (statement is IrDeclare { Initializer: null } declaration)
        unset.Add(declaration.Local);

      foreach (var inner in statement is IrBlock block ? block.Statements : IrTree.Blocks(statement))
        Statement(inner);
    }

    Statement(body);
    return unset;
  }

  /// <summary>Every local a statement or expression may give a value, anywhere within it, the bodies of lambdas included.</summary>
  private static IEnumerable<IrLocal> Assigned(object node) => IrVerifier.AllNodes(node switch {
    IrStmt statement => statement,
    _ => new IrExprStmt((IrExpr)node)
  }).SelectMany(Gives);

  /// <summary>The locals one node gives a value itself.</summary>
  private static IEnumerable<IrLocal> Gives(object node) => node switch {
    IrDeclare { Initializer: not null } declaration => [declaration.Local],
    IrAssign assignment => [assignment.Local],
    IrBind binding => [binding.Local],
    IrUsing scope => [scope.Resource],
    IrForEachSequence loop => [loop.Element],
    IrForEachCollection loop => new[] { loop.Element, loop.Value }.OfType<IrLocal>(),
    IrTry attempt => attempt.Catches.Select(clause => clause.Local).OfType<IrLocal>(),
    IrFieldStore store => Root(store.Target),
    IrStep step => Root(step.Target),
    IrIntrinsic { Op: IrIntrinsicOp.AddressOf, Arguments: [var target] } => Root(target),
    _ => []
  };

  /// <summary>The local a place is rooted in: the variable whose field, element or whole value it names.</summary>
  private static IEnumerable<IrLocal> Root(IrExpr place) => place switch {
    IrLocalRef reference => [reference.Local],
    IrFieldLoad load => Root(load.Target),
    IrArrayLoad load => Root(load.Array),
    _ => []
  };

  private sealed class Walk(VerifierRuleContext context, HashSet<IrLocal> unset) {

    private readonly HashSet<IrLocal> _reported = [];

    public void Block(IrBlock block, HashSet<IrLocal> assigned) {
      foreach (var statement in block.Statements)
        this.Statement(statement, assigned);
    }

    private void Statement(IrStmt statement, HashSet<IrLocal> assigned) {
      switch (statement) {
        case IrBlock block:
          this.Block(block, assigned);
          return;
        case IrIf conditional:
          this.Expression(conditional.Condition, assigned);
          var then = new HashSet<IrLocal>(assigned);
          this.Block(conditional.Then, then);
          if (conditional.Else is { } otherwise)
            this.Block(otherwise, assigned);

          assigned.UnionWith(then);
          return;
        case IrWhile or IrDoWhile or IrFor or IrForEachSequence or IrForEachCollection or IrSwitch:
          // A loop's top is reached again with what its body assigned; a section of a switch may follow another.
          assigned.UnionWith(Assigned(statement));
          this.Parts(statement, assigned);
          return;
        case IrFieldStore store:
          // A field of a variable is written without reading the variable: the store gives it its value.
          this.Place(store.Target, assigned);
          this.Expression(store.Value, assigned);
          assigned.UnionWith(Gives(store));
          return;
        case IrTry attempt:
          this.Block(attempt.Body, new HashSet<IrLocal>(assigned));
          assigned.UnionWith(Assigned(statement));
          this.Parts(attempt with { Body = new IrBlock([]) }, assigned);
          return;
        default:
          foreach (var expression in IrTree.Expressions(statement))
            this.Expression(expression, assigned);

          assigned.UnionWith(Gives(statement));
          foreach (var block in IrTree.Blocks(statement))
            this.Block(block, assigned);

          return;
      }
    }

    /// <summary>The expressions and then the blocks of a statement, each block from what holds before it.</summary>
    private void Parts(IrStmt statement, HashSet<IrLocal> assigned) {
      foreach (var expression in IrTree.Expressions(statement))
        this.Expression(expression, assigned);

      foreach (var block in IrTree.Blocks(statement))
        this.Block(block, new HashSet<IrLocal>(assigned));
    }

    /// <summary>The target of a field store: the variable it is rooted in is not read, an array's element is.</summary>
    private void Place(IrExpr place, HashSet<IrLocal> assigned) {
      if (place is IrFieldLoad load)
        this.Place(load.Target, assigned);
      else if (place is not IrLocalRef)
        this.Expression(place, assigned);
    }

    private void Expression(IrExpr expression, HashSet<IrLocal> assigned) {
      switch (expression) {
        case IrLocalRef reference:
          if (unset.Contains(reference.Local) && !assigned.Contains(reference.Local) && this._reported.Add(reference.Local))
            context.Report(reference, $"'{reference.Local.Name}' is read where no path has given it a value");

          return;
        case IrLambda lambda:
          // The body runs when the lambda is called; it is checked against its own declarations.
          new Walk(context, Unset(lambda.Body)).Block(lambda.Body, []);
          assigned.UnionWith(Assigned(lambda));
          return;
        case IrIntrinsic { Op: IrIntrinsicOp.AddressOf }:
          // Taking the address reads nothing: the pointer is how the value is given.
          assigned.UnionWith(Gives(expression));
          return;
      }

      foreach (var operand in IrTree.Operands(expression))
        this.Expression(operand, assigned);

      assigned.UnionWith(Gives(expression));
    }

  }

}
