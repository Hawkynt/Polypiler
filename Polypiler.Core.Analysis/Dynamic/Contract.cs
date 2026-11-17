using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Polypiler.Core.Analysis.Dynamic;

/// <summary>
/// What a caller contract of <c>--assume "&lt;target&gt;: &lt;contract&gt;"</c> states about a value
/// (<c>spec/ir/assumptions.md</c>). Every contract excludes the absent value unless it is a
/// <see cref="NullableContract"/>. Two contracts are equal when their <see cref="Text"/> is.
/// </summary>
public abstract record Contract {

  /// <summary>The canonical spelling, the one the manifest and the diagnostics write.</summary>
  public abstract string Text { get; }

  /// <inheritdoc/>
  public sealed override string ToString() => this.Text;

}

/// <summary>A scalar: <c>u8</c> … <c>i64</c>, <c>f32</c>, <c>f64</c>, <c>bool</c> or <c>text</c>.</summary>
/// <param name="Name">The scalar as the grammar spells it.</param>
public sealed record ScalarContract(string Name) : Contract {

  /// <inheritdoc/>
  public override string Text => this.Name;

}

/// <summary><c>int[lo, hi]</c>: an integer between two bounds, both inclusive.</summary>
/// <param name="Low">The lower bound.</param>
/// <param name="High">The upper bound, not below <paramref name="Low"/>.</param>
public sealed record IntegerContract(BigInteger Low, BigInteger High) : Contract {

  /// <inheritdoc/>
  public override string Text => $"int[{this.Low}, {this.High}]";

}

/// <summary><c>seq&lt;c&gt;</c> with an optional length <c>[n]</c> or <c>[lo..hi]</c>: a sequence whose every element satisfies a contract.</summary>
/// <param name="Element">The contract of every element.</param>
/// <param name="MinimumLength">The fewest elements, or <see langword="null"/> without a length.</param>
/// <param name="MaximumLength">The most elements, or <see langword="null"/> without a length.</param>
public sealed record SequenceContract(Contract Element, BigInteger? MinimumLength, BigInteger? MaximumLength) : Contract {

  /// <inheritdoc/>
  public override string Text => $"seq<{this.Element.Text}>" + (this.MinimumLength is not { } minimum ? ""
    : minimum == this.MaximumLength ? $"[{minimum}]" : $"[{minimum}..{this.MaximumLength}]");

}

/// <summary>A field of a <see cref="RecordContract"/>.</summary>
/// <param name="Name">The field name.</param>
/// <param name="Contract">The contract of its value.</param>
public sealed record ContractField(string Name, Contract Contract);

/// <summary><c>record{ f: c, … }</c>: a record that has at least the listed fields, each satisfying its contract.</summary>
/// <param name="Fields">The fields, in the order written, each name once.</param>
public sealed record RecordContract(IReadOnlyList<ContractField> Fields) : Contract {

  /// <inheritdoc/>
  public override string Text => $"record{{ {string.Join(", ", this.Fields.Select(member => $"{member.Name}: {member.Contract.Text}"))} }}";

  /// <inheritdoc/>
  public bool Equals(RecordContract? other) => other is not null && this.Text == other.Text;

  /// <inheritdoc/>
  public override int GetHashCode() => this.Text.GetHashCode(System.StringComparison.Ordinal);

}

/// <summary><c>nullable(c)</c>: the absent value, or a value that satisfies a contract.</summary>
/// <param name="Inner">The contract of a present value.</param>
public sealed record NullableContract(Contract Inner) : Contract {

  /// <inheritdoc/>
  public override string Text => $"nullable({this.Inner.Text})";

}
