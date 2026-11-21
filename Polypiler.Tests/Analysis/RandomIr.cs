using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Analysis;

/// <summary>How large the functions of <see cref="RandomIr"/> are.</summary>
/// <param name="Parameters">The number of <c>u32</c> parameters.</param>
/// <param name="Locals">The number of <c>u32</c> variables declared at the start of the body.</param>
/// <param name="Statements">The number of statements of the body after the declarations.</param>
/// <param name="Depth">How deeply branches and loops nest.</param>
internal sealed record RandomIrOptions(int Parameters = 2, int Locals = 3, int Statements = 6, int Depth = 2);

/// <summary>
/// A seeded generator of small integer functions for the soundness tests of the analyses: straight-line
/// code, branches on comparisons and bounded counting loops with <c>break</c> and <c>continue</c>, over
/// <c>u32</c> values with the integer operations of the IR. The same seed gives the same function.
/// </summary>
internal sealed class RandomIr {

  private static readonly IrBinaryOp[] _arithmetic = [
    IrBinaryOp.Add, IrBinaryOp.Subtract, IrBinaryOp.Multiply, IrBinaryOp.And, IrBinaryOp.Or, IrBinaryOp.Xor, IrBinaryOp.Divide, IrBinaryOp.Remainder
  ];

  private static readonly IrBinaryOp[] _comparisons = [IrBinaryOp.Equal, IrBinaryOp.NotEqual, IrBinaryOp.Less, IrBinaryOp.LessOrEqual, IrBinaryOp.Greater];

  private readonly Random _random;
  private readonly RandomIrOptions _options;
  private readonly List<IrLocal> _parameters;
  private readonly List<IrLocal> _variables;
  private readonly List<IrLocal> _counters = [];

  private RandomIr(int seed, RandomIrOptions options) {
    this._random = new Random(seed);
    this._options = options;
    this._parameters = [.. Enumerable.Range(0, options.Parameters).Select(index => new IrLocal("p" + index, IrType.U32))];
    this._variables = [.. Enumerable.Range(0, options.Locals).Select(index => new IrLocal("v" + index, IrType.U32))];
  }

  /// <summary>A function named <c>f</c>.</summary>
  public static IrFunction Generate(int seed, RandomIrOptions? options = null) => new RandomIr(seed, options ?? new RandomIrOptions()).Function();

  private IrFunction Function() {
    var body = new List<IrStmt>();
    foreach (var variable in this._variables)
      body.Add(new IrDeclare(variable, this.Expression(2, body.Count)));

    body.AddRange(this.Statements(this._options.Statements, this._options.Depth, false));
    body.Add(new IrReturn(new IrLocalRef(this._variables[0])));
    return new IrFunction("f", true, IrType.U32, this._parameters, [.. this._variables, .. this._counters], new IrBlock(body));
  }

  private List<IrStmt> Statements(int count, int depth, bool inLoop) {
    var statements = new List<IrStmt>();
    for (var index = 0; index < count; ++index)
      switch (this._random.Next(depth > 0 ? 10 : 6)) {
        case < 5:
          statements.Add(new IrAssign(this.Pick(this._variables), this.Expression(2, this._variables.Count)));
          break;
        case 5 when inLoop:
          statements.Add(new IrIf(this.Condition(), new IrBlock([this._random.Next(2) == 0 ? new IrBreak() : new IrContinue()]), null));
          break;
        case 5:
          statements.Add(new IrAssign(this.Pick(this._variables), this.Constant()));
          break;
        case < 8:
          var then = this.Statements(this._random.Next(1, 3), depth - 1, inLoop);
          var otherwise = this._random.Next(2) == 0 ? null : new IrBlock(this.Statements(this._random.Next(1, 3), depth - 1, inLoop));
          statements.Add(new IrIf(this.Condition(), new IrBlock(then), otherwise));
          break;
        default:
          statements.AddRange(this.Loop(depth));
          break;
      }

    return statements;
  }

  /// <summary>A counter from 0 below a small bound, the body first, the increment last.</summary>
  private IEnumerable<IrStmt> Loop(int depth) {
    var counter = new IrLocal("i" + this._counters.Count, IrType.U32);
    this._counters.Add(counter);
    var body = this.Statements(this._random.Next(1, 3), depth - 1, true);
    var step = new IrAssign(counter, new IrBinary(IrBinaryOp.Add, new IrLocalRef(counter), new IrConst(IrType.U32, 1)));
    var condition = new IrBinary(IrBinaryOp.Less, new IrLocalRef(counter), new IrConst(IrType.U32, this._random.Next(0, 5)));
    yield return new IrDeclare(counter, new IrConst(IrType.U32, 0));
    yield return new IrFor(condition, new IrBlock(body), new IrBlock([step]));
  }

  private IrExpr Condition() {
    var left = new IrLocalRef(this.Pick([.. this._parameters, .. this._variables]));
    return new IrBinary(_comparisons[this._random.Next(_comparisons.Length)], left, this._random.Next(2) == 0 ? this.Constant() : this.Leaf(this._variables.Count));
  }

  private IrExpr Expression(int depth, int declared) {
    if (depth == 0 || this._random.Next(3) == 0)
      return this.Leaf(declared);

    return new IrBinary(_arithmetic[this._random.Next(_arithmetic.Length)], this.Expression(depth - 1, declared), this.Expression(depth - 1, declared));
  }

  /// <summary>A constant, a parameter or a variable declared already.</summary>
  private IrExpr Leaf(int declared) {
    var choices = this._parameters.Concat(this._variables.Take(declared)).ToList();
    return this._random.Next(3) == 0 || choices.Count == 0 ? this.Constant() : new IrLocalRef(this.Pick(choices));
  }

  private IrConst Constant() => new(IrType.U32, this._random.Next(4) == 0 ? this._random.Next() : this._random.Next(0, 8));

  private IrLocal Pick(IReadOnlyList<IrLocal> locals) => locals[this._random.Next(locals.Count)];

}
