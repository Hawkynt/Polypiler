using System.Numerics;

namespace Polypiler.Core.Options;

/// <summary>What an assumption of <c>--assume</c> states about its subject.</summary>
public enum AssumptionKind {

  /// <summary><c>path in [lo, hi]</c>: the value lies between two bounds, both inclusive.</summary>
  Range,

  /// <summary><c>path != null</c>: the value is never null.</summary>
  NonNull,

  /// <summary><c>path is type</c>: the value has a scalar type.</summary>
  Type,

  /// <summary><c>path noalias other</c>: the two values never share storage.</summary>
  NoAlias

}

/// <summary>
/// A fact the user states with <c>--assume</c> and the compiler does not prove (PRD §4.6): never
/// trusted silently, always reported as <c>PLY0009</c> and recorded in the canonical arguments.
/// </summary>
/// <param name="Kind">What the fact states.</param>
/// <param name="Subject">The path the fact is about, such as <c>Checksum.data.length</c>.</param>
/// <param name="Low">The lower bound of a <see cref="AssumptionKind.Range"/>; otherwise <see langword="null"/>.</param>
/// <param name="High">The upper bound of a <see cref="AssumptionKind.Range"/>, not below <paramref name="Low"/>; otherwise <see langword="null"/>.</param>
/// <param name="TypeName">The type of a <see cref="AssumptionKind.Type"/>; otherwise <see langword="null"/>.</param>
/// <param name="Other">The second path of a <see cref="AssumptionKind.NoAlias"/>; otherwise <see langword="null"/>.</param>
/// <param name="Text">
/// The normalized spelling: single spaces around <c>in</c>, <c>!=</c>, <c>is</c> and <c>noalias</c>,
/// a range as <c>[lo, hi]</c> with decimal integers. Two spellings of one fact have one text.
/// </param>
public sealed record Assumption(AssumptionKind Kind, string Subject, BigInteger? Low, BigInteger? High, string? TypeName, string? Other, string Text);
