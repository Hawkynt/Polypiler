using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize;

/// <summary>One rule application: which rule, where.</summary>
/// <param name="Id">The optimization number of the rule.</param>
/// <param name="Name">What the rule does.</param>
/// <param name="Function">The function it applied in.</param>
/// <param name="Position">
/// The statement it applied to, as the path of 1-based statement positions from the body of the
/// function inwards - <c>3.1</c> is the first statement in a block of the third statement. The
/// prototype IR carries no source lines, so this is the position the IR has.
/// </param>
public sealed record RuleApplication(string Id, string Name, string Function, string Position) {

  /// <summary>The application as <c>--explain-opt</c> prints it.</summary>
  public override string ToString() => $"{this.Id} {this.Name}: '{this.Function}' statement {this.Position}";

}

/// <summary>What the optimizer made of a module.</summary>
/// <param name="Module">The optimized module.</param>
/// <param name="Applications">Every rule application, in the order they happened.</param>
public sealed record OptimizationResult(IrModule Module, IReadOnlyList<RuleApplication> Applications);

/// <summary>
/// The pass pipeline between the frontend and the backend: the rules of a table, in table order, run
/// over every function until a pass changes nothing or the pass limit is reached. In one pass every
/// statement and every expression is offered to the rules once: a block's statements first, from the
/// first on, then - inside each statement - every expression before its operands, so that the
/// largest idiom is taken whole. What a rule replaced is looked at again only in the next pass.
/// </summary>
public static class Optimizer {

  /// <summary>Optimizes a module.</summary>
  /// <param name="module">The module, as the frontend made it.</param>
  /// <param name="options">What runs.</param>
  /// <returns>The module and the rule applications.</returns>
  /// <exception cref="InvalidOperationException">With <see cref="OptimizerOptions.Verify"/>: a rule left the module ill-typed.</exception>
  public static OptimizationResult Run(IrModule module, OptimizerOptions options) {
    var applications = new List<RuleApplication>();
    if (options.Level == OptimizationLevel.None)
      return new(module, applications);

    var rules = options.Rules.Rules.Where(rule => !options.Disabled.Contains(rule.Id)).ToList();
    var functions = module.Functions.ToList();
    for (var pass = 0; pass < options.PassLimit; ++pass) {
      var before = applications.Count;
      for (var index = 0; index < functions.Count; ++index)
        functions[index] = new Pass(module with { Functions = functions }, index, rules, options, applications).Run();

      if (applications.Count == before)
        break;
    }

    return new(module with { Functions = functions }, applications);
  }

  /// <summary>One pass over one function, on a mirror of its tree that can be changed in place and read back at any time.</summary>
  private sealed class Pass(IrModule module, int index, IReadOnlyList<IrRule> rules, OptimizerOptions options, List<RuleApplication> applications) {

    private readonly IrFunction _function = module.Functions[index];
    private readonly RuleContext _context = new(module.Functions[index], options.Target);
    private Node _root = null!;

    public IrFunction Run() {
      this._root = Node.Of(this._function.Body, null);
      this.Visit(this._root);
      return this.Current();
    }

    private IrFunction Current() => this._function with { Body = (IrBlock)this._root.Freeze() };

    private void Visit(Node node) {
      if (node.Value is IrBlock)
        this.Statements(node);
      else if (node.Value is IrExpr && this.Expression(node))
        return;

      for (var position = 0; position < node.Children.Count; ++position) {
        var child = node.Children[position];
        var nested = node.Value is IrBlock && IrStatements.Blocks((IrStmt)child.Value).Count > 0;
        if (nested)
          this._context.Enter(new RuleFrame(node.Children.Select(statement => (IrStmt)statement.Freeze()).ToList(), position));

        this.Visit(child);
        if (nested)
          this._context.Leave();
      }
    }

    private void Statements(Node block) {
      for (var position = 0; position < block.Children.Count;) {
        var statements = block.Children.Select(statement => (IrStmt)statement.Freeze()).ToList();
        StatementRewrite? rewrite = null;
        var rule = rules.FirstOrDefault(candidate => candidate.TryApply(statements, position, this._context, out rewrite));
        if (rule is null || rewrite is null) {
          ++position;
          continue;
        }

        var where = this.Position(block.Children[position]);
        block.Children.RemoveRange(position, rewrite.Count);
        block.Children.InsertRange(position, rewrite.Statements.Select(statement => Node.Of(statement, block)));
        block.Invalidate();
        this.Applied(rule, where);
        position += rewrite.Statements.Count;
      }
    }

    /// <summary>Offers an expression to the rules before its operands; the first rule that applies replaces it.</summary>
    private bool Expression(Node node) {
      var expression = (IrExpr)node.Freeze();
      IrExpr? replacement = null;
      // A rule that would put back what is there - its own result, met again - does not apply.
      var rule = rules.FirstOrDefault(candidate => candidate.TryApply(expression, this._context, out replacement) && !IrShape.Same(replacement, expression));
      if (rule is null || replacement is null)
        return false;

      if (replacement.Type != expression.Type)
        throw new InvalidOperationException($"{rule} replaced an expression of the type {expression.Type} by one of the type {replacement.Type}.");

      node.Replace(replacement);
      this.Applied(rule, this.Position(node));
      return true;
    }

    private void Applied(IrRule rule, string position) {
      applications.Add(new(rule.Id, rule.Name, this._function.Name, position));
      if (!options.Verify)
        return;

      var functions = module.Functions.ToList();
      functions[index] = this.Current();
      var problems = IrVerifier.Verify(module with { Functions = functions });
      if (problems.Count > 0)
        throw new InvalidOperationException($"{rule} left '{this._function.Name}' ill-typed: {string.Join("; ", problems)}");
    }

    /// <summary>The path of the statement a node belongs to, 1-based from the body inwards.</summary>
    private string Position(Node node) {
      var path = new List<int>();
      for (var current = node; current.Parent is { } parent; current = parent)
        if (parent.Value is IrBlock && current.Value is IrStmt)
          path.Add(parent.Children.IndexOf(current) + 1);

      path.Reverse();
      return string.Join('.', path);
    }

  }

  /// <summary>A node of the tree that can be changed: its children, and the IR it stands for, built again only after a change below it.</summary>
  private sealed class Node {

    private object? _frozen;

    private Node(object value, Node? parent) {
      this.Value = value;
      this.Parent = parent;
      this._frozen = value;
      this.Children = [.. Parts(value).Select(child => new Node(child, this))];
    }

    public object Value { get; private set; }

    public Node? Parent { get; }

    public List<Node> Children { get; private set; }

    public static Node Of(object value, Node? parent) => new(value, parent);

    /// <summary>The node replaced by an expression of the same type.</summary>
    public void Replace(IrExpr expression) {
      this.Value = expression;
      this.Children = [.. Parts(expression).Select(child => new Node(child, this))];
      this._frozen = expression;
      this.Parent?.Invalidate();
    }

    /// <summary>Marks the node and every node above it as changed.</summary>
    public void Invalidate() {
      for (var node = this; node is not null && node._frozen is not null; node = node.Parent)
        node._frozen = null;
    }

    /// <summary>The IR the node stands for now.</summary>
    public object Freeze() => this._frozen ??= Rebuild(this.Value, [.. this.Children.Select(child => child.Freeze())]);

    private static IReadOnlyList<object> Parts(object value) => value switch {
      IrExpr expression => IrShape.Operands(expression),
      IrBlock block => block.Statements,
      IrIf conditional => [conditional.Condition, conditional.Then, .. conditional.Else is null ? Array.Empty<object>() : [conditional.Else]],
      IrWhile loop => [loop.Condition, loop.Body],
      IrDoWhile loop => [loop.Body, loop.Condition],
      IrFor loop => [loop.Condition, loop.Body, loop.Step],
      IrSwitch choice => [choice.Value, .. choice.Sections.Select(section => section.Body)],
      IrStmt statement => IrStatements.Expressions(statement),
      _ => []
    };

    private static object Rebuild(object value, IReadOnlyList<object> parts) => value switch {
      IrExpr expression => IrShape.With(expression, [.. parts.Cast<IrExpr>()]),
      IrBlock => new IrBlock([.. parts.Cast<IrStmt>()]),
      IrDeclare declare => declare with { Initializer = parts.Count > 0 ? (IrExpr)parts[0] : null },
      IrAssign assign => assign with { Value = (IrExpr)parts[0] },
      IrGlobalAssign assign => assign with { Value = (IrExpr)parts[0] },
      IrStore => new IrStore((IrExpr)parts[0], (IrExpr)parts[1], (IrExpr)parts[2]),
      IrFieldStore store => store with { Target = (IrExpr)parts[0], Value = (IrExpr)parts[1] },
      IrExprStmt => new IrExprStmt((IrExpr)parts[0]),
      IrIf => new IrIf((IrExpr)parts[0], (IrBlock)parts[1], parts.Count > 2 ? (IrBlock)parts[2] : null),
      IrWhile => new IrWhile((IrExpr)parts[0], (IrBlock)parts[1]),
      IrDoWhile => new IrDoWhile((IrBlock)parts[0], (IrExpr)parts[1]),
      IrFor => new IrFor((IrExpr)parts[0], (IrBlock)parts[1], (IrBlock)parts[2]),
      IrSwitch choice => new IrSwitch((IrExpr)parts[0], [.. choice.Sections.Select((section, position) => section with { Body = (IrBlock)parts[position + 1] })]),
      IrReturn returned => returned with { Value = parts.Count > 0 ? (IrExpr)parts[0] : null },
      _ => value
    };

  }

}
