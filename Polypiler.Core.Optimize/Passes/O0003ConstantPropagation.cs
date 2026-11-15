using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize.Passes;

/// <summary>
/// O0003, constant propagation (prototype, on the structured IR rather than on SSA): a read of a
/// variable that holds a known constant on every way to it becomes the constant. The knowledge flows
/// forward through a function; after a choice the variables both branches agree on stay known, and a
/// variable a loop writes is unknown in the loop and after it. Three clean-ups make what an inlined
/// helper leaves behind one expression again: a variable that copies another, neither ever written
/// again, is that other one; a declaration directly followed by an assignment of the same variable
/// takes the value; and a variable read once, in the statement after its declaration, whose value has
/// no effect and cannot trap, becomes that value there. A variable a lambda names is never touched.
/// </summary>
public sealed class O0003ConstantPropagation : IIrPass {

  /// <inheritdoc/>
  public string Id => "O0003";

  /// <inheritdoc/>
  public string Name => "constant propagation";

  /// <inheritdoc/>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(scope);
    ArgumentNullException.ThrowIfNull(applied);
    var changed = false;
    var functions = module.Functions.Select(function => {
      if (!scope(function))
        return function;

      var run = new Walk(function, applied);
      var body = run.Body();
      if (!run.Changed)
        return function;

      changed = true;
      return function with { Body = body };
    }).ToList();
    return changed ? module with { Functions = functions } : module;
  }

  private sealed class Walk(IrFunction function, Action<string, string> applied) {

    private readonly HashSet<IrLocal> _shared = IrPassSupport.Shared(function);
    private readonly List<int> _position = [];
    private readonly HashSet<string> _reported = [];

    public bool Changed { get; private set; }

    public IrBlock Body() {
      var body = this.Constants(function.Body, []);
      body = this.Copies(body);
      body = this.Forward(body);
      return body;
    }

    private void Applied() {
      this.Changed = true;
      var position = this._position.Count == 0 ? "1" : string.Join('.', this._position);
      if (this._reported.Add(position))
        applied(function.Name, position);
    }

    // ---------------------------------------------------------------- constants

    private IrBlock Constants(IrBlock block, Dictionary<IrLocal, IrConst> known) {
      var statements = new List<IrStmt>(block.Statements.Count);
      for (var index = 0; index < block.Statements.Count; ++index) {
        this._position.Add(index + 1);
        statements.Add(this.Constants(block.Statements[index], known));
        this._position.RemoveAt(this._position.Count - 1);
      }

      return new IrBlock(statements);
    }

    private IrStmt Constants(IrStmt statement, Dictionary<IrLocal, IrConst> known) {
      switch (statement) {
        case IrBlock block:
          return this.Constants(block, known);
        case IrDeclare declare: {
          var value = declare.Initializer is null ? null : this.Substitute(declare.Initializer, known);
          this.Forget(statement, known);
          this.Learn(declare.Local, value, known);
          return declare with { Initializer = value };
        }
        case IrAssign assign: {
          var value = this.Substitute(assign.Value, known);
          this.Forget(statement, known);
          this.Learn(assign.Local, value, known);
          return assign with { Value = value };
        }
        case IrIf conditional: {
          var condition = this.Substitute(conditional.Condition, known);
          this.Forget(new IrExprStmt(condition), known);
          var then = new Dictionary<IrLocal, IrConst>(known);
          var otherwise = new Dictionary<IrLocal, IrConst>(known);
          var thenBlock = this.Constants(conditional.Then, then);
          var elseBlock = conditional.Else is null ? null : this.Constants(conditional.Else, otherwise);
          Meet(known, IrPassSupport.Leaves(thenBlock) ? null : then, elseBlock is not null && IrPassSupport.Leaves(elseBlock) ? null : otherwise);
          return new IrIf(condition, thenBlock, elseBlock);
        }
        case IrWhile or IrDoWhile or IrFor or IrForEachCollection or IrForEachSequence or IrSwitch or IrTry or IrUsing: {
          // What the statement writes anywhere is unknown in it - a loop runs its body again - and after it.
          foreach (var written in IrStatements.Writes(statement))
            known.Remove(written);

          var outer = new Dictionary<IrLocal, IrConst>(known);
          var result = IrPassSupport.Map(statement, expression => this.Substitute(expression, outer), block => this.Constants(block, new Dictionary<IrLocal, IrConst>(outer)));
          foreach (var written in IrStatements.Writes(statement))
            known.Remove(written);

          return result;
        }
        default: {
          var result = IrPassSupport.Map(statement, expression => this.Substitute(expression, known), block => this.Constants(block, new Dictionary<IrLocal, IrConst>(known)));
          this.Forget(result, known);
          // Whatever else the statement writes anywhere - a statement of a kind the cases above do not know - is unknown after it.
          foreach (var written in IrStatements.Writes(result))
            known.Remove(written);

          return result;
        }
      }
    }

    /// <summary>Forgets what the expressions of a statement write by a step or a binding.</summary>
    private void Forget(IrStmt statement, Dictionary<IrLocal, IrConst> known) {
      foreach (var expression in IrTree.Expressions(statement))
        foreach (var node in IrTree.Nodes(expression))
          if (node is IrStep { Target: IrLocalRef { Local: var stepped } })
            known.Remove(stepped);
          else if (node is IrBind bind)
            known.Remove(bind.Local);
    }

    /// <summary>
    /// Remembers what a variable holds after it is set: a constant, or the value of an expression of
    /// constants alone, which the interpreter computes - the expression itself is folding's to replace,
    /// so a chain of steps is known to its end in one run.
    /// </summary>
    private void Learn(IrLocal local, IrExpr? value, Dictionary<IrLocal, IrConst> known) {
      var constant = value as IrConst ?? (value is not null && IrPassSupport.IsPure(value) && !IrTree.Nodes(value).Any(node => node is IrLocalRef or IrConstRef)
        ? IrPassSupport.Evaluate(value)
        : null);
      if (constant is not null && !this._shared.Contains(local))
        known[local] = constant;
      else
        known.Remove(local);
    }

    /// <summary>Keeps what every branch that runs on agrees on; a branch that leaves is <see langword="null"/>.</summary>
    private static void Meet(Dictionary<IrLocal, IrConst> known, Dictionary<IrLocal, IrConst>? then, Dictionary<IrLocal, IrConst>? otherwise) {
      known.Clear();
      var branches = new[] { then, otherwise }.OfType<Dictionary<IrLocal, IrConst>>().ToList();
      if (branches.Count == 0)
        return;

      foreach (var (local, value) in branches[0])
        if (branches.Skip(1).All(branch => branch.TryGetValue(local, out var other) && other == value))
          known[local] = value;
    }

    private IrExpr Substitute(IrExpr expression, Dictionary<IrLocal, IrConst> known) {
      if (known.Count == 0)
        return expression;

      var result = Replace(expression, known);
      if (!ReferenceEquals(result, expression))
        this.Applied();

      return result;
    }

    /// <summary>The expression with every read of a known variable replaced; the variable a step changes is its place, not a read.</summary>
    private static IrExpr Replace(IrExpr expression, Dictionary<IrLocal, IrConst> known) {
      if (expression is IrLocalRef reference)
        return known.TryGetValue(reference.Local, out var value) ? value : expression;

      if (expression is IrStep { Target: IrLocalRef })
        return expression;

      var operands = IrTree.Operands(expression);
      if (expression is IrLambda || operands.Count == 0)
        return expression;

      var mapped = operands.Select(operand => Replace(operand, known)).ToList();
      return mapped.Where((operand, index) => !ReferenceEquals(operand, operands[index])).Any() ? IrShape.With(expression, mapped) : expression;
    }

    // ---------------------------------------------------------------- copies

    /// <summary>
    /// A variable declared as a copy of another, where neither is written again, a lambda names
    /// neither and the variable is declared once, is the other one: every read of it reads the other.
    /// </summary>
    private IrBlock Copies(IrBlock body) {
      var writes = IrPassSupport.Writes(body);
      var declarations = IrPassSupport.Declarations(body);
      var parameters = function.Parameters.ToHashSet();
      var copies = new Dictionary<IrLocal, IrLocal>();
      foreach (var declare in IrPassSupport.Statements(body).OfType<IrDeclare>())
        if (declare.Initializer is IrLocalRef { Local: var source } && source.Type == declare.Local.Type
            && !writes.ContainsKey(declare.Local) && !writes.ContainsKey(source) && declarations.GetValueOrDefault(declare.Local) == 1
            && (parameters.Contains(source) || declarations.GetValueOrDefault(source) == 1)
            && !this._shared.Contains(declare.Local) && !this._shared.Contains(source) && !parameters.Contains(declare.Local))
          copies[declare.Local] = source;

      if (copies.Count == 0)
        return body;

      IrLocal Resolve(IrLocal local) => copies.TryGetValue(local, out var source) ? Resolve(source) : local;
      this.Changed = true;
      applied(function.Name, "1");
      return this.Rewrite(body, statement => statement is IrDeclare declare && copies.ContainsKey(declare.Local) ? null : statement,
        expression => IrShape.Map(expression, node => node is IrLocalRef reference && copies.ContainsKey(reference.Local) ? new IrLocalRef(Resolve(reference.Local)) : null));
    }

    /// <summary>A block with every statement kept or dropped and every expression mapped, the blocks within included.</summary>
    private IrBlock Rewrite(IrBlock block, Func<IrStmt, IrStmt?> keep, Func<IrExpr, IrExpr> expression) {
      var statements = new List<IrStmt>();
      foreach (var statement in block.Statements)
        if (keep(statement) is { } kept)
          statements.Add(IrPassSupport.Map(kept, expression, inner => this.Rewrite(inner, keep, expression)));

      return new IrBlock(statements);
    }

    // ---------------------------------------------------------------- forward

    /// <summary>
    /// In every block: a declaration followed by an assignment of the same variable that does not read
    /// it takes the value, the first value being one that may go unevaluated; and a variable read once
    /// in the whole function, in the statement after its declaration, whose value may be evaluated
    /// anywhere, is replaced by the value.
    /// </summary>
    private IrBlock Forward(IrBlock body) {
      var reads = IrPassSupport.Reads(body);
      var writes = IrPassSupport.Writes(body);
      return this.Forward(body, reads, writes);
    }

    private IrBlock Forward(IrBlock block, Dictionary<IrLocal, int> reads, Dictionary<IrLocal, int> writes) {
      var statements = block.Statements.Select(statement => IrPassSupport.Map(statement, expression => expression, inner => this.Forward(inner, reads, writes))).ToList();
      var changed = false;
      for (var index = 0; index + 1 < statements.Count; ++index) {
        if (statements[index] is not IrDeclare declare || this._shared.Contains(declare.Local))
          continue;

        if (Coalesced(statements, index, writes) is { } at) {
          var assign = (IrAssign)statements[at];
          var merged = declare.Initializer is null || !IrShape.Reads(assign.Value, declare.Local)
            ? assign.Value
            : IrShape.Substitute(assign.Value, declare.Local, declare.Initializer);
          statements[at] = declare with { Initializer = merged };
          statements.RemoveAt(index);
          writes[declare.Local] = writes.GetValueOrDefault(declare.Local) - 1;
          changed = true;
          --index;
          continue;
        }

        if (declare.Initializer is { } value && IrPassSupport.IsPure(value) && reads.GetValueOrDefault(declare.Local) == 1 && writes.GetValueOrDefault(declare.Local) == 0
            && Substituted(statements[index + 1], declare.Local, value) is { } next) {
          statements[index] = next;
          statements.RemoveAt(index + 1);
          reads[declare.Local] = 0;
          foreach (var local in IrShape.Locals(value))
            reads[local] = reads.GetValueOrDefault(local);

          changed = true;
          --index;
        }
      }

      if (!changed)
        return new IrBlock(statements);

      this.Changed = true;
      applied(function.Name, "1");
      return new IrBlock(statements);
    }

    /// <summary>
    /// The position of the assignment a declaration merges with: the next statement of the block that
    /// names the variable assigns it and reads it at most once - not at all where it is declared without
    /// a value - the declared value may go unevaluated, and nothing between the two, nor the assignment
    /// itself, writes a variable that value reads. <see langword="null"/> where there is none.
    /// </summary>
    private static int? Coalesced(List<IrStmt> statements, int index, Dictionary<IrLocal, int> writes) {
      var declare = (IrDeclare)statements[index];
      // A variable nothing assigns has no assignment to merge with: the common case, decided without a scan.
      if (writes.GetValueOrDefault(declare.Local) < 1 || (declare.Initializer is { } initial && !IrPassSupport.IsPure(initial)))
        return null;

      for (var at = index + 1; at < statements.Count; ++at) {
        if (!IrStatements.Mentions(statements[at], declare.Local))
          continue;

        if (statements[at] is not IrAssign assign || assign.Local != declare.Local)
          return null;

        var reads = IrTree.Nodes(assign.Value).Count(node => node is IrLocalRef reference && reference.Local == declare.Local);
        if (reads > 1 || (reads == 1 && declare.Initializer is null))
          return null;

        if (declare.Initializer is { } value) {
          var read = IrShape.Locals(value);
          if (statements.Skip(index + 1).Take(at - index).Any(statement => IrStatements.Writes(statement).Any(written => written != declare.Local && read.Contains(written))))
            return null;
        }

        return at;
      }

      return null;
    }

    /// <summary>
    /// The statement with the one read of a variable replaced by a value, where the read stands among
    /// the expressions the statement evaluates once, nothing in the statement writes a variable the
    /// value reads, and the statement is no loop; otherwise <see langword="null"/>.
    /// </summary>
    private static IrStmt? Substituted(IrStmt statement, IrLocal local, IrExpr value) {
      if (statement is IrWhile or IrDoWhile or IrFor or IrForEachCollection or IrForEachSequence or IrBlock or IrTry or IrUsing)
        return null;

      var own = IrTree.Expressions(statement);
      if (!own.Any(expression => IrShape.Reads(expression, local)))
        return null;

      var read = IrShape.Locals(value);
      var written = own.SelectMany(IrTree.Nodes).Select(node => node switch {
        IrStep { Target: IrLocalRef { Local: var stepped } } => stepped,
        IrBind bind => bind.Local,
        _ => null
      }).OfType<IrLocal>();
      if (written.Any(read.Contains) || own.SelectMany(IrTree.Nodes).OfType<IrLambda>().Any())
        return null;

      return IrPassSupport.Map(statement, expression => IrShape.Substitute(expression, local, value), block => block);
    }

  }

}
