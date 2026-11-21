using System;
using System.Runtime.CompilerServices;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis;

/// <summary>
/// A place in the structured IR a fact holds at: the point before statement <see cref="Index"/> of
/// <see cref="Block"/>, or its end when the index is the number of statements. The block is the very node
/// of the function - two blocks that are equal records are still two places - so a point compares by the
/// identity of its block. The edge into a branch of an <see cref="IrIf"/> is the entry of that branch.
/// </summary>
public readonly record struct ProgramPoint {

  /// <summary>Creates a point.</summary>
  /// <param name="block">The block.</param>
  /// <param name="index">The statement the point lies before, 0 to the number of statements.</param>
  /// <exception cref="ArgumentNullException"><paramref name="block"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> lies outside the block.</exception>
  public ProgramPoint(IrBlock block, int index) {
    ArgumentNullException.ThrowIfNull(block);
    ArgumentOutOfRangeException.ThrowIfNegative(index);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(index, block.Statements.Count);
    this.Block = block;
    this.Index = index;
  }

  /// <summary>The block the point lies in.</summary>
  public IrBlock Block { get; }

  /// <summary>The statement the point lies before; the number of statements for the end of the block.</summary>
  public int Index { get; }

  /// <summary>Whether the point is the end of its block, after its last statement.</summary>
  public bool IsEnd => this.Index == this.Block.Statements.Count;

  /// <summary>The point before the first statement of a block.</summary>
  /// <param name="block">The block.</param>
  /// <returns>The entry of the block.</returns>
  public static ProgramPoint Entry(IrBlock block) => new(block, 0);

  /// <summary>The point after the last statement of a block, reached when it runs through.</summary>
  /// <param name="block">The block.</param>
  /// <returns>The end of the block.</returns>
  public static ProgramPoint End(IrBlock block) {
    ArgumentNullException.ThrowIfNull(block);
    return new(block, block.Statements.Count);
  }

  /// <summary>Whether two points are one: the same block node and the same index.</summary>
  /// <param name="other">The other point.</param>
  /// <returns><see langword="true"/> for the same place.</returns>
  public bool Equals(ProgramPoint other) => ReferenceEquals(this.Block, other.Block) && this.Index == other.Index;

  /// <summary>The point in words, without its block, which is a whole tree: such as <c>statement 2 of 5</c>.</summary>
  /// <returns>The text.</returns>
  public override string ToString() => this.Block is null ? "no point" : this.IsEnd ? $"end of {this.Block.Statements.Count}" : $"statement {this.Index} of {this.Block.Statements.Count}";

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Block is null ? 0 : RuntimeHelpers.GetHashCode(this.Block), this.Index);

}
