using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Dynamic;

/// <summary>The missing values of the dynamic languages, which an <see cref="DynamicOptional"/> may hold besides its value.</summary>
[Flags]
public enum DynamicMissing {

  /// <summary>No missing value.</summary>
  None = 0,

  /// <summary>A value known to be missing: Visual Basic's <c>Null</c>, JavaScript's <c>null</c>, Python's <c>None</c>, Perl's <c>undef</c>.</summary>
  Null = 1,

  /// <summary>JavaScript's second missing value, <c>undefined</c>.</summary>
  Undefined = 2,

  /// <summary>A value not assigned yet: Visual Basic's <c>Empty</c>, which is 0 or <c>""</c> by context.</summary>
  Empty = 4

}

/// <summary>
/// The rule of a language's semantics table the lattice asks when two exact scalars meet (ADR 0300
/// decision 2): whether the program can tell them apart. A JavaScript <c>i32</c> and <c>f64</c> are
/// both Numbers and join to <c>f64</c>; a Visual Basic <c>Integer</c> and <c>Long</c> are two kinds that
/// <c>VarType</c> tells apart and join to a union.
/// </summary>
/// <remarks>
/// The types a policy joins must form classes: if it joins <c>a</c> with <c>b</c> and <c>b</c> with
/// <c>c</c>, it joins <c>a</c> with <c>c</c>, and every join of the class stays in it, commutatively
/// and associatively; otherwise the joins of the lattice are not associative.
/// </remarks>
public interface IJoinPolicy {

  /// <summary>The type two different exact scalars join to when the language cannot tell them apart, or <see langword="null"/> when it can.</summary>
  /// <param name="left">One type.</param>
  /// <param name="right">The other type, never equal to <paramref name="left"/>.</param>
  /// <returns>The wider type, or <see langword="null"/>.</returns>
  IrType? Join(IrType left, IrType right);

}

/// <summary>A policy that tells every two types apart: their join is always a union.</summary>
public sealed class DistinctJoins : IJoinPolicy {

  private DistinctJoins() { }

  /// <summary>The policy.</summary>
  public static DistinctJoins Instance { get; } = new();

  /// <inheritdoc/>
  public IrType? Join(IrType left, IrType right) => null;

}

/// <summary>
/// What a place - a local, a parameter, a result, a global, a field, an element - may hold, from the
/// most static to the least (ADR 0300 decision 2): <see cref="DynamicNothing"/> (level 0),
/// <see cref="DynamicExact"/> (1), <see cref="DynamicNominal"/> (2), <see cref="DynamicShape"/> (3),
/// <see cref="DynamicOptional"/> (4), <see cref="DynamicUnion"/> (5) and <see cref="DynamicVariant"/>
/// (6). The elements are kept canonical, so two that mean the same are equal: an optional is always
/// the outermost element - a union's members are of levels 1 to 3 and the missing values of a union
/// are those of the optional around it - and a union has 2 to <see cref="MostMembers"/> members in the
/// order of their names.
/// </summary>
public abstract record DynamicType {

  /// <summary>The most members a union has; a join that would give more is a variant.</summary>
  public const int MostMembers = 4;

  /// <summary>The most nesting of shapes in shapes a widening keeps; deeper is a variant.</summary>
  public const int MostDepth = 4;

  private protected DynamicType() { }

  /// <summary>The element no value reaches: dead code. It is the identity of the join.</summary>
  public static DynamicType Nothing { get; } = new DynamicNothing();

  /// <summary>The element of a place that may hold anything. It absorbs every join.</summary>
  public static DynamicType Variant { get; } = new DynamicVariant();

  /// <summary>The level of the element in the lattice, 0 to 6.</summary>
  public abstract int Level { get; }

  /// <summary>An exact type of the IR.</summary>
  /// <param name="type">The type: any type of the IR but <c>variant</c>, <c>void</c> and a union.</param>
  /// <param name="tag">The kind the language tells the value apart by where two kinds share one IR type, such as Visual Basic's <c>Currency</c> on an <c>i64</c>; <see langword="null"/> for the type alone.</param>
  /// <returns>The element.</returns>
  /// <exception cref="ArgumentException">The type is <c>variant</c>, <c>void</c> or a union.</exception>
  public static DynamicType Exact(IrType type, IrVariantTag? tag = null) {
    ArgumentNullException.ThrowIfNull(type);
    if (type.IsVariant || type == IrType.Void || type.IsUnion)
      throw new ArgumentException($"The type {type} is no exact element.", nameof(type));
    return new DynamicExact(type, tag == IrVariants.TagOf(type) ? null : tag);
  }

  /// <summary>
  /// The element of a value of the dynamic type that holds a tag: the missing value <c>Empty</c> or
  /// <c>Null</c>, else the exact payload type with the tag where the payload type alone does not name it.
  /// </summary>
  /// <param name="tag">The tag.</param>
  /// <returns>The element.</returns>
  public static DynamicType OfTag(IrVariantTag tag) => tag switch {
    IrVariantTag.Empty => Optional(Nothing, DynamicMissing.Empty),
    IrVariantTag.Null => Optional(Nothing, DynamicMissing.Null),
    _ => Exact(IrVariants.PayloadType(tag) ?? throw new ArgumentException($"The tag {tag} has no element.", nameof(tag)), tag)
  };

  /// <summary>One declared class, struct, interface or enum.</summary>
  /// <param name="declaration">The declaration.</param>
  /// <returns>The element.</returns>
  public static DynamicType Nominal(IrTypeDeclaration declaration) {
    ArgumentNullException.ThrowIfNull(declaration);
    return new DynamicNominal(declaration);
  }

  /// <summary>A record of named fields, each with its own element.</summary>
  /// <param name="fields">The fields by name.</param>
  /// <returns>The element.</returns>
  public static DynamicType Shape(IReadOnlyDictionary<string, DynamicType> fields) {
    ArgumentNullException.ThrowIfNull(fields);
    foreach (var field in fields.Values)
      ArgumentNullException.ThrowIfNull(field, nameof(fields));
    return new DynamicShape(new SortedDictionary<string, DynamicType>(fields.ToDictionary(), StringComparer.Ordinal));
  }

  /// <summary>An element or the language's missing values: canonical, so an optional of a variant is a variant and an optional of an optional one optional.</summary>
  /// <param name="value">The element of the value when it is not missing; <see cref="Nothing"/> for a place that only ever holds a missing value.</param>
  /// <param name="missing">The missing values; <see cref="DynamicMissing.None"/> gives the element itself.</param>
  /// <returns>The element.</returns>
  public static DynamicType Optional(DynamicType value, DynamicMissing missing) {
    ArgumentNullException.ThrowIfNull(value);
    return value switch {
      DynamicVariant => Variant,
      DynamicOptional optional => Optional(optional.Value, optional.Missing | missing),
      _ when missing == DynamicMissing.None => value,
      _ => new DynamicOptional(value, missing)
    };
  }

  /// <summary>The join of elements under a policy: a union of them, or what the policy and the rules make of it.</summary>
  /// <param name="policy">The policy.</param>
  /// <param name="members">The elements.</param>
  /// <returns>The join of all of them; <see cref="Nothing"/> for none.</returns>
  public static DynamicType Union(IJoinPolicy policy, params DynamicType[] members) {
    ArgumentNullException.ThrowIfNull(members);
    return members.Aggregate(Nothing, (union, member) => Join(union, member, policy));
  }

  /// <summary>
  /// The least element both elements are below: <see cref="Nothing"/> is the identity and
  /// <see cref="Variant"/> absorbs; missing values join into the optional around the rest; two exact
  /// types join to one where they are equal or the policy joins them, two shapes of the same field names
  /// field by field, and everything else to a union - a variant when it would have more than
  /// <see cref="MostMembers"/> members.
  /// </summary>
  /// <param name="left">One element.</param>
  /// <param name="right">The other element.</param>
  /// <param name="policy">The policy of the language.</param>
  /// <returns>The join.</returns>
  public static DynamicType Join(DynamicType left, DynamicType right, IJoinPolicy policy) {
    ArgumentNullException.ThrowIfNull(left);
    ArgumentNullException.ThrowIfNull(right);
    ArgumentNullException.ThrowIfNull(policy);
    if (left is DynamicNothing || left == right)
      return right;
    if (right is DynamicNothing)
      return left;
    if (left is DynamicVariant || right is DynamicVariant)
      return Variant;
    if (left is DynamicOptional || right is DynamicOptional) {
      var (leftValue, leftMissing) = Split(left);
      var (rightValue, rightMissing) = Split(right);
      return Optional(Join(leftValue, rightValue, policy), leftMissing | rightMissing);
    }

    var members = new List<DynamicType>(Members(left));
    foreach (var member in Members(right)) {
      var index = members.FindIndex(known => Merge(known, member, policy) is not null);
      if (index < 0)
        members.Add(member);
      else
        members[index] = Merge(members[index], member, policy)!;
    }

    return members.Count switch {
      1 => members[0],
      > MostMembers => Variant,
      _ => new DynamicUnion([.. members.OrderBy(member => member.ToString(), StringComparer.Ordinal)])
    };
  }

  /// <summary>
  /// The greatest element below both: what holds where both hold, as a test refines a value -
  /// <see cref="Nothing"/> where they have nothing in common.
  /// </summary>
  /// <param name="left">One element.</param>
  /// <param name="right">The other element.</param>
  /// <param name="policy">The policy of the language.</param>
  /// <returns>The meet.</returns>
  public static DynamicType Meet(DynamicType left, DynamicType right, IJoinPolicy policy) {
    ArgumentNullException.ThrowIfNull(left);
    ArgumentNullException.ThrowIfNull(right);
    ArgumentNullException.ThrowIfNull(policy);
    if (left is DynamicVariant || left == right)
      return right;
    if (right is DynamicVariant)
      return left;
    if (left is DynamicNothing || right is DynamicNothing)
      return Nothing;
    if (left is DynamicOptional || right is DynamicOptional) {
      var (leftValue, leftMissing) = Split(left);
      var (rightValue, rightMissing) = Split(right);
      return Optional(Meet(leftValue, rightValue, policy), leftMissing & rightMissing);
    }

    return Members(left).SelectMany(a => Members(right).Select(b => Below(a, b, policy))).Aggregate(Nothing, (meet, member) => Join(meet, member, policy));
  }

  /// <summary>Whether this element is below another: their join is the other.</summary>
  /// <param name="other">The other element.</param>
  /// <param name="policy">The policy of the language.</param>
  /// <returns><see langword="true"/> when every value this element allows the other allows too.</returns>
  public bool LessOrEqual(DynamicType other, IJoinPolicy policy) => Join(this, other, policy) == other;

  /// <summary>
  /// The type of the IR a place of this element is given: the exact type, the declared type, the
  /// optional type over a value type or a reference type (whose <c>null</c> is the missing value) for
  /// an optional of one missing value, and the union of the members' types; <see langword="null"/> for
  /// <see cref="Nothing"/>, a shape - which becomes a generated type (#1588) - and <see cref="Variant"/>,
  /// and for every element whose parts have no type.
  /// </summary>
  /// <returns>The type, or <see langword="null"/>.</returns>
  public abstract IrType? ToIrType();

  /// <summary>How deep shapes nest in this element: 0 for an element without one.</summary>
  public virtual int Depth => 0;

  /// <summary>The value and the missing values of an element.</summary>
  private static (DynamicType Value, DynamicMissing Missing) Split(DynamicType type)
    => type is DynamicOptional optional ? (optional.Value, optional.Missing) : (type, DynamicMissing.None);

  /// <summary>The members of an element of levels 0 to 3 or of a union.</summary>
  private static IReadOnlyList<DynamicType> Members(DynamicType type) => type switch {
    DynamicUnion union => union.Members,
    DynamicNothing => [],
    _ => [type]
  };

  /// <summary>One member that stands for two, or <see langword="null"/> when they stay two members of a union.</summary>
  private static DynamicType? Merge(DynamicType left, DynamicType right, IJoinPolicy policy) => (left, right) switch {
    _ when left == right => left,
    (DynamicExact a, DynamicExact b) when a.Tag is null && b.Tag is null && policy.Join(a.Type, b.Type) is { } joined => new DynamicExact(joined, null),
    (DynamicShape a, DynamicShape b) when a.Fields.Keys.SequenceEqual(b.Fields.Keys) => Shape(a.Fields.ToDictionary(field => field.Key, field => Join(field.Value, b.Fields[field.Key], policy))),
    _ => null
  };

  /// <summary>The meet of two members of levels 1 to 3.</summary>
  private static DynamicType Below(DynamicType left, DynamicType right, IJoinPolicy policy) => (left, right) switch {
    _ when left == right => left,
    (DynamicExact a, DynamicExact b) when a.Tag is null && b.Tag is null && policy.Join(a.Type, b.Type) is { } joined => joined == a.Type ? b : joined == b.Type ? a : Nothing,
    (DynamicShape a, DynamicShape b) when a.Fields.Keys.SequenceEqual(b.Fields.Keys) => Shape(a.Fields.ToDictionary(field => field.Key, field => Meet(field.Value, b.Fields[field.Key], policy))),
    _ => Nothing
  };

}

/// <summary>Level 0: no value reaches the place.</summary>
public sealed record DynamicNothing : DynamicType {

  internal DynamicNothing() { }

  /// <inheritdoc/>
  public override int Level => 0;

  /// <inheritdoc/>
  public override IrType? ToIrType() => null;

  /// <inheritdoc/>
  public override string ToString() => "nothing";

}

/// <summary>Level 1: one type of the IR, and where two kinds of the language share it, the kind.</summary>
/// <param name="Type">The type.</param>
/// <param name="Tag">The kind, or <see langword="null"/>.</param>
public sealed record DynamicExact : DynamicType {

  internal DynamicExact(IrType type, IrVariantTag? tag) {
    this.Type = type;
    this.Tag = tag;
  }

  /// <summary>The type.</summary>
  public IrType Type { get; }

  /// <summary>The kind the language tells the value apart by, or <see langword="null"/>.</summary>
  public IrVariantTag? Tag { get; }

  /// <summary>The tag a value of the element is boxed with in a variant: its kind, else the tag of its type; <see langword="null"/> for a type no tag holds.</summary>
  public IrVariantTag? BoxTag => this.Tag ?? IrVariants.TagOf(this.Type);

  /// <inheritdoc/>
  public override int Level => 1;

  /// <inheritdoc/>
  public override IrType? ToIrType() => this.Type;

  /// <inheritdoc/>
  public override string ToString() => this.Tag is { } tag ? $"{this.Type} {tag}" : this.Type.ToString();

}

/// <summary>Level 2: one declared type.</summary>
public sealed record DynamicNominal : DynamicType {

  internal DynamicNominal(IrTypeDeclaration declaration) => this.Declaration = declaration;

  /// <summary>The declaration.</summary>
  public IrTypeDeclaration Declaration { get; }

  /// <inheritdoc/>
  public override int Level => 2;

  /// <inheritdoc/>
  public override IrType? ToIrType() => this.Declaration switch {
    IrStructType type => IrType.Struct(type),
    IrClassType type => IrType.Object(type),
    IrInterfaceType type => IrType.Object(type),
    IrEnumType type => type.Type,
    _ => null
  };

  /// <inheritdoc/>
  public bool Equals(DynamicNominal? other) => other is not null && ReferenceEquals(this.Declaration, other.Declaration);

  /// <inheritdoc/>
  public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this.Declaration);

  /// <inheritdoc/>
  public override string ToString() => this.Declaration.Name;

}

/// <summary>Level 3: a record of named fields with constant names, each with its own element.</summary>
public sealed record DynamicShape : DynamicType {

  internal DynamicShape(SortedDictionary<string, DynamicType> fields) => this.Fields = fields;

  /// <summary>The fields by name, in ordinal order.</summary>
  public IReadOnlyDictionary<string, DynamicType> Fields { get; }

  /// <inheritdoc/>
  public override int Level => 3;

  /// <inheritdoc/>
  public override int Depth => 1 + this.Fields.Values.Select(value => value.Depth).DefaultIfEmpty(0).Max();

  /// <inheritdoc/>
  public override IrType? ToIrType() => null;

  /// <inheritdoc/>
  public bool Equals(DynamicShape? other) => other is not null && this.Fields.Count == other.Fields.Count
    && this.Fields.All(field => other.Fields.TryGetValue(field.Key, out var value) && value == field.Value);

  /// <inheritdoc/>
  public override int GetHashCode() => this.Fields.Aggregate(this.Fields.Count, (hash, field) => HashCode.Combine(hash, field.Key, field.Value));

  /// <inheritdoc/>
  public override string ToString() => $"{{{string.Join(", ", this.Fields.Select(field => $"{field.Key}: {field.Value}"))}}}";

}

/// <summary>Level 4: an element of levels 0 to 3 or a union, or one of the language's missing values.</summary>
public sealed record DynamicOptional : DynamicType {

  internal DynamicOptional(DynamicType value, DynamicMissing missing) {
    this.Value = value;
    this.Missing = missing;
  }

  /// <summary>The element of the value when it is not missing; <see cref="DynamicType.Nothing"/> when it is always missing.</summary>
  public DynamicType Value { get; }

  /// <summary>The missing values.</summary>
  public DynamicMissing Missing { get; }

  /// <inheritdoc/>
  /// <remarks>An optional of a union is as dynamic as the union: level 5.</remarks>
  public override int Level => Math.Max(4, this.Value.Level);

  /// <inheritdoc/>
  public override int Depth => this.Value.Depth;

  /// <inheritdoc/>
  public override IrType? ToIrType()
    => this.Missing is DynamicMissing.Null or DynamicMissing.Undefined or DynamicMissing.Empty && this.Value.ToIrType() is { } type
      ? type.IsOptionalValue ? IrType.Optional(type) : type.IsNullable ? type : null
      : null;

  /// <inheritdoc/>
  public override string ToString() => $"optional<{this.Value}; {this.Missing}>";

}

/// <summary>Level 5: one of 2 to <see cref="DynamicType.MostMembers"/> members of levels 1 to 3, in the order of their names.</summary>
public sealed record DynamicUnion : DynamicType {

  internal DynamicUnion(IReadOnlyList<DynamicType> members) => this.Members = members;

  /// <summary>The members.</summary>
  public IReadOnlyList<DynamicType> Members { get; }

  /// <inheritdoc/>
  public override int Level => 5;

  /// <inheritdoc/>
  public override int Depth => this.Members.Max(member => member.Depth);

  /// <inheritdoc/>
  public override IrType? ToIrType() {
    var types = this.Members.Select(member => member.ToIrType()).ToArray();
    if (types.Any(type => type is null))
      return null;
    var union = IrType.Union([.. types.OfType<IrType>()]);
    return IrUnions.Problem(union) is null ? union : null;
  }

  /// <inheritdoc/>
  public bool Equals(DynamicUnion? other) => other is not null && this.Members.Count == other.Members.Count && this.Members.All(other.Members.Contains);

  /// <inheritdoc/>
  public override int GetHashCode() => this.Members.Aggregate(this.Members.Count, (hash, member) => hash ^ member.GetHashCode());

  /// <inheritdoc/>
  public override string ToString() => $"union<{string.Join(", ", this.Members)}>";

}

/// <summary>Level 6: anything; the IR's <c>variant</c>.</summary>
public sealed record DynamicVariant : DynamicType {

  internal DynamicVariant() { }

  /// <inheritdoc/>
  public override int Level => 6;

  /// <inheritdoc/>
  public override IrType? ToIrType() => null;

  /// <inheritdoc/>
  public override string ToString() => "variant";

}
