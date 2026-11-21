using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis;

/// <summary>
/// The blocks of a function and where each lies: the block and statement that hold it, its number in
/// pre-order (the body is block 0) and which variables are defined at each of its points. The blocks
/// of a lambda's body belong to the lambda, not to the function.
/// </summary>
public sealed class FunctionLayout {

  private readonly Dictionary<IrBlock, Place> _places = new(ReferenceEqualityComparer.Instance);
  private readonly HashSet<IrBlock> _repeated = new(ReferenceEqualityComparer.Instance);
  private readonly HashSet<IrLocal> _parameters;

  /// <summary>Lays out a function.</summary>
  /// <param name="function">The function.</param>
  /// <exception cref="ArgumentNullException"><paramref name="function"/> is <see langword="null"/>.</exception>
  public FunctionLayout(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    this.Function = function;
    this._parameters = [.. function.Parameters];
    this.Add(function.Body, null, null, -1);
  }

  /// <summary>The function.</summary>
  public IrFunction Function { get; }

  /// <summary>Whether a block is a block of the function.</summary>
  /// <param name="block">The block.</param>
  /// <returns><see langword="true"/> when the function holds this very node.</returns>
  public bool Contains(IrBlock block) => this._places.ContainsKey(block);

  /// <summary>The point in words: the number of its block and the statement, such as <c>block 2, statement 0</c>.</summary>
  /// <param name="point">A point of the function.</param>
  /// <returns>The text.</returns>
  /// <exception cref="KeyNotFoundException">The point is not in the function.</exception>
  public string Describe(ProgramPoint point) {
    var number = this._places[point.Block].Number;
    return point.IsEnd ? $"block {number}, end" : $"block {number}, statement {point.Index}";
  }

  /// <summary>
  /// Checks that a value may be asked about at a point: the point lies in the function, its block
  /// occurs once, and every variable the value reads is defined there - a parameter, or declared or
  /// bound before the point on the way from the body to it.
  /// </summary>
  /// <param name="value">The value.</param>
  /// <param name="point">The point.</param>
  /// <exception cref="ArgumentException">The point lies outside the function, in a block that occurs twice, or before a variable the value reads is defined.</exception>
  public void Check(IrExpr value, ProgramPoint point) {
    ArgumentNullException.ThrowIfNull(value);
    if (point.Block is null || !this._places.ContainsKey(point.Block))
      throw new ArgumentException($"The point is not in a block of '{this.Function.Name}'.", nameof(point));

    if (this._repeated.Contains(point.Block))
      throw new ArgumentException($"The {this.Describe(point)} of '{this.Function.Name}' is one node in several places, so a point in it names no single place.", nameof(point));

    foreach (var local in IrTree.Nodes(value).OfType<IrLocalRef>().Select(reference => reference.Local))
      if (!this.Defined(local, point))
        throw new ArgumentException($"'{local.Name}' is not defined at {this.Describe(point)} of '{this.Function.Name}'.", nameof(value));
  }

  /// <summary>Whether a variable is defined at a point of the function.</summary>
  /// <param name="local">The variable.</param>
  /// <param name="point">A point of the function.</param>
  /// <returns><see langword="true"/> for a parameter, or a variable declared or bound before the point.</returns>
  /// <exception cref="KeyNotFoundException">The point is not in the function.</exception>
  public bool Defined(IrLocal local, ProgramPoint point) {
    if (this._parameters.Contains(local))
      return true;

    var (block, index) = (point.Block, point.Index);
    while (true) {
      var place = this._places[block];
      for (var before = 0; before < index; ++before)
        if (Defines(block.Statements[before], local))
          return true;

      if (place.Parent is null)
        return false;

      if (place.Owner is { } owner && Binds(owner, local, block))
        return true;

      (block, index) = (place.Parent, place.Index);
    }
  }

  /// <summary>Whether a statement, once it has run, leaves a variable defined: a declaration, or a binding in one of its expressions.</summary>
  private static bool Defines(IrStmt statement, IrLocal local)
    => statement is IrDeclare declare && declare.Local == local
      || IrTree.Expressions(statement).SelectMany(IrTree.Nodes).OfType<IrBind>().Any(bind => bind.Local == local);

  /// <summary>Whether a statement makes a variable for one of its blocks: a loop's element, a caught exception, a resource, or a binding of its condition.</summary>
  private static bool Binds(IrStmt owner, IrLocal local, IrBlock block) => owner switch {
    IrForEachCollection loop => loop.Element == local || loop.Value == local,
    IrForEachSequence loop => loop.Element == local,
    IrUsing scope => scope.Resource == local,
    IrTry attempt => attempt.Catches.Any(clause => ReferenceEquals(clause.Body, block) && clause.Local == local),
    _ => IrTree.Expressions(owner).SelectMany(IrTree.Nodes).OfType<IrBind>().Any(bind => bind.Local == local)
  };

  private void Add(IrBlock block, IrBlock? parent, IrStmt? owner, int index) {
    if (!this._places.TryAdd(block, new Place(this._places.Count, parent, owner, index))) {
      this._repeated.Add(block);
      return;
    }

    for (var position = 0; position < block.Statements.Count; ++position) {
      var statement = block.Statements[position];
      foreach (var inner in IrTree.Blocks(statement))
        if (ReferenceEquals(inner, statement))
          this.Add(inner, block, null, position);
        else
          this.Add(inner, block, statement, position);
    }
  }

  /// <summary>Where a block lies: its number, the block and the index of the statement holding it, and that statement (<see langword="null"/> for a nested block statement and for the body).</summary>
  private sealed record Place(int Number, IrBlock? Parent, IrStmt? Owner, int Index);

}
