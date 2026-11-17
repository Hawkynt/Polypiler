using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Polypiler.Core.Analysis.Range;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Provenance;

/// <summary>What an allocation site makes a pointer into (#1331, <c>docs/reference/pointers.md</c>).</summary>
public enum AllocationKind {

  /// <summary>A <c>stackalloc</c>: a block of the stack, as many elements as its count.</summary>
  StackAlloc,

  /// <summary>A pin of an array or a view: its elements, of a length the analysis may not know.</summary>
  Pin,

  /// <summary>The address of a local: the one value of its type.</summary>
  Local

}

/// <summary>
/// The node of the IR a region comes from, compared by identity: two allocations that are equal records
/// are still two sites. Its size is known for a <c>stackalloc</c> of a constant count and for a local.
/// </summary>
/// <param name="Node">The <see cref="IrIntrinsic"/> that makes the region.</param>
/// <param name="Kind">What it makes.</param>
/// <param name="Size">The bytes of the region; <see langword="null"/> when not known.</param>
/// <param name="Name">The variable the region was first stored in, for descriptions.</param>
public sealed record AllocationSite(IrIntrinsic Node, AllocationKind Kind, long? Size, string Name) {

  /// <summary>Whether two sites are one node.</summary>
  /// <param name="other">The other site.</param>
  /// <returns><see langword="true"/> for the same node.</returns>
  public bool Equals(AllocationSite? other) => other is not null && ReferenceEquals(this.Node, other.Node);

  /// <inheritdoc/>
  public override int GetHashCode() => RuntimeHelpers.GetHashCode(this.Node);

  /// <inheritdoc/>
  public override string ToString() => this.Name;

}

/// <summary>
/// The fact of the provenance domain (#93): which allocation sites a pointer may point into - at most
/// <see cref="MaxSites"/>, more is Unknown - its byte offset from the start of the region, and whether it
/// may be the null pointer. A null pointer has no site. The address space is the site's kind.
/// </summary>
public readonly record struct Provenance : IFact<Provenance> {

  /// <summary>The most sites a fact lists before it gives them up.</summary>
  public const int MaxSites = 4;

  /// <summary>Creates a fact.</summary>
  /// <param name="sites">The sites, at most <see cref="MaxSites"/>; more make the fact Unknown.</param>
  /// <param name="offset">The byte offsets from the start of the region.</param>
  /// <param name="mayBeNull">Whether the pointer may be the null pointer.</param>
  public Provenance(ImmutableArray<AllocationSite> sites, ValueRange offset, bool mayBeNull) {
    this.Sites = sites.IsDefault || sites.Length > MaxSites ? default : [.. sites.Distinct()];
    this._offset = offset;
    this._mayBeNull = mayBeNull;
  }

  private readonly ValueRange _offset;
  private readonly bool _mayBeNull;

  /// <summary>The sites, <see langword="default"/> for any.</summary>
  public ImmutableArray<AllocationSite> Sites { get; }

  /// <summary>The byte offsets from the start of the region; Unknown for an Unknown fact.</summary>
  public ValueRange Offset => this.IsUnknown ? ValueRange.Unknown : this._offset;

  /// <summary>Whether the pointer may be the null pointer; always for an Unknown fact.</summary>
  public bool MayBeNull => this.IsUnknown || this._mayBeNull;

  /// <summary>Whether no execution reaches the point.</summary>
  public bool IsUnreachable { get; private init; }

  /// <summary>Whether the fact claims nothing.</summary>
  public bool IsUnknown => this.Sites.IsDefault && !this.IsUnreachable;

  /// <summary>Whether the pointer has exactly one site and is never null.</summary>
  public bool IsSingleSite => !this.Sites.IsDefault && this.Sites.Length == 1 && !this.MayBeNull;

  /// <summary>Any pointer.</summary>
  public static Provenance Unknown => default;

  /// <summary>No pointer: no execution reaches the point.</summary>
  public static Provenance Unreachable { get; } = new(ImmutableArray<AllocationSite>.Empty, ValueRange.Unreachable, false) { IsUnreachable = true };

  /// <summary>The null pointer.</summary>
  public static Provenance Null { get; } = new(ImmutableArray<AllocationSite>.Empty, ValueRange.Of(0), true);

  /// <summary>A pointer into one site.</summary>
  /// <param name="site">The site.</param>
  /// <param name="offset">The byte offsets.</param>
  /// <returns>The fact.</returns>
  public static Provenance Into(AllocationSite site, ValueRange offset) => new([site], offset, false);

  /// <inheritdoc/>
  public static IFactDomain<Provenance> Domain => ProvenanceDomain.Instance;

  /// <summary>Whether two facts claim the same: the same sites in any order, offsets and nullness.</summary>
  /// <param name="other">The other fact.</param>
  /// <returns><see langword="true"/> when they do.</returns>
  public bool Equals(Provenance other) => (this.IsUnknown && other.IsUnknown) || this.IsUnreachable == other.IsUnreachable && this.MayBeNull == other.MayBeNull && this.Offset == other.Offset
    && this.Sites.IsDefault == other.Sites.IsDefault && (this.Sites.IsDefault || (this.Sites.Length == other.Sites.Length && this.Sites.All(other.Sites.Contains)));

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.IsUnreachable, this.MayBeNull, this.Offset, this.Sites.IsDefault ? -1 : this.Sites.Length);

  /// <inheritdoc/>
  public override string ToString() => ProvenanceDomain.Instance.Describe(this);

}

/// <summary>
/// The lattice of <see cref="Provenance"/>: join unites the sites (beyond <see cref="Provenance.MaxSites"/>
/// to Unknown), takes the hull of the offsets and the or of the nullness; meet intersects; widening widens
/// the offsets as the range domain does.
/// </summary>
public sealed class ProvenanceDomain : IFactDomain<Provenance> {

  private ProvenanceDomain() { }

  /// <summary>The domain.</summary>
  public static ProvenanceDomain Instance { get; } = new();

  /// <inheritdoc/>
  public string Name => "provenance";

  /// <inheritdoc/>
  public Provenance Unknown => Provenance.Unknown;

  /// <inheritdoc/>
  public Provenance Unreachable => Provenance.Unreachable;

  /// <inheritdoc/>
  public Provenance Join(Provenance left, Provenance right) => Combine(left, right, RangeDomain.Instance.Join);

  /// <inheritdoc/>
  public Provenance Meet(Provenance left, Provenance right) {
    if (left.IsUnreachable || right.IsUnknown)
      return left;

    if (right.IsUnreachable || left.IsUnknown)
      return right;

    var sites = left.Sites.Intersect(right.Sites).ToImmutableArray();
    var offset = RangeDomain.Instance.Meet(left.Offset, right.Offset);
    var mayBeNull = left.MayBeNull && right.MayBeNull;
    return (sites.Length == 0 && !mayBeNull) || offset.IsEmpty ? Provenance.Unreachable : new Provenance(sites, offset, mayBeNull);
  }

  /// <inheritdoc/>
  public Provenance Widen(Provenance previous, Provenance next) => Combine(previous, next, RangeDomain.Instance.Widen);

  /// <inheritdoc/>
  public bool Implies(Provenance stronger, Provenance weaker) => this.Join(stronger, weaker) == weaker;

  /// <inheritdoc/>
  public bool Contains(Provenance fact, object value) {
    if (fact.IsUnknown)
      return true;

    if (value is not IrObservedPointer pointer || fact.IsUnreachable || !fact.Offset.Contains(pointer.Offset))
      return false;

    return pointer.IsNull ? fact.MayBeNull : fact.Sites.Any(site => site.Size is not { } size || size == pointer.Bytes);
  }

  /// <inheritdoc/>
  public string Describe(Provenance fact) {
    if (fact.IsUnknown)
      return "unknown";

    if (fact.IsUnreachable)
      return "unreachable";

    var sites = fact.Sites.Length == 0 ? "null" : "{" + string.Join(", ", fact.Sites.Select(site => site.Name).Order(StringComparer.Ordinal)) + "}";
    var offset = string.Create(CultureInfo.InvariantCulture, $"[{fact.Offset.Min}, {fact.Offset.Max}]");
    return $"{sites} offset {(fact.Offset.IsUnknown ? "unknown" : offset)}{(fact.MayBeNull && fact.Sites.Length > 0 ? " or null" : "")}";
  }

  private static Provenance Combine(Provenance left, Provenance right, Func<ValueRange, ValueRange, ValueRange> offsets) {
    if (left.IsUnreachable)
      return right;

    if (right.IsUnreachable)
      return left;

    if (left.IsUnknown || right.IsUnknown)
      return Provenance.Unknown;

    return new Provenance([.. left.Sites, .. right.Sites.Where(site => !left.Sites.Contains(site))], offsets(left.Offset, right.Offset), left.MayBeNull || right.MayBeNull);
  }

}
