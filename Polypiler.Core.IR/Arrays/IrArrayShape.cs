using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>Which dimension of an array of several varies fastest along memory (#111).</summary>
public enum IrStorageOrder {

  /// <summary>The last dimension varies fastest, as in C, C# and BASIC: row-major.</summary>
  Row,

  /// <summary>The first dimension varies fastest, as in Fortran: column-major.</summary>
  Column

}

/// <summary>One dimension of an array: its lowest index and how many indices it has (#111).</summary>
/// <param name="Lower">The lowest index, 0 in C, 1 in <c>DIM a(1 TO 10)</c>, any integer.</param>
/// <param name="Extent">How many indices, at least 0.</param>
public sealed record IrDimension(long Lower, long Extent) {

  /// <summary>The highest index, one below <see cref="Lower"/> for a dimension of no indices.</summary>
  public long Upper => this.Lower + this.Extent - 1;

  /// <summary>Whether an index lies in the dimension.</summary>
  /// <param name="index">The index.</param>
  /// <returns><see langword="true"/> from <see cref="Lower"/> to <see cref="Upper"/>.</returns>
  public bool Contains(long index) => index >= this.Lower && unchecked((ulong)(index - this.Lower)) < (ulong)this.Extent;

  /// <summary>The dimension as <c>lower..upper</c>.</summary>
  public override string ToString() => $"{this.Lower}..{this.Upper}";

}

/// <summary>
/// The shape of an array laid out in memory (#111): rank 1 to 8, per dimension a lower bound and an extent, and the
/// storage order. The element at the indices <c>(i_0, …, i_{r-1})</c> is the element at the linear position
/// <c>Σ (i_d − lower_d) · stride_d</c>, where with row order the last dimension has the stride 1 and every other the
/// product of the extents after it, and with column order the first has the stride 1 and every other the product of
/// the extents before it (ECMA-335 §II.14.2, ISO C §6.5.2.1; <c>spec/ir/operations/elemaddr.yaml</c>). Each index is
/// checked against its own dimension, never only the linear position.
/// </summary>
public sealed record IrArrayShape {

  /// <summary>The largest rank.</summary>
  public const int MaxRank = 8;

  /// <summary>Makes a shape.</summary>
  /// <param name="dimensions">The dimensions, the first one first; 1 to <see cref="MaxRank"/> of them.</param>
  /// <param name="order">The storage order.</param>
  /// <exception cref="ArgumentException">There are no dimensions or more than <see cref="MaxRank"/>, or an extent is negative.</exception>
  public IrArrayShape(IReadOnlyList<IrDimension> dimensions, IrStorageOrder order = IrStorageOrder.Row) {
    ArgumentNullException.ThrowIfNull(dimensions);
    if (dimensions.Count is < 1 or > MaxRank)
      throw new ArgumentException($"an array of rank {dimensions.Count}; the rank is 1 to {MaxRank}", nameof(dimensions));

    if (dimensions.FirstOrDefault(dimension => dimension.Extent < 0) is { } negative)
      throw new ArgumentException($"a dimension of the extent {negative.Extent}", nameof(dimensions));

    this.Dimensions = [.. dimensions];
    this.Order = order;
  }

  /// <summary>The fixed-length array <c>array&lt;T, n&gt;</c>: one dimension of <paramref name="length"/> indices from 0.</summary>
  /// <param name="length">The number of elements.</param>
  /// <returns>The shape.</returns>
  public static IrArrayShape Fixed(long length) => new([new IrDimension(0, length)]);

  /// <summary>The dimensions, the first one first.</summary>
  public IReadOnlyList<IrDimension> Dimensions { get; }

  /// <summary>The storage order.</summary>
  public IrStorageOrder Order { get; }

  /// <summary>The number of dimensions.</summary>
  public int Rank => this.Dimensions.Count;

  /// <summary>The number of elements: the product of the extents.</summary>
  /// <exception cref="OverflowException">The product does not fit a 64-bit integer.</exception>
  public long Count => this.Dimensions.Aggregate(1L, (product, dimension) => checked(product * dimension.Extent));

  /// <summary>The stride of every dimension, in elements, in dimension order.</summary>
  public IReadOnlyList<long> Strides {
    get {
      var strides = new long[this.Rank];
      var stride = 1L;
      for (var step = 0; step < this.Rank; ++step) {
        var dimension = this.Order == IrStorageOrder.Row ? this.Rank - 1 - step : step;
        strides[dimension] = stride;
        stride = unchecked(stride * this.Dimensions[dimension].Extent);
      }

      return strides;
    }
  }

  /// <summary>The first dimension an index of which lies outside it, or -1 when all lie inside.</summary>
  /// <param name="indices">One index per dimension.</param>
  /// <returns>The dimension, or -1.</returns>
  /// <exception cref="ArgumentException">The number of indices is not the rank.</exception>
  public int Outside(IReadOnlyList<long> indices) {
    this.Arity(indices);
    for (var dimension = 0; dimension < this.Rank; ++dimension)
      if (!this.Dimensions[dimension].Contains(indices[dimension]))
        return dimension;

    return -1;
  }

  /// <summary>The linear position of the element at the indices, each checked against its dimension.</summary>
  /// <param name="indices">One index per dimension.</param>
  /// <returns>The position, from 0 to <see cref="Count"/> - 1.</returns>
  /// <exception cref="ArgumentException">The number of indices is not the rank.</exception>
  /// <exception cref="IrTrapException">An index lies outside its dimension: an index out of range.</exception>
  public long Linear(IReadOnlyList<long> indices) {
    if (this.Outside(indices) >= 0)
      throw new IrTrapException(IrTrap.IndexOutOfRange);

    return this.Unchecked(indices);
  }

  /// <summary>The linear position the formula gives for the indices without checking them, wrapping on overflow.</summary>
  /// <param name="indices">One index per dimension.</param>
  /// <returns>The position, which may lie outside the array.</returns>
  /// <exception cref="ArgumentException">The number of indices is not the rank.</exception>
  public long Unchecked(IReadOnlyList<long> indices) {
    this.Arity(indices);
    var strides = this.Strides;
    var linear = 0L;
    for (var dimension = 0; dimension < this.Rank; ++dimension)
      linear = unchecked(linear + ((indices[dimension] - this.Dimensions[dimension].Lower) * strides[dimension]));

    return linear;
  }

  /// <summary>Whether two shapes have the same dimensions and order.</summary>
  /// <param name="other">The other shape.</param>
  /// <returns><see langword="true"/> for equal shapes.</returns>
  public bool Equals(IrArrayShape? other) => other is not null && this.Order == other.Order && this.Dimensions.SequenceEqual(other.Dimensions);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Order, this.Rank, this.Dimensions[0]);

  /// <summary>The shape as <c>[0..2, 1..4] row</c>.</summary>
  public override string ToString() => $"[{string.Join(", ", this.Dimensions)}] {this.Order.ToString().ToLowerInvariant()}";

  private void Arity(IReadOnlyList<long> indices) {
    ArgumentNullException.ThrowIfNull(indices);
    if (indices.Count != this.Rank)
      throw new ArgumentException($"{indices.Count} indices for an array of rank {this.Rank}", nameof(indices));
  }

}

/// <summary>
/// The address of an element of an array that lies in memory (#111): <c>ElemAddr</c>. The base points at the
/// element at the lowest index of every dimension; the result points at the element the indices name, at the linear
/// position <see cref="IrArrayShape.Linear"/> gives, in the same region. Each index is extended by its own type -
/// signed or unsigned, never truncated. A checked address traps with an index out of range when an index lies
/// outside its dimension, before anything is read; an unchecked one is formed without a check, and the load or the
/// store through it checks the region, as for every pointer.
/// </summary>
/// <param name="Base">A pointer to a number at the first element.</param>
/// <param name="Shape">The shape of the array.</param>
/// <param name="Indices">One integer index per dimension, the first dimension first.</param>
/// <param name="Checked">Whether every index is checked against its dimension.</param>
public sealed record IrElementAddress(IrExpr Base, IrArrayShape Shape, IReadOnlyList<IrExpr> Indices, bool Checked) : IrExpr(Base.Type);
