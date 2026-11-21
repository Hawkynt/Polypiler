using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Analysis.Dataflow;
using Polypiler.Core.Analysis.Range;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Provenance;

/// <summary>
/// Pointer provenance (#93) over the bounded pointers of the IR (#1331): for every pointer value, the
/// allocation sites it derives from and its byte offset. A <c>stackalloc</c>, a pin and the address of a
/// local are sites at offset 0 (a pin of element <c>i</c> at <c>i</c> elements); the null value is the null
/// pointer; pointer arithmetic adds the range of its count times the element size - an offset that may
/// leave the 64-bit range is Unknown, the sites stay - and a pointer cast keeps the fact. The counts come
/// from the <see cref="RangeAnalysis"/> of the same function at the same point. Anything else, a parameter
/// among them, is Unknown.
/// </summary>
public sealed class ProvenanceAnalysis : TreeDataflow<Provenance>, IFactProvider<Provenance> {

  private readonly RangeAnalysis _ranges;
  private readonly Dictionary<IrIntrinsic, string> _names = new(ReferenceEqualityComparer.Instance);

  /// <summary>Prepares the analysis of a function; it runs on the first query.</summary>
  /// <param name="function">The function.</param>
  /// <param name="budget">The most statements each walk may visit.</param>
  public ProvenanceAnalysis(IrFunction function, long budget = Constant.ConstantAnalysis.DefaultBudget) : base(function, budget) {
    this._ranges = new RangeAnalysis(function, budget);
    foreach (var (local, value) in Stores(function.Body))
      if (Allocation(value) is { } node)
        this._names.TryAdd(node, local.Name);
  }

  /// <summary>The ranges the counts of pointer arithmetic are read from.</summary>
  public RangeAnalysis Ranges => this._ranges;

  /// <inheritdoc/>
  protected override Provenance Evaluate(IrExpr expression, FactState<Provenance> state) {
    switch (expression) {
      case IrLocalRef reference:
        return state.Fact(reference.Local);
      case IrNull { Type.IsPointer: true }:
        return Provenance.Null;
      case IrConditional choice:
        return ProvenanceDomain.Instance.Join(this.Evaluate(choice.WhenTrue, state), this.Evaluate(choice.WhenFalse, state));
      case IrIntrinsic { Op: IrIntrinsicOp.StackAlloc, Type.IsPointer: true } allocation:
        var count = this.RangeOf(allocation.Arguments[0]);
        long? size = count.Min == count.Max ? (long)count.Min * allocation.Type.Element!.Size : null;
        return Provenance.Into(this.Site(allocation, AllocationKind.StackAlloc, size), ValueRange.Of(0));
      case IrIntrinsic { Op: IrIntrinsicOp.AddressOf } address:
        return Provenance.Into(this.Site(address, AllocationKind.Local, address.Type.Element!.Size), ValueRange.Of(0));
      case IrIntrinsic { Op: IrIntrinsicOp.Pin, Arguments.Count: 1 } pin:
        // A null or empty array pins to the null pointer.
        return new Provenance([this.Site(pin, AllocationKind.Pin, null)], ValueRange.Of(0), true);
      case IrIntrinsic { Op: IrIntrinsicOp.Pin } pin:
        // Element i has to exist, or the pin traps.
        var index = RangeDomain.Instance.Meet(this.RangeOf(pin.Arguments[1]), new ValueRange(0, Int128.MaxValue));
        return Provenance.Into(this.Site(pin, AllocationKind.Pin, null), Scaled(index, pin.Type.Element!.Size));
      case IrIntrinsic { Op: IrIntrinsicOp.PointerAdd } add:
        return Moved(this.Evaluate(add.Arguments[0], state), Scaled(this.RangeOf(add.Arguments[1]), add.Type.Element!.Size));
      case IrIntrinsic { Op: IrIntrinsicOp.PointerCast } cast:
        return this.Evaluate(cast.Arguments[0], state);
      default:
        return Provenance.Unknown;
    }
  }

  /// <summary>Whether an access of some bytes through a pointer with a fact stays within its region (#93).</summary>
  /// <param name="pointer">The fact of the pointer.</param>
  /// <param name="accessBytes">The bytes the access reads or writes, at least 1.</param>
  /// <returns>
  /// <see cref="Truth.True"/> when the pointer is never null, every site has a known size <c>S</c> and every
  /// offset lies in <c>[0, S - accessBytes]</c>; <see cref="Truth.False"/> when every pointer it may be - the
  /// null pointer included - leaves its region; <see cref="Truth.Unknown"/> otherwise.
  /// </returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="accessBytes"/> is below 1.</exception>
  public static Truth InBounds(Provenance pointer, long accessBytes) {
    ArgumentOutOfRangeException.ThrowIfLessThan(accessBytes, 1);
    if (pointer.IsUnknown || pointer.IsUnreachable)
      return Truth.Unknown;

    var (low, high) = (pointer.Offset.Min, pointer.Offset.Max);
    if (!pointer.MayBeNull && pointer.Sites.All(site => site.Size is { } size && low >= 0 && high + accessBytes <= size))
      return Truth.True;

    return pointer.Sites.All(site => high < 0 || (site.Size is { } size && low + accessBytes > size)) ? Truth.False : Truth.Unknown;
  }

  private ValueRange RangeOf(IrExpr count) => this.Current is { } point ? this._ranges.RangeAt(count, point) : ValueRange.Of(count.Type);

  private AllocationSite Site(IrIntrinsic node, AllocationKind kind, long? size)
    => new(node, kind, size, this._names.TryGetValue(node, out var name) ? name : kind.ToString());

  /// <summary>The 64-bit offsets a pointer may have.</summary>
  private static ValueRange Long { get; } = new(long.MinValue, long.MaxValue);

  /// <summary>A pointer moved by a range of bytes: Unknown offsets where the sum may leave 64 bits.</summary>
  private static Provenance Moved(Provenance pointer, ValueRange bytes) {
    if (pointer.IsUnknown || pointer.IsUnreachable)
      return pointer;

    // Both within 64 bits, the sum cannot leave 128.
    var fits = Long.Covers(pointer.Offset) && Long.Covers(bytes) && Long.Covers(new ValueRange(pointer.Offset.Min + bytes.Min, pointer.Offset.Max + bytes.Max));
    return new Provenance(pointer.Sites, fits ? new ValueRange(pointer.Offset.Min + bytes.Min, pointer.Offset.Max + bytes.Max) : ValueRange.Unknown, pointer.MayBeNull);
  }

  /// <summary>A range of elements as bytes; Unknown where it leaves 64 bits.</summary>
  private static ValueRange Scaled(ValueRange elements, int size) {
    if (elements.IsEmpty || !Long.Covers(elements))
      return ValueRange.Unknown;

    var bytes = new ValueRange(elements.Min * size, elements.Max * size);
    return Long.Covers(bytes) ? bytes : ValueRange.Unknown;
  }

  /// <summary>The allocation a stored value is, through pointer casts.</summary>
  private static IrIntrinsic? Allocation(IrExpr value) => value switch {
    IrIntrinsic { Op: IrIntrinsicOp.PointerCast } cast => Allocation(cast.Arguments[0]),
    IrIntrinsic { Op: IrIntrinsicOp.StackAlloc or IrIntrinsicOp.AddressOf or IrIntrinsicOp.Pin } node => node,
    _ => null
  };

  /// <summary>Every value stored into a variable, by declaration or assignment, in the order of the tree.</summary>
  private static IEnumerable<(IrLocal Local, IrExpr Value)> Stores(IrBlock body) {
    foreach (var statement in body.Statements) {
      switch (statement) {
        case IrDeclare { Initializer: { } value } declare:
          yield return (declare.Local, value);
          break;
        case IrAssign assign:
          yield return (assign.Local, assign.Value);
          break;
      }

      foreach (var inner in IrTree.Blocks(statement))
        foreach (var store in Stores(inner))
          yield return store;
    }
  }

}

/// <summary>The provenance queries of the facade (#93).</summary>
public static class ProvenanceFacts {

  /// <summary>The provenance of a pointer at a point: <see cref="ValueFacts.Get{TFact}(IrExpr, ProgramPoint)"/> of <see cref="Provenance"/>.</summary>
  /// <param name="facts">The facade.</param>
  /// <param name="value">The pointer.</param>
  /// <param name="point">The point.</param>
  /// <returns>The fact; Unknown when no provenance analysis is registered.</returns>
  public static Provenance ProvenanceAt(this ValueFacts facts, IrExpr value, ProgramPoint point) {
    ArgumentNullException.ThrowIfNull(facts);
    return facts.Get<Provenance>(value, point);
  }

  /// <summary>Whether an access of some bytes through a pointer stays within its region at a point (<see cref="ProvenanceAnalysis.InBounds"/>).</summary>
  /// <param name="facts">The facade.</param>
  /// <param name="pointer">The pointer.</param>
  /// <param name="accessBytes">The bytes the access reads or writes, at least 1.</param>
  /// <param name="point">The point.</param>
  /// <returns>The answer; Unknown when no provenance analysis is registered.</returns>
  public static Truth IsInBounds(this ValueFacts facts, IrExpr pointer, long accessBytes, ProgramPoint point)
    => ProvenanceAnalysis.InBounds(facts.ProvenanceAt(pointer, point), accessBytes);

}
